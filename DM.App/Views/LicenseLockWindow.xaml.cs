using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using DM.App.ViewModels;

namespace DM.App.Views;

public partial class LicenseLockWindow
{
    private readonly LicenseLockViewModel _vm;

    public LicenseLockWindow(LicenseLockViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        DataContext = vm;

        // DismissedToActive fires from the VM (possibly thread-pool thread via heartbeat)
        vm.DismissedToActive += () => Dispatcher.BeginInvoke(Close);

        vm.RequestChangeKey += () => Dispatcher.BeginInvoke(() =>
        {
            (Application.Current as App)?.ShowActivationWindow();
            Close();
        });

        Closed += (_, _) => _vm.Unsubscribe();
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
