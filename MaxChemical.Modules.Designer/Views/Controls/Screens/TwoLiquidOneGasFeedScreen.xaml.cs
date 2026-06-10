// ============================================================================
//  TwoLiquidOneGasFeedScreen.xaml.cs
//  放置位置：MaxChemical.Modules.Designer/Views/Controls/Screens/
//  与 TwoLiquidOneGasFeedScreen.xaml 配对（partial class）。
//
//  职责：
//    - 继承 DeviceScreenBase，轮询命令 "获取所有数据"，把值填进面板；
//    - 三个开关点击 → 调对应 ON/OFF 命令（MFC 反逻辑，泵正逻辑）；
//    - F-SV 可编辑回车下发"设置xx F_SV"；
//    - 开关 ON 时光带沿管路 Path 流动（StrokeDashOffset 动画）；
//    - 不显示 Add-SV（按驱动）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DevicePlugins.Devices;

namespace MaxChemical.Modules.Designer.Views.Controls.Screens
{
    public partial class TwoLiquidOneGasFeedScreen : DeviceScreenBase
    {
        protected override string PollCommandName => "获取所有数据";

        private bool _mfcOn, _p1On, _p2On;
        private bool _busyMfc, _busyP1, _busyP2;

        public TwoLiquidOneGasFeedScreen()
        {
            InitializeComponent();
            ApplySwitchVisual(MfcSwitchTrack, MfcSwitchText, MfcSwitchKnob, false);
            ApplySwitchVisual(Pump1SwitchTrack, Pump1SwitchText, Pump1SwitchKnob, false);
            ApplySwitchVisual(Pump2SwitchTrack, Pump2SwitchText, Pump2SwitchKnob, false);
        }

        // ===================== 数据刷新 =====================
        protected override void OnDataRefreshed(DeviceParameters o)
        {
            if (!MfcFsv.IsKeyboardFocusWithin) MfcFsv.Text = Fmt(o, "MFC_FSV");
            MfcFlow.Text = Fmt(o, "MFC_FLOW");
            MfcAdd.Text = Fmt(o, "MFC_Add");
            if (!Pump1Fsv.IsKeyboardFocusWithin) Pump1Fsv.Text = Fmt(o, "Pump1_FSV");
            Pump1Flow.Text = Fmt(o, "Pump1_FLOW");
            Pump1Add.Text = Fmt(o, "Pump1_Add");
            Pump1P.Text = Fmt(o, "Pump1_P");
            if (!Pump2Fsv.IsKeyboardFocusWithin) Pump2Fsv.Text = Fmt(o, "Pump2_FSV");
            Pump2Flow.Text = Fmt(o, "Pump2_FLOW");
            Pump2Add.Text = Fmt(o, "Pump2_Add");
            Pump2P.Text = Fmt(o, "Pump2_P");

            var mfcRaw = o.GetNullableValue<double>("MFC_OnOff");
            if (mfcRaw.HasValue && !_busyMfc) SetMfc(Math.Abs(mfcRaw.Value) < 0.5);   // 反逻辑：0=运行
            var p1Raw = o.GetNullableValue<double>("Pump1_OnOff");
            if (p1Raw.HasValue && !_busyP1) SetP1(p1Raw.Value > 0.5);
            var p2Raw = o.GetNullableValue<double>("Pump2_OnOff");
            if (p2Raw.HasValue && !_busyP2) SetP2(p2Raw.Value > 0.5);
        }

        protected override void OnPollError(Exception ex)
        {
            foreach (var tb in new[] { MfcFlow, MfcAdd, Pump1Flow, Pump1Add, Pump1P, Pump2Flow, Pump2Add, Pump2P })
                tb.Text = "--";
            if (!MfcFsv.IsKeyboardFocusWithin) MfcFsv.Text = "--";
            if (!Pump1Fsv.IsKeyboardFocusWithin) Pump1Fsv.Text = "--";
            if (!Pump2Fsv.IsKeyboardFocusWithin) Pump2Fsv.Text = "--";
        }

        private static string Fmt(DeviceParameters o, string key)
        {
            var v = o.GetNullableValue<double>(key);
            return v.HasValue ? v.Value.ToString("0.0", CultureInfo.InvariantCulture) : "--";
        }

        // ===================== 开关点击 → 调命令 =====================
        private async void MfcSwitch_Click(object sender, RoutedEventArgs e)
        {
            bool target = !_mfcOn;
            short writeVal = (short)(target ? 0 : 1);   // MFC 反逻辑：运行=0
            _busyMfc = true;
            SetMfc(target);
            bool ok = await ExecuteCommandAsync("设置MFC ON/OFF",
                new Dictionary<string, object> { ["TargetValue"] = writeVal });
            if (!ok) SetMfc(!target);
            _busyMfc = false;
        }

        private async void Pump1Switch_Click(object sender, RoutedEventArgs e)
        {
            bool target = !_p1On;
            short writeVal = (short)(target ? 1 : 0);
            _busyP1 = true;
            SetP1(target);
            bool ok = await ExecuteCommandAsync("设置泵1 ON/OFF",
                new Dictionary<string, object> { ["TargetValue"] = writeVal });
            if (!ok) SetP1(!target);
            _busyP1 = false;
        }

        private async void Pump2Switch_Click(object sender, RoutedEventArgs e)
        {
            bool target = !_p2On;
            short writeVal = (short)(target ? 1 : 0);
            _busyP2 = true;
            SetP2(target);
            bool ok = await ExecuteCommandAsync("设置泵2 ON/OFF",
                new Dictionary<string, object> { ["TargetValue"] = writeVal });
            if (!ok) SetP2(!target);
            _busyP2 = false;
        }

        // ===================== F-SV 设定值下发 =====================
        private async void MfcFsv_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            await SubmitFsv(MfcFsv, "设置MFC F_SV");
        }
        private async void Pump1Fsv_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            await SubmitFsv(Pump1Fsv, "设置泵1 F_SV");
        }
        private async void Pump2Fsv_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            await SubmitFsv(Pump2Fsv, "设置泵2 F_SV");
        }

        private async System.Threading.Tasks.Task SubmitFsv(
            System.Windows.Controls.TextBox box, string commandName)
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

        private void Fsv_SelectAll(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.TextBox tb)
                tb.Dispatcher.BeginInvoke(new Action(() => tb.SelectAll()));
        }

        // ===================== 开关状态设置（外观 + 管道流动） =====================
        private void SetMfc(bool on)
        {
            _mfcOn = on;
            ApplySwitchVisual(MfcSwitchTrack, MfcSwitchText, MfcSwitchKnob, on);
            SetFlow(FlowMfc, on);
        }
        private void SetP1(bool on)
        {
            _p1On = on;
            ApplySwitchVisual(Pump1SwitchTrack, Pump1SwitchText, Pump1SwitchKnob, on);
            SetFlow(FlowP1, on);
        }
        private void SetP2(bool on)
        {
            _p2On = on;
            ApplySwitchVisual(Pump2SwitchTrack, Pump2SwitchText, Pump2SwitchKnob, on);
            SetFlow(FlowP2, on);
        }

        private static readonly Brush OnBrush = new SolidColorBrush(Color.FromRgb(0x1D, 0xA8, 0x5A));
        private static readonly Brush OffBrush = new SolidColorBrush(Color.FromRgb(0xCF, 0x39, 0x2F));

        private void ApplySwitchVisual(System.Windows.Controls.Border track,
                                       System.Windows.Controls.TextBlock text,
                                       System.Windows.Controls.Border knob, bool on)
        {
            track.Background = on ? OnBrush : OffBrush;
            text.Text = on ? "ON" : "OFF";
            if (on)
            {
                text.HorizontalAlignment = HorizontalAlignment.Right;
                text.Margin = new Thickness(0, 0, 15, 0);
                knob.HorizontalAlignment = HorizontalAlignment.Left;
                knob.Margin = new Thickness(5, 0, 0, 0);
            }
            else
            {
                text.HorizontalAlignment = HorizontalAlignment.Left;
                text.Margin = new Thickness(15, 0, 0, 0);
                knob.HorizontalAlignment = HorizontalAlignment.Right;
                knob.Margin = new Thickness(0, 0, 5, 0);
            }
        }

        // 管道流动：光带沿 Path 用 StrokeDashOffset 滚动（转角自然跟随）
        private void SetFlow(Path flow, bool on)
        {
            if (on)
            {
                flow.Opacity = 0.95;
                var anim = new DoubleAnimation
                {
                    From = 0,
                    To = -24,
                    Duration = TimeSpan.FromSeconds(0.8),
                    RepeatBehavior = RepeatBehavior.Forever
                };
                flow.BeginAnimation(Shape.StrokeDashOffsetProperty, anim);
            }
            else
            {
                flow.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                flow.Opacity = 0;
            }
        }
    }
}