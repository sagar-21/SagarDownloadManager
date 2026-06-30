using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DM.App.Licensing;

/// <summary>Claims extracted from a validated license JWT.</summary>
internal sealed record LicenseInfo(
    string    LicenseKey,
    string    Plan,
    int       MaxDevices,
    string    Fingerprint,
    string    CustomerName,
    DateTime  TokenExpiry,
    DateTime  TokenIssuedAt,
    DateTime? LicenseExpiry
);

/// <summary>
/// Verifies RS256 JWT signatures using only the embedded RSA PUBLIC key.
///
/// Verification is done manually (split on '.', verify signature with RSA,
/// parse payload with System.Text.Json) so we don't need the full
/// Microsoft.IdentityModel packages in the desktop app.
///
/// Security model:
///   • The PRIVATE key never leaves the license server — it is physically
///     impossible to forge a token without it.
///   • This class can verify but never create tokens.
///   • Removing or replacing this class defeats the local check, but the
///     server heartbeat will still disable the app at the next tick.
/// </summary>
internal static class LicenseTokenValidator
{
    // ── Public key ─────────────────────────────────────────────────────────────
    //
    // IMPORTANT: Replace this placeholder with the output of:
    //   dotnet run --project DM.LicenseServer/tools/GenerateKeys
    //
    // The key printed to stdout (keys/signing.pub) goes here verbatim.
    // The private key (keys/signing.key) must NEVER appear in client code.
    //
    private const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIICIjANBgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEAxPq+kdIb/Rj2NX0MNefD
        y3/BTejNbuY7vVQhrOBDCqGUeDPgpBgcuKpQpeCoFMnG85ZqH4IlzQdGS7vHTymI
        TULK7H97sUeCouatmPm7OPvRqKDEeVkSjh1ae1lUb2Yz1O4wxZNZaargPy0F4qNH
        PHfFqkhTxmABpM/4jnRK5SyY8Rl0mFTb4rKPyxt3tiihWBGEUqpRQ96ADkOdjDW5
        jPIy/t4LF2CFRgDn9goFfiwEbaW9nmKWqMTBIOq84LfWyhpqVKT8rG06ltDNE0oB
        3xtIF29njt8xh/NX6VYRwG3x86Yz8UiCrlzjUhbVzLnPKh7Qh3wdGGptL85EpT0d
        5xrTwynff3tyjUH8QyNrG206/qssnDrTolEC1ZMDLsih+26bHEsMY8FyA7l/TACc
        PZ3XTMBG1Ok9txIbrspBuxrjUFc/8IWlNmX4q1Hnb0xMtWPqvprDqEO1mqh83l1F
        BtOhVUzu+SFRBwhs5Hr58nfTj4hvW3eqSuw/LFrLXp8JlBdUgfZIlfi/1k3Neg1t
        /3R9R/nhaHBK1tWrwnTE6+l2VITg6hKlQ4Jo2C6f/LJqNwybPXSFovyWMFjtF6ec
        WmywZKmejzycAhavOuFRXJ6QXdO1zFxHgjmaSDwWhTh6ddo8laTAaRg25QV1SdhY
        mi39lRN8vlJUD/NVgdKMbukCAwEAAQ==
        -----END PUBLIC KEY-----
        """;

    // Null when the placeholder above hasn't been replaced yet.
    private static readonly RSA? _publicKey = TryLoadPublicKey();

    private static RSA? TryLoadPublicKey()
    {
        try
        {
            var rsa = RSA.Create();
            rsa.ImportFromPem(PublicKeyPem);
            return rsa;
        }
        catch
        {
            // Key not yet configured — all validation calls will return null,
            // so the app shows the activation screen but activation won't work
            // until a real key is embedded.
            return null;
        }
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies a raw RS256 signature without parsing the JWT.
    /// Used by SessionDataCache which handles its own claim parsing.
    /// </summary>
    internal static bool VerifyRawSignature(byte[] signingInput, byte[] signature)
    {
        if (_publicKey is null) return false;
        return _publicKey.VerifyData(signingInput, signature,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    /// <summary>
    /// Validates signature, expiry, and fingerprint.
    /// Returns null on ANY failure — treat as invalid/tampered.
    /// Does NOT make network calls.
    /// </summary>
    internal static LicenseInfo? Validate(string jwt, string currentFingerprint)
    {
        if (_publicKey is null) return null;
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3) return null;

            // RS256: signature covers ASCII bytes of "header.payload"
            var signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            var signature    = Base64UrlDecode(parts[2]);

            if (!_publicKey.VerifyData(signingInput, signature,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return null;

            var payload = ParsePayload(parts[1]);
            if (payload is null) return null;

            // 5-minute clock-skew tolerance
            if (payload.TokenExpiry < DateTime.UtcNow.AddMinutes(-5)) return null;

            // Token must be bound to THIS machine
            if (!string.Equals(payload.Fingerprint, currentFingerprint,
                StringComparison.OrdinalIgnoreCase))
                return null;

            return payload;
        }
        catch { return null; }
    }

    /// <summary>
    /// Reads the expiry claim WITHOUT verifying the signature.
    /// Used only to determine offline grace period — never used to grant access.
    /// </summary>
    internal static DateTime? PeekExpiry(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3) return null;
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));
            var exp = doc.RootElement.GetProperty("exp").GetInt64();
            return DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime;
        }
        catch { return null; }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static LicenseInfo? ParsePayload(string payloadB64)
    {
        var json = Encoding.UTF8.GetString(Base64UrlDecode(payloadB64));
        using var doc  = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var exp = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("exp").GetInt64()).UtcDateTime;
        var iat = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("iat").GetInt64()).UtcDateTime;
        var fp  = root.GetProperty("fp").GetString() ?? "";

        DateTime? licExp = null;
        if (root.TryGetProperty("lic_exp", out var licExpEl) && licExpEl.ValueKind != JsonValueKind.Null)
            licExp = DateTime.Parse(licExpEl.GetString()!, null,
                System.Globalization.DateTimeStyles.RoundtripKind);

        // max_dev may be a JSON string ("5") or number (5) depending on JWT library version
        var maxDevEl = root.GetProperty("max_dev");
        var maxDevices = maxDevEl.ValueKind == JsonValueKind.Number
            ? maxDevEl.GetInt32()
            : int.Parse(maxDevEl.GetString()!);

        return new LicenseInfo(
            LicenseKey   : root.GetProperty("lic_key").GetString()!,
            Plan         : root.GetProperty("plan").GetString()!,
            MaxDevices   : maxDevices,
            Fingerprint  : fp,
            CustomerName : root.TryGetProperty("cus_name", out var cn) ? cn.GetString() ?? "" : "",
            TokenExpiry  : exp,
            TokenIssuedAt: iat,
            LicenseExpiry: licExp
        );
    }

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        int pad = s.Length % 4;
        if (pad == 2) s += "==";
        else if (pad == 3) s += "=";
        return Convert.FromBase64String(s);
    }
}
