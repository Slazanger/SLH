using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SLH.Services;

namespace SLH.Converters;

public sealed class HexColorToBrushConverter : IValueConverter
{
    public static readonly HexColorToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string { Length: > 0 } s && Color.TryParse(s, out var c))
            return new SolidColorBrush(c);
        return new SolidColorBrush(Color.Parse(EveStandingColors.DefaultText));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
