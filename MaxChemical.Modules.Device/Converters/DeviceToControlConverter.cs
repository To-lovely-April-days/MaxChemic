using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
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
                                IsHitTestVisible = false,
                                Cursor = System.Windows.Input.Cursors.Hand
                            };

                            var flaskViewbox = new System.Windows.Controls.Viewbox
                            {
                                Width = 40,
                                Height = 40,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                StretchDirection = System.Windows.Controls.StretchDirection.Both,
                                Child = flaskControl,
                                IsHitTestVisible = true
                            };

                            return flaskViewbox;
                        #endregion

                        #region 柱塞泵控件
                        case "PistonPumpControl":
                            var pumpControl = new PistonPumpControl
                            {
                                IsHitTestVisible = false,
                                Cursor = System.Windows.Input.Cursors.Hand
                            };

                            var pumpViewbox = new System.Windows.Controls.Viewbox
                            {
                                Width = 40,
                                Height = 40,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                StretchDirection = System.Windows.Controls.StretchDirection.Both,
                                Child = pumpControl,
                                IsHitTestVisible = true
                            };

                            return pumpViewbox;
                        #endregion

                        #region 阀门控件
                        case "ValveControl":
                            var valveControl = new ValveControl
                            {
                                IsHitTestVisible = false,
                                Cursor = System.Windows.Input.Cursors.Hand,
                                IsOpen = false
                            };

                            var valveViewbox = new System.Windows.Controls.Viewbox
                            {
                                Width = 40,
                                Height = 40,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                StretchDirection = System.Windows.Controls.StretchDirection.Both,
                                Child = valveControl,
                                IsHitTestVisible = true
                            };

                            return valveViewbox;
                        #endregion

                        #region 电磁阀控件
                        case "SolenoidValveControl":
                            var solenoidValveControl = new SolenoidValveControl
                            {
                                IsHitTestVisible = false,
                                Cursor = System.Windows.Input.Cursors.Hand,
                                IsOpen = false
                            };

                            var solenoidValveViewbox = new System.Windows.Controls.Viewbox
                            {
                                Width = 40,
                                Height = 40,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                StretchDirection = System.Windows.Controls.StretchDirection.Both,
                                Child = solenoidValveControl,
                                IsHitTestVisible = true
                            };

                            return solenoidValveViewbox;
                        #endregion

                        #region 流量计控件
                        case "MassFlowMeterControl":
                            var meterControl = new MassFlowMeterControl
                            {
                                IsHitTestVisible = false,
                                Cursor = System.Windows.Input.Cursors.Hand,
                                CurrentFlow = 0.0,
                                TargetFlow = 20.0,
                                MaxFlow = 150.0,
                                IsFlowing = false
                            };

                            var meterViewbox = new System.Windows.Controls.Viewbox
                            {
                                Width = 40,
                                Height = 40,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                StretchDirection = System.Windows.Controls.StretchDirection.Both,
                                Child = meterControl,
                                IsHitTestVisible = true
                            };

                            return meterViewbox;
                        #endregion

                        #region 微反应器控件
                        case "MicroreactorControl":
                            var reactorControl = new MicroreactorControl
                            {
                                IsHitTestVisible = false,
                                Cursor = System.Windows.Input.Cursors.Hand,
                                Temperature1 = 85.5,
                                Temperature2 = 92.3,
                                Temperature3 = 98.7,
                                IsReacting = false
                            };

                            var reactorViewbox = new System.Windows.Controls.Viewbox
                            {
                                Width = 40,
                                Height = 40,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                StretchDirection = System.Windows.Controls.StretchDirection.Both,
                                Child = reactorControl,
                                IsHitTestVisible = true
                            };

                            return reactorViewbox;
                        #endregion

                        #region 管式反应器控件
                        case "TubularReactorControl":
                            var tubularReactorControl = new TubularReactorControl
                            {
                                IsHitTestVisible = false,
                                Cursor = System.Windows.Input.Cursors.Hand,
                                Temperature = 0.0,
                                Pressure = 0.0,
                                IsReacting = false
                            };

                            var tubularReactorViewbox = new System.Windows.Controls.Viewbox
                            {
                                Width = 40,
                                Height = 40,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                StretchDirection = System.Windows.Controls.StretchDirection.Both,
                                Child = tubularReactorControl,
                                IsHitTestVisible = true
                            };

                            return tubularReactorViewbox;
                        #endregion

                        #region 换热器控件
                        case "HeatExchangerControl":
                            var exchangerControl = new HeatExchangerControl
                            {
                                IsHitTestVisible = false,
                                Cursor = System.Windows.Input.Cursors.Hand,
                                Temperature = 85.0,
                                Pressure = 2.5,
                                IsOperating = false
                            };

                            var exchangerViewbox = new System.Windows.Controls.Viewbox
                            {
                                Width = 40,
                                Height = 40,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                StretchDirection = System.Windows.Controls.StretchDirection.Both,
                                Child = exchangerControl,
                                IsHitTestVisible = true
                            };

                            return exchangerViewbox;
                        #endregion

                        case "DigitalScaleControl":
                            var scaleControl = new DigitalScaleControl
                            {
                                IsHitTestVisible = true,
                                Cursor = System.Windows.Input.Cursors.Hand,
                                IsPoweredOn = false,
                                WeightValue = 0.0,
                                CurrentUnit = "g",
                                IsTared = false
                            };

                            var ScaleViewbox = new System.Windows.Controls.Viewbox
                            {
                                Width = 45,
                                Height = 23,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                StretchDirection = System.Windows.Controls.StretchDirection.Both,
                                Child = scaleControl,
                                IsHitTestVisible = false
                            };

                            return ScaleViewbox;

                        case "TemperatureControlSystemControl":
                            var circulatorControl = new TemperatureControlSystemControl
                            {
                                IsHitTestVisible = true,
                                Cursor = System.Windows.Input.Cursors.Hand,
                                SetTemperature = -40.0,
                                CurrentTemperature = 24.5,
                            };

                            var circulatorViewbox = new System.Windows.Controls.Viewbox
                            {
                                Width = 40,
                                Height = 40,
                                Stretch = System.Windows.Media.Stretch.Uniform,
                                StretchDirection = System.Windows.Controls.StretchDirection.Both,
                                Child = circulatorControl,
                                IsHitTestVisible = true
                            };

                            return circulatorViewbox;

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