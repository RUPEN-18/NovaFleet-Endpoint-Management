#Requires -Version 5.1
#Requires -RunAsAdministrator
. (Join-Path $PSScriptRoot 'DeploymentService-Common.ps1')
Assert-NovaAdministrator
if ($null -eq (Get-NovaOwnedService)) {
    Write-Host 'Nova Deployment Service is not installed.'
    return
}
Stop-NovaService
Invoke-NovaSc -ScArguments @('delete', $NovaServiceName)
Write-Host 'Service registration removed. The service files and diagnostic history are retained.'
