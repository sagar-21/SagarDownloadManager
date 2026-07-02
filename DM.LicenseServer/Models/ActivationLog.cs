namespace DM.LicenseServer.Models;

public sealed class ActivationLog
{
    public Guid            Id         { get; set; } = Guid.NewGuid();
    public Guid            LicenseId  { get; set; }
    public Guid?           DeviceId   { get; set; }
    public ActivationEvent Event      { get; set; }
    public string          IpAddress  { get; set; } = "";
    public string          Notes      { get; set; } = "";
    public DateTime        OccurredAt { get; set; } = DateTime.UtcNow;

    public License  License { get; set; } = null!;
    public Device?  Device  { get; set; }
}

public enum ActivationEvent
{
    Activated,
    Deactivated,
    HeartbeatOk,
    HeartbeatFailed,
    Revoked,
    Suspended,
    Reactivated,
    Blacklisted,        // auto-blacklisted by abuse detection
    TamperReported,     // app reported its own hash; mismatch logged here
    AbuseFlagged,       // geo-impossibility, device-limit burst, etc.
    SessionDataServed,  // /session-data issued a token (audit trail)
    Extended,           // admin extended the license expiry date
}
