using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DM.App.Licensing;

/// <summary>Token and metadata kept between sessions.</summary>
internal sealed record StoredLicense(
    string    LicenseKey,
    string    Token,         // signed JWT from server
    string    Fingerprint,   // hardware fingerprint at activation time
    string?   AssemblyHash,  // SHA-256 of DM.App.exe at activation time (anti-tamper baseline)
    DateTime  StoredAt,
    DateTime? ActivatedAt = null  // set once on first activation; null for legacy stored licenses
);

/// <summary>
/// Persists the license token using Windows DPAPI (Data Protection API).
///
/// DataProtectionScope.CurrentUser means:
///   • Encrypted with a key derived from the Windows login credentials.
///   • Only decryptable by the same Windows user on the same machine.
///   • Survives reboots; destroyed if the Windows user profile is deleted.
///   • Does NOT protect against the user running another process as themselves
///     (they could decrypt their own data) — but that's fine, since the token
///     is signed by the server and bound to this machine's fingerprint.
///
/// The file is just ciphertext to any other user account or process.
/// </summary>
internal sealed class LicenseStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SagarDM", "lic.dat");

    internal StoredLicense? Load()
    {
        if (!File.Exists(StorePath)) return null;
        try
        {
            var ciphertext = File.ReadAllBytes(StorePath);
            var plaintext  = ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredLicense>(Encoding.UTF8.GetString(plaintext));
        }
        catch { return null; } // corrupted or decryption failed (different user/machine)
    }

    internal void Save(StoredLicense data)
    {
        var plaintext  = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));
        var ciphertext = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllBytes(StorePath, ciphertext);
    }

    internal void Delete()
    {
        try { File.Delete(StorePath); } catch { }
    }
}
