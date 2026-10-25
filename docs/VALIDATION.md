# Static packaging validation

Checked 25 October 2026.

- PASS: 20 XML source/project/installer documents parsed.
- PASS: 22 explicit project, linked source, resource and configuration paths resolve.
- PASS: 75 declared UI event callbacks have matching code declarations.
- PASS: recovered Client settings property names and invoked hub method names match the saved Step 4 contracts.
- PASS: later per-PC acknowledgement and enforced-lockdown references are absent from Client code.
- PASS: SignalR.Client is set to 10.0.13. Tracked configuration examples contain blank credentials.
- PASS: saved MSI payload checksums match their packaging manifest.
- PASS: no private runtime records, machine pairing exports, private key files, known office addresses or generated binaries are included.
- PASS: Git exclusions cover build output, runtime configuration, credentials and installers, while retaining all prepared source files.

Not run: Windows PowerShell parser, C# compilation, the two .NET console check programs, WiX compilation, MSI installation, network pairing or live shutdown/deployment tests. C# interface/name checks and XML parsing do not prove a successful build. Run scripts/Verify-RecoveredSource.ps1 on Windows or inspect the Windows Actions job after upload.
