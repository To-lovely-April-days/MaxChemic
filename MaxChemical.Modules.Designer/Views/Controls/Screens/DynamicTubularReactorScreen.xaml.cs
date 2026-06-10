// ============================================================================
//  DynamicTubularReactorScreen.xaml.cs
//  放置位置：MaxChemical.Modules.Designer/Views/Controls/Screens/
//  与 DynamicTubularReactorScreen.xaml 配对。
//
//  职责（严格按驱动）：
//    - 轮询"获取所有数据"，填 搅拌SV/PV、釜温、压力、搅拌开关；
//    - 搅拌开关点击 → 调"设置 ON/OFF"（反逻辑 0运行1停止，驱动取值 byte）；
//    - SV / 釜温 / 压力 可编辑回车下发（设置SV / 设置釜温 / 设置压力，input key=TargetValue, float）；
//    - PV 只读。
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
    public partial class DynamicTubularReactorScreen : DeviceScreenBase
    {
        protected override string PollCommandName => "获取所有数据";

        private bool _stirOn;
        private bool _busyStir;

        public DynamicTubularReactorScreen()
        {
            InitializeComponent();
            ApplyStirVisual(false);
        }

        // ===================== 数据刷新 =====================
        protected override void OnDataRefreshed(DeviceParameters o)
        {
            if (!StirSv.IsKeyboardFocusWithin) StirSv.Text = Fmt(o, "SV", "0");
            StirPv.Text = Fmt(o, "PV", "0");
            if (!Temp.IsKeyboardFocusWithin)  Temp.Text  = Fmt(o, "Temperature", "0.0");
            if (!Press.IsKeyboardFocusWithin) Press.Text = Fmt(o, "Pressure", "0.00");

            var onoff = o.GetNullableValue<double>("ON/OFF");
            if (onoff.HasValue && !_busyStir)
                SetStir(Math.Abs(onoff.Value) < 0.5);   // 反逻辑：0=运行
        }

        protected override void OnPollError(Exception ex)
        {
            StirPv.Text = "--";
            if (!StirSv.IsKeyboardFocusWithin) StirSv.Text = "--";
            if (!Temp.IsKeyboardFocusWithin)  Temp.Text  = "--";
            if (!Press.IsKeyboardFocusWithin) Press.Text = "--";
        }

        private static string Fmt(DeviceParameters o, string key, string fmt)
        {
            var v = o.GetNullableValue<double>(key);
            return v.HasValue ? v.Value.ToString(fmt, CultureInfo.InvariantCulture) : "--";
        }

        // ===================== 搅拌开关 → 设置 ON/OFF =====================
        private async void StirSwitch_Click(object sender, RoutedEventArgs e)
        {
            bool target = !_stirOn;
            // 界面 ON=运行；驱动反逻辑 → 运行写 0，停止写 1；驱动取值 byte
            byte writeVal = (byte)(target ? 0 : 1);
            _busyStir = true;
            SetStir(target);
            bool ok = await ExecuteCommandAsync("设置 ON/OFF",
                new Dictionary<string, object> { ["TargetValue"] = writeVal });
            if (!ok) SetStir(!target);
            _busyStir = false;
        }

        // ===================== 可编辑设定下发 =====================
        private async void StirSv_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            await Submit(StirSv, "设置 SV");
        }
        private async void Temp_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            await Submit(Temp, "设置釜温");
        }
        private async void Press_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            await Submit(Press, "设置压力");
        }

        private async System.Threading.Tasks.Task Submit(TextBox box, string commandName)
        {
            if (!double.TryParse(box.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double val))
            {
                System.Media.SystemSounds.Beep.Play();
                return;
            }
            bool ok = await ExecuteCommandAsync(commandName,
                new Dictionary<string, object> { ["TargetValue"] = val });
            if (!ok) System.Media.SystemSounds.Hand.Play();
            System.Windows.Input.Keyboard.ClearFocus();
        }

        private void Sv_SelectAll(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
                tb.Dispatcher.BeginInvoke(new Action(() => tb.SelectAll()));
        }

        // ===================== 搅拌开关外观 =====================
        private void SetStir(bool on)
        {
            _stirOn = on;
            ApplyStirVisual(on);
        }

        private static readonly Brush OnBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0xB8, 0x4D));
        private static readonly Brush OffBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x44, 0x4D));
        private static readonly Brush OnText = new SolidColorBrush(Color.FromRgb(0x06, 0x28, 0x0F));
        private static readonly Brush OffText = new SolidColorBrush(Color.FromRgb(0xAE, 0xBC, 0xCA));

        private void ApplyStirVisual(bool on)
        {
            StirTrack.Background = on ? OnBrush : OffBrush;
            StirText.Text = on ? "ON" : "OFF";
            StirText.Foreground = on ? OnText : OffText;
            if (on)
            {
                StirText.HorizontalAlignment = HorizontalAlignment.Right;
                StirText.Margin = new Thickness(0, 0, 14, 0);
                StirKnob.HorizontalAlignment = HorizontalAlignment.Left;
                StirKnob.Margin = new Thickness(4, 0, 0, 0);
            }
            else
            {
                StirText.HorizontalAlignment = HorizontalAlignment.Left;
                StirText.Margin = new Thickness(14, 0, 0, 0);
                StirKnob.HorizontalAlignment = HorizontalAlignment.Right;
                StirKnob.Margin = new Thickness(0, 0, 4, 0);
            }
        }
    }
}
