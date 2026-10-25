# Embedded as a deferred SYSTEM action; a failed check fails/rolls back installation.
$ErrorActionPreference = 'Stop'
$clientRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'AutoShutdown.Nova.Client'
$helper = Join-Path $clientRoot 'scripts\DeploymentService-Common.ps1'
$bytes = [IO.File]::ReadAllBytes($helper)
$sha = [Security.Cryptography.SHA256]::Create()
try { $digest = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '') }
finally { $sha.Dispose() }
if ($digest -ine 'NOVA_EXPECTED_COMMON_SHA256') { throw 'The installed protection helper differs from the packaged file.' }
# Execute only the already-hashed memory copy, not another read from a suspect path.
. ([scriptblock]::Create([Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xFEFF)))
foreach ($root in @($clientRoot, $NovaInstallRoot, $NovaDataRoot, $NovaStatusRoot)) {
    Assert-NovaProtectedTree $root
}
& (Join-Path $clientRoot 'scripts\Test-DeploymentService.ps1')
Write-Host 'MSI INSTALLATION CHECK PASSED: all four Nova folders are protected and the LocalSystem heartbeat advances.'
