namespace DM.Core.Licensing;

public interface ILicenseValidator
{
    Task<LicenseResult> ValidateAsync(CancellationToken ct = default);
}

// Carry the validation outcome and an optional human-readable message
// (e.g. "License expired", "No internet — using grace period").
public sealed record LicenseResult(bool IsValid, string? Message = null);
