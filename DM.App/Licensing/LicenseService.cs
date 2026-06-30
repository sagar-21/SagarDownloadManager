namespace DM.App.Licensing;

public enum LicenseStatus
{
    Unknown,        // before TryAutoActivateAsync completes
    NotActivated,   // no stored license on this machine
    Active,         // server confirmed (or fresh token within grace window)
    GracePeriod,    // server unreachable but token not yet expired
    Suspended,      // server returned "warn" (admin suspended the license)
    Revoked,        // server returned "disable" (revoked or permanently expired)
    Expired,        // token expired AND server unreachable — grace window over
}

/// <summary>
/// Orchestrates the full licensing lifecycle:
///   1. Startup:    load stored token → local validation → server heartbeat
///   2. Activation: call server → store token → update status
///   3. Heartbeat:  periodic 6h server check; rotates token; sends pending reports
///   4. Session:    fetches /session-data every 80 min (before 90-min expiry)
///   5. Integrity:  queues tamper/debugger reports; sends on next heartbeat
///   6. Deactivation: notify server → clear stored token
///
/// StatusChanged fires from BOTH the UI thread (startup, activation) and the
/// thread-pool (heartbeat). App.xaml.cs must marshal to the dispatcher.
/// </summary>
public sealed class LicenseService : IDisposable
{
    private readonly LicenseStore      _store;
    private readonly LicenseClient     _client;
    internal readonly SessionDataCache SessionData = new();

    private StoredLicense?            _current;
    private CancellationTokenSource?  _heartbeatCts;
    private CancellationTokenSource?  _sessionCts;

    // Pending integrity report — queued when tamper is detected offline, sent
    // on the next successful network connection.
    private PendingReport? _pendingReport;
    private readonly object _reportLock = new();

    public LicenseStatus Status       { get; private set; } = LicenseStatus.Unknown;
    public string?       LicenseKey   { get; private set; }
    public string?       Plan         { get; private set; }
    public string?       CustomerName { get; private set; }
    internal LicenseInfo?  Token      { get; private set; }

    /// <summary>
    /// Fires whenever license status changes.
    /// May be raised on any thread — callers must marshal to UI thread if needed.
    /// </summary>
    public event Action<LicenseStatus, string?>? StatusChanged;

    internal LicenseService(LicenseStore store, LicenseClient client)
    {
        _store  = store;
        _client = client;
    }

    // ── Startup ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called once on startup. Returns the initial status.
    ///
    /// Fast path (token ≤ 1 h old): trust local token, skip network.
    /// Normal path: validate token locally → run heartbeat to get fresh token.
    /// Offline path: token still within 48-h expiry → GracePeriod.
    /// Grace expired: show lock screen.
    /// </summary>
    public async Task<LicenseStatus> TryAutoActivateAsync(bool forceHeartbeat = false)
    {
        var stored = _store.Load();
        if (stored is null) return Set(LicenseStatus.NotActivated, null);

        var fp   = HardwareFingerprint.Compute();
        var info = LicenseTokenValidator.Validate(stored.Token, fp);

        if (info is null)
        {
            _store.Delete();
            return Set(LicenseStatus.NotActivated, null);
        }

        _current = stored;
        ApplyInfo(info);

        bool tokenFresh = (DateTime.UtcNow - stored.StoredAt) < TimeSpan.FromHours(1);
        if (tokenFresh && !forceHeartbeat)
        {
            // Fast path — still need to fetch session data for the download engine
            _ = TryFetchSessionDataAsync(fp);
            return Set(LicenseStatus.Active, null);
        }

        return await RunHeartbeatOnceAsync(fp);
    }

    // ── Activation ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Activates a new license key on this machine.
    /// On success the token is stored and status is set to Active.
    /// </summary>
    public async Task<(bool Ok, string? Error)> ActivateAsync(string key)
    {
        var fp      = HardwareFingerprint.Compute();
        var machine = Environment.MachineName;
        var os      = Environment.OSVersion.ToString();

        try
        {
            var resp = await _client.ActivateAsync(key.Trim().ToUpper(), fp, machine, os);
            if (resp is null) return (false, "Could not reach the license server. Check your connection.");
            if (!resp.Ok)     return (false, resp.Error ?? "Activation failed.");
            if (resp.Token is null) return (false, "Server returned no token.");

            var info = LicenseTokenValidator.Validate(resp.Token, fp);
            if (info is null) return (false, "Server token failed local verification. Contact support.");

            var stored = new StoredLicense(
                LicenseKey  : resp.LicenseKey ?? key,
                Token       : resp.Token,
                Fingerprint : fp,
                AssemblyHash: AntiTamper.HashMainAssembly(),
                StoredAt    : DateTime.UtcNow);

            _store.Save(stored);
            _current = stored;
            ApplyInfo(info);
            Set(LicenseStatus.Active, null);
            return (true, null);
        }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            return (false, "Could not reach the license server. Check your connection.");
        }
    }

    // ── Integrity reporting ────────────────────────────────────────────────────

    /// <summary>
    /// Queues a tamper or debugger event for delivery on the next heartbeat.
    /// Safe to call from any thread at any point — if the report cannot be sent
    /// immediately (network down), it will be delivered in the next cycle.
    ///
    /// reportType: "tamper" | "debugger" | "hash_mismatch"
    /// </summary>
    public void QueueIntegrityReport(string reportType, string assemblyHash, string? details = null)
    {
        lock (_reportLock)
        {
            // Don't accumulate — keep only the most recent report per type
            if (_pendingReport is null || _pendingReport.ReportType != reportType)
                _pendingReport = new PendingReport(reportType, assemblyHash, details);
        }
    }

    private async Task SendPendingReportAsync(string fp, CancellationToken ct)
    {
        PendingReport? report;
        lock (_reportLock) { report = _pendingReport; }
        if (report is null || _current is null) return;

        var sent = await _client.ReportAsync(
            _current.LicenseKey, fp,
            report.AssemblyHash, report.ReportType, report.Details,
            ct);

        if (sent)
            lock (_reportLock) { if (_pendingReport == report) _pendingReport = null; }
    }

    // ── Heartbeat ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts the background heartbeat loop (fires every 6 hours).
    /// Also starts the session-data refresh loop (fires every 80 minutes).
    /// Safe to call multiple times — subsequent calls are ignored.
    /// </summary>
    public void StartHeartbeat()
    {
        if (_heartbeatCts is not null) return;
        _heartbeatCts = new CancellationTokenSource();
        _sessionCts   = new CancellationTokenSource();
        _ = HeartbeatLoopAsync(_heartbeatCts.Token);
        _ = SessionRefreshLoopAsync(_sessionCts.Token);
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_current is null) continue;
                try
                {
                    var fp = HardwareFingerprint.Compute();
                    await RunHeartbeatOnceAsync(fp, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* keep loop alive on unexpected errors */ }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Refreshes session data every 80 minutes (tokens expire at 90 minutes).
    /// This ensures the download key is always fresh without waiting for the
    /// 6-hour license heartbeat.
    /// </summary>
    private async Task SessionRefreshLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_current is null || Status is not (LicenseStatus.Active or LicenseStatus.GracePeriod))
                    continue;
                var fp = HardwareFingerprint.Compute();
                await TryFetchSessionDataAsync(fp, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<LicenseStatus> RunHeartbeatOnceAsync(string fp,
        CancellationToken ct = default)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            // Send any pending integrity report before the validate call
            await SendPendingReportAsync(fp, linked.Token);

            var resp = await _client.ValidateAsync(_current!.LicenseKey, fp, linked.Token);
            if (resp is null) return HandleOffline();

            if (resp.Token is not null)
            {
                var info = LicenseTokenValidator.Validate(resp.Token, fp);
                if (info is not null)
                {
                    _current = _current with { Token = resp.Token, StoredAt = DateTime.UtcNow };
                    _store.Save(_current);
                    ApplyInfo(info);
                }
            }

            var status = resp.Action switch
            {
                "continue"    => Set(LicenseStatus.Active,      null),
                "warn"        => Set(LicenseStatus.Suspended,   resp.Error ?? "License suspended."),
                "disable"     => Set(LicenseStatus.Revoked,     resp.Error ?? "License revoked."),
                "blacklisted" => Set(LicenseStatus.Revoked,     resp.Error ?? "This installation has been blocked."),
                _             => Set(LicenseStatus.Revoked,     resp.Error ?? "License revoked."),
            };

            // Fetch fresh session data after a successful heartbeat
            if (status is LicenseStatus.Active or LicenseStatus.GracePeriod)
                _ = TryFetchSessionDataAsync(fp);
            else
                SessionData.Clear(); // revoked/suspended — kill the download key immediately

            return status;
        }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            return HandleOffline();
        }
    }

    /// <summary>
    /// Fetches and stores a fresh session data token.
    /// Non-critical — failure is logged but does NOT change license status.
    /// The download engine will degrade gracefully when no session is available.
    /// </summary>
    private async Task TryFetchSessionDataAsync(string fp, CancellationToken ct = default)
    {
        if (_current is null) return;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            var resp = await _client.FetchSessionDataAsync(_current.LicenseKey, fp, linked.Token);
            if (resp?.Token is not null)
                SessionData.Store(resp.Token, fp);
        }
        catch { /* non-critical — session refresh will retry */ }
    }

    private LicenseStatus HandleOffline()
    {
        // Use the token's server-set expiry as the offline grace window.
        // The server controls grace duration by choosing the JWT lifetime (default 48 h).
        // A shorter token TTL = tighter enforcement; longer = more user-friendly offline mode.
        var expiry = Token?.TokenExpiry
                     ?? LicenseTokenValidator.PeekExpiry(_current!.Token);

        if (expiry.HasValue && expiry.Value > DateTime.UtcNow)
        {
            var remaining = expiry.Value - DateTime.UtcNow;
            var msg = $"Running offline. License check will retry. Grace expires in {FormatDuration(remaining)}.";
            return Set(LicenseStatus.GracePeriod, msg);
        }

        return Set(LicenseStatus.Expired,
            "Offline grace period has expired. Connect to the internet to verify your license.");
    }

    // ── Deactivation ───────────────────────────────────────────────────────────

    /// <summary>
    /// Releases this device slot on the server and wipes the local token.
    /// Safe to call even when the server is unreachable (best-effort notify).
    /// </summary>
    public async Task DeactivateAsync()
    {
        if (_current is not null)
        {
            await _client.DeactivateAsync(_current.LicenseKey, HardwareFingerprint.Compute());
        }
        _store.Delete();
        _current = null;
        ApplyInfo(null);
        Set(LicenseStatus.NotActivated, null);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private LicenseStatus Set(LicenseStatus s, string? message)
    {
        Status = s;
        StatusChanged?.Invoke(s, message);
        return s;
    }

    private void ApplyInfo(LicenseInfo? info)
    {
        Token        = info;
        LicenseKey   = info?.LicenseKey;
        Plan         = info?.Plan;
        CustomerName = info?.CustomerName;
    }

    private static bool IsNetworkError(Exception ex)
        => ex is HttpRequestException or TaskCanceledException or OperationCanceledException;

    private static string FormatDuration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        return $"{t.Minutes}m";
    }

    public void Dispose()
    {
        _heartbeatCts?.Cancel();
        _heartbeatCts?.Dispose();
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        SessionData.Clear();
        _client.Dispose();
    }
}

// ── Internal types ─────────────────────────────────────────────────────────────

sealed record PendingReport(string ReportType, string AssemblyHash, string? Details);
