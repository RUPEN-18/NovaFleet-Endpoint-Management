#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding(DefaultParameterSetName = 'Enable')]
param(
    [Parameter(Mandatory = $true, ParameterSetName = 'Enable')][string] $AdminAddress,
    [Parameter(Mandatory = $true, ParameterSetName = 'Enable')][string[]] $ClientAddress,
    [Parameter(ParameterSetName = 'Enable')][string] $AdminExe = '',
    [Parameter(Mandatory = $true, ParameterSetName = 'Remove')][switch] $Remove
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ruleName = 'NovaDeploymentDevice5057'
$group = 'Nova Deployment'
$existing = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
if ($null -ne $existing -and $existing.Group -ne $group) { throw 'A firewall rule with this name belongs to another application. It was not changed.' }
if ($Remove) {
    if ($null -ne $existing) { Remove-NetFirewallRule -Name $ruleName }
    Write-Host 'Nova LAN firewall rule removed.'
    return
}
function Assert-PrivateIPv4([string] $Text) {
    $address = $null
    if (-not [Net.IPAddress]::TryParse($Text, [ref]$address) -or $address.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $address.ToString() -cne $Text) { throw "Invalid IPv4 address: $Text" }
    $b = $address.GetAddressBytes()
    if (-not ($b[0] -eq 10 -or ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31) -or ($b[0] -eq 192 -and $b[1] -eq 168))) { throw 'Use private IPv4 addresses on the same trusted LAN.' }
}
Assert-PrivateIPv4 $AdminAddress
foreach ($address in $ClientAddress) { Assert-PrivateIPv4 $address }
if ($AdminAddress -in $ClientAddress) { throw 'Enter the two different PC addresses.' }
$adapter = @(Get-NetIPAddress -IPAddress $AdminAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue)
if ($adapter.Count -ne 1) { throw 'The Admin address is not assigned to exactly one local adapter.' }
$network = Get-NetConnectionProfile -InterfaceIndex $adapter[0].InterfaceIndex
if ($network.NetworkCategory -notin @('Private', 'DomainAuthenticated')) {
    throw 'This adapter is Public. Use a trusted Private or Domain network before enabling LAN deployment.'
}
if ([string]::IsNullOrWhiteSpace($AdminExe)) {
    $installed = Join-Path (Split-Path $PSScriptRoot -Parent) 'AutoShutdown.SystemA.exe'
    $AdminExe = if (Test-Path -LiteralPath $installed -PathType Leaf) { $installed } else { Join-Path (Split-Path $PSScriptRoot -Parent) 'src\AutoShutdown.Admin\bin\Debug\net10.0-windows\AutoShutdown.SystemA.exe' }
}
$exe = Get-Item -LiteralPath $AdminExe
if ($exe.PSIsContainer -or $exe.Name -ne 'AutoShutdown.SystemA.exe') { throw 'Select the built Nova Admin executable.' }
# Retain already allowed exact client IPs when adding another Client or changing the Admin executable path.
$addresses = @($ClientAddress)
if ($null -ne $existing) {
    $previous = @(Get-NetFirewallAddressFilter -AssociatedNetFirewallRule $existing | Select-Object -ExpandProperty RemoteAddress)
    foreach ($address in $previous) { Assert-PrivateIPv4 $address }
    $addresses = @($addresses + $previous | Select-Object -Unique)
    Remove-NetFirewallRule -Name $ruleName
}
New-NetFirewallRule -Name $ruleName -DisplayName 'Nova Client installation and control HTTPS (5057,5058)' -Group $group `
    -Direction Inbound -Action Allow -Protocol TCP -LocalAddress $AdminAddress -LocalPort @(5057, 5058) `
    -RemoteAddress $addresses -Program $exe.FullName -Profile @('Private', 'Domain') -EdgeTraversalPolicy Block | Out-Null
Write-Host "LAN FIREWALL RULE READY: $($addresses -join ", ") can reach $AdminAddress on TCP 5057/5058 using Nova Admin." -ForegroundColor Green
