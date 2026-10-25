#Requires -Version 5.1
param([Parameter(Mandatory = $true)][string] $Path)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Path = (Get-Item -LiteralPath $Path).FullName
function Invoke-ComMethod($Object, [string] $Name, [object[]] $Arguments) {
    $Object.GetType().InvokeMember($Name, [Reflection.BindingFlags]::InvokeMethod, $null, $Object, $Arguments)
}
function Read-ComString($Object, [int] $Index) {
    $Object.GetType().InvokeMember('StringData', [Reflection.BindingFlags]::GetProperty, $null, $Object, @($Index))
}
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $null
try {
    $database = Invoke-ComMethod $installer 'OpenDatabase' @($Path, 0)
    function Read-Rows([string] $Sql, [int] $Columns) {
        $view = Invoke-ComMethod $database 'OpenView' @($Sql)
        try {
            $null = Invoke-ComMethod $view 'Execute' @()
            while ($null -ne ($record = Invoke-ComMethod $view 'Fetch' @())) {
                try {
                    $values = @()
                    for ($index = 1; $index -le $Columns; $index++) { $values += Read-ComString $record $index }
                    ,$values
                } finally { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) }
            }
        } finally {
            $null = Invoke-ComMethod $view 'Close' @()
            $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
        }
    }
    $files = @(Read-Rows 'SELECT `FileName` FROM `File`' 1 | ForEach-Object { ($_[0] -split '\|')[-1] })
    foreach ($name in @('AutoShutdown.SystemB.exe', 'AutoShutdown.DeploymentService.exe', 'Connect-NovaClient.ps1',
        'Start-ManagedClient.ps1', 'Import-DeploymentConnection.ps1', 'Test-DeploymentService.ps1', 'coreclr.dll')) {
        if ($name -notin $files) { throw "MSI is missing $name." }
    }
    if (@($files | Where-Object { $_ -eq 'coreclr.dll' }).Count -ne 2) { throw 'Both self-contained runtimes must be included.' }
    if ('appsettings.json' -in $files -or 'connection.json' -in $files) { throw 'Credentials/configuration must not be owned or distributed by this MSI.' }
    $services = @(Read-Rows 'SELECT `Name`,`ServiceType`,`StartType`,`StartName` FROM `ServiceInstall`' 4)
    if ($services.Count -ne 1 -or $services[0][0] -ne 'NovaDeploymentService' -or
        [int]$services[0][1] -ne 16 -or [int]$services[0][2] -ne 2 -or $services[0][3] -ne 'LocalSystem') {
        throw 'MSI must install exactly one automatic LocalSystem service.'
    }
    $media = @(Read-Rows 'SELECT `Cabinet` FROM `Media`' 1)
    if ($media.Count -lt 1 -or @($media | Where-Object { -not $_[0].StartsWith('#') }).Count -gt 0) {
        throw 'MSI must embed its cabinets; no external CAB file should be needed.'
    }
    $permissions = @(Read-Rows 'SELECT `LockObject`,`Table`,`SDDLText` FROM `MsiLockPermissionsEx`' 3)
    $privateAcl = 'O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)'
    $publicAcl = $privateAcl + '(A;OICI;0x1200a9;;;AU)'
    $expectedAcls = @{ CLIENTROOT = $publicAcl; SERVICEROOT = $privateAcl; SERVICEDATA = $privateAcl; PUBLICSTATUS = $publicAcl }
    if ($permissions.Count -ne $expectedAcls.Count) { throw 'Both binary folders and both data/status folders need explicit permissions.' }
    foreach ($row in $permissions) {
        if ($row[1] -ne 'CreateFolder' -or -not $expectedAcls.ContainsKey($row[0]) -or $row[2] -cne $expectedAcls[$row[0]]) {
            throw 'MSI permissions do not match the restricted Nova folder policy.'
        }
        $expectedAcls.Remove($row[0])
    }
    if ($expectedAcls.Count -ne 0) { throw 'A Nova folder is missing its explicit permissions.' }
    $actions = @(Read-Rows 'SELECT `Action`,`Sequence` FROM `InstallExecuteSequence`' 2)
    $sequence = @{}
    foreach ($row in $actions) { $sequence[$row[0]] = [int]$row[1] }
    foreach ($action in @('NovaPreflightBefore', 'NovaPreflightAfter', 'StopServices', 'CreateFolders', 'StartServices', 'NovaVerifyInstalled', 'InstallFinalize')) {
        if (-not $sequence.ContainsKey($action)) { throw "MSI is missing $action." }
    }
    if ($sequence.NovaPreflightBefore -ge $sequence.StopServices -or $sequence.NovaPreflightAfter -le $sequence.StopServices -or
        $sequence.NovaPreflightAfter -ge $sequence.CreateFolders) { throw 'Safety checks must surround service stopping and precede filesystem changes.' }
    if ($sequence.NovaVerifyInstalled -le $sequence.StartServices -or $sequence.NovaVerifyInstalled -ge $sequence.InstallFinalize) {
        throw 'Installed folder/service checks must run after service startup and before MSI commit.'
    }
    Write-Host 'PASS: both self-contained apps, separate pairing, LocalSystem auto-service, embedded cabinets, folder permissions and preflight sequence.' -ForegroundColor Green
    Write-Host 'MSI metadata checks do not execute installation or prove runtime startup.'
} finally {
    if ($null -ne $database) { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
    $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}
