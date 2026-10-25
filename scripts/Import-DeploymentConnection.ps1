#Requires -Version 5.1
#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory = $true)][string] $PairingFile,
    [Parameter(Mandatory = $true)][ValidatePattern('\A[0-9a-fA-F]{64}\z')][string] $ExpectedCertificateSha256
)
. (Join-Path $PSScriptRoot 'DeploymentService-Common.ps1')
Assert-NovaAdministrator
$owned = Get-NovaOwnedService
if ($null -eq $owned) { throw 'Install Nova Deployment Service on this PC before importing a pairing file.' }
Assert-NovaProtectedTree $NovaDataRoot
$source = Get-Item -LiteralPath $PairingFile
if ($source.PSIsContainer -or ($source.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $source.Length -lt 1 -or $source.Length -gt 8192) { throw 'Invalid or linked pairing file.' }
$text = [IO.File]::ReadAllText($source.FullName)
$profile = $text | ConvertFrom-Json
foreach ($field in @('serverUrl', 'serverCertificateSha256', 'deviceId', 'deviceToken', 'computerName')) {
    if ($null -eq $profile.PSObject.Properties[$field] -or [string]::IsNullOrWhiteSpace([string]$profile.$field)) {
        throw "Pairing file is missing $field."
    }
}
if ([string]$profile.computerName -ine [Environment]::MachineName) { throw 'This pairing file belongs to a different computer.' }
if ([string]$profile.computerName -notmatch '\A[A-Za-z0-9][A-Za-z0-9_-]{0,62}\z') { throw 'Invalid client computer name.' }
if ([string]$profile.serverCertificateSha256 -notmatch '\A[0-9a-fA-F]{64}\z' -or [string]$profile.deviceToken -notmatch '\A[0-9a-fA-F]{64}\z' -or [string]$profile.deviceId -notmatch '\A[0-9a-fA-F]{32}\z') { throw 'Invalid pairing credentials.' }
if ([string]$profile.serverCertificateSha256 -ine $ExpectedCertificateSha256) {
    throw 'Certificate fingerprint does not match the value shown in Admin. Pairing was not changed.'
}
$uri = [Uri]([string]$profile.serverUrl)
if (-not $uri.IsAbsoluteUri -or $uri.Scheme -ne 'https' -or $uri.Port -ne 5057 -or $uri.UserInfo.Length -ne 0 -or $uri.AbsolutePath -ne '/' -or $uri.Query.Length -ne 0 -or $uri.Fragment.Length -ne 0) {
    throw 'A LAN pairing file must use an HTTPS root URL on port 5057.'
}
$address = $null
if (-not [Net.IPAddress]::TryParse($uri.Host, [ref]$address) -or $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { throw 'Use a private IPv4 Admin address.' }
$bytes = $address.GetAddressBytes()
if (-not ($bytes[0] -eq 10 -or ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or ($bytes[0] -eq 192 -and $bytes[1] -eq 168))) { throw 'The Admin address is not a private LAN IPv4 address.' }

$activity = Join-Path $NovaStatusRoot 'activity.json'
if (Test-Path -LiteralPath $activity) {
    $busy = Get-Content -LiteralPath $activity -Raw | ConvertFrom-Json
    if ($busy.busy) { throw 'An installation is active. Wait for its final result before changing pairing.' }
}
$wasRunning = $owned.State -eq 'Running'
$temporary = Join-Path $NovaDataRoot ('connection-' + [Guid]::NewGuid().ToString('N') + '.tmp')
Stop-NovaService
try {
    # Recheck the durable ledger after stopping. Never abandon an installer or an unsent result.
    $jobs = Join-Path $NovaDataRoot 'jobs'
    if (Test-Path -LiteralPath $jobs) {
        foreach ($file in @(Get-ChildItem -LiteralPath $jobs -Recurse -File -Filter 'job.json')) {
            $record = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
            if ($record.stage -ne 'Done' -or -not $record.reported) {
                throw 'This client has unfinished installation work or a result awaiting Admin. Resolve it before changing pairing.'
            }
        }
    }
    $destination = Join-Path $NovaDataRoot 'connection.json'
    if ((Test-Path -LiteralPath $destination) -and
        ((Get-Item -LiteralPath $destination).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The connection file must not be a link.' }
    # Write only known fields. Private service-folder ACLs are inherited by this file.
    $clean = [ordered]@{ serverUrl = [string]$profile.serverUrl; serverCertificateSha256 = [string]$profile.serverCertificateSha256
        deviceId = [string]$profile.deviceId; deviceToken = [string]$profile.deviceToken; computerName = [string]$profile.computerName }
    [IO.File]::WriteAllText($temporary, ($clean | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Protect-NovaTree $NovaDataRoot
    if (Test-Path -LiteralPath $destination) { [IO.File]::Replace($temporary, $destination, $null) }
    else { [IO.File]::Move($temporary, $destination) }
    Assert-NovaProtectedTree $NovaDataRoot
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    if ($wasRunning) { Start-Service -Name $NovaServiceName }
}
if ((Get-Service -Name $NovaServiceName).Status -ne 'Running') { Start-Service -Name $NovaServiceName }
& (Join-Path $PSScriptRoot 'Test-DeploymentService.ps1')
Write-Host 'PAIRING PROFILE IMPORTED. Refresh Admin and wait for this client to show ONLINE.' -ForegroundColor Green
Write-Host 'Keep the pairing file private; remove transfer copies after confirming the connection.'
