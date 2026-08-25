# Windows deployment and operations

## 1. Prepare the Server PC

1. Assign or reserve `192.168.1.100` for the Server PC.
2. Install a trusted LAN certificate whose SAN includes the server DNS name/IP used by Agents.
3. Create a restricted service account and a writable ACL-protected data directory, for example
   `C:\ProgramData\TempyServer`.
4. Allow inbound TCP 8765 only from the management LAN / Agent VLAN. Do not expose it to the public
   internet.
5. Build with `scripts\publish-win-x64.ps1` and copy the publish folder to
   `C:\Program Files\TempyServer`.

## 2. Production configuration

Copy `src\Server\appsettings.Production.example.json` outside source control to the publish folder
as `appsettings.Production.json`, then set secrets through the Windows service environment or
protected configuration. Prefer machine-level environment variables:

```powershell
[Environment]::SetEnvironmentVariable('BootstrapAdmin__Username', 'admin', 'Machine')
[Environment]::SetEnvironmentVariable('BootstrapAdmin__Password', '<long unique password>', 'Machine')
[Environment]::SetEnvironmentVariable('Security__AgentEnrollmentToken', '<long random bootstrap token>', 'Machine')
```

For normal operation set:

```json
{
  "Server": {
    "AdvertisedHost": "192.168.1.100",
    "Port": 8765,
    "UseHttps": true,
    "CertificatePath": "C:\\ProgramData\\TempyServer\\certs\\server.pfx"
  },
  "Security": {
    "AllowNewDeviceEnrollment": false,
    "AllowEnrollmentTokenForExistingDevices": false,
    "RequireHttpsForAdmin": true
  }
}
```

Do not leave automatic enrollment enabled after the known PCs are provisioned. Use the
administrator provisioning API to register a unique long per-device token; distribute
the plaintext token only to that device’s Agent installer.

## 3. Run as a service

The self-contained executable supports Windows service hosting. A manual install example:

```powershell
sc.exe create TempyManagementServer binPath= '"C:\Program Files\TempyServer\Server.exe" --service' start= auto
sc.exe description TempyManagementServer "Tempy durable LAN management server"
New-NetFirewallRule -DisplayName 'Tempy Agent WSS 8765' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 8765 -RemoteAddress 192.168.1.0/24
sc.exe start TempyManagementServer
```

Use `installer/ServerInstaller.iss` only after reviewing the output path, service account, signing,
and firewall policy for the local environment.

## 4. Backup and recovery

Stop the service (or use a SQLite online backup procedure) before copying the SQLite `.db` file.
Back up:

- `data\tempy-management.db` plus consistent `-wal` and `-shm` files when present;
- `data\keys` (ASP.NET Core data-protection keys, needed to preserve auth-cookie continuity);
- certificate material stored outside the app directory.

After a normal Server restart, device records, policy versions, operation history, and pending jobs
remain. Existing connections reconnect; `SENT` jobs without acknowledgements are re-queued by the
same command ID.

## 5. Operator checklist

- Verify dashboard online/offline count rather than assuming a command was delivered.
- Review operation detail rows: `SUCCESS` is agent-confirmed; `PENDING` is retained for offline PCs.
- Use **SYNC ALL** after desired DNS/browser changes; offline PCs synchronize after reconnect.
- Review failed/timed-out jobs and the audit trail regularly.
- Rotate bootstrap/enrollment tokens, disable enrollment, and rotate per-device tokens when a PC is
  reprovisioned.
