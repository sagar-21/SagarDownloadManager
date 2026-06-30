namespace DM.LicenseServer.Models;

/// <summary>
/// Records a single abuse-detection signal.  Flags are created automatically by
/// AbuseDetectionService and reviewed by admins on the License Detail page.
///
/// Flags are informational — they do NOT automatically change license status
/// (except when TamperReportCount reaches the auto-blacklist threshold).
/// All automatic status changes are also logged to ActivationLog.
/// </summary>
public sealed class AbuseFlag
{
    public Guid          Id         { get; set; } = Guid.NewGuid();
    public Guid          LicenseId  { get; set; }
    public Guid?         DeviceId   { get; set; }  // null = license-level flag
    public AbuseFlagType Type       { get; set; }
    public string        Details    { get; set; } = "";
    public bool          Reviewed   { get; set; }  // admin has seen + dismissed
    public DateTime      CreatedAt  { get; set; } = DateTime.UtcNow;

    public License License { get; set; } = null!;
    public Device? Device  { get; set; }
}

public enum AbuseFlagType
{
    // Same fingerprint appears on two geographically separate IPs within a short window.
    // NOTE: VPNs, corporate proxies, and IPv6 tunnels cause false positives.
    // Treat as a soft signal for admin review, NOT as an automatic block.
    ImpossibleGeo,

    // Activation attempted beyond the license's MaxDevices limit.
    DeviceLimitExceeded,

    // App reported its own assembly hash, and it doesn't match the baseline set at
    // first activation.  May indicate a patched binary or a legitimate update.
    AssemblyHashMismatch,

    // App explicitly called /report with type="tamper" or "debugger".
    // Indicates the app's own anti-tamper checks fired.
    SelfReportedTamper,

    // Too many activation attempts from the same IP in a short window.
    // Suggests brute-force key scanning or automated key-sharing.
    RapidActivations,

    // The hardware fingerprint has been seen on a different license that was
    // previously blacklisted.  May indicate fingerprint cloning/spoofing.
    KnownBadFingerprint,
}
