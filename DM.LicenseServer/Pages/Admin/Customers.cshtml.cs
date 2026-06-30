using DM.LicenseServer.Models;
using DM.LicenseServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DM.LicenseServer.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class CustomersModel : PageModel
{
    private readonly ILicenseService _svc;
    public CustomersModel(ILicenseService svc) => _svc = svc;

    [FromQuery(Name = "q")] public string? Q { get; set; }
    public List<Customer> Customers { get; set; } = [];

    public async Task OnGetAsync()
    {
        Customers = await _svc.GetCustomersAsync(Q);
    }
}
