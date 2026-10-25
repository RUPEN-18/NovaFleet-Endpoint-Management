#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ProjectRoot,
    [string]$OutputFolder = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$kit = $PSScriptRoot
$version = '0.5.5'
$utf8 = New-Object Text.UTF8Encoding($false)
if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitOperatingSystem -or [IntPtr]::Size -ne 8) { throw 'Build with 64-bit PowerShell on a 64-bit Windows PC.' }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The build PC needs the .NET 10 SDK.' }
$sdks = & dotnet --list-sdks
if ($LASTEXITCODE -ne 0 -or -not ($sdks -match '^10\.')) { throw 'The .NET 10 SDK is required.' }
$root = (Resolve-Path -LiteralPath $ProjectRoot).Path
foreach ($relative in @('Directory.Build.props', 'src\AutoShutdown.Admin\AutoShutdown.Admin.csproj', 'src\AutoShutdown.Admin\Infrastructure\AdminSecurityStore.cs',
    'src\AutoShutdown.Admin\MainWindow.Installation.Batch.cs', 'src\AutoShutdown.Client\AutoShutdown.Client.csproj',
    'src\AutoShutdown.Client\App.xaml.cs', 'src\AutoShutdown.Client\Services\ClientAgent.cs', 'src\AutoShutdown.Client\Services\ClientOptions.cs',
    'src\AutoShutdown.DeploymentService\AutoShutdown.DeploymentService.csproj', 'src\AutoShutdown.DeploymentService\ServiceConstants.cs',
    'tests\AdminSecurityChecks\AdminSecurityChecks.csproj', 'tests\InstallBatchChecks\InstallBatchChecks.csproj')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)) { throw ('The working Step 4 project is missing: ' + $relative) }
}
$clientApp = [IO.File]::ReadAllText((Join-Path $root 'src\AutoShutdown.Client\App.xaml.cs'))
$agent = [IO.File]::ReadAllText((Join-Path $root 'src\AutoShutdown.Client\Services\ClientAgent.cs'))
if ($clientApp -notmatch 'CertificateSha256' -or $clientApp -notmatch 'DeviceId' -or $agent -notmatch 'CertificateSha256') {
    throw 'Client source predates the working pinned-HTTPS Client. Send its project/source before building; old insecure Client source will not be packaged.'
}
if ($agent -match 'SignedPolicy') { throw 'Client source includes later policy work. This kit targets the working Step 4 protocol; send that source before packaging.' }
$serviceConstants = [IO.File]::ReadAllText((Join-Path $root 'src\AutoShutdown.DeploymentService\ServiceConstants.cs'))
$match = [regex]::Match($serviceConstants, 'Version\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"')
if (-not $match.Success -or [version]$match.Groups[1].Value -lt [version]'0.5.0') { throw 'The working installer service 0.5.0 or newer is required.' }
$serviceVersion = $match.Groups[1].Value
# Parse all kit scripts before running tools. PowerShell 5.1 is supported.
foreach ($file in Get-ChildItem -LiteralPath $kit -Filter '*.ps1' -Recurse -File) {
    $tokens = $null; $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) { throw ('Script syntax error: ' + $file.Name + ': ' + ($errors.Message -join '; ')) }
}
$manifest = Get-Content -LiteralPath (Join-Path $kit 'kit-files.json') -Raw | ConvertFrom-Json
foreach ($item in $manifest) {
    if ($item.Path.Contains('..') -or [IO.Path]::IsPathRooted($item.Path)) { throw 'Invalid kit manifest path.' }
    $file = Join-Path $kit $item.Path
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $item.Sha256) { throw ('Kit checksum mismatch: ' + $item.Path) }
}
if ([string]::IsNullOrWhiteSpace($OutputFolder)) {
    $OutputFolder = Join-Path ([Environment]::GetFolderPath('Desktop')) ('LT-MSI-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,6))
}
$OutputFolder = [IO.Path]::GetFullPath($OutputFolder)
if (Test-Path -LiteralPath $OutputFolder) { throw 'Choose a new output folder. Existing release files are never overwritten.' }
$work = Join-Path $env:LOCALAPPDATA ('LT-MsiBuilds\' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
$snapshot = Join-Path $work 'source'; [void][IO.Directory]::CreateDirectory($snapshot)
function Copy-SourceTree([string]$from, [string]$to) {
    $entry = Get-Item -LiteralPath $from -Force
    if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('Source links are not copied: ' + $from) }
    [void][IO.Directory]::CreateDirectory($to)
    foreach ($file in Get-ChildItem -LiteralPath $from -Force) {
        if ($file.PSIsContainer -and $file.Name -in @('bin','obj','.git')) { continue }
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('Source links are not copied: ' + $file.FullName) }
        $target = Join-Path $to $file.Name
        if ($file.PSIsContainer) { Copy-SourceTree $file.FullName $target }
        elseif ($file.Extension -notin @('.pfx','.p12','.msi','.zip') -and $file.Name -notin @('connection.json','secrets.json','admin-accounts.json','install-jobs.json','admin-audit.jsonl') -and $file.Name -notlike '*.nova-pairing.json') {
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
    }
}
Write-Host 'Copying working source to an isolated build folder. The running installation and source are not changed.'
Copy-SourceTree (Join-Path $root 'src') (Join-Path $snapshot 'src')
Copy-SourceTree (Join-Path $root 'tests') (Join-Path $snapshot 'tests')
foreach ($name in @('Directory.Build.props','Directory.Build.targets')) {
    if (Test-Path -LiteralPath (Join-Path $root $name)) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $snapshot $name) }
}
# Include the latest scrolling/refresh fix only in the isolated build copy.
$uiFiles = Get-Content -LiteralPath (Join-Path $kit 'ui-fix\files.json') -Raw | ConvertFrom-Json
foreach ($item in $uiFiles) {
    $destination = Join-Path $snapshot $item.Relative
    $current = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    if ($current -notin @($item.Before,$item.Step4Before,$item.After)) { throw ('UI source has changed. Send this file before packaging it: ' + $item.Relative) }
    Copy-Item -LiteralPath (Join-Path (Join-Path $kit 'ui-fix\payload') $item.Relative) -Destination $destination -Force
}
# Recognize MSI-managed startup without importing later policy code.
Copy-Item -LiteralPath (Join-Path $kit 'client-ui\StartupManager.cs') -Destination (Join-Path $snapshot 'src\AutoShutdown.Client\Services\StartupManager.cs') -Force
$clientWindowPath = Join-Path $snapshot 'src\AutoShutdown.Client\ClientWindow.xaml.cs'
if (Test-Path -LiteralPath $clientWindowPath) {
    $windowSource = [IO.File]::ReadAllText($clientWindowPath)
    $startupLine = 'StartupCheck.IsChecked = StartupManager.IsEnabled();'
    if ($windowSource.Contains($startupLine) -and -not $windowSource.Contains('StartupCheck.IsEnabled = !StartupManager.IsManaged')) {
        $windowSource = $windowSource.Replace($startupLine, $startupLine + "`r`n            StartupCheck.IsEnabled = !StartupManager.IsManaged;`r`n            if (StartupManager.IsManaged) StartupCheck.ToolTip = `"Startup is enabled for all users by Client setup.`";")
        [IO.File]::WriteAllText($clientWindowPath, $windowSource, $utf8)
    }
}
# Compile and test the user's actual working source, not the unfinished unified source tree.
foreach ($test in @('InstallBatchChecks','AdminSecurityChecks')) {
    Write-Host ('Running ' + $test + ' with isolated test data.')
    & dotnet run --project (Join-Path $snapshot ('tests\' + $test + '\' + $test + '.csproj')) -c Release
    if ($LASTEXITCODE -ne 0) { throw ('Checks failed: ' + $test + '. No MSI release is ready.') }
}
$publishRoot = Join-Path $work 'publish'
$published = @{}
foreach ($role in @('Admin','Client','DeploymentService')) {
    $destination = Join-Path $publishRoot $role
    $project = Join-Path $snapshot ('src\AutoShutdown.' + $role + '\AutoShutdown.' + $role + '.csproj')
    Write-Host ('Publishing self-contained win-x64 ' + $role + '.')
    & dotnet publish $project -c Release -r win-x64 --self-contained true -o $destination --nologo -p:DebugType=None -p:DebugSymbols=false -p:PublishSingleFile=false -p:Version=$version
    if ($LASTEXITCODE -ne 0) { throw ('Publish failed for ' + $role + '. No MSI release is ready.') }
    $published[$role] = $destination
    foreach ($file in Get-ChildItem -LiteralPath $destination -Recurse -File) {
        if ($file.Name -like 'appsettings*.json') { Remove-Item -LiteralPath $file.FullName -Force; continue }
        if ($file.Extension -in @('.pfx','.p12') -or $file.Name -in @('connection.json','secrets.json','admin-accounts.json','admin-audit.jsonl','install-jobs.json') -or $file.Name -like '*.nova-pairing.json') {
            throw ('Runtime secrets/history found in publish output: ' + $file.Name)
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $destination 'coreclr.dll'))) { throw 'A publish is missing its self-contained .NET runtime.' }
}
[IO.File]::WriteAllText((Join-Path $published.Admin 'appsettings.json'), '{"Server":{"Url":"http://127.0.0.1:5055","ClientBaseUrl":"http://127.0.0.1:5055","ApiKey":""}}', $utf8)
foreach ($pair in @(@('Admin','AutoShutdown.SystemA.exe'),@('Client','AutoShutdown.SystemB.exe'),@('DeploymentService','AutoShutdown.DeploymentService.exe'))) {
    if (-not (Test-Path -LiteralPath (Join-Path $published[$pair[0]] $pair[1]) -PathType Leaf)) { throw ('Missing published executable: ' + $pair[1]) }
}
$adminSupport = Join-Path $work 'admin-support'; $clientSupport = Join-Path $work 'client-support'
[void][IO.Directory]::CreateDirectory($adminSupport); [void][IO.Directory]::CreateDirectory($clientSupport)
Copy-Item -LiteralPath (Join-Path $kit 'scripts\Configure-DeploymentLanFirewall.ps1') -Destination $adminSupport
foreach ($name in @('DeploymentService-Common.ps1','Import-DeploymentConnection.ps1','Test-DeploymentService.ps1')) {
    Copy-Item -LiteralPath (Join-Path $kit ('scripts\' + $name)) -Destination $clientSupport
}
foreach ($name in @('Connect-NovaClient.ps1','Start-ManagedClient.ps1','Open-LTClient.ps1','Connect-ClientWizard.ps1')) {
    Copy-Item -LiteralPath (Join-Path $kit ('installer\Client\' + $name)) -Destination $clientSupport
}
$healthScript = Join-Path $clientSupport 'Test-DeploymentService.ps1'
[IO.File]::WriteAllText($healthScript, ([IO.File]::ReadAllText($healthScript).Replace('EXPECTED_SERVICE_VERSION', $serviceVersion)), $utf8)
$tools = Join-Path $work 'tools'
Write-Host 'Downloading isolated WiX 5.0.2 build tools from NuGet.'
& dotnet tool install wix --version 5.0.2 --tool-path $tools --allow-roll-forward
if ($LASTEXITCODE -ne 0) { throw 'WiX download failed. Check the build PC internet/NuGet connection.' }
$wix = Join-Path $tools 'wix.exe'
Push-Location $work
try {
    & $wix extension add WixToolset.Util.wixext/5.0.2
    if ($LASTEXITCODE -ne 0) { throw 'WiX Util extension download failed.' }
    function Encode-CheckedScript([string]$body) {
        $wrapped = "try {`r`n" + ($body -replace '(?m)^#Requires[^\r\n]*', '') + "`r`n} catch { [Console]::Error.WriteLine(`$_.Exception.Message); exit 1 }`r`n"
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($wrapped))
        if ($encoded.Length -gt 28000) { throw 'An MSI action exceeds the supported command size.' }
        return $encoded
    }
    $candidates = @()
    foreach ($role in @('Admin','Client')) {
        $authoring = Join-Path $kit ('installer\' + $role)
        $preflight = [IO.File]::ReadAllText((Join-Path $authoring 'Msi-Preflight.ps1'))
        if ($role -eq 'Client') { $preflight = [IO.File]::ReadAllText((Join-Path $clientSupport 'DeploymentService-Common.ps1')) + "`r`n" + $preflight }
        $encoded = Encode-CheckedScript $preflight
        $candidate = Join-Path $work ('LT.' + $role + '.' + $version + '.x64.msi')
        $arguments = @('build', (Join-Path $authoring ($role + '.wxs')), '-arch', 'x64', '-d', ('AppPublish=' + $published[$role]),
            '-d', ('SupportScripts=' + $(if ($role -eq 'Admin') { $adminSupport } else { $clientSupport })), '-d', ('PreflightEncoded=' + $encoded), '-ext', 'WixToolset.Util.wixext', '-o', $candidate)
        if ($role -eq 'Client') {
            $hash = (Get-FileHash -LiteralPath (Join-Path $clientSupport 'DeploymentService-Common.ps1') -Algorithm SHA256).Hash
            $verification = [IO.File]::ReadAllText((Join-Path $authoring 'Msi-VerifyInstallation.ps1')).Replace('NOVA_EXPECTED_COMMON_SHA256', $hash)
            $arguments += @('-d', ('ServicePublish=' + $published.DeploymentService), '-d', ('VerifyEncoded=' + (Encode-CheckedScript $verification)))
        }
        Write-Host ('Compiling ' + $role + ' MSI and validating its installer database.')
        & $wix @arguments
        if ($LASTEXITCODE -ne 0) { throw ($role + ' MSI compilation failed. No MSI release is ready.') }
        & (Join-Path $authoring ('Test-' + $role + 'Msi.ps1')) -Path $candidate
        $candidates += $candidate
    }
    [void][IO.Directory]::CreateDirectory($OutputFolder)
    foreach ($candidate in $candidates) {
        $destination = Join-Path $OutputFolder ([IO.Path]::GetFileName($candidate))
        Copy-Item -LiteralPath $candidate -Destination $destination
        [IO.File]::WriteAllText(($destination + '.sha256.txt'), ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash + '  ' + [IO.Path]::GetFileName($destination) + "`r`n"), $utf8)
    }
    Copy-Item -LiteralPath (Join-Path $kit 'Install-LTAdmin.ps1') -Destination $OutputFolder
    Copy-Item -LiteralPath (Join-Path $kit 'Install-LTClient.ps1') -Destination $OutputFolder
    Copy-Item -LiteralPath (Join-Path $kit 'README-DEPLOY.md') -Destination $OutputFolder
    Copy-Item -LiteralPath (Join-Path $kit 'scripts\DeploymentService-Common.ps1') -Destination $OutputFolder
    Copy-Item -LiteralPath (Join-Path $kit 'installer\Client\Msi-Preflight.ps1') -Destination (Join-Path $OutputFolder 'Client-Preflight.ps1')
    $info = @{ PackageVersion=$version; ServiceVersion=$serviceVersion; BuiltAt=(Get-Date).ToString('o'); SourceProject=$root; BuildWorkspace=$work }
    [IO.File]::WriteAllText((Join-Path $OutputFolder 'BuildInfo.json'), ($info | ConvertTo-Json), $utf8)
    Write-Host 'BOTH MSI FILES BUILT; APPLICATION CHECKS AND MSI DATABASE CHECKS PASSED.' -ForegroundColor Green
    Write-Host ('Admin MSI: ' + (Join-Path $OutputFolder ('LT.Admin.' + $version + '.x64.msi')))
    Write-Host ('Client MSI: ' + (Join-Path $OutputFolder ('LT.Client.' + $version + '.x64.msi')))
    Write-Host 'Install/startup/pairing must still be checked on Windows before wider rollout. This build did not install either app.'
    Start-Process explorer.exe -ArgumentList ('"' + $OutputFolder + '"')
} finally { Pop-Location }
