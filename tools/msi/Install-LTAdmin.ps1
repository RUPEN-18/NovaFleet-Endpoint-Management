#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param([string]$ExistingSettingsPath = '')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([IntPtr]::Size -ne 8) { throw 'Use 64-bit Windows PowerShell for MSI installation.' }
$msi = Join-Path $PSScriptRoot 'LT.Admin.0.5.5.x64.msi'
$expected = ((Get-Content -LiteralPath ($msi + '.sha256.txt') -Raw).Trim() -split '\s+')[0]
if ((Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash -ne $expected) { throw 'Admin MSI checksum mismatch.' }
if (Get-Process -Name 'AutoShutdown.SystemA' -ErrorAction SilentlyContinue) { throw 'Finish installation jobs and close every Admin instance first.' }
$installedRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'AutoShutdown.Nova.Admin'
if ([string]::IsNullOrWhiteSpace($ExistingSettingsPath)) {
    $installed = Join-Path $installedRoot 'appsettings.json'
    if (Test-Path -LiteralPath $installed) { $ExistingSettingsPath = $installed }
    elseif (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'BuildInfo.json')) {
        $info = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'BuildInfo.json') -Raw | ConvertFrom-Json
        $source = Join-Path $info.SourceProject 'src\AutoShutdown.Admin\appsettings.json'
        if (Test-Path -LiteralPath $source) { $ExistingSettingsPath = $source }
    }
}
$legacyKey = $null
if (-not [string]::IsNullOrWhiteSpace($ExistingSettingsPath)) {
    $file = Get-Item -LiteralPath $ExistingSettingsPath
    if ($file.PSIsContainer -or $file.Length -gt 65536 -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Invalid existing Admin settings file.' }
    $config = [IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json
    if ($null -ne $config.PSObject.Properties['Server'] -and $null -ne $config.Server.PSObject.Properties['ApiKey']) {
        $legacyKey = [string]$config.Server.ApiKey
        if ($legacyKey.Length -gt 1024 -or $legacyKey -match '[\x00-\x1F]') { throw 'Existing local compatibility key is invalid.' }
    }
}
$log = Join-Path $env:TEMP ('LT-Admin-MSI-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
$process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\msiexec.exe') -ArgumentList ('/i "' + $msi + '" /passive /norestart /L*v "' + $log + '"') -Wait -PassThru
if ($process.ExitCode -notin @(0,3010)) { throw ('Admin MSI installation failed with code ' + $process.ExitCode + '. Log: ' + $log) }
if ($null -ne $legacyKey) {
    # Preserve the existing localhost Client key on THIS PC; it is never included in a distributable MSI.
    $settings = @{Server=@{Url='http://127.0.0.1:5055';ClientBaseUrl='http://127.0.0.1:5055';ApiKey=$legacyKey}}
    [IO.File]::WriteAllText((Join-Path $installedRoot 'appsettings.json'), ($settings | ConvertTo-Json -Depth 3), [Text.UTF8Encoding]::new($false))
}
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Test-Path -LiteralPath $runKey) {
    $startup = Get-ItemProperty -LiteralPath $runKey
    if ($null -ne $startup.PSObject.Properties['AutoShutdownNovaSystemA']) {
        Set-ItemProperty -LiteralPath $runKey -Name 'AutoShutdownNovaSystemA' -Value ('"' + (Join-Path $installedRoot 'AutoShutdown.SystemA.exe') + '"')
    }
}
Write-Host 'ADMIN MSI INSTALLED. Open L&T Admin from the Start menu under the same Windows user as before.' -ForegroundColor Green
Write-Host 'Existing per-user accounts, pairing, certificate and history remain in their current data folders.'
Write-Host 'Update the Admin firewall rule to the installed executable path using scripts\Configure-DeploymentLanFirewall.ps1.'
Write-Host ('Installation log: ' + $log)
if ($process.ExitCode -eq 3010) { Write-Host 'Windows reported that a restart is required.' }
