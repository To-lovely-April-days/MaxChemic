// ============================================================================
//  SiliconCarbideChipScreen.xaml.cs
//  放置位置：MaxChemical.Modules.Designer/Views/Controls/Screens/
//  与 SiliconCarbideChipScreen.xaml 配对。
//
//  职责（严格按驱动）：
//    - 轮询"获取所有数据"，填 T1~T6、压力P、SV%、ON/OFF；
//    - SV% 可编辑回车下发（设置背压阀SV%，驱动取值 short，input key=TargetValue）；
//    - 背压阀开关点击 → 调"设置背压阀ON/OFF"（反逻辑 0运行1停止，驱动取值 short）；
//    - T1~T6 / 压力 只读。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DevicePlugins.Devices;

namespace MaxChemical.Modules.Designer.Views.Controls.Screens
{
    public partial class SiliconCarbideChipScreen : DeviceScreenBase
    {
        protected override string PollCommandName => "获取所有数据";

        private bool _valveOn;
        private bool _busyValve;

        public SiliconCarbideChipScreen()
        {
            InitializeComponent();
            ApplyValveVisual(false);
        }

        // ===================== 数据刷新 =====================
        protected override void OnDataRefreshed(DeviceParameters o)
        {
            T1.Text = Fmt(o, "T1", "0.0");
            T2.Text = Fmt(o, "T2", "0.0");
            T3.Text = Fmt(o, "T3", "0.0");
            T4.Text = Fmt(o, "T4", "0.0");
            T5.Text = Fmt(o, "T5", "0.0");
            T6.Text = Fmt(o, "T6", "0.0");
            Press.Text = Fmt(o, "Pressure", "0.00");
            if (!SvPercent.IsKeyboardFocusWithin) SvPercent.Text = Fmt(o, "SV%", "0");

            var onoff = o.GetNullableValue<double>("ON/OFF");
            if (onoff.HasValue && !_busyValve)
                SetValve(Math.Abs(onoff.Value) < 0.5);   // 反逻辑：0=运行
        }

        protected override void OnPollError(Exception ex)
        {
            foreach (var tb in new[] { T1, T2, T3, T4, T5, T6, Press })
                tb.Text = "--";
            if (!SvPercent.IsKeyboardFocusWithin) SvPercent.Text = "--";
        }

        private static string Fmt(DeviceParameters o, string key, string fmt)
        {
            var v = o.GetNullableValue<double>(key);
            return v.HasValue ? v.Value.ToString(fmt, CultureInfo.InvariantCulture) : "--";
        }

        // ===================== 背压阀开关 → 设置背压阀ON/OFF =====================
        private async void ValveSwitch_Click(object sender, RoutedEventArgs e)
        {
            bool target = !_valveOn;
            // 界面 ON=运行；驱动反逻辑 → 运行写 0，停止写 1；驱动取值 short
            short writeVal = (short)(target ? 0 : 1);
            _busyValve = true;
            SetValve(target);
            bool ok = await ExecuteCommandAsync("设置背压阀ON/OFF",
                new Dictionary<string, object> { ["TargetValue"] = writeVal });
            if (!ok) SetValve(!target);
            _busyValve = false;
        }

        // ===================== SV% 可编辑下发 =====================
        private async void SvPercent_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            // 驱动"设置背压阀SV%"取值 short
            if (!short.TryParse(SvPercent.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out short val))
            {
                System.Media.SystemSounds.Beep.Play();
                return;
            }
            bool ok = await ExecuteCommandAsync("设置背压阀SV%",
                new Dictionary<string, object> { ["TargetValue"] = val });
            if (!ok) System.Media.SystemSounds.Hand.Play();
            System.Windows.Input.Keyboard.ClearFocus();
        }

        private void Sv_SelectAll(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
                tb.Dispatcher.BeginInvoke(new Action(() => tb.SelectAll()));
        }

        // ===================== 开关外观（仿 tank-system.html：ON 绿 / OFF 红） =====================
        private void SetValve(bool on)
        {
            _valveOn = on;
            ApplyValveVisual(on);
        }

        private static readonly Brush OnBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0xB8, 0x4D));
        private static readonly Brush OffBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x48, 0x48));

        private void ApplyValveVisual(bool on)
        {
            ValveTrack.Background = on ? OnBrush : OffBrush;
            ValveText.Text = on ? "ON" : "OFF";
            if (on)
            {
                ValveText.HorizontalAlignment = HorizontalAlignment.Right;
                ValveText.Margin = new Thickness(0, 0, 14, 0);
                ValveKnob.HorizontalAlignment = HorizontalAlignment.Left;
                ValveKnob.Margin = new Thickness(5, 0, 0, 0);
            }
            else
            {
                ValveText.HorizontalAlignment = HorizontalAlignment.Left;
                ValveText.Margin = new Thickness(14, 0, 0, 0);
                ValveKnob.HorizontalAlignment = HorizontalAlignment.Right;
                ValveKnob.Margin = new Thickness(0, 0, 5, 0);
            }
        }
    }
}
