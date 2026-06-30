using System.ComponentModel.DataAnnotations;
using DM.LicenseServer.Models;

namespace DM.LicenseServer.DTOs;

// ── App-facing API request bodies ────────────────────────────────────────────

public sealed record ActivateRequest(
    [Required] string LicenseKey,
    [Required] string Fingerprint,
    string? MachineName,
    string? OperatingSystem
);

public sealed record ValidateRequest(
    [Required] string LicenseKey,
    [Required] string Fingerprint
);

public sealed record DeactivateRequest(
    [Required] string LicenseKey,
    [Required] string Fingerprint
);

/// <summary>
/// Sent by the app when its anti-tamper or integrity checks fire.
/// The server compares AssemblyHash against the stored KnownGoodHash.
///
/// Design: the app SELF-REPORTS suspicious behaviour.  A skilled attacker can
/// patch out these calls, but doing so is additional effort — and the server's
/// heartbeat hash-comparison catches unpatched tampers independently.
/// </summary>
public sealed record ReportRequest(
    [Required] string LicenseKey,
    [Required] string Fingerprint,
    [Required] string AssemblyHash,    // SHA-256 hex of DM.App.exe
    [Required] string ReportType,      // "tamper" | "debugger" | "hash_mismatch"
    string?           Details           // freeform context (e.g. "DebuggerPresent")
);

public sealed record ReportResponse(bool Acknowledged);

/// <summary>
/// Request for a short-lived session token.  The server binds the token to
/// the hardware fingerprint so stolen tokens are useless on other machines.
/// </summary>
public sealed record SessionDataRequest(
    [Required] string LicenseKey,
    [Required] string Fingerprint
);

/// <summary>
/// Contains a signed, short-lived JWT with:
///   dk  — AES-256 key the app uses to decrypt its embedded download templates
///   mq  — max quality allowed for this tier ("720", "1080", "4320")
///   mc  — max concurrent downloads
///   be  — batch downloads enabled
///
/// The client stores this in memory only (never on disk).  Expiry = 90 minutes,
/// so a revoked license becomes fully non-functional within 90 minutes even
/// without a heartbeat, because the download key is gone.
/// </summary>
public sealed record SessionDataResponse(string Token);

// ── App-facing API responses ──────────────────────────────────────────────────

public sealed record ActivateResponse(
    bool   Ok,
    string Token,
    string LicenseKey,
    string Plan,
    int    MaxDevices,
    string CustomerName,
    string? ExpiresAt,
    string? Error
);

public sealed record ValidateResponse(
    bool    Ok,
    string? Token,
    string  Action,   // "continue" | "warn" | "disable"
    string? Error
);

public sealed record DeactivateResponse(
    bool    Ok,
    string? Error
);

public sealed record ErrorResponse(string Error, string? Detail = null);

// ── Admin API DTOs ────────────────────────────────────────────────────────────

public sealed record CreateCustomerDto(
    [Required, MaxLength(200)] string Name,
    [Required, EmailAddress, MaxLength(320)] string Email,
    [MaxLength(100)] string? Country,
    string? Notes
);

public sealed record CreateLicenseDto(
    [Required] Guid   CustomerId,
    [Required] string Plan,
    [Range(1, 1000)] int MaxDevices,
    DateTime? ExpiresAt,
    string? Notes
);

public sealed record UpdateLicenseStatusDto(
    [Required] LicenseStatus Status,
    string? Notes
);

// ── View projections (returned in admin pages / API) ─────────────────────────

public sealed record LicenseSummary(
    Guid          Id,
    string        Key,
    string        CustomerName,
    string        CustomerEmail,
    string        Plan,
    LicenseStatus Status,
    int           MaxDevices,
    int           ActiveDeviceCount,
    DateTime      IssuedAt,
    DateTime?     ExpiresAt
);

public sealed record DeviceSummary(
    Guid            Id,
    string          HardwareFingerprint,
    string          MachineName,
    string          OperatingSystem,
    string          IpAddress,
    string?         LastCountry,
    DateTime        FirstSeenAt,
    DateTime        LastSeenAt,
    DeviceStatus    Status,
    IntegrityStatus IntegrityStatus,
    string?         LastReportedHash,
    int             TamperReportCount
);

public sealed record AbuseFlagSummary(
    Guid          Id,
    AbuseFlagType Type,
    string        Details,
    bool          Reviewed,
    DateTime      CreatedAt,
    Guid?         DeviceId
);

public sealed record LogEntry(
    Guid            Id,
    ActivationEvent Event,
    string          IpAddress,
    string          Notes,
    DateTime        OccurredAt,
    Guid?           DeviceId
);

public sealed record ActivityLogEntry(
    Guid            Id,
    Guid            LicenseId,
    string          LicenseKey,
    ActivationEvent Event,
    string          IpAddress,
    string          Notes,
    DateTime        OccurredAt,
    Guid?           DeviceId
);
