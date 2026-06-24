using DM.App.Views;
using System.Windows;

namespace DM.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ── License gate ────────────────────────────────────────────────────
        // Replace CheckLicenseAsync() with a real ILicenseValidator call when
        // the license system is ready.  Show a license/activation window here
        // and call Shutdown() if validation fails — the main window never opens.
        bool licensed = await CheckLicenseAsync();
        if (!licensed)
        {
            MessageBox.Show("License validation failed.", "Download Manager",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        // ────────────────────────────────────────────────────────────────────

        new MainWindow().Show();
    }

    private static Task<bool> CheckLicenseAsync()
    {
        // Stub: always valid until the real license system is wired up.
        return Task.FromResult(true);
    }
}
