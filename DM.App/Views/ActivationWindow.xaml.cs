using System.Windows;
using System.Windows.Input;
using DM.App.ViewModels;

namespace DM.App.Views;

public partial class ActivationWindow
{
    public ActivationWindow(ActivationViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        // ActivationSucceeded fires on the UI thread (from async command continuation).
        // BeginInvoke avoids any re-entrancy if called while the dispatcher is already
        // inside a synchronous operation.
        vm.ActivationSucceeded += () => Dispatcher.BeginInvoke(ContinueToApp);
        Loaded += (_, _) => KeyBox.Focus();
    }

    private void KeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var vm = (ActivationViewModel)DataContext;
            if (vm.ActivateCommand.CanExecute(null))
                vm.ActivateCommand.Execute(null);
        }
    }

    private void ContinueToApp()
    {
        // Tell App to start the main window, then close ourselves.
        (Application.Current as App)?.OnActivationSucceeded();
        Close();
    }
}
