#Requires -Version 5.1
<#
.SYNOPSIS
    Full release pipeline: publish → verify tools → obfuscate → sign → package installer → sign installer.

.DESCRIPTION
    Run from the repository root:
        .\scripts\Release.ps1 -Version "1.2.0"

    Prerequisites (installed and on PATH):
        dotnet          .NET 9 SDK
        obfuscar.console  Obfuscar — install via: dotnet tool install -g Obfuscar.GlobalTool
        signtool.exe    Windows SDK (for code signing — skip with -SkipSign)
        iscc.exe        Inno Setup 6 — https://jrsoftware.org/isinfo.php

    Third-party binaries (NOT in source control):
        DM.App\tools\yt-dlp.exe     Download before release
        DM.App\tools\ffmpeg.exe
        DM.App\tools\ffprobe.exe

.PARAMETER Version
    Semantic version string, e.g. "1.2.0". Must match AssemblyVersion in DM.App.csproj.

.PARAMETER CertThumbprint
    SHA-1 thumbprint of your code-signing certificate in the Windows certificate store.
    Run: Get-ChildItem Cert:\CurrentUser\My | Select Subject,Thumbprint

.PARAMETER TimestampUrl
    RFC 3161 timestamp authority URL. Defaults to DigiCert's free TSA.

.PARAMETER SkipSign
    Skip signtool steps (useful for local test builds without a certificate).

.PARAMETER SkipObfuscate
    Skip Obfuscar step (useful when iterating on installer layout).
#>

param(
    [Parameter(Mandatory)]
    [string]$Version,

    [string]$CertThumbprint = "",

    [string]$TimestampUrl = "http://timestamp.digicert.com",

    [switch]$SkipSign,

    [switch]$SkipObfuscate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$RepoRoot   = $PSScriptRoot | Split-Path -Parent
$AppProject = Join-Path $RepoRoot "DM.App\DM.App.csproj"
$PublishDir = Join-Path $RepoRoot "publish\win-x64"
$ObfDir     = Join-Path $RepoRoot "publish\win-x64-obf"
$InstallerScript = Join-Path $RepoRoot "installer\setup.iss"
$InstallerOutDir = Join-Path $RepoRoot "installer\output"
$ToolsDir   = Join-Path $RepoRoot "DM.App\tools"

function Step([string]$Name) {
    Write-Host ""
    Write-Host "══════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "  $Name" -ForegroundColor Cyan
    Write-Host "══════════════════════════════════════════════════════" -ForegroundColor Cyan
}

function Assert-Tool([string]$Name, [string]$Hint = "") {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        $msg = "Required tool not found: $Name"
        if ($Hint) { $msg += "`n  $Hint" }
        Write-Error $msg
    }
}

# ── Validate version format ───────────────────────────────────────────────────
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Write-Error "Version must be in the form MAJOR.MINOR.PATCH (e.g. 1.2.0)"
}

# ── Check required tools ─────────────────────────────────────────────────────
Step "Pre-flight checks"
Assert-Tool "dotnet"             "Install .NET 9 SDK from https://dotnet.microsoft.com/download"
if (-not $SkipObfuscate) {
    Assert-Tool "obfuscar.console"   "Run: dotnet tool install -g Obfuscar.GlobalTool"
}
Assert-Tool "iscc"               "Install Inno Setup 6 from https://jrsoftware.org/isinfo.php"
if (-not $SkipSign) {
    if (-not $CertThumbprint) {
        Write-Warning "-CertThumbprint not specified — signing will be skipped. Pass -SkipSign to suppress this warning."
        $SkipSign = $true
    } else {
        # signtool is in the Windows SDK; find it in the default install path if not on PATH
        $signtool = Get-Command "signtool.exe" -ErrorAction SilentlyContinue
        if (-not $signtool) {
            $sdkBin = "C:\Program Files (x86)\Windows Kits\10\bin"
            $signtool = Get-ChildItem "$sdkBin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
                        Sort-Object FullName -Descending | Select-Object -First 1
            if ($signtool) {
                $env:PATH += ";$($signtool.DirectoryName)"
                Write-Host "  signtool found: $($signtool.FullName)"
            } else {
                Write-Error "signtool.exe not found. Install the Windows SDK or add it to PATH."
            }
        }
    }
}

Write-Host "  Version    : $Version"
Write-Host "  Repo root  : $RepoRoot"
Write-Host "  Publish dir: $PublishDir"
if (-not $SkipSign) { Write-Host "  Cert       : $CertThumbprint" }

# ── Verify third-party binaries ───────────────────────────────────────────────
Step "Verify bundled tools"
foreach ($bin in @("yt-dlp.exe", "ffmpeg.exe", "ffprobe.exe")) {
    $path = Join-Path $ToolsDir $bin
    if (-not (Test-Path $path)) {
        Write-Error "Missing: $path`n  Download it and place it in DM.App\tools\ before releasing."
    }
    Write-Host "  OK: $bin"
}

# ── Clean previous outputs ────────────────────────────────────────────────────
Step "Clean"
foreach ($dir in @($PublishDir, $ObfDir)) {
    if (Test-Path $dir) {
        Remove-Item $dir -Recurse -Force
        Write-Host "  Removed: $dir"
    }
}

# ── Publish ───────────────────────────────────────────────────────────────────
Step "dotnet publish"
Push-Location $RepoRoot
try {
    dotnet publish $AppProject `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishProfile=win-x64-release `
        -p:Version=$Version `
        -p:AssemblyVersion="$Version.0" `
        -p:FileVersion="$Version.0" `
        --nologo
    if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish failed (exit $LASTEXITCODE)" }
} finally {
    Pop-Location
}
Write-Host "  Published to: $PublishDir"

# ── Obfuscate ─────────────────────────────────────────────────────────────────
if ($SkipObfuscate) {
    Write-Host ""
    Write-Host "  [obfuscation skipped]" -ForegroundColor Yellow
    # Use the non-obfuscated publish dir as the source for signing and installer
    $FinalDir = $PublishDir
} else {
    Step "Obfuscar"

    # Patch InPath/OutPath in the config to absolute paths so obfuscar works
    # regardless of current directory.
    $ObfConfig = Join-Path $RepoRoot "scripts\obfuscar.xml"
    $ObfConfigTmp = Join-Path $env:TEMP "obfuscar_release.xml"
    (Get-Content $ObfConfig -Raw) `
        -replace 'value="publish\\win-x64"',     "value=`"$PublishDir`"" `
        -replace 'value="publish\\win-x64-obf"', "value=`"$ObfDir`"" `
        -replace 'value="publish\\obfuscar.log"', "value=`"$(Join-Path $RepoRoot 'publish\obfuscar.log')`"" |
        Out-File $ObfConfigTmp -Encoding utf8

    obfuscar.console $ObfConfigTmp
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Obfuscar failed. Check publish\obfuscar.log for details."
    }

    # Copy non-obfuscated files (native runtime, tools, licenses) to obf output
    # Obfuscar only writes the assemblies it processed; everything else must be copied.
    Write-Host "  Copying non-obfuscated files..."
    Get-ChildItem $PublishDir -Recurse | Where-Object { -not $_.PSIsContainer } |
        Where-Object { $_.Extension -notin @('.dll', '.exe') -or $_.Name -notmatch '^DM\.' } |
        ForEach-Object {
            $rel  = $_.FullName.Substring($PublishDir.Length).TrimStart('\')
            $dest = Join-Path $ObfDir $rel
            $destDir = Split-Path $dest -Parent
            if (-not (Test-Path $destDir)) { New-Item $destDir -ItemType Directory -Force | Out-Null }
            Copy-Item $_.FullName $dest -Force
        }

    Write-Host "  Obfuscated output: $ObfDir"
    $FinalDir = $ObfDir
}

# ── Sign main executable ──────────────────────────────────────────────────────
if (-not $SkipSign) {
    Step "Sign DM.App.exe"
    $exe = Join-Path $FinalDir "DM.App.exe"
    signtool sign `
        /sha1 $CertThumbprint `
        /fd sha256 `
        /tr $TimestampUrl `
        /td sha256 `
        /d "Sagar Download Manager" `
        $exe
    if ($LASTEXITCODE -ne 0) { Write-Error "signtool failed on $exe" }
    Write-Host "  Signed: $exe"
}

# ── Compile installer ─────────────────────────────────────────────────────────
Step "Inno Setup"
if (-not (Test-Path $InstallerOutDir)) {
    New-Item $InstallerOutDir -ItemType Directory -Force | Out-Null
}
# Pass the source dir and version into the .iss script
iscc `
    "/DMyAppVersion=$Version" `
    "/DMySourceDir=$FinalDir" `
    "/DMyOutputDir=$InstallerOutDir" `
    $InstallerScript
if ($LASTEXITCODE -ne 0) { Write-Error "Inno Setup compilation failed" }

$installerExe = Get-ChildItem $InstallerOutDir -Filter "*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $installerExe) { Write-Error "Installer .exe not found in $InstallerOutDir" }
Write-Host "  Installer: $($installerExe.FullName)"

# ── Sign installer ────────────────────────────────────────────────────────────
if (-not $SkipSign) {
    Step "Sign installer"
    signtool sign `
        /sha1 $CertThumbprint `
        /fd sha256 `
        /tr $TimestampUrl `
        /td sha256 `
        /d "Sagar Download Manager Setup" `
        $installerExe.FullName
    if ($LASTEXITCODE -ne 0) { Write-Error "signtool failed on installer" }
    Write-Host "  Signed: $($installerExe.FullName)"
}

# ── Summary ───────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "══════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host "  Release $Version complete!" -ForegroundColor Green
Write-Host "══════════════════════════════════════════════════════" -ForegroundColor Green
Write-Host "  Installer : $($installerExe.FullName)"
Write-Host "  App files : $FinalDir"
Write-Host ""
Write-Host "  Next steps:"
Write-Host "   1. Run the installer on a clean VM and smoke-test"
Write-Host "   2. Upload installer to your distribution host"
Write-Host "   3. Tag the commit: git tag v$Version && git push --tags"
