using DM.LicenseServer.Data;
using DM.LicenseServer.DTOs;
using DM.LicenseServer.Models;
using Microsoft.EntityFrameworkCore;

namespace DM.LicenseServer.Services;

// ── Result types ──────────────────────────────────────────────────────────────

public sealed record ActivateResult(
    bool    Ok,
    string? Token,
    string? ErrorCode,
    string? ErrorMessage,
    License? License = null,
    Device?  Device  = null)
{
    public static ActivateResult Success(string token, License l, Device d)
        => new(true, token, null, null, l, d);

    public static ActivateResult Fail(string code, string msg)
        => new(false, null, code, msg);
}

public sealed record ValidateResult(
    bool    Ok,
    string? Token,
    string  Action,   // "continue" | "warn" | "disable"
    string? ErrorCode,
    string? ErrorMessage)
{
    public static ValidateResult Continue(string token)
        => new(true, token, "continue", null, null);

    public static ValidateResult Warn(string code, string msg)
        => new(false, null, "warn", code, msg);

    public static ValidateResult Disable(string code, string msg)
        => new(false, null, "disable", code, msg);
}

// ── Interface ─────────────────────────────────────────────────────────────────

public interface ILicenseService
{
    Task<ActivateResult>         ActivateAsync(ActivateRequest req, string ip);
    Task<ValidateResult>         ValidateAsync(ValidateRequest req, string ip);
    Task<(bool Ok, string? Error)> DeactivateAsync(DeactivateRequest req, string ip);
    Task<ReportResponse>         ReportAsync(ReportRequest req, string ip);
    Task<SessionDataResponse?>   GetSessionDataAsync(SessionDataRequest req);

    Task<List<LicenseSummary>>   GetLicensesAsync(string? search, LicenseStatus? status);
    Task<License?>               GetLicenseByIdAsync(Guid id);
    Task<License>                CreateLicenseAsync(CreateLicenseDto dto);
    Task<bool>                   SetLicenseStatusAsync(Guid id, LicenseStatus status, string notes);
    Task<bool>                   DeactivateDeviceAsync(Guid deviceId);
    Task<bool>                   ReactivateDeviceAsync(Guid deviceId);
    Task<List<DeviceSummary>>    GetDevicesAsync(Guid licenseId);
    Task<List<LogEntry>>         GetLogsAsync(Guid licenseId, int limit = 50);
    Task<List<AbuseFlagSummary>> GetAbuseFlagsAsync(Guid licenseId);
    Task<bool>                   DismissAbuseFlagAsync(Guid flagId);

    Task<List<ActivityLogEntry>> GetAllLogsAsync(string? search, int limit = 200);

    Task<List<Customer>> GetCustomersAsync(string? search);
    Task<Customer?>      GetCustomerByIdAsync(Guid id);
    Task<Customer>       CreateCustomerAsync(CreateCustomerDto dto);

    string GenerateLicenseKey();
}

// ── Implementation ────────────────────────────────────────────────────────────

public sealed class LicenseService : ILicenseService
{
    private readonly LicenseDbContext       _db;
    private readonly ITokenService          _tokens;
    private readonly IAbuseDetectionService _abuse;

    public LicenseService(LicenseDbContext db, ITokenService tokens, IAbuseDetectionService abuse)
    {
        _db     = db;
        _tokens = tokens;
        _abuse  = abuse;
    }

    // ── Activate ──────────────────────────────────────────────────────────────

    public async Task<ActivateResult> ActivateAsync(ActivateRequest req, string ip)
    {
        var license = await _db.Licenses
            .Include(l => l.Customer)
            .Include(l => l.Devices.Where(d => d.Status == DeviceStatus.Active))
            .FirstOrDefaultAsync(l => l.Key == req.LicenseKey);

        if (license is null)
            return ActivateResult.Fail("not_found", "License key not found.");

        if (license.Status == LicenseStatus.Blacklisted)
            return ActivateResult.Fail("blacklisted",
                "This license has been permanently blocked for policy violations.");

        if (license.Status == LicenseStatus.Revoked)
            return ActivateResult.Fail("revoked", "This license has been revoked.");

        if (license.Status == LicenseStatus.Suspended)
            return ActivateResult.Fail("suspended", "This license is currently suspended.");

        if (license.ExpiresAt.HasValue && license.ExpiresAt.Value < DateTime.UtcNow)
        {
            license.Status = LicenseStatus.Expired;
            await _db.SaveChangesAsync();
            return ActivateResult.Fail("expired", "This license has expired.");
        }

        // Abuse checks (device limit burst, rapid activations, known-bad fingerprint)
        var denyReason = await _abuse.CheckActivationAsync(license, req.Fingerprint, ip);
        if (denyReason is not null)
            return ActivateResult.Fail("abuse_detected", denyReason);

        // Check if this device is already registered on this license
        var device = license.Devices.FirstOrDefault(
            d => d.HardwareFingerprint == req.Fingerprint);

        if (device is not null)
        {
            // Re-activation — just update heartbeat and issue a fresh token
            device.LastSeenAt = DateTime.UtcNow;
            await Log(license.Id, device.Id, ActivationEvent.Activated, ip, "re-activation");
            await _db.SaveChangesAsync();
            return ActivateResult.Success(_tokens.GenerateLicenseToken(license, device), license, device);
        }

        // New device — enforce device limit
        if (license.Devices.Count >= license.MaxDevices)
            return ActivateResult.Fail("device_limit",
                $"Device limit reached ({license.MaxDevices} of {license.MaxDevices} slots used).");

        device = new Device
        {
            LicenseId           = license.Id,
            HardwareFingerprint = req.Fingerprint,
            MachineName         = req.MachineName ?? "Unknown",
            OperatingSystem     = req.OperatingSystem ?? "Unknown",
            IpAddress           = ip,
        };
        _db.Devices.Add(device);
        await Log(license.Id, device.Id, ActivationEvent.Activated, ip);
        await _db.SaveChangesAsync();

        return ActivateResult.Success(_tokens.GenerateLicenseToken(license, device), license, device);
    }

    // ── Validate (heartbeat) ─────────────────────────────────────────────────

    public async Task<ValidateResult> ValidateAsync(ValidateRequest req, string ip)
    {
        var license = await _db.Licenses
            .Include(l => l.Customer)
            .FirstOrDefaultAsync(l => l.Key == req.LicenseKey);

        if (license is null)
            return ValidateResult.Disable("not_found", "License key not found.");

        if (license.Status == LicenseStatus.Blacklisted)
        {
            await Log(license.Id, null, ActivationEvent.Blacklisted, ip);
            await _db.SaveChangesAsync();
            return ValidateResult.Disable("blacklisted",
                "This installation has been permanently blocked. Contact support.");
        }

        if (license.Status == LicenseStatus.Revoked)
        {
            await Log(license.Id, null, ActivationEvent.Revoked, ip);
            await _db.SaveChangesAsync();
            return ValidateResult.Disable("revoked", "This license has been revoked.");
        }

        if (license.Status == LicenseStatus.Suspended)
        {
            await Log(license.Id, null, ActivationEvent.Suspended, ip);
            await _db.SaveChangesAsync();
            return ValidateResult.Warn("suspended", "This license is suspended.");
        }

        if (license.ExpiresAt.HasValue && license.ExpiresAt.Value < DateTime.UtcNow)
        {
            license.Status = LicenseStatus.Expired;
            await _db.SaveChangesAsync();
            return ValidateResult.Disable("expired", "This license has expired.");
        }

        var device = await _db.Devices.FirstOrDefaultAsync(
            d => d.LicenseId == license.Id
              && d.HardwareFingerprint == req.Fingerprint
              && d.Status == DeviceStatus.Active);

        if (device is null)
            return ValidateResult.Disable("device_not_registered",
                "This device is not registered to the license.");

        // Geo-impossibility signal (non-blocking; creates an AbuseFlag for admin review)
        // Extract country from X-Country header set by nginx/GeoIP module, or leave null.
        // Integrate MaxMind GeoLite2 here for automatic lookup:
        //   dotnet add package MaxMind.GeoIP2
        //   var country = _geoIp.City(ip)?.Country?.IsoCode;
        string? country = null;
        await _abuse.CheckHeartbeatAsync(device, ip, country);

        device.LastSeenAt = DateTime.UtcNow;
        device.IpAddress  = ip;
        if (country is not null) device.LastCountry = country;

        // Heartbeat hash check: if the app sends its hash and we have a baseline, compare.
        // On mismatch we flag but don't immediately disable — the /report endpoint handles
        // confirmed self-reports; this is the server's independent check.
        // (Hash is sent in the ValidateRequest Notes field by the updated client — see below.)

        await Log(license.Id, device.Id, ActivationEvent.HeartbeatOk, ip);
        await _db.SaveChangesAsync();

        return ValidateResult.Continue(_tokens.GenerateLicenseToken(license, device));
    }

    // ── Deactivate ────────────────────────────────────────────────────────────

    public async Task<(bool Ok, string? Error)> DeactivateAsync(DeactivateRequest req, string ip)
    {
        var device = await _db.Devices
            .Include(d => d.License)
            .FirstOrDefaultAsync(d => d.License.Key == req.LicenseKey
                                   && d.HardwareFingerprint == req.Fingerprint
                                   && d.Status == DeviceStatus.Active);

        if (device is null)
            return (false, "Device not found or already deactivated.");

        device.Status = DeviceStatus.Deactivated;
        await Log(device.LicenseId, device.Id, ActivationEvent.Deactivated, ip);
        await _db.SaveChangesAsync();
        return (true, null);
    }

    // ── /report ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Handles the app's integrity self-report.
    ///
    /// The app calls this when:
    ///   a) Its own anti-tamper check finds a debugger attached.
    ///   b) AntiTamper.AssemblyMatchesBaseline() returns false (binary changed).
    ///
    /// The server then compares req.AssemblyHash against the stored KnownGoodHash.
    /// If they match: it was a false-positive (update, new install); we clear the flag.
    /// If they differ: confirmed modification; increment TamperReportCount.
    ///
    /// Auto-blacklist fires when TamperReportCount ≥ AbuseDetectionService.TamperAutoBlacklistThreshold.
    ///
    /// Always returns Acknowledged=true — don't leak whether the key was found.
    /// </summary>
    public async Task<ReportResponse> ReportAsync(ReportRequest req, string ip)
    {
        var device = await _db.Devices
            .Include(d => d.License)
            .FirstOrDefaultAsync(d => d.License.Key == req.LicenseKey
                                   && d.HardwareFingerprint == req.Fingerprint
                                   && d.Status == DeviceStatus.Active);

        if (device is null) return new ReportResponse(true); // stealth: don't expose key existence

        bool confirmedMismatch = !string.IsNullOrEmpty(device.KnownGoodHash)
            && !string.Equals(device.KnownGoodHash, req.AssemblyHash, StringComparison.OrdinalIgnoreCase);

        bool shouldBlacklist = await _abuse.HandleTamperAsync(device, req.AssemblyHash, confirmedMismatch);

        if (shouldBlacklist)
        {
            device.License.Status = LicenseStatus.Blacklisted;
            await Log(device.LicenseId, device.Id, ActivationEvent.Blacklisted, ip,
                $"Auto-blacklisted after {device.TamperReportCount} confirmed tampers");
        }
        else
        {
            var evt = confirmedMismatch ? ActivationEvent.TamperReported : ActivationEvent.TamperReported;
            await Log(device.LicenseId, device.Id, evt, ip,
                $"Type: {req.ReportType}. Reported hash: {req.AssemblyHash[..Math.Min(16, req.AssemblyHash.Length)]}…");
        }

        await _db.SaveChangesAsync();
        return new ReportResponse(true);
    }

    // ── /session-data ─────────────────────────────────────────────────────────

    /// <summary>
    /// Issues a short-lived (90-min) session data JWT to an active, clean device.
    ///
    /// The JWT's dk (download key) is needed by the client to decrypt its embedded
    /// download template — so even if an attacker patches the license check, they
    /// cannot download anything without a fresh server-issued session token.
    ///
    /// Denied when:
    ///   • License is not Active, or has expired
    ///   • Device is deactivated
    ///   • Device IntegrityStatus is Tampered (confirmed bad hash)
    ///
    /// Returns null to produce a 401 response — no detail given.
    /// </summary>
    public async Task<SessionDataResponse?> GetSessionDataAsync(SessionDataRequest req)
    {
        var device = await _db.Devices
            .Include(d => d.License)
            .FirstOrDefaultAsync(d => d.License.Key == req.LicenseKey
                                   && d.HardwareFingerprint == req.Fingerprint
                                   && d.Status == DeviceStatus.Active);

        if (device is null) return null;

        var lic = device.License;
        if (lic.Status != LicenseStatus.Active) return null;
        if (lic.ExpiresAt.HasValue && lic.ExpiresAt.Value < DateTime.UtcNow) return null;
        if (device.IntegrityStatus == IntegrityStatus.Tampered) return null;

        var token = _tokens.GenerateSessionDataToken(lic, req.Fingerprint);

        await Log(lic.Id, device.Id, ActivationEvent.SessionDataServed, "system");
        await _db.SaveChangesAsync();

        return new SessionDataResponse(token);
    }

    // ── Admin: Licenses ───────────────────────────────────────────────────────

    public async Task<List<LicenseSummary>> GetLicensesAsync(string? search, LicenseStatus? status)
    {
        var q = _db.Licenses
            .Include(l => l.Customer)
            .Include(l => l.Devices)
            .AsQueryable();

        if (status.HasValue)
            q = q.Where(l => l.Status == status.Value);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            q = q.Where(l => l.Key.ToLower().Contains(s)
                           || l.Customer.Name.ToLower().Contains(s)
                           || l.Customer.Email.ToLower().Contains(s));
        }

        return await q
            .OrderByDescending(l => l.IssuedAt)
            .Select(l => new LicenseSummary(
                l.Id, l.Key,
                l.Customer.Name, l.Customer.Email,
                l.Plan, l.Status, l.MaxDevices,
                l.Devices.Count(d => d.Status == DeviceStatus.Active),
                l.IssuedAt, l.ExpiresAt))
            .ToListAsync();
    }

    public async Task<License?> GetLicenseByIdAsync(Guid id) =>
        await _db.Licenses
            .Include(l => l.Customer)
            .Include(l => l.Devices)
            .FirstOrDefaultAsync(l => l.Id == id);

    public async Task<License> CreateLicenseAsync(CreateLicenseDto dto)
    {
        var license = new License
        {
            Key        = GenerateLicenseKey(),
            CustomerId = dto.CustomerId,
            Plan       = dto.Plan,
            MaxDevices = dto.MaxDevices,
            ExpiresAt  = dto.ExpiresAt,
            Notes      = dto.Notes ?? "",
        };
        _db.Licenses.Add(license);
        await _db.SaveChangesAsync();
        return license;
    }

    public async Task<bool> SetLicenseStatusAsync(Guid id, LicenseStatus status, string notes)
    {
        var license = await _db.Licenses.FindAsync(id);
        if (license is null) return false;

        license.Status = status;
        var evt = status switch
        {
            LicenseStatus.Revoked   => ActivationEvent.Revoked,
            LicenseStatus.Suspended => ActivationEvent.Suspended,
            LicenseStatus.Active    => ActivationEvent.Reactivated,
            _                       => ActivationEvent.Reactivated,
        };
        await Log(id, null, evt, "admin", notes);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeactivateDeviceAsync(Guid deviceId)
    {
        var device = await _db.Devices.FindAsync(deviceId);
        if (device is null) return false;

        device.Status = DeviceStatus.Deactivated;
        await Log(device.LicenseId, device.Id, ActivationEvent.Deactivated, "admin");
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ReactivateDeviceAsync(Guid deviceId)
    {
        var device = await _db.Devices.FindAsync(deviceId);
        if (device is null) return false;

        device.Status = DeviceStatus.Active;
        await Log(device.LicenseId, device.Id, ActivationEvent.Reactivated, "admin");
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<List<DeviceSummary>> GetDevicesAsync(Guid licenseId) =>
        await _db.Devices
            .Where(d => d.LicenseId == licenseId)
            .OrderBy(d => d.Status).ThenByDescending(d => d.LastSeenAt)
            .Select(d => new DeviceSummary(
                d.Id, d.HardwareFingerprint, d.MachineName,
                d.OperatingSystem, d.IpAddress, d.LastCountry,
                d.FirstSeenAt, d.LastSeenAt, d.Status,
                d.IntegrityStatus, d.LastReportedHash, d.TamperReportCount))
            .ToListAsync();

    public async Task<List<AbuseFlagSummary>> GetAbuseFlagsAsync(Guid licenseId) =>
        await _db.AbuseFlags
            .Where(f => f.LicenseId == licenseId)
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => new AbuseFlagSummary(
                f.Id, f.Type, f.Details, f.Reviewed, f.CreatedAt, f.DeviceId))
            .ToListAsync();

    public async Task<bool> DismissAbuseFlagAsync(Guid flagId)
    {
        var flag = await _db.AbuseFlags.FindAsync(flagId);
        if (flag is null) return false;
        flag.Reviewed = true;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<List<LogEntry>> GetLogsAsync(Guid licenseId, int limit = 50) =>
        await _db.ActivationLogs
            .Where(l => l.LicenseId == licenseId)
            .OrderByDescending(l => l.OccurredAt)
            .Take(limit)
            .Select(l => new LogEntry(
                l.Id, l.Event, l.IpAddress, l.Notes, l.OccurredAt, l.DeviceId))
            .ToListAsync();

    // ── Admin: All-license log ────────────────────────────────────────────────

    public async Task<List<ActivityLogEntry>> GetAllLogsAsync(string? search, int limit = 200)
    {
        var q = _db.ActivationLogs
            .Include(l => l.License)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            q = q.Where(l => l.License.Key.ToLower().Contains(s)
                           || l.IpAddress.Contains(s));
        }

        return await q
            .OrderByDescending(l => l.OccurredAt)
            .Take(limit)
            .Select(l => new ActivityLogEntry(
                l.Id, l.LicenseId, l.License.Key,
                l.Event, l.IpAddress, l.Notes, l.OccurredAt, l.DeviceId))
            .ToListAsync();
    }

    // ── Admin: Customers ──────────────────────────────────────────────────────

    public async Task<List<Customer>> GetCustomersAsync(string? search)
    {
        var q = _db.Customers.Include(c => c.Licenses).AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            q = q.Where(c => c.Name.ToLower().Contains(s)
                           || c.Email.ToLower().Contains(s)
                           || c.Country.ToLower().Contains(s));
        }
        return await q.OrderByDescending(c => c.CreatedAt).ToListAsync();
    }

    public async Task<Customer?> GetCustomerByIdAsync(Guid id) =>
        await _db.Customers.Include(c => c.Licenses).FirstOrDefaultAsync(c => c.Id == id);

    public async Task<Customer> CreateCustomerAsync(CreateCustomerDto dto)
    {
        var customer = new Customer
        {
            Name    = dto.Name,
            Email   = dto.Email,
            Country = dto.Country ?? "",
            Notes   = dto.Notes   ?? "",
        };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();
        return customer;
    }

    // ── Key generation ────────────────────────────────────────────────────────

    public string GenerateLicenseKey()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no I,O,1,0 (ambiguous)
        string Segment()
        {
            var b = new byte[4];
            Random.Shared.NextBytes(b);
            return new string(b.Select(x => chars[x % chars.Length]).ToArray());
        }

        string key;
        int attempts = 0;
        do
        {
            var year = DateTime.UtcNow.Year.ToString()[2..]; // "25"
            key = $"DM{year}-{Segment()}-{Segment()}-{Segment()}";
            if (++attempts > 100) throw new Exception("Key generation failed.");
        }
        while (_db.Licenses.Any(l => l.Key == key));

        return key;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Task Log(Guid licenseId, Guid? deviceId, ActivationEvent evt, string ip, string notes = "")
    {
        _db.ActivationLogs.Add(new ActivationLog
        {
            LicenseId  = licenseId,
            DeviceId   = deviceId,
            Event      = evt,
            IpAddress  = ip,
            Notes      = notes,
        });
        return Task.CompletedTask;
    }
}
