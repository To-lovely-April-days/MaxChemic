using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using MaxChemical.Modules.Lab3D.Models;

namespace MaxChemical.Modules.Lab3D.Converters;

public sealed class Lab3DStateBrushConverter : IValueConverter
{
    private static readonly Brush Online = Create(0x12, 0x70, 0x4F);
    private static readonly Brush Stale = Create(0x8A, 0x5A, 0x00);
    private static readonly Brush Brand = Create(0xA4, 0x16, 0x26);
    private static readonly Brush BorderStrong = Create(0xCF, 0xCB, 0xC4);
    private static readonly Brush SurfaceSunk = Create(0xF1, 0xEF, 0xEA);
    private static readonly Brush TextPrimary = Create(0x1F, 0x23, 0x26);
    private static readonly Brush TextSecondary = Create(0x5A, 0x5F, 0x63);
    private static readonly Brush Placeholder = Create(0x7A, 0x7E, 0x82);

    public static Lab3DStateBrushConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value switch
        {
            Lab3DSignalState state => Lab3DSignalStates.ToKey(state),
            string text => text.Trim().ToLowerInvariant(),
            _ => "off"
        };
        string role = (parameter as string ?? "line").Trim().ToLowerInvariant();
        return key switch
        {
            "live" => role == "text" ? TextPrimary : Online,
            "stale" => Stale,
            "alert" => Brand,
            "done" => BorderStrong,
            "todo" => SurfaceSunk,
            _ => role switch
            {
                "fill" => Brushes.Transparent,
                "text" => Placeholder,
                "status" => TextSecondary,
                _ => BorderStrong
            }
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static SolidColorBrush Create(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
