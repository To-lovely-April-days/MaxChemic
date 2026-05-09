using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;

namespace FlowMeter_ModbusTCP
{
    [Export(typeof(IDevice))]
    public class FlowMeter_ModbusTCP : Device, IDeviceUIInteraction,IControllableDevice, IFlowLifecycleAware
    {
        private readonly ILogService _logger;
        private double _currentFlow = 0;
        private double _targetFlow = 0; // 基准流量设定值
        private readonly object _stateLock = new object();

        // *** 密度相关参数 ***
        private double _currentDensity = 1.0; // 当前液体密度，默认水的密度
        private readonly object _densityLock = new object();
        private const double DEFAULT_WATER_DENSITY = 1.0; // 水的密度
        private const double MIN_DENSITY = 0.1; // 最小密度限制
        private const double MAX_DENSITY = 2.0; // 最大密度限制

        // *** 防超调优化的控制参数 ***
        private bool _autoControlEnabled = false;
        private Timer _controlTimer = null;
        private readonly object _controlLock = new object();
        private const int CONTROL_INTERVAL_MS = 20000;         // 20秒控制周期
        private const double FLOW_TOLERANCE_PERCENT = 0.8;     // ±0.8%容差
        private const double DEAD_ZONE_PERCENT = 0.3;          // ±0.3%死区
        private const double UPPER_PROTECTION_PERCENT = 0.5;   // 超过目标0.5%时强制保护

        private const double FLOW_TO_SPEED_RATIO = 1.0;
        private const int MAX_PUMP_SPEED = 100;
        private const int MAX_FLOW_RATE = 100;
        // ═══════════════════════════════════════════════════════
        //  单位常量集中定义,与监控图表的轴分类对齐
        //   - g/min(质量流量)→ "流量轴" (F)
        //   - cm³/min(体积流量)→ 与质量流量物理量不同,归未分类
        //   - g/cm³ / RPM / % → 未分类
        // ═══════════════════════════════════════════════════════
        private const string UnitMassFlow = "g/min";
        private const string UnitVolumeFlow = "cm³/min";
        private const string UnitDensity = "g/cm³";
        private const string UnitPumpSpeed = "RPM";
        private const string UnitPercent = "%";
        // *** 优化后的PID参数 ***
        private double _previousError = 0;
        private double _integralError = 0;
        private const double KP = 1.8;   // 提高比例增益
        private const double KI = 0.15;  // 提高积分增益
        private const double KD = 0.12;  // 微分增益

        // *** 优化后的变化限制和滤波 ***
        private double _lastPumpSpeedOutput = 0;
        private double _previousPumpSpeed = 0;
        private const double MAX_SPEED_CHANGE_PER_CYCLE = 1.8;  // 提高最大变化
        private const double SPEED_FILTER_FACTOR = 0.85;       // 降低滤波系数，提高响应

        // *** 超调保护 ***
        private bool _isOvershootMode = false;
        private int _overshootProtectionCount = 0;

        // *** 稳定性检测 ***
        private int _stableCount = 0;
        private const int STABLE_CYCLES_REQUIRED = 3;
        // 在 _stableCount 旁边加
        private volatile bool _isStable = false;
        // *** 简化的三档动态等待时间参数 ***
        private const int LOW_SPEED_WAIT_MS = 70000;    // 低速: 70秒 (原55秒)
        private const int MID_SPEED_WAIT_MS = 50000;    // 中速: 50秒 (原35秒)  
        private const int HIGH_SPEED_WAIT_MS = 25000;   // 高速: 25秒 (原12秒) - 关键改进
        private const double STABILITY_THRESHOLD = 2.0; // 降低到2% (原3%)，更严格
        private const int MIN_STABLE_READINGS = 4;      // 增加到4次 (原3次)

        // 新增：最小等待时间，无论如何都要等待
        private const int MIN_WAIT_TIME_MS = 15000;     // 最少等待15秒
        // *** 详细日志记录系统 ***
        private static readonly object _logFileLock = new object();
        private string _logFilePath;
        private int _controlCycleCount = 0;
        private DateTime _controlStartTime;
        private readonly StringBuilder _currentCycleLog = new StringBuilder();

        // *** 状态跟踪相关字段 ***
        private readonly List<DeviceActiveState> _activeStates = new List<DeviceActiveState>();
        private readonly List<DeviceActiveState> _pausedStates = new List<DeviceActiveState>();
        private readonly object _statesLock = new object();
        //  新增：状态变化事件（设备驱动 → UI）
        public event EventHandler<DeviceControlStateChangedEventArgs> ControlStateChanged;

        //模拟相关
        private double _simulatedFlow = 0;
        private double _flowTrend = 0;
        private DateTime _lastSimulationTime = DateTime.Now;

        // 新增：流量轮询
        private Task _currentFlowPollingTask;
        private CancellationTokenSource _flowPollingTaskCts;

        #region IDeviceUIInteraction 实现

        /// <summary>
        /// 执行 UI 命令（UI → 设备驱动）
        /// </summary>
        public async Task<CommandExecutionResult> ExecuteUICommandAsync(
            string commandName,
            Dictionary<string, object> parameters = null)
        {
            try
            {
                InfoLog($"收到UI命令: {commandName}");

                //  查找对应的 DeviceCommand
                var command = Commands?.FirstOrDefault(c => c.Name == commandName);

                if (command?.Action == null)
                {
                    return new CommandExecutionResult
                    {
                        Success = false,
                        Message = $"未找到命令: {commandName}"
                    };
                }

                //  转换参数格式
                var deviceParams = ConvertToDeviceParameters(parameters, command.InputParameters);

                //  直接执行 Command.Action
                var outputParams = await Task.Run(() => command.Action(deviceParams));

                //  从 OutputParameters 提取结果
                return ConvertToCommandResult(outputParams, commandName);
            }
            catch (Exception ex)
            {
                ErrorLog($"执行UI命令失败: {commandName}", ex);
                return new CommandExecutionResult
                {
                    Success = false,
                    Message = ex.Message,
                    Exception = ex
                };
            }
        }

        /// <summary>
        /// 获取当前设备状态（设备驱动 → UI）
        /// </summary>
        public override DeviceState GetCurrentState()
        {
            double currentDensity;
            lock (_densityLock)
            {
                currentDensity = _currentDensity;
            }

            return new DeviceState
            {
                States = new Dictionary<string, object>
                {
                    ["CurrentFlow"] = _currentFlow,
                    ["TargetFlow"] = _targetFlow,
                    ["IsFlowing"] = _autoControlEnabled,
                    ["IsStable"] = _isStable,        // ★ 新增
                    ["Density"] = currentDensity,
                    ["ComId"] = this.ComId
                }
            };
        }

        #endregion

        #region 参数转换辅助方法

        /// <summary>
        /// 将 Dictionary 参数转换为 DeviceParameters
        /// </summary>
        private DeviceParameters ConvertToDeviceParameters(
            Dictionary<string, object> inputDict,
            DeviceParameters template)
        {
            var result = new DeviceParameters();

            if (inputDict == null || template?.Variables == null)
            {
                return result;
            }

            foreach (var templateParam in template.Variables)
            {
                if (!inputDict.ContainsKey(templateParam.Name))
                {
                    // 使用模板默认值
                    result.Variables.Add(templateParam.Clone() as ParameterBase);
                    continue;
                }

                var value = inputDict[templateParam.Name];

                // 根据参数类型创建对应的 Parameter
                if (templateParam is NumberParameter numParam)
                {
                    var convertedValue = Convert.ToDouble(value);
                    result.Variables.Add(new NumberParameter(
                        numParam.Name,
                        numParam.MinValue,
                        numParam.MaxValue,
                        convertedValue,
                        numParam.HelpText));
                }
                else if (templateParam is BooleanParameter boolParam)
                {
                    var convertedValue = Convert.ToBoolean(value);
                    result.Variables.Add(new BooleanParameter(
                        boolParam.Name,
                        convertedValue,
                        boolParam.HelpText));
                }
                else if (templateParam is StringParameter strParam)
                {
                    var convertedValue = value?.ToString() ?? "";
                    result.Variables.Add(new StringParameter(
                        strParam.Name,
                        convertedValue,
                        strParam.HelpText));
                }
            }

            return result;
        }

        /// <summary>
        /// 将 DeviceParameters 转换为 CommandExecutionResult
        /// </summary>
        private CommandExecutionResult ConvertToCommandResult(
            DeviceParameters outputParams,
            string commandName)
        {
            var success = false;
            var message = "";
            var outputData = new Dictionary<string, object>();

            if (outputParams?.Variables != null)
            {
                foreach (var param in outputParams.Variables)
                {
                    var value = param.Value;
                    outputData[param.Name] = value;

                    // 查找 Success 参数
                    if (param.Name == "Success" && param is BooleanParameter boolParam)
                    {
                        success = (bool)boolParam.Value;
                    }
                }
            }

            message = success ? $"{commandName} 执行成功" : $"{commandName} 执行失败";

            return new CommandExecutionResult
            {
                Success = success,
                Message = message,
                OutputData = outputData
            };
        }

        #endregion

        #region  新增：UI 命令内部实现

      

        private void RaiseControlStateChanged(string stateName, object stateValue, Dictionary<string, object> additionalData = null)
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

            InfoLog($"状态变化通知: {stateName} = {stateValue}");
        }


        #endregion

        #region 简化的三档动态等待策略

        /// <summary>
        /// 根据泵速选择等待档位
        /// </summary>
        private (int waitTimeMs, string speedLevel) GetWaitTimeByPumpSpeed(double pumpSpeed)
        {
            if (pumpSpeed <= 10)
            {
                return (LOW_SPEED_WAIT_MS, "低速");
            }
            else if (pumpSpeed <= 20)
            {
                return (MID_SPEED_WAIT_MS, "中速");
            }
            else
            {
                return (HIGH_SPEED_WAIT_MS, "高速");
            }
        }

        /// <summary>
        /// 智能三档等待流量稳定 - 改进版本
        /// </summary>
        private async Task<(double stableFlow, double waitTimeSeconds, int readingCount)>
            SmartWaitForStability(double targetFlow, double currentPumpSpeed, double speedChange = 0)
        {
            var (baseWaitTimeMs, speedLevel) = GetWaitTimeByPumpSpeed(currentPumpSpeed);

            // 根据泵速变化幅度动态调整等待时间
            int adaptiveWaitTimeMs = GetAdaptiveWaitTime(baseWaitTimeMs, speedChange);

            AddCycleLog($"│ 开始智能稳定检测");
            AddCycleLog($"│ 目标流量: {targetFlow:F2} g/min");
            AddCycleLog($"│ 当前泵速: {currentPumpSpeed:F1} RPM ({speedLevel})");
            AddCycleLog($"│ 泵速变化: {speedChange:F1} RPM");
            AddCycleLog($"│ 基础等待: {baseWaitTimeMs / 1000}秒");
            AddCycleLog($"│ 自适应等待: {adaptiveWaitTimeMs / 1000}秒");

            // 强制最小等待时间
            int preWaitMs = Math.Max(MIN_WAIT_TIME_MS, adaptiveWaitTimeMs / 3);
            AddCycleLog($"│ 强制最小等待: {preWaitMs / 1000}秒");
            await Task.Delay(preWaitMs);

            var startTime = DateTime.Now;
            int readingCount = 0;
            double lastFlow = 0;
            Queue<double> recentFlows = new Queue<double>();

            try
            {
                // 剩余时间内监测稳定性
                int remainingWaitMs = adaptiveWaitTimeMs - preWaitMs;
                int consecutiveStableCount = 0; // 连续稳定次数

                while ((DateTime.Now - startTime).TotalMilliseconds < remainingWaitMs)
                {
                    double currentFlow = await ReadCurrentFlowAsync();
                    //  通知UI：当前流量已更新
                    RaiseControlStateChanged("CurrentFlow", currentFlow);
                    readingCount++;
                    recentFlows.Enqueue(currentFlow);

                    if (recentFlows.Count > 6) recentFlows.Dequeue(); // 增加历史数据

                    var elapsedSeconds = (DateTime.Now - startTime).TotalSeconds + preWaitMs / 1000.0;
                    AddCycleLog($"│ 读数#{readingCount}: {currentFlow:F2} g/min (已等待{elapsedSeconds:F1}s)");

                    // 更严格的稳定检测
                    if (recentFlows.Count >= MIN_STABLE_READINGS &&
                        elapsedSeconds >= MIN_WAIT_TIME_MS / 1000.0) // 确保最小等待时间
                    {
                        var recent = recentFlows.TakeLast(MIN_STABLE_READINGS).ToArray();
                        double maxValue = recent.Max();
                        double minValue = recent.Min();
                        double avgFlow = recent.Average();

                        if (avgFlow > 0)
                        {
                            double variation = (maxValue - minValue) / avgFlow * 100;

                            // 连续稳定检测
                            if (variation < STABILITY_THRESHOLD)
                            {
                                consecutiveStableCount++;
                                AddCycleLog($"│ 稳定检测: 变化率{variation:F2}% < {STABILITY_THRESHOLD}% (连续{consecutiveStableCount}次)");

                                // 需要连续多次稳定才认为真正稳定
                                int requiredConsecutiveStable = speedLevel == "高速" ? 3 : 2;
                                if (consecutiveStableCount >= requiredConsecutiveStable)
                                {
                                    AddCycleLog($"│  连续{consecutiveStableCount}次稳定! 变化率{variation:F2}% < {STABILITY_THRESHOLD}%");
                                    AddCycleLog($"│ 稳定流量: {avgFlow:F2} g/min");
                                    AddCycleLog($"│ 实际等待: {elapsedSeconds:F1}秒 (预设{adaptiveWaitTimeMs / 1000}秒)");
                                    return (avgFlow, elapsedSeconds, readingCount);
                                }
                            }
                            else
                            {
                                consecutiveStableCount = 0; // 重置连续稳定计数
                                AddCycleLog($"│ 不稳定: 变化率{variation:F2}% > {STABILITY_THRESHOLD}% (重置计数)");
                            }
                        }
                    }

                    lastFlow = currentFlow;

                    // 检测间隔：根据速度档位调整
                    int checkInterval = speedLevel == "高速" ? 1000 : (speedLevel == "中速" ? 1500 : 2000);
                    await Task.Delay(checkInterval);
                }

                // 完整等待后返回
                var totalWaitTime = (DateTime.Now - startTime).TotalSeconds + preWaitMs / 1000.0;
                double finalFlow = recentFlows.Count > 0 ? recentFlows.Average() : lastFlow;

                AddCycleLog($"│ ⏰ 完成{speedLevel}自适应等待 {totalWaitTime:F1}秒");
                AddCycleLog($"│ 最终流量: {finalFlow:F2} g/min");
                return (finalFlow, totalWaitTime, readingCount);
            }
            catch (Exception ex)
            {
                AddCycleLog($"│  智能检测异常: {ex.Message}");
                var errorWaitTime = (DateTime.Now - startTime).TotalSeconds + preWaitMs / 1000.0;
                return (lastFlow, errorWaitTime, readingCount);
            }
        }

        /// <summary>
        /// 根据泵速变化幅度自适应调整等待时间
        /// </summary>
        private int GetAdaptiveWaitTime(int baseWaitTimeMs, double speedChange)
        {
            double changeMultiplier = 1.0;

            // 根据泵速变化幅度调整
            double absChange = Math.Abs(speedChange);
            if (absChange > 2.0)
            {
                changeMultiplier = 1.8; // 大变化：增加80%等待时间
                AddCycleLog($"│ 大泵速变化({absChange:F1}RPM)：延长等待时间80%");
            }
            else if (absChange > 1.0)
            {
                changeMultiplier = 1.4; // 中等变化：增加40%等待时间
                AddCycleLog($"│ 中等泵速变化({absChange:F1}RPM)：延长等待时间40%");
            }
            else if (absChange > 0.5)
            {
                changeMultiplier = 1.2; // 小变化：增加20%等待时间
                AddCycleLog($"│ 小泵速变化({absChange:F1}RPM)：延长等待时间20%");
            }

            return (int)(baseWaitTimeMs * changeMultiplier);
        }

        #endregion

        #region 防超调核心算法 - 密度修正版本

        /// <summary>
        /// 考虑密度的防超调PID控制算法
        /// </summary>
        private double CalculateAntiOvershootPumpSpeed(double targetMassFlow, double actualMassFlow,
            double currentError, double currentPumpSpeed, double density)
        {
            try
            {
                // 将质量流量转换为体积流量进行控制计算
                double targetVolumeFlow = targetMassFlow / density;
                double actualVolumeFlow = actualMassFlow / density;
                double volumeError = targetVolumeFlow - actualVolumeFlow;

                double errorPercent = Math.Abs(volumeError / targetVolumeFlow) * 100.0;

                AddCycleLog($"│ 密度修正分析:");
                AddCycleLog($"│   液体密度: {density:F2} g/cm³");
                AddCycleLog($"│   质量流量: 目标{targetMassFlow:F2} → 实际{actualMassFlow:F2} g/min");
                AddCycleLog($"│   体积流量: 目标{targetVolumeFlow:F2} → 实际{actualVolumeFlow:F2} cm³/min");
                AddCycleLog($"│   体积误差: {volumeError:F2} cm³/min ({errorPercent:F2}%)");

                // 超调检测和处理 - 基于体积流量
                HandleOvershootDetection(actualVolumeFlow, targetVolumeFlow);

                // 计算接近目标的程度，用于调整控制强度
                double approachFactor = CalculateApproachFactor(actualVolumeFlow, targetVolumeFlow);

                AddCycleLog($"│ 防超调分析: 体积误差{errorPercent:F2}%, 接近系数{approachFactor:F2}, 超调模式{_isOvershootMode}");

                // 自适应PID参数 - 防超调优化
                double adaptiveKp = GetConservativeKp(errorPercent, actualVolumeFlow, targetVolumeFlow) * approachFactor;
                double adaptiveKi = GetConservativeKi(errorPercent, actualVolumeFlow, targetVolumeFlow) * approachFactor;
                double adaptiveKd = GetConservativeKd(errorPercent, actualVolumeFlow, targetVolumeFlow);

                // PID计算 - 基于体积流量误差
                double proportional = adaptiveKp * volumeError;

                // 防超调的积分处理
                _integralError += volumeError;
                ProcessIntegralWithOvershootProtection(actualVolumeFlow, targetVolumeFlow, volumeError);

                double integral = adaptiveKi * _integralError;
                double derivative = adaptiveKd * (volumeError - _previousError);
                _previousError = volumeError;

                double pidOutput = proportional + integral + derivative;
                double rawNewSpeed = currentPumpSpeed + pidOutput;

                // 防超调的变化限制
                double maxChange = GetConservativeMaxChange(errorPercent, actualVolumeFlow, targetVolumeFlow, approachFactor);
                double speedChange = rawNewSpeed - currentPumpSpeed;

                if (Math.Abs(speedChange) > maxChange)
                {
                    speedChange = Math.Sign(speedChange) * maxChange;
                    rawNewSpeed = currentPumpSpeed + speedChange;
                }

                // 多层超调保护 - 基于体积流量
                rawNewSpeed = ApplyMultiLayerOvershootProtection(rawNewSpeed, currentPumpSpeed, actualVolumeFlow, targetVolumeFlow);

                // 最终滤波
                double filterFactor = GetAdaptiveFilterFactor(errorPercent, actualVolumeFlow, targetVolumeFlow);
                double filteredSpeed = _lastPumpSpeedOutput * (1 - filterFactor) + rawNewSpeed * filterFactor;
                double finalSpeed = Math.Max(0, Math.Min(MAX_PUMP_SPEED, filteredSpeed));

                // 记录详细信息
                LogAntiOvershootPIDDetails(targetMassFlow, actualMassFlow, currentError, proportional, integral, derivative,
                    pidOutput, currentPumpSpeed, rawNewSpeed, speedChange, filteredSpeed, finalSpeed,
                    adaptiveKp, adaptiveKi, adaptiveKd, approachFactor, maxChange, filterFactor, density);

                return finalSpeed;
            }
            catch (Exception ex)
            {
                AddCycleLog($"[ERROR] 密度修正防超调PID计算异常: {ex.Message}");
                return currentPumpSpeed;
            }
        }

        /// <summary>
        /// 计算接近系数 - 越接近目标越保守
        /// </summary>
        private double CalculateApproachFactor(double actualFlow, double targetFlow)
        {
            if (actualFlow <= 0 || targetFlow <= 0) return 1.0;

            double flowRatio = actualFlow / targetFlow;

            if (flowRatio >= 1.0) // 已经达到或超过目标
            {
                return 0.3; // 非常保守
            }
            else if (flowRatio >= 0.95) // 95%以上
            {
                double excess = (flowRatio - 0.95) / 0.05;
                return 0.3 + (1.0 - excess) * 0.4; // 从0.7降到0.3
            }
            else if (flowRatio >= 0.85) // 85-95%
            {
                double progress = (flowRatio - 0.85) / 0.1;
                return 0.7 + (1.0 - progress) * 0.2; // 从0.9降到0.7
            }
            else if (flowRatio >= 0.70) // 70-85%
            {
                return 0.9; // 稍微保守
            }
            else // 70%以下
            {
                return 1.0; // 正常响应
            }
        }

        /// <summary>
        /// 保守的Kp参数
        /// </summary>
        private double GetConservativeKp(double errorPercent, double actualFlow, double targetFlow)
        {
            double baseKp = KP;

            // 超调保护模式
            if (_isOvershootMode)
            {
                baseKp *= 0.4;
                AddCycleLog($"│ 超调保护: Kp降低至{baseKp:F3}");
                return baseKp;
            }

            // 根据当前流量位置调整
            if (actualFlow > targetFlow)
            {
                baseKp *= 0.5; // 已经超过目标，非常保守
                AddCycleLog($"│ 已超目标: Kp={baseKp:F3}");
            }
            else if (actualFlow > targetFlow * 0.95)
            {
                baseKp *= 0.7; // 非常接近目标
                AddCycleLog($"│ 接近目标: Kp={baseKp:F3}");
            }
            else if (actualFlow > targetFlow * 0.85)
            {
                baseKp *= 0.85; // 比较接近目标
            }

            // 根据误差大小进一步调整
            if (errorPercent > 20) return baseKp * 1.1;
            if (errorPercent > 10) return baseKp;
            if (errorPercent > 5) return baseKp * 0.8;
            return baseKp * 0.6;
        }

        /// <summary>
        /// 保守的Ki参数
        /// </summary>
        private double GetConservativeKi(double errorPercent, double actualFlow, double targetFlow)
        {
            double baseKi = KI;

            if (_isOvershootMode || actualFlow > targetFlow)
            {
                return baseKi * 0.3; // 超调时大幅降低积分
            }

            if (actualFlow > targetFlow * 0.9)
            {
                return baseKi * 0.6; // 接近目标时降低积分
            }

            return baseKi;
        }

        /// <summary>
        /// 保守的Kd参数
        /// </summary>
        private double GetConservativeKd(double errorPercent, double actualFlow, double targetFlow)
        {
            double baseKd = KD;

            if (actualFlow > targetFlow * 0.85)
            {
                return baseKd * 1.5; // 接近目标时增强微分，抑制超调
            }

            return baseKd;
        }

        /// <summary>
        /// 处理积分项的超调保护
        /// </summary>
        private void ProcessIntegralWithOvershootProtection(double actualFlow, double targetFlow, double currentError)
        {
            // 防超调的积分处理
            if (actualFlow > targetFlow && currentError < 0)
            {
                // 如果已经超过目标且误差为负，不允许积分继续增加
                _integralError = Math.Max(_integralError, 0);
                AddCycleLog($"│ 积分保护: 超调时清除负向积分");
            }

            // 超调模式下的积分限制
            double maxIntegral;
            if (_isOvershootMode)
            {
                maxIntegral = 2.0; // 超调时严格限制积分
            }
            else if (actualFlow > targetFlow * 0.95)
            {
                maxIntegral = 3.0; // 接近目标时限制积分
            }
            else
            {
                maxIntegral = 5.0; // 正常积分限制
            }

            _integralError = Math.Max(-maxIntegral, Math.Min(maxIntegral, _integralError));
        }

        /// <summary>
        /// 超调检测和处理
        /// </summary>
        private void HandleOvershootDetection(double actualFlow, double targetFlow)
        {
            double overshoot = actualFlow - targetFlow;
            double overshootPercent = (overshoot / targetFlow) * 100;

            if (overshootPercent > UPPER_PROTECTION_PERCENT)
            {
                if (!_isOvershootMode)
                {
                    AddCycleLog($"│  检测到超调: {overshootPercent:F2}% (>{UPPER_PROTECTION_PERCENT}%)");
                    _isOvershootMode = true;
                    _overshootProtectionCount = 0;
                }
                _overshootProtectionCount++;

                // 清除积分项，防止积分饱和
                if (_integralError < 0)
                {
                    _integralError = 0;
                    AddCycleLog($"│ 超调保护: 清除负向积分");
                }
            }
            else if (_isOvershootMode && overshootPercent < -0.1) // 回到目标以下
            {
                _isOvershootMode = false;
                _overshootProtectionCount = 0;
                AddCycleLog($"│  退出超调保护模式");
            }
        }

        /// <summary>
        /// 防超调的变化限制
        /// </summary>
        private double GetConservativeMaxChange(double errorPercent, double actualFlow, double targetFlow, double approachFactor)
        {
            double baseMaxChange = MAX_SPEED_CHANGE_PER_CYCLE;

            // 超调保护模式
            if (_isOvershootMode)
            {
                return 0.3; // 超调时最小变化
            }

            // 根据流量位置严格限制
            if (actualFlow >= targetFlow)
            {
                return 0.5; // 超过目标时最多变化0.5RPM
            }
            else if (actualFlow > targetFlow * 0.98)
            {
                return 0.8; // 非常接近时最多0.8RPM
            }
            else if (actualFlow > targetFlow * 0.95)
            {
                return 1.0; // 很接近时最多1RPM
            }
            else if (actualFlow > targetFlow * 0.85)
            {
                return 1.2; // 比较接近时最多1.2RPM
            }

            // 根据误差调整
            if (errorPercent > 30) return baseMaxChange * 1.2;
            if (errorPercent > 15) return baseMaxChange;
            return baseMaxChange * 0.8;
        }

        /// <summary>
        /// 多层超调保护
        /// </summary>
        private double ApplyMultiLayerOvershootProtection(double proposedSpeed, double currentSpeed, double actualFlow, double targetFlow)
        {
            // 第一层：绝对禁止超调时增加泵速
            if (actualFlow > targetFlow)
            {
                if (proposedSpeed > currentSpeed)
                {
                    double forcedReduction = Math.Max(0.2, (actualFlow - targetFlow) / targetFlow * 2.0);
                    double newSpeed = Math.Max(0, currentSpeed - forcedReduction);
                    AddCycleLog($"│ 超调保护1: 强制降速 {currentSpeed:F1}→{newSpeed:F1} RPM (降{forcedReduction:F1})");
                    return newSpeed;
                }
            }

            // 第二层：接近目标时限制大幅增加
            else if (actualFlow > targetFlow * 0.95)
            {
                double speedIncrease = proposedSpeed - currentSpeed;
                if (speedIncrease > 0.8)
                {
                    double limitedSpeed = currentSpeed + 0.8;
                    AddCycleLog($"│ 超调保护2: 限制增速 {proposedSpeed:F1}→{limitedSpeed:F1} RPM");
                    return limitedSpeed;
                }
            }

            // 第三层：渐进式接近保护
            else if (actualFlow > targetFlow * 0.85)
            {
                double speedIncrease = proposedSpeed - currentSpeed;
                double maxAllowedIncrease = 1.2 * (targetFlow - actualFlow) / (targetFlow * 0.15);

                if (speedIncrease > maxAllowedIncrease)
                {
                    double limitedSpeed = currentSpeed + maxAllowedIncrease;
                    AddCycleLog($"│ 超调保护3: 渐进接近 {proposedSpeed:F1}→{limitedSpeed:F1} RPM");
                    return limitedSpeed;
                }
            }

            return proposedSpeed;
        }

        /// <summary>
        /// 自适应滤波系数
        /// </summary>
        private double GetAdaptiveFilterFactor(double errorPercent, double actualFlow, double targetFlow)
        {
            if (_isOvershootMode || actualFlow > targetFlow)
            {
                return 0.98; // 超调时强烈滤波
            }

            if (actualFlow > targetFlow * 0.95)
            {
                return 0.95; // 接近目标时强滤波
            }

            if (errorPercent > 20) return 0.8;  // 大误差时快响应
            if (errorPercent > 10) return 0.9;  // 中等误差
            return SPEED_FILTER_FACTOR;         // 小误差时正常滤波
        }

        /// <summary>
        /// 根据密度优化的泵速计算 - 98%起步
        /// </summary>
        private double CalculateRequiredPumpSpeed(double targetMassFlow, double density, double actualFlow = 0, double deviation = 0)
        {
            double baseSpeed;

            if (_controlCycleCount == 0) // 首次启动
            {
                // 计算体积流量：质量流量 / 密度
                double requiredVolumeFlow = targetMassFlow / density;
                // 98%积极起步，基于体积流量
                baseSpeed = requiredVolumeFlow * FLOW_TO_SPEED_RATIO * 0.98;

                WriteDetailedLog($"[密度修正积极起步] 目标质量流量={targetMassFlow:F2}g/min, 密度={density:F2}g/cm³");
                WriteDetailedLog($"[密度修正积极起步] 所需体积流量={requiredVolumeFlow:F2}cm³/min → 初始泵速={baseSpeed:F1}RPM (98%积极起步)");
            }
            else
            {
                // 后续启动保持积极
                double requiredVolumeFlow = targetMassFlow / density;
                baseSpeed = requiredVolumeFlow * FLOW_TO_SPEED_RATIO * 0.95;
            }

            baseSpeed = Math.Max(0, Math.Min(MAX_PUMP_SPEED, baseSpeed));
            return baseSpeed;
        }

        /// <summary>
        /// 记录密度修正防超调PID详情
        /// </summary>
        private void LogAntiOvershootPIDDetails(double targetMassFlow, double actualMassFlow, double currentError,
            double proportional, double integral, double derivative, double pidOutput,
            double currentPumpSpeed, double rawNewSpeed, double speedChange,
            double filteredSpeed, double finalSpeed, double adaptiveKp, double adaptiveKi, double adaptiveKd,
            double approachFactor, double maxChange, double filterFactor, double density)
        {
            double targetVolumeFlow = targetMassFlow / density;
            double actualVolumeFlow = actualMassFlow / density;
            double volumeError = targetVolumeFlow - actualVolumeFlow;

            AddCycleLog($"┌── 密度修正防超调PID详情 ──");
            AddCycleLog($"│   液体密度: {density:F2} g/cm³");
            AddCycleLog($"│   质量流量: 目标{targetMassFlow:F2} → 实际{actualMassFlow:F2} g/min");
            AddCycleLog($"│   体积流量: 目标{targetVolumeFlow:F2} → 实际{actualVolumeFlow:F2} cm³/min");
            AddCycleLog($"│   体积误差: {volumeError:F2} cm³/min");
            AddCycleLog($"│   误差百分比: {Math.Abs(volumeError / targetVolumeFlow) * 100:F3}%");
            AddCycleLog($"│   ───");
            AddCycleLog($"│   自适应Kp: {adaptiveKp:F3} (基础{KP})");
            AddCycleLog($"│   自适应Ki: {adaptiveKi:F3} (基础{KI})");
            AddCycleLog($"│   自适应Kd: {adaptiveKd:F3} (基础{KD})");
            AddCycleLog($"│   接近系数: {approachFactor:F2}");
            AddCycleLog($"│   ───");
            AddCycleLog($"│   P项: {proportional:F3}");
            AddCycleLog($"│   I项: {integral:F3} (累积={_integralError:F3})");
            AddCycleLog($"│   D项: {derivative:F3}");
            AddCycleLog($"│   PID输出: {pidOutput:F3}");
            AddCycleLog($"│   ───");
            AddCycleLog($"│   当前泵速: {currentPumpSpeed:F1} RPM");
            AddCycleLog($"│   原始计算: {rawNewSpeed:F1} RPM");
            AddCycleLog($"│   变化限制: {speedChange:F1} RPM (最大±{maxChange:F1})");
            AddCycleLog($"│   滤波系数: {filterFactor:F2}");
            AddCycleLog($"│   滤波输出: {filteredSpeed:F1} RPM");
            AddCycleLog($"│   最终输出: {finalSpeed:F1} RPM");
            if (_isOvershootMode)
            {
                AddCycleLog($"│    超调保护模式激活 (第{_overshootProtectionCount}次)");
            }
            AddCycleLog($"└──────────────");
        }

        #endregion

        #region 智能自动控制逻辑 - 密度修正防超调版本

        /// <summary>
        /// 启动智能自动控制
        /// </summary>
        private void StartAutoControl()
        {
            lock (_controlLock)
            {
                if (_autoControlEnabled)
                {
                    LogControlStateChange("智能自动控制已经在运行中");
                    return;
                }

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

                double currentDensity;
                lock (_densityLock)
                {
                    currentDensity = _currentDensity;
                }

                var startDetails = new
                {
                    间隔 = $"{CONTROL_INTERVAL_MS}ms",
                    容差 = $"±{FLOW_TOLERANCE_PERCENT}%",
                    死区 = $"±{DEAD_ZONE_PERCENT}%",
                    超调保护 = $"±{UPPER_PROTECTION_PERCENT}%",
                    PID参数 = $"Kp={KP}, Ki={KI}, Kd={KD}",
                    智能检测 = $"三档动态等待",
                    目标流量 = $"{_targetFlow}g/min",
                    液体密度 = $"{currentDensity}g/cm³",
                    防超调 = "启用",
                    密度修正 = "启用"
                };

                LogControlStateChange("密度修正防超调智能流量控制启动", startDetails);

                //WriteDetailedLog("");
                //WriteDetailedLog("".PadRight(100, ''));
                //WriteDetailedLog($"密度修正防超调智能控制启动 - {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                //WriteDetailedLog($"目标流量: {_targetFlow} g/min");
                //WriteDetailedLog($"液体密度: {currentDensity} g/cm³");
                //WriteDetailedLog($"控制参数: 间隔={CONTROL_INTERVAL_MS}ms, 容差=±{FLOW_TOLERANCE_PERCENT}%, 死区=±{DEAD_ZONE_PERCENT}%");
                //WriteDetailedLog($"超调保护: ±{UPPER_PROTECTION_PERCENT}% 阈值");
                //WriteDetailedLog($"PID参数: Kp={KP}, Ki={KI}, Kd={KD} (密度修正版本)");
                //WriteDetailedLog($"智能检测: 三档动态等待 (低速{LOW_SPEED_WAIT_MS / 1000}s, 中速{MID_SPEED_WAIT_MS / 1000}s, 高速{HIGH_SPEED_WAIT_MS / 1000}s)");
                //WriteDetailedLog("".PadRight(100, ''));
                //WriteDetailedLog("");
            }
        }

        /// <summary>
        /// 停止智能自动控制
        /// </summary>
        private void StopAutoControl()
        {
            lock (_controlLock)
            {
                if (!_autoControlEnabled)
                {
                    return;
                }

                _autoControlEnabled = false;
                _controlTimer?.Dispose();
                _controlTimer = null;

                var duration = DateTime.Now - _controlStartTime;
                var stopDetails = new
                {
                    运行时长 = $"{duration.TotalMinutes:F1}分钟",
                    总周期数 = _controlCycleCount,
                    平均周期间隔 = $"{duration.TotalMilliseconds / Math.Max(_controlCycleCount, 1):F0}ms",
                    超调保护次数 = _overshootProtectionCount,
                    版本 = "密度修正三档动态等待版本"
                };

                LogControlStateChange("密度修正防超调智能流量控制停止", stopDetails);

                WriteDetailedLog("");
                WriteDetailedLog("■".PadRight(100, '■'));
                WriteDetailedLog($"密度修正防超调智能控制停止 - {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                WriteDetailedLog($"运行时长: {duration.TotalMinutes:F1}分钟");
                WriteDetailedLog($"总控制周期: {_controlCycleCount}次");
                WriteDetailedLog($"平均周期间隔: {duration.TotalMilliseconds / Math.Max(_controlCycleCount, 1):F0}ms");
                WriteDetailedLog($"超调保护触发: {_overshootProtectionCount}次");
                WriteDetailedLog($"版本: 密度修正三档动态等待智能控制系统");
                WriteDetailedLog("■".PadRight(100, '■'));
                WriteDetailedLog("");

                _previousError = 0;
                _integralError = 0;
                _stableCount = 0;
                _lastPumpSpeedOutput = 0;
                _previousPumpSpeed = 0;
                _isOvershootMode = false;
                _overshootProtectionCount = 0;
                _isStable = false;
                RaiseControlStateChanged("IsStable", false);
            }
        }

        /// <summary>
        /// 智能自动控制回调
        /// </summary>
        private async void AutoControlCallback(object state)
        {
            try
            {
                await PerformAntiOvershootControlAsync();
            }
            catch (Exception ex)
            {
                WriteDetailedLog($"[ERROR] 密度修正防超调控制执行异常: {ex.Message}");
                WriteDetailedLog($"[ERROR] 异常堆栈: {ex.StackTrace}");
                ErrorLog("密度修正防超调控制执行异常", ex);
            }
        }
        private double _lastTargetFlow = 0; // 类成员变量
        /// <summary>
        /// 执行密度修正防超调智能控制逻辑
        /// </summary>
        private async Task PerformAntiOvershootControlAsync()
        {
            StartCycleLog();

            double targetFlow;
            double currentDensity;
            bool controlEnabled;
            bool isTargetChanged = false;

            lock (_controlLock)
            {
                controlEnabled = _autoControlEnabled;
            }

            if (!controlEnabled)
            {
                AddCycleLog("控制已禁用，跳过本周期");
                EndCycleLog("控制已禁用");
                return;
            }

            lock (_stateLock)
            {
                targetFlow = _targetFlow;
                // 检测目标流量是否改变
                if (Math.Abs(targetFlow - _lastTargetFlow) > 0.01)
                {
                    isTargetChanged = true;
                    AddCycleLog($"检测到目标流量切换: {_lastTargetFlow:F2} → {targetFlow:F2} g/min");
                    _lastTargetFlow = targetFlow;
                }
            }

            // 如果目标改变，重置控制状态
            if (isTargetChanged)
            {
                ResetControlState();
                AddCycleLog($"目标流量切换，重置控制状态");
            }
            lock (_densityLock)
            {
                currentDensity = _currentDensity;
            }

            if (targetFlow <= 0)
            {
                AddCycleLog($"目标流量无效 ({targetFlow})，跳过本周期");
                EndCycleLog("目标流量无效");
                return;
            }

            AddCycleLog($"目标质量流量: {targetFlow:F2} g/min");
            AddCycleLog($"液体密度: {currentDensity:F2} g/cm³");

            try
            {
                // *** 获取当前泵速用于智能等待 ***
                var pumpDeviceId = Parameters.GetValue<string>("PumpDeviceId");
                double currentPumpSpeed = _lastPumpSpeedOutput;

                if (currentPumpSpeed <= 0)
                {
                    try
                    {
                        currentPumpSpeed = await ConnectionManager.ReadAsync<double>(pumpDeviceId, "P0101泵速");
                        AddCycleLog($"│ 读取当前泵速: {currentPumpSpeed:F1} RPM");
                    }
                    catch (Exception ex)
                    {
                        // 使用密度修正的默认值
                        currentPumpSpeed = (targetFlow / currentDensity) * 0.98;
                        AddCycleLog($"│ 泵速读取失败，使用密度修正默认值: {currentPumpSpeed:F1} RPM");
                    }
                }
                else
                {
                    AddCycleLog($"│ 使用上次泵速: {currentPumpSpeed:F1} RPM");
                }

                // *** 判断是否首次设置 ***
                bool isFirstSetup = (_controlCycleCount == 1) || isTargetChanged;

                if (isFirstSetup)
                {
                    string reason = _controlCycleCount == 1 ? "系统首次启动" : "目标流量切换";
                    AddCycleLog($"┌── 密度修正积极起步策略 ({reason}) ──");
                    // *** 首次设置：使用密度修正的98%积极起步 ***
                    double requiredVolumeFlow = targetFlow / currentDensity;
                    double firstSetupSpeed = requiredVolumeFlow * FLOW_TO_SPEED_RATIO * 0.98;
                    firstSetupSpeed = Math.Max(0, Math.Min(MAX_PUMP_SPEED, firstSetupSpeed));

                    AddCycleLog("┌── 密度修正首次积极起步策略 ──");
                    AddCycleLog($"│  首次设置使用密度修正98%积极起步");
                    AddCycleLog($"│ 目标质量流量: {targetFlow:F2} g/min");
                    AddCycleLog($"│ 液体密度: {currentDensity:F2} g/cm³");
                    AddCycleLog($"│ 所需体积流量: {requiredVolumeFlow:F2} cm³/min");
                    AddCycleLog($"│ 计算泵速: {requiredVolumeFlow:F2} × 0.98 = {firstSetupSpeed:F1} RPM");
                    AddCycleLog($"│ 跳过PID计算，直接设置泵速");
                    AddCycleLog($"└─ 密度修正首次起步泵速: {firstSetupSpeed:F1} RPM");

                    // 直接设置泵速
                    AddCycleLog("┌── 步骤: 设置密度修正首次起步泵速 ──");
                    var firstSetStart = DateTime.Now;
                    bool firstSetSuccess = await SetPumpSpeedAsync(pumpDeviceId, firstSetupSpeed);
                    var firstSetDuration = DateTime.Now - firstSetStart;

                    if (firstSetSuccess)
                    {
                        _lastPumpSpeedOutput = firstSetupSpeed;
                        _previousPumpSpeed = firstSetupSpeed;
                        AddCycleLog($"│  密度修正首次设置成功: {firstSetupSpeed:F1} RPM (耗时: {firstSetDuration.TotalMilliseconds:F0}ms)");
                        EndCycleLog($"密度修正首次98%积极起步: {firstSetupSpeed:F1} RPM (密度{currentDensity:F2})");
                    }
                    else
                    {
                        AddCycleLog($"│  密度修正首次设置失败: {firstSetupSpeed:F1} RPM");
                        EndCycleLog($"密度修正首次泵速设置失败: {firstSetupSpeed:F1} RPM");
                    }
                    AddCycleLog($"└─ 密度修正首次设置结果: {(firstSetSuccess ? "成功" : "失败")}");
                    return;
                }

                // *** 非首次设置：正常密度修正PID控制流程 ***

                // *** 智能三档等待流量稳定 ***
                AddCycleLog("┌── 步骤1: 智能三档等待流量稳定 ──");
                // 在步骤1之前，记录泵速变化
                double previousPumpSpeed = currentPumpSpeed;
                double speedChange = 0;

                if (_controlCycleCount > 1)
                {
                    speedChange = currentPumpSpeed - _previousPumpSpeed;
                    AddCycleLog($"│ 上次泵速变化: {_previousPumpSpeed:F1} → {currentPumpSpeed:F1} RPM (变化{speedChange:F1})");
                }

                // 修改智能等待调用
                var (finalFlow, stabilizeTime, readings) = await SmartWaitForStability(targetFlow, currentPumpSpeed, speedChange);
                // 记录泵速供下次使用
                _previousPumpSpeed = currentPumpSpeed;



                AddCycleLog($"│ 质量流量已稳定: {finalFlow:F2} g/min");
                AddCycleLog($"│ 实际等待: {stabilizeTime:F1}秒");
                AddCycleLog($"│ 监测次数: {readings}次");
                AddCycleLog($"└─ 智能稳定检测完成");

                double actualFlow = finalFlow;

                // 2. 计算流量偏差 - 基于质量流量
                AddCycleLog("┌── 步骤2: 计算质量流量偏差 ──");
                double massFlowError = targetFlow - actualFlow;
                double errorPercent = Math.Abs(massFlowError / targetFlow) * 100.0;

                // 同时计算体积流量信息用于显示
                double targetVolumeFlow = targetFlow / currentDensity;
                double actualVolumeFlow = actualFlow / currentDensity;
                double volumeFlowError = targetVolumeFlow - actualVolumeFlow;

                AddCycleLog($"│ 质量流量误差: {massFlowError:F2} g/min");
                AddCycleLog($"│ 体积流量误差: {volumeFlowError:F2} cm³/min");
                AddCycleLog($"│ 误差百分比: {errorPercent:F3}%");
                AddCycleLog($"│ 死区范围: ±{DEAD_ZONE_PERCENT}%");
                AddCycleLog($"│ 容差范围: ±{FLOW_TOLERANCE_PERCENT}%");
                AddCycleLog($"│ 超调保护: ±{UPPER_PROTECTION_PERCENT}%");

                // 3. 防超调的死区控制
                if (errorPercent <= DEAD_ZONE_PERCENT)
                {
                    _stableCount++;
                    AddCycleLog($"│  判断: 在死区内 ({errorPercent:F3}% ≤ {DEAD_ZONE_PERCENT}%)");
                    AddCycleLog($"│ 稳定计数: {_stableCount}");

                    var oldIntegral = _integralError;
                    _integralError *= 0.92;
                    AddCycleLog($"│ 积分误差衰减: {oldIntegral:F3} → {_integralError:F3}");
                    AddCycleLog($"│ 🎯 密度修正防超调死区稳定 - 保持当前泵速");

                    EndCycleLog($"密度修正防超调死区稳定 (偏差{errorPercent:F3}% ≤ {DEAD_ZONE_PERCENT}%, 密度{currentDensity:F2}) - 连续{_stableCount}次");
                    return;
                }

                // 4. 防超调的容差控制
                if (errorPercent <= FLOW_TOLERANCE_PERCENT)
                {
                    _stableCount++;
                    AddCycleLog($"│  判断: 在容差内 ({errorPercent:F3}% ≤ {FLOW_TOLERANCE_PERCENT}%)");
                    AddCycleLog($"│ 稳定计数: {_stableCount}/{STABLE_CYCLES_REQUIRED}");

                    if (_stableCount >= STABLE_CYCLES_REQUIRED)
                    {
                        AddCycleLog($"│ 🎯 达到连续稳定要求 ({_stableCount} ≥ {STABLE_CYCLES_REQUIRED})");
                        _integralError = 0;

                        // ★ 新增：标记稳定并通知
                        if (!_isStable)
                        {
                            _isStable = true;
                            RaiseControlStateChanged("IsStable", true);
                            AddCycleLog($"│ ✅ 流量稳定状态：true");
                        }

                        EndCycleLog($"...");
                        return;
                    }
                    else
                    {
                        AddCycleLog($"│  需要连续{STABLE_CYCLES_REQUIRED}次稳定，当前{_stableCount}次");
                    }
                }
                else
                {
                    AddCycleLog($"│  判断: 超出容差 ({errorPercent:F3}% > {FLOW_TOLERANCE_PERCENT}%)");
                    _stableCount = 0;

                    // ★ 新增：失稳通知
                    if (_isStable)
                    {
                        _isStable = false;
                        RaiseControlStateChanged("IsStable", false);
                        AddCycleLog($"│ ⚠ 流量失稳：true → false");
                    }
                    AddCycleLog($"│ 稳定计数已重置: {_stableCount}");
                }

                AddCycleLog($"└─ 需要调整泵速");

                // 5. 获取当前泵速
                 currentPumpSpeed = 0;
                try
                {
                    var pumpReadStart = DateTime.Now;
                    currentPumpSpeed = await ConnectionManager.ReadAsync<double>(pumpDeviceId, "P0101泵速");
                    var pumpReadDuration = DateTime.Now - pumpReadStart;

                    // 合理性检查
                    if (isTargetChanged && Math.Abs(currentPumpSpeed - _lastPumpSpeedOutput) > 10)
                    {
                        AddCycleLog($"│  检测到泵速异常变化: 上次输出{_lastPumpSpeedOutput:F1} → 当前读取{currentPumpSpeed:F1} RPM");
                        AddCycleLog($"│ 目标流量刚切换，使用上次输出值作为起点");
                        currentPumpSpeed = _lastPumpSpeedOutput;
                    }

                    AddCycleLog($"│ 读取成功: {currentPumpSpeed:F1} RPM (耗时: {pumpReadDuration.TotalMilliseconds:F0}ms)");
                }
                catch (Exception ex)
                {
                    currentPumpSpeed = _lastPumpSpeedOutput;
                    AddCycleLog($"│ 读取失败，使用上次输出值: {currentPumpSpeed:F1} RPM");
                    AddCycleLog($"│ 错误: {ex.Message}");
                }
                AddCycleLog($"└─ 当前泵速: {currentPumpSpeed:F1} RPM");
                // 6. 密度修正防超调PID控制计算
                AddCycleLog("┌── 步骤4: 密度修正防超调PID控制计算 ──");
                double newPumpSpeed = CalculateAntiOvershootPumpSpeed(targetFlow, actualFlow, massFlowError, currentPumpSpeed, currentDensity);
                AddCycleLog($"└─ 计算的新泵速: {newPumpSpeed:F1} RPM");

                // 7. 设置新泵速
                AddCycleLog("┌── 步骤5: 设置新泵速 ──");
                var pumpSetStart = DateTime.Now;
                bool adjustSuccess = await SetPumpSpeedAsync(pumpDeviceId, newPumpSpeed);
                var pumpSetDuration = DateTime.Now - pumpSetStart;

                if (adjustSuccess)
                {
                    _lastPumpSpeedOutput = newPumpSpeed;
                    _previousPumpSpeed = newPumpSpeed;
                    //  可选：通知UI泵速变化（如果流量计显示泵速）
                     RaiseControlStateChanged("PumpSpeed", newPumpSpeed);
                    AddCycleLog($"│  设置成功: {newPumpSpeed:F1} RPM (耗时: {pumpSetDuration.TotalMilliseconds:F0}ms)");
                    AddCycleLog($"│ 泵速变化: {currentPumpSpeed:F1} → {newPumpSpeed:F1} RPM");

                    var summary = $"密度修正防超调调整: {currentPumpSpeed:F1}→{newPumpSpeed:F1}RPM, 流量偏差: {errorPercent:F3}%, 密度: {currentDensity:F2}";
                    if (_isOvershootMode)
                    {
                        summary += " [超调保护模式]";
                    }
                    EndCycleLog(summary);
                }
                else
                {
                    AddCycleLog($"│  设置失败: {newPumpSpeed:F1} RPM");
                    EndCycleLog($"密度修正泵速设置失败: {newPumpSpeed:F1} RPM");
                }
                AddCycleLog($"└─ 设置结果: {(adjustSuccess ? "成功" : "失败")}");
            }
            catch (Exception ex)
            {
                AddCycleLog($"[ERROR] 密度修正防超调控制异常: {ex.Message}");
                EndCycleLog($"异常: {ex.Message}");
                throw;
            }
        }
        /// <summary>
        /// 重置控制状态 - 用于目标流量切换时
        /// </summary>
        private void ResetControlState()
        {
            _previousError = 0;
            _integralError = 0;
            _stableCount = 0;
            _lastPumpSpeedOutput = 0;
            _previousPumpSpeed = 0;
            _isOvershootMode = false;
            _overshootProtectionCount = 0;

            AddCycleLog($"│ 重置项目: PID积分={_integralError}, 稳定计数={_stableCount}, 超调保护={_isOvershootMode}");
        }

        #endregion

        #region 设备初始化和其他方法

        public FlowMeter_ModbusTCP()
        {
            _logger = LogManager.GetLogger<FlowMeter_ModbusTCP>();

            Name = "流量计";
            Manufacturer = "ModbusTCP流量计";
            Category = DeviceCategories.Flowmeter;
            ComId = "FT0101";

            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();

            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            //ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/D07-19BMassFlowMeter.png";
            ImageLocation = "CUSTOM_CONTROL:MassFlowMeterControl";
            Parameters.Variables.Add(new StringParameter("DeviceId", "FT0101")
            {
                Options = new ObservableCollection<string>(new List<string>() { "FT0101", "FT0102", "FT0103" }),
                HelpText = "ModbusTCP流量计设备ID"
            });

            Parameters.Variables.Add(new StringParameter("PumpDeviceId", "P0101")
            {
                Options = new ObservableCollection<string>(new List<string>() { "P0101", "P0111", "P0121" }),
                HelpText = "关联的蠕动泵设备ID"
            });

            InitializeDetailedLogging();
            InitializeCommands();
        }

        /// <summary>
        /// 设置泵速
        /// </summary>
        private async Task<bool> SetPumpSpeedAsync(string pumpDeviceId, double speed)
        {
            try
            {
                var originalSpeed = speed;
                speed = Math.Max(0, Math.Min(MAX_PUMP_SPEED, speed));

                if (Math.Abs(originalSpeed - speed) > 0.01)
                {
                    AddCycleLog($"│   泵速限制: {originalSpeed:F1} → {speed:F1} RPM");
                }

                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null && !IsSimulationMode)
                {
                    AddCycleLog($"│   设置基准模式...");
                    await ConnectionManager.WriteAsync(pumpDeviceId, "P0101泵基准模式", true);

                    AddCycleLog($"│   写入泵速: {speed:F1} RPM");
                    var result = await ConnectionManager.WriteAsync<float>(pumpDeviceId, "P0101泵基准泵速", (float)speed);

                    AddCycleLog($"│   ModbusTCP写入结果: {result}");
                    return result;
                }
                else if (IsSimulationMode)
                {
                    AddCycleLog($"│   [模拟] 设置泵速: {speed:F1} RPM");

                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(500);
                        lock (_stateLock)
                        {
                            double currentDensity;
                            lock (_densityLock)
                            {
                                currentDensity = _currentDensity;
                            }

                            // 模拟更真实的系统响应，包含密度影响、惯性和延迟
                            var targetVolumeResponse = speed * 0.98; // 体积流量响应
                            var targetMassResponse = targetVolumeResponse * currentDensity; // 质量流量响应
                            var oldCurrentFlow = _currentFlow;

                            // 降低响应速度，模拟实际系统的惯性
                            double responseRate = 0.25;
                            _currentFlow = _currentFlow * (1 - responseRate) + targetMassResponse * responseRate;
                            _currentFlow = Math.Max(0, Math.Min(MAX_FLOW_RATE, _currentFlow));

                            WriteDetailedLog($"[密度修正防超调模拟响应] 泵速={speed:F1}RPM → 体积响应={targetVolumeResponse:F2}cm³/min → 密度={currentDensity:F2}g/cm³ → 质量响应={targetMassResponse:F2}g/min → 流量更新: {oldCurrentFlow:F2} → {_currentFlow:F2}g/min");
                        }
                    });

                    return true;
                }
                else
                {
                    AddCycleLog($"│   错误: 设备未配置为ModbusTCP模式且非模拟模式");
                    return false;
                }
            }
            catch (Exception ex)
            {
                AddCycleLog($"│   [ERROR] 设置泵速异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 读取当前流量
        /// </summary>
        private async Task<double> ReadCurrentFlowAsync()
        {
            try
            {
                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null && !IsSimulationMode)
                {
                    return await ConnectionManager.ReadAsync<double>(ComId, "_FT0101_g_m");
                }
                else if (IsSimulationMode)
                {
                    lock (_stateLock)
                    {
                        double currentDensity;
                        lock (_densityLock)
                        {
                            currentDensity = _currentDensity;
                        }

                        // 模拟更真实的流量响应，增加防超调特性和密度影响
                        var targetVolumeResponse = _lastPumpSpeedOutput * 0.95;
                        var targetMassResponse = targetVolumeResponse * currentDensity;
                        var random = new Random();

                        // 模拟系统响应延迟和噪声，增加惯性防止快速变化
                        double responseRate = 0.12; // 降低响应速度，防止模拟超调
                        _currentFlow = _currentFlow * (1 - responseRate) + targetMassResponse * responseRate;
                        _currentFlow += (random.NextDouble() - 0.5) * 0.05; // 减小噪声
                        _currentFlow = Math.Max(0, Math.Min(MAX_FLOW_RATE, _currentFlow));

                        return _currentFlow;
                    }
                }
                else
                {
                    throw new InvalidOperationException("设备未配置为有效的通信模式");
                }
            }
            catch (Exception ex)
            {
                AddCycleLog($"│ 读取流量失败: {ex.Message}");
                throw;
            }
        }

        #endregion

        #region 详细日志记录系统

        /// <summary>
        /// 初始化详细日志文件
        /// </summary>
        private void InitializeDetailedLogging()
        {
            try
            {
                var logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ControlLogs");
                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _logFilePath = Path.Combine(logDir, $"FlowControl_DensityCorrection_AntiOvershoot_{timestamp}.log");

                WriteDetailedLog("=".PadRight(100, '='));
                WriteDetailedLog($"密度修正防超调智能流量控制详细日志启动");
                WriteDetailedLog($"设备: {Name} ({ComId})");
                WriteDetailedLog($"日志文件: {_logFilePath}");
                WriteDetailedLog($"启动时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                WriteDetailedLog($"智能稳定检测: 三档动态等待 (低速{LOW_SPEED_WAIT_MS / 1000}s, 中速{MID_SPEED_WAIT_MS / 1000}s, 高速{HIGH_SPEED_WAIT_MS / 1000}s)");
                WriteDetailedLog($"控制周期间隔: {CONTROL_INTERVAL_MS}ms");
                WriteDetailedLog($"防超调控制: 容差±{FLOW_TOLERANCE_PERCENT}%, 死区±{DEAD_ZONE_PERCENT}%, 保护±{UPPER_PROTECTION_PERCENT}%");
                WriteDetailedLog($"优化PID: Kp={KP}, Ki={KI}, Kd={KD}");
                WriteDetailedLog($"密度范围: {MIN_DENSITY}-{MAX_DENSITY} g/cm³, 默认: {DEFAULT_WATER_DENSITY} g/cm³");
                WriteDetailedLog("=".PadRight(100, '='));
                WriteDetailedLog("");
            }
            catch (Exception ex)
            {
                ErrorLog($"初始化详细日志失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 写入详细日志
        /// </summary>
        private void WriteDetailedLog(string message)
        {
            if (string.IsNullOrEmpty(_logFilePath)) return;

            try
            {
                lock (_logFileLock)
                {
                    var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
                    File.AppendAllText(_logFilePath, logEntry + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                ErrorLog($"写入详细日志失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 开始新的控制周期日志
        /// </summary>
        private void StartCycleLog()
        {
            _controlCycleCount++;
            _currentCycleLog.Clear();
            _currentCycleLog.AppendLine($"┌─ 密度修正防超调控制周期 #{_controlCycleCount} 开始 ─ {DateTime.Now:HH:mm:ss.fff} ────");
        }

        /// <summary>
        /// 添加周期内日志
        /// </summary>
        private void AddCycleLog(string message)
        {
            _currentCycleLog.AppendLine($"│ {message}");
        }

        /// <summary>
        /// 结束控制周期日志
        /// </summary>
        private void EndCycleLog(string result)
        {
            _currentCycleLog.AppendLine($"│ 周期结果: {result}");
            _currentCycleLog.AppendLine($"└─ 密度修正防超调控制周期 #{_controlCycleCount} 结束 ─ {DateTime.Now:HH:mm:ss.fff} ────");
            _currentCycleLog.AppendLine("");

            WriteDetailedLog(_currentCycleLog.ToString());
        }

        /// <summary>
        /// 记录控制状态变化
        /// </summary>
        private void LogControlStateChange(string action, object details = null)
        {
            var message = $"[控制状态] {action}";
            if (details != null)
            {
                message += $" - {details}";
            }
            WriteDetailedLog(message);
            InfoLog(message);
        }

        #endregion

        #region 命令初始化

        private void InitializeCommands()
        {
            // 1. 获取基准流量
            Commands.Add(new DeviceCommand
            {
                Name = "获取基准流量",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/flowrate.png",
                HelpText = "读取ModbusTCP地址40030获取基准质量流量",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
        {
            new NumberParameter("BaseFlow", 0, MAX_FLOW_RATE, 0, "基准质量流量",        true, UnitMassFlow),
            new NumberParameter("CurrentDensity", MIN_DENSITY, MAX_DENSITY, 0, "当前液体密度", true, UnitDensity)
        }
                },
                Action = WrapAction("获取基准流量", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double baseFlow = 0;
                    double currentDensity;

                    lock (_densityLock)
                    {
                        currentDensity = _currentDensity;
                    }

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null && !IsSimulationMode)
                        {
                            baseFlow = await ConnectionManager.ReadAsync<double>(ComId, "_FT0101_g_m");
                            InfoLog($"从ModbusTCP地址40030获取基准质量流量: {baseFlow} g/min (密度: {currentDensity} g/cm³)");
                        }
                        else if (IsSimulationMode)
                        {
                            lock (_stateLock)
                            {
                                double targetFlow = _currentFlow;

                                // 初始化模拟流量
                                if (_simulatedFlow == 0)
                                {
                                    _simulatedFlow = targetFlow;
                                }

                                // 计算时间间隔
                                var now = DateTime.Now;
                                double deltaTime = (now - _lastSimulationTime).TotalSeconds;
                                _lastSimulationTime = now;

                                // 趋势衰减（逐渐回归目标值）
                                double returnRate = 0.1; // 10% 回归速率
                                _flowTrend += (targetFlow - _simulatedFlow) * returnRate * deltaTime;

                                // 添加随机扰动
                                double randomChange = (Random.Shared.NextDouble() - 0.5) * 2 * targetFlow * 0.01; // ±1%
                                _flowTrend += randomChange;

                                // 限制趋势变化速率
                                double maxTrendChange = targetFlow * 0.05; // 最大 5%/秒
                                _flowTrend = Math.Clamp(_flowTrend, -maxTrendChange, maxTrendChange);

                                // 更新模拟流量
                                _simulatedFlow += _flowTrend * deltaTime;

                                // 限制在合理范围
                                _simulatedFlow = Math.Clamp(_simulatedFlow, targetFlow * 0.7, targetFlow * 1.3);
                                _simulatedFlow = Math.Max(0, _simulatedFlow);

                                baseFlow = _simulatedFlow;
                            }
                            InfoLog($"模拟模式获取基准质量流量: {baseFlow} g/min (密度: {currentDensity} g/cm³)");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        //  通知UI：当前流量已更新
                        RaiseControlStateChanged("CurrentFlow", baseFlow);
                    }
                    catch (Exception ex)
                    { //  通知UI：命令执行异常
                        RaiseControlStateChanged("CommandState_获取基准流量", CommandExecutionState.Error,
                            new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });
                        ErrorLog($"获取基准流量失败: {ex.Message}");
                        throw;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
        {
            new NumberParameter("BaseFlow", 0, MAX_FLOW_RATE, Math.Round(baseFlow, 2), "基准质量流量",            true, UnitMassFlow),
            new NumberParameter("CurrentDensity", MIN_DENSITY, MAX_DENSITY, Math.Round(currentDensity, 2), "当前液体密度", true, UnitDensity)
        }
                    };
                })
            });

            // 2. 设置基准流量 - 密度修正版本
            Commands.Add(new DeviceCommand
            {
                Name = "设置基准流量",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/speed_set.png",
                HelpText = "设置目标质量流量和液体密度，启动密度修正的防超调智能PID控制",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
        {
            new NumberParameter("TargetFlow", 0, MAX_FLOW_RATE, 50.0, "目标质量流量(g/min)"),
            new NumberParameter("Density", MIN_DENSITY, MAX_DENSITY, DEFAULT_WATER_DENSITY, "液体密度(g/cm³)")
            {
                HelpText = "输入液体密度：水=1.0，导热油通常0.8-0.9，其他液体请查阅资料"
            },
            new BooleanParameter("EnableAutoControl", true, "启用密度修正防超调智能控制")
        }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
{
    new BooleanParameter("Success", false, "设置成功") { IsVisibleInVariableManager = true },
    new NumberParameter("SetFlow", 0, MAX_FLOW_RATE, 0, "设置的质量流量",          true, UnitMassFlow),
    new NumberParameter("SetDensity", MIN_DENSITY, MAX_DENSITY, 0, "设置的液体密度", true, UnitDensity),
    new NumberParameter("RequiredVolumeFlow", 0, MAX_FLOW_RATE * 2, 0, "所需体积流量", true, UnitVolumeFlow),
    new NumberParameter("CalculatedSpeed", 0, MAX_PUMP_SPEED, 0, "计算的泵速",       true, UnitPumpSpeed),
    new BooleanParameter("AutoControlStarted", false, "密度修正防超调控制已启动") { IsVisibleInVariableManager = true }
}
                },
                Action = WrapAction("设置基准流量", async delegate (DeviceParameters parameters)
                {
                    //  1. 通知UI：开始执行
                    RaiseControlStateChanged("CommandState_设置基准流量", CommandExecutionState.Running);

                    ComId = Parameters.GetValue<string>("DeviceId");
                    double targetFlow = parameters.GetValue<double>("TargetFlow");
                    double density = parameters.GetValue<double>("Density");
                    bool enableAutoControl = parameters.GetValue<bool>("EnableAutoControl");
                    bool success = false;
                    double calculatedSpeed = 0;
                    double requiredVolumeFlow = 0;
                    bool autoControlStarted = false;

                    // 参数验证和限制
                    targetFlow = Math.Max(0, Math.Min(MAX_FLOW_RATE, targetFlow));
                    density = Math.Max(MIN_DENSITY, Math.Min(MAX_DENSITY, density));

                    try
                    {
                        // 设置目标流量和密度
                        lock (_stateLock)
                        {
                            _targetFlow = targetFlow;
                        }

                        lock (_densityLock)
                        {
                            _currentDensity = density;
                        }

                        //  2. 通知UI：参数已更新
                        RaiseControlStateChanged("TargetFlow", targetFlow);
                        RaiseControlStateChanged("Density", density);

                        // 计算所需体积流量和泵速
                        if (targetFlow > 0)
                        {
                            requiredVolumeFlow = targetFlow / density;
                            calculatedSpeed = CalculateRequiredPumpSpeed(targetFlow, density);

                            InfoLog($"密度修正计算: 目标质量流量={targetFlow}g/min, 密度={density}g/cm³ → 所需体积流量={requiredVolumeFlow:F2}cm³/min → 初始泵速={calculatedSpeed}RPM");

                            var pumpDeviceId = Parameters.GetValue<string>("PumpDeviceId");
                            //先启动泵
                            if (!await ConnectionManager.ReadAsync<bool>(pumpDeviceId, "P0101启动"))
                            {
                                await ConnectionManager.WriteAsync(pumpDeviceId, "P0101启动", true);
                            } 
                            var pumpSetSuccess = await SetPumpSpeedAsync(pumpDeviceId, calculatedSpeed);

                            if (pumpSetSuccess)
                            {
                                InfoLog($"密度修正初始泵速设置成功: {calculatedSpeed} RPM");
                                success = true;

                                //  3. 通知UI：流动状态已启动
                                RaiseControlStateChanged("IsFlowing", true);

                                if (enableAutoControl)
                                {
                                    StartAutoControl();
                                    autoControlStarted = true;
                                    InfoLog($"密度修正防超调智能PID控制已启动 - 目标质量流量: {targetFlow} g/min, 密度: {density} g/cm³, 三档动态等待策略");
                                }

                                //  4. 通知UI：命令执行成功
                                RaiseControlStateChanged("CommandState_设置基准流量", CommandExecutionState.Succeeded);
                            }
                            else
                            {
                                WarnLog($"密度修正初始泵速设置失败: {calculatedSpeed} RPM");

                                //  5. 通知UI：命令执行失败
                                RaiseControlStateChanged("CommandState_设置基准流量", CommandExecutionState.Failed);
                            }

                            //if (success)
                            //{
                            //    AddActiveState("FlowControlActive", "设置基准流量", "停止流量控制", stateData: new { targetFlow, density });
                            //}
                        }
                        else
                        {
                            // 目标流量为0，停止控制
                            StopAutoControl();
                            //RemoveActiveState("FlowControlActive");
                            success = true;

                            //  通知UI：流动已停止
                            RaiseControlStateChanged("IsFlowing", false);
                            RaiseControlStateChanged("CommandState_设置基准流量", CommandExecutionState.Succeeded);

                            InfoLog("目标流量设置为0，密度修正流量控制已停止");
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"设置密度修正基准流量失败: {ex.Message}");

                        //  通知UI：命令执行异常
                        RaiseControlStateChanged("CommandState_设置基准流量", CommandExecutionState.Error,
                            new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });

                        throw;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
{
    new BooleanParameter("Success", success, "设置成功") { IsVisibleInVariableManager = true },
    new NumberParameter("SetFlow", 0, MAX_FLOW_RATE, targetFlow, "设置的质量流量",            true, UnitMassFlow),
    new NumberParameter("SetDensity", MIN_DENSITY, MAX_DENSITY, density, "设置的液体密度",     true, UnitDensity),
    new NumberParameter("RequiredVolumeFlow", 0, MAX_FLOW_RATE * 2, Math.Round(requiredVolumeFlow, 2), "所需体积流量", true, UnitVolumeFlow),
    new NumberParameter("CalculatedSpeed", 0, MAX_PUMP_SPEED, Math.Round(calculatedSpeed, 1), "计算的泵速", true, UnitPumpSpeed),
    new BooleanParameter("AutoControlStarted", autoControlStarted, "密度修正防超调控制已启动") { IsVisibleInVariableManager = true }
}
                    };
                })
            });

            // 3. 停止流量控制
            Commands.Add(new DeviceCommand
            {
                Name = "停止流量控制",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/off.png",
                HelpText = "停止密度修正防超调智能流量控制并设置泵速为0",
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
                    //  1. 通知UI：开始执行
                    RaiseControlStateChanged("CommandState_停止流量控制", CommandExecutionState.Running);

                    ComId = Parameters.GetValue<string>("DeviceId");
                    bool success = false;

                    try
                    {
                        StopAutoControl();
                        InfoLog("密度修正防超调智能自动控制已停止");

                        var pumpDeviceId = Parameters.GetValue<string>("PumpDeviceId");
                        success = await SetPumpSpeedAsync(pumpDeviceId, 0);
                        InfoLog($"泵速已设置为0 RPM，结果: {success}");

                        lock (_stateLock)
                        {
                            _targetFlow = 0;
                            _currentFlow = 0;
                        }

                        if (success)
                        {
                            RemoveActiveState("FlowControlActive");

                            //  2. 通知UI：流动已停止
                            RaiseControlStateChanged("IsFlowing", false);
                            RaiseControlStateChanged("TargetFlow", 0.0);
                            RaiseControlStateChanged("CurrentFlow", 0.0);

                            //  3. 通知UI：命令执行成功
                            RaiseControlStateChanged("CommandState_停止流量控制", CommandExecutionState.Succeeded);
                        }
                        else
                        {
                            //  通知UI：命令执行失败
                            RaiseControlStateChanged("CommandState_停止流量控制", CommandExecutionState.Failed);
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"停止密度修正流量控制失败: {ex.Message}");

                        //  通知UI：命令执行异常
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

            // 4. 获取控制状态 - 包含密度信息
            Commands.Add(new DeviceCommand
            {
                Name = "获取控制状态",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/status.png",
                HelpText = "获取密度修正防超调智能流量控制系统当前状态",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
        {
            new BooleanParameter("AutoControlEnabled", false, "密度修正防超调控制启用") { IsVisibleInVariableManager = true },
            new NumberParameter("TargetFlow", 0, MAX_FLOW_RATE, 0, "目标质量流量",       true, UnitMassFlow),
            new NumberParameter("CurrentFlow", 0, MAX_FLOW_RATE, 0, "当前质量流量",      true, UnitMassFlow),
            new NumberParameter("FlowDeviation", -MAX_FLOW_RATE, MAX_FLOW_RATE, 0, "质量流量偏差", true, UnitMassFlow),
            new NumberParameter("DeviationPercent", -100, 100, 0, "偏差百分比",          true, UnitPercent),
            new NumberParameter("CurrentDensity", MIN_DENSITY, MAX_DENSITY, 0, "当前液体密度", true, UnitDensity),
            new NumberParameter("TargetVolumeFlow", 0, MAX_FLOW_RATE * 2, 0, "目标体积流量",  true, UnitVolumeFlow),
            new NumberParameter("CurrentVolumeFlow", 0, MAX_FLOW_RATE * 2, 0, "当前体积流量", true, UnitVolumeFlow),
            new NumberParameter("CurrentPumpSpeed", 0, MAX_PUMP_SPEED, 0, "当前泵速",     true, UnitPumpSpeed),
            new BooleanParameter("OvershootMode", false, "超调保护模式") { IsVisibleInVariableManager = true },
            new StringParameter("ControlStatus", "", "控制状态") { IsVisibleInVariableManager = true }
        }
                },

                Action = WrapAction("获取控制状态", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    bool autoControlEnabled = false;
                    double targetFlow = 0;
                    double currentFlow = 0;
                    double flowDeviation = 0;
                    double deviationPercent = 0;
                    double currentDensity = 0;
                    double targetVolumeFlow = 0;
                    double currentVolumeFlow = 0;
                    double currentPumpSpeed = 0;
                    bool overshootMode = false;
                    string controlStatus = "";

                    try
                    {
                        lock (_controlLock)
                        {
                            autoControlEnabled = _autoControlEnabled;
                        }

                        lock (_stateLock)
                        {
                            targetFlow = _targetFlow;
                            currentFlow = _currentFlow;
                        }

                        lock (_densityLock)
                        {
                            currentDensity = _currentDensity;
                        }

                        overshootMode = _isOvershootMode;

                        // 计算体积流量
                        if (currentDensity > 0)
                        {
                            targetVolumeFlow = targetFlow / currentDensity;
                            currentVolumeFlow = currentFlow / currentDensity;
                        }

                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null && !IsSimulationMode)
                        {
                            try
                            {
                                currentFlow = await ConnectionManager.ReadAsync<double>(ComId, "_FT0101_g_m");
                                lock (_stateLock)
                                {
                                    _currentFlow = currentFlow;
                                }

                                // 重新计算体积流量
                                if (currentDensity > 0)
                                {
                                    currentVolumeFlow = currentFlow / currentDensity;
                                }

                                var pumpDeviceId = Parameters.GetValue<string>("PumpDeviceId");
                                currentPumpSpeed = await ConnectionManager.ReadAsync<double>(pumpDeviceId, "P0101泵速");
                            }
                            catch (Exception ex)
                            {
                                WarnLog($"读取设备状态失败: {ex.Message}");
                            }
                        }
                        else if (IsSimulationMode)
                        {
                            currentPumpSpeed = _lastPumpSpeedOutput;
                        }

                        flowDeviation = currentFlow - targetFlow;

                        if (targetFlow > 0)
                        {
                            deviationPercent = (flowDeviation / targetFlow) * 100.0;
                        }

                        var (waitTime, speedLevel) = GetWaitTimeByPumpSpeed(currentPumpSpeed);
                        controlStatus = autoControlEnabled ?
                            $"密度修正PID控制中 (目标:{targetFlow:F1}g/min, 密度:{currentDensity:F2}g/cm³, 偏差:{deviationPercent:F3}%, {speedLevel}档{waitTime / 1000}s等待)" :
                            "手动模式";

                        if (overshootMode)
                        {
                            controlStatus += " [超调保护激活]";
                        }

                        InfoLog($"密度修正防超调流量控制状态: 自动={autoControlEnabled}, 目标={targetFlow:F2}g/min, 实际={currentFlow:F2}g/min, " +
                               $"偏差={deviationPercent:F3}%, 密度={currentDensity:F2}g/cm³, 体积流量={currentVolumeFlow:F2}cm³/min, 泵速={currentPumpSpeed:F1}RPM({speedLevel}), 超调保护={overshootMode}");
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"获取密度修正控制状态失败: {ex.Message}");
                        controlStatus = $"状态获取失败: {ex.Message}";
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
        {
            new BooleanParameter("AutoControlEnabled", autoControlEnabled, "密度修正防超调控制启用") { IsVisibleInVariableManager = true },
            new NumberParameter("TargetFlow", 0, MAX_FLOW_RATE, Math.Round(targetFlow, 3), "目标质量流量",          true, UnitMassFlow),
            new NumberParameter("CurrentFlow", 0, MAX_FLOW_RATE, Math.Round(currentFlow, 3), "当前质量流量",         true, UnitMassFlow),
            new NumberParameter("FlowDeviation", -MAX_FLOW_RATE, MAX_FLOW_RATE, Math.Round(flowDeviation, 3), "质量流量偏差", true, UnitMassFlow),
            new NumberParameter("DeviationPercent", -100, 100, Math.Round(deviationPercent, 3), "偏差百分比",          true, UnitPercent),
            new NumberParameter("CurrentDensity", MIN_DENSITY, MAX_DENSITY, Math.Round(currentDensity, 2), "当前液体密度", true, UnitDensity),
            new NumberParameter("TargetVolumeFlow", 0, MAX_FLOW_RATE * 2, Math.Round(targetVolumeFlow, 2), "目标体积流量",  true, UnitVolumeFlow),
            new NumberParameter("CurrentVolumeFlow", 0, MAX_FLOW_RATE * 2, Math.Round(currentVolumeFlow, 2), "当前体积流量", true, UnitVolumeFlow),
            new NumberParameter("CurrentPumpSpeed", 0, MAX_PUMP_SPEED, Math.Round(currentPumpSpeed, 1), "当前泵速",     true, UnitPumpSpeed),
            new BooleanParameter("OvershootMode", overshootMode, "超调保护模式") { IsVisibleInVariableManager = true },
            new StringParameter("ControlStatus", controlStatus, "控制状态") { IsVisibleInVariableManager = true }
        }
                    };

                })
            });

            // 5. 设置液体密度命令
            Commands.Add(new DeviceCommand
            {
                Name = "设置液体密度",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/density.png",
                HelpText = "单独设置液体密度参数，用于不同液体的切换",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
        {
            new NumberParameter("Density", MIN_DENSITY, MAX_DENSITY, DEFAULT_WATER_DENSITY, "液体密度(g/cm³)")
            {
                HelpText = "常见液体密度：水=1.0，乙醇=0.789，导热油=0.8-0.9，甘油=1.26"
            }
        }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
{
    new BooleanParameter("Success", false, "设置成功") { IsVisibleInVariableManager = true },
    new NumberParameter("SetDensity", MIN_DENSITY, MAX_DENSITY, 0, "设置的密度",      true, UnitDensity),
    new NumberParameter("PreviousDensity", MIN_DENSITY, MAX_DENSITY, 0, "之前的密度", true, UnitDensity)
}
                },
                Action = WrapAction("设置液体密度", async delegate (DeviceParameters parameters)
                {
                    //  1. 通知UI：开始执行
                    RaiseControlStateChanged("CommandState_设置液体密度", CommandExecutionState.Running);

                    double newDensity = parameters.GetValue<double>("Density");
                    double previousDensity;
                    bool success = false;

                    // 参数验证和限制
                    newDensity = Math.Max(MIN_DENSITY, Math.Min(MAX_DENSITY, newDensity));

                    try
                    {
                        lock (_densityLock)
                        {
                            previousDensity = _currentDensity;
                            _currentDensity = newDensity;
                        }

                        success = true;
                        InfoLog($"液体密度已更新: {previousDensity:F2} → {newDensity:F2} g/cm³");

                        //  2. 通知UI：密度已更新
                        RaiseControlStateChanged("Density", newDensity);

                        // 如果控制正在运行，记录密度变化对控制的影响
                        bool isControlActive;
                        lock (_controlLock)
                        {
                            isControlActive = _autoControlEnabled;
                        }

                        if (isControlActive)
                        {
                            double targetFlow;
                            lock (_stateLock)
                            {
                                targetFlow = _targetFlow;
                            }

                            if (targetFlow > 0)
                            {
                                double oldVolumeFlow = targetFlow / previousDensity;
                                double newVolumeFlow = targetFlow / newDensity;
                                InfoLog($"密度变化影响分析: 目标质量流量{targetFlow}g/min → 体积流量需求 {oldVolumeFlow:F2}→{newVolumeFlow:F2}cm³/min");
                                WarnLog("密度已变化，建议观察控制系统响应，必要时重新设置基准流量");
                            }
                        }

                        //  3. 通知UI：命令执行成功
                        RaiseControlStateChanged("CommandState_设置液体密度", CommandExecutionState.Succeeded);
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"设置液体密度失败: {ex.Message}");

                        lock (_densityLock)
                        {
                            previousDensity = _currentDensity;
                        }

                        //  通知UI：命令执行异常
                        RaiseControlStateChanged("CommandState_设置液体密度", CommandExecutionState.Error,
                            new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });

                        throw;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
{
    new BooleanParameter("Success", success, "设置成功") { IsVisibleInVariableManager = true },
    new NumberParameter("SetDensity", MIN_DENSITY, MAX_DENSITY, newDensity, "设置的密度",        true, UnitDensity),
    new NumberParameter("PreviousDensity", MIN_DENSITY, MAX_DENSITY, previousDensity, "之前的密度", true, UnitDensity)
}
                    };
                })
            });
        }

        #endregion

        #region IControllableDevice Implementation

        public List<DeviceActiveState> ActiveStates
        {
            get
            {
                lock (_statesLock)
                {
                    return new List<DeviceActiveState>(_activeStates);
                }
            }
        }

        public event EventHandler<DeviceStateChangedEventArgs> StateChanged;

        public virtual async Task<bool> PauseAllActiveStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_activeStates.Any())
                {
                    InfoLog("密度修正ModbusTCP流量计没有活跃状态需要暂停");
                    return true;
                }
                InfoLog($"暂停密度修正ModbusTCP流量计的 {_activeStates.Count} 个活跃状态");
            }

            StopAutoControl();

            var pauseTasks = new List<Task<bool>>();
            DeviceActiveState[] statesToPause;

            lock (_statesLock)
            {
                statesToPause = _activeStates.ToArray();
            }

            foreach (var state in statesToPause)
            {
                pauseTasks.Add(PauseStateAsync(state));
            }

            try
            {
                var results = await Task.WhenAll(pauseTasks);
                bool allSuccess = results.All(r => r);
                InfoLog(allSuccess ? "密度修正ModbusTCP流量计所有活跃状态已暂停" : "密度修正ModbusTCP流量计部分活跃状态暂停失败");
                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("密度修正ModbusTCP流量计暂停活跃状态异常", ex);
                return false;
            }
        }

        public virtual async Task<bool> ResumeAllPausedStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_pausedStates.Any())
                {
                    InfoLog("密度修正ModbusTCP流量计没有暂停的状态需要恢复");
                    return true;
                }
                InfoLog($"恢复密度修正ModbusTCP流量计的 {_pausedStates.Count} 个暂停的状态");
            }

            var resumeTasks = new List<Task<bool>>();
            DeviceActiveState[] statesToResume;

            lock (_statesLock)
            {
                statesToResume = _pausedStates.ToArray();
            }

            foreach (var state in statesToResume)
            {
                resumeTasks.Add(ResumeStateAsync(state));
            }

            try
            {
                var results = await Task.WhenAll(resumeTasks);
                bool allSuccess = results.All(r => r);

                if (allSuccess)
                {
                    StartAutoControl();
                }

                InfoLog(allSuccess ? "密度修正ModbusTCP流量计所有暂停状态已恢复" : "密度修正ModbusTCP流量计部分暂停状态恢复失败");
                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("密度修正ModbusTCP流量计恢复暂停状态异常", ex);
                return false;
            }
        }

        public virtual async Task<bool> StopAllActiveStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_activeStates.Any() && !_pausedStates.Any())
                {
                    InfoLog("密度修正ModbusTCP流量计没有活跃或暂停的状态需要停止");
                    return true;
                }
                InfoLog($"停止密度修正ModbusTCP流量计的 {_activeStates.Count} 个活跃状态和 {_pausedStates.Count} 个暂停状态");
            }

            StopAutoControl();

            var stopTasks = new List<Task<bool>>();
            DeviceActiveState[] allStates;

            lock (_statesLock)
            {
                allStates = _activeStates.Concat(_pausedStates).ToArray();
            }

            foreach (var state in allStates)
            {
                stopTasks.Add(StopStateAsync(state));
            }

            try
            {
                var results = await Task.WhenAll(stopTasks);
                bool allSuccess = results.All(r => r);

                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                InfoLog(allSuccess ? "密度修正ModbusTCP流量计所有状态已停止" : "密度修正ModbusTCP流量计部分状态停止失败");
                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("密度修正ModbusTCP流量计停止状态异常", ex);
                return false;
            }
        }

        #endregion

        #region IFlowLifecycleAware Implementation

        public virtual async Task<bool> OnFlowStartedAsync(FlowStartContext context)
        {
            try
            {
                InfoLog($"密度修正ModbusTCP流量计流程开始: {context.FlowName}");

                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                if (!IsConnected)
                {
                    WarnLog("密度修正ModbusTCP流量计设备未连接，但流程已开始");
                }

                // 启动流量轮询
                await StartCurrentFlowPollingAsync();

                InfoLog("密度修正ModbusTCP流量计设备已准备好执行流程");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("密度修正ModbusTCP流量计流程开始处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowCompletedAsync(FlowCompletionContext context)
        {
            try
            {
                InfoLog($"密度修正ModbusTCP流量计流程完成: {context.FlowName}, 成功: {context.IsSuccessful}");
                //StopAutoControl();
                await StopCurrentFlowPollingAsync();
                await StopAllActiveStatesAsync();
                InfoLog("密度修正ModbusTCP流量计流程完成处理结束");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("密度修正ModbusTCP流量计流程完成处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowFailedAsync(FlowFailureContext context)
        {
            try
            {
                ErrorLog($"密度修正ModbusTCP流量计流程失败: {context.FlowName}, 错误: {context.ErrorMessage}");
                StopAutoControl();
                await StopCurrentFlowPollingAsync();
                await StopAllActiveStatesAsync();
                InfoLog("密度修正ModbusTCP流量计流程失败处理完成");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("密度修正ModbusTCP流量计流程失败处理异常", ex);
                return false;
            }
        }

        #endregion

        #region 状态管理内部方法

        protected void AddActiveState(string stateName, string activateCommand, string deactivateCommand,
            string pauseCommand = null, string resumeCommand = null, object stateData = null)
        {
            lock (_statesLock)
            {
                var existing = _activeStates.FirstOrDefault(s => s.StateName == stateName);
                if (existing != null)
                {
                    existing.ActivatedTime = DateTime.Now;
                    existing.StateData = stateData;
                    return;
                }

                var newState = new DeviceActiveState
                {
                    StateName = stateName,
                    ActivateCommand = activateCommand,
                    DeactivateCommand = deactivateCommand,
                    PauseCommand = pauseCommand ?? deactivateCommand,
                    ResumeCommand = resumeCommand ?? activateCommand,
                    ActivatedTime = DateTime.Now,
                    IsPaused = false,
                    StateData = stateData
                };

                _activeStates.Add(newState);
                InfoLog($"密度修正ModbusTCP流量计添加活跃状态: {stateName}");
            }

            StateChanged?.Invoke(this, new DeviceStateChangedEventArgs
            {
                StateName = stateName,
                IsActive = true,
                Timestamp = DateTime.Now
            });
        }

        protected void RemoveActiveState(string stateName)
        {
            lock (_statesLock)
            {
                var state = _activeStates.FirstOrDefault(s => s.StateName == stateName);
                if (state != null)
                {
                    _activeStates.Remove(state);
                    InfoLog($"密度修正ModbusTCP流量计移除活跃状态: {stateName}");
                }

                var pausedState = _pausedStates.FirstOrDefault(s => s.StateName == stateName);
                if (pausedState != null)
                {
                    _pausedStates.Remove(pausedState);
                    InfoLog($"密度修正ModbusTCP流量计移除暂停状态: {stateName}");
                }
            }

            StateChanged?.Invoke(this, new DeviceStateChangedEventArgs
            {
                StateName = stateName,
                IsActive = false,
                Timestamp = DateTime.Now
            });
        }

        private async Task<bool> PauseStateAsync(DeviceActiveState state)
        {
            try
            {
                InfoLog($"暂停密度修正ModbusTCP流量计状态: {state.StateName}");

                if (!string.IsNullOrEmpty(state.PauseCommand))
                {
                    await ExecuteDeviceCommandByNameAsync(state.PauseCommand);
                }

                lock (_statesLock)
                {
                    _activeStates.Remove(state);
                    state.IsPaused = true;
                    _pausedStates.Add(state);
                }

                return true;
            }
            catch (Exception ex)
            {
                ErrorLog($"密度修正ModbusTCP流量计暂停状态失败: {state.StateName}", ex);
                return false;
            }
        }

        private async Task<bool> ResumeStateAsync(DeviceActiveState state)
        {
            try
            {
                InfoLog($"恢复密度修正ModbusTCP流量计状态: {state.StateName}");

                if (!string.IsNullOrEmpty(state.ResumeCommand))
                {
                    var resumeParams = new DeviceParameters();
                    if (state.StateData != null)
                    {
                        // 处理包含密度信息的状态数据
                        if (state.StateData is double flow)
                        {
                            resumeParams.Variables.Add(new NumberParameter("TargetFlow", 0, MAX_FLOW_RATE, flow, "目标流量"));
                            double currentDensity;
                            lock (_densityLock)
                            {
                                currentDensity = _currentDensity;
                            }
                            resumeParams.Variables.Add(new NumberParameter("Density", MIN_DENSITY, MAX_DENSITY, currentDensity, "液体密度"));
                            resumeParams.Variables.Add(new BooleanParameter("EnableAutoControl", true, "启用密度修正防超调智能控制"));
                        }
                        else if (state.StateData.GetType().GetProperty("targetFlow") != null)
                        {
                            // 处理匿名对象包含targetFlow和density
                            var data = state.StateData;
                            var targetFlowProp = data.GetType().GetProperty("targetFlow");
                            var densityProp = data.GetType().GetProperty("density");

                            if (targetFlowProp != null && densityProp != null)
                            {
                                var targetFlow = (double)targetFlowProp.GetValue(data);
                                var density = (double)densityProp.GetValue(data);

                                resumeParams.Variables.Add(new NumberParameter("TargetFlow", 0, MAX_FLOW_RATE, targetFlow, "目标流量"));
                                resumeParams.Variables.Add(new NumberParameter("Density", MIN_DENSITY, MAX_DENSITY, density, "液体密度"));
                                resumeParams.Variables.Add(new BooleanParameter("EnableAutoControl", true, "启用密度修正防超调智能控制"));
                            }
                        }
                    }
                    await ExecuteDeviceCommandByNameAsync(state.ResumeCommand, resumeParams);
                }

                lock (_statesLock)
                {
                    _pausedStates.Remove(state);
                    state.IsPaused = false;
                    state.ActivatedTime = DateTime.Now;
                    _activeStates.Add(state);
                }

                return true;
            }
            catch (Exception ex)
            {
                ErrorLog($"密度修正ModbusTCP流量计恢复状态失败: {state.StateName}", ex);
                return false;
            }
        }

        private async Task<bool> StopStateAsync(DeviceActiveState state)
        {
            try
            {
                InfoLog($"停止密度修正ModbusTCP流量计状态: {state.StateName}");

                if (!string.IsNullOrEmpty(state.DeactivateCommand))
                {
                    await ExecuteDeviceCommandByNameAsync(state.DeactivateCommand);
                }

                return true;
            }
            catch (Exception ex)
            {
                ErrorLog($"密度修正ModbusTCP流量计停止状态失败: {state.StateName}", ex);
                return false;
            }
        }

        private async Task ExecuteDeviceCommandByNameAsync(string commandName, DeviceParameters parameters = null)
        {
            var command = Commands?.FirstOrDefault(c => c.Name == commandName);
            if (command?.Action != null)
            {
                var inputParams = parameters ?? new DeviceParameters();
                await Task.Run(() => command.Action(inputParams));
                InfoLog($"密度修正ModbusTCP流量计执行命令: {commandName}");
            }
            else
            {
                WarnLog($"密度修正ModbusTCP流量计未找到命令: {commandName}");
            }
        }

        #endregion

        #region 基类重写方法

        public override void Initialize()
        {
            WriteDetailedLog($"[Initialize] 开始初始化密度修正ModbusTCP流量计设备");

            try
            {
                base.Initialize();

                lock (_stateLock)
                {
                    _currentFlow = 0;
                    _targetFlow = 0;
                }

                lock (_densityLock)
                {
                    _currentDensity = DEFAULT_WATER_DENSITY;
                }

                lock (_controlLock)
                {
                    _autoControlEnabled = false;
                }

                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                _isOvershootMode = false;
                _overshootProtectionCount = 0;

                WriteDetailedLog($"[Initialize] 密度修正ModbusTCP流量计设备初始化完成");
                InfoLog("密度修正ModbusTCP流量计设备初始化完成");
            }
            catch (Exception ex)
            {
                WriteDetailedLog($"[Initialize ERROR] 密度修正ModbusTCP流量计设备初始化失败: {ex.Message}");
                ErrorLog("密度修正ModbusTCP流量计设备初始化失败", ex);
                throw;
            }
        }

        public override void Shutdown()
        {
            WriteDetailedLog($"[Shutdown] 开始关闭密度修正ModbusTCP流量计设备");

            try
            {
                StopAutoControl();

                var stopTask = StopAllActiveStatesAsync();
                stopTask.Wait(TimeSpan.FromSeconds(5));

                lock (_stateLock)
                {
                    _currentFlow = 0;
                    _targetFlow = 0;
                }

                lock (_densityLock)
                {
                    _currentDensity = DEFAULT_WATER_DENSITY;
                }

                _isOvershootMode = false;

                base.Shutdown();
                WriteDetailedLog($"[Shutdown] 密度修正ModbusTCP流量计设备关闭完成");
                InfoLog("密度修正ModbusTCP流量计设备关闭完成");
            }
            catch (Exception ex)
            {
                WriteDetailedLog($"[Shutdown ERROR] 密度修正ModbusTCP流量计设备关闭失败: {ex.Message}");
                ErrorLog("密度修正ModbusTCP流量计设备关闭失败", ex);
                throw;
            }
        }

        protected override async Task<bool> OnConnectAsync()
        {
            WriteDetailedLog($"[OnConnect] 密度修正ModbusTCP流量计连接");
            InfoLog("密度修正ModbusTCP流量计连接");
            await Task.Delay(100);
            return true;
        }

        protected override async Task<bool> OnDisconnectAsync()
        {
            WriteDetailedLog($"[OnDisconnect] 密度修正ModbusTCP流量计断开连接");
            InfoLog("密度修正ModbusTCP流量计断开连接");
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

        private Func<DeviceParameters, DeviceParameters> WrapAction(string commandName, Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return (DeviceParameters parameters) =>
            {
                WriteDetailedLog($"[Command] 开始执行命令: {commandName}");
                _logger.LogInformation($"[密度修正ModbusTCP流量计命令-开始] Device={Name} Command={commandName}");

                var sw = Stopwatch.StartNew();
                try
                {
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();

                    WriteDetailedLog($"[Command] 命令执行完成: {commandName} (耗时: {sw.ElapsedMilliseconds}ms)");
                    _logger.LogInformation($"[密度修正ModbusTCP流量计命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    WriteDetailedLog($"[Command ERROR] 命令执行异常: {commandName} (耗时: {sw.ElapsedMilliseconds}ms) - {ex.Message}");
                    _logger.LogError(ex, $"[密度修正ModbusTCP流量计命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        public void Dispose()
        {
            try
            {
                StopAutoControl();
                Shutdown();
                WriteDetailedLog($"[Dispose] 资源已释放");
                _logger.LogInformation("[{DeviceName}] 资源已释放", Name);
            }
            catch (Exception ex)
            {
                WriteDetailedLog($"[Dispose ERROR] 释放资源异常: {ex.Message}");
                _logger.LogError(ex, "[{DeviceName}] 释放资源异常", Name);
            }
        }

        #region 当前流量轮询

        private async Task StartCurrentFlowPollingAsync()
        {
            InfoLog("启动流量轮询任务");
            try
            {
                if (!_currentFlowPollingTask?.IsCompleted ?? false)
                {
                    InfoLog("已有流量轮询任务，先停止");
                    await StopCurrentFlowPollingAsync();
                }

                _flowPollingTaskCts = new CancellationTokenSource();
                _currentFlowPollingTask = Task.Run(async () => await CurrentFlowPollingAsync(_flowPollingTaskCts.Token), _flowPollingTaskCts.Token);

                InfoLog("流量轮询已启动");
            }
            catch (Exception e)
            {
                ErrorLog("流量轮询启动失败", e);
            }
        }

        private async Task CurrentFlowPollingAsync(CancellationToken token)
        {
            InfoLog("当前流量轮询任务已启动");

            while (!token.IsCancellationRequested)
            {
                try
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double flowValue = 0;
                    bool changed = false;

                    if (IsSimulationMode)
                    {
                        Random random = new Random();
                        flowValue = random.NextDouble() + 10;
                    }
                    else if (ConnectionManager != null && DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp)
                    {
                        flowValue = await ConnectionManager.ReadAsync<double>(ComId, "_FT0101_g_m");
                    }
                    else
                    {
                        return;
                    }

                    lock (_stateLock)
                    {
                        if (_currentFlow != flowValue)
                        {
                            _currentFlow = flowValue;
                            changed = true;
                        }
                    }
                    // 更新UI界面
                    if (changed)
                    {
                        RaiseControlStateChanged("CurrentFlow", flowValue);
                    }
                    // 等待
                    await Task.Delay(1000, token);
                }
                catch(OperationCanceledException)
                {
                    InfoLog("当前流量轮询任务已取消");
                    break;
                }
                catch (Exception e)
                {
                    ErrorLog("当前流量轮询任务异常", e);

                    try
                    {
                        await Task.Delay(1000, token); // 发生异常，等待一段时间再进行轮询
                    }
                    catch (OperationCanceledException)
                    {
                        break; // 操作取消，结束轮询
                    }
                }
            }

            InfoLog("当前流量轮询任务已退出");
        }

        private async Task StopCurrentFlowPollingAsync()
        {
            try
            {
                if (_flowPollingTaskCts != null)
                {
                    InfoLog("正在停止流量轮询");
                    _flowPollingTaskCts.Cancel();
                    try
                    {
                        if (_currentFlowPollingTask != null)
                        {
                            // 等待任务结束
                            await _currentFlowPollingTask.WaitAsync(TimeSpan.FromSeconds(2));
                        }
                    }
                    catch (Exception) { }

                    _flowPollingTaskCts.Dispose();
                    _flowPollingTaskCts = null;
                    _currentFlowPollingTask = null;
                    InfoLog("流量轮询已停止");
                }
            }
            catch (Exception e)
            {
                ErrorLog("停止轮询失败",e);
            }
        }

        #endregion
    }
}