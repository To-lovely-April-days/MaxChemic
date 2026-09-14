// ============================================================================
//  PeristalticPumpLCScreen.xaml.cs
//  与 PeristalticPumpLCScreen.xaml 配对。创锐 LC / FC 系列蠕动泵(触摸屏款)的嵌入式屏幕。
//
//  职责:
//    - 轮询「获取所有数据」,填流速 / 灌装量 / 灌装时间 / 模式 / 方向 / 全速 / 泵头泵管 / K / 485 状态;
//    - 流速下发、按流速启动、启动 / 停止、全速、方向、四种运转模式;
//    - 灌装量 / 灌装时间 / 回吸速度 / 回吸角度 / 泵头 / 泵管 / 校准两步。
//
//  泵头下拉回填会触发 SelectionChanged,用 _suppress 挡住,免得回填一次就下发一次。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DevicePlugins.Devices;

namespace MaxChemical.Modules.Designer.Views.Controls.Screens
{
    public partial class PeristalticPumpLCScreen : DeviceScreenBase
    {
        protected override string PollCommandName => "获取所有数据";

        private bool _suppress;
        private bool _running;
        private bool _full;
        private bool? _rs485;
        private int _mode = -1;
        private int _direction = -1;
        private double _flow;

        private static readonly string[] HeadNames =
        {
            "YZ1515x", "YZ15M", "313D", "TX315", "YZ2515x", "YZ25M", "BZ25", "KZ25",
            "DG(6)", "LP15", "BZ15", "YZ25L", "LP25", "YZ35", "KZ50", "DG(10)",
        };
        private static readonly string[] ModeNames = { "传输", "定量", "定时", "定时定量" };
        private readonly Button[] _modeButtons = new Button[ModeNames.Length];

        /// <summary>输入框合法范围,与驱动命令一致。</summary>
        private static readonly Dictionary<string, (double min, double max, string fmt)> BoxRanges = new()
        {
            ["设置灌装量"] = (0.001, 9999000, "F3"),
            ["设置灌装时间"] = (0.1, 599940, "F1"),
            ["设置回吸速度"] = (10, 300, "F0"),
            ["设置回吸角度"] = (0, 3600, "F0"),
            ["设置泵管"] = (0, 99, "F0"),
            ["设置校准液量"] = (0.001, 9999000, "F3"),
            ["设置实测液量"] = (0.001, 9999000, "F3"),
        };

        private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0xB8, 0x4D));
        private static readonly Brush WaitBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x74));
        private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x1A));
        private static readonly Brush DimText = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAA));
        private static readonly Brush OkText = new SolidColorBrush(Color.FromRgb(0x7F, 0xE3, 0x9B));
        private static readonly Brush ChipBorder = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x55));
        private static readonly Brush BtnBg = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42));
        private static readonly Brush BtnBorder = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x64));
        private static readonly Brush ActiveBg = new SolidColorBrush(Color.FromRgb(0x1F, 0x5F, 0x5A));
        private static readonly Brush ActiveBorder = new SolidColorBrush(Color.FromRgb(0x4F, 0xD1, 0xC5));
        private static readonly Brush WarnBg = new SolidColorBrush(Color.FromRgb(0x5A, 0x44, 0x1E));

        static PeristalticPumpLCScreen()
        {
            foreach (var b in new[] { OkBrush, WaitBrush, WarnBrush, DimText, OkText, ChipBorder, BtnBg, BtnBorder, ActiveBg, ActiveBorder, WarnBg })
                b.Freeze();
        }

        public PeristalticPumpLCScreen()
        {
            InitializeComponent();
            PollIntervalMs = 1000;

            _suppress = true;
            try
            {
                for (int i = 0; i < HeadNames.Length; i++)
                    HeadCombo.Items.Add(new ComboBoxItem { Content = $"{i}  {HeadNames[i]}" });
            }
            finally { _suppress = false; }

            var style = (Style)FindResource("ModeBtn");
            for (int i = 0; i < ModeNames.Length; i++)
            {
                var btn = new Button { Style = style, Content = ModeNames[i], Tag = i };
                btn.Click += ModeBtn_Click;
                _modeButtons[i] = btn;
                ModePanel.Children.Add(btn);
            }
        }

        protected override void OnDeviceAttached()
        {
            TitleText.Text = Device?.Name ?? "LC 蠕动泵";
            try
            {
                var station = Device?.Parameters?.GetValue<double>("Modbus站号") ?? 1;
                var baud = Device?.Parameters?.GetValue<string>("波特率") ?? "9600";
                MetaText.Text = $"RS485 · Modbus RTU · {baud} 8N1 · 站号 {station:F0}";
            }
            catch { /* 参数缺失不影响屏幕打开 */ }
        }

        // ══════════════════════ 数据回填 ══════════════════════

        protected override void OnDataRefreshed(DeviceParameters o)
        {
            bool ok = o.GetNullableValue<bool>("Success") ?? false;
            SetLink(ok);
            if (!ok) { Blank(); return; }

            var flow = o.GetNullableValue<double>("FLOW");
            if (flow.HasValue)
            {
                _flow = flow.Value;
                FlowText.Text = flow.Value.ToString("F3", CultureInfo.InvariantCulture);
                if (!FlowBox.IsKeyboardFocusWithin) FlowBox.Text = FlowText.Text;
            }

            FillVolumeText.Text = Fmt(o, "FILL_VOLUME", "F3");
            FillTimeText.Text = Fmt(o, "FILL_TIME", "F1");
            KText.Text = Fmt(o, "K_VALUE", "F4");

            _running = o.GetNullableValue<bool>("RUNNING") ?? false;
            _full = o.GetNullableValue<bool>("FULL_SPEED") ?? false;
            SetRun(_running);
            FullBtn.Content = _full ? "取消全速" : "全速";

            _direction = (int)(o.GetNullableValue<double>("DIRECTION") ?? -1);
            string dirText = o.GetValue<string>("DIRECTION_TEXT") is { Length: > 0 } d ? d : "--";
            DirFullText.Text = $"{dirText}({(_direction >= 0 ? _direction.ToString() : "-")}) / {(_full ? "全速" : "否")}";
            Highlight(CwBtn, _direction == 1);
            Highlight(CcwBtn, _direction == 0);

            _mode = (int)(o.GetNullableValue<double>("MODE") ?? -1);
            ModeChipText.Text = o.GetValue<string>("MODE_TEXT") is { Length: > 0 } m ? m : "--";
            for (int i = 0; i < _modeButtons.Length; i++) Highlight(_modeButtons[i], i == _mode);

            int head = (int)(o.GetNullableValue<double>("HEAD_CODE") ?? -1);
            int tube = (int)(o.GetNullableValue<double>("TUBE_CODE") ?? -1);
            string headName = o.GetValue<string>("HEAD_NAME") ?? "--";
            bool ext = o.GetNullableValue<bool>("EXT_ENABLE") ?? false;
            HeadText.Text = $"泵头 {head} {headName} · 泵管 {tube} · 外控 {(ext ? "使能" : "未使能")}";

            _rs485 = o.GetNullableValue<bool>("RS485_ENABLED");
            SetRs485(_rs485);

            _suppress = true;
            try
            {
                if (head >= 0 && head < HeadCombo.Items.Count && HeadCombo.SelectedIndex != head)
                    HeadCombo.SelectedIndex = head;
                SetBox(TubeBox, o, "TUBE_CODE", "F0");
                SetBox(FillVolumeBox, o, "FILL_VOLUME", "F3");
                SetBox(FillTimeBox, o, "FILL_TIME", "F1");
                SetBox(BackSpeedBox, o, "BACK_SPEED", "F0");
                SetBox(BackAngleBox, o, "BACK_ANGLE", "F0");
                SetBox(CalVolumeBox, o, "CAL_VOLUME", "F3");
                SetBox(CalActualBox, o, "CAL_ACTUAL", "F3");
            }
            finally { _suppress = false; }
        }

        protected override void OnPollError(Exception ex)
        {
            SetLink(false);
            Blank();
        }

        private void Blank()
        {
            FlowText.Text = "--";
            FillVolumeText.Text = FillTimeText.Text = KText.Text = DirFullText.Text = "--";
            ModeChipText.Text = "--";
            HeadText.Text = "泵头 -- · 泵管 -- · 外控 --";
            SetRun(null);
            SetRs485(null);
        }

        private void SetLink(bool online)
        {
            LinkDot.Fill = online ? OkBrush : WaitBrush;
            LinkText.Text = online ? "通讯正常" : "等待应答";
            LinkText.Foreground = online ? OkText : DimText;
        }

        private void SetRun(bool? running)
        {
            if (!running.HasValue)
            {
                RunChip.BorderBrush = ChipBorder; RunText.Text = "—"; RunText.Foreground = DimText;
                return;
            }
            RunChip.BorderBrush = running.Value ? OkBrush : ChipBorder;
            RunText.Text = running.Value ? "运行中" : "已停止";
            RunText.Foreground = running.Value ? OkText : DimText;
        }

        private void SetRs485(bool? enabled)
        {
            if (!enabled.HasValue)
            {
                Rs485Btn.Content = "485 未知";
                Rs485Btn.Background = BtnBg; Rs485Btn.BorderBrush = BtnBorder;
                return;
            }
            Rs485Btn.Content = enabled.Value ? "485 已使能" : "485 禁能，点此使能";
            Rs485Btn.Background = enabled.Value ? ActiveBg : WarnBg;
            Rs485Btn.BorderBrush = enabled.Value ? ActiveBorder : WarnBrush;
        }

        private static void Highlight(Button b, bool active)
        {
            b.Background = active ? ActiveBg : BtnBg;
            b.BorderBrush = active ? ActiveBorder : BtnBorder;
        }

        private static string Fmt(DeviceParameters o, string key, string fmt)
        {
            var v = o.GetNullableValue<double>(key);
            return v.HasValue ? v.Value.ToString(fmt, CultureInfo.InvariantCulture) : "--";
        }

        private static void SetBox(TextBox box, DeviceParameters o, string key, string fmt)
        {
            if (!box.IsKeyboardFocusWithin) box.Text = Fmt(o, key, fmt);
        }

        // ══════════════════════ 流速 ══════════════════════

        private bool TryReadFlow(out double v)
        {
            if (!double.TryParse(FlowBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out v) || v <= 0)
            {
                Status("流速必须是大于 0 的数值");
                System.Media.SystemSounds.Beep.Play();
                FlowBox.Text = _flow.ToString("F3", CultureInfo.InvariantCulture);
                return false;
            }
            if (_mode > 0)
            {
                Status($"当前是{ModeNames[_mode]}模式，分配模式不能设置流速；请先切到传输模式");
                System.Media.SystemSounds.Beep.Play();
                return false;
            }
            return true;
        }

        private async void FlowBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (!TryReadFlow(out double v)) return;
            Keyboard.ClearFocus();
            await SendValueAsync("设置流速", v, "mL/min");
        }

        private async void SetFlowBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadFlow(out double v)) return;
            await SendValueAsync("设置流速", v, "mL/min");
        }

        private async void StartAtBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadFlow(out double v)) return;
            await SendValueAsync("按流速启动", v, "mL/min");
        }

        private async System.Threading.Tasks.Task SendValueAsync(string command, double v, string unit)
        {
            var input = new DeviceParameters();
            input.Variables.Add(new NumberParameter("TargetValue", -1e9, 1e9, v, command, false, unit));
            var output = await ExecuteForOutputAsync(command, input);
            ShowResult(command, output);
        }

        // ══════════════════════ 开关类 ══════════════════════

        private async void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            bool ok = await ExecuteCommandAsync("启动");
            Status(ok ? "启动命令已下发并回读确认" : "启动失败（485 未使能或泵在校准）");
            if (!ok) System.Media.SystemSounds.Hand.Play();
        }

        private async void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            bool ok = await ExecuteCommandAsync("停止");
            Status(ok ? "停止命令已下发并回读确认" : "停止失败");
            if (!ok) System.Media.SystemSounds.Hand.Play();
        }

        private async void FullBtn_Click(object sender, RoutedEventArgs e)
        {
            bool target = !_full;
            bool ok = await ExecuteCommandAsync("设置全速", new Dictionary<string, object> { ["TargetValue"] = target ? 1 : 0 });
            Status(ok ? (target ? "已切到全速" : "已取消全速") : "全速切换失败");
            if (!ok) System.Media.SystemSounds.Hand.Play();
        }

        private async void CwBtn_Click(object sender, RoutedEventArgs e) => await SendDirection(1);

        private async void CcwBtn_Click(object sender, RoutedEventArgs e) => await SendDirection(0);

        private async System.Threading.Tasks.Task SendDirection(int v)
        {
            if (_running)
            {
                Status("泵在运行中，请先停止再改方向");
                System.Media.SystemSounds.Beep.Play();
                return;
            }
            bool ok = await ExecuteCommandAsync("设置方向", new Dictionary<string, object> { ["TargetValue"] = v });
            // 手册对 0/1 的含义前后矛盾,这里只报寄存器值,文字以轮询回读的 DIRECTION_TEXT 为准
            Status(ok ? $"方向寄存器已写为 {v}，以泵实际转向为准" : "方向写入失败");
            if (!ok) System.Media.SystemSounds.Hand.Play();
        }

        private async void ModeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not int mode) return;
            if (mode == _mode) return;
            if (_running)
            {
                Status("泵在运行中，请先停止再切换运转模式");
                System.Media.SystemSounds.Beep.Play();
                return;
            }
            bool ok = await ExecuteCommandAsync("设置运转模式", new Dictionary<string, object> { ["TargetValue"] = mode });
            Status(ok ? $"已切到{ModeNames[mode]}模式" : "模式切换失败");
            if (!ok) System.Media.SystemSounds.Hand.Play();
        }

        private async void Rs485Btn_Click(object sender, RoutedEventArgs e)
        {
            if (_rs485 == true)
            {
                Status("485 已经是使能状态");
                return;
            }
            bool ok = await ExecuteCommandAsync("启用485控制");
            Status(ok ? "485 控制已使能" : "485 使能失败，检查站号与接线");
            if (!ok) System.Media.SystemSounds.Hand.Play();
        }

        // ══════════════════════ 参数与泵头 ══════════════════════

        private async void ParamBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (sender is not TextBox box || box.Tag is not string command) return;
            if (!BoxRanges.TryGetValue(command, out var range)) return;

            if (!double.TryParse(box.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double v)
                || v < range.min || v > range.max)
            {
                Status($"{command}:请输入 {range.min} ~ {range.max} 之间的数值");
                System.Media.SystemSounds.Beep.Play();
                return;
            }

            Keyboard.ClearFocus();
            await SendValueAsync(command, v, "");
        }

        private async void HeadCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress) return;
            int i = HeadCombo.SelectedIndex;
            if (i < 0) return;
            bool ok = await ExecuteCommandAsync("设置泵头", new Dictionary<string, object> { ["TargetValue"] = i });
            Status(ok ? $"泵头已设为 {i} {HeadNames[i]}（换泵头后请核对泵管编号与 K 值）" : "泵头写入失败");
            if (!ok) System.Media.SystemSounds.Hand.Play();
        }

        private void ShowResult(string command, DeviceParameters? output)
        {
            if (output is null)
            {
                Status($"{command} 无应答");
                System.Media.SystemSounds.Hand.Play();
                return;
            }
            bool ok = output.GetNullableValue<bool>("Success") ?? false;
            string msg = output.GetValue<string>("Message") ?? "";
            Status(msg.Length > 0 ? msg : (ok ? $"{command} 成功" : $"{command} 失败"));
            if (!ok) System.Media.SystemSounds.Hand.Play();
        }

        private void SelectAllOnFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
                tb.Dispatcher.BeginInvoke(new Action(() => tb.SelectAll()));
        }

        private void Status(string message)
        {
            StatusText.Text = $"{DateTime.Now:HH:mm:ss}  {message}";
        }
    }
}
