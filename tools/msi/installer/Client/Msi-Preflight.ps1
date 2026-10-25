# Embedded with DeploymentService-Common.ps1; not executed from an installed file.
$clientRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'AutoShutdown.Nova.Client'
foreach ($root in @($clientRoot, $NovaInstallRoot, $NovaDataRoot, $NovaStatusRoot)) {
    if (Test-Path -LiteralPath $root) { Assert-NovaProtectedTree $root }
}
$null = Get-NovaOwnedService
if (@(Get-Process -Name 'AutoShutdown.SystemB' -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Close Nova Client before installing or uninstalling it.'
}
if (Test-Path -LiteralPath $NovaDataRoot) {
    foreach ($file in @(Get-ChildItem -LiteralPath $NovaDataRoot -Recurse -File -Filter 'job.json')) {
        $record = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
        if ($null -eq $record.PSObject.Properties['stage'] -or $null -eq $record.PSObject.Properties['reported'] -or
            $record.stage -ne 'Done' -or -not $record.reported) {
            throw 'Nova has an unfinished installation or an unsent result. Keep Admin open until it finishes before uninstalling.'
        }
    }
}
Write-Host 'Nova MSI preflight passed: protected paths, closed Client and no unfinished jobs.'
