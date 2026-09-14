// ============================================================================
//  PeristalticPumpSN100CQScreen.xaml.cs
//  与 PeristalticPumpSN100CQScreen.xaml 配对。创锐 SN100CQ 蠕动泵的嵌入式屏幕。
//
//  职责:
//    - 轮询「获取所有数据」,填实际转速 / 设定转速 / 方向 / 全速 / 量程 / 通讯参数;
//    - 转速下发、按转速启动、启动 / 停止、方向、全速、量程三项。
//
//  所有写命令的结果都以驱动回读比对为准:这台泵不在 485 控制方式时,写命令
//  回显成功、值却不变,驱动会把这一情况写进 Message,屏幕原样显示。
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
    public partial class PeristalticPumpSN100CQScreen : DeviceScreenBase
    {
        protected override string PollCommandName => "获取所有数据";

        private double _maxSpeed = 150.0;
        private double _speedSet;
        private bool _running;
        private bool _full;
        private int _direction = -1;

        private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0xB8, 0x4D));
        private static readonly Brush WaitBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x74));
        private static readonly Brush DimText = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAA));
        private static readonly Brush OkText = new SolidColorBrush(Color.FromRgb(0x7F, 0xE3, 0x9B));
        private static readonly Brush ChipBorder = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x55));
        private static readonly Brush BtnBg = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42));
        private static readonly Brush BtnBorder = new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x64));
        private static readonly Brush ActiveBg = new SolidColorBrush(Color.FromRgb(0x1F, 0x5F, 0x5A));
        private static readonly Brush ActiveBorder = new SolidColorBrush(Color.FromRgb(0x4F, 0xD1, 0xC5));

        static PeristalticPumpSN100CQScreen()
        {
            foreach (var b in new[] { OkBrush, WaitBrush, DimText, OkText, ChipBorder, BtnBg, BtnBorder, ActiveBg, ActiveBorder })
                b.Freeze();
        }

        public PeristalticPumpSN100CQScreen()
        {
            InitializeComponent();
            PollIntervalMs = 1000;   // 一轮三帧 Modbus,1s 足够
        }

        protected override void OnDeviceAttached()
        {
            TitleText.Text = Device?.Name ?? "SN100CQ 蠕动泵";
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

            SpeedPvText.Text = Fmt(o, "SPEED_PV", "F1");

            var set = o.GetNullableValue<double>("SPEED_SET");
            if (set.HasValue)
            {
                _speedSet = set.Value;
                SpeedSetText.Text = set.Value.ToString("F1", CultureInfo.InvariantCulture);
                if (!SpeedBox.IsKeyboardFocusWithin) SpeedBox.Text = SpeedSetText.Text;
            }

            var max = o.GetNullableValue<double>("MAX_SPEED");
            if (max.HasValue && max.Value >= 2) _maxSpeed = max.Value;
            SpeedLabel.Text = $"转速设定 0.1~{_maxSpeed:F0} rpm（回车下发）";

            _running = o.GetNullableValue<bool>("RUNNING") ?? false;
            _full = o.GetNullableValue<bool>("FULL_SPEED") ?? false;
            SetRun(_running);
            FullSpeedText.Text = _full ? "全速中" : "否";
            FullBtn.Content = _full ? "取消全速" : "全速";

            _direction = (int)(o.GetNullableValue<double>("DIRECTION") ?? -1);
            DirectionText.Text = o.GetValue<string>("DIRECTION_TEXT") is { Length: > 0 } d ? d : "--";
            Highlight(CwBtn, _direction == 1);
            Highlight(CcwBtn, _direction == 0);

            SetBox(MaxSpeedBox, o, "MAX_SPEED", "F1");
            SetBox(AnalogHiBox, o, "ANALOG_HI", "F1");
            SetBox(AnalogLoBox, o, "ANALOG_LO", "F1");
            AddressText.Text = Fmt(o, "ADDRESS", "F0");
            BaudText.Text = o.GetValue<string>("BAUD_TEXT") is { Length: > 0 } b ? b : "--";
        }

        protected override void OnPollError(Exception ex)
        {
            SetLink(false);
            Blank();
        }

        private void Blank()
        {
            SpeedPvText.Text = "--";
            SpeedSetText.Text = "--";
            DirectionText.Text = "--";
            FullSpeedText.Text = "--";
            AddressText.Text = "--";
            BaudText.Text = "--";
            SetRun(null);
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

        // ══════════════════════ 转速 ══════════════════════

        private bool TryReadSpeed(out double v)
        {
            if (!double.TryParse(SpeedBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out v)
                || v < 0.1 || v > _maxSpeed)
            {
                Status($"转速需在 0.1 ~ {_maxSpeed:F1} rpm 之间（上限是泵当前的手控最大转速）");
                System.Media.SystemSounds.Beep.Play();
                SpeedBox.Text = _speedSet.ToString("F1", CultureInfo.InvariantCulture);
                return false;
            }
            return true;
        }

        private async void SpeedBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (!TryReadSpeed(out double v)) return;
            Keyboard.ClearFocus();
            await SendSpeedAsync("设置转速", v);
        }

        private async void SetSpeedBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadSpeed(out double v)) return;
            await SendSpeedAsync("设置转速", v);
        }

        private async void StartAtBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadSpeed(out double v)) return;
            await SendSpeedAsync("按转速启动", v);
        }

        private async System.Threading.Tasks.Task SendSpeedAsync(string command, double v)
        {
            var input = new DeviceParameters();
            input.Variables.Add(new NumberParameter("TargetValue", 0, 1000, v, "转速设定", false, "rpm"));
            var output = await ExecuteForOutputAsync(command, input);
            ShowResult(command, output);
        }

        // ══════════════════════ 开关类 ══════════════════════

        private async void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            bool ok = await ExecuteCommandAsync("启动");
            Status(ok ? "启动命令已下发并回读确认" : "启动失败");
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
                // 运行中换向对泵管不友好,也可能把管路里的液体打回去;先停再换
                Status("泵在运行中，请先停止再改方向");
                System.Media.SystemSounds.Beep.Play();
                return;
            }
            bool ok = await ExecuteCommandAsync("设置方向", new Dictionary<string, object> { ["TargetValue"] = v });
            Status(ok ? $"方向已设为{(v == 1 ? "顺时针" : "逆时针")}" : "方向写入失败（泵可能不在 485 控制方式）");
            if (!ok) System.Media.SystemSounds.Hand.Play();
        }

        // ══════════════════════ 量程 ══════════════════════

        private async void ParamBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (sender is not TextBox box || box.Tag is not string command) return;

            if (!double.TryParse(box.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double v) || v < 0 || v > 150)
            {
                Status($"{command}:请输入 0 ~ 150 之间的数值");
                System.Media.SystemSounds.Beep.Play();
                return;
            }

            Keyboard.ClearFocus();
            var input = new DeviceParameters();
            input.Variables.Add(new NumberParameter("TargetValue", 0, 1000, v, command, false, "rpm"));
            var output = await ExecuteForOutputAsync(command, input);
            ShowResult(command, output);
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
