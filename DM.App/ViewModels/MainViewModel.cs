using CommunityToolkit.Mvvm.ComponentModel;

namespace DM.App.ViewModels;

// ObservableObject from CommunityToolkit gives us INotifyPropertyChanged for free.
// Decorate fields with [ObservableProperty] to auto-generate public properties that
// fire change notifications — no boilerplate needed.
public partial class MainViewModel : ObservableObject
{
    // Placeholder — commands and observable properties added per feature.
}
