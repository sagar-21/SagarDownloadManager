// GenerateKeys — run once on the server to produce the RSA key pair.
//
// Usage:
//   dotnet run --project tools/GenerateKeys
//
// Outputs:
//   keys/signing.key   — private key (PEM, PKCS#8). KEEP ON SERVER ONLY.
//   keys/signing.pub   — public key  (PEM, SPKI).   Embed in the desktop app.

using System.Security.Cryptography;

string keysDir = Path.Combine(
    Directory.GetCurrentDirectory(), "keys");

Directory.CreateDirectory(keysDir);

string privPath = Path.Combine(keysDir, "signing.key");
string pubPath  = Path.Combine(keysDir, "signing.pub");

if (File.Exists(privPath) && args.FirstOrDefault() != "--force")
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("signing.key already exists. Pass --force to overwrite.");
    Console.ResetColor();
    return 1;
}

using var rsa = RSA.Create(4096);

string privatePem = rsa.ExportPkcs8PrivateKeyPem();
string publicPem  = rsa.ExportSubjectPublicKeyInfoPem();

File.WriteAllText(privPath, privatePem);
File.WriteAllText(pubPath,  publicPem);

// Set restrictive permissions on the private key (Unix only)
if (!OperatingSystem.IsWindows())
{
    System.Diagnostics.Process.Start("chmod", $"600 \"{privPath}\"")?.WaitForExit();
}

Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine($"✓ Private key written to: {privPath}");
Console.WriteLine($"✓ Public  key written to: {pubPath}");
Console.ResetColor();
Console.WriteLine();
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("Public key (embed in DM.App):");
Console.ResetColor();
Console.WriteLine(publicPem);
Console.ForegroundColor = ConsoleColor.Red;
Console.WriteLine("IMPORTANT: Never commit signing.key or copy it off the server.");
Console.ResetColor();

return 0;
