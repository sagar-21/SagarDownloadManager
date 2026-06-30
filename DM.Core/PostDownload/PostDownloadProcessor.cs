using System.Security.Cryptography;

namespace DM.Core.PostDownload;

public sealed record PostDownloadResult(
    string  FinalPath,
    string? ChecksumSummary,
    bool    ChecksumOk);

public static class PostDownloadProcessor
{
    public static async Task<PostDownloadResult> ProcessAsync(
        string filePath,
        CategorizationSettings categorization,
        string? expectedChecksum,
        CancellationToken ct = default)
    {
        string finalPath = filePath;

        if (categorization.Enabled && File.Exists(filePath))
        {
            string? moved = TryCategorize(filePath, categorization);
            if (moved is not null) finalPath = moved;
        }

        string? checksumSummary = null;
        bool    checksumOk      = true;

        if (!string.IsNullOrEmpty(expectedChecksum) && File.Exists(finalPath))
        {
            var r = await ComputeChecksumAsync(finalPath, expectedChecksum, ct);
            checksumOk      = r.Matches;
            checksumSummary = r.Matches
                ? $"{r.Algorithm}: {r.Computed} ✓"
                : $"{r.Algorithm}: {r.Computed} ✗ (expected: {r.Expected})";
        }

        return new PostDownloadResult(finalPath, checksumSummary, checksumOk);
    }

    private static string? TryCategorize(string filePath, CategorizationSettings settings)
    {
        var ext  = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        var rule = settings.Rules.FirstOrDefault(r => r.Enabled && r.Extensions.Contains(ext));
        if (rule is null) return null;

        var dir       = Path.GetDirectoryName(filePath) ?? ".";
        var targetDir = Path.IsPathRooted(rule.TargetFolder)
            ? rule.TargetFolder
            : Path.Combine(dir, rule.TargetFolder);

        Directory.CreateDirectory(targetDir);

        var dest = UniqueFilePath(Path.Combine(targetDir, Path.GetFileName(filePath)));
        if (string.Equals(dest, filePath, StringComparison.OrdinalIgnoreCase)) return null;

        File.Move(filePath, dest);
        return dest;
    }

    private static string UniqueFilePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir  = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext  = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            var c = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(c)) return c;
        }
    }

    private static async Task<(bool Matches, string Algorithm, string Computed, string Expected)>
        ComputeChecksumAsync(string filePath, string expected, CancellationToken ct)
    {
        string algo, expectedHash;

        if (expected.StartsWith("md5:", StringComparison.OrdinalIgnoreCase))
            (algo, expectedHash) = ("MD5", expected[4..]);
        else if (expected.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            (algo, expectedHash) = ("SHA256", expected[7..]);
        else if (expected.Length == 32)
            (algo, expectedHash) = ("MD5", expected);
        else
            (algo, expectedHash) = ("SHA256", expected);

        await using var stream = File.OpenRead(filePath);
        byte[] hash = algo == "MD5"
            ? await MD5.HashDataAsync(stream, ct)
            : await SHA256.HashDataAsync(stream, ct);

        var computed = Convert.ToHexString(hash).ToLowerInvariant();
        return (computed == expectedHash.ToLowerInvariant(), algo, computed, expectedHash.ToLowerInvariant());
    }
}
