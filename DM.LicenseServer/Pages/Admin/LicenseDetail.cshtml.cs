using DM.LicenseServer.DTOs;
using DM.LicenseServer.Models;
using DM.LicenseServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DM.LicenseServer.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class LicenseDetailModel : PageModel
{
    private readonly ILicenseService _svc;
    public LicenseDetailModel(ILicenseService svc) => _svc = svc;

    public License?            License { get; set; }
    public List<DeviceSummary> Devices { get; set; } = [];
    public List<LogEntry>      Logs    { get; set; } = [];
    public string              Tab     { get; set; } = "devices";
    public string?             Flash   { get; set; }
    public string?             Error   { get; set; }

    public async Task OnGetAsync(Guid id, string? tab, string? flash)
    {
        Tab     = tab ?? "devices";
        Flash   = flash;
        License = await _svc.GetLicenseByIdAsync(id);
        if (License is null) return;
        Devices = await _svc.GetDevicesAsync(id);
        Logs    = await _svc.GetLogsAsync(id, 100);
    }

    public async Task<IActionResult> OnPostSetStatusAsync(Guid id, string status)
    {
        if (!Enum.TryParse<LicenseStatus>(status, out var s))
        {
            Error = "Invalid status.";
            return await ReloadPage(id);
        }

        var ok = await _svc.SetLicenseStatusAsync(id, s, "via admin panel");
        if (!ok)
        {
            Error = "License not found.";
            return await ReloadPage(id);
        }

        return Redirect($"/admin/licenses/{id}?flash=Status+updated+to+{s}");
    }

    public async Task<IActionResult> OnPostExtendAsync(Guid id, string newExpiry)
    {
        if (!DateTime.TryParse(newExpiry, out var expiry))
        {
            Error = "Invalid expiry date.";
            return await ReloadPage(id);
        }

        var ok = await _svc.ExtendLicenseAsync(id, expiry, "via admin panel");
        if (!ok)
        {
            Error = "License not found.";
            return await ReloadPage(id);
        }

        return Redirect($"/admin/licenses/{id}?flash=Expiry+extended+to+{expiry:yyyy-MM-dd}");
    }

    public async Task<IActionResult> OnPostDeactivateDeviceAsync(Guid id, Guid deviceId)
    {
        await _svc.DeactivateDeviceAsync(deviceId);
        return Redirect($"/admin/licenses/{id}?flash=Device+deactivated&tab=devices");
    }

    public async Task<IActionResult> OnPostReactivateDeviceAsync(Guid id, Guid deviceId)
    {
        await _svc.ReactivateDeviceAsync(deviceId);
        return Redirect($"/admin/licenses/{id}?flash=Device+reactivated&tab=devices");
    }

    private async Task<PageResult> ReloadPage(Guid id)
    {
        License = await _svc.GetLicenseByIdAsync(id);
        Devices = await _svc.GetDevicesAsync(id);
        Logs    = await _svc.GetLogsAsync(id, 100);
        Tab     = "devices";
        return Page();
    }
}
