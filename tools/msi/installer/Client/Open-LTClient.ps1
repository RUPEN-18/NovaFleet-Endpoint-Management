#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
try {
    $root = Split-Path $PSScriptRoot -Parent
    $settings = Join-Path $root 'appsettings.json'
    if (-not (Test-Path -LiteralPath $settings -PathType Leaf)) {
        Add-Type -AssemblyName PresentationFramework
        [void][Windows.MessageBox]::Show('Open "Connect L&T Client to Admin" from the Start menu and select this PC''s pairing file exported by Admin.', 'L&T Client setup')
        return
    }
    $config = Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json
    if ([string]$config.Client.DeviceId -notmatch '\A[0-9a-fA-F]{32}\z' -or [string]$config.Client.CertificateSha256 -notmatch '\A[0-9a-fA-F]{64}\z' -or [string]$config.Client.ApiKey -notmatch '\A[0-9a-fA-F]{64}\z') {
        throw 'Client pairing is incomplete.'
    }
    & (Join-Path $PSScriptRoot 'Start-ManagedClient.ps1')
} catch {
    Add-Type -AssemblyName PresentationFramework
    [void][Windows.MessageBox]::Show('Client could not start. Open "Connect L&T Client to Admin" to check its configuration.', 'L&T Client')
}
