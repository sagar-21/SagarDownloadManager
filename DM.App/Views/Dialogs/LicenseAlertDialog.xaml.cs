using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DM.App.Views.Dialogs;

public enum LicenseAlertType { Suspended, Revoked, Expired }

public partial class LicenseAlertDialog : Window
{
    private const string LinkedInUrl =
        "https://www.linkedin.com/in/sagar-vishwakarma-b1a015129";

    /// <summary>Fires when the user clicks "Enter New Key" on the Expired dialog.</summary>
    public event Action? NewKeyRequested;

    /// <summary>Fires when the user clicks "Try Again" — does a heartbeat without deactivating.</summary>
    public event Action? RetryRequested;

    public LicenseAlertDialog(LicenseAlertType type)
    {
        InitializeComponent();
        Apply(type);
    }

    private void Apply(LicenseAlertType type)
    {
        if (type == LicenseAlertType.Suspended)
        {
            var orange = (Color)ColorConverter.ConvertFromString("#F59E0B");
            AccentBar.Background   = new SolidColorBrush(orange);
            IconBorder.Background  = new SolidColorBrush(Color.FromArgb(40, 245, 158, 11));
            IconText.Text          = "!";
            IconText.Foreground    = new SolidColorBrush(orange);
            TitleText.Text         = "License Suspended";
            MessageText.Text       =
                "Your license has been suspended by the administrator.\n\n" +
                "The application will now close. To reactivate your license " +
                "or for any queries, please contact support on LinkedIn.";
            SetLinkedInLabel("📎  Contact on LinkedIn");
        }
        else if (type == LicenseAlertType.Expired)
        {
            var amber = (Color)ColorConverter.ConvertFromString("#F97316");
            AccentBar.Background        = new SolidColorBrush(amber);
            IconBorder.Background       = new SolidColorBrush(Color.FromArgb(40, 249, 115, 22));
            IconText.Text               = "⌛";
            IconText.Foreground         = new SolidColorBrush(amber);
            TitleText.Text              = "License Expired";
            MessageText.Text            =
                "Your license has expired and the application can no longer run.\n\n" +
                "• Already extended on the admin website? Click \"Try Again\" below.\n" +
                "• Have a new key? Click \"Enter New Key\" to activate it.\n" +
                "• Need to renew? Click \"Request Extension\" to contact support.";
            TryAgainBtn.Visibility      = Visibility.Visible;
            ExpiredActionRow.Visibility = Visibility.Visible;
            LinkedInBtn.Visibility      = Visibility.Collapsed;
            CloseBtn.Content            = "Close";
        }
        else
        {
            var red = (Color)ColorConverter.ConvertFromString("#EF4444");
            AccentBar.Background   = new SolidColorBrush(red);
            IconBorder.Background  = new SolidColorBrush(Color.FromArgb(40, 239, 68, 68));
            IconText.Text          = "✕";
            IconText.Foreground    = new SolidColorBrush(red);
            TitleText.Text         = "License Revoked";
            MessageText.Text       =
                "Your license has been revoked and this installation has been disabled.\n\n" +
                "The application will now close. For further assistance, " +
                "please contact support on LinkedIn.";
            SetLinkedInLabel("📎  Contact on LinkedIn");
        }
    }

    private void SetLinkedInLabel(string content) => LinkedInBtn.Content = content;

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void LinkedIn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(LinkedInUrl) { UseShellExecute = true });
        }
        catch { }
    }

    private void TryAgain_Click(object sender, RoutedEventArgs e)
    {
        RetryRequested?.Invoke();
        Close();
    }

    private void NewKey_Click(object sender, RoutedEventArgs e)
    {
        NewKeyRequested?.Invoke();
        Close();
    }

    private void RequestExtension_Click(object sender, RoutedEventArgs e)
    {
        // Opens LinkedIn so the user can message the developer to request a license extension
        try
        {
            Process.Start(new ProcessStartInfo(LinkedInUrl) { UseShellExecute = true });
        }
        catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
        Application.Current.Shutdown();
    }
}
