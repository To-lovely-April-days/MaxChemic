// Converters/BoolToGeometryConverter.cs
using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace MaxChemical.Modules.Designer.Converters
{
    public class BoolToGeometryConverter : IValueConverter
    {
        public string TrueValue { get; set; }
        public string FalseValue { get; set; }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                var geometryString = boolValue ? TrueValue : FalseValue;
                return Geometry.Parse(geometryString);
            }
            return Geometry.Parse(FalseValue);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}