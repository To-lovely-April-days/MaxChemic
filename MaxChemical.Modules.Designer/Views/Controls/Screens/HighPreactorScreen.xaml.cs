using DevicePlugins.Devices;
using Google.Protobuf.WellKnownTypes;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Security.Policy;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace MaxChemical.Modules.Designer.Views.Controls.Screens
{
    /// <summary>
    /// HighPreactorScreen.xaml 的交互逻辑
    /// </summary>
    public partial class HighPreactorScreen : DeviceScreenBase
    {
        private bool? _isStart;
        private bool _isStartSpeed = false;

        private bool? _isHeat;
        private bool _isHeating = false;

        public HighPreactorScreen()
        {
            InitializeComponent();
        }

        protected override string PollCommandName => "获取可视化界面参数";

        protected override void OnDataRefreshed(DeviceParameters output)
        {
            // 釜温 SV
            var value = output.GetNullableValue<double>("BathTemperatureSv");
            if (!TbBathSv.IsKeyboardFocusWithin)
            {
                // 判断当前文本框是否有焦点，没有则更新显示
                TbBathSv.Text= value.HasValue ? value.Value.ToString("F2") : "--";
            }
            // 釜温 PV
            value = output.GetNullableValue<double>("BathTemperaturePv");
            TbBathPv.Text = value.HasValue ? value.Value.ToString("F2") : "--";
            // 压力
            value = output.GetNullableValue<double>("Pressure");
            TbPressure.Text= value.HasValue ? value.Value.ToString("F2") : "--";
            // 搅拌 SV
            value = output.GetNullableValue<double>("SpeedSv");
            if (!TbSpeedSv.IsKeyboardFocusWithin)
            {
                // 判断当前文本框是否有焦点，没有则更新显示
                TbSpeedSv.Text= value.HasValue ? value.Value.ToString("F2") : "--";
            }
            // 搅拌 PV
            value = output.GetNullableValue<double>("SpeedPv");
            TbSpeedPv.Text= value.HasValue ? value.Value.ToString("F2") : "--";
            // 炉温 PV
            value = output.GetNullableValue<double>("FurnacePv");
            TbFurnacePv.Text= value.HasValue ? value.Value.ToString("F2") : "--";
            // 炉温 SV
            value = output.GetNullableValue<double>("BathTemperatureSv");
            if (!TbFurnaceSv.IsKeyboardFocusWithin)
            {
                // 判断当前文本框是否有焦点，没有则更新显示
                TbFurnaceSv.Text = value.HasValue ? value.Value.ToString("F2") : "--";
            }
            // 升温斜率
            value = output.GetNullableValue<double>("Slope");
            if (!TbSlope.IsKeyboardFocusWithin)
                TbSlope.Text= value.HasValue ? value.Value.ToString("F2") : "--";
            // 转换拟合阈值
            value = output.GetNullableValue<double>("Threshold");
            if (!TbThreshold.IsKeyboardFocusWithin)
                TbThreshold.Text = value.HasValue ? value.Value.ToString("F2") : "--";
            // 当前段运行时间
            value = output.GetNullableValue<double>("SectionRunTime");
            TbSectionRunTime.Text = value.HasValue ? value.Value.ToString("F1") : "--";
            // 当前段运行总时间
            value = output.GetNullableValue<double>("SectionRunTotal");
            TbTotalTime.Text = value.HasValue ? value.Value.ToString("F1") : "--";
            // 当前运行段
            int? val = output.GetNullableValue<int>("RunningSection");
            TbSection.Text = value.HasValue ? value.Value.ToString() : "--";
            // 已运行时间
            val = output.GetNullableValue<int>("RunTime");
            TbRunTime.Text = value.HasValue ? value.Value.ToString() : "--";

            // 控制模式
            ushort mode = output.GetValue<ushort>("CtrlMode");
            CtrlModelProcess(mode);

            if (!_isStartSpeed)
            {
                _isStart = output.GetNullableValue<bool>("Start/Stop");
                // 正在搅拌
                if (_isStart.HasValue && _isStart.Value)
                {
                    BtnSpeed.Background = System.Windows.Media.Brushes.White;
                    BtnSpeed.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 33, 176, 33));
                }
                else
                {
                    BtnSpeed.Background = System.Windows.Media.Brushes.LightGray;
                    BtnSpeed.Foreground = System.Windows.Media.Brushes.Red;
                }
            }

            if (!_isHeating)
            {
                _isHeat = output.GetNullableValue<bool>("Open/Close");
                // 正在加热
                if (_isHeat.HasValue && _isHeat.Value)
                {
                    BtnHeat.Background = System.Windows.Media.Brushes.White;
                    BtnHeat.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 33, 176, 33));
                }
                else
                {
                    BtnHeat.Background = System.Windows.Media.Brushes.LightGray;
                    BtnHeat.Foreground = System.Windows.Media.Brushes.Red;
                }
            }
        }

        protected override void OnPollError(Exception ex)
        {
            if (!TbBathSv.IsKeyboardFocusWithin)
            {
                TbBathSv.Text = "--";
            }
            TbBathPv.Text = "--";

            TbPressure.Text = "--";

            if (!TbSpeedSv.IsKeyboardFocusWithin)
            {
                TbSpeedSv.Text = "--";
            }
            TbSpeedPv.Text = "--";

            TbFurnacePv.Text = "--";
            TbFurnaceSv.Text = "--";

            TbCtrlMode.Text = "";

            TbRunTime.Text = "--";

            TbSlope.Text = "--";

            TbThreshold.Text = "--";

            TbSection.Text = "--";

            TbSectionRunTime.Text = "--";

            TbTotalTime.Text = "--";
        }

        /// <summary>
        /// 釜温 SV 设定
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void TbBathSv_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }
            if (!float.TryParse(TbBathSv.Text,out float value))
            {
                TbBathSv.Text = "";
                return;
            }
            var input = new Dictionary<string, object>()
            {
                ["TargetTemperature"] = value
            };
            await ExecuteCommandAsync("设置设定温度", input);

            // 清除文本框焦点
            Keyboard.ClearFocus();
        }

        /// <summary>
        /// 搅拌速度 SV 设定
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void TbSpeedSv_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            if (!float.TryParse(TbSpeedSv.Text,out float value))
            {
                TbSpeedSv.Text = "";
                return;
            }

            var input = new Dictionary<string, object>()
            {
                ["TargetSpeed"] = value
            };

            await ExecuteCommandAsync("设置搅拌速度 SV", input);

            // 清除文本框焦点
            Keyboard.ClearFocus();
        }

        /// <summary>
        /// 处理不同的控制模式
        /// </summary>
        /// <param name="value"></param>
        private void CtrlModelProcess(ushort value)
        {
            switch (value)
            {
                case 1: 
                    TbCtrlMode.Text = "斜率控制";
                    GridTempCtrl.Visibility = Visibility.Collapsed;
                    GridProgramCtrl.Visibility = Visibility.Collapsed;
                    GridSlopeCtrl.Visibility = Visibility.Visible;
                    TbBathSv.Visibility = Visibility.Visible;
                    TbFurnaceSv.Visibility = Visibility.Collapsed;
                    break;
                case 3: 
                    TbCtrlMode.Text = "程序控制";
                    GridTempCtrl.Visibility = Visibility.Collapsed;
                    GridProgramCtrl.Visibility = Visibility.Visible;
                    GridSlopeCtrl.Visibility = Visibility.Collapsed;
                    TbBathSv.Visibility = Visibility.Visible;
                    TbFurnaceSv.Visibility = Visibility.Collapsed;
                    break;
                case 4: 
                    TbCtrlMode.Text =  "炉温控制";
                    GridTempCtrl.Visibility = Visibility.Visible;
                    GridProgramCtrl.Visibility = Visibility.Collapsed;
                    GridSlopeCtrl.Visibility = Visibility.Collapsed;
                    TbBathSv.Visibility = Visibility.Collapsed;
                    TbFurnaceSv.Visibility = Visibility.Visible;
                    break;
                case 0:
                default:
                    TbCtrlMode.Text = "釜温控制";
                    GridTempCtrl.Visibility = Visibility.Visible;
                    GridProgramCtrl.Visibility = Visibility.Collapsed;
                    GridSlopeCtrl.Visibility = Visibility.Collapsed;
                    TbBathSv.Visibility = Visibility.Visible;
                    TbFurnaceSv.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        /// <summary>
        /// 加热按钮处理事件
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void BtnHeat_Click(object sender, RoutedEventArgs e)
        {
            if (_isHeating)
            {
                return;
            }

            if (_isHeat.HasValue)
            {
                _isHeating = true;
                // 是否已在加热
                if (_isHeat.Value)
                {
                    // 停止加热
                    var input = new Dictionary<string, object>()
                    {
                        ["Heat"] = 0
                    };
                    await ExecuteCommandAsync("加热开/关", input);
                }
                else
                {
                    // 开始加热
                    var input = new Dictionary<string, object>()
                    {
                        ["Heat"] = 1
                    };
                    await ExecuteCommandAsync("加热开/关", input);
                }
                _isHeating = false;
            }
        }

        /// <summary>
        /// 搅拌按钮处理事件
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void BtnSpeed_Click(object sender, RoutedEventArgs e)
        {
            if (_isStartSpeed)
            {
                return;
            }

            if (_isStart.HasValue)
            {
                _isStartSpeed = true;
                // 是否启动
                if (_isStart.Value)
                {
                    // 停止
                    var input = new Dictionary<string, object>()
                    {
                        ["Start/Stop"] = 0
                    };
                    await ExecuteCommandAsync("搅拌启动/停止", input);
                }
                else
                {
                    // 启动
                    var input = new Dictionary<string, object>()
                    {
                        ["Start/Stop"] = 1
                    };
                    await ExecuteCommandAsync("搅拌启动/停止", input);
                }
                _isStartSpeed = false;
            }
        }

        /// <summary>
        /// 设置升温斜率
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void TbSlope_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }
            if (!float.TryParse(TbSlope.Text, out float value))
            {
                TbSlope.Text = "";
                return;
            }
            var input = new Dictionary<string, object>()
            {
                ["TargetValue"] = value
            };
            await ExecuteCommandAsync("设置升温斜率", input);

            // 清除文本框焦点
            Keyboard.ClearFocus();
        }

        /// <summary>
        /// 设置转换拟合阈值
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void TbThreshold_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }
            if (!float.TryParse(TbThreshold.Text, out float value))
            {
                TbThreshold.Text = "";
                return;
            }
            var input = new Dictionary<string, object>()
            {
                ["TargetValue"] = value
            };
            await ExecuteCommandAsync("设置转换拟合阈值", input);

            // 清除文本框焦点
            Keyboard.ClearFocus();
        }
    }
}
