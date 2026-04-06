using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace SLH.Converters;

public sealed class BoolBrushConverter : IValueConverter
{
    /// <summary>Highlight border for the current UTC hour column on the zKill activity strip.</summary>
    public static readonly BoolBrushConverter HeatmapCurrentHourOutline = new()
    {
        WhenTrue = new SolidColorBrush(Color.Parse("#00E5FF")),
        WhenFalse = Brushes.Transparent
    };

    public IBrush WhenTrue { get; set; } = new SolidColorBrush(Color.Parse("#22c55e"));
    public IBrush WhenFalse { get; set; } = new SolidColorBrush(Color.Parse("#52525b"));

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? WhenTrue : WhenFalse;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
