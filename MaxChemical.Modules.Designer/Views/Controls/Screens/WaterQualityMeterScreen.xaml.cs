// ============================================================================
//  WaterQualityMeterScreen.xaml.cs
//  与 WaterQualityMeterScreen.xaml 配对。便携式三参数水质测定仪的嵌入式屏幕。
//
//  职责:
//    - 轮询「获取测量数据」(每次是一趟真实的串口往返),填当前模式的读数;
//    - 九个模式按钮按联机应答里的使能位图启用/禁用,点一下切模式;
//    - 设置整包:打开屏幕后读一次,之后每次下发后回读一次,不跟轮询走;
//    - 记录读取、设备锁、恢复出厂三个维护动作。
//
//  ── 回填与用户操作互相触发的坑 ────────────────────────────────────
//  回填下拉框会触发 SelectionChanged → 又下发一次 → 回读 → 再回填……
//  所有回填都包在 _suppress 里,SelectionChanged 开头先看这个标志。
//  设置这块不轮询,所以不会像酸度计那样每半秒来一次,但下发后的回读一样会触发。
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
    public partial class WaterQualityMeterScreen : DeviceScreenBase
    {
        protected override string PollCommandName => "获取测量数据";

        private bool _suppress;
        private bool _settingsLoaded;
        private bool _loadingSettings;
        private bool _locked;

        private static readonly string[] ModeLabels =
        {
            "pH", "mV", "ORP", "电导率", "电阻率", "TDS", "盐度", "溶解氧", "溶氧饱和度",
        };
        private readonly Button[] _modeButtons = new Button[ModeLabels.Length];
        private int _currentMode = -1;

        /// <summary>输入框的合法范围(与驱动命令的范围一致),越界不下发。</summary>
        private static readonly Dictionary<string, (double min, double max, string fmt)> BoxRanges = new()
        {
            ["设置背光亮度"] = (3, 100, "F0"),
            ["设置自动关机时间"] = (0, 255, "F0"),
            ["设置手动温补温度"] = (-10, 150, "F1"),
            ["设置电导率参考温度"] = (0, 100, "F1"),
            ["设置电导率温度系数"] = (0, 10, "F3"),
            ["设置TDS系数"] = (0, 10, "F3"),
            ["设置电极常数"] = (0, 100, "F3"),
            ["设置溶解氧盐度补偿"] = (0, 100, "F1"),
            ["设置溶解氧大气压补偿"] = (0, 2000, "F1"),
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

        static WaterQualityMeterScreen()
        {
            foreach (var b in new[] { OkBrush, WaitBrush, WarnBrush, DimText, OkText, ChipBorder, BtnBg, BtnBorder, ActiveBg, ActiveBorder })
                b.Freeze();
        }

        public WaterQualityMeterScreen()
        {
            InitializeComponent();

            // 每次轮询都是一趟串口往返,文档建议 ≥ 200ms;500ms 足够看到读数在动,又不把线占死
            PollIntervalMs = 500;

            BuildModeButtons();
        }

        private void BuildModeButtons()
        {
            var style = (Style)FindResource("ModeBtn");
            for (int i = 0; i < ModeLabels.Length; i++)
            {
                var btn = new Button
                {
                    Style = style,
                    Content = ModeLabels[i],
                    Tag = i,
                    IsEnabled = false,   // 联机应答回来之前一律灰
                };
                btn.Click += ModeBtn_Click;
                _modeButtons[i] = btn;
                ModePanel.Children.Add(btn);
            }
        }

        // ══════════════════════ 数据回填 ══════════════════════

        protected override void OnDataRefreshed(DeviceParameters o)
        {
            bool ok = o.GetNullableValue<bool>("Success") ?? false;
            SetLink(ok);
            if (!ok)
            {
                Blank();
                return;
            }

            int decimals = (int)(o.GetNullableValue<double>("DECIMALS") ?? 2);
            if (decimals < 0 || decimals > 6) decimals = 2;

            MainValue.Text = Fmt(o, "VALUE", "F" + decimals);
            MainUnit.Text = o.GetValue<string>("UNIT") ?? "";
            ModeChipText.Text = o.GetValue<string>("MODE_NAME") ?? "--";

            TempValue.Text = Fmt(o, "TEMP", "F1");
            TempUnit.Text = o.GetValue<string>("TEMP_UNIT") ?? "℃";
            AtcText.Text = (o.GetNullableValue<bool>("ATC") ?? false) ? "ATC 自动" : "MTC 手动";
            LevelText.Text = $"{Fmt(o, "POLE_LEVEL", "F0")} / {Fmt(o, "CAL_LEVEL", "F0")}";

            SetStable(o.GetNullableValue<bool>("STABLE"));

            DeviceIdText.Text = Fmt(o, "DEVICE_ID", "F0");
            ModesText.Text = o.GetValue<string>("MODES_ENABLED") is { Length: > 0 } m ? m : "--";
            DecimalsText.Text = decimals.ToString(CultureInfo.InvariantCulture);

            int modesSta = (int)(o.GetNullableValue<double>("MODES_STA") ?? 0);
            int mode = (int)(o.GetNullableValue<double>("MODE_CODE") ?? -1);
            UpdateModeButtons(modesSta, mode);

            // 设置整包只读一次;拿到第一包实时数据说明链路通了,这时再去读
            if (!_settingsLoaded) _ = LoadSettingsAsync();
        }

        protected override void OnPollError(Exception ex)
        {
            SetLink(false);
            Blank();
        }

        private void Blank()
        {
            MainValue.Text = "--";
            MainUnit.Text = "";
            ModeChipText.Text = "--";
            TempValue.Text = "--";
            AtcText.Text = "--";
            LevelText.Text = "--";
            SetStable(null);
            foreach (var b in _modeButtons) b.IsEnabled = false;
        }

        private void SetLink(bool online)
        {
            LinkDot.Fill = online ? OkBrush : WaitBrush;
            LinkText.Text = online ? "联机正常" : "等待应答";
            LinkText.Foreground = online ? OkText : DimText;
        }

        private void SetStable(bool? stable)
        {
            if (!stable.HasValue)
            {
                StableChip.BorderBrush = ChipBorder;
                StableText.Text = "—";
                StableText.Foreground = DimText;
                return;
            }

            if (stable.Value)
            {
                StableChip.BorderBrush = OkBrush;
                StableText.Text = "读数稳定";
                StableText.Foreground = OkText;
            }
            else
            {
                StableChip.BorderBrush = WarnBrush;
                StableText.Text = "漂移中";
                StableText.Foreground = WarnBrush;
            }
        }

        private void UpdateModeButtons(int modesSta, int current)
        {
            _currentMode = current;
            for (int i = 0; i < _modeButtons.Length; i++)
            {
                var b = _modeButtons[i];
                // 位图全 0 的情况(模拟模式没给、或固件没实现)不把按钮全灰掉,否则没法用
                b.IsEnabled = modesSta == 0 || (modesSta & (1 << i)) != 0;
                bool active = i == current;
                b.Background = active ? ActiveBg : BtnBg;
                b.BorderBrush = active ? ActiveBorder : BtnBorder;
            }
        }

        private static string Fmt(DeviceParameters o, string key, string fmt)
        {
            var v = o.GetNullableValue<double>(key);
            return v.HasValue ? v.Value.ToString(fmt, CultureInfo.InvariantCulture) : "--";
        }

        // ══════════════════════ 设置整包 ══════════════════════

        private async System.Threading.Tasks.Task LoadSettingsAsync()
        {
            if (_loadingSettings) return;
            _loadingSettings = true;
            try
            {
                var s = await ExecuteForOutputAsync("读取设置", null);
                if (s is null || !(s.GetNullableValue<bool>("Success") ?? false))
                {
                    Status("读取设置失败");
                    return;
                }

                _suppress = true;
                try
                {
                    SelectIndex(TempUnitCombo, s.GetNullableValue<double>("TEMP_UNIT_CODE"));
                    SelectIndexAt(BeepCombo, (s.GetNullableValue<bool>("BEEP") ?? false) ? 1 : 0);
                    SelectIndex(WaterCombo, s.GetNullableValue<double>("PH_WATER_TYPE"));
                    SelectIndex(PhStdCombo, s.GetNullableValue<double>("PH_STD_TYPE"));
                    // 小数位 2/3 对应下拉的 0/1 项
                    var res = s.GetNullableValue<double>("PH_DISP_RES");
                    if (res.HasValue) SelectIndexAt(PhResCombo, (int)res.Value - 2);
                    SelectIndex(CondStdCombo, s.GetNullableValue<double>("COND_STD_TYPE"));

                    SetBox(BgLightBox, s, "BG_LIGHT", "F0");
                    SetBox(ShutdownBox, s, "SHUTDOWN_MIN", "F0");
                    SetBox(ManualTempBox, s, "MANUAL_TEMP", "F1");
                    SetBox(RefTempBox, s, "COND_REF_TEMP", "F1");
                    SetBox(TempCoeBox, s, "COND_TEMP_COE", "F3");
                    SetBox(TdsCoeBox, s, "COND_TDS_COE", "F3");
                    SetBox(CondKBox, s, "COND_K", "F3");
                    SetBox(DoSalBox, s, "DO_SAL", "F1");
                    SetBox(DoPressureBox, s, "DO_PRESSURE", "F1");

                    _locked = s.GetNullableValue<bool>("DEVICE_LOCK") ?? false;
                    LockBtn.Content = _locked ? "解锁仪器按键" : "锁定仪器按键";
                }
                finally
                {
                    _suppress = false;
                }

                _settingsLoaded = true;
            }
            finally
            {
                _loadingSettings = false;
            }
        }

        /// <summary>正在输入时不要把用户敲的字冲掉。</summary>
        private static void SetBox(TextBox box, DeviceParameters s, string key, string fmt)
        {
            if (!box.IsKeyboardFocusWithin) box.Text = Fmt(s, key, fmt);
        }

        private static void SelectIndex(ComboBox combo, double? value)
        {
            if (value.HasValue) SelectIndexAt(combo, (int)value.Value);
        }

        private static void SelectIndexAt(ComboBox combo, int index)
        {
            if (index < 0 || index >= combo.Items.Count) return;
            if (combo.SelectedIndex != index) combo.SelectedIndex = index;
        }

        // ══════════════════════ 设置下发 ══════════════════════

        private void Combo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_suppress) return;
            if (sender is not ComboBox combo || combo.Tag is not string command) return;

            int i = combo.SelectedIndex;
            if (i < 0) return;

            // 小数位下拉的 0/1 项对应协议里的 2/3
            double value = ReferenceEquals(combo, PhResCombo) ? i + 2 : i;
            _ = SendSetting(command, value);
        }

        private async void Box_KeyDown(object sender, KeyEventArgs e)
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
            await SendSetting(command, v);
        }

        private async System.Threading.Tasks.Task SendSetting(string command, double value)
        {
            if (_suppress) return;

            bool ok = await ExecuteCommandAsync(command,
                new Dictionary<string, object> { ["TargetValue"] = value });

            Status(ok ? $"{command} 已写入仪器" : $"{command} 写入失败");
            if (!ok) System.Media.SystemSounds.Hand.Play();

            // 不管成没成功都回读一遍:成功了让界面与仪器一致,失败了把界面退回仪器的真实值
            await LoadSettingsAsync();
        }

        private void SelectAllOnFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
                tb.Dispatcher.BeginInvoke(new Action(() => tb.SelectAll()));
        }

        private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            Status("正在读取设置…");
            await LoadSettingsAsync();
            if (_settingsLoaded) Status("设置已重新读取");
        }

        // ══════════════════════ 模式切换 ══════════════════════

        private async void ModeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not int mode) return;
            if (mode == _currentMode) return;

            var input = new DeviceParameters();
            input.Variables.Add(new NumberParameter("TargetValue", 0, 8, mode, "目标模式码", false));
            var output = await ExecuteForOutputAsync("切换测量模式", input);

            string msg = output?.GetValue<string>("Message") ?? "";
            bool ok = output?.GetNullableValue<bool>("Success") ?? false;
            Status(ok ? msg : (msg.Length > 0 ? msg : "切换模式失败"));
            if (!ok) System.Media.SystemSounds.Hand.Play();
        }

        // ══════════════════════ 记录与维护 ══════════════════════

        private async void RecordsBtn_Click(object sender, RoutedEventArgs e)
        {
            // 最多 3000 条逐条读,要一会儿;禁用按钮防连点
            RecordsBtn.IsEnabled = false;
            Status("正在逐条读取仪器记录…");
            try
            {
                var output = await ExecuteForOutputAsync("读取存储记录", null);
                if (output is null)
                {
                    Status("读取记录失败");
                    return;
                }

                int count = (int)(output.GetNullableValue<double>("Count") ?? 0);
                bool complete = output.GetNullableValue<bool>("Success") ?? false;
                string msg = output.GetValue<string>("Message") ?? "";

                Status(complete
                    ? $"共读到 {count} 条记录（详见流程输出 / 日志）"
                    : $"记录读取中断，已读 {count} 条：{msg}");
            }
            finally
            {
                RecordsBtn.IsEnabled = true;
            }
        }

        private async void LockBtn_Click(object sender, RoutedEventArgs e)
        {
            if (!_locked)
            {
                // 锁上之后仪器面板按键全部失效,只能从这里解 —— 必须问一次
                if (MessageBox.Show("锁定后仪器面板上的按键全部失效，只能由本软件解锁。确定锁定吗？",
                        "锁定仪器按键", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                    != MessageBoxResult.OK) return;
            }

            await SendSetting("设置设备锁", _locked ? 0 : 1);
        }

        private async void ResetBtn_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("将把仪器全部设置恢复为出厂默认值，且无法撤销。确定继续吗？",
                    "恢复出厂设置", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                != MessageBoxResult.OK) return;

            bool ok = await ExecuteCommandAsync("恢复出厂设置");
            Status(ok ? "已恢复出厂设置" : "恢复出厂失败");
            await LoadSettingsAsync();
        }

        private void Status(string message)
        {
            StatusText.Text = $"{DateTime.Now:HH:mm:ss}  {message}";
        }
    }
}
