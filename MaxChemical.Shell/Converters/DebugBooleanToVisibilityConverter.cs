using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MaxChemical.Shell.Converters
{
    public class DebugBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool boolValue = value is bool b && b;
            Visibility result = boolValue ? Visibility.Visible : Visibility.Collapsed;

            string paramStr = parameter?.ToString() ?? "Unknown";
            Debug.WriteLine($"[转换器] {paramStr}: {value} -> {result}");

            return result;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is Visibility visibility && visibility == Visibility.Visible;
        }
    }
}