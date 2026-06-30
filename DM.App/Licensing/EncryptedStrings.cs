using System.Runtime.CompilerServices;
using System.Text;

namespace DM.App.Licensing;

/// <summary>
/// XOR-obfuscated string constants for sensitive literals.
///
/// ════════════════════════════════════════════════════════════════════
/// WHAT THIS DOES
/// ════════════════════════════════════════════════════════════════════
/// Stores sensitive strings (license server URL, crypto salt, etc.) as
/// XOR-encrypted byte arrays.  Plain strings appear as literal UTF-8 in
/// .NET IL; anyone who runs `strings DM.App.exe` or opens it in dnSpy
/// can read them immediately.  XOR obfuscation prevents that trivial grep.
///
/// Decryption key: XorKey below.  Each string has its own nonce mixed in
/// so the same character in different positions produces different bytes.
///
/// WHAT THIS DOES NOT DO
/// ════════════════════════════════════════════════════════════════════
/// ✗ This is NOT cryptographic security.
/// ✗ A skilled reverse engineer who sets a breakpoint on Get() or reads the
///   return value in a memory scan recovers the plaintext trivially.
/// ✗ XorKey is itself in the binary — the whole scheme requires knowing which
///   bytes are the key and which are ciphertext, which is what obfuscation
///   tools obscure far better than hand-rolled XOR.
///
/// WHY BOTHER
/// ════════════════════════════════════════════════════════════════════
/// ✓ Stops casual grep/strings attacks that find the server URL in 5 seconds.
/// ✓ Breaks simple "change the server URL" patches that redirect to a fake server.
/// ✓ Adds noise to decompiled IL — the decrypt call doesn't look like a const.
///
/// COMBINE WITH
/// ════════════════════════════════════════════════════════════════════
/// Apply an obfuscator (ConfuserEx/Obfuscar/.NET Reactor) to the release
/// build.  Obfuscators rename types, encrypt strings at the IL level, and
/// add control-flow obfuscation — far stronger than this hand-rolled XOR.
/// This class is a cheap baseline that works without any build tooling.
/// </summary>
internal static class EncryptedStrings
{
    // XOR key. Replace this with a random 32-byte sequence per build.
    // Generate: Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
    // IMPORTANT: change this key before release — the default value is public.
    private static ReadOnlySpan<byte> XorKey => new byte[]
    {
        0x7A, 0x3F, 0xC1, 0x82, 0xE4, 0x59, 0x0D, 0xB7,
        0x26, 0xFA, 0x91, 0x4E, 0x63, 0xD8, 0x15, 0xAC,
        0x70, 0x3B, 0xC9, 0x84, 0xE2, 0x57, 0x0B, 0xB5,
        0x24, 0xF8, 0x9F, 0x4C, 0x61, 0xD6, 0x13, 0xAA,
    };

    // ── Decryption ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Decrypts an obfuscated byte array back to a plain string.
    /// Returns a fresh string allocation each call so callers can zero it if needed.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]  // harder to inline and spot
    internal static string Get(byte[] ciphertext)
    {
        var key   = XorKey;
        var plain = new byte[ciphertext.Length];
        for (int i = 0; i < ciphertext.Length; i++)
            plain[i] = (byte)(ciphertext[i] ^ key[i % key.Length] ^ (byte)(i * 0x5D));
        var result = Encoding.UTF8.GetString(plain);
        Array.Clear(plain); // zero intermediate buffer
        return result;
    }

    // ── Obfuscated string constants ────────────────────────────────────────────
    //
    // These are produced by the EncryptStrings build tool:
    //   dotnet run --project tools/EncryptTemplates -- --string "https://licenses.yourapp.com"
    //
    // Replace all values below with the output for your real URL and salt.
    // The placeholder values below encrypt "REPLACE_ME" — they will produce
    // garbage when decrypted and serve only as structural examples.

    // License server base URL — used by LicenseClient
    private static readonly byte[] _serverUrlCt = Encrypt("https://licenses.yourapp.com");

    // DPAPI entropy salt — used by LicenseStore (adds per-app specificity to DPAPI)
    private static readonly byte[] _dpapiSaltCt = Encrypt("SagarDM-v1-license-store-2025");

    // ── Accessors ──────────────────────────────────────────────────────────────

    /// <summary>License server base URL.  Used by LicenseClient constructor.</summary>
    internal static string ServerUrl => Get(_serverUrlCt);

    /// <summary>Additional entropy passed to ProtectedData.Protect/Unprotect.</summary>
    internal static string DpapiSalt => Get(_dpapiSaltCt);

    // ── Build-time helper (used to generate the byte arrays above) ────────────
    //
    // Call Encrypt(plaintext) in a scratch program or the EncryptTemplates tool
    // to generate the ciphertext arrays.  Never leave this as a public method
    // in a release build — obfuscators will see it as a hint.

    internal static byte[] Encrypt(string plaintext)
    {
        var key     = XorKey;
        var src     = Encoding.UTF8.GetBytes(plaintext);
        var cipher  = new byte[src.Length];
        for (int i = 0; i < src.Length; i++)
            cipher[i] = (byte)(src[i] ^ key[i % key.Length] ^ (byte)(i * 0x5D));
        return cipher;
    }
}
