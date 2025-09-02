// MaxChemical.Modules.Designer/Converters/ValueConverters.cs
using System;
using System.Globalization;
using System.Windows.Data;

namespace MaxChemical.Modules.Designer.Converters
{
    /// <summary>
    /// 将秒数转换为时间跨度字符串
    /// </summary>
    public class SecondsToTimeSpanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double seconds && seconds > 0)
            {
                var timeSpan = TimeSpan.FromSeconds(seconds);
                return timeSpan.ToString(@"hh\:mm\:ss");
            }
            return "00:00:00";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 布尔值转换为字符串
    /// </summary>
    public class BooleanToStringConverter : IValueConverter
    {
        public string TrueValue { get; set; } = "True";
        public string FalseValue { get; set; } = "False";

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? TrueValue : FalseValue;
            }
            return FalseValue;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string stringValue)
            {
                return stringValue == TrueValue;
            }
            return false;
        }
    }

    /// <summary>
    /// 空值转换为可见性
    /// </summary>
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value == null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 数值转换为百分比字符串
    /// </summary>
    public class PercentageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double doubleValue)
            {
                return $"{doubleValue:F1}%";
            }
            if (value is int intValue)
            {
                return $"{intValue:F1}%";
            }
            return "0.0%";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 毫秒转换为时间跨度字符串
    /// </summary>
    public class MillisecondsToTimeSpanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double milliseconds && milliseconds > 0)
            {
                var timeSpan = TimeSpan.FromMilliseconds(milliseconds);
                if (timeSpan.TotalHours >= 1)
                {
                    return timeSpan.ToString(@"hh\:mm\:ss\.fff");
                }
                else if (timeSpan.TotalMinutes >= 1)
                {
                    return timeSpan.ToString(@"mm\:ss\.fff");
                }
                else
                {
                    return timeSpan.ToString(@"ss\.fff") + "s";
                }
            }
            return "0.000s";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 状态转换为颜色
    /// </summary>
    public class StatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isSuccessful)
            {
                return isSuccessful ?
                    new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green) :
                    new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Red);
            }
            return new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}