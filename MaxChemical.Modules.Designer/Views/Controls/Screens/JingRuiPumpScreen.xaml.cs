using DevicePlugins.Devices;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MaxChemical.Modules.Designer.Views.Controls.Screens
{
    /// <summary>
    /// JingRuiPumpScreen.xaml 的交互逻辑。
    /// 精睿系列柱塞泵(杭州精进科技)控制屏,走手册第 7.4 节的 RS485 标准 Modbus 协议。
    ///
    /// 界面照泵机身上那块触摸屏做:标题条 → 三个页签(流量/压力/状态) → 右侧常驻动作键 → 状态栏。
    ///
    /// 读:轮询「获取所有数据」。
    /// 写:每个操作各自对应驱动里的一条命令,屏幕不拼报文、不直接碰串口。
    ///
    /// 右侧「运行」对应驱动的「按流量启动」—— 写流量 → 回读比对 → 启动,
    /// 三步里任何一步没过就不启动。手册明确建议这么做:泵在 485 写入时只改 RAM 不改 Flash,
    /// 若不先写流量再比对,泵可能按屏幕存在 Flash 里的旧流量跑起来。
    /// </summary>
    public partial class JingRuiPumpScreen : DeviceScreenBase
    {
        /// <summary>量程。设备参数里配了「最大流量」「最大压力」就按实际值走。</summary>
        private double _maxFlow = 100.0;
        private double _maxPressure = 10.0;

        // ── 屏幕持有的当前设定与状态 ──
        private double _flowSv;
        private double _pressureMax = 4.0;
        private double _pressureMin = 0.0;
        private bool _running;
        private bool _dosing;
        private int _faultCode;

        // 压力量程条几何(与 XAML 里那个 Canvas 对齐:内缩 1px 边框)
        private const double BarInset = 1.0;

        private readonly Brush _ledOff = new SolidColorBrush(Color.FromRgb(0xC8, 0xD4, 0xDE));
        private readonly Brush _ledAlarm = new SolidColorBrush(Color.FromRgb(0xD2, 0x60, 0x4F));
        private readonly Brush _ledActive = new SolidColorBrush(Color.FromRgb(0x3F, 0xAE, 0x79));
        private readonly Brush _ledInfo = new SolidColorBrush(Color.FromRgb(0x3A, 0x7E, 0xC4));
        private readonly Brush _powerIdle = new SolidColorBrush(Color.FromRgb(0x7F, 0xA9, 0xCF));
        private readonly Brush _inkStrong = new SolidColorBrush(Color.FromRgb(0x16, 0x32, 0x4B));
        private readonly Brush _hintWarn = new SolidColorBrush(Color.FromRgb(0xA0, 0x52, 0x2D));
        private readonly Brush _flowInk = new SolidColorBrush(Color.FromRgb(0x0E, 0x6B, 0x4F));
        private readonly Brush _pressureInk = new SolidColorBrush(Color.FromRgb(0xB5, 0x70, 0x1A));

        public JingRuiPumpScreen()
        {
            InitializeComponent();
            PollIntervalMs = 1000;   // 一轮 2~3 帧 Modbus,1s 足够,也不至于占满 485 总线
        }

        protected override string PollCommandName => "获取所有数据";

        protected override void OnDeviceAttached()
        {
            TbTitle.Text = Device?.Name ?? "精睿进料泵";
            try
            {
                var mode = Device?.Parameters?.GetValue<string>("通信方式");
                var station = Device?.Parameters?.GetValue<double>("Modbus站号") ?? 9;
                TbMeta.Text = $"RS485 · Modbus RTU · 站号 {station:F0}";
                TbComm.Text = $"通讯:{mode ?? "--"}";

                var maxFlow = Device?.Parameters?.GetValue<double>("最大流量") ?? 0;
                if (maxFlow > 0) _maxFlow = maxFlow;

                var maxPressure = Device?.Parameters?.GetValue<double>("最大压力") ?? 0;
                if (maxPressure > 0) _maxPressure = maxPressure;
            }
            catch { /* 参数缺失不影响屏幕打开 */ }

            UpdateLimitHint();
        }

        #region 轮询数据 → 屏幕

        protected override void OnDataRefreshed(DeviceParameters output)
        {
            // ── 流量 ──
            var flow = output.GetNullableValue<double>("Flow");
            var flowSv = output.GetNullableValue<double>("FlowSV");

            TbFlowBig.Text = flow.HasValue ? flow.Value.ToString("F2") : "--";
            TbFlowBig.Foreground = _flowInk;

            if (flowSv.HasValue)
            {
                _flowSv = flowSv.Value;
                TbFlowSvHint.Text = $"设定 {_flowSv:F2} mL/min";
                TbFlowSvSide.Text = _flowSv.ToString("F2");
                // 正在输入时不要抢用户的光标
                if (!TbSetFlow.IsKeyboardFocusWithin)
                    TbSetFlow.Text = _flowSv.ToString("F2");
            }

            // ── 压力 ──
            var pressure = output.GetNullableValue<double>("Pressure");
            TbPressureBig.Text = pressure.HasValue ? pressure.Value.ToString("F3") : "--";

            // 上下限是只写寄存器,读不回来 —— 驱动回显的是它记住的上次下发值
            var pMax = output.GetNullableValue<double>("PressureMax");
            var pMin = output.GetNullableValue<double>("PressureMin");
            if (pMax.HasValue) _pressureMax = pMax.Value;
            if (pMin.HasValue) _pressureMin = pMin.Value;
            if (!TbPressureMax.IsKeyboardFocusWithin) TbPressureMax.Text = _pressureMax.ToString("F1");
            if (!TbPressureMin.IsKeyboardFocusWithin) TbPressureMin.Text = _pressureMin.ToString("F1");
            UpdateLimitHint();
            UpdatePressureBar(pressure ?? 0);

            // 触到上限就把示值本身变红 —— 量程条在小尺寸下不够醒目
            bool overLimit = _pressureMax > 0 && (pressure ?? 0) >= _pressureMax;
            TbPressureBig.Foreground = overLimit ? _ledAlarm : _pressureInk;

            // ── 运行状态 ──
            var running = output.GetNullableValue<bool>("IsRunning");
            var dosing = output.GetNullableValue<bool>("IsDosing");
            if (running.HasValue) _running = running.Value;
            if (dosing.HasValue) _dosing = dosing.Value;

            string stateText = _running ? (_dosing ? "定量运行" : "运行中") : "停止";
            TbRunState.Text = stateText;
            TbStateSummary.Text = stateText;
            LedPower.Fill = _running ? _ledActive : _powerIdle;

            // ── 故障 ──
            var faultCode = output.GetNullableValue<double>("FaultCode");
            if (faultCode.HasValue) _faultCode = (int)Math.Round(faultCode.Value);

            TbFaultTop.Text = DescribeFault(_faultCode);
            TbFaultTop.Foreground = _faultCode == 0 ? _inkStrong : _ledAlarm;

            SetLed(LedRunning, _running, _ledActive);
            SetLed(LedDosing, _dosing, _ledInfo);
            SetLed(LedHighPressure, output.GetNullableValue<bool>("FaultHighPressure") ?? false, _ledAlarm);
            SetLed(LedLowPressure, output.GetNullableValue<bool>("FaultLowPressure") ?? false, _ledAlarm);
            SetLed(LedCircuit, output.GetNullableValue<bool>("FaultCircuit") ?? false, _ledAlarm);
            SetLed(LedPumpBody, output.GetNullableValue<bool>("FaultPumpBody") ?? false, _ledAlarm);
            SetLed(LedDriver, output.GetNullableValue<bool>("FaultDriver") ?? false, _ledAlarm);
        }

        protected override void OnPollError(Exception ex)
        {
            TbFlowBig.Text = TbPressureBig.Text = "--";
            TbRunState.Text = TbStateSummary.Text = "--";
            LedPower.Fill = _powerIdle;
            ShowHint("通讯异常,数据已停止刷新", warn: true);
        }

        /// <summary>压力量程条:填充宽度按实测/满量程,红刻线按上限位置。</summary>
        private void UpdatePressureBar(double pressure)
        {
            try
            {
                double track = PressureLimitTick?.Parent is FrameworkElement canvas && canvas.ActualWidth > 0
                    ? canvas.ActualWidth - BarInset * 2
                    : 0;
                if (track <= 0) return;

                double range = _maxPressure > 0 ? _maxPressure : 10.0;
                PressureBar.Width = track * Clamp01(pressure / range);

                if (_pressureMax > 0 && _pressureMax < range)
                {
                    System.Windows.Controls.Canvas.SetLeft(
                        PressureLimitTick, BarInset + track * (_pressureMax / range));
                    PressureLimitTick.Visibility = Visibility.Visible;
                }
                else
                {
                    // 上限没设或超出量程时不画刻线,免得永远贴在最右端让人误以为已经到顶
                    PressureLimitTick.Visibility = Visibility.Collapsed;
                }
            }
            catch { /* 布局未就绪时跳过,下一轮再画 */ }
        }

        #endregion

        #region 操作 → 驱动命令

        /// <summary>流量框回车 = 点「下发」:只下发流量(带回读比对),不启动。</summary>
        private async void TbSetFlow_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (!TryReadFlowBox(out double value)) return;

            await SendFlowAsync("设置流量", value);
            Keyboard.ClearFocus();
        }

        private async void BtnSetFlow_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadFlowBox(out double value)) return;
            await SendFlowAsync("设置流量", value);
        }

        /// <summary>运行:走「按流量启动」—— 写流量 → 回读比对 → 启动,一步不过就不启动。</summary>
        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadFlowBox(out double value)) return;

            if (value <= 0)
            {
                ShowHint("流量为 0,泵不会出液;请先设一个大于 0 的流量", warn: true);
                return;
            }

            await SendFlowAsync("按流量启动", value);
        }

        /// <summary>下发流量类命令的公共部分:两条命令的输入输出结构一样,只是要不要启动的差别。</summary>
        private async Task SendFlowAsync(string commandName, double targetFlow)
        {
            var input = new DeviceParameters()
            {
                Variables = new ObservableCollection<ParameterBase>()
                {
                    new NumberParameter("TargetFlow", 0, 299.99, targetFlow, "流量设定值", true, "mL/min")
                }
            };

            var output = await ExecuteForOutputAsync(commandName, input);
            if (output == null)
            {
                ShowHint($"{commandName} 下发失败(设备无应答)", warn: true);
                return;
            }

            bool ok = output.GetNullableValue<bool>("Success") ?? false;
            bool verified = output.GetNullableValue<bool>("Verified") ?? false;
            double readBack = output.GetNullableValue<double>("ReadBackFlow") ?? 0;

            if (ok)
            {
                _flowSv = targetFlow;
                ShowHint(commandName == "按流量启动"
                    ? $"已按 {targetFlow:F2} mL/min 启动(回读 {readBack:F2},比对通过)"
                    : $"流量已下发:{targetFlow:F2} mL/min(回读 {readBack:F2},比对通过)");
            }
            else if (!verified)
            {
                // 这是最需要说清楚的一种失败:写进去了,但泵里存的不是这个值
                ShowHint($"流量回读比对不通过:设定 {targetFlow:F2},回读 {readBack:F2} mL/min。" +
                         (commandName == "按流量启动" ? "已放弃启动。" : "") +
                         "请检查泵是否连着屏幕(部分版本要重启后才更新)",
                         warn: true);
            }
            else
            {
                ShowHint($"{commandName} 失败", warn: true);
            }
        }

        private async void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            bool ok = await ExecuteCommandAsync("停止泵");
            ShowHint(ok ? "停止指令已下发" : "停止指令下发失败", warn: !ok);
        }

        private async void BtnPressureLimit_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(TbPressureMax.Text, out double pMax) || pMax < 0 || pMax > _maxPressure)
            {
                ShowHint($"压力上限需在 0 ~ {_maxPressure:F1} MPa 之间", warn: true);
                TbPressureMax.Text = _pressureMax.ToString("F1");
                return;
            }
            if (!double.TryParse(TbPressureMin.Text, out double pMin) || pMin < 0 || pMin > _maxPressure)
            {
                ShowHint($"压力下限需在 0 ~ {_maxPressure:F1} MPa 之间", warn: true);
                TbPressureMin.Text = _pressureMin.ToString("F1");
                return;
            }
            if (pMin > pMax)
            {
                ShowHint("压力下限不能高于上限", warn: true);
                return;
            }

            bool ok = await ExecuteCommandAsync("设置压力上下限", new Dictionary<string, object>
            {
                ["PressureMax"] = pMax,
                ["PressureMin"] = pMin
            });

            if (ok)
            {
                _pressureMax = pMax;
                _pressureMin = pMin;
                UpdateLimitHint();
                ShowHint($"压力限值已下发:上限 {pMax:F1} / 下限 {pMin:F1} MPa");
            }
            else
            {
                ShowHint("压力限值下发失败", warn: true);
            }
        }

        private async void BtnDose_Click(object sender, RoutedEventArgs e)
        {
            if (!double.TryParse(TbDoseVolume.Text, out double volume) || volume <= 0 || volume > 2999.9)
            {
                ShowHint("定量体积需在 0.1 ~ 2999.9 mL 之间", warn: true);
                return;
            }

            // 定量也带上流量框里的值:泵按这个流量把体积送完,和「按流量启动」同一个道理
            double flow = double.TryParse(TbSetFlow.Text, out var f) && f > 0 ? f : 0;

            bool ok = await ExecuteCommandAsync("定量输送", new Dictionary<string, object>
            {
                ["Volume"] = volume,
                ["TargetFlow"] = flow
            });

            if (ok)
            {
                ShowHint(flow > 0
                    ? $"定量输送已启动:{volume:F1} mL @ {flow:F2} mL/min"
                    : $"定量输送已启动:{volume:F1} mL(沿用泵内当前流量)");
            }
            else
            {
                ShowHint("定量输送启动失败(流量比对不通过时不会启动)", warn: true);
            }
        }

        private async void BtnZero_Click(object sender, RoutedEventArgs e)
        {
            // 带压校零会把当前残压当成零点,之后所有压力读数都偏低,所以运行中直接拦下
            if (_running)
            {
                ShowHint("请先停泵并泄压后再做压力校零,带压校零会把残压当成零点", warn: true);
                return;
            }

            bool ok = await ExecuteCommandAsync("压力校零");
            ShowHint(ok ? "压力校零指令已下发" : "压力校零下发失败", warn: !ok);
        }

        private async void BtnClearFault_Click(object sender, RoutedEventArgs e)
        {
            bool ok = await ExecuteCommandAsync("故障消除");
            ShowHint(ok ? "故障消除指令已下发;若报警重现,请先排查成因" : "故障消除下发失败", warn: !ok);
        }

        private async void BtnReadSerial_Click(object sender, RoutedEventArgs e)
        {
            var output = await ExecuteForOutputAsync("读序列号后四位", null);
            var tail = output?.GetNullableValue<double>("SerialTail");

            if (tail.HasValue)
            {
                TbSerial.Text = ((int)Math.Round(tail.Value)).ToString("D4");
                ShowHint($"序列号后四位:{TbSerial.Text}(改 485 地址时要用)");
            }
            else
            {
                TbSerial.Text = "----";
                ShowHint("读序列号失败", warn: true);
            }
        }

        /// <summary>读流量输入框并校验范围,非法时回填上次值并给提示。</summary>
        private bool TryReadFlowBox(out double value)
        {
            if (!double.TryParse(TbSetFlow.Text, out value) || value < 0 || value > _maxFlow)
            {
                ShowHint($"流量设定需在 0 ~ {_maxFlow:F2} mL/min 之间", warn: true);
                TbSetFlow.Text = _flowSv.ToString("F2");
                value = 0;
                return false;
            }
            return true;
        }

        #endregion

        #region UI 小工具

        /// <summary>故障码 → 中文描述。与驱动里的 DescribeFault 保持一致。</summary>
        private static string DescribeFault(int code) => code switch
        {
            0 => "正常",
            1 => "电路故障",
            5 => "压力超上限",
            6 => "压力超下限",
            7 => "泵体故障",
            44 => "驱动器故障",
            _ => $"未知({code})"
        };

        private void UpdateLimitHint()
        {
            TbPressureLimitHint.Text = $"压力限值\n{_pressureMax:F1} / {_pressureMin:F1} MPa";
        }

        private void SetLed(Ellipse led, bool on, Brush onBrush)
        {
            led.Fill = on ? onBrush : _ledOff;
        }

        private void ShowHint(string text, bool warn = false)
        {
            TbCmdHint.Text = text;
            TbCmdHint.Foreground = warn ? _hintWarn : _inkStrong;
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        #endregion
    }
}
