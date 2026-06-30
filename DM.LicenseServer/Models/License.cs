namespace DM.LicenseServer.Models;

public sealed class License
{
    public Guid          Id         { get; set; } = Guid.NewGuid();
    public string        Key        { get; set; } = "";   // e.g. DM24-A1F3-9K2X-8BQE
    public Guid          CustomerId { get; set; }
    public string        Plan       { get; set; } = "Basic";
    public LicenseStatus Status     { get; set; } = LicenseStatus.Active;
    public int           MaxDevices { get; set; } = 1;
    public DateTime      IssuedAt   { get; set; } = DateTime.UtcNow;
    public DateTime?     ExpiresAt  { get; set; }
    public string        Notes      { get; set; } = "";

    public Customer             Customer   { get; set; } = null!;
    public List<Device>         Devices    { get; set; } = [];
    public List<ActivationLog>  Logs       { get; set; } = [];
    public List<AbuseFlag>      AbuseFlags { get; set; } = [];
}

// Blacklisted = permanently blocked due to confirmed piracy/tamper; cannot be reactivated.
// Distinguished from Revoked so the app can show a different (less reversible) message.
public enum LicenseStatus { Active, Suspended, Revoked, Expired, Blacklisted }
