// ============================================================================
//  TwoLiquidOneGasFeedScreen.xaml.cs
//  放置位置：MaxChemical.Modules.Designer/Views/Controls/Screens/
//  与 TwoLiquidOneGasFeedScreen.xaml 配对（partial class）。
//
//  职责：
//    - 继承 DeviceScreenBase，轮询命令 "获取所有数据"，把值填进面板 TextBlock；
//    - 三个开关点击 → 调对应 ON/OFF 命令（MFC 反逻辑，泵正逻辑）；
//    - 开关状态联动管道流动动画（光带平移 Storyboard）；
//    - 不显示 Add-SV（按驱动）。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DevicePlugins.Devices;

namespace MaxChemical.Modules.Designer.Views.Controls.Screens
{
    public partial class TwoLiquidOneGasFeedScreen : DeviceScreenBase
    {
        protected override string PollCommandName => "获取所有数据";

        // 开关的“界面是否 ON”状态缓存（避免轮询回填和用户点击打架）
        private bool _mfcOn, _p1On, _p2On;
        private bool _busyMfc, _busyP1, _busyP2;

        // 管道流动动画 Storyboard，按行缓存
        private Storyboard _flowMfc, _flowP1, _flowP2;

        public TwoLiquidOneGasFeedScreen()
        {
            InitializeComponent();
            // 初始把开关都画成 OFF 外观
            ApplySwitchVisual(MfcSwitchTrack, MfcSwitchText, MfcSwitchKnob, false);
            ApplySwitchVisual(Pump1SwitchTrack, Pump1SwitchText, Pump1SwitchKnob, false);
            ApplySwitchVisual(Pump2SwitchTrack, Pump2SwitchText, Pump2SwitchKnob, false);
        }

        // ===================== 数据刷新 =====================
        protected override void OnDataRefreshed(DeviceParameters o)
        {
            // MFC（F-SV 正在编辑时不回填，避免冲掉用户输入）
            if (!MfcFsv.IsKeyboardFocusWithin) MfcFsv.Text = Fmt(o, "MFC_FSV");
            MfcFlow.Text = Fmt(o, "MFC_FLOW");
            MfcAdd.Text = Fmt(o, "MFC_Add");
            // 泵1
            if (!Pump1Fsv.IsKeyboardFocusWithin) Pump1Fsv.Text = Fmt(o, "Pump1_FSV");
            Pump1Flow.Text = Fmt(o, "Pump1_FLOW");
            Pump1Add.Text = Fmt(o, "Pump1_Add");
            Pump1P.Text = Fmt(o, "Pump1_P");
            // 泵2
            if (!Pump2Fsv.IsKeyboardFocusWithin) Pump2Fsv.Text = Fmt(o, "Pump2_FSV");
            Pump2Flow.Text = Fmt(o, "Pump2_FLOW");
            Pump2Add.Text = Fmt(o, "Pump2_Add");
            Pump2P.Text = Fmt(o, "Pump2_P");

            // 开关状态回填（用户正在操作的那个不回填，避免抖动）
            var mfcRaw = o.GetNullableValue<double>("MFC_OnOff");
            if (mfcRaw.HasValue && !_busyMfc)
                SetMfc(Math.Abs(mfcRaw.Value) < 0.5);   // 反逻辑：0=运行

            var p1Raw = o.GetNullableValue<double>("Pump1_OnOff");
            if (p1Raw.HasValue && !_busyP1)
                SetP1(p1Raw.Value > 0.5);               // 正逻辑：1=运行

            var p2Raw = o.GetNullableValue<double>("Pump2_OnOff");
            if (p2Raw.HasValue && !_busyP2)
                SetP2(p2Raw.Value > 0.5);
        }

        protected override void OnPollError(Exception ex)
        {
            // 只读 TextBlock 置位
            foreach (var tb in new[] { MfcFlow, MfcAdd,
                Pump1Flow, Pump1Add, Pump1P,
                Pump2Flow, Pump2Add, Pump2P })
                tb.Text = "--";
            // 可编辑 F-SV：不在编辑中才置位
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
            // 界面 ON=运行；MFC 反逻辑 → 运行写 0，停止写 1
            short writeVal = (short)(target ? 0 : 1);
            _busyMfc = true;
            SetMfc(target);                       // 先乐观更新外观
            bool ok = await ExecuteCommandAsync("设置MFC ON/OFF",
                new Dictionary<string, object> { ["TargetValue"] = writeVal });
            if (!ok) SetMfc(!target);             // 失败回弹
            _busyMfc = false;
        }

        private async void Pump1Switch_Click(object sender, RoutedEventArgs e)
        {
            bool target = !_p1On;
            short writeVal = (short)(target ? 1 : 0);   // 泵正逻辑
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
        // 回车时把输入框的值下发到对应的"设置xx F_SV"命令（驱动 input key = TargetValue, float）
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
            // 解析输入；非法输入直接忽略（不下发）
            if (!double.TryParse(box.Text, System.Globalization.NumberStyles.Any,
                    CultureInfo.InvariantCulture, out double val))
            {
                System.Media.SystemSounds.Beep.Play();
                return;
            }

            // 下发命令；input key = TargetValue（驱动 GetValue<float>）
            bool ok = await ExecuteCommandAsync(commandName,
                new Dictionary<string, object> { ["TargetValue"] = val });

            if (!ok) System.Media.SystemSounds.Hand.Play();

            // 把焦点移开，让下一轮轮询能回填实际读数
            System.Windows.Input.Keyboard.ClearFocus();
        }

        // 输入框获得焦点时全选，方便直接覆盖输入
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
            SetFlow(ref _flowMfc, FlowMfc, FlowMfcT, on);
        }
        private void SetP1(bool on)
        {
            _p1On = on;
            ApplySwitchVisual(Pump1SwitchTrack, Pump1SwitchText, Pump1SwitchKnob, on);
            SetFlow(ref _flowP1, FlowP1, FlowP1T, on);
        }
        private void SetP2(bool on)
        {
            _p2On = on;
            ApplySwitchVisual(Pump2SwitchTrack, Pump2SwitchText, Pump2SwitchKnob, on);
            SetFlow(ref _flowP2, FlowP2, FlowP2T, on);
        }

        private static readonly Brush OnBrush = new SolidColorBrush(Color.FromRgb(0x1F, 0xAA, 0x5C));
        private static readonly Brush OffBrush = new SolidColorBrush(Color.FromRgb(0xD2, 0x3A, 0x30));

        // 开关外观：ON 绿色 + 文字右 + 滑块左；OFF 红色 + 文字左 + 滑块右
        private void ApplySwitchVisual(System.Windows.Controls.Border track,
                                       System.Windows.Controls.TextBlock text,
                                       System.Windows.Controls.Border knob, bool on)
        {
            track.Background = on ? OnBrush : OffBrush;
            text.Text = on ? "ON" : "OFF";
            if (on)
            {
                text.HorizontalAlignment = HorizontalAlignment.Right;
                text.Margin = new Thickness(0, 0, 16, 0);
                knob.HorizontalAlignment = HorizontalAlignment.Left;
                knob.Margin = new Thickness(5, 0, 0, 0);
            }
            else
            {
                text.HorizontalAlignment = HorizontalAlignment.Left;
                text.Margin = new Thickness(16, 0, 0, 0);
                knob.HorizontalAlignment = HorizontalAlignment.Right;
                knob.Margin = new Thickness(0, 0, 5, 0);
            }
        }

        // 管道流动：on=启动光带平移循环，off=停止并隐藏
        private void SetFlow(ref Storyboard sb, System.Windows.Shapes.Rectangle flow,
                             TranslateTransform tt, bool on)
        {
            if (on)
            {
                flow.Opacity = 1;
                if (sb == null)
                {
                    var anim = new DoubleAnimation
                    {
                        From = -120,
                        To = flow.ActualWidth > 0 ? GetConduitWidth(flow) : 900,
                        Duration = TimeSpan.FromSeconds(1.6),
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    Storyboard.SetTarget(anim, tt);
                    Storyboard.SetTargetProperty(anim, new PropertyPath(TranslateTransform.XProperty));
                    sb = new Storyboard();
                    sb.Children.Add(anim);
                }
                sb.Begin();
            }
            else
            {
                sb?.Stop();
                flow.Opacity = 0;
            }
        }

        // 取管道可视宽度，作为光带平移终点（光带跑出右端）
        private double GetConduitWidth(System.Windows.Shapes.Rectangle flow)
        {
            var parent = flow.Parent as FrameworkElement;
            return (parent?.ActualWidth ?? 900) + 120;
        }
    }
}