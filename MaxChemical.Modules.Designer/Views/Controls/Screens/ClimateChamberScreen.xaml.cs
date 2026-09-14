// ============================================================================
//  ClimateChamberScreen.xaml.cs
//  与 ClimateChamberScreen.xaml 配对。人工气候箱的嵌入式屏幕。
//
//  职责：
//    - 轮询「获取所有数据」，填温度 / 转速 / 各路输出与自整定状态 / 定时使能；
//    - 启停设备、电机、太阳光、剥离、消音、对时；
//    - 程序段的读取与写入。
//
//  ── 所有开关按钮都是"读回来的状态取反" ───────────────────────────
//  协议里太阳光、剥离这些是 0 关 / 1 开 / 2 翻转 三态。用"翻转"最省事，
//  但翻转是盲的：连点两下、或者仪器那边被人手动动过，界面和仪器就对不上了。
//  所以这里一律按**上一轮读回来的状态**算出要写 0 还是 1，明确赋值。
//
//  ── 有两类"发了但不动"，不是驱动的 bug ───────────────────────────
//    · 0x1000 启停 / 0x1002 电机：协议注了"需要 license 许可 lic_ok=1"，
//      而 lic_ok 没有给读取地址，上位机看不见；
//    · 三路 PID 自整定：协议注了"设备运行时才生效"。
//  两种情况仪器都会正常回显，所以命令返回"成功"。屏幕的做法是发完之后
//  提示"看状态灯"，把判据交给从仪器读回来的那一位，而不是命令的返回值。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using DevicePlugins.Devices;

namespace MaxChemical.Modules.Designer.Views.Controls.Screens
{
    public partial class ClimateChamberScreen : DeviceScreenBase
    {
        protected override string PollCommandName => "获取所有数据";

        private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0xB8, 0x4D));
        private static readonly Brush WaitBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x74));
        private static readonly Brush OffLamp = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x55));
        private static readonly Brush OnLamp = new SolidColorBrush(Color.FromRgb(0x4F, 0xD1, 0xC5));
        private static readonly Brush WarnLamp = new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x1A));
        private static readonly Brush NormalText = new SolidColorBrush(Color.FromRgb(0xC8, 0xCC, 0xD4));

        static ClimateChamberScreen()
        {
            OkBrush.Freeze();
            WaitBrush.Freeze();
            OffLamp.Freeze();
            OnLamp.Freeze();
            WarnLamp.Freeze();
            NormalText.Freeze();
        }

        // 上一轮从仪器读回来的开关状态。按钮点击时拿它算"该写 0 还是 1"。
        private bool _running, _motorOn, _sunOn, _stripOn;

        public ClimateChamberScreen()
        {
            InitializeComponent();
            // 一轮轮询最多要发六条 Modbus 读（协议里这台仪器的寄存器是散的，
            // 中间夹着不存在的地址，不能一把梭）。9600 波特下这一轮要几百毫秒，
            // 所以放到 1.5 秒一轮，别把串口占死。
            PollIntervalMs = 1500;
        }

        // ══════════════════════ 数据回填 ══════════════════════

        protected override void OnDataRefreshed(DeviceParameters o)
        {
            bool ok = o.GetNullableValue<bool>("Success") ?? false;
            SetLink(ok);
            if (!ok) { Blank(); return; }

            bool measureOk = o.GetNullableValue<bool>("MEASURE_OK") ?? false;
            bool statusOk = o.GetNullableValue<bool>("STATUS_OK") ?? false;

            // ── 测量值 ──────────────────────────────────────────
            if (measureOk)
            {
                TempPv.Text = Fmt(o, "TEMP_PV", "F1");
                TempSet.Text = Fmt(o, "TEMP_SET", "F1") + " ℃";
                SpeedPv.Text = Fmt(o, "SPEED_PV", "F0");
                SpeedSet.Text = Fmt(o, "SPEED_SET", "F0") + " RPM";
            }
            else
            {
                TempPv.Text = "--";
                TempSet.Text = "--";
                SpeedPv.Text = "--";
                SpeedSet.Text = "--";
            }

            // 标度提示：从设备参数里取，不是从读数猜的。
            TempScaleHint.Text = DescribeScale();

            // ── 状态位 ──────────────────────────────────────────
            if (statusOk)
            {
                _running = o.GetNullableValue<bool>("RUNNING") ?? false;
                _motorOn = o.GetNullableValue<bool>("MOTOR_RUN") ?? false;
                _sunOn = o.GetNullableValue<bool>("SUNLIGHT") ?? false;
                _stripOn = o.GetNullableValue<bool>("STRIP") ?? false;

                SetLamp(RunLamp, RunText, _running, "运行中", "已停止");
                SetLamp(MotorLamp, MotorText, _motorOn, "运行", "停止");
                SunLamp.Fill = _sunOn ? OnLamp : OffLamp;
                StripLamp.Fill = _stripOn ? OnLamp : OffLamp;

                TuneTempLamp.Fill = (o.GetNullableValue<bool>("TUNE_TEMP") ?? false) ? WarnLamp : OffLamp;
                TuneHumiLamp.Fill = (o.GetNullableValue<bool>("TUNE_HUMI") ?? false) ? WarnLamp : OffLamp;
                TuneCo2Lamp.Fill = (o.GetNullableValue<bool>("TUNE_CO2") ?? false) ? WarnLamp : OffLamp;

                SetLamp(TimerStartLamp, TimerStartText, o.GetNullableValue<bool>("TIMER_START_EN") ?? false, "已开启", "已关闭");
                SetLamp(TimerStopLamp, TimerStopText, o.GetNullableValue<bool>("TIMER_STOP_EN") ?? false, "已开启", "已关闭");

                // 调试开关亮着是不正常的状态，用告警色而不是正常色。
                IoDebugLamp.Fill = (o.GetNullableValue<bool>("IO_DEBUG") ?? false) ? WarnLamp : OffLamp;
                RemainDebugLamp.Fill = (o.GetNullableValue<bool>("REMAIN_DEBUG") ?? false) ? WarnLamp : OffLamp;

                StartBtn.IsEnabled = !_running;
                StopBtn.IsEnabled = _running;
                MotorBtn.Content = _motorOn ? "电机 开" : "电机 关";
                SunBtn.Content = _sunOn ? "太阳光 开" : "太阳光 关";
                StripBtn.Content = _stripOn ? "剥离 开" : "剥离 关";
            }

            // 两块只读到一块时说清楚是哪一块没读到 —— 这台仪器地址是散的，
            // 哪一段读不到直接指向"那一段的地址在这台机器上不存在"。
            if (!measureOk || !statusOk)
                Say(!measureOk
                    ? "测量值那一块没读到（0x0010/0x0011/0x0016/0x0017）。若一直读不到，" +
                      "试试把设备参数「测量值功能码」从 03 改成 04"
                    : "状态那一块没读到（0x1000 段）。检查站号和偶校验。");
        }

        protected override void OnPollError(Exception ex) => SetLink(false);

        /// <summary>从设备参数里取温度标度，显示成一句人话。</summary>
        private string DescribeScale()
        {
            try
            {
                int d = (int)(Device?.Parameters?.GetValue<double>("温度小数位") ?? 1);
                return d == 0 ? "整数（÷1）" : $"{d} 位小数（÷{Math.Pow(10, d):F0}）";
            }
            catch { return "--"; }
        }

        private void SetLink(bool ok)
        {
            LinkDot.Fill = ok ? OkBrush : WaitBrush;
            LinkText.Text = ok ? "已连接" : "等待数据";
        }

        private static void SetLamp(Ellipse lamp, TextBlock text, bool on, string onText, string offText)
        {
            lamp.Fill = on ? OnLamp : OffLamp;
            text.Text = on ? onText : offText;
            text.Foreground = NormalText;
        }

        private void Blank()
        {
            TempPv.Text = "--";
            TempSet.Text = "--";
            SpeedPv.Text = "--";
            SpeedSet.Text = "--";
            ClockText.Text = "--";
            RunText.Text = "--";
            MotorText.Text = "--";
            TimerStartText.Text = "--";
            TimerStopText.Text = "--";
            foreach (var lamp in new[] { RunLamp, MotorLamp, SunLamp, StripLamp,
                                         TuneTempLamp, TuneHumiLamp, TuneCo2Lamp,
                                         TimerStartLamp, TimerStopLamp, IoDebugLamp, RemainDebugLamp })
                lamp.Fill = OffLamp;
        }

        // ══════════════════════ 操作 ══════════════════════

        private async void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            Say("正在下发启动…");
            bool ok = await ExecuteCommandAsync("启动设备");
            // 仪器会正常回显，但真的动不动还要看 lic_ok。判据交给状态灯。
            Say(ok ? "启动指令已下发 —— 看「设备运行」灯有没有亮；不亮多半是仪器侧没有 license"
                   : "启动指令下发失败");
        }

        private async void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            Say("正在下发停止…");
            Say(await ExecuteCommandAsync("停止设备") ? "停止指令已下发" : "停止指令下发失败");
        }

        private async void MotorBtn_Click(object sender, RoutedEventArgs e)
        {
            string cmd = _motorOn ? "电机停止" : "电机启动";
            Say(await ExecuteCommandAsync(cmd) ? $"{cmd}指令已下发 —— 看电机灯" : $"{cmd}下发失败");
        }

        private async void SunBtn_Click(object sender, RoutedEventArgs e)
        {
            // 明确赋值 0 / 1，不用协议里的 2（翻转）。理由见文件抬头。
            int target = _sunOn ? 0 : 1;
            Say(await ExecuteCommandAsync("太阳光输出", new Dictionary<string, object> { ["TargetValue"] = target })
                ? (target == 1 ? "已下发：打开太阳光" : "已下发：关闭太阳光") : "太阳光指令下发失败");
        }

        private async void StripBtn_Click(object sender, RoutedEventArgs e)
        {
            int target = _stripOn ? 0 : 1;
            Say(await ExecuteCommandAsync("剥离输出", new Dictionary<string, object> { ["TargetValue"] = target })
                ? (target == 1 ? "已下发：开启剥离" : "已下发：关闭剥离") : "剥离指令下发失败");
        }

        private async void BuzzerBtn_Click(object sender, RoutedEventArgs e)
        {
            Say(await ExecuteCommandAsync("蜂鸣器清除") ? "已下发消音" : "消音下发失败");
        }

        private async void SyncClockBtn_Click(object sender, RoutedEventArgs e)
        {
            Say("正在对时（要连发七条写命令）…");
            bool ok = await ExecuteCommandAsync("同步系统时间");
            Say(ok ? "已把电脑时间写进仪器" : "对时失败 —— 可能只写进去了一部分，请重发一次");

            var clock = await ExecuteForOutputAsync("读取仪器时间", null);
            if (clock != null && (clock.GetNullableValue<bool>("Success") ?? false))
                ClockText.Text = clock.GetValue<string>("Clock") ?? "--";
        }

        // ── 程序段 ──────────────────────────────────────────────

        private async void SegReadBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!TryInt(SegBox, out int seg)) { Say("段号填的不是整数"); return; }

            Say($"正在读第 {seg} 段…");
            var input = new DeviceParameters();
            input.Variables.Add(new NumberParameter("Segment", 0, 60, seg, "段号", false));

            var o = await ExecuteForOutputAsync("读程序段", input);
            if (o == null || !(o.GetNullableValue<bool>("Success") ?? false))
            {
                Say($"第 {seg} 段读不到。如果每一段都读不到，多半是设备参数「程序段基址」的进制不对 —— " +
                    "文档写的 2000 没带 0x，试试改成 8192（=0x2000）");
                return;
            }

            SegTempBox.Text = Fmt(o, "Temp", "F1");
            SegTimeBox.Text = Fmt(o, "Time", "F0");
            SegSpeedBox.Text = Fmt(o, "Speed", "F0");
            Say($"第 {seg} 段已读出（基址 {Fmt(o, "BaseAddress", "F0")}）");
        }

        private async void SegWriteBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!TryInt(SegBox, out int seg)) { Say("段号填的不是整数"); return; }
            if (!TryNum(SegTempBox, out double temp)) { Say("设定温度填的不是数字"); return; }
            if (!TryNum(SegTimeBox, out double time)) time = 0;
            if (!TryNum(SegSpeedBox, out double speed)) speed = 0;

            Say($"正在写第 {seg} 段（三条单寄存器写）…");
            bool ok = await ExecuteCommandAsync("写程序段", new Dictionary<string, object>
            {
                ["Segment"] = seg,
                ["Temp"] = temp,
                ["Time"] = time,
                ["Speed"] = speed,
            });

            // 协议只支持 06，三个值是分三条发的。中间断了会写进半套，
            // 所以失败时提示"读回来核对"，而不是让人以为什么都没写进去。
            Say(ok ? $"第 {seg} 段已写入，建议点「读取本段」核对一遍"
                   : $"第 {seg} 段写入失败 —— 可能已经写进去一部分，请点「读取本段」核对");
        }

        // ══════════════════════ 小工具 ══════════════════════

        private void Say(string text)
        {
            StatusText.Text = text;
        }

        private static bool TryNum(TextBox box, out double value) =>
            double.TryParse(box.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        private static bool TryInt(TextBox box, out int value) =>
            int.TryParse(box.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        private static string Fmt(DeviceParameters o, string key, string format)
        {
            var v = o.GetNullableValue<double>(key);
            return v.HasValue ? v.Value.ToString(format, CultureInfo.InvariantCulture) : "--";
        }

        private void SelectAllOnFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox tb) tb.SelectAll();
        }
    }
}
