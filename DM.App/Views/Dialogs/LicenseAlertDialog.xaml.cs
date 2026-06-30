using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace DM.App.Views.Dialogs;

public enum LicenseAlertType { Suspended, Revoked }

public partial class LicenseAlertDialog : Window
{
    private const string LinkedInUrl =
        "https://www.linkedin.com/in/sagar-vishwakarma-b1a015129";

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
        }
    }

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

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
        Application.Current.Shutdown();
    }
}
