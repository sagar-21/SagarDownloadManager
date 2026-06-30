namespace DM.LicenseServer.Models;

public sealed class Device
{
    public Guid         Id                  { get; set; } = Guid.NewGuid();
    public Guid         LicenseId           { get; set; }
    public string       HardwareFingerprint { get; set; } = "";
    public string       MachineName         { get; set; } = "";
    public string       OperatingSystem     { get; set; } = "";
    public string       IpAddress           { get; set; } = "";
    public string?      LastCountry         { get; set; }   // ISO-3166 α2; null until geo lookup works
    public DateTime     FirstSeenAt         { get; set; } = DateTime.UtcNow;
    public DateTime     LastSeenAt          { get; set; } = DateTime.UtcNow;
    public DeviceStatus Status              { get; set; } = DeviceStatus.Active;

    // ── Integrity tracking ────────────────────────────────────────────────────
    // KnownGoodHash: set once at first activation from the app's self-reported hash.
    //   Reset by the admin when issuing a legitimate update.
    // LastReportedHash: what the app reported on its most recent heartbeat or /report call.
    // IntegrityStatus: set to Tampered when KnownGoodHash ≠ LastReportedHash.
    // TamperReportCount: auto-incremented on each confirmed tamper; triggers auto-blacklist
    //   when it reaches AbuseDetectionService.TamperThreshold.
    public IntegrityStatus IntegrityStatus     { get; set; } = IntegrityStatus.Unknown;
    public string?         KnownGoodHash       { get; set; }
    public string?         LastReportedHash    { get; set; }
    public int             TamperReportCount   { get; set; }

    public License             License  { get; set; } = null!;
    public List<ActivationLog> Logs     { get; set; } = [];
    public List<AbuseFlag>     AbuseFlags { get; set; } = [];
}

public enum DeviceStatus    { Active, Deactivated }
public enum IntegrityStatus { Unknown, Clean, Suspected, Tampered }
