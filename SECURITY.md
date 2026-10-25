# Security notes

This is a source recovery edition with Windows runtime verification pending. Do not use an unverified recovery build to manage production machines.

Pairing files contain device credentials. Account/recovery data, private TLS keys, installer connection profiles, exported office inventory and authentication tokens must remain local. The included appsettings.example.json files contain only blank credentials and loopback defaults; populate configuration through the intended pairing flow, not by publishing a shared key.

Report an issue without passwords, private recovery codes, pairing contents or private certificates. The project has no public vulnerability-reporting address configured. Contact the repository owner privately for security-sensitive details.
