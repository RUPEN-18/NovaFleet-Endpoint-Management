#Requires -Version 5.1
# Invoked at sign-in under the ordinary logged-in user's identity, never as SYSTEM.
$ErrorActionPreference = 'Stop'
try {
    $root = Split-Path $PSScriptRoot -Parent
    $settings = Join-Path $root 'appsettings.json'
    if (-not (Test-Path -LiteralPath $settings -PathType Leaf)) { return }
    $config = Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json
    if ([string]$config.Client.DeviceId -notmatch '\A[0-9a-fA-F]{32}\z' -or
        [string]$config.Client.CertificateSha256 -notmatch '\A[0-9a-fA-F]{64}\z' -or
        [string]$config.Client.ApiKey -notmatch '\A[0-9a-fA-F]{64}\z') { return }
    if (@(Get-Process -Name 'AutoShutdown.SystemB' -ErrorAction SilentlyContinue |
        Where-Object { $_.SessionId -eq [Diagnostics.Process]::GetCurrentProcess().SessionId }).Count -gt 0) { return }
    Start-Process -FilePath (Join-Path $root 'AutoShutdown.SystemB.exe')
} catch {
    # Startup is silent when Client has not yet been paired. Do not expose credentials in a dialog.
}
