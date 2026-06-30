using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;
using Microsoft.AspNetCore.Mvc;

namespace DM.LicenseServer.Controllers;

/// <summary>
/// First-run endpoint to set the admin password.
///
/// This endpoint ONLY works while Admin:PasswordHash == "CHANGE_ME" in appsettings.json.
/// Once the password is set it becomes permanently unavailable — the guard
/// checks the live config value, not a one-time flag.
///
/// Usage (first run):
///   POST https://your-server/admin/setup
///   { "password": "your-secure-password" }
///
/// This writes the hash and salt back to appsettings.json in-place.
/// On Docker / prod with read-only config, set the hash manually instead
/// (run this locally against a dev instance, then copy the hash into your env).
/// </summary>
[ApiController]
[Route("admin/setup")]
public class AdminSetupController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<AdminSetupController> _logger;

    public AdminSetupController(IConfiguration cfg, IWebHostEnvironment env,
        ILogger<AdminSetupController> logger)
    {
        _config  = cfg;
        _env     = env;
        _logger  = logger;
    }

    [HttpPost]
    public IActionResult SetPassword([FromBody] SetPasswordRequest req)
    {
        if (_config["Admin:PasswordHash"] != "CHANGE_ME")
            return NotFound(); // acts as if endpoint doesn't exist once password is set

        if (string.IsNullOrWhiteSpace(req.Password) || req.Password.Length < 12)
            return BadRequest(new { error = "Password must be at least 12 characters." });

        var (hash, salt) = HashPassword(req.Password);

        // Patch appsettings.json — replace FIRST occurrence (hash), then SECOND (salt)
        var settingsPath = Path.Combine(_env.ContentRootPath, "appsettings.json");
        if (System.IO.File.Exists(settingsPath))
        {
            const string placeholder = "\"CHANGE_ME\"";
            var json = System.IO.File.ReadAllText(settingsPath);

            var hashIdx = json.IndexOf(placeholder, StringComparison.Ordinal);
            if (hashIdx >= 0)
                json = json[..hashIdx] + $"\"{hash}\"" + json[(hashIdx + placeholder.Length)..];

            var saltIdx = json.IndexOf(placeholder, StringComparison.Ordinal);
            if (saltIdx >= 0)
                json = json[..saltIdx] + $"\"{salt}\"" + json[(saltIdx + placeholder.Length)..];

            System.IO.File.WriteAllText(settingsPath, json);
        }

        _logger.LogWarning("Admin password set via setup endpoint.");
        return Ok(new { ok = true, message = "Admin password configured. The /admin/setup endpoint is now disabled." });
    }

    public static (string Hash, string Salt) HashPassword(string password)
    {
        var saltBytes = new byte[32];
        Random.Shared.NextBytes(saltBytes);
        var salt = Convert.ToBase64String(saltBytes);
        var hash = ComputeHash(password, salt);
        return (hash, salt);
    }

    public static bool VerifyPassword(string password, string storedHash, string storedSalt)
        => CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(ComputeHash(password, storedSalt)),
            System.Text.Encoding.UTF8.GetBytes(storedHash));

    private static string ComputeHash(string password, string saltBase64)
    {
        var salt = Convert.FromBase64String(saltBase64);
        return Convert.ToBase64String(KeyDerivation.Pbkdf2(
            password:       password,
            salt:           salt,
            prf:            KeyDerivationPrf.HMACSHA256,
            iterationCount: 200_000,
            numBytesRequested: 32));
    }

    public sealed record SetPasswordRequest([Required] string Password);
}
