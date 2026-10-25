#Requires -Version 5.1
#Requires -RunAsAdministrator
param([string] $PairingFile = '', [string] $ExpectedCertificateSha256 = '')
. (Join-Path $PSScriptRoot 'DeploymentService-Common.ps1')
Assert-NovaAdministrator
$destination = Split-Path $PSScriptRoot -Parent
if (-not (Test-Path -LiteralPath (Join-Path $destination 'AutoShutdown.SystemB.exe') -PathType Leaf)) {
    throw 'Install Nova Client MSI first, then run this script from its installed scripts folder.'
}
if (@(Get-Process -Name 'AutoShutdown.SystemB' -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Close Client before pairing.' }
Assert-NovaProtectedTree $destination
Assert-NovaProtectedTree $NovaDataRoot
if ([string]::IsNullOrWhiteSpace($PairingFile)) {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    try {
        $dialog.Title = 'Select the connection exported from Admin for THIS PC'
        $dialog.Filter = 'Nova pairing files (*.nova-pairing.json)|*.nova-pairing.json'
        if ($dialog.ShowDialog() -ne [Windows.Forms.DialogResult]::OK) { throw 'No pairing file selected.' }
        $PairingFile = $dialog.FileName
    } finally { $dialog.Dispose() }
}
if ([string]::IsNullOrWhiteSpace($ExpectedCertificateSha256)) {
    $ExpectedCertificateSha256 = Read-Host 'Paste the 64-character certificate fingerprint shown in Admin (not from the pairing file)'
}
$ExpectedCertificateSha256 = $ExpectedCertificateSha256.Trim()
if ($ExpectedCertificateSha256 -notmatch '\A[0-9a-fA-F]{64}\z') { throw 'Paste the complete 64-character fingerprint shown in Admin.' }
$file = Get-Item -LiteralPath $PairingFile
if ($file.PSIsContainer -or $file.Length -lt 1 -or $file.Length -gt 8192 -or
    ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Invalid pairing file.' }
$profileText = [IO.File]::ReadAllText($file.FullName)
$profile = $profileText | ConvertFrom-Json
foreach ($field in @('computerName', 'serverUrl', 'serverCertificateSha256', 'deviceId', 'deviceToken', 'controlServerUrl', 'controlApiKey')) {
    if ($null -eq $profile.PSObject.Properties[$field] -or [string]::IsNullOrWhiteSpace([string]$profile.$field)) {
        throw 'Export a new Client connection from Admin. This file does not configure both features.'
    }
}
if ([string]$profile.computerName -ine [Environment]::MachineName) { throw 'This pairing file belongs to another computer.' }
if ([string]$profile.serverCertificateSha256 -ine $ExpectedCertificateSha256) { throw 'The certificate fingerprint differs from Admin.' }
if ([string]$profile.controlApiKey -notmatch '\A[0-9a-fA-F]{64}\z') { throw 'Invalid GUI credential.' }
$control = [Uri]([string]$profile.controlServerUrl)
$deployment = [Uri]([string]$profile.serverUrl)
if (-not $control.IsAbsoluteUri -or $control.Scheme -ne 'https' -or $control.Port -ne 5058 -or
    $control.Host -ne $deployment.Host -or $control.AbsolutePath -ne '/' -or
    $control.UserInfo.Length -ne 0 -or $control.Query.Length -ne 0 -or $control.Fragment.Length -ne 0) {
    throw 'Invalid Client control address.'
}
# Import revalidates service identity, all credentials, private IPv4, machine name,
# expected fingerprint, and unfinished jobs before stopping or changing the service.
# Both channels use the same validated bytes. A transfer copy cannot be swapped
# between this validation and the service importer reading it again.
$snapshot = Join-Path $NovaDataRoot ('pairing-' + [guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::WriteAllText($snapshot, $profileText, [Text.UTF8Encoding]::new($false))
    $options = @{ PairingFile = $snapshot; ExpectedCertificateSha256 = $ExpectedCertificateSha256 }
    & (Join-Path $PSScriptRoot 'Import-DeploymentConnection.ps1') @options
} finally {
    if (Test-Path -LiteralPath $snapshot) { Remove-Item -LiteralPath $snapshot -Force }
}
$settings = @{ Client = @{ ServerUrl = [string]$profile.controlServerUrl; ApiKey = [string]$profile.controlApiKey
    CertificateSha256 = [string]$profile.serverCertificateSha256; DeviceId = [string]$profile.deviceId } }
$target = Join-Path $destination 'appsettings.json'
$temporary = Join-Path $destination ('settings-' + [guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::WriteAllText($temporary, ($settings | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $target) { [IO.File]::Replace($temporary, $target, $null) }
    else { [IO.File]::Move($temporary, $target) }
    Protect-NovaTree -Root $target -AllowUserRead
    Assert-NovaProtectedTree $destination
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}
Write-Host 'CLIENT PAIRED. Close this elevated terminal and open Nova Client from the Start menu normally.' -ForegroundColor Green
Write-Host 'Confirm CONNECTED in Client and ONLINE in Admin. Remove the private transfer copy after both connections work.'
