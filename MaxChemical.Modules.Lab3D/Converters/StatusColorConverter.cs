using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MaxChemical.Modules.Lab3D.Converters;

public sealed class StatusColorConverter : IValueConverter
{
    public static StatusColorConverter Instance { get; } = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value as string == "已连接"
            ? new SolidColorBrush(Color.FromRgb(29, 158, 117))
            : new SolidColorBrush(Color.FromRgb(196, 38, 38));

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
