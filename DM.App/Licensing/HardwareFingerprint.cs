using System.Management;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace DM.App.Licensing;

/// <summary>
/// Produces a stable 32-character hex identifier for this machine.
///
/// Sources combined (in order of reliability):
///   1. Windows MachineGuid — always present, per-OS-install
///   2. CPU ProcessorId     — hardware-bound, survives OS reinstall
///   3. Motherboard serial  — hardware-bound; absent on some VMs
///
/// A SHA-256 hash of the combined string means a change to any single source
/// produces an entirely different fingerprint. The first 32 hex digits give
/// 128 bits of uniqueness — enough to distinguish machines, not unique enough
/// to be a privacy concern in isolation.
///
/// Minor hardware changes (RAM, GPU, drives) do NOT affect the fingerprint;
/// a CPU swap or OS reinstall WILL change it, requiring re-activation.
/// </summary>
internal static class HardwareFingerprint
{
    private static string? _cached;

    internal static string Compute() => _cached ??= ComputeImpl();

    private static string ComputeImpl()
    {
        var parts = new List<string>
        {
            GetMachineGuid(),
            GetProcessorId(),
            GetMotherboardSerial(),
        };

        var combined = string.Join("|", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (string.IsNullOrWhiteSpace(combined))
            combined = Environment.MachineName; // last-resort fallback

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(hash)[..32].ToLower();
    }

    private static string GetMachineGuid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid")?.ToString() ?? "";
        }
        catch { return ""; }
    }

    private static string GetProcessorId()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessorId FROM Win32_Processor");
            foreach (ManagementObject obj in searcher.Get())
                return obj["ProcessorId"]?.ToString()?.Trim() ?? "";
        }
        catch { }
        return "";
    }

    private static string GetMotherboardSerial()
    {
        // Some OEM boards and VMs return placeholder strings
        var garbage = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Default string", "To be filled by O.E.M.", "None", "N/A", "" };
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT SerialNumber FROM Win32_BaseBoard");
            foreach (ManagementObject obj in searcher.Get())
            {
                var s = obj["SerialNumber"]?.ToString()?.Trim() ?? "";
                if (!garbage.Contains(s)) return s;
            }
        }
        catch { }
        return "";
    }
}
