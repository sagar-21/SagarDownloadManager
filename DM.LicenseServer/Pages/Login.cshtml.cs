using System.Security.Claims;
using DM.LicenseServer.Controllers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DM.LicenseServer.Pages;

[AllowAnonymous]
public class LoginModel : PageModel
{
    private readonly IConfiguration _config;

    public LoginModel(IConfiguration config) => _config = config;

    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    public string? Error { get; set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
            return Redirect("/admin");

        if (_config["Admin:PasswordHash"] == "CHANGE_ME")
            Error = "Admin password not configured. POST to /admin/setup with your desired password first.";

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl)
    {
        var expectedUsername = _config["Admin:Username"] ?? "admin";
        var storedHash       = _config["Admin:PasswordHash"] ?? "";
        var storedSalt       = _config["Admin:PasswordSalt"] ?? "";

        if (storedHash == "CHANGE_ME" || storedSalt == "CHANGE_ME")
        {
            Error = "Admin password not configured. POST to /admin/setup first.";
            return Page();
        }

        if (!string.Equals(Username, expectedUsername, StringComparison.OrdinalIgnoreCase)
            || !AdminSetupController.VerifyPassword(Password, storedHash, storedSalt))
        {
            Error = "Invalid username or password.";
            return Page();
        }

        var claims = new List<Claim>
        {
            new Claim(ClaimTypes.Name, expectedUsername),
            new Claim(ClaimTypes.Role, "admin"),
        };

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims,
                CookieAuthenticationDefaults.AuthenticationScheme)));

        return Redirect(returnUrl ?? "/admin");
    }
}
