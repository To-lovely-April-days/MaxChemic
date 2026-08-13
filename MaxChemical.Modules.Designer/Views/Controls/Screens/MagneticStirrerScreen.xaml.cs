using DevicePlugins.Devices;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MaxChemical.Modules.Designer.Views.Controls.Screens
{
    /// <summary>
    /// MagneticStirrerScreen.xaml 的交互逻辑。
    /// 加热磁力搅拌器(RTC 系列控制器)控制屏。
    ///
    /// 读:轮询「获取所有数据」——一次 FC03 读 0x0002~0x0011 共 16 个寄存器。
    /// 写:驱动只有两条合并指令,屏幕上的所有操作都归到这两条:
    ///   「温度控制」= 温度设定值 + 温度开关
    ///   「搅拌控制」= 时间设定(时/分/秒) + 速度设定 + 速度开关
    /// 因此改设定值和拨开关走的是同一条指令,另一半参数用屏幕当前值补齐,
    /// 不会出现"只写开关把设定值冲掉"的情况。
    /// </summary>
    public partial class MagneticStirrerScreen : DeviceScreenBase
    {
        /// <summary>转速表盘满量程,与面板丝印 0-2000rpm 一致。</summary>
        private const double MaxSpeed = 2000.0;

        /// <summary>温度表盘满量程。设备回读到"温度最大设定值"后按实际值刷新。</summary>
        private double _maxTemp = 320.0;

        // ── 屏幕持有的当前设定与开关状态:下发合并指令时用来补齐另一半 ──
        private double _tempSv;
        private double _speedSv;
        private bool _heatOn;
        private bool _stirOn;
        private int _hourSv, _minuteSv, _secondSv;

        private readonly Brush _ledOff = new SolidColorBrush(Color.FromRgb(0x3A, 0x41, 0x47));
        private readonly Brush _ledAlarm = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
        private readonly Brush _ledActive = new SolidColorBrush(Color.FromRgb(0x35, 0xE0, 0x7A));

        public MagneticStirrerScreen()
        {
            InitializeComponent();
            PollIntervalMs = 1000;   // 一轮只有一次 Modbus 读,1s 足够
        }

        protected override string PollCommandName => "获取所有数据";

        protected override void OnDeviceAttached()
        {
            TbTitle.Text = Device?.Name ?? "磁力搅拌器";
            try
            {
                var mode = Device?.Parameters?.GetValue<string>("通信方式");
                var station = Device?.Parameters?.GetValue<double>("Modbus站号") ?? 1;
                TbMeta.Text = $"加热磁力搅拌器 · Modbus RTU · 站号 {station:F0}";
                TbComm.Text = $"通讯:{mode ?? "--"}";
            }
            catch { /* 参数缺失不影响屏幕打开 */ }
        }

        #region 轮询数据 → 屏幕

        protected override void OnDataRefreshed(DeviceParameters output)
        {
            // ── 量程:设备自己报的温度上限优先 ──
            var tempMax = output.GetNullableValue<double>("TempMaxSetting");
            if (tempMax.HasValue && tempMax.Value > 1)
            {
                _maxTemp = tempMax.Value;
                TbTempScaleMax.Text = _maxTemp.ToString("F0");
            }

            // ── 温度 ──
            var extTemp = output.GetNullableValue<double>("ExternalTemperature");
            var intTemp = output.GetNullableValue<double>("InternalTemperature");
            var tempSv = output.GetNullableValue<double>("TemperatureSV");

            string extText = extTemp.HasValue ? extTemp.Value.ToString("F1") : "--";
            TbExtTemp.Text = extText;
            TbTempBig.Text = extText;
            TbIntTemp.Text = intTemp.HasValue ? intTemp.Value.ToString("F1") : "--";
            ArcTemp.Angle = -180 + Clamp01((extTemp ?? 0) / _maxTemp) * 180;

            if (tempSv.HasValue)
            {
                _tempSv = tempSv.Value;
                TbTempSvHint.Text = $"设定 {_tempSv:F1} ℃";
                NeedleTempSv.Angle = -180 + Clamp01(_tempSv / _maxTemp) * 180;
                // 正在输入时不要抢用户的光标
                if (!TbSetTemp.IsKeyboardFocusWithin)
                {
                    TbSetTemp.Text = _tempSv.ToString("F1");
                }
            }

            // ── 转速 ──
            var speed = output.GetNullableValue<double>("SpeedMeasured");
            var speedSv = output.GetNullableValue<double>("SpeedSV");

            TbSpeedBig.Text = speed.HasValue ? speed.Value.ToString("F0") : "--";
            ArcSpeed.Angle = -180 + Clamp01((speed ?? 0) / MaxSpeed) * 180;

            if (speedSv.HasValue)
            {
                _speedSv = speedSv.Value;
                TbSpeedSvHint.Text = $"设定 {_speedSv:F0} rpm";
                NeedleSpeedSv.Angle = -180 + Clamp01(_speedSv / MaxSpeed) * 180;
                if (!TbSetSpeed.IsKeyboardFocusWithin)
                {
                    TbSetSpeed.Text = _speedSv.ToString("F0");
                }
            }

            // ── 定时:设定值回填输入框,计时值显示在信息条 ──
            var hSv = output.GetNullableValue<int>("TimeHourSV");
            var mSv = output.GetNullableValue<int>("TimeMinuteSV");
            var sSv = output.GetNullableValue<int>("TimeSecondSV");
            if (hSv.HasValue) _hourSv = hSv.Value;
            if (mSv.HasValue) _minuteSv = mSv.Value;
            if (sSv.HasValue) _secondSv = sSv.Value;
            if (!TbSetHour.IsKeyboardFocusWithin) TbSetHour.Text = _hourSv.ToString();
            if (!TbSetMinute.IsKeyboardFocusWithin) TbSetMinute.Text = _minuteSv.ToString();
            if (!TbSetSecond.IsKeyboardFocusWithin) TbSetSecond.Text = _secondSv.ToString();

            var th = output.GetNullableValue<int>("TimerHour") ?? 0;
            var tm = output.GetNullableValue<int>("TimerMinute") ?? 0;
            var ts = output.GetNullableValue<int>("TimerSecond") ?? 0;
            TbTimer.Text = $"{th:D2}:{tm:D2}:{ts:D2}";

            // ── 两个开关 ──
            var heatOn = output.GetNullableValue<bool>("TemperatureSwitch");
            var stirOn = output.GetNullableValue<bool>("SpeedSwitch");
            if (heatOn.HasValue) SetHeatUI(heatOn.Value);
            if (stirOn.HasValue) SetStirUI(stirOn.Value);

            // ── 示意图跟着走 ──
            if (extTemp.HasValue) StirrerVisual.Temperature = extTemp.Value;
            if (speed.HasValue) StirrerVisual.Speed = speed.Value;
            if (tempSv.HasValue) StirrerVisual.TemperatureSetpoint = _tempSv;
            if (speedSv.HasValue) StirrerVisual.SpeedSetpoint = _speedSv;

            // ── 状态位指示灯 ──
            SetLed(LedHeating, output.GetNullableValue<bool>("HeatingOutput") ?? false, _ledActive);
            SetLed(LedTimerFinished, output.GetNullableValue<bool>("TimerFinished") ?? false, _ledActive);
            SetLed(LedExtTempAlarm, output.GetNullableValue<bool>("ExtTempAlarm") ?? false, _ledAlarm);
            SetLed(LedIntTempAlarm, output.GetNullableValue<bool>("IntTempAlarm") ?? false, _ledAlarm);
            SetLed(LedUpperDeviation, output.GetNullableValue<bool>("UpperDeviationAlarm") ?? false, _ledAlarm);
            SetLed(LedSensorFallOff, output.GetNullableValue<bool>("SensorFallOffAlarm") ?? false, _ledAlarm);
            SetLed(LedIntTempProtect, output.GetNullableValue<bool>("IntTempProtect") ?? false, _ledAlarm);
            SetLed(LedMotorHallFault, output.GetNullableValue<bool>("MotorHallFault") ?? false, _ledAlarm);
        }

        protected override void OnPollError(Exception ex)
        {
            TbExtTemp.Text = TbIntTemp.Text = TbTempBig.Text = TbSpeedBig.Text = "--";
            TbTimer.Text = "--:--:--";
            TbCmdHint.Text = "通讯异常,数据已停止刷新";
            TbCmdHint.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x3B));
        }

        #endregion

        #region 操作 → 两条合并指令

        /// <summary>温度设定框回车:带着当前加热开关状态下发「温度控制」。</summary>
        private async void TbSetTemp_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            if (!double.TryParse(TbSetTemp.Text, out double value) || value < 0 || value > _maxTemp)
            {
                ShowHint($"温度设定需在 0 ~ {_maxTemp:F0} ℃ 之间", warn: true);
                TbSetTemp.Text = _tempSv.ToString("F1");
                return;
            }

            await SendTemperatureAsync(value, _heatOn);
            Keyboard.ClearFocus();
        }

        /// <summary>加热开关:带着当前温度设定值下发「温度控制」。</summary>
        private async void BtnHeat_Click(object sender, RoutedEventArgs e)
        {
            double sv = double.TryParse(TbSetTemp.Text, out var v) ? v : _tempSv;
            await SendTemperatureAsync(sv, !_heatOn);
        }

        /// <summary>转速设定框回车:带着当前定时与搅拌开关状态下发「搅拌控制」。</summary>
        private async void TbSetSpeed_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            if (!double.TryParse(TbSetSpeed.Text, out double value) || value < 0 || value > MaxSpeed)
            {
                ShowHint($"转速设定需在 0 ~ {MaxSpeed:F0} rpm 之间", warn: true);
                TbSetSpeed.Text = _speedSv.ToString("F0");
                return;
            }

            await SendStirAsync(value, _stirOn);
            Keyboard.ClearFocus();
        }

        /// <summary>定时三个框回车:同样走「搅拌控制」,转速与开关保持不变。</summary>
        private async void TbSetTime_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            double sv = double.TryParse(TbSetSpeed.Text, out var v) ? v : _speedSv;
            await SendStirAsync(sv, _stirOn);
            Keyboard.ClearFocus();
        }

        /// <summary>搅拌开关:带着当前转速与定时下发「搅拌控制」。</summary>
        private async void BtnStir_Click(object sender, RoutedEventArgs e)
        {
            double sv = double.TryParse(TbSetSpeed.Text, out var v) ? v : _speedSv;
            await SendStirAsync(sv, !_stirOn);
        }

        /// <summary>下发「温度控制」:温度设定值 + 温度开关,一条指令。</summary>
        private async System.Threading.Tasks.Task SendTemperatureAsync(double targetTemperature, bool switchOn)
        {
            var input = new Dictionary<string, object>
            {
                ["TargetTemperature"] = targetTemperature,
                // 基类把入参一律建成数值参数,驱动侧 GetValue<bool> 会把 1/0 转成 true/false
                ["TemperatureSwitch"] = switchOn ? 1 : 0
            };

            bool ok = await ExecuteCommandAsync("温度控制", input);
            if (ok)
            {
                _tempSv = targetTemperature;
                SetHeatUI(switchOn);
                ShowHint($"温度控制已下发:{targetTemperature:F1} ℃ · {(switchOn ? "启动" : "停止")}");
            }
            else
            {
                ShowHint("温度控制下发失败", warn: true);
            }
        }

        /// <summary>下发「搅拌控制」:定时 + 转速设定 + 速度开关,一条指令。</summary>
        private async System.Threading.Tasks.Task SendStirAsync(double targetSpeed, bool switchOn)
        {
            int hour = ParseTimeBox(TbSetHour, 99, _hourSv);
            int minute = ParseTimeBox(TbSetMinute, 59, _minuteSv);
            int second = ParseTimeBox(TbSetSecond, 59, _secondSv);

            var input = new Dictionary<string, object>
            {
                ["TimeHour"] = hour,
                ["TimeMinute"] = minute,
                ["TimeSecond"] = second,
                ["TargetSpeed"] = targetSpeed,
                ["SpeedSwitch"] = switchOn ? 1 : 0
            };

            bool ok = await ExecuteCommandAsync("搅拌控制", input);
            if (ok)
            {
                _speedSv = targetSpeed;
                _hourSv = hour; _minuteSv = minute; _secondSv = second;
                SetStirUI(switchOn);
                string timeText = (hour == 0 && minute == 0 && second == 0)
                    ? "不定时"
                    : $"{hour}:{minute:D2}:{second:D2}";
                ShowHint($"搅拌控制已下发:{targetSpeed:F0} rpm · {timeText} · {(switchOn ? "启动" : "停止")}");
            }
            else
            {
                ShowHint("搅拌控制下发失败", warn: true);
            }
        }

        /// <summary>读一个定时输入框,非法或越界时退回上次值。</summary>
        private static int ParseTimeBox(System.Windows.Controls.TextBox box, int max, int fallback)
        {
            if (!int.TryParse(box.Text, out int v) || v < 0 || v > max)
            {
                box.Text = fallback.ToString();
                return fallback;
            }
            return v;
        }

        #endregion

        #region UI 小工具

        private void SetHeatUI(bool on)
        {
            _heatOn = on;
            BtnHeat.Content = on ? "加热 开" : "加热 关";
            BtnHeat.Tag = on ? "on" : "off";
            StirrerVisual.IsHeating = on;
            UpdateVisualHint();
        }

        private void SetStirUI(bool on)
        {
            _stirOn = on;
            BtnStir.Content = on ? "搅拌 开" : "搅拌 关";
            BtnStir.Tag = on ? "on" : "off";
            StirrerVisual.IsStirring = on;
            UpdateVisualHint();
        }

        private void UpdateVisualHint()
        {
            TbVisualHint.Text = $"加热{(_heatOn ? "开" : "关")} · 搅拌{(_stirOn ? "开" : "关")}";
        }

        private void SetLed(Ellipse led, bool on, Brush onBrush)
        {
            led.Fill = on ? onBrush : _ledOff;
        }

        private void ShowHint(string text, bool warn = false)
        {
            TbCmdHint.Text = text;
            TbCmdHint.Foreground = warn
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x3B))
                : new SolidColorBrush(Color.FromRgb(0x9F, 0xD8, 0xC4));
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        #endregion
    }
}
