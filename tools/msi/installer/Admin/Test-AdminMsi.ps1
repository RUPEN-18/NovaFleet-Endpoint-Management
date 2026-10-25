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
    foreach ($name in @('AutoShutdown.SystemA.exe', 'coreclr.dll', 'Configure-DeploymentLanFirewall.ps1', 'appsettings.json')) {
        if ($name -notin $files) { throw "Admin MSI is missing $name." }
    }
    foreach ($name in @('server.pfx', 'secrets.json', 'admin-accounts.json', 'admin-audit.jsonl', 'install-jobs.json', 'connection.json')) {
        if ($name -in $files) { throw 'Runtime pairing, passwords and history must not be embedded in the MSI.' }
    }
    $media = @(Read-Rows 'SELECT `Cabinet` FROM `Media`' 1)
    if ($media.Count -lt 1 -or @($media | Where-Object { -not $_[0].StartsWith('#') }).Count -gt 0) { throw 'All MSI cabinets must be embedded.' }
    $permissions = @(Read-Rows 'SELECT `LockObject`,`Table`,`SDDLText` FROM `MsiLockPermissionsEx`' 3)
    if ($permissions.Count -ne 1 -or $permissions[0][0] -ne 'ADMINROOT' -or $permissions[0][1] -ne 'CreateFolder' -or $permissions[0][2] -cne 'O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;AU)') { throw 'Admin binary permissions are incomplete.' }
    $shortcuts = @(Read-Rows 'SELECT `Name` FROM `Shortcut`' 1)
    if ($shortcuts.Count -lt 1) { throw 'The Start menu shortcut is missing.' }
    $actions = @(Read-Rows 'SELECT `Action`,`Sequence` FROM `InstallExecuteSequence`' 2)
    $sequence = @{}; foreach ($row in $actions) { $sequence[$row[0]] = [int]$row[1] }
    if (-not $sequence.ContainsKey('AdminPreflight') -or $sequence.AdminPreflight -ge $sequence.InstallFiles) { throw 'Admin preflight must precede file installation.' }
    Write-Host 'PASS: self-contained Admin, Start menu shortcut, embedded payload, restricted binary folder and no runtime secrets.' -ForegroundColor Green
    Write-Host 'Database checks do not prove installation or application startup.'
} finally {
    if ($null -ne $database) { $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) }
    $null = [Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}
