// ============================================================================
//  ShakingIncubatorScreen.xaml.cs
//  与 ShakingIncubatorScreen.xaml 配对。恒温振荡培养箱的嵌入式屏幕。
//
//  职责：
//    - 轮询「获取所有数据」，填温度 / 转速 / 风速 / 运行状态 / 报警 / 时钟；
//    - 按仪器自报的五个功能开关，把不存在的功能整块置灰；
//    - 启动 / 停止 / 照明 / 灭菌 / 切模式 / 对时；
//    - 下发定值模式的温度、转速、风速、运行时长。
//
//  ── 输入框为什么不做本地乐观更新 ──────────────────────────────────
//  仪器的温度设定值(0x0008)是只读的，改设定要走"编辑窗口 + 写标志"那套
//  流程。仪器有没有真收下，只能看下一轮读回来的 0x0008 变没变。
//  所以按下"下发"之后界面不改任何数值，等轮询把仪器的实际值填回来 ——
//  仪器因为超量程拒了的话，界面会如实退回原值，而不是显示一个假的成功。
//
//  ── 回填与输入的冲突 ──────────────────────────────────────────────
//  轮询每秒回填一次输入框。人正在框里敲字时不能冲掉他敲的内容，
//  所以回填前一律先看 IsKeyboardFocusWithin。下拉框同理，用 _suppress
//  挡住回填触发的 SelectionChanged，否则会变成"回填→下发→仪器变→回填"
//  的自激循环。
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
    public partial class ShakingIncubatorScreen : DeviceScreenBase
    {
        protected override string PollCommandName => "获取所有数据";

        /// <summary>回填期间置位，挡住 SelectionChanged 反过来又下发一次。</summary>
        private bool _suppress;

        private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0xB8, 0x4D));
        private static readonly Brush WaitBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x74));
        private static readonly Brush AlarmBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x4B, 0x5A));
        private static readonly Brush OnText = new SolidColorBrush(Color.FromRgb(0x7F, 0xE3, 0x9B));
        private static readonly Brush OffText = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEE));

        static ShakingIncubatorScreen()
        {
            OkBrush.Freeze();
            WaitBrush.Freeze();
            AlarmBrush.Freeze();
            OnText.Freeze();
            OffText.Freeze();
        }

        public ShakingIncubatorScreen()
        {
            InitializeComponent();
            // 一次轮询要发两条 Modbus 读(受手册单次 32 个寄存器的限制)，
            // 9600 波特下两条大约 250ms。500ms 轮一次会让串口一直占着，
            // 手动下发命令要排队等，操作起来发黏。1 秒足够看温度了。
            PollIntervalMs = 1000;
        }

        // ══════════════════════ 数据回填 ══════════════════════

        protected override void OnDataRefreshed(DeviceParameters o)
        {
            bool ok = o.GetNullableValue<bool>("Success") ?? false;
            SetLink(ok);
            if (!ok) { Blank(); return; }

            // ── 主读数 ──────────────────────────────────────────
            TempPv.Text = Fmt(o, "TEMP_PV", "F1");
            TempSet.Text = Fmt(o, "TEMP_SET", "F1") + " ℃";
            TempRange.Text = $"{Fmt(o, "TEMP_LO", "F1")} ~ {Fmt(o, "TEMP_HI", "F1")} ℃";

            SpeedPv.Text = Fmt(o, "SPEED_PV", "F0");
            SpeedSet.Text = Fmt(o, "SPEED_SET", "F0") + " RPM";
            SpeedRange.Text = $"{Fmt(o, "SPEED_LO", "F0")} ~ {Fmt(o, "SPEED_HI", "F0")}";

            FanPv.Text = Fmt(o, "FAN_PV", "F0");
            FanSet.Text = Fmt(o, "FAN_SET", "F0") + " 档";
            FanRange.Text = $"{Fmt(o, "FAN_LO", "F0")} ~ {Fmt(o, "FAN_HI", "F0")}";

            // ── 功能开关决定哪些块可用 ──────────────────────────
            //
            // 仪器自己报了这台机器有没有转速/风机/照明/灭菌/预约。
            // 没有的功能整块置灰并给一句说明，比留一个永远不动的 0 讲得清楚。
            bool hasSpeed = o.GetNullableValue<bool>("FUNC_SPEED") ?? false;
            bool hasFan = o.GetNullableValue<bool>("FUNC_FAN") ?? false;
            bool hasLight = o.GetNullableValue<bool>("FUNC_LIGHT") ?? false;
            bool hasSteri = o.GetNullableValue<bool>("FUNC_STERILIZE") ?? false;

            SetFeature(hasSpeed, SpeedOff, SpeedPv, SpeedUnit, SpeedSet, SpeedRange);
            SetFeature(hasFan, FanOff, FanPv, FanUnit, FanSet, FanRange);
            SetSpeedPanel.IsEnabled = hasSpeed;
            SetFanPanel.IsEnabled = hasFan;
            LightBtn.IsEnabled = hasLight;
            SterilizeBtn.IsEnabled = hasSteri;

            // 照明/灭菌的当前状态来自报警字 bit8/bit9（驱动已经拆好），
            // 不是 0x0038/0x0039 —— 那两个是写入口，回读的是上位机写过的值。
            bool lightOn = o.GetNullableValue<bool>("LIGHT_ON") ?? false;
            bool steriOn = o.GetNullableValue<bool>("STERILIZE_ON") ?? false;
            LightBtn.Content = hasLight ? (lightOn ? "照明 开" : "照明 关") : "照明";
            LightBtn.Foreground = lightOn ? OnText : OffText;
            SterilizeBtn.Content = hasSteri ? (steriOn ? "灭菌 开" : "灭菌 关") : "灭菌";
            SterilizeBtn.Foreground = steriOn ? OnText : OffText;

            // ── 运行状态 ────────────────────────────────────────
            RunStateText.Text = o.GetValue<string>("RUN_STATE_TEXT") ?? "--";
            ModeText.Text = o.GetValue<string>("MODE_TEXT") ?? "--";
            SegCycleText.Text = $"{Fmt(o, "RUN_SEGMENT", "F0")}/{Fmt(o, "SET_SEGMENT", "F0")} 段 · " +
                                  $"{Fmt(o, "RUN_CYCLE", "F0")}/{Fmt(o, "SET_CYCLE", "F0")} 周";
            TimeText.Text = $"{Fmt(o, "ELAPSED_H", "F0")}:{Pad2(o, "ELAPSED_M")} / " +
                                  $"{Fmt(o, "SET_H", "F0")}:{Pad2(o, "SET_M")}";

            string clock = o.GetValue<string>("CLOCK") ?? "";
            ClockText.Text = string.IsNullOrWhiteSpace(clock) ? "--" : clock;

            bool running = o.GetNullableValue<bool>("RUNNING") ?? false;
            StartBtn.IsEnabled = !running;
            StopBtn.IsEnabled = running;

            // ── 报警 ────────────────────────────────────────────
            string alarms = o.GetValue<string>("ALARM_TEXT") ?? "";
            bool hasAlarm = (o.GetNullableValue<bool>("HAS_ALARM") ?? false) || alarms.Length > 0;
            AlarmChip.Visibility = hasAlarm ? Visibility.Visible : Visibility.Collapsed;
            if (hasAlarm)
            {
                int n = string.IsNullOrEmpty(alarms) ? 1 : alarms.Split('、').Length;
                AlarmCount.Text = n.ToString(CultureInfo.InvariantCulture);
                AlarmChip.ToolTip = alarms.Length > 0 ? alarms : "仪器报有报警，但报警位全为 0";
                StatusText.Text = "报警：" + (alarms.Length > 0 ? alarms : "仪器置了报警标志但没有具体位");
                StatusText.Foreground = AlarmBrush;
            }

            // ── 回填 ────────────────────────────────────────────
            _suppress = true;
            try
            {
                int mode = (int)(o.GetNullableValue<double>("MODE") ?? 0);
                if (mode >= 0 && mode < ModeCombo.Items.Count) ModeCombo.SelectedIndex = mode;

                // 正在输入时不要把用户敲的字冲掉
                if (!SetTempBox.IsKeyboardFocusWithin) SetTempBox.Text = Fmt(o, "TEMP_SET", "F1");
                if (!SetSpeedBox.IsKeyboardFocusWithin) SetSpeedBox.Text = Fmt(o, "SPEED_SET", "F0");
                if (!SetFanBox.IsKeyboardFocusWithin) SetFanBox.Text = Fmt(o, "FAN_SET", "F0");
                if (!SetHourBox.IsKeyboardFocusWithin) SetHourBox.Text = Fmt(o, "SET_H", "F0");
                if (!SetMinuteBox.IsKeyboardFocusWithin) SetMinuteBox.Text = Fmt(o, "SET_M", "F0");
            }
            finally { _suppress = false; }

            // 抬头补一句身份，串口选错时这一行会很不一样
            double type = o.GetNullableValue<double>("PRODUCT_TYPE") ?? 0;
            if (type > 0)
                SubTitle.Text = $"Modbus RTU · RS485 · 产品类型编号 0x{(int)type:X4} · 序列号 {Fmt(o, "SERIAL_NO", "F0")}";
        }

        protected override void OnPollError(Exception ex) => SetLink(false);

        /// <summary>整块功能不存在时：藏掉数值、露出说明文字。</summary>
        private static void SetFeature(bool available, TextBlock offNotice, params TextBlock[] values)
        {
            offNotice.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
            foreach (var v in values)
                v.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetLink(bool ok)
        {
            LinkDot.Fill = ok ? OkBrush : WaitBrush;
            LinkText.Text = ok ? "已连接" : "等待数据";
        }

        private void Blank()
        {
            TempPv.Text = "--";
            TempSet.Text = "--";
            TempRange.Text = "--";
            SpeedPv.Text = "--";
            SpeedSet.Text = "--";
            SpeedRange.Text = "--";
            FanPv.Text = "--";
            FanSet.Text = "--";
            FanRange.Text = "--";
            RunStateText.Text = "--";
            ModeText.Text = "--";
            SegCycleText.Text = "--";
            TimeText.Text = "--";
            ClockText.Text = "--";
            AlarmChip.Visibility = Visibility.Collapsed;
        }

        // ══════════════════════ 操作 ══════════════════════

        private async void StartBtn_Click(object sender, RoutedEventArgs e)
        {
            Say("正在下发启动…");
            Say(await ExecuteCommandAsync("启动") ? "启动指令已下发" : "启动指令下发失败");
        }

        private async void StopBtn_Click(object sender, RoutedEventArgs e)
        {
            Say("正在下发停止…");
            Say(await ExecuteCommandAsync("停止") ? "停止指令已下发" : "停止指令下发失败");
        }

        private async void LightBtn_Click(object sender, RoutedEventArgs e)
        {
            // 按当前状态取反。状态取自报警字里的 bit8，不是我们上次写的值 ——
            // 所以连点两次不会因为本地状态和仪器状态不同步而"点了没反应"。
            bool on = ReferenceEquals(LightBtn.Foreground, OnText);
            Say(await ExecuteCommandAsync("照明开关", new Dictionary<string, object> { ["TargetValue"] = on ? 0 : 1 })
                ? (on ? "已关闭照明" : "已开启照明") : "照明指令下发失败");
        }

        private async void SterilizeBtn_Click(object sender, RoutedEventArgs e)
        {
            bool on = ReferenceEquals(SterilizeBtn.Foreground, OnText);
            Say(await ExecuteCommandAsync("灭菌开关", new Dictionary<string, object> { ["TargetValue"] = on ? 0 : 1 })
                ? (on ? "已关闭灭菌" : "已开启灭菌") : "灭菌指令下发失败");
        }

        private async void SyncClockBtn_Click(object sender, RoutedEventArgs e)
        {
            Say(await ExecuteCommandAsync("同步系统时间") ? "已把电脑时间写进仪器" : "对时失败");
        }

        private async void ModeCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress) return;
            int idx = ModeCombo.SelectedIndex;
            if (idx < 0) return;

            Say(await ExecuteCommandAsync("设置当前模式", new Dictionary<string, object> { ["TargetValue"] = idx })
                ? $"已切到{(idx == 0 ? "定值模式" : $"程式模式 {idx}")}" : "切换模式失败");
        }

        private async void ApplyBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!TryNum(SetTempBox, out double temp)) { Say("温度填的不是数字"); return; }
            if (!TryNum(SetSpeedBox, out double speed)) speed = 0;
            if (!TryNum(SetFanBox, out double fan)) fan = 0;
            if (!TryNum(SetHourBox, out double hour)) hour = 0;
            if (!TryNum(SetMinuteBox, out double minute)) minute = 0;

            Say("正在下发定值参数…");
            bool ok = await ExecuteCommandAsync("设置定值参数", new Dictionary<string, object>
            {
                ["Temp"] = temp,
                ["Speed"] = speed,
                ["Fan"] = fan,
                ["Hour"] = hour,
                ["Minute"] = minute,
            });

            // 说"已下发"而不是"已生效"：写标志落下去仪器就应答了，
            // 但它可能因为超出自己的量程而不采纳。真凭据是下一轮读回来的设定值。
            Say(ok ? "定值参数已下发，等仪器回读确认" : "定值参数下发失败");
        }

        // ══════════════════════ 小工具 ══════════════════════

        private void Say(string text)
        {
            StatusText.Text = text;
            StatusText.Foreground = OffText;
        }

        private static bool TryNum(TextBox box, out double value) =>
            double.TryParse(box.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        private static string Fmt(DeviceParameters o, string key, string format)
        {
            var v = o.GetNullableValue<double>(key);
            return v.HasValue ? v.Value.ToString(format, CultureInfo.InvariantCulture) : "--";
        }

        private static string Pad2(DeviceParameters o, string key)
        {
            var v = o.GetNullableValue<double>(key);
            return v.HasValue ? ((int)v.Value).ToString("00", CultureInfo.InvariantCulture) : "--";
        }

        private void SelectAllOnFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox tb) tb.SelectAll();
        }
    }
}
