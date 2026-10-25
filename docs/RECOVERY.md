# Recovery and validation

Prepared 25 October 2026 from the project files retained in the development conversation. This is a recovery edition, not a verified copy of the source on the original Windows build PC.

## Provenance

| Component | Recovery basis |
|---|---|
| Admin and Shared | Saved Step 4 source with the second scrolling fix |
| Deployment service | Saved service version 0.5.0 source |
| Client | Available later 0.6.0 development snapshot adapted to the saved Step 4 models and hub methods |
| MSI kit | Saved 0.5.5 kit with the PowerShell 5.1 multiline condition correction |
| Checks | Saved AdminSecurityChecks and InstallBatchChecks console programs |

## Client recovery edits

- Retained pinned HTTPS, device identity, control messages, heartbeat, deployment probes and orderly application shutdown handling.
- Removed the later policy protocol header, PolicyProposed event, policy revision/signature fields and AcknowledgePolicy handshake because the saved Step 4 server does not implement them.
- Retained SettingsChanged and GetSettings, which the saved server does implement.
- Removed later CentrallyEnforced window/exit and shutdown-cancellation restrictions; restored ordinary Client window controls and the saved MSI-managed startup helper.
- Updated SignalR.Client from 10.0.12 to 10.0.13, matching the package correction recorded during the earlier Windows MSI work.
- Marked the Client informational version and displayed title as recovered.
- Kept automatic inactivity shutdown disabled after a process restart until a fresh server settings response is received. A transient disconnect after a successful response keeps the current settings.

Blank default credentials replace the earlier shared development placeholder. Current account stores, device tokens, certificates, runtime settings and installers are not part of this archive.

## Checks completed in the packaging environment

See VALIDATION.md for the actual static checks and their results. The packaging environment has no Windows desktop, .NET SDK, PowerShell runtime or WiX. A static check is not a C# compilation or a runtime test.

## Required Windows checks

1. Run scripts/Verify-RecoveredSource.ps1, or inspect the Windows GitHub Actions job after pushing.
2. Open Admin and create a temporary account under an isolated Windows test user.
3. Install a test Client/service package on a spare PC and import its exact connection profile.
4. Verify both online states, settings refresh, pause/resume, message acknowledgement and image delivery.
5. Test inactivity and shutdown behavior only on a disposable test PC with unsaved work closed.
6. Check deployment verification, partial batch results and retry against actual software results.
7. Check Client restart/sign-in, uninstall and upgrade behavior. Load testing for 100+ Clients remains separate work.

Do not attribute the earlier 117 successful checks or earlier MSI installation result to this recovery edition until these checks are rerun. If the original Windows source becomes available later, compare the recovered Client and service against it before replacing them.
