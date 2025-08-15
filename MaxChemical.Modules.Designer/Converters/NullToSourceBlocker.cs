using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;

namespace MaxChemical.Modules.Designer.Converters
{
    // 当目标值为 null/空字符串时，不回写到源（返回 Binding.DoNothing）
    public class NullToSourceBlocker : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            // 源 -> 目标：原样传递
            return value;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return Binding.DoNothing;
            if (value is string s && string.IsNullOrWhiteSpace(s)) return Binding.DoNothing;
            return value;
        }
    }
}
