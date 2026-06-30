# NOTICES

Third-party open-source components bundled with **Download Manager**.  
Full license texts belong in `DM.App/licenses/` — see each placeholder file for the source URL.

| Component | Version | SPDX License | Homepage | License file |
|-----------|---------|--------------|----------|--------------|
| .NET Runtime | 9.0 | MIT | https://github.com/dotnet/runtime | https://github.com/dotnet/runtime/blob/main/LICENSE.TXT |
| WPF | 9.0 | MIT | https://github.com/dotnet/wpf | https://github.com/dotnet/wpf/blob/main/LICENSE.TXT |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | https://github.com/CommunityToolkit/dotnet | https://github.com/CommunityToolkit/dotnet/blob/main/License.md |
| WPF UI (Wpf.Ui) | 3.0.5 | MIT | https://github.com/lepoco/wpfui | https://github.com/lepoco/wpfui/blob/main/LICENSE |
| yt-dlp | latest | Unlicense | https://github.com/yt-dlp/yt-dlp | https://github.com/yt-dlp/yt-dlp/blob/master/LICENSE |
| ffmpeg | LGPL build | LGPL-2.1-or-later | https://ffmpeg.org | https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html |

---

## ffmpeg — LGPL Compliance Notice

This application bundles the **LGPL build** of ffmpeg (compiled without `--enable-gpl`).

- ffmpeg is invoked as a **separate child process** via `Process.Start("ffmpeg.exe", ...)`.
- It is **not** statically or dynamically linked into this application.
- Users may replace `Tools\ffmpeg.exe` with any LGPL-compatible build of their choosing.
- Source code, as required by LGPL §6, is available at: <https://ffmpeg.org/download.html>
