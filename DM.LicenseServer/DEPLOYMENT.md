# Deployment Guide — DM.LicenseServer

## Architecture overview

```
DM.App (desktop)
    │  POST /api/v1/activate   (on install)
    │  POST /api/v1/validate   (every 24h heartbeat)
    │  POST /api/v1/deactivate (on uninstall)
    ▼
DM.LicenseServer  ──────  SQLite / PostgreSQL
    │
    └── /admin/*  (Razor Pages, cookie auth)
```

The server is the **single source of truth**.  
The desktop app holds a short-lived JWT (48 h) that it refreshes via heartbeat.  
Revoking a license takes effect at the next heartbeat — no client-side killswitch needed.

---

## First-time setup

### 1. Generate the signing key pair

Run once on the server. Never run on a developer machine if you can avoid it.

```bash
cd /opt/dm-license           # wherever you deploy to
dotnet run --project tools/GenerateKeys
```

This writes:
- `keys/signing.key` — private key. **Stays on this machine only.**
- `keys/signing.pub` — public key. Copy the PEM text into `DM.App`.

### 2. Embed the public key in DM.App

In `DM.App/Services/LicenseValidator.cs` (create this file), add:

```csharp
private const string PublicKeyPem = """
-----BEGIN PUBLIC KEY-----
<paste the content of keys/signing.pub here>
-----END PUBLIC KEY-----
""";
```

The app uses this to verify JWT signatures. It **never** calls private-key operations.

### 3. Configure appsettings.json (or env vars)

Minimum production changes:

```json
{
  "Database": {
    "Provider": "sqlite",
    "SqliteConnectionString": "Data Source=/data/licensedb.sqlite"
  },
  "Signing": {
    "PrivateKeyPath": "/opt/dm-license/keys/signing.key",
    "PublicKeyPath":  "/opt/dm-license/keys/signing.pub"
  },
  "Admin": {
    "Username":     "admin",
    "PasswordHash": "CHANGE_ME",
    "PasswordSalt": "CHANGE_ME"
  }
}
```

Leave `PasswordHash` and `PasswordSalt` as `"CHANGE_ME"` — the first-run setup
endpoint (`POST /admin/setup`) will patch them in place when you set your password.

### 4. Run the app and set the admin password

```bash
dotnet DM.LicenseServer.dll
```

Open `https://your-server/admin/setup` and set a strong password.  
That endpoint returns 404 after the password is set.

### 5. HTTPS

Use a reverse proxy (nginx/Caddy) for TLS. The app itself can run on plain HTTP
behind the proxy. Configure the `ASPNETCORE_URLS` env var:

```bash
export ASPNETCORE_URLS="http://127.0.0.1:5000"
```

Caddy example (`/etc/caddy/Caddyfile`):

```
licenses.yourapp.com {
    reverse_proxy 127.0.0.1:5000
}
```

---

## Switching to PostgreSQL

1. Change `"Provider": "postgres"` in `appsettings.json`.
2. Fill in `"PostgresConnectionString": "Host=...;Database=...;Username=...;Password=..."`.
3. Replace `db.Database.EnsureCreated()` in `Program.cs` with
   `db.Database.Migrate()` and run `dotnet ef migrations add Init`.

---

## Signing key security model

```
SERVER              APP (desktop)
──────────────      ─────────────────────────────────
signing.key  ──►  generates JWT
signing.pub        signing.pub (embedded constant)
                        │
                        └─► verifies JWT locally
                             (no network call needed)
```

**Why RSA asymmetric signing?**

If the app had the private key, an attacker could extract it and mint their own
valid tokens — bypassing the server entirely. With RSA:

- The **private key** is only on the server. It signs tokens.
- The **public key** is in the app. It can verify but not create tokens.
- Revoking a license: the server returns `"action": "disable"` on the next
  heartbeat. The app receives this and disables itself — even though the locally
  cached token is still cryptographically valid.

**Token lifetime (48 hours)** is intentionally short so the server stays in
control. A revoked license is disabled within 48 hours at most (at next heartbeat).

---

## systemd service (Linux)

`/etc/systemd/system/dm-license.service`:

```ini
[Unit]
Description=DM LicenseServer
After=network.target

[Service]
WorkingDirectory=/opt/dm-license
ExecStart=/usr/bin/dotnet /opt/dm-license/DM.LicenseServer.dll
Restart=always
RestartSec=5
User=www-data
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://127.0.0.1:5000

[Install]
WantedBy=multi-user.target
```

```bash
systemctl enable --now dm-license
```

---

## Backup

Back up daily:
- `keys/signing.key` (irreplaceable — losing it invalidates all existing tokens)
- `licensedb.sqlite` (or PostgreSQL dump)

Store key backups **offline** (encrypted USB or a hardware security module).
Never store them in the same cloud bucket as the database backup.

---

## Rate limiting defaults

| Endpoint        | Limit      | Window |
|-----------------|------------|--------|
| `/activate`     | 10 / IP    | 1 min  |
| `/validate`     | 120 / IP   | 1 min  |
| `/deactivate`   | 10 / IP    | 1 min  |

Tune in `Program.cs` (`RateLimitPartition`) if needed.

---

## Upgrading

1. Back up the database and `keys/`.
2. Deploy the new build.
3. If schema changed, run `dotnet ef database update` (or `EnsureCreated` handles
   non-breaking additions automatically for SQLite).
4. Restart the service.
