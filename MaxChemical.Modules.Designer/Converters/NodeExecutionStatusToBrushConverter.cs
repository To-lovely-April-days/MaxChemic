using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static MaxChemical.Modules.Designer.Models.CommandNode;
using System.Windows.Data;
using System.Windows.Media;

namespace MaxChemical.Modules.Designer.Converters
{
    public class NodeExecutionStatusToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is NodeExecutionStatus status)
            {
                return status switch
                {
                    NodeExecutionStatus.Idle => new SolidColorBrush(Colors.LightGray),
                    NodeExecutionStatus.Waiting => new SolidColorBrush(Colors.Orange),
                    NodeExecutionStatus.Executing => new SolidColorBrush(Colors.Blue),
                    NodeExecutionStatus.Completed => new SolidColorBrush(Colors.Green),
                    NodeExecutionStatus.Failed => new SolidColorBrush(Colors.Red),
                    NodeExecutionStatus.Skipped => new SolidColorBrush(Colors.Gray),
                    _ => new SolidColorBrush(Colors.LightGray)
                };
            }
            return new SolidColorBrush(Colors.LightGray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class NodeExecutionStatusToOpacityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is NodeExecutionStatus status)
            {
                return status switch
                {
                    NodeExecutionStatus.Idle => 1.0,
                    NodeExecutionStatus.Waiting => 0.8,
                    NodeExecutionStatus.Executing => 1.0,
                    NodeExecutionStatus.Completed => 0.9,
                    NodeExecutionStatus.Failed => 0.95,
                    NodeExecutionStatus.Skipped => 0.6,
                    _ => 1.0
                };
            }
            return 1.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
