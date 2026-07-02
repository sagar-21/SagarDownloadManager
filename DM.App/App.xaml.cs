using DM.App.Licensing;
using DM.App.ViewModels;
using DM.App.Views;
using DM.App.Views.Dialogs;
using DM.Core.Settings;
using System.Windows;
using Wpf.Ui.Appearance;

namespace DM.App;

public partial class App : Application
{
    internal LicenseService? LicenseService { get; private set; }

    // Created only after license is confirmed.
    public MainViewModel? ViewModel { get; private set; }

    // Prevents the background heartbeat handler from showing a second lock window
    // when one is already open handling retries.
    private bool _lockWindowOpen = false;

    // Single-instance guard — held for the entire process lifetime.
    private static Mutex? _singleInstanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Hide the main thread from user-mode debugger events before doing anything else.
        AntiTamper.HardenThread();

        // Enforce single instance: if another copy is already running, exit quietly.
        // "Global\" prefix works across user-session boundaries (e.g. fast-user-switching).
        _singleInstanceMutex = new Mutex(true, @"Global\SagarDownloadManager_SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
        ApplicationThemeManager.Apply(ApplicationTheme.Dark);

        // ── Build license service ─────────────────────────────────────────────
        var settings = new AppSettingsService();
        settings.Load();

        var store  = new LicenseStore();
        var client = new LicenseClient(settings.Current.LicenseServerUrl);
        LicenseService = new LicenseService(store, client);

        // ── Anti-tamper pre-check ─────────────────────────────────────────────
        //
        // Assembly hash mismatch → force a server heartbeat instead of trusting
        // the cached token. A legitimate update re-validates cleanly; a patched
        // binary will still be caught by the server's license check.
        //
        // We don't hard-exit here. False-positive lockouts (e.g. two users on
        // the same machine, or a benign update) hurt honest users more than the
        // marginal gain. The SERVER is the real authority — heartbeat revocation
        // works regardless of any local patches.
        var stored          = store.Load();
        var tamper          = AntiTamper.Check(stored);
        bool forceHeartbeat = tamper.RequiresImmediateHeartbeat;

        // Queue soft signals (debugger/timing/env) for delivery to /report.
        // These do NOT force a heartbeat — we don't hard-lock on them to avoid
        // false positives from corporate AV tools or Wine users.
        if (tamper.AssemblyModified)
            LicenseService.QueueIntegrityReport("hash_mismatch",
                AntiTamper.HashMainAssembly() ?? "unknown",
                "Assembly hash changed since activation");
        else if (tamper.AnySoftSignal && tamper.SoftReportType is { } softType)
            LicenseService.QueueIntegrityReport(softType,
                AntiTamper.HashMainAssembly() ?? "unknown",
                tamper.DebuggerDetected ? "Debugger present at startup" : null);

        // ── Startup license check ─────────────────────────────────────────────
        //
        // Subscribe AFTER the startup check so the status changes fired internally
        // by TryAutoActivateAsync don't trigger the heartbeat handler prematurely.
        var status = await LicenseService.TryAutoActivateAsync(forceHeartbeat);
        LicenseService.StatusChanged += OnLicenseStatusChanged;

        switch (status)
        {
            case LicenseStatus.Active:
            case LicenseStatus.GracePeriod:
                LicenseService.StartHeartbeat();
                ShowMainWindow();
                break;

            case LicenseStatus.NotActivated:
                ShowActivationWindow();
                break;

            case LicenseStatus.Suspended:
            case LicenseStatus.Revoked:
            case LicenseStatus.LicenseExpired:
                ShowAlertDialog(
                    status == LicenseStatus.Suspended      ? LicenseAlertType.Suspended :
                    status == LicenseStatus.LicenseExpired ? LicenseAlertType.Expired :
                    LicenseAlertType.Revoked);
                break;

            default: // Expired, offline grace expired
                ShowLockWindow(status, null);
                break;
        }
    }

    // ── Called by ActivationWindow on successful key entry ─────────────────────

    internal void OnActivationSucceeded()
    {
        LicenseService!.StartHeartbeat();
        // StatusChanged already subscribed from OnStartup — no need to re-subscribe.
        ShowMainWindow();
    }

    // ── License status change handler (fires from heartbeat thread-pool) ───────
    //
    // Design:
    //   Active/GracePeriod → create main window if not yet created, or show it.
    //   Anything else      → hide main window, show lock screen (once).
    //
    //   The LicenseLockViewModel ALSO subscribes to StatusChanged internally to
    //   update its message and fire DismissedToActive when the status recovers.
    //   Both subscriptions are intentional — they handle different responsibilities.

    private void OnLicenseStatusChanged(LicenseStatus status, string? message)
    {
        // BeginInvoke: heartbeat fires on a thread-pool thread; all UI work must
        // be marshalled to the WPF dispatcher.
        Dispatcher.BeginInvoke(() =>
        {
            switch (status)
            {
                case LicenseStatus.Active:
                case LicenseStatus.GracePeriod:
                    _lockWindowOpen = false;
                    LicenseService!.StartHeartbeat(); // idempotent re-arm
                    if (MainWindow is null)
                        ShowMainWindow();             // first-time after lock-screen startup
                    else
                        MainWindow.Show();
                    break;

                case LicenseStatus.Suspended:
                case LicenseStatus.Revoked:
                case LicenseStatus.LicenseExpired:
                    ShowAlertDialog(
                        status == LicenseStatus.Suspended      ? LicenseAlertType.Suspended :
                        status == LicenseStatus.LicenseExpired ? LicenseAlertType.Expired :
                        LicenseAlertType.Revoked);
                    break;

                default:
                    if (_lockWindowOpen) return;      // already locked — lock VM handles update
                    MainWindow?.Hide();
                    ShowLockWindow(status, message);
                    break;
            }
        });
    }

    // ── Window helpers ────────────────────────────────────────────────────────

    private void ShowAlertDialog(LicenseAlertType type)
    {
        // Keep app alive while the dialog is shown and during the window transition
        // that follows — restored by ChangeKeyAsync after activation window opens.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var dlg = new LicenseAlertDialog(type);
        bool wantsNewKey = false;
        dlg.NewKeyRequested += () => wantsNewKey = true;
        dlg.ShowDialog();

        if (wantsNewKey)
            _ = ChangeKeyAsync();
        else
        {
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            Shutdown();
        }
    }

    internal void ShowActivationWindow()
    {
        new ActivationWindow(new ActivationViewModel(LicenseService!)).Show();
    }

    /// <summary>
    /// Deactivates the current license, closes the main window, and shows the activation
    /// screen — used by "Change License Key" from Settings and the Expired dialog.
    /// </summary>
    internal async Task ChangeKeyAsync()
    {
        // Prevent WPF auto-shutdown during the gap between windows.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Unsubscribe so DeactivateAsync's NotActivated status change
        // does not trigger the lock-screen or alert handler mid-transition.
        LicenseService!.StatusChanged -= OnLicenseStatusChanged;

        // Close main window if it was open (Settings "Change Key" path).
        if (MainWindow is not null)
        {
            MainWindow.Close();
            MainWindow = null;
            ViewModel?.Dispose();
            ViewModel = null;
        }

        // Tell the server this device is deactivated (best-effort).
        await LicenseService.DeactivateAsync();

        // Open activation window — user enters their key here.
        ShowActivationWindow();

        // Re-subscribe so the heartbeat handler works after activation.
        LicenseService.StatusChanged += OnLicenseStatusChanged;

        // Restore normal shutdown behaviour now that a window is open.
        ShutdownMode = ShutdownMode.OnLastWindowClose;
    }

    private void ShowMainWindow()
    {
        ViewModel  = new MainViewModel();
        ViewModel.SettingsVm.SetLicenseService(LicenseService!);
        var win    = new MainWindow();
        MainWindow = win;
        win.Show();
    }

    private void ShowLockWindow(LicenseStatus status, string? message)
    {
        _lockWindowOpen = true;
        var vm  = new LicenseLockViewModel(LicenseService!, status, message);
        var win = new LicenseLockWindow(vm);
        win.Closed += (_, _) => _lockWindowOpen = false;
        win.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Release the single-instance mutex so a new launch can succeed immediately.
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        _singleInstanceMutex?.Dispose();

        LicenseService?.Dispose();
        ViewModel?.Dispose();
        base.OnExit(e);
    }
}
