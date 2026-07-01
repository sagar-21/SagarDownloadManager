using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace DM.App.Licensing;

// Mirrors the server's response DTOs — keep in sync with DM.LicenseServer/DTOs/LicenseDtos.cs
internal sealed record ActivateResponse(bool Ok, string? Token, string? LicenseKey,
    string? Plan, int MaxDevices, string? CustomerName, string? ExpiresAt, string? Error);

internal sealed record ValidateResponse(bool Ok, string? Token, string Action, string? Error);

internal sealed record ReportResponse(bool Acknowledged);
internal sealed record SessionDataResponse(string Token);

/// <summary>
/// Thin HTTP wrapper around the license server API.
/// All methods are best-effort: callers catch network errors and apply
/// the offline grace policy themselves.
/// </summary>
internal sealed class LicenseClient : IDisposable
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    internal LicenseClient(string serverUrl)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"),
            Timeout     = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.Add("User-Agent", "DM.App/1.0");
    }

    internal async Task<ActivateResponse?> ActivateAsync(
        string key, string fingerprint, string machineName, string os,
        CancellationToken ct = default)
    {
        var body = new { licenseKey = key, fingerprint, machineName, operatingSystem = os };
        using var resp = await _http.PostAsJsonAsync("api/v1/activate", body, JsonOpts, ct);

        // For error responses (4xx), the server returns {"error": "...", "detail": "..."}
        // which doesn't map to ActivateResponse. Parse both shapes and return a unified record.
        if (!resp.IsSuccessStatusCode)
        {
            try
            {
                using var doc = await JsonDocument.ParseAsync(
                    await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                var root    = doc.RootElement;
                var detail  = root.TryGetProperty("detail", out var d) ? d.GetString() : null;
                var errCode = root.TryGetProperty("error",  out var e) ? e.GetString() : null;
                var msg     = detail ?? errCode switch
                {
                    "not_found"     => "License key not found.",
                    "expired"       => "This license has expired.",
                    "device_limit"  => "Device limit reached for this license.",
                    "revoked"       => "This license has been revoked.",
                    "suspended"     => "This license is currently suspended.",
                    "blacklisted"   => "This installation has been blocked.",
                    _               => "Activation failed. Please contact support.",
                };
                return new ActivateResponse(false, null, null, null, 0, null, null, msg);
            }
            catch { return null; }
        }

        return await resp.Content.ReadFromJsonAsync<ActivateResponse>(JsonOpts, ct);
    }

    internal async Task<ValidateResponse?> ValidateAsync(
        string key, string fingerprint,
        CancellationToken ct = default)
    {
        var body = new { licenseKey = key, fingerprint };
        using var resp = await _http.PostAsJsonAsync("api/v1/validate", body, JsonOpts, ct);
        // Accept 200 (normal) and 403 (disable action) — both carry a ValidateResponse body.
        // Anything else (5xx, network error, etc.) is treated as offline.
        if (resp.IsSuccessStatusCode ||
            resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return await resp.Content.ReadFromJsonAsync<ValidateResponse>(JsonOpts, ct);
        }
        return null;
    }

    internal async Task DeactivateAsync(string key, string fingerprint,
        CancellationToken ct = default)
    {
        var body = new { licenseKey = key, fingerprint };
        // Fire-and-forget: don't throw if the server is unreachable
        try { await _http.PostAsJsonAsync("api/v1/deactivate", body, JsonOpts, ct); }
        catch { }
    }

    /// <summary>
    /// Reports an integrity event (tamper, debugger, hash mismatch) to the server.
    /// The server compares the hash against the stored baseline and may blacklist.
    ///
    /// Always fire-and-forget on failure — if the server is unreachable the report
    /// will be retried on the next heartbeat cycle via the pending-report queue.
    /// </summary>
    internal async Task<bool> ReportAsync(
        string key, string fingerprint, string assemblyHash,
        string reportType, string? details,
        CancellationToken ct = default)
    {
        var body = new { licenseKey = key, fingerprint, assemblyHash, reportType, details };
        try
        {
            using var resp = await _http.PostAsJsonAsync("api/v1/report", body, JsonOpts, ct);
            if (!resp.IsSuccessStatusCode) return false;
            var r = await resp.Content.ReadFromJsonAsync<ReportResponse>(JsonOpts, ct);
            return r?.Acknowledged ?? false;
        }
        catch { return false; } // network error — caller will retry
    }

    /// <summary>
    /// Fetches a short-lived session data JWT.
    ///
    /// Returns null on any failure (network error, unauthorized, server down).
    /// The caller (LicenseService) decides whether to degrade or retry.
    ///
    /// The returned token contains the AES download key (dk) bound to this
    /// machine's fingerprint and signed by the server's private key.
    /// </summary>
    internal async Task<SessionDataResponse?> FetchSessionDataAsync(
        string key, string fingerprint,
        CancellationToken ct = default)
    {
        var body = new { licenseKey = key, fingerprint };
        try
        {
            using var resp = await _http.PostAsJsonAsync("api/v1/session-data", body, JsonOpts, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<SessionDataResponse>(JsonOpts, ct);
        }
        catch { return null; }
    }

    public void Dispose() => _http.Dispose();
}
