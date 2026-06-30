using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DM.Core.Models;
using DM.Core.Queue;

namespace DM.App.Converters;

[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not Visibility.Visible;
}

[ValueConversion(typeof(double), typeof(string))]
public sealed class ProgressToPercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d ? $"{d * 100:F0}%" : "0%";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ── Status badge background ────────────────────────────────────────────────
// All brushes are frozen static singletons allocated once at class load.
// With RefreshDerived() firing on every progress tick, avoiding per-call
// allocation here meaningfully reduces GC pressure during active downloads.

[ValueConversion(typeof(DownloadStatus), typeof(Brush))]
public sealed class StatusToBadgeBackgroundConverter : IValueConverter
{
    private static readonly SolidColorBrush s_downloading = B(Color.FromArgb(48, 100, 149, 237));
    private static readonly SolidColorBrush s_paused      = B(Color.FromArgb(48, 255, 165,   0));
    private static readonly SolidColorBrush s_completed   = B(Color.FromArgb(48,  76, 175,  80));
    private static readonly SolidColorBrush s_failed      = B(Color.FromArgb(48, 244,  67,  54));
    private static readonly SolidColorBrush s_cancelled   = B(Color.FromArgb(40, 160, 160, 160));
    private static readonly SolidColorBrush s_queued      = B(Color.FromArgb(36, 130, 130, 150));

    private static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DownloadStatus s ? s switch
        {
            DownloadStatus.Downloading => s_downloading,
            DownloadStatus.Paused      => s_paused,
            DownloadStatus.Completed   => s_completed,
            DownloadStatus.Failed      => s_failed,
            DownloadStatus.Cancelled   => s_cancelled,
            _                          => s_queued,
        } : s_queued;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ── Status badge foreground ────────────────────────────────────────────────

[ValueConversion(typeof(DownloadStatus), typeof(Brush))]
public sealed class StatusToForegroundConverter : IValueConverter
{
    private static readonly SolidColorBrush s_downloading = B(Color.FromRgb(100, 149, 237));
    private static readonly SolidColorBrush s_paused      = B(Color.FromRgb(255, 165,   0));
    private static readonly SolidColorBrush s_completed   = B(Color.FromRgb( 76, 175,  80));
    private static readonly SolidColorBrush s_failed      = B(Color.FromRgb(244,  67,  54));
    private static readonly SolidColorBrush s_cancelled   = B(Color.FromRgb(160, 160, 160));
    private static readonly SolidColorBrush s_queued      = B(Color.FromRgb(160, 160, 175));

    private static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DownloadStatus s ? s switch
        {
            DownloadStatus.Downloading => s_downloading,
            DownloadStatus.Paused      => s_paused,
            DownloadStatus.Completed   => s_completed,
            DownloadStatus.Failed      => s_failed,
            DownloadStatus.Cancelled   => s_cancelled,
            _                          => s_queued,
        } : Brushes.White;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ── Priority badge color ───────────────────────────────────────────────────

[ValueConversion(typeof(DownloadPriority), typeof(Brush))]
public sealed class PriorityToColorConverter : IValueConverter
{
    private static readonly SolidColorBrush s_high   = B(Color.FromArgb(160, 230,  80,  80));
    private static readonly SolidColorBrush s_normal = B(Color.FromArgb(120, 100, 149, 237));
    private static readonly SolidColorBrush s_low    = B(Color.FromArgb(100, 120, 120, 140));

    private static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is DownloadPriority p ? p switch
        {
            DownloadPriority.High => s_high,
            DownloadPriority.Low  => s_low,
            _                     => s_normal,
        } : s_normal;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ── Int-to-Visibility (0 → Collapsed, else Visible) ──────────────────────

[ValueConversion(typeof(int), typeof(Visibility))]
public sealed class IntToInverseVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int n && n == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ── IntEquals: Visible when int value == ConverterParameter, else Collapsed ──

[ValueConversion(typeof(int), typeof(Visibility))]
public sealed class IntEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int v && parameter is string p && int.TryParse(p, out int expected))
            return v == expected ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

// ── BoolToOpacity: 1.0 when true, 0.38 when false (disabled-look) ─────────

public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? 1.0 : 0.38;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
