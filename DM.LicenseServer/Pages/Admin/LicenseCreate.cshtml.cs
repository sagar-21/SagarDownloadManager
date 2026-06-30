using DM.LicenseServer.DTOs;
using DM.LicenseServer.Models;
using DM.LicenseServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DM.LicenseServer.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class LicenseCreateModel : PageModel
{
    private readonly ILicenseService _svc;
    public LicenseCreateModel(ILicenseService svc) => _svc = svc;

    [BindProperty] public LicenseInputModel Input { get; set; } = new() { MaxDevices = 1, Plan = "Basic" };
    public List<Customer> Customers { get; set; } = [];
    public string? Error { get; set; }

    public async Task OnGetAsync()
    {
        Customers = await _svc.GetCustomersAsync(null);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Customers = await _svc.GetCustomersAsync(null);

        if (!ModelState.IsValid || Input.CustomerId == Guid.Empty)
        {
            Error = "Please select a customer and fill in all required fields.";
            return Page();
        }

        var license = await _svc.CreateLicenseAsync(new CreateLicenseDto(
            Input.CustomerId, Input.Plan!, Input.MaxDevices, Input.ExpiresAt, Input.Notes));

        return Redirect($"/admin/licenses/{license.Id}");
    }

    public sealed class LicenseInputModel
    {
        [System.ComponentModel.DataAnnotations.Required]
        public Guid   CustomerId { get; set; }

        [System.ComponentModel.DataAnnotations.Required]
        public string? Plan      { get; set; } = "Basic";

        [System.ComponentModel.DataAnnotations.Range(1, 1000)]
        public int    MaxDevices { get; set; } = 1;

        public DateTime? ExpiresAt { get; set; }
        public string?   Notes     { get; set; }
    }
}
