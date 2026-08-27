// ============================================================================
//  PenMeterScreen.xaml.cs
//  与 PenMeterScreen.xaml 配对。笔式酸度-电导率计的嵌入式屏幕。
//
//  ── 为什么可以在一次轮询里读四条命令 ──────────────────────────────
//  DeviceScreenBase 只支持一条轮询命令，但这台仪器是"主动推 + 驱动缓存"的模型：
//  「获取测量数据」「获取工作状态」「获取设置数据」「获取校准数据」四条命令在驱动里
//  全都只是抄内存里的快照，一次串口往返都不产生。所以在 OnDataRefreshed 里
//  顺手把另外三条也读了，代价可以忽略。仍加了一个 _auxBusy 防重入，
//  免得界面卡顿时排起长队。
//
//  ── 三个区块按工作模式启用 ────────────────────────────────────────
//  仪器只有进了配置模式才推 0x02、进了校准模式才推 0x03。所以：
//    · 配置区块在收到配置之前是禁用的，并给出"先切到配置模式"的提示；
//    · 「确认本次校准」按钮只在校准模式且读数稳定时才可点。
//  这比让人对着一排 "--" 猜哪里坏了要清楚得多。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DevicePlugins.Devices;

namespace MaxChemical.Modules.Designer.Views.Controls.Screens
{
    public partial class PenMeterScreen : DeviceScreenBase
    {
        protected override string PollCommandName => "获取测量数据";

        /// <summary>回填期间置位，挡住 SelectionChanged 反过来又下发一次配置。</summary>
        private bool _suppress;

        /// <summary>附加读取（工作状态/配置/校准）的防重入标志。</summary>
        private bool _auxBusy;

        /// <summary>当前工作模式：0关机 1测量 2配置 4校准。</summary>
        private int _workMode = 1;

        /// <summary>是否已经拿到过仪器的配置。没有就不允许下发（驱动那边也会拒绝）。</summary>
        private bool _hasSettings;

        private bool _holdOn;

        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4F, 0xD1, 0xC5));
        private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0xB8, 0x4D));
        private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x1A));
        private static readonly Brush FailBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0x48, 0x48));
        private static readonly Brush IdleBrush = new SolidColorBrush(Color.FromRgb(0x43, 0x43, 0x4E));
        private static readonly Brush IdleText = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x74));
        private static readonly Brush DimText = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAA));
        private static readonly Brush ChipBg = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2E));
        private static readonly Brush ChipOnBg = new SolidColorBrush(Color.FromRgb(0x1C, 0x3E, 0x3B));
        private static readonly Brush Dark = new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x18));

        static PenMeterScreen()
        {
            Accent.Freeze(); OkBrush.Freeze(); WarnBrush.Freeze(); FailBrush.Freeze();
            IdleBrush.Freeze(); IdleText.Freeze(); DimText.Freeze();
            ChipBg.Freeze(); ChipOnBg.Freeze(); Dark.Freeze();
        }

        public PenMeterScreen()
        {
            InitializeComponent();
        }

        // ══════════════════════ 主轮询：测量数据 ══════════════════════

        protected override void OnDataRefreshed(DeviceParameters o)
        {
            bool ok = o.GetNullableValue<bool>("Success") ?? false;
            SetLink(ok);

            if (ok)
            {
                // pH/ORP 只需两位小数就够；电导率在 uS/cm 下动辄四位数，
                // 统一 F2 反而不好读，所以按当前单位挑格式。
                string unit = o.GetValue<string>("VALUE_UNIT") ?? "";
                double value = o.GetNullableValue<double>("VALUE") ?? 0;
                MainValue.Text = value.ToString(PickFormat(unit), CultureInfo.InvariantCulture);
                MainUnit.Text = unit;

                TempValue.Text = Fmt(o, "TEMP", "F1");
                TempUnit.Text = o.GetValue<string>("TEMP_UNIT") ?? "℃";

                SetChip(StableChip, StableText, o.GetNullableValue<bool>("STABLE") ?? false, "稳定", "漂移", OkBrush, WarnBrush);

                // 保持状态以仪器上报的为准：操作员也可能直接按笔上的按键锁定读数，
                // 只跟着本地 _holdOn 走的话，下一次点"数据保持"会发出相反的命令。
                _holdOn = o.GetNullableValue<bool>("HOLD") ?? _holdOn;
                SetChip(HoldChip, HoldText, _holdOn, "已保持", "保持", Accent, null);

                bool hi = o.GetNullableValue<bool>("OVER_HIGH") ?? false;
                bool lo = o.GetNullableValue<bool>("OVER_LOW") ?? false;
                if (hi || lo)
                {
                    LimitChip.BorderBrush = FailBrush;
                    LimitText.Text = hi ? "超上限" : "超下限";
                    LimitText.Foreground = FailBrush;
                }
                else
                {
                    LimitChip.BorderBrush = IdleBrush;
                    LimitText.Text = "限值正常";
                    LimitText.Foreground = IdleText;
                }

                SetBattery(o.GetNullableValue<double>("BATTERY"));
                SetModeChips((int)(o.GetNullableValue<double>("MODE") ?? 0));
                SetCalibrationLamps((int)(o.GetNullableValue<double>("CAL_FLAGS") ?? 0));

                ZeroValue.Text = Fmt(o, "ZERO_POTENTIAL", "F1");
                SlopeValue.Text = Fmt(o, "SLOPE", "F1");
            }
            else
            {
                Blank();
            }

            // 顺手把工作状态 / 配置 / 校准也刷一遍（都是读缓存，不产生通信）
            if (!_auxBusy)
            {
                _auxBusy = true;
                _ = RefreshAuxAsync();
            }
        }

        protected override void OnPollError(Exception ex)
        {
            SetLink(false);
            Blank();
        }

        private void Blank()
        {
            MainValue.Text = "--";
            TempValue.Text = "--";
            ZeroValue.Text = "--";
            SlopeValue.Text = "--";
            BatteryText.Text = "--";
            BatteryFill.Width = 0;

            SetChip(StableChip, StableText, false, "稳定", "稳定", OkBrush, null);
            SetChip(HoldChip, HoldText, false, "保持", "保持", Accent, null);
            LimitChip.BorderBrush = IdleBrush;
            LimitText.Text = "限值正常";
            LimitText.Foreground = IdleText;

            SetCalibrationLamps(0);
        }

        /// <summary>电导率/电阻率的量程跨好几个数量级，小数位数按单位挑。</summary>
        private static string PickFormat(string unit) => unit switch
        {
            "pH" => "F2",
            "mV" => "F1",
            "%" => "F1",
            "uS/cm" => "F1",
            "mS/cm" => "F3",
            "ppt" => "F2",
            _ => "F2",
        };

        private static string Fmt(DeviceParameters o, string key, string fmt)
        {
            var v = o.GetNullableValue<double>(key);
            return v.HasValue ? v.Value.ToString(fmt, CultureInfo.InvariantCulture) : "--";
        }

        // ══════════════════════ 附加读取 ══════════════════════

        private async Task RefreshAuxAsync()
        {
            try
            {
                var status = await ExecuteForOutputAsync("获取工作状态", null);
                if (status != null)
                {
                    _workMode = (int)(status.GetNullableValue<double>("WORK_MODE") ?? 1);
                    SetWorkModeChips(_workMode);

                    int err = (int)(status.GetNullableValue<double>("ERROR_CODE") ?? 0);
                    if (err != 0) Status($"仪器上报错误编号 {err}");
                }

                var settings = await ExecuteForOutputAsync("获取设置数据", null);
                bool hasSettings = settings != null && (settings.GetNullableValue<bool>("Success") ?? false);
                if (hasSettings) FillSettings(settings!);
                SetSettingsEnabled(hasSettings);

                var cal = await ExecuteForOutputAsync("获取校准数据", null);
                FillCalibration(cal);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PenMeterScreen] 附加读取失败: {ex.Message}");
            }
            finally
            {
                _auxBusy = false;
            }
        }

        private void FillSettings(DeviceParameters s)
        {
            _hasSettings = true;

            _suppress = true;
            try
            {
                SelectIndex(TempUnitCombo, s.GetNullableValue<double>("TEMP_UNIT"));
                SelectIndex(PhBufferCombo, s.GetNullableValue<double>("PH_BUFFER"));
                SelectIndex(PhResCombo, s.GetNullableValue<double>("PH_RESOLUTION"));

                // 正在输入的框不要覆盖，否则打到一半就被冲掉了
                if (!RefTempBox.IsKeyboardFocusWithin)
                    RefTempBox.Text = Fmt(s, "COND_REF_TEMP", "F0");
                if (!CellConstBox.IsKeyboardFocusWithin)
                    CellConstBox.Text = Fmt(s, "CELL_CONSTANT", "F2");
                if (!TdsBox.IsKeyboardFocusWithin)
                    TdsBox.Text = Fmt(s, "TDS_FACTOR", "F2");
            }
            finally
            {
                _suppress = false;
            }
        }

        private void SetSettingsEnabled(bool enabled)
        {
            SettingPanel.IsEnabled = enabled;
            SelfTestBtn.IsEnabled = enabled;
            SettingHint.Text = enabled
                ? "已收到仪器配置，可直接修改（每项单独下发整包 0x81）"
                : "仪器需先切到「配置模式」，收到配置后本区才可用";
            SettingHint.Foreground = enabled ? DimText : WarnBrush;
        }

        private void FillCalibration(DeviceParameters? cal)
        {
            bool inCalMode = _workMode == 4;
            bool hasCal = cal != null && (cal.GetNullableValue<bool>("Success") ?? false);

            CalModeHint.Text = inCalMode ? "校准模式" : "仪器未进入校准模式";
            CalModeHint.Foreground = inCalMode ? Accent : DimText;

            if (!hasCal)
            {
                CalDetail.Text = "—";
                ConfirmCalBtn.IsEnabled = false;
                return;
            }

            string type = cal!.GetValue<string>("CAL_TYPE_TEXT") ?? "";
            string mUnit = cal.GetValue<string>("MEASURED_UNIT") ?? "";
            string sUnit = cal.GetValue<string>("STANDARD_UNIT") ?? "";
            bool stable = cal.GetNullableValue<bool>("STABLE") ?? false;
            int errorCode = (int)(cal.GetNullableValue<double>("ERROR_CODE") ?? 0);

            CalDetail.Text =
                $"{type}：当前 {Fmt(cal, "MEASURED", "F2")} {mUnit} → 标准 {Fmt(cal, "STANDARD", "F2")} {sUnit}" +
                (stable ? " · 已稳定" : " · 等待稳定") +
                (errorCode != 0 ? $" · 错误 {errorCode}" : "");
            CalDetail.Foreground = errorCode != 0 ? FailBrush : (stable ? Accent : DimText);

            // 读数没稳就确认，等于把一个漂着的值当标准点存进去 —— 直接禁掉
            ConfirmCalBtn.IsEnabled = inCalMode && stable && errorCode == 0;
        }

        // ══════════════════════ 视觉状态 ══════════════════════

        private void SetLink(bool online)
        {
            LinkDot.Fill = online ? OkBrush : IdleText;
            LinkText.Text = online ? "数据在线" : "等待数据";
            LinkText.Foreground = online ? OkBrush : DimText;
        }

        private static void SetChip(Border chip, TextBlock text, bool on,
                                    string onLabel, string offLabel, Brush onBrush, Brush? offBrush)
        {
            if (on)
            {
                chip.BorderBrush = onBrush;
                text.Text = onLabel;
                text.Foreground = onBrush;
            }
            else
            {
                chip.BorderBrush = offBrush ?? IdleBrush;
                text.Text = offLabel;
                text.Foreground = offBrush ?? IdleText;
            }
        }

        private void SetBattery(double? percent)
        {
            if (!percent.HasValue)
            {
                BatteryText.Text = "--";
                BatteryFill.Width = 0;
                return;
            }

            double p = Math.Clamp(percent.Value, 0, 100);
            BatteryText.Text = $"{p:F0}%";
            BatteryFill.Width = 84 * p / 100.0;   // 边框内可用宽度 88 - 2*2
            // 低电量会导致笔自动关机、通信莫名中断，提前变色比事后查日志有用
            BatteryFill.Fill = p <= 15 ? FailBrush : (p <= 30 ? WarnBrush : OkBrush);
        }

        private void SetModeChips(int mode)
        {
            var chips = new[] { ModePh, ModeOrp, ModeCond, ModeTds, ModeSal, ModeRes };
            for (int i = 0; i < chips.Length; i++) ApplyChip(chips[i], i == mode);
        }

        private void SetWorkModeChips(int workMode)
        {
            ApplyChip(WorkMeasure, workMode == 1);
            ApplyChip(WorkConfig, workMode == 2);
            ApplyChip(WorkCal, workMode == 4);
        }

        private static void ApplyChip(Button chip, bool selected)
        {
            chip.Background = selected ? ChipOnBg : ChipBg;
            chip.BorderBrush = selected ? Accent : IdleBrush;
            chip.Foreground = selected ? Accent : DimText;
        }

        private void SetCalibrationLamps(int flags)
        {
            ApplyLamp(CalL1, (flags & 0x01) != 0);
            ApplyLamp(CalM, (flags & 0x02) != 0);
            ApplyLamp(CalH1, (flags & 0x04) != 0);
            ApplyLamp(CalL2, (flags & 0x08) != 0);
            ApplyLamp(CalH2, (flags & 0x10) != 0);
        }

        private static void ApplyLamp(Border lamp, bool lit)
        {
            lamp.Background = lit ? ChipOnBg : Dark;
            lamp.BorderBrush = lit ? Accent : IdleBrush;
            if (lamp.Child is TextBlock tb) tb.Foreground = lit ? Accent : IdleText;
        }

        private static void SelectIndex(ComboBox combo, double? value)
        {
            if (!value.HasValue) return;
            int i = (int)value.Value;
            if (i < 0 || i >= combo.Items.Count) return;
            if (combo.SelectedIndex != i) combo.SelectedIndex = i;
        }

        // ══════════════════════ 下发 ══════════════════════

        private void ModeChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string tag && int.TryParse(tag, out int mode))
                _ = SendAsync("切换测量模式", mode, $"已切到 {b.Content} 模式");
        }

        private void WorkMode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string tag && int.TryParse(tag, out int mode))
                _ = SendAsync("切换工作模式", mode, $"已请求切到{b.Content}模式");
        }

        private void HoldBtn_Click(object sender, RoutedEventArgs e)
        {
            _holdOn = !_holdOn;
            _ = SendAsync("数据保持", _holdOn ? 1 : 0, _holdOn ? "已请求保持读数" : "已取消保持");
        }

        private void ConfirmCalBtn_Click(object sender, RoutedEventArgs e)
            => _ = SendAsync("确认校准", 1, "已确认本次校准值");

        private async void SelfTestBtn_Click(object sender, RoutedEventArgs e)
        {
            bool ok = await ExecuteCommandAsync("屏幕自检");
            Status(ok ? "屏幕自检命令已下发" : "屏幕自检下发失败");
        }

        private void TempUnitCombo_Changed(object sender, SelectionChangedEventArgs e)
            => _ = SendSettingAsync("设置温度单位", TempUnitCombo.SelectedIndex);

        private void PhBufferCombo_Changed(object sender, SelectionChangedEventArgs e)
            => _ = SendSettingAsync("设置PH缓冲液", PhBufferCombo.SelectedIndex);

        private void PhResCombo_Changed(object sender, SelectionChangedEventArgs e)
            => _ = SendSettingAsync("设置PH分辨率", PhResCombo.SelectedIndex);

        private void RefTempBox_KeyDown(object sender, KeyEventArgs e)
            => HandleNumberEntry(e, RefTempBox, "设置电导率参比温度", 15, 30, "参比温度需在 15 ~ 30 ℃ 之间");

        private void CellConstBox_KeyDown(object sender, KeyEventArgs e)
            => HandleNumberEntry(e, CellConstBox, "设置电导率电极常数", 0.80, 1.20, "电极常数需在 0.80 ~ 1.20 之间");

        private void TdsBox_KeyDown(object sender, KeyEventArgs e)
            => HandleNumberEntry(e, TdsBox, "设置TDS系数", 0.40, 1.00, "TDS 系数需在 0.40 ~ 1.00 之间");

        private void HandleNumberEntry(KeyEventArgs e, TextBox box, string command,
                                       double min, double max, string rangeMessage)
        {
            if (e.Key != Key.Enter) return;

            if (!double.TryParse(box.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double v)
                || v < min || v > max)
            {
                // 越界的值发下去仪器会当成非法配置整包丢弃，先在这里拦下来
                Status(rangeMessage);
                System.Media.SystemSounds.Beep.Play();
                return;
            }

            _ = SendSettingAsync(command, v);
            Keyboard.ClearFocus();
        }

        private void SelectAllOnFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
                tb.Dispatcher.BeginInvoke(new Action(() => tb.SelectAll()));
        }

        private async Task SendAsync(string command, double value, string okMessage)
        {
            bool ok = await ExecuteCommandAsync(command,
                new Dictionary<string, object> { ["TargetValue"] = value });
            Status(ok ? okMessage : $"{command} 下发失败");
            if (!ok) System.Media.SystemSounds.Hand.Play();
        }

        private async Task SendSettingAsync(string command, double value)
        {
            if (_suppress) return;        // 回填触发的，不是用户改的
            if (value < 0) return;

            if (!_hasSettings)
            {
                Status("尚未收到仪器配置，请先把仪器切到「配置模式」");
                return;
            }

            bool ok = await ExecuteCommandAsync(command,
                new Dictionary<string, object> { ["TargetValue"] = value });
            Status(ok ? $"{command} 已下发" : $"{command} 下发失败");
            if (!ok) System.Media.SystemSounds.Hand.Play();
        }

        private void Status(string message)
        {
            StatusText.Text = $"{DateTime.Now:HH:mm:ss}  {message}";
        }
    }
}
