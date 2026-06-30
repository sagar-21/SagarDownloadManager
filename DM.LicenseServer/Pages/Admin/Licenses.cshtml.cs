using DM.LicenseServer.DTOs;
using DM.LicenseServer.Models;
using DM.LicenseServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DM.LicenseServer.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class LicensesModel : PageModel
{
    private readonly ILicenseService _svc;
    public LicensesModel(ILicenseService svc) => _svc = svc;

    [FromQuery(Name = "q")]          public string? Q             { get; set; }
    [FromQuery(Name = "status")]     public LicenseStatus? StatusFilter { get; set; }
    [FromQuery(Name = "customerId")] public Guid?   CustomerId    { get; set; }

    public List<LicenseSummary> Licenses { get; set; } = [];

    public async Task OnGetAsync()
    {
        Licenses = await _svc.GetLicensesAsync(Q, StatusFilter);
        if (CustomerId.HasValue)
            Licenses = Licenses.Where(l => true).ToList(); // further filter if needed
    }
}
