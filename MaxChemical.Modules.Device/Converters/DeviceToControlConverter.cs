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
                        #region 锥形瓶控件
                        case "ConicalFlaskControl":
                            var flaskControl = new ConicalFlaskControl
                            {
                                FlaskWidth = 80,
                                FlaskHeight = 120,
                                IsHitTestVisible = true,  // ✅ 允许交互
                                Cursor = System.Windows.Input.Cursors.Hand
                            };

                            var flaskViewbox = new System.Windows.Controls.Viewbox
                            {
                                Width = 40,
                                Height = 40,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                StretchDirection = System.Windows.Controls.StretchDirection.Both,
                                Child = flaskControl,
                                IsHitTestVisible = true  // ✅ 允许交互
                            };

                            return flaskViewbox;
                        #endregion

                        #region 柱塞泵控件
                        case "PistonPumpControl":
                            var pumpControl = new PistonPumpControl
                            {
                                IsHitTestVisible = true,  // ✅ 允许交互
                                Cursor = System.Windows.Input.Cursors.Hand
                            };

                            var pumpViewbox = new System.Windows.Controls.Viewbox
                            {
                                Width = 40,
                                Height = 40,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                StretchDirection = System.Windows.Controls.StretchDirection.Both,
                                Child = pumpControl,
                                IsHitTestVisible = true  // ✅ 允许交互
                            };

                            return pumpViewbox;
                        #endregion

                        #region 阀门控件
                        case "ValveControl":
                            var valveControl = new ValveControl
                            {
                                IsHitTestVisible = true,  // ✅ 允许交互
                                Cursor = System.Windows.Input.Cursors.Hand,
                                IsOpen = false  // 初始为关闭状态
                            };

                            var valveViewbox = new System.Windows.Controls.Viewbox
                            {
                                Width = 40,
                                Height = 40,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                StretchDirection = System.Windows.Controls.StretchDirection.Both,
                                Child = valveControl,
                                IsHitTestVisible = true  // ✅ 允许交互
                            };

                            return valveViewbox;
                        #endregion

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