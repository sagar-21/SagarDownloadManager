# Sagar Download Manager — How to Run

Full end-to-end setup from clean checkout to a working app + license server.

---

## Prerequisites

- .NET 9 SDK — verify: `dotnet --version` (must show 9.x)
- All commands run from the repo root: `d:\projects\SagarDownloadManager\`
- `curl` / PowerShell / Postman — for the one-time admin password step

---

## Phase A — License Server: First-Time Setup

### Step 1 — Generate RSA signing keys

Run once to create a 4096-bit key pair. Private key stays on the server; public key goes in the desktop app.

```powershell
dotnet run --project DM.LicenseServer/tools/GenerateKeys
```

Creates:
- `DM.LicenseServer\keys\signing.key` — private key. **Never commit. Never copy off server.**
- `DM.LicenseServer\keys\signing.pub` — public key. Embed in `DM.App` (Step 5).

### Step 2 — Set the session master secret

Generate a random 32-byte base64 value:

```powershell
[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

Paste the result into `DM.LicenseServer\appsettings.json`:

```json
"Signing": {
  "PrivateKeyPath":      "keys/signing.key",
  "PublicKeyPath":       "keys/signing.pub",
  "TokenExpiryHours":    48,
  "SessionMasterSecret": "← paste generated value here"
}
```

### Step 3 — Start the license server

```powershell
# Terminal 1 — keep open
dotnet run --project DM.LicenseServer/DM.LicenseServer.csproj
```

SQLite database is created automatically on first run. Note the URL printed (usually `https://localhost:5001`).

### Step 4 — Set the admin password (one-time only)

The `/admin/setup` endpoint only works while `Admin:PasswordHash` is still `"CHANGE_ME"`.

```powershell
Invoke-RestMethod -Method Post -Uri https://localhost:5001/admin/setup `
    -ContentType "application/json" `
    -SkipCertificateCheck `
    -Body '{"password":"YourSecurePassword123!"}'
```

After one successful call, the endpoint permanently returns 404. `appsettings.json` is updated with the hash.

---

## Phase B — Desktop App: First-Time Setup

### Step 5 — Embed the public key

Open `DM.LicenseServer\keys\signing.pub`, copy the entire contents, and paste into:

**File:** `DM.App\Licensing\LicenseToken.cs`

```csharp
private const string PublicKeyPem = """
    -----BEGIN PUBLIC KEY-----
    ← paste signing.pub contents here
    -----END PUBLIC KEY-----
    """;
```

### Step 6 — Point the app at the local server

**Option A (easiest):** Run the app once (it creates `settings.json`), close it, then edit:

```
%AppData%\DownloadManager\settings.json
```

Change:
```json
"licenseServerUrl": "https://localhost:5001"
```

**Option B (permanent dev default):** Edit `DM.Core\Settings\AppSettings.cs` line 23 to default to `https://localhost:5001`.

### Step 7 — Build and run the desktop app

```powershell
# Terminal 2
dotnet run --project DM.App/DM.App.csproj
```

The app opens showing the **activation window** — expected, since no license exists yet.

---

## Phase C — Create Your First License

### Step 8 — Open the admin panel

Navigate to `https://localhost:5001` in your browser. Accept the dev certificate warning.  
Log in: username `admin`, password from Step 4.

### Step 9 — Create a customer and issue a license key

1. **Customers** → **New Customer** → enter name + email → Save
2. **Licenses** → **New License** → select customer, pick plan (Basic / Pro / Enterprise), set max devices + expiry → Save
3. Copy the generated key (format: `DM25-XXXX-XXXX-XXXX`)

### Step 10 — Activate the desktop app

Paste the license key into the activation window and click **Activate**.  
On success the main download manager UI opens. Setup complete.

---

## Phase D — Daily Development

Every session, open two terminals:

```powershell
# Terminal 1 — License Server
dotnet run --project DM.LicenseServer/DM.LicenseServer.csproj

# Terminal 2 — Desktop App
dotnet run --project DM.App/DM.App.csproj
```

The app caches its license token locally (DPAPI-encrypted). After the first activation it starts without showing the activation window, even if the server is briefly down (48-hour grace period).

### Re-test activation from scratch

Delete the cached token:

```powershell
Remove-Item "$env:APPDATA\SagarDM\lic.dat" -ErrorAction SilentlyContinue
```

### Test revoke → app locks

1. In the admin panel: open the license → click **Revoke**
2. Restart the desktop app (heartbeat fires within ~30 seconds of startup)
3. App should show the lock screen with a "revoked" message

---

## Optional — Download Tools

Place these binaries in `DM.App\tools\` for actual download functionality:

- `yt-dlp.exe` — from github.com/yt-dlp/yt-dlp/releases
- `ffmpeg.exe` + `ffprobe.exe` — from ffmpeg.org

The `.csproj` copies them to the output directory automatically on build.
