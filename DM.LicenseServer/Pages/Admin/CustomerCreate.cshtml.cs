using DM.LicenseServer.DTOs;
using DM.LicenseServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DM.LicenseServer.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class CustomerCreateModel : PageModel
{
    private readonly ILicenseService _svc;
    public CustomerCreateModel(ILicenseService svc) => _svc = svc;

    [BindProperty] public CustomerInputModel Input { get; set; } = new();
    public string? Error { get; set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            Error = "Please fix the validation errors below.";
            return Page();
        }
        try
        {
            await _svc.CreateCustomerAsync(new CreateCustomerDto(
                Input.Name!, Input.Email!, Input.Country, Input.Notes));
            return Redirect("/admin/customers");
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
            when (ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505")
        {
            Error = "A customer with that email already exists.";
            return Page();
        }
    }

    public sealed class CustomerInputModel
    {
        [System.ComponentModel.DataAnnotations.Required]
        public string? Name    { get; set; }
        [System.ComponentModel.DataAnnotations.Required]
        [System.ComponentModel.DataAnnotations.EmailAddress]
        public string? Email   { get; set; }
        public string? Country { get; set; }
        public string? Notes   { get; set; }
    }
}
