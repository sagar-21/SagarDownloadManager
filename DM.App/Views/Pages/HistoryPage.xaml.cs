using System.Windows.Controls;
using DM.App.ViewModels;

namespace DM.App.Views.Pages;

public partial class HistoryPage : Page
{
    private readonly HistoryViewModel _vm;

    public HistoryPage(HistoryViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
        => _vm.SearchQuery = SearchBox.Text;

    private void ClearAll_Click(object sender, System.Windows.RoutedEventArgs e)
        => _vm.ClearHistoryCommand.Execute(null);
}
