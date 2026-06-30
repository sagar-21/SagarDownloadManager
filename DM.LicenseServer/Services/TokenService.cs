using System.Security.Claims;
using System.Security.Cryptography;
using DM.LicenseServer.Models;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

namespace DM.LicenseServer.Services;

/// <summary>
/// Signs and verifies license tokens using RSA-4096 + JWT (RS256).
///
/// Why RSA asymmetric signing?
///   The app only needs to verify tokens, never create them.
///   Embed only the PUBLIC key (signing.pub) in your app.
///   Keep the PRIVATE key (signing.key) exclusively on this server.
///   Even if an attacker extracts the embedded public key, they cannot
///   forge tokens without the private key.
///
/// Token format: standard JWT with custom claims.
///   lic_key  — the license key string
///   plan     — plan/tier name
///   max_dev  — max device count
///   fp       — bound hardware fingerprint (SHA-256 of hardware ID)
///   cus_name — customer display name
///   lic_exp  — license expiry (ISO-8601, or absent if perpetual)
///   sub      — license key (standard JWT subject)
///   exp      — token expiry (standard, = now + TokenExpiryHours)
///   jti      — unique token ID (standard)
/// </summary>
public interface ITokenService
{
    string     GenerateLicenseToken(License license, Device device);
    string     GenerateSessionDataToken(License license, string fingerprint);
    string     GetPublicKeyPem();
    DateTime   GetTokenExpiry();
}

public sealed class TokenService : ITokenService
{
    private readonly RSA              _rsa;
    private readonly RsaSecurityKey   _securityKey;
    private readonly JwtSecurityTokenHandler _handler = new();
    private readonly string           _publicKeyPem;
    private readonly int              _expiryHours;

    public TokenService(IConfiguration config, ILogger<TokenService> logger)
    {
        _config = config;
        var privateKeyPath = config["Signing:PrivateKeyPath"]
            ?? throw new InvalidOperationException("Signing:PrivateKeyPath not configured.");

        if (!File.Exists(privateKeyPath))
            throw new FileNotFoundException(
                $"RSA private key not found at '{privateKeyPath}'. " +
                "Run: dotnet run --project tools/GenerateKeys to generate keys, " +
                "then update Signing:PrivateKeyPath in appsettings.json.", privateKeyPath);

        _rsa = RSA.Create();
        _rsa.ImportFromPem(File.ReadAllText(privateKeyPath));
        _securityKey = new RsaSecurityKey(_rsa);

        var publicKeyPath = config["Signing:PublicKeyPath"]
            ?? Path.ChangeExtension(privateKeyPath, ".pub");
        _publicKeyPem = File.Exists(publicKeyPath)
            ? File.ReadAllText(publicKeyPath)
            : _rsa.ExportSubjectPublicKeyInfoPem();

        _expiryHours = config.GetValue<int?>("Signing:TokenExpiryHours") ?? 48;

        logger.LogInformation("TokenService initialised. RSA key size: {Size} bits, token expiry: {Hours}h.",
            _rsa.KeySize, _expiryHours);
    }

    public DateTime GetTokenExpiry() => DateTime.UtcNow.AddHours(_expiryHours);

    public string GenerateLicenseToken(License license, Device device)
    {
        var expiry = GetTokenExpiry();

        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub,  license.Key),
            new Claim(JwtRegisteredClaimNames.Jti,  Guid.NewGuid().ToString("N")),
            new Claim("lic_key",  license.Key),
            new Claim("plan",     license.Plan),
            new Claim("max_dev",  license.MaxDevices.ToString(), ClaimValueTypes.Integer32),
            new Claim("fp",       device.HardwareFingerprint),
            new Claim("cus_name", license.Customer?.Name ?? ""),
        };

        if (license.ExpiresAt.HasValue)
            claims.Add(new Claim("lic_exp",
                license.ExpiresAt.Value.ToString("O"))); // ISO 8601 round-trip

        var descriptor = new SecurityTokenDescriptor
        {
            Subject            = new ClaimsIdentity(claims),
            NotBefore          = DateTime.UtcNow.AddSeconds(-30), // 30s clock-skew tolerance
            Expires            = expiry,
            SigningCredentials = new SigningCredentials(
                _securityKey, SecurityAlgorithms.RsaSha256),
        };

        return _handler.CreateEncodedJwt(descriptor);
    }

    public string GetPublicKeyPem() => _publicKeyPem;

    // ── Session data token ─────────────────────────────────────────────────────
    //
    // The session data JWT is short-lived (90 min) and contains:
    //   dk  — AES-256 download key (32-byte, base64); derived from a server master
    //         secret + fingerprint + current UTC day.  Changes daily per device.
    //   mq  — max download quality for this plan ("720", "1080", "4320")
    //   mc  — max concurrent downloads
    //   be  — batch mode enabled
    //   fp  — hardware fingerprint (client verifies this matches its own)
    //
    // Why this makes cracking harder:
    //   A "patch the if" cracker still has no download key.  The encrypted
    //   format templates in the client binary cannot be decrypted without dk.
    //   The cracker must also hook the decryption call AND fake a server
    //   response — significantly more effort than a single conditional patch.
    //
    // Honest limitation:
    //   A sophisticated attacker who runs the app legitimately once can capture
    //   dk from memory, then patch the SessionDataCache to replay it.  The
    //   server-side heartbeat is the final backstop — a revoked license gets
    //   no fresh session tokens after the current one expires (90 min).

    private const int SessionTokenMinutes = 90;
    private const string MasterSecretKey  = "Signing:SessionMasterSecret";

    public string GenerateSessionDataToken(License license, string fingerprint)
    {
        var masterSecret = _config[MasterSecretKey]
            ?? throw new InvalidOperationException($"{MasterSecretKey} not configured.");

        // Derive dk: HKDF(masterSecret, info = fingerprint|plan|dayEpoch)
        // Changes every UTC day per device, so captured tokens are stale within 24h.
        var dayEpoch  = (long)(DateTime.UtcNow.Date - DateTime.UnixEpoch).TotalDays;
        var info      = System.Text.Encoding.UTF8.GetBytes(
                            $"{fingerprint}|{license.Plan}|{dayEpoch}");
        var secretBytes = System.Text.Encoding.UTF8.GetBytes(masterSecret);
        var dk          = System.Security.Cryptography.HKDF.DeriveKey(
                            System.Security.Cryptography.HashAlgorithmName.SHA256,
                            secretBytes, outputLength: 32, info: info);

        var (maxQuality, maxConcurrent, batchEnabled) = license.Plan.ToLowerInvariant() switch
        {
            "starter" or "basic" => ("1080",  2, false),
            "pro"                => ("4320",  5, true),
            "enterprise"         => ("4320", 20, true),
            _                    => ("720",   1, false),
        };

        var now = DateTime.UtcNow;
        var claims = new List<System.Security.Claims.Claim>
        {
            new(JwtRegisteredClaimNames.Sub,  license.Key),
            new(JwtRegisteredClaimNames.Jti,  Guid.NewGuid().ToString("N")),
            new("fp",  fingerprint),
            new("dk",  Convert.ToBase64String(dk)),
            new("mq",  maxQuality),
            new("mc",  maxConcurrent.ToString()),
            new("be",  batchEnabled ? "1" : "0"),
        };

        var descriptor = new SecurityTokenDescriptor
        {
            Subject            = new System.Security.Claims.ClaimsIdentity(claims),
            NotBefore          = now.AddSeconds(-30),
            Expires            = now.AddMinutes(SessionTokenMinutes),
            SigningCredentials = new SigningCredentials(_securityKey, SecurityAlgorithms.RsaSha256),
        };

        return _handler.CreateEncodedJwt(descriptor);
    }

    // Back-reference to config so GenerateSessionDataToken can read the master secret
    private readonly IConfiguration _config;
}
