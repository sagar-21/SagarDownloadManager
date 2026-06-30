using System.Windows.Controls;
using DM.App.ViewModels;

namespace DM.App.Views.Pages;

public partial class DownloadsPage : Page
{
    public DownloadsPage(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
