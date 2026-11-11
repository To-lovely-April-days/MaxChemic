using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MaxChemical.Modules.Lab3D.Converters;

public sealed class ProtocolVisibilityConverter : IValueConverter
{
    public static ProtocolVisibilityConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string protocol = value as string ?? string.Empty;
        return !string.IsNullOrWhiteSpace(protocol)
            && protocol != "未绑定"
            && !string.Equals(protocol, "Custom / Local", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
