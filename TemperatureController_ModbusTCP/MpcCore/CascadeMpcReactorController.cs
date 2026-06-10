using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevicePlugins.Devices;
using MaxChemical.Logging;

namespace MicroReactor_ModbusTCP.MpcCore
{
    /// <summary>
    /// 串级 MPC 反应器温度控制器(替换 CascadePidController)
    ///
    /// === 设计原则:外壳不变,内核换 MPC ===
    /// 本类对外暴露与 CascadePidController 完全一致的接口
    /// (StartAsync/StopAsync/Pause/Resume/ChangeTargetAsync/CurrentPhase/
    ///  MasterSensor/FailureReason/IsSystemStable/LastJacketTarget/IntegralAccum/
    ///  GetCurrentStateInfo),因此 MicroReactor_ModbusTCP 主驱动几乎无需改动。
    ///
    /// 保留的"外壳"逻辑(与 PID 版一致):
    ///   - 三路温度读取 + 主控故障转移 (HandleMasterFailover)
    ///   - 物料温度滑动平均 (ApplyMasterSmoothing)
    ///   - 监测路偏差检查 (CheckMonitorDeviation)
    ///   - 三路 OR 稳定判据 (UpdateStabilityStatusOR / CheckStableOR)
    ///   - SafetyMonitor 分级安全报警
    ///   - 控制日志
    ///
    /// 替换的"内核":
    ///   原 ComputeWorkingTarget + ComputePidOutput  →  CascadeMpcController.Step()
    ///
    /// === 与 MpcWorkbench 验证版严格一致的关键点 ===
    ///   1. Ts = 10 秒(控制周期也强制 10 秒)
    ///   2. 喂给 MPC 的是主控单路物料温度 + 夹套实测
    ///   3. 夹套实测做 0.6/0.4 一阶低通(τ≈12s)后再喂 MPC
    ///   4. 启动序列:读 jacket → Reset → initialPush(SP+15%散热,封顶+10) → 写 → 再 Reset
    ///   5. SP 切换(ChangeTargetAsync)不做预压、不 Reset 控制器(SP_BOOST 已禁用)
    ///   6. MPC 参数 = MpcParameters.Default()(Np=50,Nc=3,RateMax=2.5,DobGain=0.05,τref=1.8)
    /// </summary>
    public class CascadeMpcReactorController
    {
        // ================ 不可变配置 ================
        private readonly string _deviceId;
        private readonly string _circulatorDeviceId;
        private readonly CascadeMpcConfig _config;
        private readonly IDeviceConnectionManager _connectionManager;
        private readonly ILogService _logger;
        private readonly SmartControlLogger _controlLogger;
        private readonly bool _isSimulationMode;
        private readonly ConcurrentDictionary<string, double> _deviceData;
        private readonly SystemStabilityStatus _stabilityStatus;

        public const string Sensor1 = "TT1001";
        public const string Sensor2 = "TT1002";
        public const string Sensor3 = "TT1003";

        // ================ MPC 内核 ================
        private readonly CascadeMpcController _mpc;
        private bool _mpcInitialized = false;
        private double _jacketFiltered = double.NaN;   // 夹套低通(与 MpcRunner 一致)

        // ================ 运行时状态 ================
        private CancellationTokenSource _cts;
        private Task _controlTask;
        private volatile bool _isRunning;
        private volatile bool _isPaused;
        private volatile ControlPhase _currentPhase = ControlPhase.Initializing;
        private string _failureReason = "";

        private string _masterSensor;

        // 物料温度滑动平均
        private readonly Queue<double> _masterTempHistory = new Queue<double>();
        private const int SmoothingWindow = 3;

        // 稳定性判定
        private DateTime _stableStartTime = DateTime.MinValue;
        private bool _hasEnteredStable = false;

        // 安全监测
        private readonly Dictionary<string, SafetyMonitor> _safetyMonitors;

        private int _controlCycleCount = 0;
        private double _lastJacketTarget = double.NaN;

        private double _startMasterTemp;

        // ================ 公共属性(与 PID 版接口一致) ================
        public ControlPhase CurrentPhase => _currentPhase;
        public string MasterSensor => _masterSensor;
        public string FailureReason => _failureReason;
        public bool IsSystemStable => CheckStableOR();
        public double LastJacketTarget => _lastJacketTarget;
        // PID 版有 IntegralAccum;MPC 没有积分项,这里返回 DOB 估计(等价的稳态补偿量),
        // 保证 UI 读取此属性不崩,且语义合理(都是"消稳态偏差的累积量")。
        public double IntegralAccum => _mpc?.LastDisturbance ?? 0.0;

        public CascadeMpcReactorController(
            string deviceId,
            string circulatorDeviceId,
            CascadeMpcConfig config,
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

            // MPC 内核:默认参数 = 验证版
            var bank = MpcModelBank.CreateDefault();
            var mpcParams = MpcParameters.Default();   // Ts=10, Np=50, Nc=3, RateMax=2.5, DobGain=0.05, τref=1.8
            _mpc = new CascadeMpcController(bank, mpcParams);

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
        // 启停 / 暂停 / 切换目标
        // ============================================================

        public async Task StartAsync()
        {
            if (_isRunning) return;

            _cts = new CancellationTokenSource();
            _isRunning = true;
            _isPaused = false;
            _currentPhase = ControlPhase.Initializing;
            _failureReason = "";
            _controlCycleCount = 0;
            _hasEnteredStable = false;
            _stableStartTime = DateTime.MinValue;
            _lastJacketTarget = double.NaN;
            _jacketFiltered = double.NaN;
            _mpcInitialized = false;
            _masterTempHistory.Clear();

            // 读起始物料温度(用于日志)
            double startTemp;
            try
            {
                startTemp = await ReadOneTemperatureAsync(_masterSensor);
                if (double.IsNaN(startTemp) || startTemp < _config.SensorMinValid || startTemp > _config.SensorMaxValid)
                {
                    startTemp = 25.0;
                    _controlLogger?.LogWarning("读取启动温度失败,使用默认 25℃");
                }
            }
            catch { startTemp = 25.0; }
            _startMasterTemp = startTemp;

            _deviceData[$"{_deviceId}_{Sensor1}_Target"] = _config.TargetTemperature;
            _deviceData[$"{_deviceId}_{Sensor2}_Target"] = _config.TargetTemperature;
            _deviceData[$"{_deviceId}_{Sensor3}_Target"] = _config.TargetTemperature;

            _controlTask = Task.Run(async () => await ControlLoopAsync(_cts.Token));

            _controlLogger?.LogInfo("====== 串级 MPC 温度控制器启动 ======",
                $"目标温度: {_config.TargetTemperature:F2}℃\n" +
                $"起始温度: {_startMasterTemp:F2}℃\n" +
                $"容差(达标判据): ±{_config.Tolerance:F2}℃\n" +
                $"主控传感器: {_masterSensor}\n" +
                $"循环器: {_circulatorDeviceId}\n" +
                $"夹套油温寄存器: {GetJacketTempRegister()}\n" +
                $"控制周期: {_config.ReadingInterval.TotalSeconds:F0}秒 (MPC Ts=10s)\n" +
                $"MPC: Np=50, Nc=3, RateMax=2.5℃/min, DobGain=0.05, τref×1.8\n" +
                $"工作模式: {(_isSimulationMode ? "模拟" : "实机")}");
        }

        public async Task StopAsync(bool stopCirculator = true)
        {
            if (!_isRunning) return;

            _cts?.Cancel();
            if (_controlTask != null)
            {
                try { await _controlTask; }
                catch (OperationCanceledException) { }
            }

            _isRunning = false;
            _currentPhase = ControlPhase.Stopped;

            if (stopCirculator)
                await StopCirculatorAsync();
            else
                _controlLogger?.LogInfo("控制器停止,循环器保持运行(平滑切换)");

            UpdateStabilityStatusForAllSensors(false);

            _controlLogger?.LogInfo("MPC 控制器停止",
                $"总周期: {_controlCycleCount}, 末次循环器命令: {_lastJacketTarget:F2}℃, DOB: {_mpc.LastDisturbance:F3}");
        }

        public Task PauseAsync()
        {
            _isPaused = true;
            _controlLogger?.LogInfo("控制器暂停");
            return Task.CompletedTask;
        }

        public Task ResumeAsync()
        {
            _isPaused = false;
            _controlLogger?.LogInfo("控制器恢复");
            return Task.CompletedTask;
        }

        /// <summary>
        /// 平滑切换目标温度。
        /// === 与 PID 版的关键区别:不预压、不 Reset MPC 状态(SP_BOOST 已禁用)===
        /// 让 MPC 从当前 jacketSp 平滑过渡到新稳态。仅重置稳定判据与安全监测器。
        /// 这是 2026-05-19 降温失控复盘后的正确做法。
        /// </summary>
        public async Task<bool> ChangeTargetAsync(double newTarget)
        {
            if (!_isRunning)
            {
                _controlLogger?.LogWarning("ChangeTargetAsync 失败: 控制器未运行");
                return false;
            }

            double oldTarget = _config.TargetTemperature;
            _controlLogger?.LogInfo("====== 平滑切换目标温度(MPC,无预压)======",
                $"{oldTarget:F2}℃ → {newTarget:F2}℃\n主控:{_masterSensor},循环器与 MPC 状态保持连续");

            bool wasPaused = _isPaused;
            _isPaused = true;
            try
            {
                _config.TargetTemperature = newTarget;

                _deviceData[$"{_deviceId}_{Sensor1}_Target"] = newTarget;
                _deviceData[$"{_deviceId}_{Sensor2}_Target"] = newTarget;
                _deviceData[$"{_deviceId}_{Sensor3}_Target"] = newTarget;

                // 只重置"达标判据"和安全监测器,不动 MPC 内核状态。
                _hasEnteredStable = false;
                _stableStartTime = DateTime.MinValue;
                foreach (var monitor in _safetyMonitors.Values) monitor.Reset();
                UpdateStabilityStatusForAllSensors(false);

                _currentPhase = ControlPhase.Adjusting;
                _failureReason = "";

                _controlLogger?.LogSuccess("目标切换完成(MPC 状态连续,无预压冲击)");
                await Task.CompletedTask;
                return true;
            }
            catch (Exception ex)
            {
                _controlLogger?.LogError("切换目标失败", ex);
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
                        await PerformOneCycleAsync();

                    await Task.Delay(_config.ReadingInterval, ct);
                }
                catch (OperationCanceledException) { break; }
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
            double rawJacketTemp = await ReadJacketTemperatureAsync();

            HandleMasterFailover(temps);
            double rawMasterTemp = temps[_masterSensor];
            double smoothedMasterTemp = ApplyMasterSmoothing(rawMasterTemp);

            double errorToFinal = _config.TargetTemperature - smoothedMasterTemp;
            double absErrToFinal = Math.Abs(errorToFinal);

            var safetyResults = AssessSafetyForAllSensors(temps);
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

            // === 夹套低通(与 MpcRunner 一致:0.6 旧 + 0.4 新,τ≈12s)===
            double jacketForMpc;
            if (double.IsNaN(rawJacketTemp))
            {
                // 夹套读不到:本周期跳过控制(保持上次命令),与 MpcRunner 跳过逻辑一致
                _controlLogger?.LogWarning("夹套温度无效,本周期跳过控制");
                UpdateStabilityState(absErrToFinal);
                UpdateStabilityStatusOR();
                return;
            }
            if (double.IsNaN(_jacketFiltered)) _jacketFiltered = rawJacketTemp;
            else _jacketFiltered = 0.6 * _jacketFiltered + 0.4 * rawJacketTemp;
            jacketForMpc = _jacketFiltered;

            // 流量
            double flow = GetCurrentFlow();

            // === MPC 初始化(首个有效周期,复刻 MpcRunner 启动序列)===
            if (!_mpcInitialized)
            {
                double y0 = smoothedMasterTemp;
                double jacket0 = jacketForMpc;
                _mpc.Reset(initialJacketSp: jacket0, initialY: y0);

                // 启动智能预热:initialPush = SP + max(0, (SP-y0)*0.15),封顶 SP+10,夹紧到循环器上下限
                double initialPush = _config.TargetTemperature + Math.Max(0, (_config.TargetTemperature - y0) * 0.15);
                initialPush = Math.Max(_config.MinCirculatorCommand,
                              Math.Min(_config.MaxCirculatorCommand, initialPush));
                initialPush = Math.Min(initialPush, _config.TargetTemperature + 10);

                await WriteCirculatorCommandAsync(initialPush);
                _lastJacketTarget = initialPush;
                _mpc.Reset(initialJacketSp: initialPush, initialY: y0);
                _mpcInitialized = true;

                _controlLogger?.LogInfo("MPC 已初始化",
                    $"y0={y0:F2}, jacket0={jacket0:F2}, 启动预热命令={initialPush:F2}℃");

                UpdateStabilityState(absErrToFinal);
                UpdateStabilityStatusOR();
                if (_currentPhase == ControlPhase.Initializing) _currentPhase = ControlPhase.Adjusting;
                return; // 本周期只做预热,下周期开始正式 MPC
            }

            // === MPC 一步 ===
            double jacketSpNew = _mpc.Step(_config.TargetTemperature, smoothedMasterTemp, jacketForMpc, flow);
            _lastJacketTarget = jacketSpNew;

            await WriteCirculatorCommandAsync(jacketSpNew);

            UpdateStabilityState(absErrToFinal);
            UpdateStabilityStatusOR();

            if (_currentPhase == ControlPhase.Initializing) _currentPhase = ControlPhase.Adjusting;

            if (_controlCycleCount % 5 == 0)
                LogControlState(temps, smoothedMasterTemp, errorToFinal, rawJacketTemp, jacketSpNew, safetyResults);
        }

        private double GetCurrentFlow()
        {
            // 优先用设备数据里的流量;读不到则用默认 20(与验证工况一致)
            double flow = _deviceData.GetValueOrDefault($"{_deviceId}_PumpFlow", double.NaN);
            if (double.IsNaN(flow) || flow <= 0) flow = 20.0;
            return flow;
        }

        // ============================================================
        // 稳定性判定(达标判据,与 PID 版一致,容差由 config 给,默认已改 3.0)
        // ============================================================

        private void UpdateStabilityState(double absError)
        {
            bool inTolerance = absError <= _config.Tolerance;

            if (inTolerance)
            {
                if (_stableStartTime == DateTime.MinValue)
                    _stableStartTime = DateTime.Now;

                TimeSpan stableDuration = DateTime.Now - _stableStartTime;
                if (stableDuration >= _config.StableConfirmDuration && !_hasEnteredStable)
                {
                    _hasEnteredStable = true;
                    _currentPhase = ControlPhase.Stable;
                    _controlLogger?.LogSuccess("====== 进入稳定状态 ======",
                        $"主控 {_masterSensor} 偏差 {absError:F2}℃ 持续 {stableDuration.TotalSeconds:F0}秒\n" +
                        $"循环器命令 {_lastJacketTarget:F2}℃");
                }
            }
            else
            {
                if (_hasEnteredStable && absError > _config.Tolerance * 3)
                {
                    _hasEnteredStable = false;
                    _currentPhase = ControlPhase.Adjusting;
                    _controlLogger?.LogWarning("====== 失稳 ======",
                        $"偏差 {absError:F2}℃ > 失稳阈值 ±{_config.Tolerance * 3:F2}℃");
                }
                _stableStartTime = DateTime.MinValue;
            }
        }

        private bool CheckStableOR()
        {
            if (!_hasEnteredStable) return false;
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
        // 主控故障切换 + 监测路检查(与 PID 版一致)
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
                _masterTempHistory.Clear();
                // 注意:不重置 MPC 内核;主控切换后物料读数应连续,MPC 由 DOB 吸收小阶跃。
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
                if (diff > _config.MonitorMaxDeviation && _controlCycleCount % 30 == 0)
                {
                    _controlLogger?.LogWarning($"监测路偏差大 {sensor}",
                        $"监测 {monitorTemp:F2}℃ vs 主控 {masterTemp:F2}℃, 偏差 {diff:F2}℃ > 限值 {_config.MonitorMaxDeviation}℃");
                }
            }
        }

        private Dictionary<string, SafetyAssessment> AssessSafetyForAllSensors(Dictionary<string, double> temps)
        {
            var results = new Dictionary<string, SafetyAssessment>();
            double currentCirc = double.IsNaN(_lastJacketTarget) ? _config.TargetTemperature : _lastJacketTarget;

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
        // 滑动平均
        // ============================================================

        private double ApplyMasterSmoothing(double rawTemp)
        {
            if (double.IsNaN(rawTemp))
                return _masterTempHistory.Count > 0 ? _masterTempHistory.Average() : rawTemp;
            _masterTempHistory.Enqueue(rawTemp);
            while (_masterTempHistory.Count > SmoothingWindow) _masterTempHistory.Dequeue();
            return _masterTempHistory.Average();
        }

        // ============================================================
        // 温度读取(与 PID 版一致)
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
                return GetSimulatedMaterialTemperature(sensorName);

            try
            {
                return await _connectionManager.ReadAsync<double>(_deviceId, $"{sensorName}_℃");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"读取传感器失败: {_deviceId}_{sensorName}");
                _controlLogger?.LogError($"读取传感器失败 {sensorName}", ex);
                if (_deviceData.TryGetValue($"{_deviceId}_{sensorName}_Actual", out double cached))
                    return cached;
                return double.NaN;
            }
        }

        private async Task<double> ReadJacketTemperatureAsync()
        {
            if (_isSimulationMode)
                return SimulateJacketTemperature();

            try
            {
                string register = GetJacketTempRegister();
                return await _connectionManager.ReadAsync<double>(_circulatorDeviceId, register);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"读取夹套油温失败: {_circulatorDeviceId}");
                if (_deviceData.TryGetValue($"{_circulatorDeviceId}_JacketTemp", out double cached))
                    return cached;
                return double.NaN;
            }
        }

        private string GetJacketTempRegister() =>
            _circulatorDeviceId == "TC0101" ? "YY_PV_Real_2" : "YY_PV_Real_4";

        // ============================================================
        // 模拟器(与 PID 版一致,仅模拟模式用)
        // ============================================================

        private double SimulateJacketTemperature()
        {
            string jacketKey = $"{_circulatorDeviceId}_JacketTemp";
            string cmdKey = $"{_circulatorDeviceId}_CmdTarget";
            double currentJacket = _deviceData.GetValueOrDefault(jacketKey, 22.0);
            double cmd = _deviceData.GetValueOrDefault(cmdKey, 22.0);

            double dtSec = _config.ReadingInterval.TotalSeconds;
            double tau = 60.0;
            double alpha = dtSec / (tau + dtSec);
            double newJacket = currentJacket + alpha * (cmd - currentJacket);

            var rand = new Random((int)DateTime.Now.Ticks);
            newJacket += (rand.NextDouble() - 0.5) * 0.1;
            _deviceData[jacketKey] = newJacket;
            return newJacket;
        }

        private double GetSimulatedMaterialTemperature(string sensorName)
        {
            string actualKey = $"{_deviceId}_{sensorName}_Set";
            string jacketKey = $"{_circulatorDeviceId}_JacketTemp";
            double currentMaterial = _deviceData.TryGetValue(actualKey, out double existing) ? existing : 22.0;
            double jacketTemp = _deviceData.GetValueOrDefault(jacketKey, 22.0);

            double tempDiff = jacketTemp - currentMaterial;
            double dtSec = _config.ReadingInterval.TotalSeconds;
            double k = 0.025;
            double change = k * tempDiff * (dtSec / 60.0);
            change = Math.Clamp(change, -1.5, 1.5);
            double newMaterial = currentMaterial + change;

            var rand = new Random(sensorName.GetHashCode() + (int)DateTime.Now.Ticks);
            newMaterial += (rand.NextDouble() - 0.5) * 0.1;
            newMaterial += sensorName switch { Sensor1 => -0.1, Sensor2 => 0.0, Sensor3 => +0.1, _ => 0.0 };
            _deviceData[actualKey] = newMaterial;
            return newMaterial;
        }

        // ============================================================
        // 循环器命令(与 PID 版一致)
        // ============================================================

        private async Task WriteCirculatorCommandAsync(double command)
        {
            try
            {
                command = Math.Max(_config.MinCirculatorCommand,
                          Math.Min(_config.MaxCirculatorCommand, command));

                if (_isSimulationMode)
                {
                    _deviceData[$"{_circulatorDeviceId}_CmdTarget"] = command;
                    return;
                }
                string cmd = GetCirculatorTempSetCommand();
                bool ok = await _connectionManager.WriteAsync(_circulatorDeviceId, cmd, command);
                if (!ok) _controlLogger?.LogError($"写循环器目标失败: {cmd} = {command:F2}", null);
            }
            catch (Exception ex)
            {
                _controlLogger?.LogError("写循环器命令异常", ex);
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

        private void LogControlState(Dictionary<string, double> temps,
            double smoothedMaster, double errorToFinal, double jacketActual,
            double jacketSp, Dictionary<string, SafetyAssessment> safety)
        {
            string m1 = _masterSensor == Sensor1 ? "[主]" : "    ";
            string m2 = _masterSensor == Sensor2 ? "[主]" : "    ";
            string m3 = _masterSensor == Sensor3 ? "[主]" : "    ";
            string stableTag = _hasEnteredStable ? "[稳定]" : "";

            _controlLogger?.LogDebug($"周期#{_controlCycleCount}",
                $"阶段:{_currentPhase}{stableTag} | 主控{_masterSensor}:{smoothedMaster:F2}℃\n" +
                $"目标:{_config.TargetTemperature:F2}℃ 误差:{errorToFinal:+0.00;-0.00}℃\n" +
                $"夹套SP(MPC输出):{jacketSp:F2}℃ | 夹套实际:{jacketActual:F2}℃ (差{jacketSp - jacketActual:+0.00;-0.00})\n" +
                $"MPC: 参考={_mpc.LastReference:F2} DOB={_mpc.LastDisturbance:+0.000} 解算={_mpc.LastSolveTimeMs:F1}ms 收敛={_mpc.LastSolveConverged}\n" +
                $"三路: {m1}TT1001:{temps[Sensor1]:F2} {m2}TT1002:{temps[Sensor2]:F2} {m3}TT1003:{temps[Sensor3]:F2}\n" +
                $"安全: {safety[_masterSensor]}");
        }

        public string GetCurrentStateInfo()
        {
            return _currentPhase switch
            {
                ControlPhase.Initializing => "初始化",
                ControlPhase.Adjusting => "调整中(MPC)",
                ControlPhase.Stable => "已稳定",
                ControlPhase.Failed => $"失败:{_failureReason}",
                ControlPhase.Stopped => "已停止",
                _ => "未知"
            };
        }
    }

    // ============================================================
    // MPC 控制器配置(替换 CascadePidConfig 中 MPC 用得到的字段)
    // 保留与安全/稳定/循环器相关的配置项,丢弃 PID 专有项(Kp/Ki/Kd/Ramp/Bleed...)
    // ============================================================
    public class CascadeMpcConfig
    {
        // 工艺
        public double TargetTemperature { get; set; }
        public string MasterSensor { get; set; } = "TT1002";
        public double Tolerance { get; set; } = 3.0;   // 达标判据,默认改为 ±3.0(用户验收标准)

        // 循环器命令上下限(对应原 Safety.Min/MaxCirculatorCommand,也即 MPC 的 UMin/UMax 物理边界)
        public double MinCirculatorCommand { get; set; } = 5.0;
        public double MaxCirculatorCommand { get; set; } = 90.0;

        // 控制周期(强制 10 秒,与 MPC Ts 一致)
        public TimeSpan ReadingInterval { get; set; } = TimeSpan.FromSeconds(10);

        // 稳定确认
        public TimeSpan StableConfirmDuration { get; set; } = TimeSpan.FromSeconds(30);

        // 失控保护
        public double SensorMinValid { get; set; } = -100.0;
        public double SensorMaxValid { get; set; } = 500.0;

        // 安全监测
        public double AlarmDeviationDeg { get; set; } = 10.0;
        public double AlarmDurationSec { get; set; } = 120.0;
        public double MaxRateDegPerMin { get; set; } = 10.0;
        public double TrendLookaheadSec { get; set; } = 120.0;
        public double TrendAlarmDeviationDeg { get; set; } = 15.0;

        // 监测路
        public double MonitorMaxDeviation { get; set; } = 10.0;
    }
}
