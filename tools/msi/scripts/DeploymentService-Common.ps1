#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$NovaServiceName = 'NovaDeploymentService'
$NovaExeName = 'AutoShutdown.DeploymentService.exe'
$NovaInstallRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'AutoShutdown.Nova.DeploymentService'
$NovaDataRoot = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AutoShutdown.Nova.DeploymentService'
$NovaStatusRoot = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'AutoShutdown.Nova.DeploymentStatus'
$NovaBinaryPath = '"' + (Join-Path $NovaInstallRoot $NovaExeName) + '"'
$NovaScExe = Join-Path $env:SystemRoot 'System32\sc.exe'

function Assert-NovaAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Open PowerShell using Run as administrator, then run this script again.'
        }
    } finally { $identity.Dispose() }
}

function Get-NovaOwnedService {
    $service = Get-CimInstance -ClassName Win32_Service -Filter "Name='$NovaServiceName'"
    if ($null -ne $service) {
        if ($service.PathName -ne $NovaBinaryPath -or $service.StartName -notin @('LocalSystem', 'NT AUTHORITY\SYSTEM')) {
            throw "A service named $NovaServiceName already exists with a different executable or account. It was not changed."
        }
    }
    return $service
}

function Get-NovaTreeItems([string] $Root) {
    $queue = [Collections.Generic.Queue[string]]::new()
    $queue.Enqueue($Root)
    while ($queue.Count -gt 0) {
        $path = $queue.Dequeue()
        $item = Get-Item -LiteralPath $path -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "A service folder or file is a link: $path"
        }
        $item
        if ($item.PSIsContainer) {
            foreach ($child in @(Get-ChildItem -LiteralPath $path -Force)) {
                $queue.Enqueue($child.FullName)
            }
        }
    }
}

function Assert-NovaProtectedTree([string] $Root) {
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { throw "Service directory not found: $Root" }
    $trusted = @('S-1-5-18', 'S-1-5-32-544') # SYSTEM and local Administrators.
    $writeRights = [Security.AccessControl.FileSystemRights]::Write -bor
        [Security.AccessControl.FileSystemRights]::Delete -bor
        [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor
        [Security.AccessControl.FileSystemRights]::ChangePermissions -bor
        [Security.AccessControl.FileSystemRights]::TakeOwnership
    foreach ($item in @(Get-NovaTreeItems $Root)) {
        $acl = Get-Acl -LiteralPath $item.FullName
        $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($owner -notin $trusted) {
            throw "Service path has an unexpected owner: $($item.FullName)"
        }
        foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
                ($rule.FileSystemRights -band $writeRights) -ne 0 -and $rule.IdentityReference.Value -notin $trusted) {
                throw "A non-administrator can modify a service path: $($item.FullName)"
            }
        }
    }
}

function Protect-NovaTree {
    param([string] $Root, [switch] $AllowUserRead)
    foreach ($item in @(Get-NovaTreeItems $Root)) {
        if ($item.PSIsContainer) {
            $acl = [Security.AccessControl.DirectorySecurity]::new()
            $inheritance = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
        } else {
            $acl = [Security.AccessControl.FileSecurity]::new()
            $inheritance = [Security.AccessControl.InheritanceFlags]::None
        }
        $acl.SetAccessRuleProtection($true, $false)
        $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
        foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
            $rule = [Security.AccessControl.FileSystemAccessRule]::new(
                [Security.Principal.SecurityIdentifier]::new($sid),
                [Security.AccessControl.FileSystemRights]::FullControl,
                $inheritance,
                [Security.AccessControl.PropagationFlags]::None,
                [Security.AccessControl.AccessControlType]::Allow)
            $acl.AddAccessRule($rule)
        }
        if ($AllowUserRead) {
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
                [Security.Principal.SecurityIdentifier]::new('S-1-5-11'),
                [Security.AccessControl.FileSystemRights]::ReadAndExecute,
                $inheritance,
                [Security.AccessControl.PropagationFlags]::None,
                [Security.AccessControl.AccessControlType]::Allow))
        }
        Set-Acl -LiteralPath $item.FullName -AclObject $acl
    }
}

function Stop-NovaService {
    $owned = Get-NovaOwnedService
    if ($null -eq $owned) { return }
    $controller = Get-Service -Name $NovaServiceName
    try {
        if ($controller.Status -ne 'Stopped') {
            Stop-Service -Name $NovaServiceName
            $controller.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(20))
        }
    } finally { $controller.Dispose() }
}

function Invoke-NovaSc([string[]] $ScArguments) {
    & $NovaScExe @ScArguments
    if ($LASTEXITCODE -ne 0) { throw "Windows service command failed with exit code $LASTEXITCODE." }
}
