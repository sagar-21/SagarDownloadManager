using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DM.App.Licensing;

namespace DM.App.ViewModels;

public partial class ActivationViewModel : ObservableObject
{
    private readonly LicenseService _svc;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ActivateCommand))]
    private string _licenseKey = "";

    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool   _isActivating  = false;
    [ObservableProperty] private bool   _hasError       = false;
    [ObservableProperty] private bool   _isSuccess      = false;
    [ObservableProperty] private bool   _showStatus     = false;

    partial void OnStatusMessageChanged(string value) => ShowStatus = !string.IsNullOrEmpty(value);

    // Computed once — WMI calls are cached by HardwareFingerprint
    public string MachineFingerprint { get; } = HardwareFingerprint.Compute();

    public event Action? ActivationSucceeded;

    public ActivationViewModel(LicenseService svc) => _svc = svc;

    private bool CanActivate =>
        !IsActivating && LicenseKey.Replace("-", "").Replace(" ", "").Length >= 14;

    [RelayCommand(CanExecute = nameof(CanActivate))]
    private async Task ActivateAsync()
    {
        IsActivating  = true;
        HasError      = false;
        IsSuccess     = false;
        StatusMessage = "Contacting license server…";

        var (ok, error) = await _svc.ActivateAsync(LicenseKey);

        IsActivating = false;

        if (ok)
        {
            IsSuccess     = true;
            StatusMessage = $"Activated! Welcome, {_svc.CustomerName ?? ""}";
            await Task.Delay(900); // brief visual confirmation before continuing
            ActivationSucceeded?.Invoke();
        }
        else
        {
            HasError      = true;
            StatusMessage = error ?? "Activation failed.";
        }
    }

    // Auto-format key as user types: insert dashes at positions 4, 9, 14
    partial void OnLicenseKeyChanged(string value)
    {
        var digits = value.Replace("-", "").Replace(" ", "").ToUpper();
        if (digits.Length > 16) digits = digits[..16];

        var formatted = digits.Length switch
        {
            <= 4  => digits,
            <= 8  => $"{digits[..4]}-{digits[4..]}",
            <= 12 => $"{digits[..4]}-{digits[4..8]}-{digits[8..]}",
            _     => $"{digits[..4]}-{digits[4..8]}-{digits[8..12]}-{digits[12..]}",
        };

        // Avoid re-entering the partial if the value hasn't actually changed
        if (formatted != value)
        {
            _licenseKey = formatted;
            OnPropertyChanged(nameof(LicenseKey));
            ActivateCommand.NotifyCanExecuteChanged();
        }
    }
}
