using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using RockcliffeCourtBooker.App.Models;

namespace RockcliffeCourtBooker.App.Converters;

public sealed class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null &&
        string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null)
        {
            return Binding.DoNothing;
        }

        return Enum.Parse(targetType, parameter.ToString()!, ignoreCase: true);
    }
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
public sealed class StatusToneToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            UiStatusTone.Success => "ToneSuccessBrush",
            UiStatusTone.Warning => "ToneWarningBrush",
            UiStatusTone.Error => "ToneErrorBrush",
            UiStatusTone.Info => "ToneInfoBrush",
            _ => "TextSecondaryBrush"
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.SlateGray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class StatusToneToBackgroundConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            UiStatusTone.Success => "ToneSuccessBackgroundBrush",
            UiStatusTone.Warning => "ToneWarningBackgroundBrush",
            UiStatusTone.Error => "ToneErrorBackgroundBrush",
            UiStatusTone.Info => "ToneInfoBackgroundBrush",
            _ => "SurfaceSubtleBrush"
        };

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
