using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DM.App.ViewModels;
using DM.App.Views.Dialogs;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace DM.App.Views.Pages;

public partial class SettingsPage : Page
{
    private readonly MainViewModel _mainVm;

    public SettingsPage(MainViewModel vm)
    {
        InitializeComponent();
        _mainVm     = vm;
        DataContext = vm.SettingsVm;
    }

    private void AccentSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string hex) return;
        var color = (Color)ColorConverter.ConvertFromString(hex);
        ApplicationAccentColorManager.Apply(
            color,
            _mainVm.IsDarkTheme ? ApplicationTheme.Dark : ApplicationTheme.Light);
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var dlg = new OpenFolderDialog
        {
            Title            = "Select default download folder",
            InitialDirectory = vm.DefaultDownloadFolder,
        };
        if (dlg.ShowDialog() == true)
            vm.DefaultDownloadFolder = dlg.FolderName;
    }

    private void Licenses_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new LicensesDialog { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
    }

    private void CookiesBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Select cookies.txt file",
            Filter = "Cookies file (*.txt)|*.txt|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true)
            vm.CookiesFilePath = dlg.FileName;
    }

    private void CookiesClear_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        vm.CookiesFilePath  = "";
        vm.CookiesExpanded  = false;
    }

    private void LinkedIn_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "https://www.linkedin.com/in/sagar-vishwakarma-b1a015129")
            { UseShellExecute = true });
    }

    private async void ChangeKey_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
            await app.ChangeKeyAsync();
    }
}
