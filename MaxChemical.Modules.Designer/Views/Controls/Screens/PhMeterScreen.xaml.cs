// ============================================================================
//  PhMeterScreen.xaml.cs
//  与 PhMeterScreen.xaml 配对。触摸屏酸度计的嵌入式屏幕。
//
//  职责：
//    - 轮询「获取测量数据」，填 PH / mV / 温度 / 电导率 / 溶解氧及各自状态；
//    - 五个设置控件下发手册 2.2.4 的系统参数设置包；
//    - 记录相关的四个按钮（保存 / 查询 / 清除 / 恢复出厂）。
//
//  ── 一个必须处理的坑：下拉框回填与用户操作会互相触发 ──────────────
//  仪器每 800ms 推一包，屏幕每 500ms 回填一次下拉框。若不加抑制，
//  回填触发 SelectionChanged → 又下发一次设置包 → 仪器再推 → 再回填……
//  形成一个每半秒发一次包的死循环，而且用户手动改的值会被下一次回填冲掉。
//  所以所有回填都包在 _suppress 里，SelectionChanged 开头先看这个标志。
//
//  ── 为什么设置项不做本地乐观更新 ──────────────────────────────────
//  手册没有为设置包规定应答，驱动的 Success 只代表"字节写出去了"。
//  真正的凭据是随后推来的测量包里对应字段变了没有。所以下发之后什么都不改，
//  等回填 —— 大约一秒内就能看到。这样万一仪器拒绝了某个设置，
//  界面会如实退回原值，而不是显示一个假的成功状态。
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
    public partial class PhMeterScreen : DeviceScreenBase
    {
        protected override string PollCommandName => "获取测量数据";

        /// <summary>回填期间置位，挡住 SelectionChanged 反过来又下发一次。</summary>
        private bool _suppress;

        private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0xB8, 0x4D));
        private static readonly Brush WaitBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x74));
        private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xB3, 0x1A));
        private static readonly Brush DimText = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAA));
        private static readonly Brush OkText = new SolidColorBrush(Color.FromRgb(0x7F, 0xE3, 0x9B));

        static PhMeterScreen()
        {
            OkBrush.Freeze();
            WaitBrush.Freeze();
            WarnBrush.Freeze();
            DimText.Freeze();
            OkText.Freeze();
        }

        public PhMeterScreen()
        {
            InitializeComponent();
        }

        // ══════════════════════ 数据回填 ══════════════════════

        protected override void OnDataRefreshed(DeviceParameters o)
        {
            bool ok = o.GetNullableValue<bool>("Success") ?? false;
            SetLink(ok);
            if (!ok)
            {
                // 串口开着但还没收到第一包（刚连上、或者仪器被人从测量界面切走了）。
                // 保留上一次的数值反而误导人，统一打成 "--"。
                Blank();
                return;
            }

            // 小数位数跟着仪器给的分辨率走：低精度机型显示 6.9，高精度显示 6.860。
            // 写死 2 位在高精度机型上会把最后一位有效数字截掉。
            int decimals = (int)(o.GetNullableValue<double>("PH_RESOLUTION") ?? 2);
            if (decimals < 1 || decimals > 3) decimals = 2;

            PhValue.Text = Fmt(o, "PH", "F" + decimals);
            MvValue.Text = Fmt(o, "MV", "F1");
            PhTempValue.Text = Fmt(o, "PH_TEMP", "F1");
            PhTempUnit.Text = o.GetValue<string>("TEMP_UNIT") ?? "℃";

            bool atc = o.GetNullableValue<bool>("PH_ATC") ?? false;
            PhAtcText.Text = atc ? "ATC 自动" : "MTC 手动";

            CondValue.Text = Fmt(o, "COND", "F2");
            CondTempValue.Text = Fmt(o, "COND_TEMP", "F1");
            CondMetaValue.Text = $"{Fmt(o, "COND_REF_TEMP", "F0")}℃ / K={Fmt(o, "COND_K", "F3")}";

            DoValue.Text = Fmt(o, "DO", "F2");
            DoSatValue.Text = Fmt(o, "DO_SAT", "F1") + " %";
            DoTempValue.Text = Fmt(o, "DO_TEMP", "F1");

            SetStable(PhStableChip, PhStableText, o.GetNullableValue<bool>("PH_STABLE"));
            SetStable(CondStableChip, CondStableText, o.GetNullableValue<bool>("COND_STABLE"));
            SetStable(DoStableChip, DoStableText, o.GetNullableValue<bool>("DO_STABLE"));

            // ── 设置项回填 ──────────────────────────────────────
            _suppress = true;
            try
            {
                SelectIndex(H2oCombo, o.GetNullableValue<double>("PH_H2O_TYPE"));
                // 回填温度源用 PH_TEMP_SRC（设定值）而不是 PH_ATC（当前实际状态）：
                // 两者正常一致，但设了 ATC 却没插温度探头时仪器会实际走 MTC，
                // 那时用 PH_ATC 回填会把用户的设定显示反。
                SelectIndex(TempSrcCombo, o.GetNullableValue<double>("PH_TEMP_SRC"));
                SelectIndex(StdTypeCombo, o.GetNullableValue<double>("PH_STD_TYPE"));
                // 分辨率编码 1/2/3 对应下拉的 0/1/2 项
                var res = o.GetNullableValue<double>("PH_RESOLUTION");
                if (res.HasValue) SelectIndexAt(ResolutionCombo, (int)res.Value - 1);

                // 正在输入时不要把用户敲的字冲掉
                if (!MtcTempBox.IsKeyboardFocusWithin)
                    MtcTempBox.Text = Fmt(o, "PH_MTC_TEMP", "F1");
            }
            finally
            {
                _suppress = false;
            }
        }

        protected override void OnPollError(Exception ex)
        {
            SetLink(false);
            Blank();
        }

        private void Blank()
        {
            PhValue.Text = "--";
            MvValue.Text = "--";
            PhTempValue.Text = "--";
            PhAtcText.Text = "--";
            CondValue.Text = "--";
            CondTempValue.Text = "--";
            CondMetaValue.Text = "--";
            DoValue.Text = "--";
            DoSatValue.Text = "--";
            DoTempValue.Text = "--";

            SetStable(PhStableChip, PhStableText, null);
            SetStable(CondStableChip, CondStableText, null);
            SetStable(DoStableChip, DoStableText, null);
        }

        private void SetLink(bool online)
        {
            LinkDot.Fill = online ? OkBrush : WaitBrush;
            LinkText.Text = online ? "数据在线" : "等待数据";
            LinkText.Foreground = online ? OkText : DimText;
        }

        private static void SetStable(Border chip, TextBlock text, bool? stable)
        {
            if (!stable.HasValue)
            {
                chip.BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x55));
                text.Text = "—";
                text.Foreground = DimText;
                return;
            }

            if (stable.Value)
            {
                chip.BorderBrush = OkBrush;
                text.Text = "读数稳定";
                text.Foreground = OkText;
            }
            else
            {
                chip.BorderBrush = WarnBrush;
                text.Text = "漂移中";
                text.Foreground = WarnBrush;
            }
        }

        private static string Fmt(DeviceParameters o, string key, string fmt)
        {
            var v = o.GetNullableValue<double>(key);
            return v.HasValue ? v.Value.ToString(fmt, CultureInfo.InvariantCulture) : "--";
        }

        // 两个名字不同的重载而不是同名重载：参数是 double? 和 int? 时，
        // 传 int 字面量的调用点走哪一个要靠可空数值转换的"较优"规则去推，
        // 读代码的人（和以后改代码的人）容易看错。分开命名，一眼就知道走哪条。
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

        private void H2oCombo_Changed(object sender, SelectionChangedEventArgs e)
            => _ = SendSetting("设置水类型", H2oCombo.SelectedIndex);

        private void TempSrcCombo_Changed(object sender, SelectionChangedEventArgs e)
            => _ = SendSetting("设置PH温度源", TempSrcCombo.SelectedIndex);

        private void StdTypeCombo_Changed(object sender, SelectionChangedEventArgs e)
            => _ = SendSetting("设置PH标液系列", StdTypeCombo.SelectedIndex);

        private void ResolutionCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            // 下拉的 0/1/2 项对应协议里的分辨率编码 1/2/3。
            // 先挡住 SelectedIndex == -1：那时 +1 会得到 0，而 0 不是合法编码。
            int i = ResolutionCombo.SelectedIndex;
            if (i < 0) return;
            _ = SendSetting("设置PH分辨率", i + 1);
        }

        private async System.Threading.Tasks.Task SendSetting(string command, int value)
        {
            if (_suppress) return;            // 回填触发的，不是用户点的
            if (value < 0) return;

            bool ok = await ExecuteCommandAsync(command,
                new Dictionary<string, object> { ["TargetValue"] = value });

            Status(ok ? $"{command} 已下发，等待仪器回读" : $"{command} 下发失败");
            if (!ok) System.Media.SystemSounds.Hand.Play();
        }

        private async void MtcTempBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            if (!double.TryParse(MtcTempBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out double v)
                || v < 0 || v > 99.9)
            {
                // 手册明确写了范围 0.0-99.9℃，越界的值发下去仪器也不会认
                Status("手动补偿温度需在 0.0 ~ 99.9 ℃ 之间");
                System.Media.SystemSounds.Beep.Play();
                return;
            }

            bool ok = await ExecuteCommandAsync("设置PH手动温度",
                new Dictionary<string, object> { ["TargetValue"] = v });

            Status(ok ? $"手动补偿温度 {v:F1}℃ 已下发" : "手动补偿温度下发失败");
            Keyboard.ClearFocus();
        }

        private void SelectAllOnFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
                tb.Dispatcher.BeginInvoke(new Action(() => tb.SelectAll()));
        }

        // ══════════════════════ 记录操作 ══════════════════════

        private async void SaveBtn_Click(object sender, RoutedEventArgs e)
        {
            bool ok = await ExecuteCommandAsync("保存当前PH数据");
            Status(ok ? "已请求仪器保存当前 PH 数据" : "保存请求下发失败");
        }

        private async void QueryBtn_Click(object sender, RoutedEventArgs e)
        {
            // 记录条数多时仪器要推好一会儿，禁用按钮防止连点导致两批记录混在一起
            QueryBtn.IsEnabled = false;
            Status("正在向仪器索取记录…");
            try
            {
                var output = await ExecuteForOutputAsync("查询PH记录", null);
                if (output is null)
                {
                    Status("查询记录失败");
                    return;
                }

                int count = (int)(output.GetNullableValue<double>("Count") ?? 0);
                bool complete = output.GetNullableValue<bool>("Success") ?? false;

                Status(complete
                    ? $"共取到 {count} 条记录（详见流程输出 / 日志）"
                    : $"记录上传未收到结束包，已取到 {count} 条，可能不完整");
            }
            finally
            {
                QueryBtn.IsEnabled = true;
            }
        }

        private async void ClearBtn_Click(object sender, RoutedEventArgs e)
        {
            // 清记录不可撤销，这里必须问一次
            if (MessageBox.Show("将清除仪器内保存的全部 PH 记录，且无法恢复。确定继续吗？",
                    "清除 PH 记录", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                != MessageBoxResult.OK) return;

            bool ok = await ExecuteCommandAsync("清除PH记录");
            Status(ok ? "清除记录命令已下发" : "清除记录下发失败");
        }

        private async void ResetBtn_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("将把仪器的 PH 参数恢复为出厂设置，校准数据也会丢失。确定继续吗？",
                    "PH 恢复出厂", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                != MessageBoxResult.OK) return;

            bool ok = await ExecuteCommandAsync("PH恢复出厂");
            Status(ok ? "恢复出厂命令已下发" : "恢复出厂下发失败");
        }

        private void Status(string message)
        {
            StatusText.Text = $"{DateTime.Now:HH:mm:ss}  {message}";
        }
    }
}
