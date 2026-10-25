# MSI build tools

This folder contains the saved WiX 5.0.2 authoring and PowerShell build kit for Admin and Client 0.5.5.

First validate the recovered source using the root scripts/Verify-RecoveredSource.ps1 script. From the repository root on Windows, run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File .\tools\msi\Build-BothMSI.ps1 `
    -ProjectRoot (Get-Location).Path
```

The kit creates an isolated source copy, checks application tests, publishes the three Windows components, compiles two MSIs and validates their installer databases. It does not install either package. Runtime installation, pairing, reboot, upgrade and larger deployments remain separate checks.

Read the root README and docs/RECOVERY.md for the recovery status. Read README-DEPLOY.md for the saved installation procedure. Do not interpret historical results as validation of newly reconstructed Client files.
