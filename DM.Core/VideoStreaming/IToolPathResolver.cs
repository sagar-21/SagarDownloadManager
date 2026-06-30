namespace DM.Core.VideoStreaming;

public interface IToolPathResolver
{
    string ResolveYtDlp();
    string ResolveFfmpeg();
}

public sealed class DefaultToolPathResolver : IToolPathResolver
{
    private readonly string _toolsDir;

    public DefaultToolPathResolver(YtDlpSettings settings)
    {
        // Append a separator so StartsWith("C:\App\Tools\") never matches
        // the sibling directory "C:\App\ToolsSibling\".
        _toolsDir = Path.GetFullPath(settings.ToolsDirectory)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
    }

    public string ResolveYtDlp()  => Resolve(OperatingSystem.IsWindows() ? "yt-dlp.exe"  : "yt-dlp");
    public string ResolveFfmpeg() => Resolve(OperatingSystem.IsWindows() ? "ffmpeg.exe"   : "ffmpeg");

    private string Resolve(string fileName)
    {
        var full = Path.GetFullPath(Path.Combine(_toolsDir, fileName));

        // Guard against path-traversal: resolved path must stay inside tools dir.
        if (!full.StartsWith(_toolsDir, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Resolved tool path '{full}' escapes the tools directory.");

        if (!File.Exists(full))
            throw new VideoDownloadException(
                VideoDownloadFailureReason.ProcessNotFound,
                $"Required tool not found: '{full}'. " +
                "Place yt-dlp and ffmpeg in the application's Tools folder.");

        return full;
    }
}
