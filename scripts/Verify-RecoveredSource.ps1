#Requires -Version 5.1
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ($env:OS -ne 'Windows_NT') { throw 'Use Windows with the .NET 10 SDK.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install the .NET 10 SDK first.' }
foreach ($file in Get-ChildItem -LiteralPath $root -Filter '*.ps1' -Recurse -File) {
    if ($file.FullName -match '[\\/](bin|obj|\.git)[\\/]') { continue }
    $tokens = $null
    $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) { throw ($file.Name + ': ' + ($errors.Message -join '; ')) }
}
& dotnet build (Join-Path $root 'AutoShutdown.Nova.sln') -c Release -p:NuGetAuditMode=all '-warnaserror:NU1903,NU1904'
if ($LASTEXITCODE -ne 0) { throw 'Recovered source build failed. No runtime validation is claimed.' }
foreach ($name in @('AdminSecurityChecks','InstallBatchChecks')) {
    & dotnet run --project (Join-Path $root ('tests\' + $name + '\' + $name + '.csproj')) -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw ('Checks failed: ' + $name) }
}
Write-Host 'Recovered source built and both console check programs passed. Live Client/service, MSI, pairing and reboot checks remain.' -ForegroundColor Green
