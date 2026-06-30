using DM.LicenseServer.DTOs;
using DM.LicenseServer.Models;
using DM.LicenseServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DM.LicenseServer.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class ActivityModel : PageModel
{
    private readonly ILicenseService _svc;
    public ActivityModel(ILicenseService svc) => _svc = svc;

    [FromQuery(Name = "q")]   public string?          Q           { get; set; }
    [FromQuery(Name = "evt")] public ActivationEvent? EventFilter { get; set; }

    public List<ActivityLogEntry> Logs { get; set; } = [];

    public async Task OnGetAsync()
    {
        Logs = await _svc.GetAllLogsAsync(Q, 500);
        if (EventFilter.HasValue)
            Logs = Logs.Where(l => l.Event == EventFilter.Value).ToList();
    }
}
