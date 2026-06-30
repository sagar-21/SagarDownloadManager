# Hosting Guide — Zero se Live (Hinglish)

Ye guide teri exact project ke liye hai. Koi bhi step skip mat karna.  
**Credit card kahi bhi nahi maangta** — sab free hai.

---

## Quick Reference — Poora Stack

| Component | Platform | URL | Cost |
|-----------|----------|-----|------|
| Database (PostgreSQL) | Neon.tech | neon.tech | Free, No CC |
| License Server (.NET) | Render.com | render.com | Free, No CC |
| Server Ko Jaaga Rakho | UptimeRobot | uptimerobot.com | Free, No CC |
| App Distribution | GitHub Releases | github.com | Free, No CC |
| Browser Extension | Edge Add-ons | aka.ms/MSEdgeAddons | Free, No CC |

---

## Part 1 — Local Testing (Pehle Locally Test Karo)

### Step 1 — RSA Keys Generate Karo (ek baar, teri machine pe)

```powershell
cd d:\projects\SagarDownloadManager
dotnet run --project DM.LicenseServer/tools/GenerateKeys
```

Output mein 2 files banegi:
- `keys/signing.key` → **Private key. Sirf server ke liye. KABHI commit mat karo.**
- `keys/signing.pub` → Public key. App mein embed hogi.

> ✅ `DM.LicenseServer/.gitignore` mein `keys/signing.key` already ignore hai — accidental push nahi hoga.

---

### Step 2 — Public Key App Mein Daalo

File kholo: `DM.App/Licensing/LicenseToken.cs`  
Line ~43 pe `PublicKeyPem` constant dhundho.

```powershell
# Public key content clipboard mein copy karo
Get-Content keys/signing.pub | Set-Clipboard
```

Phir `LicenseToken.cs` mein `PublicKeyPem` ke andar wala old key replace karo:

```csharp
private const string PublicKeyPem = """
    -----BEGIN PUBLIC KEY-----
    YAHAN SIGNING.PUB KA CONTENT PASTE KARO
    -----END PUBLIC KEY-----
    """;
```

---

### Step 3 — Session Master Secret Set Karo (ek baar)

```powershell
# Random 32-byte base64 string generate karo
[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

Copy karo aur `DM.LicenseServer/appsettings.json` mein:
```json
"Signing": {
  "SessionMasterSecret": "YAHAN_PASTE_KARO"
}
```

---

### Step 4 — Local Server Start Karo

**Terminal 1 — License Server:**
```powershell
dotnet run --project DM.LicenseServer/DM.LicenseServer.csproj
```

Server shuru hoga: `http://localhost:5000`

**Terminal 2 — Desktop App:**
```powershell
dotnet run --project DM.App/DM.App.csproj
```

---

### Step 5 — Admin Password Set Karo (pehli baar sirf)

`appsettings.json` mein Admin section:
```json
"Admin": {
  "Username": "admin",
  "PasswordHash": "CHANGE_ME",
  "PasswordSalt": "CHANGE_ME"
}
```

Server locally chal raha ho toh password set karo:
```powershell
$body = '{"password": "TumharaPassword123!@#"}'
Invoke-RestMethod -Method POST `
  -Uri "http://localhost:5000/admin/setup" `
  -ContentType "application/json" `
  -Body $body
```

Response: `Admin password configured.`

Ab `appsettings.json` mein Hash aur Salt update ho gaye honge — **in dono values copy karke note kar lo (Render env var mein dalenge).**

Phir file mein wapas `CHANGE_ME` kar do (git pe safe rahega, Render env var override karega):
```json
"Admin": {
  "PasswordHash": "CHANGE_ME",
  "PasswordSalt": "CHANGE_ME"
}
```

---

### Step 6 — Local Test

Browser mein: `http://localhost:5000/admin`  
Username: `admin`, Password: jo set kiya tha

Admin panel → Customers → New → License → New → Key copy karo → App mein Activate karo.

---

## Part 2 — Production Deploy (Free, No Credit Card)

### Step 7 — GitHub Pe Code Push Karo

```powershell
git init
git remote add origin https://github.com/TERA_USERNAME/SagarDownloadManager.git
git add .

# ZAROOR check karo — signing.key nahi dikhna chahiye
git status

git commit -m "Initial commit"
git push -u origin main
```

> ⚠️ `git status` mein `keys/signing.key` dikhna **nahi** chahiye. Agar dikhta hai toh push mat karo.

---

### Step 8 — Database Banao: Neon.tech (Free PostgreSQL, No CC)

1. **neon.tech** pe jao → **"Sign up"** → **"Continue with GitHub"**
2. **"New Project"** → Name: `sdm-licenses` → Region: Asia Pacific (Singapore) → **"Create project"**
3. Left sidebar → **"Connection Details"** → Dropdown: **"Pooled connection"** → **"Connection string"** tab → **Copy**

Connection string kuch aisa hoga:
```
postgresql://neondb_owner:PASSWORD@ep-abc-xyz.ap-southeast-1.aws.neon.tech/neondb?sslmode=require
```

**Ye string note kar lo — Render env var mein dalenge.**

---

### Step 9 — Server Deploy Karo: Render.com (Free, No CC)

1. **render.com** pe jao → **"Get Started for Free"** → **"Continue with GitHub"**
2. Dashboard → **"New +"** → **"Web Service"**
3. GitHub repo connect karo → `SagarDownloadManager` select karo

Settings exactly ye bharo:

| Field | Value |
|-------|-------|
| Name | `sdm-license-server` |
| Root Directory | `DM.LicenseServer` ← zaroori |
| Build Command | `dotnet publish -c Release -o out` |
| Start Command | `dotnet out/DM.LicenseServer.dll` |
| Instance Type | `Free` |

**"Environment Variables"** section mein ye sab add karo:

```
Database__Provider                  = postgres
Database__PostgresConnectionString  = postgresql://...  (Step 8 wala Neon URL)
SIGNING_KEY_CONTENT                 = (keys/signing.key ka pura content — multiline paste karo)
SIGNING_PUB_CONTENT                 = (keys/signing.pub ka pura content)
Admin__PasswordHash                 = (Step 5 mein copy kiya tha)
Admin__PasswordSalt                 = (Step 5 mein copy kiya tha)
ASPNETCORE_ENVIRONMENT              = Production
ASPNETCORE_URLS                     = http://+:10000
```

**SIGNING_KEY_CONTENT kaise paste karo:**
```powershell
Get-Content keys/signing.key | Set-Clipboard
```
Render ke env var field mein directly paste karo (multiline supported hai).

**"Create Web Service"** → 3–5 min wait karo → **Status: Live**

Test karo: `https://sdm-license-server.onrender.com/api/ping`  
Response aana chahiye: `{"status":"ok"}`

---

### Step 10 — App Mein Production Server URL Update Karo

File: `DM.Core/Settings/AppSettings.cs` — Line ~23:

```csharp
// PURANA:
public string LicenseServerUrl { get; set; } = "http://localhost:5000";

// NAYA — apna Render URL:
public string LicenseServerUrl { get; set; } = "https://sdm-license-server.onrender.com";
```

Changes commit karo:
```powershell
git add DM.Core/Settings/AppSettings.cs
git add DM.App/Licensing/LicenseToken.cs
git commit -m "Set production license server URL + public key"
git push
```

---

### Step 11 — Server Ko Jaaga Rakho: UptimeRobot (Free, No CC)

Render free tier 15 min idle hone ke baad server sojata hai (cold start 30 sec). Fix:

1. **uptimerobot.com** → **"Register for FREE"** (sirf email chahiye)
2. Login → **"Add New Monitor"**
3. Monitor Type: **HTTP(s)**
4. URL: `https://sdm-license-server.onrender.com/api/ping`
5. Interval: **5 minutes**
6. **"Create Monitor"**

Ab server kabhi nahi soyega.

---

### Step 12 — Production Admin Panel Login + License Banao

1. Browser mein: `https://sdm-license-server.onrender.com/admin`
2. Username: `admin` | Password: Step 5 wala
3. **Customers** → **"Create Customer"** → naam, email bharo
4. **Licenses** → **"Create License"** → customer select, plan, max devices set karo → Save
5. License key milegi (e.g., `XXXX-YYYY-ZZZZ-AAAA`) → copy karo → app mein test karo

---

## Part 3 — Windows Installer Banana

### Step 13 — App Publish Karo

```powershell
dotnet publish DM.App/DM.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o publish/win-x64
```

Output: `publish/win-x64/DM.App.exe`

yt-dlp aur ffmpeg bhi chahiye:
```powershell
New-Item -ItemType Directory -Force publish/win-x64/tools
Invoke-WebRequest "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe" -OutFile "publish/win-x64/tools/yt-dlp.exe"
# ffmpeg.exe bhi yahan copy karo: publish/win-x64/tools/ffmpeg.exe
```

---

### Step 14 — Inno Setup Installer Banana

1. **jrsoftware.org/isinfo.php** se Inno Setup download + install karo (free)
2. Project root mein `installer.iss` file banao:

```ini
[Setup]
AppName=Sagar Download Manager
AppVersion=1.0.0
AppPublisher=Tera Naam
DefaultDirName={autopf}\SagarDownloadManager
DefaultGroupName=Sagar Download Manager
OutputDir=installer_output
OutputBaseFilename=SagarDownloadManager_Setup_v1.0.0
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Files]
Source: "publish\win-x64\DM.App.exe";         DestDir: "{app}";       Flags: ignoreversion
Source: "publish\win-x64\tools\yt-dlp.exe";   DestDir: "{app}\tools"; Flags: ignoreversion
Source: "publish\win-x64\tools\ffmpeg.exe";   DestDir: "{app}\tools"; Flags: ignoreversion

[Icons]
Name: "{group}\Sagar Download Manager";       Filename: "{app}\DM.App.exe"
Name: "{commondesktop}\Sagar Download Manager"; Filename: "{app}\DM.App.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create desktop shortcut"

[Run]
Filename: "{app}\DM.App.exe"; Description: "Launch SDM"; Flags: nowait postinstall skipifsilent
```

3. Inno Setup Compiler mein file kholo → **Build → Compile** (F9)
4. Output: `installer_output/SagarDownloadManager_Setup_v1.0.0.exe`

---

### Step 15 — GitHub Release Banao

1. github.com/TERA_USERNAME/SagarDownloadManager pe jao
2. **Releases** → **"Create a new release"**
3. Tag: `v1.0.0` | Title: `Sagar Download Manager v1.0.0`
4. Assets mein `SagarDownloadManager_Setup_v1.0.0.exe` drag & drop karo
5. **"Publish release"**

Download link: `https://github.com/TERA_USERNAME/SagarDownloadManager/releases/latest`

---

## Part 4 — Browser Extension Publish Karo

### Step 16 — Extension Zip Banao

```powershell
Compress-Archive -Path "DM.BrowserExtension\*" -DestinationPath "sdm-extension.zip"
```

### Step 17 — Local Test (Chrome/Edge)

1. `chrome://extensions` ya `edge://extensions`
2. **"Developer mode"** ON karo
3. **"Load unpacked"** → `DM.BrowserExtension\` folder select karo
4. SDM app start karo → video wali site pe jao → hover karo → download button dikhna chahiye

### Step 18 — Edge Add-ons Store Submit Karo (Free, No CC)

1. **aka.ms/MSEdgeAddons** → Microsoft account se login
2. **"Submit new extension"** → `sdm-extension.zip` upload
3. Details bharo: Name, Description, Category: Productivity
4. **"Submit for review"** → 1–3 business days mein approve hoga

---

## Poora Flow — Summary

```
1. dotnet run --project DM.LicenseServer/tools/GenerateKeys  (keys banao)
2. signing.pub → LicenseToken.cs mein paste karo             (public key embed)
3. Local admin setup karo, hash copy karo                    (password)
4. git push → GitHub                                         (code upload)
5. neon.tech → Free PostgreSQL banao                         (database)
6. render.com → Web Service deploy karo + env vars           (server live)
7. uptimerobot.com → /api/ping monitor add karo              (server jaaga)
8. AppSettings.cs mein Render URL update karo                (app connect)
9. dotnet publish + Inno Setup → installer.exe               (app banana)
10. GitHub Release → installer upload                        (distribute)
11. Admin panel → Customer + License banao                   (key generate)
12. Edge Add-ons → extension submit karo                     (browser)
```

---

## Files Reference (Kaunsi File Mein Kya Change Hota Hai)

| File | Kya Change Karna Hai |
|------|----------------------|
| `DM.App/Licensing/LicenseToken.cs` | Line ~43: `PublicKeyPem` mein `keys/signing.pub` content paste karo |
| `DM.Core/Settings/AppSettings.cs` | Line ~23: `LicenseServerUrl` mein Render URL dalo |
| `DM.LicenseServer/appsettings.json` | `Signing:SessionMasterSecret` set karo (local dev ke liye) |
| `installer.iss` | New file — Inno Setup script (root mein banao) |

---

## Troubleshooting

**"Build failed" on Render:**  
→ Check karo `Root Directory` mein `DM.LicenseServer` dala hai ya nahi.

**"/api/ping" nahi chal raha:**  
→ Render logs mein error dekho. `ASPNETCORE_URLS=http://+:10000` env var set hai?

**Activation fail ho raha hai:**  
→ `LicenseToken.cs` mein naya public key dala? Build dobara karo.

**Admin login nahi ho raha:**  
→ Render mein `Admin__PasswordHash` aur `Admin__PasswordSalt` env vars set hain?

**Database tables nahi bani:**  
→ Neon connection string mein `?sslmode=require` hai? Connection string dobara check karo.
