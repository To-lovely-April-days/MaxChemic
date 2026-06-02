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
    /// HighLowTemperature.xaml 的交互逻辑
    /// </summary>
    public partial class HighLowTemperature : DeviceScreenBase
    {
        public HighLowTemperature()
        {
            InitializeComponent();
        }

        protected override string PollCommandName => "获取可视化数据";

        protected override void OnDataRefreshed(DeviceParameters output)
        {
            // 物料温度
            var value = output.GetNullableValue<double>("MaterialTemperature");
            TbMaterial.Text = value.HasValue ? value.Value.ToString("F2") : "--";
            // 出口温度
            value = output.GetNullableValue<double>("OutletTemperature");
            TbOutlet.Text = value.HasValue ? value.Value.ToString("F2") : "--";
            // 回口温度
            value = output.GetNullableValue<double>("BackTemperature");
            TbBack.Text = value.HasValue ? value.Value.ToString("F2") : "--";
            // 温度设定值
            value = output.GetNullableValue<double>("SV");
            if (!TbSetSv.IsKeyboardFocusWithin)
            {
                TbSetSv.Text = value.HasValue ? value.Value.ToString("F2") : "--";
            }

            var run = output.GetNullableValue<bool>("Run");
            if (run.HasValue && run.Value)
            {
                BtnRunStop.Content = "停止";
            }
            else
            {
                BtnRunStop.Content = "运行";
            }
        }

        protected override void OnPollError(Exception ex)
        {
            TbMaterial.Text = "--";
            TbOutlet.Text = "--";
            TbBack.Text = "--";
            if (!TbSetSv.IsKeyboardFocusWithin)
            {
                TbSetSv.Text = "--";
            }
        }

        /// <summary>
        /// 温度设定值文本框按键响应事件
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void TbSetSv_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }
            if (!float.TryParse(TbSetSv.Text, out float value))
            {
                TbSetSv.Text = "";
                return;
            }
            var input = new Dictionary<string, object>()
            {
                ["TargetValue"] = value
            };
            await ExecuteCommandAsync("设置温度SV", input);

            // 清除文本框焦点
            Keyboard.ClearFocus();
        }


        private async void BtnRunStop_Click(object sender, RoutedEventArgs e)
        {
            if (BtnRunStop.Content == "运行")
            {
                var input = new Dictionary<string, object>()
                {
                    ["TargetValue"] = true
                };
                if (await ExecuteCommandAsync("定值启动/停止", input))
                {
                    BtnRunStop.Content = "停止";
                }
                return;
            }


            if (BtnRunStop.Content == "停止")
            {
                var input = new Dictionary<string, object>()
                {
                    ["TargetValue"] = false
                };
                if (await ExecuteCommandAsync("定值启动/停止", input))
                {
                    BtnRunStop.Content = "运行";
                }
            }
        }
    }
}
