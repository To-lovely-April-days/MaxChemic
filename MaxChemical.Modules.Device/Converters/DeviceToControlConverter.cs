using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Controls;
using DevicePlugins.Devices;
using MaxChemical.Modules.Designer.Views.Controls;

namespace MaxChemical.Modules.Device.Converters
{
    /// <summary>
    /// 设备到控件转换器 - 根据设备的 ImageLocation 判断是使用自定义控件还是图片
    /// </summary>
    public class DeviceToControlConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is IDevice device && !string.IsNullOrEmpty(device.ImageLocation))
            {
                if (device.ImageLocation.StartsWith("CUSTOM_CONTROL:"))
                {
                    string controlType = device.ImageLocation.Replace("CUSTOM_CONTROL:", "");

                    switch (controlType)
                    {
                        case "ConicalFlaskControl":
                            var flaskControl = new ConicalFlaskControl
                            {
                                FlaskWidth = 80,
                                FlaskHeight = 120,
                                IsHitTestVisible = false,
                                Cursor = System.Windows.Input.Cursors.Hand
                            };

                            var flaskViewbox = new System.Windows.Controls.Viewbox
                            {
                                Width = 40,
                                Height = 40,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                StretchDirection = System.Windows.Controls.StretchDirection.Both,
                                Child = flaskControl
                            };

                            // 禁用锥形瓶的交互元素
                            flaskControl.Loaded += (s, e) =>
                            {
                                var flask = s as ConicalFlaskControl;
                                if (flask != null)
                                {
                                    var selectionBorder = flask.FindName("SelectionBorder") as System.Windows.Shapes.Rectangle;
                                    var topAnchor = flask.FindName("TopAnchor") as System.Windows.Shapes.Ellipse;
                                    var bottomAnchor = flask.FindName("BottomAnchor") as System.Windows.Shapes.Ellipse;
                                    var leftAnchor = flask.FindName("LeftAnchor") as System.Windows.Shapes.Ellipse;
                                    var rightAnchor = flask.FindName("RightAnchor") as System.Windows.Shapes.Ellipse;

                                    if (selectionBorder != null) selectionBorder.Visibility = System.Windows.Visibility.Collapsed;
                                    if (topAnchor != null) topAnchor.Visibility = System.Windows.Visibility.Collapsed;
                                    if (bottomAnchor != null) bottomAnchor.Visibility = System.Windows.Visibility.Collapsed;
                                    if (leftAnchor != null) leftAnchor.Visibility = System.Windows.Visibility.Collapsed;
                                    if (rightAnchor != null) rightAnchor.Visibility = System.Windows.Visibility.Collapsed;
                                }
                            };

                            return flaskViewbox;

                        // ✅ 新增：柱塞泵控件
                        case "PistonPumpControl":
                            var pumpControl = new PistonPumpControl
                            {
                                IsHitTestVisible = false,
                                Cursor = System.Windows.Input.Cursors.Hand
                            };

                            // 使用 Viewbox 包裹以实现缩放
                            var pumpViewbox = new System.Windows.Controls.Viewbox
                            {
                                Width = 40,  
                                Height = 40,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                StretchDirection = System.Windows.Controls.StretchDirection.Both,
                                Child = pumpControl
                            };

                            // ✅ 不隐藏按钮，保持完整显示
                            // 如果需要停止动画以节省性能，可以取消下面的注释
                            pumpControl.Loaded += (s, e) =>
                            {
                                var pump = s as PistonPumpControl;
                                if (pump != null)
                                {
                                    pump.StopPump();
                                }
                            };

                            return pumpViewbox;

                        default:
                            return null;
                    }
                }
            }

            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 判断设备是否使用自定义控件
    /// </summary>
    public class IsCustomControlConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is IDevice device && !string.IsNullOrEmpty(device.ImageLocation))
            {
                return device.ImageLocation.StartsWith("CUSTOM_CONTROL:");
            }
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}