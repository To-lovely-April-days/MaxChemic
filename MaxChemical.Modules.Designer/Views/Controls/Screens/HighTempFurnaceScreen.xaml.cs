using DevicePlugins.Devices;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MaxChemical.Modules.Designer.Views.Controls.Screens
{
    /// <summary>
    /// HighTempFurnaceScreen.xaml 的交互逻辑。
    /// 高温炉(宇电 AI 温控器)控制屏:设定温度 / 实时温度 PV / 运行·停止。
    /// 轮询驱动的「获取可视化数据」(PV/SV/Run),写命令走「设置温度」「开启」「停止」。
    /// </summary>
    public partial class HighTempFurnaceScreen : DeviceScreenBase
    {
        private const double MaxTemp = 1300.0;   // 表盘满量程,与驱动 0~1300℃ 一致

        private bool _animationRunning;
        private readonly Storyboard _storyboard;

        public HighTempFurnaceScreen()
        {
            InitializeComponent();
            PollIntervalMs = 1000;   // 一轮 3 次 Modbus 读,放宽到 1s

            _storyboard = new Storyboard();
            ApplyGlowAnimation();
        }

        protected override string PollCommandName => "获取可视化数据";

        protected override void OnDeviceAttached()
        {
            TbTitle.Text = Device?.Name ?? "高温炉";
            try
            {
                var mode = Device?.Parameters?.GetValue<string>("通信方式");
                var station = Device?.Parameters?.GetValue<double>("Modbus站号");
                TbMeta.Text = $"管式高温炉 · Modbus RTU · 站号 {station:F0}";
                TbComm.Text = $"通讯:{mode ?? "--"}";
            }
            catch { /* 参数缺失不影响屏幕打开 */ }
        }

        protected override void OnDataRefreshed(DeviceParameters output)
        {
            var pv = output.GetNullableValue<double>("PV");
            var sv = output.GetNullableValue<double>("SV");
            var run = output.GetNullableValue<bool>("Run");

            string pvText = pv.HasValue ? pv.Value.ToString("F1") : "--";
            TbPv.Text = pvText;
            TbPvBar.Text = pvText;
            TbPvSmall.Text = pvText;
            TbPanelPv.Text = pvText;
            ArcProgress.Angle = -180 + Clamp01((pv ?? 0) / MaxTemp) * 180;

            string svText = sv.HasValue ? sv.Value.ToString("F1") : "--";
            TbSv.Text = svText;
            TbSvSmall.Text = svText;
            ArcSv.Angle = -180 + Clamp01((sv ?? 0) / MaxTemp) * 180;
            if (!TbSetSv.IsKeyboardFocusWithin)
            {
                TbSetSv.Text = svText;
            }

            if (run.HasValue && run.Value)
            {
                SetRunningUI(true);
            }
            else
            {
                SetRunningUI(false);
            }
        }

        protected override void OnPollError(Exception ex)
        {
            TbPv.Text = TbPvBar.Text = TbPvSmall.Text = TbPanelPv.Text = "--";
            TbSv.Text = TbSvSmall.Text = "--";
            if (!TbSetSv.IsKeyboardFocusWithin)
            {
                TbSetSv.Text = "--";
            }
            TbState.Text = "通讯异常";
            TbState.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x3B));
        }

        /// <summary>设定温度文本框:回车下发「设置温度」。</summary>
        private async void TbSetSv_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }
            if (!float.TryParse(TbSetSv.Text, out float value) || value < 0 || value > MaxTemp)
            {
                TbSetSv.Text = "";
                return;
            }
            var input = new Dictionary<string, object>()
            {
                ["TargetTemperature"] = value
            };
            await ExecuteCommandAsync("设置温度", input);

            Keyboard.ClearFocus();
        }

        private async void BtnRunStop_Click(object sender, RoutedEventArgs e)
        {
            if (BtnRunStop.Content.ToString() == "运行")
            {
                if (await ExecuteCommandAsync("开启"))
                {
                    SetRunningUI(true);
                }
                return;
            }

            if (BtnRunStop.Content.ToString() == "停止")
            {
                if (await ExecuteCommandAsync("停止"))
                {
                    SetRunningUI(false);
                }
            }
        }

        /// <summary>统一切换运行/停止的按钮文字、状态徽标、指示灯与发热腔呼吸动画。</summary>
        private void SetRunningUI(bool running)
        {
            if (running)
            {
                BtnRunStop.Content = "停止";
                TbState.Text = "加热运行中";
                TbState.Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0xFF, 0xB0));
                RunLed.Fill = new SolidColorBrush(Color.FromRgb(0x35, 0xE0, 0x7A));
                if (!_animationRunning)
                {
                    _storyboard.Begin(this, true);
                    _animationRunning = true;
                }
            }
            else
            {
                BtnRunStop.Content = "运行";
                TbState.Text = "待机";
                TbState.Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0xC2, 0xCB));
                RunLed.Fill = new SolidColorBrush(Color.FromRgb(0x55, 0x5B, 0x61));
                if (_animationRunning)
                {
                    _storyboard.Remove(this);
                    HeatGlow.BeginAnimation(UIElement.OpacityProperty, null);
                    HeatGlow.Opacity = 0.12;
                    _animationRunning = false;
                }
            }
        }

        /// <summary>发热腔呼吸发光动画(运行时循环)。</summary>
        private void ApplyGlowAnimation()
        {
            this.RegisterName("HeatGlow", HeatGlow);

            var glow = new DoubleAnimation()
            {
                From = 0.25,
                To = 0.95,
                Duration = TimeSpan.FromSeconds(1.2),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase() { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTargetName(glow, "HeatGlow");
            Storyboard.SetTargetProperty(glow, new PropertyPath("Opacity"));
            _storyboard.Children.Add(glow);
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
    }
}
