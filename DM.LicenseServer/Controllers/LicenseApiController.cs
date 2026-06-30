using DM.LicenseServer.DTOs;
using DM.LicenseServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DM.LicenseServer.Controllers;

/// <summary>
/// App-facing endpoints. Called by the desktop app, not by humans.
/// All responses are JSON. All routes are under /api/v1.
///
/// Rate limits (per IP):
///   POST /activate   — 10 req/min  (prevents brute-force key scanning)
///   POST /validate   — 120 req/min (heartbeats; one every 30s × N apps is still OK)
///   POST /deactivate — 10 req/min
/// </summary>
[ApiController]
[Route("api/v1")]
[Produces("application/json")]
public class LicenseApiController : ControllerBase
{
    private readonly ILicenseService _svc;
    private readonly ITokenService   _tokens;

    public LicenseApiController(ILicenseService svc, ITokenService tokens)
    {
        _svc    = svc;
        _tokens = tokens;
    }

    // ── POST /api/v1/activate ─────────────────────────────────────────────────

    [HttpPost("activate")]
    [EnableRateLimiting("activate")]
    public async Task<IActionResult> Activate([FromBody] ActivateRequest req)
    {
        if (!ModelState.IsValid)
            return BadRequest(new ErrorResponse("validation_error", ModelStateErrors()));

        var ip     = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result = await _svc.ActivateAsync(req, ip);

        if (!result.Ok)
            return result.ErrorCode switch
            {
                "not_found"     => NotFound(new ErrorResponse(result.ErrorCode!, result.ErrorMessage)),
                "device_limit"  => Conflict(new ErrorResponse(result.ErrorCode!, result.ErrorMessage)),
                _               => StatusCode(403, new ErrorResponse(result.ErrorCode!, result.ErrorMessage)),
            };

        var l = result.License!;
        return Ok(new ActivateResponse(
            Ok:           true,
            Token:        result.Token!,
            LicenseKey:   l.Key,
            Plan:         l.Plan,
            MaxDevices:   l.MaxDevices,
            CustomerName: l.Customer?.Name ?? "",
            ExpiresAt:    l.ExpiresAt?.ToString("O"),
            Error:        null));
    }

    // ── POST /api/v1/validate ─────────────────────────────────────────────────

    [HttpPost("validate")]
    [EnableRateLimiting("validate")]
    public async Task<IActionResult> Validate([FromBody] ValidateRequest req)
    {
        if (!ModelState.IsValid)
            return BadRequest(new ErrorResponse("validation_error", ModelStateErrors()));

        var ip     = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result = await _svc.ValidateAsync(req, ip);

        if (!result.Ok)
            return Ok(new ValidateResponse(false, null, result.Action, result.ErrorMessage));

        return Ok(new ValidateResponse(true, result.Token, "continue", null));
    }

    // ── POST /api/v1/deactivate ───────────────────────────────────────────────

    [HttpPost("deactivate")]
    [EnableRateLimiting("activate")]
    public async Task<IActionResult> Deactivate([FromBody] DeactivateRequest req)
    {
        if (!ModelState.IsValid)
            return BadRequest(new ErrorResponse("validation_error", ModelStateErrors()));

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var (ok, error) = await _svc.DeactivateAsync(req, ip);

        return ok
            ? Ok(new DeactivateResponse(true, null))
            : NotFound(new DeactivateResponse(false, error));
    }

    // ── POST /api/v1/report ───────────────────────────────────────────────────
    //
    // Called when the app's anti-tamper checks detect something suspicious.
    // The server compares the reported assembly hash against the stored baseline.
    //
    // DESIGN: Always returns 200 Acknowledged=true, even for unknown keys.
    // Revealing whether a key exists would help attackers enumerate valid keys.
    //
    // FLOW:
    //   1. App detects debugger or assembly hash mismatch → queues report.
    //   2. On next heartbeat window, calls /report with the suspicious hash.
    //   3. Server compares against KnownGoodHash:
    //      a) Match   → false positive (e.g. legitimate update) — clears suspicion.
    //      b) Mismatch → confirmed tamper → increments TamperReportCount.
    //   4. If TamperReportCount ≥ threshold → auto-blacklist.
    //   5. On next heartbeat, app gets "disable" / "blacklisted" response and locks.

    [HttpPost("report")]
    [EnableRateLimiting("report")]
    public async Task<IActionResult> Report([FromBody] ReportRequest req)
    {
        if (!ModelState.IsValid)
            return BadRequest(new ErrorResponse("validation_error", ModelStateErrors()));

        var ip     = GetClientIp();
        var result = await _svc.ReportAsync(req, ip);
        return Ok(result);
    }

    // ── POST /api/v1/session-data ─────────────────────────────────────────────
    //
    // Issues a short-lived JWT containing the AES download key.
    // The client's download engine requires this key to decrypt its embedded
    // format templates — so cracking the license check alone doesn't enable downloads.
    //
    // KEYGEN IMPOSSIBILITY EXPLAINED:
    //   The session token is signed with the same RSA-4096 private key as the license
    //   token.  That key exists ONLY on this server.  Even if an attacker extracts
    //   the embedded public key from the client binary, they cannot:
    //     • forge a session token (need the private key to sign)
    //     • derive the private key from the public key (RSA 4096 = infeasible)
    //   Therefore, cracking the client's license-check code is insufficient —
    //   they still need a server response to obtain the download key.
    //
    // SERVER-DEPENDENT CORE EXPLAINED:
    //   The encrypted format templates in DM.App are AES-256-CBC ciphertext.
    //   The decryption key (dk) comes exclusively from this endpoint.
    //   No dk → no decryption → no downloads.
    //   This means patching "if (isLicensed) return" still leaves the download
    //   engine broken.  The attacker must ALSO hook or fake the session response,
    //   which is significantly more work than a single conditional patch.

    [HttpPost("session-data")]
    [EnableRateLimiting("session")]
    public async Task<IActionResult> SessionData([FromBody] SessionDataRequest req)
    {
        if (!ModelState.IsValid)
            return BadRequest(new ErrorResponse("validation_error", ModelStateErrors()));

        var result = await _svc.GetSessionDataAsync(req);
        if (result is null) return Unauthorized(); // no detail — stealth
        return Ok(result);
    }

    // ── GET /api/v1/public-key ────────────────────────────────────────────────

    /// <summary>
    /// Returns the RSA public key in PEM format.
    /// App developers embed this at compile time; this endpoint is for convenience.
    /// </summary>
    [HttpGet("public-key")]
    public IActionResult GetPublicKey()
        => Content(_tokens.GetPublicKeyPem(), "application/x-pem-file");

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string GetClientIp()
    {
        // Respect X-Forwarded-For when behind nginx/Caddy reverse proxy
        if (HttpContext.Request.Headers.TryGetValue("X-Forwarded-For", out var fwd))
            return fwd.ToString().Split(',')[0].Trim();
        return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private string ModelStateErrors() =>
        string.Join("; ", ModelState.Values
            .SelectMany(v => v.Errors)
            .Select(e => e.ErrorMessage));
}
