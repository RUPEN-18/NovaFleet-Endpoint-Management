# UI setup and troubleshooting

These are the intended UI steps for the saved MSI workflow. The recovered source still needs the Windows checks listed in RECOVERY.md.

## Admin setup

Start → L&T Admin → Create Super Admin on a new Windows profile, or sign in with the existing account.

Settings → Register a Client PC on the LAN → ADMIN PC IPv4 → SAVE LAN ADDRESS → close and reopen Admin.

Use the IPv4 assigned to the active trusted network adapter. Windows Settings → Network & internet → connected adapter → Properties shows its address. No office address is distributed with this repository.

## Export one file per Client

On the Client: Windows Settings → System → About → Device name.

On Admin: Settings → CLIENT PC NAME → enter that exact device name → EXPORT CLIENT CONNECTION → save the .nova-pairing.json file.

Obtain the full SERVER CERTIFICATE SHA-256 fingerprint directly from the intended Admin. Transfer the matching connection file securely. Do not use the same file on multiple PCs.

## Import on the installed Client

Start → Connect L&T Client to Admin → approve the Windows administrator prompt → select this PC's pairing file → Open → paste the Admin fingerprint → Enter.

After CLIENT PAIRED, close the setup window and open L&T Client normally. Keep Admin open. The MSI-installed service starts automatically; the Client UI starts at a signed-in user's session after pairing.

Admin → Client PCs → verify Installer service and Control app separately. A Dashboard online count alone does not prove software deployment is connected.

## Firewall

On the Admin PC, allow TCP 5057 and 5058 to the installed AutoShutdown.SystemA.exe for intended Client addresses, on the appropriate trusted Domain/Private profile. The packaged script accepts exact private IPv4 addresses. It does not turn off Windows Firewall. Work with the network administrator for subnet or cross-network deployment.

Windows Advanced Security supports program, port and address scope criteria:
https://learn.microsoft.com/en-us/windows/security/operating-system-security/network-security/windows-firewall/configure

## Common problems

| Symptom | First checks |
|---|---|
| No Client in Admin | Correct device name and per-PC export; imported on matching PC; Admin account scope; app running; network path |
| Control app offline | Sign in to Windows and open L&T Client; check paired GUI config |
| Installer service offline | Services → Nova Deployment Service → Running/Automatic; record startup error if it fails |
| Wrong computer or fingerprint | Cancel import; obtain a fresh export for that exact PC and verify fingerprint directly with Admin |
| Admin IP changed | Save the new address, reopen Admin, update firewall scope, re-export and re-import affected profiles |
| Buttons disabled | Check permissions, device assignments, active work and the Super Admin role for registration |
| Installation Not queued | Reconnect offline PCs or resolve busy/disabled/old-service/package rejection, then submit again |
| Failed or NeedsReview | Inspect evidence and actual PC state; retry confirmed failures only |
| Installer needs elevation | Approve UAC; migration helpers require elevated Windows PowerShell |
| Older service blocks MSI | Use the saved installation wrapper and accompanying support files; do not erase credentials or unfinished jobs |
| Account locked | Wait for the recorded 15-minute lockout; Super Admin recovery uses its private recovery code |
| Page scrolling difficult | Collapse history, use normal wheel for page scrolling and Ctrl plus wheel/table scrollbar for rows; check installed app version |

Never stop the service during an active installation. Keep private pairing files, passwords, recovery codes and account data out of bug reports.
