using DevicePlugins.Devices;
using System;
using System.Collections.Generic;
using System.Linq;
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
    /// SiphonInfusionPumpScreen.xaml 的交互逻辑
    /// </summary>
    public partial class SiphonInfusionPumpScreen : DeviceScreenBase
    {
        public SiphonInfusionPumpScreen()
        {
            InitializeComponent();
        }

        protected override string PollCommandName => "获取所有数据";

        protected override void OnDataRefreshed(DeviceParameters output)
        {
            // 更新压力值
            double pressure = output.GetValue<double>("Pressure");
            TbPressure.Text = pressure.ToString();

            // 更新流量值
            double flow = output.GetValue<double>("Flow");
            if (!TbFlow.IsKeyboardFocusWithin)
                TbFlow.Text = flow.ToString();

            // 更新运行按钮状态
            ushort run = output.GetValue<ushort>("Run");
            ushort stop = output.GetValue<ushort>("Stop");
            if (run == 1 && stop != 1)
            {
                SwitchRunOrStop(false);
            }
            else if (run != 1 && stop == 1)
            {
                SwitchRunOrStop(true);
            }
        }

        /// <summary>
        /// 启动/停止泵
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void BtnRunStop_Click(object sender, RoutedEventArgs e)
        {
            bool success = false;
            if (BtnRunStop.Content.ToString() == "运行")
            {
                success = await ExecuteCommandAsync("启动泵");
                if (success)
                {
                    // 设置按钮为停止状态
                    SwitchRunOrStop(false);
                }
                return;
            }

            if (BtnRunStop.Content.ToString() == "停止")
            {
                success = await ExecuteCommandAsync("停止泵");
                if (success)
                {
                    // 设置按钮为运行状态
                    SwitchRunOrStop(true);
                }
            }
        }

        /// <summary>
        /// 执行按钮运行状态的切换
        /// </summary>
        /// <param name="isRun">指示按钮的运行状态</param>
        private void SwitchRunOrStop(bool isRun)
        {
            BtnRunStop.Content = isRun ? "运行" : "停止";
        }

        /// <summary>
        /// 重置压力，将压力值清零
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        /// <returns></returns>
        private async void ResetPressure_Click(object sender, RoutedEventArgs e)
        {
            if (await ExecuteCommandAsync("重置压力"))
            {
                TbPressure.Text = "0.00";
            }
        }

        private async void TbFlow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }
            if (!float.TryParse(TbFlow.Text, out float value))
            {
                TbFlow.Text = "";
                return;
            }
            var input = new Dictionary<string, object>()
            {
                ["TargetFlow"] = value
            };
            await ExecuteCommandAsync("设置流量", input);

            // 清除文本框焦点
            Keyboard.ClearFocus();
        }
    }
}
