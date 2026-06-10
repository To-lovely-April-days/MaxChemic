using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;

namespace Scales_ModbusTCP
{
    [Export(typeof(IDevice))]
    public class Scales_ModbusTCP : Device, IControllableDevice, IFlowLifecycleAware, IDeviceUIInteraction
    {
        private readonly ILogService _logger;
        private readonly object _stateLock = new object();

        // 天平基础数据缓存
        private readonly Dictionary<string, double> _scaleData = new Dictionary<string, double>();

        // *** 密度参数 ***
        private double _currentDensity = 1.0;
        private readonly object _densityLock = new object();
        private const double DEFAULT_WATER_DENSITY = 1.0;
        private const double MIN_DENSITY = 0.1;
        private const double MAX_DENSITY = 2.0;

        // *** 流量控制字段 ***
        private double _targetFlowRate = 0;         // 目标质量流量 g/min
        private double _lastTargetFlowRate = 0;
        private bool _autoControlEnabled = false;
        private Timer _controlTimer = null;
        private readonly object _controlLock = new object();

        // *** 重量采样参数 ***
        // SmartWaitForWeightStability 内部维护本地采样窗口，无需独立后台定时器
        private const int WEIGHT_WINDOW_SIZE = 8;           // 滑动窗口最大点数
        private const int MIN_SAMPLES_FOR_FLOW = 3;         // 线性回归最少点数
        private const double MIN_FLOW_WINDOW_SECONDS = 8.0; // 最短时间跨度（秒）

        // *** 当前计算出的流量（供状态查询使用）***
        private double _calculatedFlowRate = 0;
        private readonly object _flowRateLock = new object();

        // *** 控制参数 ***
        private const int CONTROL_INTERVAL_MS = 20000;
        private const double FLOW_TOLERANCE_PERCENT = 1.5;     // ±1.5% 容差（比流量计宽）
        private const double DEAD_ZONE_PERCENT = 0.5;          // ±0.5% 死区
        private const double UPPER_PROTECTION_PERCENT = 0.8;   // 超调保护阈值

        private const double FLOW_TO_SPEED_RATIO = 1.0;
        private const int MAX_PUMP_SPEED = 100;
        private const int MAX_FLOW_RATE = 100;

        // *** PID参数（针对重量计算噪声适当保守）***
        private double _previousError = 0;
        private double _integralError = 0;
        private const double KP = 1.5;
        private const double KI = 0.10;
        private const double KD = 0.08;

        // *** 变化限制和滤波 ***
        private double _lastPumpSpeedOutput = 0;
        private double _previousPumpSpeed = 0;
        private const double MAX_SPEED_CHANGE_PER_CYCLE = 1.5;
        private const double SPEED_FILTER_FACTOR = 0.85;

        // *** 超调保护 ***
        private bool _isOvershootMode = false;
        private int _overshootProtectionCount = 0;

        // *** 稳定性检测 ***
        private int _stableCount = 0;
        private const int STABLE_CYCLES_REQUIRED = 3;

        // *** 三档动态等待参数（天平版，比流量计稍长）***
        private const int LOW_SPEED_WAIT_MS = 75000;   // 低速: 75秒
        private const int MID_SPEED_WAIT_MS = 55000;   // 中速: 55秒
        private const int HIGH_SPEED_WAIT_MS = 30000;  // 高速: 30秒
        // 重量计法的稳定阈值宽一些，因为 dW/dt 天然有更多噪声
        private const double STABILITY_THRESHOLD = 3.0;
        private const int MIN_STABLE_READINGS = 3;
        private const int MIN_WAIT_TIME_MS = 20000;    // 重量法需要更长最小等待时间

        // *** 日志系统 ***
        private static readonly object _logFileLock = new object();
        private string _logFilePath;
        private int _controlCycleCount = 0;
        private DateTime _controlStartTime;
        private readonly StringBuilder _currentCycleLog = new StringBuilder();

        // *** 状态跟踪 ***
        private readonly List<DeviceActiveState> _activeStates = new List<DeviceActiveState>();
        private readonly List<DeviceActiveState> _pausedStates = new List<DeviceActiveState>();
        private readonly object _statesLock = new object();

        // *** UI 事件 ***
        public event EventHandler<DeviceControlStateChangedEventArgs> ControlStateChanged;

        // *** 模拟用 ***
        private double _simulatedWeight = 5000.0;   // 模拟初始重量 5000g

        // 重量采样点结构
        private struct WeightSample
        {
            public double Weight { get; set; }
            public DateTime Timestamp { get; set; }
        }

        #region IDeviceUIInteraction 实现

        public async Task<CommandExecutionResult> ExecuteUICommandAsync(
            string commandName,
            Dictionary<string, object> parameters = null)
        {
            try
            {
                InfoLog($"收到UI命令: {commandName}");
                var command = Commands?.FirstOrDefault(c => c.Name == commandName);
                if (command?.Action == null)
                    return new CommandExecutionResult { Success = false, Message = $"未找到命令: {commandName}" };

                var deviceParams = ConvertToDeviceParameters(parameters, command.InputParameters);
                var outputParams = await Task.Run(() => command.Action(deviceParams));
                return ConvertToCommandResult(outputParams, commandName);
            }
            catch (Exception ex)
            {
                ErrorLog($"执行UI命令失败: {commandName}", ex);
                return new CommandExecutionResult { Success = false, Message = ex.Message, Exception = ex };
            }
        }

        public override DeviceState GetCurrentState()
        {
            double currentDensity;
            lock (_densityLock) { currentDensity = _currentDensity; }

            double calculatedFlow;
            lock (_flowRateLock) { calculatedFlow = _calculatedFlowRate; }

            return new DeviceState
            {
                States = new Dictionary<string, object>
                {
                    ["CalculatedFlowRate"] = calculatedFlow,
                    ["TargetFlowRate"] = _targetFlowRate,
                    ["IsFlowing"] = _autoControlEnabled,
                    ["Density"] = currentDensity,
                    ["ComId"] = this.ComId
                }
            };
        }

        #endregion

        #region 参数转换辅助方法

        private DeviceParameters ConvertToDeviceParameters(
            Dictionary<string, object> inputDict,
            DeviceParameters template)
        {
            var result = new DeviceParameters();
            if (inputDict == null || template?.Variables == null) return result;

            foreach (var templateParam in template.Variables)
            {
                if (!inputDict.ContainsKey(templateParam.Name))
                {
                    result.Variables.Add(templateParam.Clone() as ParameterBase);
                    continue;
                }

                var value = inputDict[templateParam.Name];
                if (templateParam is NumberParameter numParam)
                    result.Variables.Add(new NumberParameter(numParam.Name, numParam.MinValue, numParam.MaxValue,
                        Convert.ToDouble(value), numParam.HelpText));
                else if (templateParam is BooleanParameter boolParam)
                    result.Variables.Add(new BooleanParameter(boolParam.Name, Convert.ToBoolean(value), boolParam.HelpText));
                else if (templateParam is StringParameter strParam)
                    result.Variables.Add(new StringParameter(strParam.Name, value?.ToString() ?? "", strParam.HelpText));
            }
            return result;
        }

        private CommandExecutionResult ConvertToCommandResult(DeviceParameters outputParams, string commandName)
        {
            var success = false;
            var outputData = new Dictionary<string, object>();

            if (outputParams?.Variables != null)
            {
                foreach (var param in outputParams.Variables)
                {
                    outputData[param.Name] = param.Value;
                    if (param.Name == "Success" && param is BooleanParameter bp)
                        success = (bool)bp.Value;
                }
            }

            return new CommandExecutionResult
            {
                Success = success,
                Message = success ? $"{commandName} 执行成功" : $"{commandName} 执行失败",
                OutputData = outputData
            };
        }

        #endregion

        #region UI 状态通知

        private void RaiseControlStateChanged(string stateName, object stateValue,
            Dictionary<string, object> additionalData = null)
        {
            ControlStateChanged?.Invoke(this, new DeviceControlStateChangedEventArgs
            {
                DeviceId = this.DeviceId,
                ComId = this.ComId,
                StateName = stateName,
                StateValue = stateValue,
                Timestamp = DateTime.Now,
                AdditionalData = additionalData ?? new Dictionary<string, object>()
            });
        }

        #endregion

        #region 重量数据读取

        /// <summary>
        /// 读取天平当前重量（g）
        /// </summary>
        private async Task<double> ReadCurrentWeightAsync()
        {
            try
            {
                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp
                    && ConnectionManager != null && !IsSimulationMode)
                {
                    return await ConnectionManager.ReadAsync<double>(ComId, $"_{ComId}_g");
                }
                else if (IsSimulationMode)
                {
                    // 模拟：重量随泵速线性减少，叠加小量噪声
                    lock (_stateLock)
                    {
                        double density;
                        lock (_densityLock) { density = _currentDensity; }

                        // 每次调用间隔约 WEIGHT_SAMPLE_INTERVAL（调用方保证）
                        // 泵速 → 体积流量 → 质量流量 → 单次减少量
                        double massFlowPerMin = _lastPumpSpeedOutput * FLOW_TO_SPEED_RATIO * density * 0.95;
                        // 假设本次调用距上次约 3 秒
                        double decrease = massFlowPerMin / 60.0 * 3.0;

                        var rng = new Random();
                        double noise = (rng.NextDouble() - 0.5) * 0.4; // ±0.2g 噪声

                        _simulatedWeight = Math.Max(0, _simulatedWeight - decrease + noise);
                        return _simulatedWeight;
                    }
                }
                else
                {
                    throw new InvalidOperationException("设备未配置为有效的通信模式");
                }
            }
            catch (Exception ex)
            {
                AddCycleLog($"│ 读取重量失败: {ex.Message}");
                throw;
            }
        }

        #endregion

        #region 流量计算核心算法

        /// <summary>
        /// 线性回归计算质量流量
        /// 对于源端天平，重量随时间线性减少：W(t) = W0 + slope * t
        /// slope < 0，因此 flowRate = -slope（g/min，正值）
        /// 使用线性回归而非两点法，有效抑制单点噪声干扰
        /// </summary>
        private (double flowRate, bool isValid) CalculateFlowRateFromSamples(WeightSample[] samples)
        {
            if (samples == null || samples.Length < MIN_SAMPLES_FOR_FLOW)
                return (0, false);

            double totalSeconds = (samples.Last().Timestamp - samples.First().Timestamp).TotalSeconds;
            if (totalSeconds < MIN_FLOW_WINDOW_SECONDS)
                return (0, false);

            // 线性回归：x = 时间（分钟），y = 重量（g）
            int n = samples.Length;
            var origin = samples[0].Timestamp;
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;

            foreach (var s in samples)
            {
                double x = (s.Timestamp - origin).TotalMinutes;
                double y = s.Weight;
                sumX += x;
                sumY += y;
                sumXY += x * y;
                sumX2 += x * x;
            }

            double denom = n * sumX2 - sumX * sumX;
            if (Math.Abs(denom) < 1e-12)
                return (0, false);

            double slope = (n * sumXY - sumX * sumY) / denom; // g/min，负值
            double flowRate = -slope;                              // 转为正值

            // R² 检验，拒绝线性相关性很差的情况（可能是停泵或异常）
            double meanY = sumY / n;
            double ssTot = 0, ssRes = 0;
            double intercept = (sumY - slope * sumX) / n;
            foreach (var s in samples)
            {
                double x = (s.Timestamp - origin).TotalMinutes;
                double yPred = intercept + slope * x;
                ssTot += Math.Pow(s.Weight - meanY, 2);
                ssRes += Math.Pow(s.Weight - yPred, 2);
            }
            double r2 = ssTot > 1e-12 ? 1.0 - ssRes / ssTot : 0;
            if (r2 < 0.5) // 线性相关性低于50%，数据质量差
            {
                AddCycleLog($"│ 线性回归R²={r2:F3}偏低，数据质量较差（流量仍参考）");
            }

            return (Math.Max(0, flowRate), true);
        }

        /// <summary>
        /// 从样本数组提取连续对的瞬时流量，用于稳定性判断
        /// </summary>
        private List<double> CalculateInstantaneousRates(WeightSample[] samples)
        {
            var rates = new List<double>();
            for (int i = 1; i < samples.Length; i++)
            {
                double dW = samples[i - 1].Weight - samples[i].Weight;  // 正值 = 重量减少
                double dt = (samples[i].Timestamp - samples[i - 1].Timestamp).TotalMinutes;
                if (dt > 1e-4)
                    rates.Add(dW / dt);
            }
            return rates;
        }

        #endregion

        #region 三档动态等待策略

        private (int waitTimeMs, string speedLevel) GetWaitTimeByPumpSpeed(double pumpSpeed)
        {
            if (pumpSpeed <= 10) return (LOW_SPEED_WAIT_MS, "低速");
            else if (pumpSpeed <= 20) return (MID_SPEED_WAIT_MS, "中速");
            else return (HIGH_SPEED_WAIT_MS, "高速");
        }

        private int GetAdaptiveWaitTime(int baseWaitTimeMs, double speedChange)
        {
            double absChange = Math.Abs(speedChange);
            if (absChange > 2.0)
            {
                AddCycleLog($"│ 大泵速变化({absChange:F1}RPM)：延长等待时间80%");
                return (int)(baseWaitTimeMs * 1.8);
            }
            else if (absChange > 1.0)
            {
                AddCycleLog($"│ 中等泵速变化({absChange:F1}RPM)：延长等待时间40%");
                return (int)(baseWaitTimeMs * 1.4);
            }
            else if (absChange > 0.5)
            {
                AddCycleLog($"│ 小泵速变化({absChange:F1}RPM)：延长等待时间20%");
                return (int)(baseWaitTimeMs * 1.2);
            }
            return baseWaitTimeMs;
        }

        /// <summary>
        /// 重量计版智能等待 —— 核心差异所在
        /// 在等待期间自主采集重量样本，通过线性回归计算质量流量，
        /// 再通过瞬时流量序列的一致性判断是否稳定
        /// </summary>
        private async Task<(double stableFlowRate, double waitTimeSeconds, int sampleCount)>
            SmartWaitForWeightStability(double targetFlow, double currentPumpSpeed, double speedChange = 0)
        {
            var (baseWaitTimeMs, speedLevel) = GetWaitTimeByPumpSpeed(currentPumpSpeed);
            int adaptiveWaitTimeMs = GetAdaptiveWaitTime(baseWaitTimeMs, speedChange);

            // 重量法需要更长最小等待，保证积累足够的 ΔW
            int preWaitMs = Math.Max(MIN_WAIT_TIME_MS, adaptiveWaitTimeMs / 3);

            AddCycleLog($"│ ── 重量计流量稳定检测 ──");
            AddCycleLog($"│ 目标流量: {targetFlow:F2} g/min");
            AddCycleLog($"│ 当前泵速: {currentPumpSpeed:F1} RPM ({speedLevel})");
            AddCycleLog($"│ 泵速变化: {speedChange:F1} RPM");
            AddCycleLog($"│ 基础等待: {baseWaitTimeMs / 1000}s  自适应等待: {adaptiveWaitTimeMs / 1000}s");
            AddCycleLog($"│ 强制最小等待: {preWaitMs / 1000}s（重量法需累积ΔW）");

            // ─── 阶段1：强制最小等待，让泵速稳定下来 ───
            await Task.Delay(preWaitMs);

            // ─── 阶段2：采样循环 ───
            var localHistory = new Queue<WeightSample>();
            var startTime = DateTime.Now;
            int sampleCount = 0;
            int consecutiveStableCount = 0;
            double lastFlowRate = 0;

            int remainingWaitMs = adaptiveWaitTimeMs - preWaitMs;
            // 高速档采样更频繁；低速档间隔长以降低噪声影响
            int sampleIntervalMs = speedLevel == "高速" ? 2000 :
                                   speedLevel == "中速" ? 3000 : 4000;

            try
            {
                while ((DateTime.Now - startTime).TotalMilliseconds < remainingWaitMs)
                {
                    double weight = await ReadCurrentWeightAsync();
                    var now = DateTime.Now;

                    localHistory.Enqueue(new WeightSample { Weight = weight, Timestamp = now });
                    if (localHistory.Count > WEIGHT_WINDOW_SIZE)
                        localHistory.Dequeue();

                    sampleCount++;
                    double elapsedTotal = (DateTime.Now - startTime).TotalSeconds + preWaitMs / 1000.0;

                    // ── 计算当前流量 ──
                    var samples = localHistory.ToArray();
                    var (flowRate, isValid) = CalculateFlowRateFromSamples(samples);

                    if (!isValid)
                    {
                        AddCycleLog($"│ 采样#{sampleCount}: 重量={weight:F2}g，窗口不足 (已等{elapsedTotal:F1}s)");
                        await Task.Delay(sampleIntervalMs);
                        continue;
                    }

                    lastFlowRate = flowRate;

                    // 更新 UI 和外部状态
                    lock (_flowRateLock) { _calculatedFlowRate = flowRate; }
                    RaiseControlStateChanged("CalculatedFlowRate", flowRate);

                    AddCycleLog($"│ 采样#{sampleCount}: 重量={weight:F2}g，流量={flowRate:F2}g/min (已等{elapsedTotal:F1}s)");

                    // ── 稳定性判断：检查瞬时流量序列的一致性 ──
                    if (samples.Length >= MIN_STABLE_READINGS + 1
                        && elapsedTotal >= MIN_WAIT_TIME_MS / 1000.0)
                    {
                        var rates = CalculateInstantaneousRates(
                            samples.TakeLast(MIN_STABLE_READINGS + 1).ToArray());

                        if (rates.Count >= MIN_STABLE_READINGS)
                        {
                            double maxR = rates.Max();
                            double minR = rates.Min();
                            double avgR = rates.Average();

                            if (avgR > 0)
                            {
                                double variation = (maxR - minR) / avgR * 100.0;

                                if (variation < STABILITY_THRESHOLD)
                                {
                                    consecutiveStableCount++;
                                    AddCycleLog($"│ 稳定检测: 瞬时变化率{variation:F2}% < {STABILITY_THRESHOLD}% (连续{consecutiveStableCount}次)");

                                    int required = speedLevel == "低速" ? 3 : 2;
                                    if (consecutiveStableCount >= required)
                                    {
                                        AddCycleLog($"│ ✓ 连续{consecutiveStableCount}次稳定，流量={flowRate:F2} g/min");
                                        AddCycleLog($"│ 实际等待: {elapsedTotal:F1}s (预设{adaptiveWaitTimeMs / 1000}s)");
                                        return (flowRate, elapsedTotal, sampleCount);
                                    }
                                }
                                else
                                {
                                    consecutiveStableCount = 0;
                                    AddCycleLog($"│ 不稳定: 变化率{variation:F2}% (重置计数)");
                                }
                            }
                        }
                    }

                    await Task.Delay(sampleIntervalMs);
                }

                // ── 超时：用最终计算值 ──
                double totalWait = (DateTime.Now - startTime).TotalSeconds + preWaitMs / 1000.0;
                var finalSamples = localHistory.ToArray();
                var (finalFlow, finalValid) = CalculateFlowRateFromSamples(finalSamples);
                double result = finalValid ? finalFlow : lastFlowRate;

                AddCycleLog($"│ ⏰ 完成{speedLevel}自适应等待 {totalWait:F1}s，流量={result:F2} g/min");
                return (result, totalWait, sampleCount);
            }
            catch (Exception ex)
            {
                AddCycleLog($"│ 重量稳定检测异常: {ex.Message}");
                double errorWait = (DateTime.Now - startTime).TotalSeconds + preWaitMs / 1000.0;
                return (lastFlowRate, errorWait, sampleCount);
            }
        }

        #endregion

        #region 防超调PID控制算法（与流量计版本一致，操作量为计算流量）

        private double CalculateAntiOvershootPumpSpeed(
            double targetMassFlow, double actualMassFlow,
            double currentError, double currentPumpSpeed, double density)
        {
            try
            {
                double targetVolumeFlow = targetMassFlow / density;
                double actualVolumeFlow = actualMassFlow / density;
                double volumeError = targetVolumeFlow - actualVolumeFlow;
                double errorPercent = targetVolumeFlow > 0
                    ? Math.Abs(volumeError / targetVolumeFlow) * 100.0 : 0;

                AddCycleLog($"│ 密度修正: 质量流量 目标{targetMassFlow:F2}→实际{actualMassFlow:F2} g/min");
                AddCycleLog($"│           体积流量 目标{targetVolumeFlow:F2}→实际{actualVolumeFlow:F2} cm³/min");
                AddCycleLog($"│           体积误差: {volumeError:F2} cm³/min ({errorPercent:F2}%)");

                HandleOvershootDetection(actualVolumeFlow, targetVolumeFlow);

                double approachFactor = CalculateApproachFactor(actualVolumeFlow, targetVolumeFlow);

                double adaptiveKp = GetConservativeKp(errorPercent, actualVolumeFlow, targetVolumeFlow) * approachFactor;
                double adaptiveKi = GetConservativeKi(errorPercent, actualVolumeFlow, targetVolumeFlow) * approachFactor;
                double adaptiveKd = GetConservativeKd(errorPercent, actualVolumeFlow, targetVolumeFlow);

                double proportional = adaptiveKp * volumeError;

                _integralError += volumeError;
                ProcessIntegralWithOvershootProtection(actualVolumeFlow, targetVolumeFlow, volumeError);

                double integral = adaptiveKi * _integralError;
                double derivative = adaptiveKd * (volumeError - _previousError);
                _previousError = volumeError;

                double pidOutput = proportional + integral + derivative;
                double rawNewSpeed = currentPumpSpeed + pidOutput;

                double maxChange = GetConservativeMaxChange(errorPercent, actualVolumeFlow, targetVolumeFlow, approachFactor);
                double speedChange = rawNewSpeed - currentPumpSpeed;
                if (Math.Abs(speedChange) > maxChange)
                {
                    speedChange = Math.Sign(speedChange) * maxChange;
                    rawNewSpeed = currentPumpSpeed + speedChange;
                }

                rawNewSpeed = ApplyMultiLayerOvershootProtection(rawNewSpeed, currentPumpSpeed, actualVolumeFlow, targetVolumeFlow);

                double filterFactor = GetAdaptiveFilterFactor(errorPercent, actualVolumeFlow, targetVolumeFlow);
                double filteredSpeed = _lastPumpSpeedOutput * (1 - filterFactor) + rawNewSpeed * filterFactor;
                double finalSpeed = Math.Max(0, Math.Min(MAX_PUMP_SPEED, filteredSpeed));

                AddCycleLog($"┌── 防超调PID详情 ──");
                AddCycleLog($"│  Kp={adaptiveKp:F3} Ki={adaptiveKi:F3} Kd={adaptiveKd:F3} 接近系数={approachFactor:F2}");
                AddCycleLog($"│  P={proportional:F3} I={integral:F3}(累积={_integralError:F3}) D={derivative:F3}");
                AddCycleLog($"│  PID输出={pidOutput:F3}");
                AddCycleLog($"│  当前泵速={currentPumpSpeed:F1} 原始计算={rawNewSpeed:F1} 变化={speedChange:F1}(最大±{maxChange:F1})");
                AddCycleLog($"│  滤波系数={filterFactor:F2} 滤波输出={filteredSpeed:F1} 最终={finalSpeed:F1} RPM");
                if (_isOvershootMode)
                    AddCycleLog($"│  ⚠ 超调保护模式激活 (第{_overshootProtectionCount}次)");
                AddCycleLog($"└──────────────");

                return finalSpeed;
            }
            catch (Exception ex)
            {
                AddCycleLog($"[ERROR] PID计算异常: {ex.Message}");
                return currentPumpSpeed;
            }
        }

        private double CalculateApproachFactor(double actualFlow, double targetFlow)
        {
            if (actualFlow <= 0 || targetFlow <= 0) return 1.0;
            double ratio = actualFlow / targetFlow;
            if (ratio >= 1.0) return 0.3;
            if (ratio >= 0.95) return 0.3 + (1.0 - (ratio - 0.95) / 0.05) * 0.4;
            if (ratio >= 0.85) return 0.7 + (1.0 - (ratio - 0.85) / 0.1) * 0.2;
            if (ratio >= 0.70) return 0.9;
            return 1.0;
        }

        private double GetConservativeKp(double errorPercent, double actualFlow, double targetFlow)
        {
            double kp = KP;
            if (_isOvershootMode) { kp *= 0.4; return kp; }
            if (actualFlow > targetFlow) { kp *= 0.5; }
            else if (actualFlow > targetFlow * 0.95) { kp *= 0.7; }
            else if (actualFlow > targetFlow * 0.85) { kp *= 0.85; }
            if (errorPercent > 20) return kp * 1.1;
            if (errorPercent > 10) return kp;
            if (errorPercent > 5) return kp * 0.8;
            return kp * 0.6;
        }

        private double GetConservativeKi(double errorPercent, double actualFlow, double targetFlow)
        {
            if (_isOvershootMode || actualFlow > targetFlow) return KI * 0.3;
            if (actualFlow > targetFlow * 0.9) return KI * 0.6;
            return KI;
        }

        private double GetConservativeKd(double errorPercent, double actualFlow, double targetFlow)
        {
            if (actualFlow > targetFlow * 0.85) return KD * 1.5;
            return KD;
        }

        private void ProcessIntegralWithOvershootProtection(double actualFlow, double targetFlow, double currentError)
        {
            if (actualFlow > targetFlow && currentError < 0)
            {
                _integralError = Math.Max(_integralError, 0);
                AddCycleLog($"│ 积分保护: 超调时清除负向积分");
            }
            double maxIntegral = _isOvershootMode ? 2.0 :
                                 actualFlow > targetFlow * 0.95 ? 3.0 : 5.0;
            _integralError = Math.Max(-maxIntegral, Math.Min(maxIntegral, _integralError));
        }

        private void HandleOvershootDetection(double actualFlow, double targetFlow)
        {
            if (targetFlow <= 0) return;
            double overshootPct = (actualFlow - targetFlow) / targetFlow * 100;
            if (overshootPct > UPPER_PROTECTION_PERCENT)
            {
                if (!_isOvershootMode)
                {
                    AddCycleLog($"│ ⚠ 检测到超调: {overshootPct:F2}%");
                    _isOvershootMode = true;
                    _overshootProtectionCount = 0;
                }
                _overshootProtectionCount++;
                if (_integralError < 0) { _integralError = 0; }
            }
            else if (_isOvershootMode && overshootPct < -0.1)
            {
                _isOvershootMode = false;
                _overshootProtectionCount = 0;
                AddCycleLog($"│ ✓ 退出超调保护模式");
            }
        }

        private double GetConservativeMaxChange(double errorPercent, double actualFlow, double targetFlow, double approachFactor)
        {
            if (_isOvershootMode) return 0.3;
            if (actualFlow >= targetFlow) return 0.5;
            if (actualFlow > targetFlow * 0.98) return 0.8;
            if (actualFlow > targetFlow * 0.95) return 1.0;
            if (actualFlow > targetFlow * 0.85) return 1.2;
            if (errorPercent > 30) return MAX_SPEED_CHANGE_PER_CYCLE * 1.2;
            if (errorPercent > 15) return MAX_SPEED_CHANGE_PER_CYCLE;
            return MAX_SPEED_CHANGE_PER_CYCLE * 0.8;
        }

        private double ApplyMultiLayerOvershootProtection(
            double proposedSpeed, double currentSpeed, double actualFlow, double targetFlow)
        {
            if (actualFlow > targetFlow)
            {
                if (proposedSpeed > currentSpeed)
                {
                    double reduction = Math.Max(0.2, (actualFlow - targetFlow) / targetFlow * 2.0);
                    double ns = Math.Max(0, currentSpeed - reduction);
                    AddCycleLog($"│ 超调保护1: 强制降速 {currentSpeed:F1}→{ns:F1} RPM");
                    return ns;
                }
            }
            else if (actualFlow > targetFlow * 0.95)
            {
                if (proposedSpeed - currentSpeed > 0.8)
                {
                    double ls = currentSpeed + 0.8;
                    AddCycleLog($"│ 超调保护2: 限制增速 {proposedSpeed:F1}→{ls:F1} RPM");
                    return ls;
                }
            }
            else if (actualFlow > targetFlow * 0.85)
            {
                double maxIncrease = 1.2 * (targetFlow - actualFlow) / (targetFlow * 0.15);
                if (proposedSpeed - currentSpeed > maxIncrease)
                {
                    double ls = currentSpeed + maxIncrease;
                    AddCycleLog($"│ 超调保护3: 渐进接近 {proposedSpeed:F1}→{ls:F1} RPM");
                    return ls;
                }
            }
            return proposedSpeed;
        }

        private double GetAdaptiveFilterFactor(double errorPercent, double actualFlow, double targetFlow)
        {
            if (_isOvershootMode || actualFlow > targetFlow) return 0.98;
            if (actualFlow > targetFlow * 0.95) return 0.95;
            if (errorPercent > 20) return 0.8;
            if (errorPercent > 10) return 0.9;
            return SPEED_FILTER_FACTOR;
        }

        private double CalculateRequiredPumpSpeed(double targetMassFlow, double density)
        {
            double requiredVolumeFlow = targetMassFlow / density;
            double speed = requiredVolumeFlow * FLOW_TO_SPEED_RATIO * 0.98;
            return Math.Max(0, Math.Min(MAX_PUMP_SPEED, speed));
        }

        #endregion

        #region 自动控制启停与主循环

        private void StartAutoControl()
        {
            lock (_controlLock)
            {
                if (_autoControlEnabled) return;

                _previousError = 0;
                _integralError = 0;
                _stableCount = 0;
                _lastPumpSpeedOutput = 0;
                _previousPumpSpeed = 0;
                _controlCycleCount = 0;
                _controlStartTime = DateTime.Now;
                _isOvershootMode = false;
                _overshootProtectionCount = 0;

                _autoControlEnabled = true;
                _controlTimer = new Timer(AutoControlCallback, null, CONTROL_INTERVAL_MS, CONTROL_INTERVAL_MS);

                double density;
                lock (_densityLock) { density = _currentDensity; }

                //WriteDetailedLog("".PadRight(80, ''));
                //WriteDetailedLog($"天平重量计法流量控制启动 - {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                //WriteDetailedLog($"目标流量: {_targetFlowRate} g/min  密度: {density} g/cm³");
                //WriteDetailedLog($"控制方式: ΔW/Δt 线性回归 → PID → 泵速调整");
                //WriteDetailedLog($"PID参数: Kp={KP} Ki={KI} Kd={KD}");
                //WriteDetailedLog($"容差: ±{FLOW_TOLERANCE_PERCENT}%  死区: ±{DEAD_ZONE_PERCENT}%  超调保护: ±{UPPER_PROTECTION_PERCENT}%");
                //WriteDetailedLog($"等待档位: 低速{LOW_SPEED_WAIT_MS / 1000}s  中速{MID_SPEED_WAIT_MS / 1000}s  高速{HIGH_SPEED_WAIT_MS / 1000}s");
                //WriteDetailedLog("".PadRight(80, ''));
            }
        }

        private void StopAutoControl()
        {
            lock (_controlLock)
            {
                if (!_autoControlEnabled) return;

                _autoControlEnabled = false;
                _controlTimer?.Dispose();
                _controlTimer = null;

                var duration = DateTime.Now - _controlStartTime;
                WriteDetailedLog("■".PadRight(80, '■'));
                WriteDetailedLog($"天平流量控制停止 - {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                WriteDetailedLog($"运行时长: {duration.TotalMinutes:F1}min  总周期: {_controlCycleCount}次  超调保护: {_overshootProtectionCount}次");
                WriteDetailedLog("■".PadRight(80, '■'));

                _previousError = 0;
                _integralError = 0;
                _stableCount = 0;
                _lastPumpSpeedOutput = 0;
                _previousPumpSpeed = 0;
                _isOvershootMode = false;
                _overshootProtectionCount = 0;
            }
        }

        private async void AutoControlCallback(object state)
        {
            try
            {
                await PerformWeightBasedControlAsync();
            }
            catch (Exception ex)
            {
                WriteDetailedLog($"[ERROR] 控制回调异常: {ex.Message}\n{ex.StackTrace}");
                ErrorLog("天平流量控制执行异常", ex);
            }
        }

        /// <summary>
        /// 主控制逻辑：与 FlowMeter 的 PerformAntiOvershootControlAsync 平行
        /// 区别在于"读取流量"换成了"通过重量变化计算流量"
        /// </summary>
        private async Task PerformWeightBasedControlAsync()
        {
            StartCycleLog();

            double targetFlow;
            double currentDensity;
            bool controlEnabled;
            bool isTargetChanged = false;

            lock (_controlLock) { controlEnabled = _autoControlEnabled; }
            if (!controlEnabled)
            {
                EndCycleLog("控制已禁用");
                return;
            }

            lock (_stateLock)
            {
                targetFlow = _targetFlowRate;
                if (Math.Abs(targetFlow - _lastTargetFlowRate) > 0.01)
                {
                    isTargetChanged = true;
                    AddCycleLog($"检测到目标流量切换: {_lastTargetFlowRate:F2} → {targetFlow:F2} g/min");
                    _lastTargetFlowRate = targetFlow;
                }
            }

            if (isTargetChanged) ResetControlState();

            lock (_densityLock) { currentDensity = _currentDensity; }

            if (targetFlow <= 0)
            {
                EndCycleLog("目标流量无效");
                return;
            }

            AddCycleLog($"目标质量流量: {targetFlow:F2} g/min  液体密度: {currentDensity:F2} g/cm³");

            try
            {
                var pumpDeviceId = Parameters.GetValue<string>("PumpDeviceId");
                double currentPumpSpeed = _lastPumpSpeedOutput;

                if (currentPumpSpeed <= 0)
                {
                    try
                    {
                        currentPumpSpeed = await ConnectionManager.ReadAsync<double>(pumpDeviceId, "P0101泵速");
                        AddCycleLog($"│ 读取当前泵速: {currentPumpSpeed:F1} RPM");
                    }
                    catch
                    {
                        currentPumpSpeed = CalculateRequiredPumpSpeed(targetFlow, currentDensity);
                        AddCycleLog($"│ 泵速读取失败，使用密度估算值: {currentPumpSpeed:F1} RPM");
                    }
                }

                // ─── 首次或目标切换：直接98%积极起步 ───
                bool isFirstSetup = (_controlCycleCount == 1) || isTargetChanged;
                if (isFirstSetup)
                {
                    string reason = _controlCycleCount == 1 ? "首次启动" : "目标切换";
                    AddCycleLog($"┌── 积极起步策略 ({reason}) ──");
                    double firstSpeed = CalculateRequiredPumpSpeed(targetFlow, currentDensity);
                    AddCycleLog($"│ 体积流量需求: {targetFlow / currentDensity:F2} cm³/min");
                    AddCycleLog($"│ 初始泵速 (98%): {firstSpeed:F1} RPM");

                    bool ok = await SetPumpSpeedAsync(pumpDeviceId, firstSpeed);
                    if (ok)
                    {
                        _lastPumpSpeedOutput = firstSpeed;
                        _previousPumpSpeed = firstSpeed;
                        EndCycleLog($"积极起步成功: {firstSpeed:F1} RPM");
                    }
                    else
                    {
                        EndCycleLog($"积极起步失败: {firstSpeed:F1} RPM");
                    }
                    return;
                }

                // ─── 步骤1：重量计法智能等待并计算稳定流量 ───
                AddCycleLog("┌── 步骤1: 重量计法智能稳定检测 ──");
                double speedChange = currentPumpSpeed - _previousPumpSpeed;
                AddCycleLog($"│ 上次泵速变化: {_previousPumpSpeed:F1} → {currentPumpSpeed:F1} RPM (Δ{speedChange:F1})");

                var (stableFlowRate, stabilizeTime, readings) =
                    await SmartWaitForWeightStability(targetFlow, currentPumpSpeed, speedChange);

                _previousPumpSpeed = currentPumpSpeed;

                // 更新外部可查询的流量
                lock (_flowRateLock) { _calculatedFlowRate = stableFlowRate; }

                AddCycleLog($"│ 稳定质量流量: {stableFlowRate:F2} g/min");
                AddCycleLog($"│ 实际等待: {stabilizeTime:F1}s  采样点数: {readings}");
                AddCycleLog($"└─ 步骤1完成");

                // ─── 步骤2：计算偏差 ───
                AddCycleLog("┌── 步骤2: 计算质量流量偏差 ──");
                double massFlowError = targetFlow - stableFlowRate;
                double errorPercent = targetFlow > 0 ? Math.Abs(massFlowError / targetFlow) * 100.0 : 0;

                AddCycleLog($"│ 质量流量误差: {massFlowError:F2} g/min  ({errorPercent:F3}%)");
                AddCycleLog($"│ 死区: ±{DEAD_ZONE_PERCENT}%  容差: ±{FLOW_TOLERANCE_PERCENT}%");

                // ─── 死区判断 ───
                if (errorPercent <= DEAD_ZONE_PERCENT)
                {
                    _stableCount++;
                    _integralError *= 0.92;
                    AddCycleLog($"│ ✓ 死区稳定 ({errorPercent:F3}% ≤ {DEAD_ZONE_PERCENT}%) 连续{_stableCount}次");
                    EndCycleLog($"死区稳定 (偏差{errorPercent:F3}%, 连续{_stableCount}次)");
                    return;
                }

                // ─── 容差判断 ───
                if (errorPercent <= FLOW_TOLERANCE_PERCENT)
                {
                    _stableCount++;
                    AddCycleLog($"│ ✓ 容差内 ({errorPercent:F3}% ≤ {FLOW_TOLERANCE_PERCENT}%) {_stableCount}/{STABLE_CYCLES_REQUIRED}");
                    if (_stableCount >= STABLE_CYCLES_REQUIRED)
                    {
                        _integralError = 0;
                        EndCycleLog($"容差稳定 (连续{_stableCount}次)");
                        return;
                    }
                }
                else
                {
                    _stableCount = 0;
                    AddCycleLog($"│ 超出容差 ({errorPercent:F3}% > {FLOW_TOLERANCE_PERCENT}%)");
                }
                AddCycleLog($"└─ 步骤2完成，需调整泵速");

                // ─── 步骤3：读取当前泵速 ───
                try
                {
                    currentPumpSpeed = await ConnectionManager.ReadAsync<double>(pumpDeviceId, "P0101泵速");
                    AddCycleLog($"步骤3 - 当前泵速: {currentPumpSpeed:F1} RPM");
                }
                catch
                {
                    currentPumpSpeed = _lastPumpSpeedOutput;
                    AddCycleLog($"步骤3 - 泵速读取失败，使用上次值: {currentPumpSpeed:F1} RPM");
                }

                // ─── 步骤4：PID计算 ───
                AddCycleLog("┌── 步骤4: 防超调PID计算 ──");
                double newPumpSpeed = CalculateAntiOvershootPumpSpeed(
                    targetFlow, stableFlowRate, massFlowError, currentPumpSpeed, currentDensity);
                AddCycleLog($"└─ 计算泵速: {newPumpSpeed:F1} RPM");

                // ─── 步骤5：执行泵速 ───
                AddCycleLog("┌── 步骤5: 设置新泵速 ──");
                bool success = await SetPumpSpeedAsync(pumpDeviceId, newPumpSpeed);
                if (success)
                {
                    _lastPumpSpeedOutput = newPumpSpeed;
                    _previousPumpSpeed = newPumpSpeed;
                    RaiseControlStateChanged("PumpSpeed", newPumpSpeed);
                    AddCycleLog($"│ ✓ 设置成功: {currentPumpSpeed:F1} → {newPumpSpeed:F1} RPM");
                    string summary = $"调整: {currentPumpSpeed:F1}→{newPumpSpeed:F1}RPM  偏差{errorPercent:F3}%  密度{currentDensity:F2}";
                    if (_isOvershootMode) summary += " [超调保护]";
                    EndCycleLog(summary);
                }
                else
                {
                    AddCycleLog($"│ ✗ 设置失败");
                    EndCycleLog($"泵速设置失败: {newPumpSpeed:F1} RPM");
                }
            }
            catch (Exception ex)
            {
                AddCycleLog($"[ERROR] 控制异常: {ex.Message}");
                EndCycleLog($"异常: {ex.Message}");
                throw;
            }
        }

        private void ResetControlState()
        {
            _previousError = 0;
            _integralError = 0;
            _stableCount = 0;
            _lastPumpSpeedOutput = 0;
            _previousPumpSpeed = 0;
            _isOvershootMode = false;
            _overshootProtectionCount = 0;
            AddCycleLog("│ 控制状态已重置 (目标流量切换)");
        }

        #endregion

        #region 泵速设置

        private async Task<bool> SetPumpSpeedAsync(string pumpDeviceId, double speed)
        {
            try
            {
                double originalSpeed = speed;
                speed = Math.Max(0, Math.Min(MAX_PUMP_SPEED, speed));
                if (Math.Abs(originalSpeed - speed) > 0.01)
                    AddCycleLog($"│ 泵速限制: {originalSpeed:F1}→{speed:F1} RPM");

                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp
                    && ConnectionManager != null && !IsSimulationMode)
                {
                    await ConnectionManager.WriteAsync(pumpDeviceId, "P0101泵基准模式", true);
                    return await ConnectionManager.WriteAsync<float>(pumpDeviceId, "P0101泵基准泵速", (float)speed);
                }
                else if (IsSimulationMode)
                {
                    AddCycleLog($"│ [模拟] 泵速设置: {speed:F1} RPM");
                    return true;
                }
                else
                {
                    AddCycleLog("│ 错误: 未配置有效通信模式");
                    return false;
                }
            }
            catch (Exception ex)
            {
                AddCycleLog($"│ [ERROR] 设置泵速异常: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region 日志系统

        private void InitializeDetailedLogging()
        {
            try
            {
                var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ControlLogs");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _logFilePath = Path.Combine(dir, $"ScaleFlowControl_{ts}.log");

                WriteDetailedLog("=".PadRight(80, '='));
                WriteDetailedLog($"天平重量计法流量控制日志  启动: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                WriteDetailedLog($"控制原理: 源端天平 ΔW/Δt 线性回归 → PID → 泵速");
                WriteDetailedLog($"等待档位: 低速{LOW_SPEED_WAIT_MS / 1000}s  中速{MID_SPEED_WAIT_MS / 1000}s  高速{HIGH_SPEED_WAIT_MS / 1000}s");
                WriteDetailedLog($"稳定判断: 瞬时流量变化率 < {STABILITY_THRESHOLD}%，连续{MIN_STABLE_READINGS}次");
                WriteDetailedLog("=".PadRight(80, '='));
            }
            catch (Exception ex) { ErrorLog($"初始化日志失败: {ex.Message}"); }
        }

        private void WriteDetailedLog(string message)
        {
            if (string.IsNullOrEmpty(_logFilePath)) return;
            try
            {
                lock (_logFileLock)
                {
                    File.AppendAllText(_logFilePath,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}",
                        Encoding.UTF8);
                }
            }
            catch { }
        }

        private void StartCycleLog()
        {
            _controlCycleCount++;
            _currentCycleLog.Clear();
            _currentCycleLog.AppendLine($"┌─ 天平流量控制周期 #{_controlCycleCount} ─ {DateTime.Now:HH:mm:ss.fff} ────");
        }

        private void AddCycleLog(string message)
            => _currentCycleLog.AppendLine($"│ {message}");

        private void EndCycleLog(string result)
        {
            _currentCycleLog.AppendLine($"│ 周期结果: {result}");
            _currentCycleLog.AppendLine($"└─ 天平流量控制周期 #{_controlCycleCount} 结束 ─ {DateTime.Now:HH:mm:ss.fff} ────");
            _currentCycleLog.AppendLine("");
            WriteDetailedLog(_currentCycleLog.ToString());
        }

        private void LogControlStateChange(string action, object details = null)
        {
            var msg = details != null ? $"[控制状态] {action} - {details}" : $"[控制状态] {action}";
            WriteDetailedLog(msg);
            InfoLog(msg);
        }

        #endregion

        #region 构造函数与命令初始化

        public Scales_ModbusTCP()
        {
            _logger = LogManager.GetLogger<Scales_ModbusTCP>();

            Name = "天平";
            Manufacturer = "ModbusTCP天平";
            Category = DeviceCategories.Sensors;
            ComId = "S0101";

            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();

            AllowedRegions = new List<RegionType>
            {
                RegionType.Feed, RegionType.PreHeat, RegionType.Reaction,
                RegionType.Quench, RegionType.PostProcess
            };

            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/Scale.png";

            Parameters.Variables.Add(new StringParameter("DeviceId", "S0101")
            {
                Options = new ObservableCollection<string> { "S0101", "S0111", "S0121" },
                HelpText = "ModbusTCP天平设备ID"
            });

            Parameters.Variables.Add(new StringParameter("PumpDeviceId", "P0101")
            {
                Options = new ObservableCollection<string> { "P0101", "P0111", "P0121" },
                HelpText = "关联的蠕动泵设备ID"
            });

            InitializeDetailedLogging();
            InitializeCommands();
        }

        private void InitializeCommands()
        {
            // ── 1. 获取天平称重（保留原有）──
            Commands.Add(new DeviceCommand
            {
                Name = "获取天平称重",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/weight.png",
                HelpText = "读取天平当前称重值",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("ScaleId", "", "天平ID")                           { IsVisibleInVariableManager = true },
                        new NumberParameter("Weight", 0, 10000, 0, "称重值(g)")                { IsVisibleInVariableManager = true },
                        new BooleanParameter("Success", false, "读取成功")                     { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("获取天平称重", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double weight = 0;
                    bool success = false;

                    try
                    {
                        weight = await ReadCurrentWeightAsync();
                        success = true;
                        InfoLog($"天平 {ComId} 称重: {weight:F3}g");
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"读取天平称重失败: {ex.Message}");
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("ScaleId", ComId, "天平ID")                         { IsVisibleInVariableManager = true },
                            new NumberParameter("Weight", 0, 10000, Math.Round(weight, 3), "称重值(g)") { IsVisibleInVariableManager = true },
                            new BooleanParameter("Success", success, "读取成功")                     { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });

            // ── 2. 获取天平累计（保留原有）──
            Commands.Add(new DeviceCommand
            {
                Name = "获取天平累计",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/cumulative.png",
                HelpText = "读取天平累计值",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("ScaleId", "", "天平ID")                               { IsVisibleInVariableManager = true },
                        new NumberParameter("CumulativeWeight", 0, 100000, 0, "累计值(g)")         { IsVisibleInVariableManager = true },
                        new BooleanParameter("Success", false, "读取成功")                         { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("获取天平累计", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double cumulative = 0;
                    bool success = false;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp
                            && ConnectionManager != null && !IsSimulationMode)
                        {
                            cumulative = await ConnectionManager.ReadAsync<double>(ComId, $"_{ComId}_Add_g");
                            success = true;
                        }
                        else if (IsSimulationMode)
                        {
                            lock (_stateLock)
                            {
                                string key = $"{ComId}_cumulative";
                                _scaleData[key] = _scaleData.ContainsKey(key)
                                    ? _scaleData[key] + new Random().NextDouble() * 10
                                    : 100 + new Random().NextDouble() * 5000;
                                cumulative = _scaleData[key];
                            }
                            success = true;
                        }
                        else throw new InvalidOperationException("设备未配置为有效通信模式");

                        InfoLog($"天平 {ComId} 累计: {cumulative:F3}g");
                    }
                    catch (Exception ex) { ErrorLog($"读取天平累计失败: {ex.Message}"); }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("ScaleId", ComId, "天平ID")                                          { IsVisibleInVariableManager = true },
                            new NumberParameter("CumulativeWeight", 0, 100000, Math.Round(cumulative, 3), "累计值(g)") { IsVisibleInVariableManager = true },
                            new BooleanParameter("Success", success, "读取成功")                                      { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });

            // ── 3. 天平全部数据（保留原有）──
            Commands.Add(new DeviceCommand
            {
                Name = "天平全部数据",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/scale_all.png",
                HelpText = "一次性读取称重值和累计值",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("ScaleId", "", "天平ID")                               { IsVisibleInVariableManager = true },
                        new NumberParameter("Weight", 0, 10000, 0, "称重值(g)")                    { IsVisibleInVariableManager = true },
                        new NumberParameter("CumulativeWeight", 0, 100000, 0, "累计值(g)")         { IsVisibleInVariableManager = true },
                        new BooleanParameter("Success", false, "读取成功")                         { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("天平全部数据", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double weight = 0, cumulative = 0;
                    bool success = false;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp
                            && ConnectionManager != null && !IsSimulationMode)
                        {
                            var wTask = ConnectionManager.ReadAsync<double>(ComId, $"_{ComId}_g");
                            var cTask = ConnectionManager.ReadAsync<double>(ComId, $"_{ComId}_Add_g");
                            await Task.WhenAll(wTask, cTask);
                            weight = await wTask;
                            cumulative = await cTask;
                            success = true;
                        }
                        else if (IsSimulationMode)
                        {
                            weight = await ReadCurrentWeightAsync();
                            lock (_stateLock)
                            {
                                string key = $"{ComId}_cumulative";
                                _scaleData[key] = _scaleData.ContainsKey(key)
                                    ? _scaleData[key] + weight * 0.1
                                    : 100 + new Random().NextDouble() * 5000;
                                cumulative = _scaleData[key];
                            }
                            success = true;
                        }
                        else throw new InvalidOperationException("设备未配置为有效通信模式");

                        InfoLog($"天平 {ComId} 全量: 称重={weight:F3}g 累计={cumulative:F3}g");
                    }
                    catch (Exception ex) { ErrorLog($"读取天平全量失败: {ex.Message}"); }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("ScaleId", ComId, "天平ID")                                           { IsVisibleInVariableManager = true },
                            new NumberParameter("Weight", 0, 10000, Math.Round(weight, 3), "称重值(g)")                { IsVisibleInVariableManager = true },
                            new NumberParameter("CumulativeWeight", 0, 100000, Math.Round(cumulative, 3), "累计值(g)") { IsVisibleInVariableManager = true },
                            new BooleanParameter("Success", success, "读取成功")                                       { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });

            // ── 4. 设置目标流量（核心新增）──
            Commands.Add(new DeviceCommand
            {
                Name = "设置目标流量",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/speed_set.png",
                HelpText = "设置天平重量计法目标质量流量，启动防超调PID控制泵速",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TargetFlowRate", 0, MAX_FLOW_RATE, 10.0, "目标质量流量(g/min)"),
                        new NumberParameter("Density", MIN_DENSITY, MAX_DENSITY, DEFAULT_WATER_DENSITY, "液体密度(g/cm³)")
                        {
                            HelpText = "水=1.0，乙醇=0.789，导热油=0.8-0.9"
                        },
                        new BooleanParameter("EnableAutoControl", true, "启用自动PID控制")
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("Success", false, "设置成功")                                                     { IsVisibleInVariableManager = true },
                        new NumberParameter("SetFlowRate", 0, MAX_FLOW_RATE, 0, "设置的目标流量(g/min)")                       { IsVisibleInVariableManager = true },
                        new NumberParameter("SetDensity", MIN_DENSITY, MAX_DENSITY, 0, "液体密度(g/cm³)")                      { IsVisibleInVariableManager = true },
                        new NumberParameter("RequiredVolumeFlow", 0, MAX_FLOW_RATE * 2, 0, "所需体积流量(cm³/min)")            { IsVisibleInVariableManager = true },
                        new NumberParameter("InitialPumpSpeed", 0, MAX_PUMP_SPEED, 0, "初始泵速(RPM)")                        { IsVisibleInVariableManager = true },
                        new BooleanParameter("AutoControlStarted", false, "自动控制已启动")                                    { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("设置目标流量", async delegate (DeviceParameters parameters)
                {
                    RaiseControlStateChanged("CommandState_设置目标流量", CommandExecutionState.Running);

                    ComId = Parameters.GetValue<string>("DeviceId");
                    double targetFlowRate = parameters.GetValue<double>("TargetFlowRate");
                    double density = parameters.GetValue<double>("Density");
                    bool enableAutoControl = parameters.GetValue<bool>("EnableAutoControl");

                    targetFlowRate = Math.Max(0, Math.Min(MAX_FLOW_RATE, targetFlowRate));
                    density = Math.Max(MIN_DENSITY, Math.Min(MAX_DENSITY, density));

                    bool success = false;
                    bool autoStarted = false;
                    double initialSpeed = 0;
                    double volumeFlow = 0;

                    try
                    {
                        lock (_stateLock) { _targetFlowRate = targetFlowRate; }
                        lock (_densityLock) { _currentDensity = density; }

                        RaiseControlStateChanged("TargetFlowRate", targetFlowRate);
                        RaiseControlStateChanged("Density", density);

                        if (targetFlowRate > 0)
                        {
                            volumeFlow = targetFlowRate / density;
                            initialSpeed = CalculateRequiredPumpSpeed(targetFlowRate, density);

                            var pumpDeviceId = Parameters.GetValue<string>("PumpDeviceId");

                            // 先确保泵已启动
                            if (!await ConnectionManager.ReadAsync<bool>(pumpDeviceId, "P0101启动"))
                                await ConnectionManager.WriteAsync(pumpDeviceId, "P0101启动", true);

                            bool pumpOk = await SetPumpSpeedAsync(pumpDeviceId, initialSpeed);
                            if (pumpOk)
                            {
                                success = true;
                                _lastPumpSpeedOutput = initialSpeed;
                                RaiseControlStateChanged("IsFlowing", true);

                                if (enableAutoControl)
                                {
                                    StartAutoControl();
                                    autoStarted = true;
                                    InfoLog($"天平流量控制启动: 目标={targetFlowRate}g/min 密度={density}g/cm³ 初始泵速={initialSpeed:F1}RPM");
                                }

                                AddActiveState("ScaleFlowControlActive", "设置目标流量", "停止流量控制",
                                    stateData: new { targetFlowRate, density });
                                RaiseControlStateChanged("CommandState_设置目标流量", CommandExecutionState.Succeeded);
                            }
                            else
                            {
                                RaiseControlStateChanged("CommandState_设置目标流量", CommandExecutionState.Failed);
                            }
                        }
                        else
                        {
                            // 目标为0 → 停止控制
                            StopAutoControl();
                            RemoveActiveState("ScaleFlowControlActive");
                            success = true;
                            RaiseControlStateChanged("IsFlowing", false);
                            RaiseControlStateChanged("CommandState_设置目标流量", CommandExecutionState.Succeeded);
                            InfoLog("目标流量为0，天平流量控制已停止");
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"设置目标流量失败: {ex.Message}");
                        RaiseControlStateChanged("CommandState_设置目标流量", CommandExecutionState.Error,
                            new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });
                        throw;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new BooleanParameter("Success", success, "设置成功")                                                               { IsVisibleInVariableManager = true },
                            new NumberParameter("SetFlowRate", 0, MAX_FLOW_RATE, targetFlowRate, "设置的目标流量(g/min)")                       { IsVisibleInVariableManager = true },
                            new NumberParameter("SetDensity", MIN_DENSITY, MAX_DENSITY, density, "液体密度(g/cm³)")                             { IsVisibleInVariableManager = true },
                            new NumberParameter("RequiredVolumeFlow", 0, MAX_FLOW_RATE * 2, Math.Round(volumeFlow, 2), "所需体积流量(cm³/min)") { IsVisibleInVariableManager = true },
                            new NumberParameter("InitialPumpSpeed", 0, MAX_PUMP_SPEED, Math.Round(initialSpeed, 1), "初始泵速(RPM)")             { IsVisibleInVariableManager = true },
                            new BooleanParameter("AutoControlStarted", autoStarted, "自动控制已启动")                                            { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });

            // ── 5. 停止流量控制（新增）──
            Commands.Add(new DeviceCommand
            {
                Name = "停止流量控制",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/off.png",
                HelpText = "停止天平流量控制并将泵速归零",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("Success", false, "停止成功") { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("停止流量控制", async delegate (DeviceParameters parameters)
                {
                    RaiseControlStateChanged("CommandState_停止流量控制", CommandExecutionState.Running);
                    ComId = Parameters.GetValue<string>("DeviceId");
                    bool success = false;

                    try
                    {
                        StopAutoControl();
                        var pumpDeviceId = Parameters.GetValue<string>("PumpDeviceId");
                        success = await SetPumpSpeedAsync(pumpDeviceId, 0);

                        lock (_stateLock) { _targetFlowRate = 0; }
                        lock (_flowRateLock) { _calculatedFlowRate = 0; }

                        if (success)
                        {
                            RemoveActiveState("ScaleFlowControlActive");
                            RaiseControlStateChanged("IsFlowing", false);
                            RaiseControlStateChanged("TargetFlowRate", 0.0);
                            RaiseControlStateChanged("CalculatedFlowRate", 0.0);
                            RaiseControlStateChanged("CommandState_停止流量控制", CommandExecutionState.Succeeded);
                        }
                        else
                        {
                            RaiseControlStateChanged("CommandState_停止流量控制", CommandExecutionState.Failed);
                        }

                        InfoLog($"天平流量控制已停止，泵速归零");
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"停止流量控制失败: {ex.Message}");
                        RaiseControlStateChanged("CommandState_停止流量控制", CommandExecutionState.Error,
                            new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });
                        throw;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new BooleanParameter("Success", success, "停止成功") { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });

            // ── 6. 获取流量控制状态（新增）──
            Commands.Add(new DeviceCommand
            {
                Name = "获取流量控制状态",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/status.png",
                HelpText = "获取天平重量计法流量控制当前状态",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("AutoControlEnabled", false, "自动控制启用")              { IsVisibleInVariableManager = true },
                        new NumberParameter("TargetFlowRate", 0, MAX_FLOW_RATE, 0, "目标质量流量(g/min)")  { IsVisibleInVariableManager = true },
                        new NumberParameter("CalculatedFlowRate", 0, MAX_FLOW_RATE, 0, "计算质量流量(g/min)") { IsVisibleInVariableManager = true },
                        new NumberParameter("FlowDeviation", -MAX_FLOW_RATE, MAX_FLOW_RATE, 0, "流量偏差(g/min)") { IsVisibleInVariableManager = true },
                        new NumberParameter("DeviationPercent", -100, 100, 0, "偏差百分比(%)")          { IsVisibleInVariableManager = true },
                        new NumberParameter("CurrentDensity", MIN_DENSITY, MAX_DENSITY, 0, "液体密度(g/cm³)") { IsVisibleInVariableManager = true },
                        new NumberParameter("CurrentWeight", 0, 10000, 0, "当前称重(g)")                { IsVisibleInVariableManager = true },
                        new NumberParameter("CurrentPumpSpeed", 0, MAX_PUMP_SPEED, 0, "当前泵速(RPM)")   { IsVisibleInVariableManager = true },
                        new BooleanParameter("OvershootMode", false, "超调保护模式")                   { IsVisibleInVariableManager = true },
                        new StringParameter("ControlStatus", "", "控制状态")                           { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("获取流量控制状态", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    bool autoEnabled = false;
                    double targetFlow = 0, calculatedFlow = 0, density = 0;
                    double weight = 0, pumpSpeed = 0;
                    bool overshoot = false;
                    string status = "";

                    try
                    {
                        lock (_controlLock) { autoEnabled = _autoControlEnabled; }
                        lock (_stateLock) { targetFlow = _targetFlowRate; }
                        lock (_densityLock) { density = _currentDensity; }
                        lock (_flowRateLock) { calculatedFlow = _calculatedFlowRate; }
                        overshoot = _isOvershootMode;

                        weight = await ReadCurrentWeightAsync();

                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp
                            && ConnectionManager != null && !IsSimulationMode)
                        {
                            var pumpDeviceId = Parameters.GetValue<string>("PumpDeviceId");
                            pumpSpeed = await ConnectionManager.ReadAsync<double>(pumpDeviceId, "P0101泵速");
                        }
                        else if (IsSimulationMode)
                        {
                            pumpSpeed = _lastPumpSpeedOutput;
                        }

                        double deviation = calculatedFlow - targetFlow;
                        double deviationPct = targetFlow > 0 ? deviation / targetFlow * 100.0 : 0;

                        var (waitTime, speedLevel) = GetWaitTimeByPumpSpeed(pumpSpeed);
                        status = autoEnabled
                            ? $"天平PID控制中 (目标:{targetFlow:F1}g/min 计算:{calculatedFlow:F2}g/min 偏差:{deviationPct:F2}% {speedLevel}档)"
                            : "手动/停止";
                        if (overshoot) status += " [超调保护]";

                        double deviationRound = Math.Round(calculatedFlow - targetFlow, 3);
                        double deviationPctRound = Math.Round(deviationPct, 3);

                        return new DeviceParameters
                        {
                            Variables = new ObservableCollection<ParameterBase>
                            {
                                new BooleanParameter("AutoControlEnabled", autoEnabled, "自动控制启用")                            { IsVisibleInVariableManager = true },
                                new NumberParameter("TargetFlowRate", 0, MAX_FLOW_RATE, Math.Round(targetFlow, 3), "目标质量流量(g/min)")       { IsVisibleInVariableManager = true },
                                new NumberParameter("CalculatedFlowRate", 0, MAX_FLOW_RATE, Math.Round(calculatedFlow, 3), "计算质量流量(g/min)") { IsVisibleInVariableManager = true },
                                new NumberParameter("FlowDeviation", -MAX_FLOW_RATE, MAX_FLOW_RATE, deviationRound, "流量偏差(g/min)")           { IsVisibleInVariableManager = true },
                                new NumberParameter("DeviationPercent", -100, 100, deviationPctRound, "偏差百分比(%)")                           { IsVisibleInVariableManager = true },
                                new NumberParameter("CurrentDensity", MIN_DENSITY, MAX_DENSITY, Math.Round(density, 2), "液体密度(g/cm³)")        { IsVisibleInVariableManager = true },
                                new NumberParameter("CurrentWeight", 0, 10000, Math.Round(weight, 3), "当前称重(g)")                              { IsVisibleInVariableManager = true },
                                new NumberParameter("CurrentPumpSpeed", 0, MAX_PUMP_SPEED, Math.Round(pumpSpeed, 1), "当前泵速(RPM)")              { IsVisibleInVariableManager = true },
                                new BooleanParameter("OvershootMode", overshoot, "超调保护模式")                                                   { IsVisibleInVariableManager = true },
                                new StringParameter("ControlStatus", status, "控制状态")                                                          { IsVisibleInVariableManager = true }
                            }
                        };
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"获取流量控制状态失败: {ex.Message}");
                        return new DeviceParameters
                        {
                            Variables = new ObservableCollection<ParameterBase>
                            {
                                new StringParameter("ControlStatus", $"获取失败: {ex.Message}", "控制状态") { IsVisibleInVariableManager = true }
                            }
                        };
                    }
                })
            });
        }

        #endregion

        #region IControllableDevice 实现

        public List<DeviceActiveState> ActiveStates
        {
            get { lock (_statesLock) { return new List<DeviceActiveState>(_activeStates); } }
        }

        public event EventHandler<DeviceStateChangedEventArgs> StateChanged;

        public virtual async Task<bool> PauseAllActiveStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_activeStates.Any()) { InfoLog("天平没有活跃状态"); return true; }
            }

            StopAutoControl();

            lock (_statesLock)
            {
                foreach (var s in _activeStates.ToArray())
                {
                    _activeStates.Remove(s);
                    s.IsPaused = true;
                    _pausedStates.Add(s);
                }
            }
            InfoLog("天平所有活跃状态已暂停");
            return true;
        }

        public virtual async Task<bool> ResumeAllPausedStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_pausedStates.Any()) { InfoLog("天平没有暂停状态"); return true; }
            }

            lock (_statesLock)
            {
                foreach (var s in _pausedStates.ToArray())
                {
                    _pausedStates.Remove(s);
                    s.IsPaused = false;
                    s.ActivatedTime = DateTime.Now;
                    _activeStates.Add(s);
                }
            }

            StartAutoControl();
            InfoLog("天平所有暂停状态已恢复，自动控制重启");
            return true;
        }

        public virtual async Task<bool> StopAllActiveStatesAsync()
        {
            StopAutoControl();
            lock (_statesLock)
            {
                _activeStates.Clear();
                _pausedStates.Clear();
            }
            InfoLog("天平所有状态已停止");
            return true;
        }

        #endregion

        #region IFlowLifecycleAware 实现

        public virtual async Task<bool> OnFlowStartedAsync(FlowStartContext context)
        {
            try
            {
                InfoLog($"天平流程开始: {context.FlowName}");
                lock (_statesLock) { _activeStates.Clear(); _pausedStates.Clear(); }
                lock (_stateLock) { _scaleData.Clear(); }
                return true;
            }
            catch (Exception ex) { ErrorLog("天平流程开始处理失败", ex); return false; }
        }

        public virtual async Task<bool> OnFlowCompletedAsync(FlowCompletionContext context)
        {
            try
            {
                InfoLog($"天平流程完成: {context.FlowName}");
                StopAutoControl();
                await StopAllActiveStatesAsync();
                return true;
            }
            catch (Exception ex) { ErrorLog("天平流程完成处理失败", ex); return false; }
        }

        public virtual async Task<bool> OnFlowFailedAsync(FlowFailureContext context)
        {
            try
            {
                ErrorLog($"天平流程失败: {context.FlowName} - {context.ErrorMessage}");
                StopAutoControl();
                await StopAllActiveStatesAsync();
                return true;
            }
            catch (Exception ex) { ErrorLog("天平流程失败处理异常", ex); return false; }
        }

        #endregion

        #region 状态管理

        protected void AddActiveState(string stateName, string activateCommand, string deactivateCommand,
            string pauseCommand = null, string resumeCommand = null, object stateData = null)
        {
            lock (_statesLock)
            {
                var existing = _activeStates.FirstOrDefault(s => s.StateName == stateName);
                if (existing != null) { existing.ActivatedTime = DateTime.Now; existing.StateData = stateData; return; }

                _activeStates.Add(new DeviceActiveState
                {
                    StateName = stateName,
                    ActivateCommand = activateCommand,
                    DeactivateCommand = deactivateCommand,
                    PauseCommand = pauseCommand ?? deactivateCommand,
                    ResumeCommand = resumeCommand ?? activateCommand,
                    ActivatedTime = DateTime.Now,
                    IsPaused = false,
                    StateData = stateData
                });
            }
            StateChanged?.Invoke(this, new DeviceStateChangedEventArgs
            { StateName = stateName, IsActive = true, Timestamp = DateTime.Now });
        }

        protected void RemoveActiveState(string stateName)
        {
            lock (_statesLock)
            {
                var s = _activeStates.FirstOrDefault(x => x.StateName == stateName);
                if (s != null) _activeStates.Remove(s);
                var ps = _pausedStates.FirstOrDefault(x => x.StateName == stateName);
                if (ps != null) _pausedStates.Remove(ps);
            }
            StateChanged?.Invoke(this, new DeviceStateChangedEventArgs
            { StateName = stateName, IsActive = false, Timestamp = DateTime.Now });
        }

        #endregion

        #region 基类重写

        public override void Initialize()
        {
            try
            {
                base.Initialize();
                lock (_stateLock) { _scaleData.Clear(); }
                lock (_statesLock) { _activeStates.Clear(); _pausedStates.Clear(); }
                lock (_densityLock) { _currentDensity = DEFAULT_WATER_DENSITY; }
                lock (_controlLock) { _autoControlEnabled = false; }
                _isOvershootMode = false;
                InfoLog("天平设备初始化完成");
            }
            catch (Exception ex) { ErrorLog("天平初始化失败", ex); throw; }
        }

        public override void Shutdown()
        {
            try
            {
                StopAutoControl();
                StopAllActiveStatesAsync().Wait(TimeSpan.FromSeconds(5));
                base.Shutdown();
                InfoLog("天平设备关闭完成");
            }
            catch (Exception ex) { ErrorLog("天平关闭失败", ex); throw; }
        }

        protected override async Task<bool> OnConnectAsync()
        {
            InfoLog("天平连接");
            await Task.Delay(100);
            return true;
        }

        protected override async Task<bool> OnDisconnectAsync()
        {
            InfoLog("天平断开连接");
            StopAutoControl();
            await StopAllActiveStatesAsync();
            await Task.Delay(100);
            return true;
        }

        protected override async Task<bool> OnCheckConnectionAsync()
        {
            await Task.Delay(50);
            return true;
        }

        #endregion

        private Func<DeviceParameters, DeviceParameters> WrapAction(
            string commandName,
            Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return (DeviceParameters parameters) =>
            {
                _logger.LogInformation($"[天平命令-开始] Device={Name} Command={commandName}");
                var sw = Stopwatch.StartNew();
                try
                {
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();
                    _logger.LogInformation($"[天平命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[天平命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }
    }
}