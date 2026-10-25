#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'DeploymentService-Common.ps1')
Assert-NovaAdministrator
if ([IntPtr]::Size -ne 8) { throw 'Use 64-bit Windows PowerShell for MSI installation.' }
$msi = Join-Path $PSScriptRoot 'LT.Client.0.5.5.x64.msi'
$expected = ((Get-Content -LiteralPath ($msi + '.sha256.txt') -Raw).Trim() -split '\s+')[0]
if ((Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash -ne $expected) { throw 'Client MSI checksum mismatch.' }
# Validate paths, closed Client and no unfinished/unsent jobs before migrating a script-installed service.
& (Join-Path $PSScriptRoot 'Client-Preflight.ps1')
$owned = Get-NovaOwnedService
$managed = Test-Path -LiteralPath 'HKLM:\Software\AutoShutdown.Nova\ClientMsi'
$removedRegistration = $false
$wasRunning = $null -ne $owned -and $owned.State -eq 'Running'
$log = Join-Path $env:TEMP ('LT-Client-MSI-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
try {
    if ($null -ne $owned -and -not $managed) {
        # Reuse existing protected files/connection/ledger; replace only the old non-MSI SCM registration.
        Stop-NovaService
        Invoke-NovaSc -ScArguments @('delete',$NovaServiceName)
        $removedRegistration = $true
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
        while ($null -ne (Get-CimInstance Win32_Service -Filter "Name='$NovaServiceName'")) {
            if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Service deletion is pending. Close Services/Task Manager windows and retry.' }
            Start-Sleep -Milliseconds 500
        }
    }
    $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList ('/i "' + $msi + '" /passive /norestart /L*v "' + $log + '"') -Wait -PassThru
    if ($process.ExitCode -notin @(0,3010)) { throw ('Client MSI failed with code ' + $process.ExitCode + '. Log: ' + $log) }
} catch {
    $failure = $_
    if ($removedRegistration -and $null -eq (Get-NovaOwnedService) -and (Test-Path -LiteralPath (Join-Path $NovaInstallRoot $NovaExeName))) {
        try {
            Invoke-NovaSc -ScArguments @('create',$NovaServiceName,'binPath=',$NovaBinaryPath,'start=','auto','obj=','LocalSystem','DisplayName=','Nova Deployment Service')
            Invoke-NovaSc -ScArguments @('failure',$NovaServiceName,'reset=','86400','actions=','restart/30000/restart/30000/restart/30000')
            if ($wasRunning) { Start-Service -Name $NovaServiceName }
            Write-Warning 'Previous script-installed service registration restored. Configuration and job records were retained.'
        } catch { Write-Warning ('Service registration recovery failed: ' + $_.Exception.Message + '. Existing service files and connection data remain under Program Files and ProgramData.') }
    }
    throw $failure
}
$clientRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'AutoShutdown.Nova.Client'
$guiConfig = Join-Path $clientRoot 'appsettings.json'
$connection = Join-Path $NovaDataRoot 'connection.json'
if (-not (Test-Path -LiteralPath $guiConfig) -and (Test-Path -LiteralPath $connection)) {
    # Reuse an existing, protected paired identity when it contains both channels.
    Assert-NovaProtectedTree $NovaDataRoot
    $profile = Get-Content -LiteralPath $connection -Raw | ConvertFrom-Json
    $localControlProfile = $false
    if ($null -ne $profile.PSObject.Properties['serverUrl'] -and $null -ne $profile.PSObject.Properties['computerName'] -and
        $profile.computerName -ieq [Environment]::MachineName) {
        $serviceAddress = [Uri]$profile.serverUrl
        if ($serviceAddress.IsLoopback -and $serviceAddress.Port -eq 5056) {
            $networkPath = Join-Path $env:LOCALAPPDATA 'AutoShutdown.Nova\DeploymentAdmin\deployment-network.json'
            if (Test-Path -LiteralPath $networkPath) {
                $network = Get-Content -LiteralPath $networkPath -Raw | ConvertFrom-Json
                if ($null -ne $network.PSObject.Properties['Address']) {
                    $address = $null
                    if ([Net.IPAddress]::TryParse([string]$network.Address, [ref]$address) -and $address.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetwork) {
                        $b = $address.GetAddressBytes()
                        $private = $b[0] -eq 10 -or ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31) -or ($b[0] -eq 192 -and $b[1] -eq 168)
                        if ($private -and @(Get-NetIPAddress -IPAddress $address.ToString() -AddressFamily IPv4 -ErrorAction SilentlyContinue).Count -gt 0) {
                            $profile | Add-Member -NotePropertyName 'controlServerUrl' -NotePropertyValue ('https://' + $address.ToString() + ':5058/') -Force
                            $localControlProfile = $true
                        }
                    }
                }
            }
        }
    }
    $complete = $true
    foreach ($name in @('computerName','deviceId','controlServerUrl','controlApiKey','serverCertificateSha256','serverUrl')) {
        if ($null -eq $profile.PSObject.Properties[$name] -or [string]::IsNullOrWhiteSpace([string]$profile.$name)) { $complete = $false }
    }
    if ($complete) {
        $control = [Uri]$profile.controlServerUrl; $deployment = [Uri]$profile.serverUrl
        if ($profile.computerName -ine [Environment]::MachineName -or [string]$profile.deviceId -notmatch '\A[0-9a-fA-F]{32}\z' -or
            [string]$profile.controlApiKey -notmatch '\A[0-9a-fA-F]{64}\z' -or [string]$profile.serverCertificateSha256 -notmatch '\A[0-9a-fA-F]{64}\z' -or
            -not $control.IsAbsoluteUri -or $control.Scheme -ne 'https' -or $control.Port -ne 5058 -or ($control.Host -ne $deployment.Host -and -not $localControlProfile) -or
            $control.AbsolutePath -ne '/' -or $control.UserInfo.Length -ne 0 -or $control.Query.Length -ne 0 -or $control.Fragment.Length -ne 0) {
            throw 'Client MSI installed, but the existing paired control profile is invalid. Export a fresh Client connection from Admin.'
        }
        $settings = @{Client=@{ServerUrl=[string]$profile.controlServerUrl;ApiKey=[string]$profile.controlApiKey;CertificateSha256=[string]$profile.serverCertificateSha256;DeviceId=[string]$profile.deviceId}}
        [IO.File]::WriteAllText($guiConfig, ($settings | ConvertTo-Json -Depth 3), [Text.UTF8Encoding]::new($false))
        Protect-NovaTree -Root $guiConfig -AllowUserRead
        Write-Host 'Existing paired Client identity restored for the installed GUI.'
    }
}
# Replace only this user's obsolete Client startup entry; the MSI owns the machine-wide entry.
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Test-Path -LiteralPath $runKey) {
    $startup = Get-ItemProperty -LiteralPath $runKey
    if ($null -ne $startup.PSObject.Properties['AutoShutdownNovaSystemB'] -and [string]$startup.AutoShutdownNovaSystemB -match 'AutoShutdown\.SystemB\.exe') {
        Remove-ItemProperty -LiteralPath $runKey -Name 'AutoShutdownNovaSystemB'
    }
}
Write-Host 'CLIENT MSI INSTALLED. The background service starts automatically.' -ForegroundColor Green
if (-not (Test-Path -LiteralPath $guiConfig)) { Write-Host 'Open Connect L&T Client to Admin from the Start menu to import this PC''s pairing file.' }
else { Write-Host 'Open L&T Client from the Start menu normally, then check both connections in Admin.' }
Write-Host ('Installation log: ' + $log)
if ($process.ExitCode -eq 3010) { Write-Host 'Windows reported that a restart is required.' }
