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
    /// HighPreactorScreen.xaml 的交互逻辑
    /// </summary>
    public partial class HighPreactorScreen : DeviceScreenBase
    {
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
            // 炉温
            value = output.GetNullableValue<double>("FurnacePv");
            TbFurnacePv.Text= value.HasValue ? value.Value.ToString("F2") : "--";
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
    }
}
