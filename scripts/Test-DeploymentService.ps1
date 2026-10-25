#Requires -Version 5.1
#Requires -RunAsAdministrator
. (Join-Path $PSScriptRoot 'DeploymentService-Common.ps1')
Assert-NovaAdministrator

function Read-NovaRunningHealth {
    $service = Get-NovaOwnedService
    if ($null -eq $service -or $service.State -ne 'Running') { throw 'The deployment service is not Running.' }
    if ($service.StartMode -ne 'Auto') { throw 'The deployment service is not configured to start automatically.' }
    $statusPath = Join-Path $NovaDataRoot 'status.json'
    $health = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json
    if ($health.serviceName -ne $NovaServiceName -or $health.state -ne 'Running' -or $health.mode -ne 'WindowsService') {
        throw 'The service status is not a live Windows service status.'
    }
    if ($health.identitySid -ne 'S-1-5-18') { throw 'The service is not running as LocalSystem.' }
    if ($health.computerName -ne [Environment]::MachineName) { throw 'The heartbeat belongs to a different computer.' }
    if ([int]$health.processId -ne [int]$service.ProcessId) { throw 'The heartbeat belongs to an old service process.' }
    if ($health.version -ne 'EXPECTED_SERVICE_VERSION') { throw 'The deployment service update is not the packaged version.' }
    $heartbeat = [DateTimeOffset]$health.heartbeatUtc
    $age = ([DateTimeOffset]::UtcNow - $heartbeat).TotalSeconds
    if ($age -gt 15 -or $age -lt -5) { throw 'The heartbeat is stale or has an invalid clock value.' }
    return $health
}

$first = $null
$deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
do {
    try { $first = Read-NovaRunningHealth; break }
    catch {
        if ([DateTimeOffset]::UtcNow -ge $deadline) { throw }
        Start-Sleep -Milliseconds 500
    }
} while ($true)

Assert-NovaProtectedTree $NovaInstallRoot
Assert-NovaProtectedTree $NovaDataRoot
Assert-NovaProtectedTree $NovaStatusRoot
Write-Host 'PASS: service is Running and configured to start automatically.' -ForegroundColor Green
Write-Host 'PASS: LocalSystem identity and current process heartbeat verified.' -ForegroundColor Green
Write-Host 'PASS: service folders allow changes only by SYSTEM and Administrators.' -ForegroundColor Green
Write-Host 'Checking that the heartbeat advances...'
Start-Sleep -Seconds 7
$second = Read-NovaRunningHealth
if ([DateTimeOffset]$second.heartbeatUtc -le [DateTimeOffset]$first.heartbeatUtc) {
    throw 'The service heartbeat did not advance.'
}
Write-Host 'PASS: heartbeat is advancing.' -ForegroundColor Green
Write-Host 'SERVICE UPDATE CHECK PASSED' -ForegroundColor Green
Get-Service -Name $NovaServiceName | Format-Table -Property @('Status', 'Name', 'DisplayName') -AutoSize
Write-Host "Status file: $(Join-Path $NovaDataRoot 'status.json')"
