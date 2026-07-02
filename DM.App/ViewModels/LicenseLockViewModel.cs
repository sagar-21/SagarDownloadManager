using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DM.App.Licensing;

namespace DM.App.ViewModels;

public partial class LicenseLockViewModel : ObservableObject
{
    private readonly LicenseService _svc;

    [ObservableProperty] private string _title              = "";
    [ObservableProperty] private string _message            = "";
    [ObservableProperty] private bool   _isSuspended        = false;
    [ObservableProperty] private bool   _isExpiredOrOffline = false;
    [ObservableProperty] private bool   _canRetry           = false;
    [ObservableProperty] private bool   _isRetrying         = false;

    /// <summary>Fires when the server confirms the license is valid again.</summary>
    public event Action? DismissedToActive;
    /// <summary>Fires when user chooses to enter a different license key.</summary>
    public event Action? RequestChangeKey;

    public LicenseLockViewModel(LicenseService svc, LicenseStatus initialStatus, string? message)
    {
        _svc = svc;
        Apply(initialStatus, message);
        // Subscribe so an auto-recovery via the background heartbeat also dismisses
        // this window — not only manual Retry clicks.
        _svc.StatusChanged += OnServiceStatusChanged;
    }

    // Called when the lock window closes (dismissal or resolution)
    public void Unsubscribe() => _svc.StatusChanged -= OnServiceStatusChanged;

    // ── Service status updates ────────────────────────────────────────────────

    // May fire on any thread.
    private void OnServiceStatusChanged(LicenseStatus status, string? msg)
    {
        if (status is LicenseStatus.Active or LicenseStatus.GracePeriod)
            DismissedToActive?.Invoke();   // handled on the lock window's dispatcher
        else
            Apply(status, msg);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task RetryAsync()
    {
        IsRetrying = true;
        // TryAutoActivateAsync fires StatusChanged, which OnServiceStatusChanged handles.
        // We just need to wait for it to finish.
        await _svc.TryAutoActivateAsync(forceHeartbeat: true);
        IsRetrying = false;
    }

    [RelayCommand]
    private async Task ChangeKeyAsync()
    {
        await _svc.DeactivateAsync();
        RequestChangeKey?.Invoke();
    }

    // ── State application ─────────────────────────────────────────────────────

    private void Apply(LicenseStatus status, string? message)
    {
        IsSuspended        = status == LicenseStatus.Suspended;
        IsExpiredOrOffline = status is LicenseStatus.Expired or LicenseStatus.GracePeriod;

        (Title, Message, CanRetry) = status switch
        {
            LicenseStatus.Suspended =>
                ("License Suspended",
                 message ?? "Your license has been temporarily suspended. Contact support to reactivate.",
                 true),

            LicenseStatus.Revoked =>
                ("License Revoked",
                 message ?? "Your license has been permanently revoked.",
                 false),

            LicenseStatus.LicenseExpired =>
                ("License Expired",
                 message ?? "Your license has expired. Please renew your subscription to continue.",
                 false),

            LicenseStatus.Expired =>
                ("Offline Grace Expired",
                 message ?? "Could not verify your license and the offline grace period has expired. Connect to the internet and try again.",
                 true),

            LicenseStatus.GracePeriod =>
                ("Running Offline",
                 message ?? "License server unreachable. Running on offline grace period.",
                 true),

            _ =>
                ("License Issue",
                 message ?? "There is a problem with your license.",
                 true),
        };
    }
}
