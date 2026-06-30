using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DM.App.Licensing;

/// <summary>
/// Stores the short-lived session data JWT returned by POST /api/v1/session-data.
/// Provides the download engine with its AES-256 key (dk) and feature flags.
///
/// ════════════════════════════════════════════════════════════════════
/// WHY THIS MAKES CRACKING HARDER (server-dependent core design)
/// ════════════════════════════════════════════════════════════════════
/// The download engine's format templates are stored AES-256-CBC encrypted
/// in EncryptedTemplates.cs.  The decryption key (dk) comes ONLY from a
/// valid /session-data server response — it is NEVER stored on disk, only
/// in the process's memory while the session is live.
///
/// Flow:
///   1. Server issues signed JWT containing dk (a 32-byte AES key).
///   2. Client verifies JWT signature with embedded public key.
///   3. dk is stored here in memory only.
///   4. DownloadEngine calls GetDownloadKey() to get dk.
///   5. dk decrypts the format templates from EncryptedTemplates.
///   6. Without valid dk → decryption fails → downloads return an error.
///
/// What this prevents:
///   ✓ "Patch out the license check" — even with IsLicensed always true, dk
///     is null until a server response arrives, so downloads still fail.
///   ✓ Offline use after session expiry (90 minutes) — dk is cleared from memory.
///
/// What this does NOT prevent:
///   ✗ Attacker who runs the app once on a valid license, captures dk from
///     process memory (e.g. with a memory scanner), then patches the cache to
///     return that captured value.
///   ✗ Attacker who replaces this class entirely with a stub that returns a
///     hardcoded key.  This requires additional reverse-engineering effort.
///   ✗ Debugging the running process to extract dk directly.
///
/// Real protection = server heartbeat.  A revoked license gets no fresh session
/// tokens; after 90 minutes the dk is gone.  For a determined attacker, the
/// server revocation response is the definitive lock.
/// ════════════════════════════════════════════════════════════════════
/// </summary>
internal sealed class SessionDataCache
{
    private SessionPayload? _current;
    private readonly Lock   _lock = new();

    // ── Write ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses, verifies, and stores the session JWT.
    /// Returns false if the JWT is invalid or expired — caller should treat
    /// as a non-fatal error and retry on the next session-data refresh.
    /// </summary>
    internal bool Store(string jwt, string currentFingerprint)
    {
        var payload = ParseAndVerify(jwt, currentFingerprint);
        if (payload is null) return false;
        lock (_lock) { _current = payload; }
        return true;
    }

    /// <summary>Clears the session — called on license revoke/suspend/logout.</summary>
    internal void Clear()
    {
        lock (_lock)
        {
            if (_current is null) return;
            // Zero the key bytes before releasing the reference
            Array.Clear(_current.DownloadKeyBytes);
            _current = null;
        }
    }

    // ── Read ───────────────────────────────────────────────────────────────────

    internal bool HasValidSession
    {
        get
        {
            lock (_lock)
                return _current is not null && _current.ExpiresAt > DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Returns a COPY of the AES-256 download key bytes.
    /// Returns null if there is no valid session.
    ///
    /// Callers MUST zero the returned array after use:
    ///   Array.Clear(key);
    /// </summary>
    internal byte[]? GetDownloadKey()
    {
        lock (_lock)
        {
            if (_current is null || _current.ExpiresAt <= DateTimeOffset.UtcNow)
                return null;
            // Return a copy so the caller can zero it independently
            return (byte[])_current.DownloadKeyBytes.Clone();
        }
    }

    internal int    MaxConcurrentDownloads { get { lock (_lock) return _current?.MaxConcurrent ?? 1; } }
    internal string MaxQuality             { get { lock (_lock) return _current?.MaxQuality ?? "720"; } }
    internal bool   BatchEnabled           { get { lock (_lock) return _current?.BatchEnabled ?? false; } }

    // ── Parsing + verification ─────────────────────────────────────────────────

    private static SessionPayload? ParseAndVerify(string jwt, string currentFingerprint)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3) return null;

            // Verify RS256 signature with embedded public key (same key as license tokens)
            var sigInput  = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
            var signature = Base64UrlDecode(parts[2]);
            if (!LicenseTokenValidator.VerifyRawSignature(sigInput, signature)) return null;

            var payloadBytes = Base64UrlDecode(parts[1]);
            using var doc = JsonDocument.Parse(payloadBytes);
            var root = doc.RootElement;

            var exp = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("exp").GetInt64());
            if (exp <= DateTimeOffset.UtcNow) return null;

            // Token must be bound to THIS machine (fp claim = our fingerprint)
            var fp = root.GetProperty("fp").GetString() ?? "";
            if (!string.Equals(fp, currentFingerprint, StringComparison.OrdinalIgnoreCase))
                return null;

            var dkB64   = root.GetProperty("dk").GetString() ?? "";
            var dkBytes = Convert.FromBase64String(dkB64);
            if (dkBytes.Length != 32) return null; // must be AES-256 (32 bytes)

            return new SessionPayload(
                DownloadKeyBytes: dkBytes,
                MaxQuality:       root.GetProperty("mq").GetString()!,
                MaxConcurrent:    int.Parse(root.GetProperty("mc").GetString()!),
                BatchEnabled:     root.GetProperty("be").GetString() == "1",
                ExpiresAt:        exp
            );
        }
        catch { return null; }
    }

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return (s.Length % 4) switch
        {
            2 => Convert.FromBase64String(s + "=="),
            3 => Convert.FromBase64String(s + "="),
            _ => Convert.FromBase64String(s),
        };
    }

    // ── Payload record ─────────────────────────────────────────────────────────

    private sealed class SessionPayload
    {
        internal readonly byte[]          DownloadKeyBytes;
        internal readonly string          MaxQuality;
        internal readonly int             MaxConcurrent;
        internal readonly bool            BatchEnabled;
        internal readonly DateTimeOffset  ExpiresAt;

        internal SessionPayload(byte[] DownloadKeyBytes, string MaxQuality,
            int MaxConcurrent, bool BatchEnabled, DateTimeOffset ExpiresAt)
        {
            this.DownloadKeyBytes = DownloadKeyBytes;
            this.MaxQuality       = MaxQuality;
            this.MaxConcurrent    = MaxConcurrent;
            this.BatchEnabled     = BatchEnabled;
            this.ExpiresAt        = ExpiresAt;
        }
    }
}
