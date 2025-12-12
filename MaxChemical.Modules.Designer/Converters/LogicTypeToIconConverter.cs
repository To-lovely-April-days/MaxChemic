using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MaxChemical.Modules.Designer.Models;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows;

namespace MaxChemical.Modules.Designer.Converters
{
    public class LogicTypeToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is LogicCommandType type)
            {
                return type switch
                {
                    LogicCommandType.If => "🔀",
                    LogicCommandType.Loop => "🔄",
                    LogicCommandType.Parallel => "⫸",
                    LogicCommandType.Wait => "",
                    LogicCommandType.WaitIf => "⏱",
                    LogicCommandType.SetVariable => "📝",
                    _ => "⚙"
                };
            }
            return "⚙";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class LogicTypeToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is LogicCommandType type)
            {
                var color = type switch
                {
                    LogicCommandType.If => Color.FromRgb(255, 193, 7),      // 黄色
                    LogicCommandType.Loop => Color.FromRgb(40, 167, 69),    // 绿色
                    LogicCommandType.Parallel => Color.FromRgb(102, 16, 242), // 紫色
                    LogicCommandType.Wait => Color.FromRgb(23, 162, 184),   // 青色
                    LogicCommandType.WaitIf => Color.FromRgb(255, 105, 180), // 粉色
                    LogicCommandType.SetVariable => Color.FromRgb(0, 120, 212), // 蓝色
              
                    _ => Color.FromRgb(108, 117, 125)                       // 灰色
                };
                return new SolidColorBrush(color);
            }
            return new SolidColorBrush(Color.FromRgb(108, 117, 125));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class LogicTypeToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool invert = parameter?.ToString() == "invert";
            bool isLogicType = value is LogicCommandType;

            if (invert)
                return isLogicType ? Visibility.Collapsed : Visibility.Visible;
            else
                return isLogicType ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BooleanAndToVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length == 0)
                return Visibility.Collapsed;

            bool result = true;
            foreach (var value in values)
            {
                if (value is bool boolValue)
                {
                    result = result && boolValue;
                }
                else if (value is Visibility visibility)
                {
                    result = result && (visibility == Visibility.Visible);
                }
                else
                {
                    // 如果是其他类型，尝试转换为布尔值
                    if (value != null && bool.TryParse(value.ToString(), out bool parsed))
                    {
                        result = result && parsed;
                    }
                    else
                    {
                        result = false;
                    }
                }
            }
            return result ? Visibility.Visible : Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // 添加一个简化的转换器用于判断是否有图片
    public class HasImageToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool invert = parameter?.ToString() == "invert";
            bool hasImage = !string.IsNullOrEmpty(value?.ToString());

            if (invert)
                return hasImage ? Visibility.Collapsed : Visibility.Visible;
            else
                return hasImage ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // 添加一个转换器用于判断是否是逻辑命令
    public class IsLogicCommandConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool invert = parameter?.ToString() == "invert";
            bool isLogicCommand = value is LogicCommand;

            if (invert)
                return isLogicCommand ? Visibility.Collapsed : Visibility.Visible;
            else
                return isLogicCommand ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BranchTypeToShortConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string branchType)
            {
                return branchType switch
                {
                    "TRUE" => "T",
                    "FALSE" => "F",
                    "LOOP" => "L",
                    "PARALLEL_A" => "A",
                    "PARALLEL_B" => "B",
                    "PARALLEL_C" => "C",
                    _ => "?"
                };
            }
            return "?";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
