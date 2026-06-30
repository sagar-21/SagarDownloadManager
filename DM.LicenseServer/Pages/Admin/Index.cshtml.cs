using DM.LicenseServer.Data;
using DM.LicenseServer.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace DM.LicenseServer.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class IndexModel : PageModel
{
    private readonly LicenseDbContext _db;
    public IndexModel(LicenseDbContext db) => _db = db;

    public int TotalCustomers  { get; set; }
    public int TotalLicenses   { get; set; }
    public int ActiveLicenses  { get; set; }
    public int ActiveDevices   { get; set; }
    public int ExpiringSoon    { get; set; }

    public List<RecentActivation> RecentActivations { get; set; } = [];
    public List<ExpiringSoonItem> ExpiringSoonList  { get; set; } = [];

    public async Task OnGetAsync()
    {
        var now    = DateTime.UtcNow;
        var in30   = now.AddDays(30);

        TotalCustomers = await _db.Customers.CountAsync();
        TotalLicenses  = await _db.Licenses.CountAsync();
        ActiveLicenses = await _db.Licenses.CountAsync(l => l.Status == LicenseStatus.Active);
        ActiveDevices  = await _db.Devices.CountAsync(d => d.Status == DeviceStatus.Active);
        ExpiringSoon   = await _db.Licenses.CountAsync(l =>
            l.Status == LicenseStatus.Active && l.ExpiresAt.HasValue
            && l.ExpiresAt.Value >= now && l.ExpiresAt.Value <= in30);

        RecentActivations = await _db.ActivationLogs
            .Where(l => l.Event == ActivationEvent.Activated)
            .OrderByDescending(l => l.OccurredAt)
            .Take(8)
            .Join(_db.Licenses.Include(lic => lic.Customer),
                  log => log.LicenseId,
                  lic => lic.Id,
                  (log, lic) => new { log, lic })
            .Select(x => new RecentActivation(
                x.lic.Key,
                x.lic.Customer.Name,
                _db.Devices
                    .Where(d => d.Id == x.log.DeviceId)
                    .Select(d => d.MachineName)
                    .FirstOrDefault() ?? "—",
                x.log.OccurredAt))
            .ToListAsync();

        ExpiringSoonList = await _db.Licenses
            .Include(l => l.Customer)
            .Where(l => l.Status == LicenseStatus.Active
                     && l.ExpiresAt.HasValue
                     && l.ExpiresAt.Value >= now
                     && l.ExpiresAt.Value <= in30)
            .OrderBy(l => l.ExpiresAt)
            .Take(6)
            .Select(l => new ExpiringSoonItem(
                l.Id, l.Key, l.Customer.Name, l.ExpiresAt!.Value))
            .ToListAsync();
    }

    public sealed record RecentActivation(
        string LicenseKey, string CustomerName, string MachineName, DateTime OccurredAt)
    {
        public string TimeAgo
        {
            get
            {
                var diff = DateTime.UtcNow - OccurredAt;
                if (diff.TotalMinutes < 1)  return "just now";
                if (diff.TotalHours   < 1)  return $"{(int)diff.TotalMinutes}m ago";
                if (diff.TotalDays    < 1)  return $"{(int)diff.TotalHours}h ago";
                return $"{(int)diff.TotalDays}d ago";
            }
        }
    }

    public sealed record ExpiringSoonItem(Guid Id, string Key, string CustomerName, DateTime ExpiresAt)
    {
        public string ExpiresIn
        {
            get
            {
                var diff = ExpiresAt - DateTime.UtcNow;
                if (diff.TotalDays < 1) return "today";
                if (diff.TotalDays < 2) return "tomorrow";
                return $"in {(int)diff.TotalDays} days";
            }
        }
    }
}
