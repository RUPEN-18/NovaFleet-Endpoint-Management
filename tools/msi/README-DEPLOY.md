# Admin and Client installation

Build output should contain LT.Admin.0.5.5.x64.msi, LT.Client.0.5.5.x64.msi, their SHA-256 files, Install-LTAdmin.ps1, Install-LTClient.ps1, DeploymentService-Common.ps1, Client-Preflight.ps1, BuildInfo.json and this guide. Keep the relevant helper files beside the MSIs when using the installation scripts.

Before installation, finish active application-deployment jobs and receive their final results. Close Admin before Admin MSI installation and Client before Client MSI installation. The build step itself does not require closing the working apps.

## 1. Admin PC

Open PowerShell as administrator, go to the generated MSI output folder, and run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-LTAdmin.ps1
```

The wrapper verifies the MSI checksum, installs it, and preserves an existing localhost Client compatibility key on the build PC without embedding that key in the MSI. When moving from another source project, use -ExistingSettingsPath with its Admin appsettings.json if needed. It copies only the Server compatibility key into the installed default config, not arbitrary endpoint configuration. Accounts, identity, certificate, pairing and history remain in their existing per-user AppData locations. Open L&T Admin from the Start menu using the same Windows user as before. Installing under another Windows user does not transfer private per-user accounts or certificate data.

The Admin installation wrapper updates an already enabled startup entry for the installing Windows user. If a different account supplied elevation, turn startup off and on in the installed Admin under its normal user so that entry points to Program Files.

The installed executable is:

```text
C:\Program Files\AutoShutdown.Nova.Admin\AutoShutdown.SystemA.exe
```

The existing firewall rule may still reference the old debug executable. From elevated PowerShell, enable the installed executable for the exact current Client addresses. Replace the example addresses with your current office/LAN addresses:

```powershell
& 'C:\Program Files\AutoShutdown.Nova.Admin\scripts\Configure-DeploymentLanFirewall.ps1' `
    -AdminAddress '192.168.1.10' -ClientAddress @('192.168.1.11','192.168.1.12')
```

In Admin Settings, save the assigned LAN address if it changed, then restart Admin. For a new Client PC, export its connection file using that PC's exact computer name and note the certificate fingerprint shown in Admin.

## 2. Client PC

Copy Client MSI, its checksum file, Install-LTClient.ps1, DeploymentService-Common.ps1 and Client-Preflight.ps1 to the PC. Run elevated PowerShell in that folder:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-LTClient.ps1
```

On a new PC, the MSI can also be installed directly using msiexec /i LT.Client.0.5.5.x64.msi /passive /norestart. The wrapper is needed to migrate a previous non-MSI service registration. It verifies that the old service uses the expected protected executable and LocalSystem account, checks no jobs/results are unfinished, stops it and removes only that old SCM registration. Existing files, pairing and ledger are retained. If MSI installation fails, it attempts to restore the old service registration. Keep the logs and send the output if recovery fails.

If the protected existing connection contains both service and control credentials, the wrapper restores the installed Client GUI settings from that same identity. Existing appsettings.json is retained. If no complete connection is available, open Connect L&T Client to Admin from the Start menu. Select the pairing file exported for THIS PC and paste the fingerprint shown by Admin. This one-time setup requires elevation. Then open L&T Client normally. Do not reuse another PC's pairing file.

The background service starts automatically at boot. Client GUI starts when a Windows user signs in and the PC is paired. Before pairing, startup stays silent; manually opening L&T Client explains how to connect. The Client installation wrapper removes the installing user's obsolete per-user AutoShutdownNovaSystemB startup entry after success; the MSI owns the machine-wide entry. Other Windows users' old per-user entries may need removing to avoid opening an old debug Client at sign-in. The packaged Client recognizes machine-managed startup.

Installed files are under:

```text
C:\Program Files\AutoShutdown.Nova.Client
C:\Program Files\AutoShutdown.Nova.DeploymentService
```

Credentials and job records remain under:

```text
C:\ProgramData\AutoShutdown.Nova.DeploymentService
```

## 3. Verify before wider use

1. Confirm Admin opens with the existing account and the intended LAN address.
2. Confirm Client shows connected and Admin shows both installer service and control connection online.
3. Check message delivery and a verified software installation on one PC, then a multi-PC batch and retry behavior on real confirmed failures.
4. Check Client reconnect after reboot/sign-in and verify that Admin startup points to the installed executable.
5. Test MSI install, upgrade, failed-install recovery and uninstall on a spare Windows PC. Uninstall should remove managed binaries, shortcuts and service registration while retaining runtime data. Do not uninstall during an active or unreported job.

Build and MSI database checks do not execute or prove these runtime scenarios. Remote shutdown and inactivity shutdown remain the existing app features.

## Logs and signing

The installation wrappers print the MSI log path under the installing user's TEMP folder. Installer preflight output may show expected rejection messages when an app is open, paths are unprotected, or jobs are unfinished.

These builds are unsigned unless your organization signs them. If the MSI is signed afterwards, update its .sha256.txt file with the new Get-FileHash value before using the wrappers. Company security policy can require a trusted signature. The installers do not override office network policy and do not provide cloud relay access.
