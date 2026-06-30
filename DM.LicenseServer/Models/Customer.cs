namespace DM.LicenseServer.Models;

public sealed class Customer
{
    public Guid     Id        { get; set; } = Guid.NewGuid();
    public string   Name      { get; set; } = "";
    public string   Email     { get; set; } = "";
    public string   Country   { get; set; } = "";
    public string   Notes     { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<License> Licenses { get; set; } = [];
}
