using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevicePlugins.Devices;
using MaxChemical.Logging;

namespace MicroReactor_ModbusTCP
{
    /// <summary>
    /// 主从温度控制器 - 单 controller 跟踪主控传感器,其他作为监测路
    /// </summary>
    /// <remarks>
    /// 设计要点:
    /// 1. 只有一个 controller,只调一次循环器,不抢占
    /// 2. 主控传感器用于反馈控制,其他监测
    /// 3. 微调累积上次循环器值,具有"积分"性质
    /// 4. 起步策略: 目标温度 ± 工艺偏置
    /// 5. 循环器命令限幅: 物料目标 ± MaxBias
    /// 6. 失稳迟滞: 进入 Stable 用 Tolerance,退出 Stable 用 3*Tolerance
    /// 7. 集成 SafetyMonitor 做分级保护
    /// 8. 支持 ChangeTargetAsync 平滑切换目标温度(同一批物料分阶段升温)
    /// </remarks>
    public class MasterSlaveTemperatureController
    {
        private readonly string _deviceId;
        private readonly string _circulatorDeviceId;
        private readonly MasterSlaveControllerConfig _config;
        private readonly IDeviceConnectionManager _connectionManager;
        private readonly ILogService _logger;
        private readonly SmartControlLogger _controlLogger;
        private readonly bool _isSimulationMode;
        private readonly ConcurrentDictionary<string, double> _deviceData;
        private readonly SystemStabilityStatus _stabilityStatus;

        public const string Sensor1 = "TT1001";
        public const string Sensor2 = "TT1002";
        public const string Sensor3 = "TT1003";

        private CancellationTokenSource _cts;
        private Task _controlTask;
        private volatile bool _isRunning;
        private volatile bool _isPaused;
        private volatile ControlPhase _currentPhase = ControlPhase.Initializing;
        private readonly object _stateLock = new object();

        private string _masterSensor;
        private double _lastSentCirculatorTemp = double.NaN;
        private DateTime _lastCirculatorAdjustTime = DateTime.MinValue;
        private int _fineAdjustCount = 0;
        private int _controlCycleCount = 0;

        private readonly Dictionary<string, List<double>> _stabilityCheckData;
        private DateTime _stabilityCheckStartTime = DateTime.MinValue;

        private readonly Dictionary<string, SafetyMonitor> _safetyMonitors;

        private string _failureReason = "";

        public ControlPhase CurrentPhase => _currentPhase;
        public string MasterSensor => _masterSensor;
        public string FailureReason => _failureReason;
        public bool IsSystemStable => CheckStableOR();

        public MasterSlaveTemperatureController(
            string deviceId,
            string circulatorDeviceId,
            MasterSlaveControllerConfig config,
            IDeviceConnectionManager connectionManager,
            ILogService logger,
            SmartControlLogger controlLogger,
            bool isSimulationMode,
            ConcurrentDictionary<string, double> deviceData,
            SystemStabilityStatus stabilityStatus)
        {
            _deviceId = deviceId ?? throw new ArgumentNullException(nameof(deviceId));
            _circulatorDeviceId = circulatorDeviceId ?? throw new ArgumentNullException(nameof(circulatorDeviceId));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _connectionManager = connectionManager;
            _logger = logger;
            _controlLogger = controlLogger;
            _isSimulationMode = isSimulationMode;
            _deviceData = deviceData ?? throw new ArgumentNullException(nameof(deviceData));
            _stabilityStatus = stabilityStatus ?? throw new ArgumentNullException(nameof(stabilityStatus));

            _masterSensor = config.MasterSensor;

            _stabilityCheckData = new Dictionary<string, List<double>>
            {
                [Sensor1] = new List<double>(),
                [Sensor2] = new List<double>(),
                [Sensor3] = new List<double>()
            };

            var safetyConfig = new SafetyMonitorConfig
            {
                AlarmDeviationDeg = _config.AlarmDeviationDeg,
                AlarmDurationSec = _config.AlarmDurationSec,
                MaxRateDegPerMin = _config.MaxRateDegPerMin,
                TrendLookaheadSec = _config.TrendLookaheadSec,
                TrendAlarmDeviationDeg = _config.TrendAlarmDeviationDeg
            };
            _safetyMonitors = new Dictionary<string, SafetyMonitor>
            {
                [Sensor1] = new SafetyMonitor(Sensor1, safetyConfig, _controlLogger),
                [Sensor2] = new SafetyMonitor(Sensor2, safetyConfig, _controlLogger),
                [Sensor3] = new SafetyMonitor(Sensor3, safetyConfig, _controlLogger)
            };
        }

        // ============================================================
        // 启停 / 暂停 / 恢复 / 切换目标
        // ============================================================

        public async Task StartAsync()
        {
            if (_isRunning) return;

            _cts = new CancellationTokenSource();
            _isRunning = true;
            _isPaused = false;
            _currentPhase = ControlPhase.Initializing;
            _fineAdjustCount = 0;
            _controlCycleCount = 0;
            _lastSentCirculatorTemp = double.NaN;
            _failureReason = "";

            _deviceData[$"{_deviceId}_{Sensor1}_Target"] = _config.TargetTemperature;
            _deviceData[$"{_deviceId}_{Sensor2}_Target"] = _config.TargetTemperature;
            _deviceData[$"{_deviceId}_{Sensor3}_Target"] = _config.TargetTemperature;

            _controlTask = Task.Run(async () => await ControlLoopAsync(_cts.Token));

            _controlLogger?.LogInfo("主从温度控制器启动",
                $"目标: {_config.TargetTemperature:F1}℃, 主控: {_masterSensor}, " +
                $"升温偏置: +{_config.WarmingBias}℃, 降温偏置: -{_config.CoolingBias}℃");
        }

        /// <summary>
        /// 停止控制器
        /// </summary>
        /// <param name="stopCirculator">true=完全关闭循环器(默认); false=保留循环器运行</param>
        public async Task StopAsync(bool stopCirculator = true)
        {
            if (!_isRunning) return;

            _cts?.Cancel();

            if (_controlTask != null)
            {
                try
                {
                    await _controlTask;
                }
                catch (OperationCanceledException) { }
            }

            _isRunning = false;
            _currentPhase = ControlPhase.Stopped;

            if (stopCirculator)
            {
                await StopCirculatorAsync();
            }
            else
            {
                _controlLogger?.LogInfo("控制器停止,但循环器保持运行(平滑切换模式)");
            }

            UpdateStabilityStatusForAllSensors(false);

            _controlLogger?.LogInfo("主从温度控制器停止",
                $"总周期: {_controlCycleCount}, 微调次数: {_fineAdjustCount}");
        }

        public Task PauseAsync()
        {
            _isPaused = true;
            _controlLogger?.LogInfo("主从温度控制器暂停");
            return Task.CompletedTask;
        }

        public Task ResumeAsync()
        {
            _isPaused = false;
            _controlLogger?.LogInfo("主从温度控制器恢复");
            return Task.CompletedTask;
        }

        /// <summary>
        /// 平滑切换目标温度 (不停 controller、不停循环器)
        /// 工艺场景: 同一批物料分阶段升温,如 50℃ → 80℃
        /// </summary>
        public async Task<bool> ChangeTargetAsync(double newTarget)
        {
            if (!_isRunning)
            {
                _controlLogger?.LogWarning("ChangeTargetAsync 调用失败: 控制器未运行");
                return false;
            }

            double oldTarget = _config.TargetTemperature;
            _controlLogger?.LogInfo("平滑切换目标温度",
                $"{oldTarget:F1}℃ → {newTarget:F1}℃, 主控 {_masterSensor}, 循环器保持运行");

            bool wasPaused = _isPaused;
            _isPaused = true;

            try
            {
                _config.TargetTemperature = newTarget;

                _deviceData[$"{_deviceId}_{Sensor1}_Target"] = newTarget;
                _deviceData[$"{_deviceId}_{Sensor2}_Target"] = newTarget;
                _deviceData[$"{_deviceId}_{Sensor3}_Target"] = newTarget;

                ClearStabilityData();
                _stabilityCheckStartTime = DateTime.Now;
                _fineAdjustCount = 0;

                foreach (var monitor in _safetyMonitors.Values)
                {
                    monitor.Reset();
                }

                UpdateStabilityStatusForAllSensors(false);

                _currentPhase = ControlPhase.Initializing;
                _failureReason = "";

                _controlLogger?.LogSuccess("目标切换完成,进入新一轮起步",
                    $"新目标 {newTarget:F1}℃, 控制器和循环器持续运行");
                return true;
            }
            catch (Exception ex)
            {
                _controlLogger?.LogError("切换目标温度失败", ex);
                return false;
            }
            finally
            {
                _isPaused = wasPaused;
            }
        }

        // ============================================================
        // 主控制循环
        // ============================================================

        private async Task ControlLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!_isPaused && _currentPhase != ControlPhase.Failed)
                    {
                        await PerformOneCycleAsync();
                    }

                    await Task.Delay(_config.ReadingInterval, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, $"控制循环异常: {_deviceId}");
                    _controlLogger?.LogError($"控制循环异常: {ex.Message}", ex);
                    try { await Task.Delay(_config.ReadingInterval, ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        private async Task PerformOneCycleAsync()
        {
            _controlCycleCount++;

            var temps = await ReadAllTemperaturesAsync();
            var safetyResults = AssessSafetyForAllSensors(temps);

            HandleMasterFailover(temps);

            double masterTemp = temps[_masterSensor];
            double masterError = masterTemp - _config.TargetTemperature;

            if (_controlCycleCount % 5 == 0)
            {
                LogCurrentState(temps, masterError, safetyResults);
            }

            // 检查硬报警 - 主控触发硬报警则停止微调
            var masterSafety = safetyResults[_masterSensor];
            if (masterSafety.ShouldStopAdjusting && _currentPhase != ControlPhase.Failed)
            {
                _failureReason = $"主控 {_masterSensor} 触发硬报警: {masterSafety.AlarmReason}";
                _currentPhase = ControlPhase.Failed;
                _controlLogger?.LogError($"进入失败状态 - {_failureReason}", null);
                _controlLogger?.LogWarning("循环器保持最后值,等待人工干预");
                UpdateStabilityStatusForAllSensors(false);
                return;
            }

            CheckMonitorDeviation(temps);

            // 失控保护 - 微调次数超限
            if (_fineAdjustCount >= _config.MaxFineAdjustments)
            {
                _failureReason = $"微调次数超限({_fineAdjustCount}/{_config.MaxFineAdjustments}),系统未收敛";
                _currentPhase = ControlPhase.Failed;
                _controlLogger?.LogError($"进入失败状态 - {_failureReason}", null);
                UpdateStabilityStatusForAllSensors(false);
                return;
            }

            switch (_currentPhase)
            {
                case ControlPhase.Initializing:
                    await HandleInitializingAsync(masterTemp);
                    break;

                case ControlPhase.Adjusting:
                    await HandleAdjustingAsync(masterTemp, masterError);
                    break;

                case ControlPhase.Stable:
                    await HandleStableAsync(masterTemp, masterError);
                    break;
            }
        }

        // ============================================================
        // 阶段处理
        // ============================================================

        private async Task HandleInitializingAsync(double masterTemp)
        {
            double bias = (masterTemp < _config.TargetTemperature)
                ? _config.WarmingBias
                : -_config.CoolingBias;

            double targetCirculatorTemp = _config.TargetTemperature + bias;
            targetCirculatorTemp = ClampCirculatorCommand(targetCirculatorTemp);

            _controlLogger?.LogInfo("起步阶段",
                $"主控 {_masterSensor}={masterTemp:F2}℃ → 目标 {_config.TargetTemperature:F2}℃, " +
                $"工艺偏置 {bias:+0.0;-0.0}℃, 循环器设定 {targetCirculatorTemp:F1}℃");

            bool ok = await SetCirculatorTemperatureAsync(targetCirculatorTemp);
            if (ok)
            {
                _lastCirculatorAdjustTime = DateTime.Now;
                _stabilityCheckStartTime = DateTime.Now;
                ClearStabilityData();
                _currentPhase = ControlPhase.Adjusting;
            }
            else
            {
                _controlLogger?.LogError("起步阶段:设置循环器失败", null);
            }
        }

        private async Task HandleAdjustingAsync(double masterTemp, double masterError)
        {
            var now = DateTime.Now;
            var timeSinceAdjust = now - _lastCirculatorAdjustTime;

            if (timeSinceAdjust < _config.MinWaitAfterAdjustment)
            {
                return;
            }

            CollectStabilityData(masterTemp);

            if (_stabilityCheckData[_masterSensor].Count < _config.MinStabilityDataPoints)
            {
                return;
            }

            double dataDurationSec = (now - _stabilityCheckStartTime).TotalSeconds;
            if (dataDurationSec < _config.StabilityTime.TotalSeconds)
            {
                return;
            }

            var stability = CheckMasterStability();

            if (!stability.IsStable)
            {
                if (_controlCycleCount % 10 == 0)
                {
                    _controlLogger?.LogDebug($"主控未稳定 {_masterSensor}", stability.Details);
                }
                TrimOldStabilityData();
                return;
            }

            if (Math.Abs(masterError) <= _config.Tolerance)
            {
                EnterStableState(masterTemp);
                return;
            }

            _controlLogger?.LogInfo($"主控稳定但未达目标 {_masterSensor}",
                $"温度 {masterTemp:F2}℃, 偏差 {masterError:F2}℃, 触发微调");

            await PerformFineAdjustAsync(masterError);
        }

        private async Task HandleStableAsync(double masterTemp, double masterError)
        {
            double exitThreshold = _config.Tolerance * 3.0;

            UpdateStabilityStatusOR();

            if (Math.Abs(masterError) > exitThreshold)
            {
                _controlLogger?.LogWarning($"主控失稳 {_masterSensor}",
                    $"温度 {masterTemp:F2}℃, 偏差 {masterError:F2}℃ > 失稳阈值 ±{exitThreshold:F2}℃");

                _currentPhase = ControlPhase.Adjusting;
                _stabilityCheckStartTime = DateTime.Now;
                ClearStabilityData();
                _lastCirculatorAdjustTime = DateTime.Now;
                UpdateStabilityStatusForAllSensors(false);
            }
        }

        // ============================================================
        // 微调 - 累积上次循环器值
        // ============================================================

        private async Task PerformFineAdjustAsync(double masterError)
        {
            _fineAdjustCount++;

            double step = _config.FineAdjustStep;
            double absErr = Math.Abs(masterError);
            if (absErr > 5.0) step = _config.FineAdjustStep * 3.0;
            else if (absErr > 2.0) step = _config.FineAdjustStep * 2.0;

            double baseTemp = double.IsNaN(_lastSentCirculatorTemp)
                ? _config.TargetTemperature
                : _lastSentCirculatorTemp;

            double newCirculatorTemp = (masterError < 0)
                ? baseTemp + step
                : baseTemp - step;

            double clampedTemp = ClampCirculatorCommand(newCirculatorTemp);
            bool wasClamped = Math.Abs(clampedTemp - newCirculatorTemp) > 0.01;

            _controlLogger?.LogInfo($"微调 #{_fineAdjustCount}",
                $"偏差 {masterError:F2}℃, 步长 {step:F1}℃, " +
                $"上次循环器 {baseTemp:F1}℃ → 新值 {newCirculatorTemp:F1}℃" +
                (wasClamped ? $" (限幅至 {clampedTemp:F1}℃)" : ""));

            bool ok = await SetCirculatorTemperatureAsync(clampedTemp);
            if (ok)
            {
                _lastCirculatorAdjustTime = DateTime.Now;
                _stabilityCheckStartTime = DateTime.Now;
                ClearStabilityData();
            }
        }

        private void EnterStableState(double masterTemp)
        {
            _currentPhase = ControlPhase.Stable;
            UpdateStabilityStatusOR();

            _controlLogger?.LogSuccess("进入稳定状态",
                $"主控 {_masterSensor} = {masterTemp:F2}℃, 目标 {_config.TargetTemperature:F2}℃, " +
                $"循环器 {_lastSentCirculatorTemp:F1}℃, 微调用了 {_fineAdjustCount} 次");
        }

        // ============================================================
        // 稳定性 OR 判据
        // ============================================================

        private bool CheckStableOR()
        {
            if (_currentPhase != ControlPhase.Stable) return false;

            return _stabilityStatus.TT1001_Stable
                || _stabilityStatus.TT1002_Stable
                || _stabilityStatus.TT1003_Stable;
        }

        private void UpdateStabilityStatusOR()
        {
            double t1 = _deviceData.GetValueOrDefault($"{_deviceId}_{Sensor1}_Actual", double.NaN);
            double t2 = _deviceData.GetValueOrDefault($"{_deviceId}_{Sensor2}_Actual", double.NaN);
            double t3 = _deviceData.GetValueOrDefault($"{_deviceId}_{Sensor3}_Actual", double.NaN);

            double tol = _config.Tolerance;
            double target = _config.TargetTemperature;

            _stabilityStatus.TT1001_Stable = !double.IsNaN(t1) && Math.Abs(t1 - target) <= tol;
            _stabilityStatus.TT1002_Stable = !double.IsNaN(t2) && Math.Abs(t2 - target) <= tol;
            _stabilityStatus.TT1003_Stable = !double.IsNaN(t3) && Math.Abs(t3 - target) <= tol;
        }

        private void UpdateStabilityStatusForAllSensors(bool stable)
        {
            _stabilityStatus.TT1001_Stable = stable;
            _stabilityStatus.TT1002_Stable = stable;
            _stabilityStatus.TT1003_Stable = stable;
        }

        // ============================================================
        // 主控故障切换
        // ============================================================

        private void HandleMasterFailover(Dictionary<string, double> temps)
        {
            double masterTemp = temps[_masterSensor];

            bool isMasterFaulty = double.IsNaN(masterTemp)
                                  || masterTemp < _config.SensorMinValid
                                  || masterTemp > _config.SensorMaxValid;

            if (!isMasterFaulty) return;

            string newMaster = null;
            foreach (var sensor in new[] { Sensor1, Sensor2, Sensor3 })
            {
                if (sensor == _masterSensor) continue;
                double t = temps[sensor];
                if (!double.IsNaN(t) && t >= _config.SensorMinValid && t <= _config.SensorMaxValid)
                {
                    newMaster = sensor;
                    break;
                }
            }

            if (newMaster != null)
            {
                _controlLogger?.LogWarning("主控故障切换",
                    $"原主控 {_masterSensor}={masterTemp:F2}℃ 异常, 切换到 {newMaster}={temps[newMaster]:F2}℃");
                _masterSensor = newMaster;
                ClearStabilityData();
                _stabilityCheckStartTime = DateTime.Now;
            }
            else
            {
                _failureReason = "全部传感器故障";
                _currentPhase = ControlPhase.Failed;
                _controlLogger?.LogError("全部传感器故障,进入失败状态", null);
            }
        }

        private void CheckMonitorDeviation(Dictionary<string, double> temps)
        {
            double masterTemp = temps[_masterSensor];
            foreach (var sensor in new[] { Sensor1, Sensor2, Sensor3 })
            {
                if (sensor == _masterSensor) continue;
                double monitorTemp = temps[sensor];
                if (double.IsNaN(monitorTemp)) continue;

                double diff = Math.Abs(monitorTemp - masterTemp);
                if (diff > _config.MonitorMaxDeviation)
                {
                    if (_controlCycleCount % 30 == 0)
                    {
                        _controlLogger?.LogWarning($"监测路偏差大 {sensor}",
                            $"监测 {monitorTemp:F2}℃ vs 主控 {masterTemp:F2}℃, " +
                            $"偏差 {diff:F2}℃ > 限值 {_config.MonitorMaxDeviation}℃");
                    }
                }
            }
        }

        // ============================================================
        // 安全监测
        // ============================================================

        private Dictionary<string, SafetyAssessment> AssessSafetyForAllSensors(Dictionary<string, double> temps)
        {
            var results = new Dictionary<string, SafetyAssessment>();
            double currentCirc = double.IsNaN(_lastSentCirculatorTemp) ? _config.TargetTemperature : _lastSentCirculatorTemp;

            foreach (var sensor in new[] { Sensor1, Sensor2, Sensor3 })
            {
                double t = temps[sensor];
                if (double.IsNaN(t))
                {
                    results[sensor] = new SafetyAssessment { OverallState = SafetyState.Normal };
                    continue;
                }
                results[sensor] = _safetyMonitors[sensor].Assess(t, _config.TargetTemperature, currentCirc);
            }
            return results;
        }

        // ============================================================
        // 循环器命令限幅
        // ============================================================

        private double ClampCirculatorCommand(double rawTarget)
        {
            double minAllowed = _config.TargetTemperature - _config.MaxBias;
            double maxAllowed = _config.TargetTemperature + _config.MaxBias;
            return Math.Clamp(rawTarget, minAllowed, maxAllowed);
        }

        // ============================================================
        // 稳定性检查 - 主控
        // ============================================================

        private StabilityResult CheckMasterStability()
        {
            var data = _stabilityCheckData[_masterSensor];
            if (data.Count < _config.MinStabilityDataPoints)
            {
                return StabilityResult.InsufficientData();
            }

            var result = new StabilityResult();

            result.StandardDeviation = CalculateStandardDeviation(data);
            result.StdDevOK = result.StandardDeviation <= _config.StabilityStdDevThreshold;

            result.TemperatureRange = data.Max() - data.Min();
            result.RangeOK = result.TemperatureRange <= _config.StabilityRangeThreshold;

            result.TrendOK = CheckTemperatureTrend(data);
            result.RecentPointsOK = CheckRecentPointsStability(data);

            result.IsStable = result.StdDevOK && result.RangeOK && result.TrendOK && result.RecentPointsOK;
            result.Details = $"标准差:{result.StandardDeviation:F3}(<={_config.StabilityStdDevThreshold}) " +
                             $"极差:{result.TemperatureRange:F2}(<={_config.StabilityRangeThreshold}) " +
                             $"趋势:{(result.TrendOK ? "OK" : "NG")} " +
                             $"近期:{(result.RecentPointsOK ? "OK" : "NG")}";

            return result;
        }

        private bool CheckTemperatureTrend(List<double> data)
        {
            if (data.Count < 6) return true;
            var recent = data.TakeLast(6).ToList();
            double slope = CalculateSlope(recent);
            return Math.Abs(slope) < 0.05;
        }

        private bool CheckRecentPointsStability(List<double> data)
        {
            if (data.Count < 4) return false;
            var recent = data.TakeLast(4).ToList();
            double recentRange = recent.Max() - recent.Min();
            bool smallFluctuation = recentRange <= 0.2;

            bool isIncreasing = true, isDecreasing = true;
            for (int i = 1; i < recent.Count; i++)
            {
                if (recent[i] <= recent[i - 1]) isIncreasing = false;
                if (recent[i] >= recent[i - 1]) isDecreasing = false;
            }
            return smallFluctuation && !(isIncreasing || isDecreasing);
        }

        private double CalculateSlope(List<double> data)
        {
            if (data.Count < 2) return 0;
            double n = data.Count, sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
            for (int i = 0; i < data.Count; i++)
            {
                sumX += i; sumY += data[i];
                sumXY += i * data[i]; sumX2 += i * i;
            }
            double denom = n * sumX2 - sumX * sumX;
            if (Math.Abs(denom) < 1e-6) return 0;
            return (n * sumXY - sumX * sumY) / denom;
        }

        private double CalculateStandardDeviation(List<double> values)
        {
            if (values == null || values.Count < 2) return 999.0;
            try
            {
                double mean = values.Average();
                double sumSq = values.Sum(x => (x - mean) * (x - mean));
                double variance = sumSq / (values.Count - 1);
                if (double.IsNaN(variance) || double.IsInfinity(variance)) return 999.0;
                double std = Math.Sqrt(variance);
                if (double.IsNaN(std) || double.IsInfinity(std)) return 999.0;
                return std;
            }
            catch { return 999.0; }
        }

        // ============================================================
        // 数据收集
        // ============================================================

        private void CollectStabilityData(double masterTemp)
        {
            _stabilityCheckData[_masterSensor].Add(masterTemp);
            int max = _config.MinStabilityDataPoints * 3;
            if (_stabilityCheckData[_masterSensor].Count > max)
            {
                _stabilityCheckData[_masterSensor].RemoveAt(0);
            }
        }

        private void TrimOldStabilityData()
        {
            int keep = _config.MinStabilityDataPoints * 2;
            foreach (var key in _stabilityCheckData.Keys.ToList())
            {
                while (_stabilityCheckData[key].Count > keep)
                {
                    _stabilityCheckData[key].RemoveAt(0);
                }
            }
        }

        private void ClearStabilityData()
        {
            foreach (var key in _stabilityCheckData.Keys.ToList())
            {
                _stabilityCheckData[key].Clear();
            }
        }

        // ============================================================
        // 温度读取(三路并行)
        // ============================================================

        private async Task<Dictionary<string, double>> ReadAllTemperaturesAsync()
        {
            var t1Task = ReadOneTemperatureAsync(Sensor1);
            var t2Task = ReadOneTemperatureAsync(Sensor2);
            var t3Task = ReadOneTemperatureAsync(Sensor3);

            await Task.WhenAll(t1Task, t2Task, t3Task);

            var temps = new Dictionary<string, double>
            {
                [Sensor1] = await t1Task,
                [Sensor2] = await t2Task,
                [Sensor3] = await t3Task
            };

            _deviceData[$"{_deviceId}_{Sensor1}_Actual"] = temps[Sensor1];
            _deviceData[$"{_deviceId}_{Sensor2}_Actual"] = temps[Sensor2];
            _deviceData[$"{_deviceId}_{Sensor3}_Actual"] = temps[Sensor3];

            return temps;
        }

        private async Task<double> ReadOneTemperatureAsync(string sensorName)
        {
            if (_isSimulationMode)
            {
                return GetSimulatedTemperature(sensorName);
            }

            try
            {
                return await _connectionManager.ReadAsync<double>(_deviceId, $"{sensorName}_℃");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"读取传感器失败: {_deviceId}_{sensorName}");
                _controlLogger?.LogError($"读取传感器失败 {sensorName}", ex);

                if (_deviceData.TryGetValue($"{_deviceId}_{sensorName}_Actual", out double cached))
                {
                    return cached;
                }
                return double.NaN;
            }
        }

        /// <summary>
        /// 模拟温度 - 三个传感器共享同一个虚拟循环器变量(物理上正确)
        /// </summary>
        private double GetSimulatedTemperature(string sensorName)
        {
            string actualKey = $"{_deviceId}_{sensorName}_Set";
            string sharedCirculatorKey = $"{_deviceId}_SharedCirculatorTemp";

            double currentTemp = _deviceData.TryGetValue(actualKey, out double existing) ? existing : 22.0;

            double effectiveCirc = _deviceData.TryGetValue(sharedCirculatorKey, out double circ)
                ? circ
                : _config.TargetTemperature;

            double tempDiff = effectiveCirc - currentTemp;

            double coeff;
            if (Math.Abs(tempDiff) > 30) coeff = 0.20;
            else if (Math.Abs(tempDiff) > 15) coeff = 0.15;
            else if (Math.Abs(tempDiff) > 5) coeff = 0.10;
            else coeff = 0.05;

            double change = coeff * tempDiff;
            change = Math.Clamp(change, -3.0, 3.0);

            double newTemp = currentTemp + change;

            var rand = new Random(sensorName.GetHashCode() + (int)DateTime.Now.Ticks);
            double noise = (rand.NextDouble() - 0.5) * 0.3;
            newTemp += noise;

            double sensorBias = sensorName switch
            {
                Sensor1 => -0.2,
                Sensor2 => 0.0,
                Sensor3 => +0.2,
                _ => 0.0
            };
            newTemp += sensorBias;

            _deviceData[actualKey] = newTemp;
            return newTemp;
        }

        // ============================================================
        // 循环器控制
        // ============================================================

        private async Task<bool> SetCirculatorTemperatureAsync(double temperature)
        {
            try
            {
                if (_isSimulationMode)
                {
                    _deviceData[$"{_deviceId}_SharedCirculatorTemp"] = temperature;
                    _lastSentCirculatorTemp = temperature;
                    return true;
                }

                string cmd = GetCirculatorTempSetCommand();
                bool result = await _connectionManager.WriteAsync(_circulatorDeviceId, cmd, temperature);
                if (result)
                {
                    _lastSentCirculatorTemp = temperature;
                }
                return result;
            }
            catch (Exception ex)
            {
                _controlLogger?.LogError("设置循环器温度异常", ex);
                return false;
            }
        }

        private async Task StopCirculatorAsync()
        {
            try
            {
                if (_isSimulationMode)
                {
                    _controlLogger?.LogInfo($"模拟模式: 停止循环器 {_circulatorDeviceId}");
                    return;
                }

                _controlLogger?.LogInfo($"停止循环器 {_circulatorDeviceId} 全部功能");
                await _connectionManager.WriteAsync(_circulatorDeviceId, GetCirculatorHeatingCommand(), false);
                await Task.Delay(300);
                await _connectionManager.WriteAsync(_circulatorDeviceId, GetCirculatorCoolingCommand(), false);
                await Task.Delay(300);
                await _connectionManager.WriteAsync(_circulatorDeviceId, GetCirculatorCycleCommand(), false);

                _controlLogger?.LogSuccess($"循环器 {_circulatorDeviceId} 已完全停止");
            }
            catch (Exception ex)
            {
                _controlLogger?.LogError($"停止循环器异常: {_circulatorDeviceId}", ex);
            }
        }

        private string GetCirculatorTempSetCommand() =>
            _circulatorDeviceId == "TC0101" ? "YY_SV_Set_1" : "YY_SV_Set_2";
        private string GetCirculatorHeatingCommand() =>
            _circulatorDeviceId == "TC0101" ? "YY_Heat_1" : "YY_Heat_2";
        private string GetCirculatorCoolingCommand() =>
            _circulatorDeviceId == "TC0101" ? "YY_Cool_1" : "YY_Cool_2";
        private string GetCirculatorCycleCommand() =>
            _circulatorDeviceId == "TC0101" ? "YY_Cycle_1" : "YY_Cycle_2";

        // ============================================================
        // 日志
        // ============================================================

        private void LogCurrentState(Dictionary<string, double> temps, double masterError, Dictionary<string, SafetyAssessment> safety)
        {
            string masterMark1 = (_masterSensor == Sensor1) ? "[主]" : "    ";
            string masterMark2 = (_masterSensor == Sensor2) ? "[主]" : "    ";
            string masterMark3 = (_masterSensor == Sensor3) ? "[主]" : "    ";

            _controlLogger?.LogDebug($"控制状态 周期#{_controlCycleCount}",
                $"阶段:{_currentPhase}, " +
                $"{masterMark1}TT1001:{temps[Sensor1]:F2}, " +
                $"{masterMark2}TT1002:{temps[Sensor2]:F2}, " +
                $"{masterMark3}TT1003:{temps[Sensor3]:F2}, " +
                $"主控偏差:{masterError:F2}℃, " +
                $"安全:{safety[_masterSensor]}");
        }

        public string GetCurrentStateInfo()
        {
            return _currentPhase switch
            {
                ControlPhase.Initializing => "初始化",
                ControlPhase.Adjusting => $"调整中 (微调#{_fineAdjustCount})",
                ControlPhase.Stable => "已稳定",
                ControlPhase.Failed => $"失败:{_failureReason}",
                ControlPhase.Stopped => "已停止",
                _ => "未知"
            };
        }
    }

    // ============================================================
    // 配置类
    // ============================================================

    public class MasterSlaveControllerConfig
    {
        public double TargetTemperature { get; set; }
        public string MasterSensor { get; set; } = "TT1002";

        public double WarmingBias { get; set; } = 5.0;
        public double CoolingBias { get; set; } = 5.0;
        public double MaxBias { get; set; } = 15.0;

        public TimeSpan ReadingInterval { get; set; } = TimeSpan.FromSeconds(3);
        public TimeSpan StabilityTime { get; set; } = TimeSpan.FromSeconds(20);
        public TimeSpan MinWaitAfterAdjustment { get; set; } = TimeSpan.FromSeconds(30);

        public double Tolerance { get; set; } = 0.5;
        public double FineAdjustStep { get; set; } = 0.5;
        public int MinStabilityDataPoints { get; set; } = 8;
        public double StabilityStdDevThreshold { get; set; } = 0.15;
        public double StabilityRangeThreshold { get; set; } = 0.4;

        public int MaxFineAdjustments { get; set; } = 20;
        public double SensorMinValid { get; set; } = -100.0;
        public double SensorMaxValid { get; set; } = 500.0;

        public double AlarmDeviationDeg { get; set; } = 10.0;
        public double AlarmDurationSec { get; set; } = 120.0;
        public double MaxRateDegPerMin { get; set; } = 10.0;
        public double TrendLookaheadSec { get; set; } = 120.0;
        public double TrendAlarmDeviationDeg { get; set; } = 15.0;

        public double MonitorMaxDeviation { get; set; } = 10.0;
    }

    public enum ControlPhase
    {
        Initializing,
        Adjusting,
        Stable,
        Failed,
        Stopped
    }

    public class StabilityResult
    {
        public bool IsStable { get; set; }
        public double StandardDeviation { get; set; }
        public double TemperatureRange { get; set; }
        public bool StdDevOK { get; set; }
        public bool RangeOK { get; set; }
        public bool TrendOK { get; set; }
        public bool RecentPointsOK { get; set; }
        public string Details { get; set; } = "";

        public static StabilityResult InsufficientData() => new StabilityResult
        {
            IsStable = false,
            Details = "数据点不足"
        };
    }
}