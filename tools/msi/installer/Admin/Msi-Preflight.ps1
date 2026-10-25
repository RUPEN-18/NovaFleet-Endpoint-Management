$ErrorActionPreference = 'Stop'
if (@(Get-Process -Name 'AutoShutdown.SystemA' -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'Close every Admin instance after all installation jobs finish, then run the MSI again.'
}
$root = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'AutoShutdown.Nova.Admin'
if (Test-Path -LiteralPath $root) {
    $items = New-Object 'System.Collections.Generic.List[string]'
    $items.Add($root)
    for ($i = 0; $i -lt $items.Count; $i++) {
        $entry = Get-Item -LiteralPath $items[$i] -Force
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Admin binary paths must not contain links.' }
        $acl = Get-Acl -LiteralPath $entry.FullName
        $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($owner -notin @('S-1-5-18', 'S-1-5-32-544')) { throw 'The existing Admin binary owner is not SYSTEM or Administrators.' }
        foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            $writes = [int][Security.AccessControl.FileSystemRights]::Write -bor [int][Security.AccessControl.FileSystemRights]::Delete -bor [int][Security.AccessControl.FileSystemRights]::ChangePermissions -bor [int][Security.AccessControl.FileSystemRights]::TakeOwnership
            if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and ($rule.FileSystemRights -band $writes) -ne 0 -and $rule.IdentityReference.Value -notin @('S-1-5-18', 'S-1-5-32-544')) { throw 'A non-administrator can modify the Admin binary folder.' }
        }
        if ($entry.PSIsContainer) { foreach ($child in @(Get-ChildItem -LiteralPath $entry.FullName -Force)) { $items.Add($child.FullName) } }
    }
}
# Admin data belongs to the Windows profile, outside the MSI. Check active jobs in existing profiles.
foreach ($profile in @(Get-CimInstance Win32_UserProfile | Where-Object { -not $_.Special })) {
    $history = Join-Path $profile.LocalPath 'AppData\Local\AutoShutdown.Nova\DeploymentAdmin\install-jobs.json'
    if (Test-Path -LiteralPath $history -PathType Leaf) {
        $data = Get-Content -LiteralPath $history -Raw | ConvertFrom-Json
        $terminal = @('Installed','AlreadyInstalled','Failed','NeedsReview','4','5','6','7')
        if (@($data.jobs | Where-Object { $terminal -notcontains [string]$_.state }).Count -gt 0) {
            throw 'An Admin profile has unfinished installation jobs. Reopen that Admin and finish the jobs before changing its installation.'
        }
    }
}
Write-Host 'Admin MSI preflight passed. Per-user pairing, account data and ProgramData history are outside MSI ownership.'
