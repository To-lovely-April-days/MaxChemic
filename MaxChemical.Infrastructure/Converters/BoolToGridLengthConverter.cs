using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace MaxChemical.Infrastructure.Converters
{
    public class BoolToGridLengthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isVisible && parameter is string widthStr)
            {
                if (double.TryParse(widthStr, out double width))
                {
                    return isVisible ? new GridLength(width) : new GridLength(0);
                }
            }
            return new GridLength(0);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}