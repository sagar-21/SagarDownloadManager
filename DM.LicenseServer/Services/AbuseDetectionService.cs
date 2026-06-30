using DM.LicenseServer.Data;
using DM.LicenseServer.Models;
using Microsoft.EntityFrameworkCore;

namespace DM.LicenseServer.Services;

/// <summary>
/// Server-side intelligence layer.  Detects and flags abuse patterns.
///
/// All detection is ADVISORY except HandleTamperAsync which can return true to
/// trigger auto-blacklisting.  Admin review is the final step for most flags.
///
/// What this catches:
///   ✓ Device-limit bursts (key shared on >N machines)
///   ✓ Rapid re-activation attempts from many IPs (brute-force scanning)
///   ✓ Confirmed assembly modification (hash mismatch after baseline set)
///   ✓ Impossible geo (same device in two countries within a short window)
///   ✓ Known-bad fingerprints (device from a previously blacklisted license)
///
/// What this does NOT catch:
///   ✗ VM cloning (fingerprints are hardware-based and clone easily)
///   ✗ Attackers who never connect after patching
///   ✗ Perfect fingerprint spoofing
/// </summary>
public interface IAbuseDetectionService
{
    Task<string?> CheckActivationAsync(License license, string fingerprint, string ip);
    Task          CheckHeartbeatAsync(Device device, string ip, string? country);
    Task<bool>    HandleTamperAsync(Device device, string reportedHash, bool confirmedMismatch);
}

public sealed class AbuseDetectionService : IAbuseDetectionService
{
    // Auto-blacklist after this many confirmed tamper/hash-mismatch events on one device.
    // Lower = tighter; raise if your update process causes false positives.
    public const int TamperAutoBlacklistThreshold = 3;

    // Rapid-activation threshold: >N activations per license per hour triggers a flag.
    private const int RapidActivationWindow  = 60;   // minutes
    private const int RapidActivationLimit   = 8;    // activations

    // Impossible-geo: a country change within this many minutes is suspicious.
    private const int ImpossibleGeoWindowMin = 90;

    private readonly LicenseDbContext _db;
    private readonly ILogger<AbuseDetectionService> _log;

    public AbuseDetectionService(LicenseDbContext db, ILogger<AbuseDetectionService> log)
    {
        _db  = db;
        _log = log;
    }

    // ── /activate ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a denial reason string if the activation should be blocked; null if clean.
    /// Side-effects: creates AbuseFlag rows for soft signals even when not blocking.
    /// </summary>
    public async Task<string?> CheckActivationAsync(License license, string fingerprint, string ip)
    {
        // 1. Device limit (new device only — re-activation of known fingerprint is allowed)
        bool isKnown = await _db.Devices.AnyAsync(d =>
            d.LicenseId == license.Id && d.HardwareFingerprint == fingerprint);

        if (!isKnown)
        {
            int activeCount = await _db.Devices.CountAsync(d =>
                d.LicenseId == license.Id && d.Status == DeviceStatus.Active);

            if (activeCount >= license.MaxDevices)
            {
                await FlagAsync(license.Id, null, AbuseFlagType.DeviceLimitExceeded,
                    $"Attempted activation on device #{activeCount + 1} " +
                    $"(limit {license.MaxDevices}) from {ip}");
                return $"Device limit reached ({license.MaxDevices} of {license.MaxDevices} slots used).";
            }
        }

        // 2. Rapid activations (brute-force / key-sharing signal — non-blocking)
        var windowStart = DateTime.UtcNow.AddMinutes(-RapidActivationWindow);
        int recentActs  = await _db.ActivationLogs.CountAsync(l =>
            l.LicenseId == license.Id
            && l.Event == ActivationEvent.Activated
            && l.OccurredAt >= windowStart);

        if (recentActs >= RapidActivationLimit)
        {
            bool alreadyFlagged = await _db.AbuseFlags.AnyAsync(f =>
                f.LicenseId == license.Id
                && f.Type == AbuseFlagType.RapidActivations
                && f.CreatedAt >= windowStart);

            if (!alreadyFlagged)
            {
                await FlagAsync(license.Id, null, AbuseFlagType.RapidActivations,
                    $"{recentActs} activations in the last {RapidActivationWindow}min from {ip}");
                _log.LogWarning("Rapid activations on license {Key}: {Count} in {Win}min",
                    license.Key, recentActs, RapidActivationWindow);
            }
        }

        // 3. Known-bad fingerprint (device already tampered on another license)
        bool badFp = await _db.Devices.AnyAsync(d =>
            d.HardwareFingerprint == fingerprint
            && d.IntegrityStatus == IntegrityStatus.Tampered
            && d.LicenseId != license.Id);

        if (badFp)
        {
            await FlagAsync(license.Id, null, AbuseFlagType.KnownBadFingerprint,
                $"Fingerprint {fingerprint[..12]}… is Tampered on another license. IP: {ip}");
            // Non-blocking by default — admin may want to investigate before hard-blocking.
            // To make it blocking, return a non-null string here.
            _log.LogWarning("Known-bad fingerprint {FP} on license {Key}", fingerprint[..12], license.Key);
        }

        return null;
    }

    // ── /validate heartbeat ───────────────────────────────────────────────────

    /// <summary>
    /// Checks for impossible-geo on each heartbeat.
    /// Non-blocking — creates a flag for admin review.
    ///
    /// IMPORTANT: VPNs, corporate proxies, and mobile roaming cause false positives.
    /// Never auto-block on this signal alone.
    /// </summary>
    public async Task CheckHeartbeatAsync(Device device, string ip, string? country)
    {
        if (country is null || device.LastCountry is null) return;
        if (device.LastCountry == country)                  return;

        var elapsed = DateTime.UtcNow - device.LastSeenAt;
        if (elapsed.TotalMinutes >= ImpossibleGeoWindowMin) return; // plausibly travelled

        bool alreadyFlagged = await _db.AbuseFlags.AnyAsync(f =>
            f.DeviceId == device.Id
            && f.Type   == AbuseFlagType.ImpossibleGeo
            && f.CreatedAt >= DateTime.UtcNow.AddHours(-24));

        if (!alreadyFlagged)
        {
            await FlagAsync(device.LicenseId, device.Id, AbuseFlagType.ImpossibleGeo,
                $"Country changed {device.LastCountry}→{country} in {elapsed.TotalMinutes:F0}min. " +
                $"Old IP: {device.IpAddress} → New IP: {ip}");
            _log.LogWarning("Impossible geo: device {DevId} went {From}→{To} in {Min}min",
                device.Id, device.LastCountry, country, (int)elapsed.TotalMinutes);
        }
    }

    // ── /report ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Called when the app reports a tamper event or when the server detects a
    /// hash mismatch during heartbeat.
    ///
    /// Returns true if the license should be auto-blacklisted.
    /// Mutates device fields — caller must SaveChangesAsync after this method.
    /// </summary>
    public async Task<bool> HandleTamperAsync(Device device, string reportedHash, bool confirmedMismatch)
    {
        device.LastReportedHash = reportedHash;

        if (confirmedMismatch)
        {
            device.IntegrityStatus   = IntegrityStatus.Tampered;
            device.TamperReportCount++;
        }
        else
        {
            // Debugger or self-report without a hash mismatch — suspect, not confirmed
            if (device.IntegrityStatus < IntegrityStatus.Suspected)
                device.IntegrityStatus = IntegrityStatus.Suspected;
        }

        var flagType = confirmedMismatch
            ? AbuseFlagType.AssemblyHashMismatch
            : AbuseFlagType.SelfReportedTamper;

        await FlagAsync(device.LicenseId, device.Id, flagType,
            $"Reported hash: {reportedHash[..Math.Min(16, reportedHash.Length)]}… " +
            $"Known-good: {device.KnownGoodHash?[..Math.Min(16, device.KnownGoodHash.Length)]?? "(not set)"}. " +
            $"Count: {device.TamperReportCount}");

        bool shouldBlacklist = confirmedMismatch
            && device.TamperReportCount >= TamperAutoBlacklistThreshold;

        if (shouldBlacklist)
        {
            _log.LogWarning(
                "Auto-blacklisting license {LicId} — device {DevId} has {Count} confirmed tampers",
                device.LicenseId, device.Id, device.TamperReportCount);
        }

        return shouldBlacklist;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task FlagAsync(Guid licenseId, Guid? deviceId, AbuseFlagType type, string details)
    {
        _db.AbuseFlags.Add(new AbuseFlag
        {
            LicenseId = licenseId,
            DeviceId  = deviceId,
            Type      = type,
            Details   = details,
        });
        await _db.SaveChangesAsync();
    }
}
