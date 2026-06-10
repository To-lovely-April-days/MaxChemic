using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using static MaxChemical.Modules.Designer.Models.CommandNode;

namespace MaxChemical.Modules.Designer.Converters
{
    /// <summary>
    /// 执行状态 → 图标区背景色
    /// Idle=#F5F5F5, Waiting=#FFF3E0, Executing=#1976D2, Completed=#43A047, Failed=#E53935, Skipped=#9E9E9E
    /// </summary>
    public class StatusToIconBackgroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is NodeExecutionStatus status)
            {
                return status switch
                {
                    NodeExecutionStatus.Idle     => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5F5F5")),
                    NodeExecutionStatus.Waiting  => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFF3E0")),
                    NodeExecutionStatus.Executing => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1976D2")),
                    NodeExecutionStatus.Completed => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#43A047")),
                    NodeExecutionStatus.Failed   => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E53935")),
                    NodeExecutionStatus.Skipped  => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9E9E9E")),
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5F5F5"))
                };
            }
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5F5F5"));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// 执行状态 → 图标区前景色（图标/文字颜色）
    /// Idle/Waiting 深色图标，Executing/Completed/Failed/Skipped 白色图标
    /// </summary>
    public class StatusToIconForegroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is NodeExecutionStatus status)
            {
                return status switch
                {
                    NodeExecutionStatus.Idle     => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#757575")),
                    NodeExecutionStatus.Waiting  => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E65100")),
                    NodeExecutionStatus.Executing => Brushes.White,
                    NodeExecutionStatus.Completed => Brushes.White,
                    NodeExecutionStatus.Failed   => Brushes.White,
                    NodeExecutionStatus.Skipped  => Brushes.White,
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#757575"))
                };
            }
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#757575"));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// 执行状态 → 卡片背景色（极淡底色）
    /// </summary>
    public class StatusToCardBackgroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is NodeExecutionStatus status)
            {
                return status switch
                {
                    NodeExecutionStatus.Idle     => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FAFAFA")),
                    NodeExecutionStatus.Waiting  => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFBF5")),
                    NodeExecutionStatus.Executing => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5F9FF")),
                    NodeExecutionStatus.Completed => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F8FCF7")),
                    NodeExecutionStatus.Failed   => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFFAF9")),
                    NodeExecutionStatus.Skipped  => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FAFAFA")),
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FAFAFA"))
                };
            }
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FAFAFA"));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// 执行状态 → 卡片边框色
    /// </summary>
    public class StatusToCardBorderConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is NodeExecutionStatus status)
            {
                return status switch
                {
                    NodeExecutionStatus.Idle     => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0E0E0")),
                    NodeExecutionStatus.Waiting  => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFE0B2")),
                    NodeExecutionStatus.Executing => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BBDEFB")),
                    NodeExecutionStatus.Completed => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C8E6C9")),
                    NodeExecutionStatus.Failed   => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFCDD2")),
                    NodeExecutionStatus.Skipped  => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0E0E0")),
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0E0E0"))
                };
            }
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E0E0E0"));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// 执行状态 → 标题文字色
    /// </summary>
    public class StatusToTitleForegroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is NodeExecutionStatus status)
            {
                return status switch
                {
                    NodeExecutionStatus.Idle     => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#424242")),
                    NodeExecutionStatus.Waiting  => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E65100")),
                    NodeExecutionStatus.Executing => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0D47A1")),
                    NodeExecutionStatus.Completed => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1B5E20")),
                    NodeExecutionStatus.Failed   => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B71C1C")),
                    NodeExecutionStatus.Skipped  => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#616161")),
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#424242"))
                };
            }
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#424242"));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// 执行状态 → 副标题/摘要文字色
    /// </summary>
    public class StatusToSubtitleForegroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is NodeExecutionStatus status)
            {
                return status switch
                {
                    NodeExecutionStatus.Idle     => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9E9E9E")),
                    NodeExecutionStatus.Waiting  => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF9800")),
                    NodeExecutionStatus.Executing => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1976D2")),
                    NodeExecutionStatus.Completed => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#43A047")),
                    NodeExecutionStatus.Failed   => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E53935")),
                    NodeExecutionStatus.Skipped  => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9E9E9E")),
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9E9E9E"))
                };
            }
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9E9E9E"));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// 执行状态 → Badge 背景色
    /// </summary>
    public class StatusToBadgeBackgroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is NodeExecutionStatus status)
            {
                return status switch
                {
                    NodeExecutionStatus.Idle     => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5F5F5")),
                    NodeExecutionStatus.Waiting  => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFF3E0")),
                    NodeExecutionStatus.Executing => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E3F2FD")),
                    NodeExecutionStatus.Completed => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E8F5E9")),
                    NodeExecutionStatus.Failed   => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFEBEE")),
                    NodeExecutionStatus.Skipped  => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5F5F5")),
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5F5F5"))
                };
            }
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5F5F5"));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// 执行状态 → Badge 文字色
    /// </summary>
    public class StatusToBadgeForegroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is NodeExecutionStatus status)
            {
                return status switch
                {
                    NodeExecutionStatus.Idle     => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9E9E9E")),
                    NodeExecutionStatus.Waiting  => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E65100")),
                    NodeExecutionStatus.Executing => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1565C0")),
                    NodeExecutionStatus.Completed => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2E7D32")),
                    NodeExecutionStatus.Failed   => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C62828")),
                    NodeExecutionStatus.Skipped  => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#757575")),
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9E9E9E"))
                };
            }
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9E9E9E"));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// 执行状态 → Badge 短文字（IDLE / WAIT / RUN / OK / ERR / SKIP）
    /// </summary>
    public class StatusToBadgeTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is NodeExecutionStatus status)
            {
                return status switch
                {
                    NodeExecutionStatus.Idle     => "IDLE",
                    NodeExecutionStatus.Waiting  => "WAIT",
                    NodeExecutionStatus.Executing => "RUN",
                    NodeExecutionStatus.Completed => "OK",
                    NodeExecutionStatus.Failed   => "ERR",
                    NodeExecutionStatus.Skipped  => "SKIP",
                    _ => "—"
                };
            }
            return "—";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// 执行状态 → 进度条可见性（仅 Executing 和 Completed 时显示）
    /// </summary>
    public class StatusToProgressVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is NodeExecutionStatus status)
            {
                return (status == NodeExecutionStatus.Executing || status == NodeExecutionStatus.Completed)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// 执行状态 → 进度条颜色
    /// </summary>
    public class StatusToProgressBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is NodeExecutionStatus status)
            {
                return status switch
                {
                    NodeExecutionStatus.Executing => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1976D2")),
                    NodeExecutionStatus.Completed => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#43A047")),
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1976D2"))
                };
            }
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1976D2"));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// 执行状态 → 耗时文字颜色
    /// </summary>
    public class StatusToDurationForegroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is NodeExecutionStatus status)
            {
                return status switch
                {
                    NodeExecutionStatus.Completed => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2E7D32")),
                    NodeExecutionStatus.Failed   => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#C62828")),
                    _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#757575"))
                };
            }
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString("#757575"));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
