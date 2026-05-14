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
    /// 经典串级 PID 温度控制器 (v5.3 - 粘性稳态 + 死区版)
    ///
    /// 相对 v5.2 的改动:
    /// 1. 粘性稳态标记 _hasEverStable: 一旦进过稳态,永久保持 true(直到换目标)
    ///    Ramp 用它判断,失稳后也保持瞄准最终目标,避免 I 项卡死
    /// 2. I 项下限保护 _adaptiveIntegralFloor: 强制衰减不准跌破此值
    ///    避免稳态所需的散热补偿被卸光,导致物料下漂
    /// 3. 死区控制: 进过稳态后,|误差| < DeadBand(默认 0.1℃) 时冻结 I 项
    ///    消除对传感器噪声的反应,稳态不再小幅振荡
    ///
    /// 既有保护(v3~v5.1):
    /// - Setpoint Ramp(虚拟目标): 升温过程零过冲
    /// - 自适应参数: 50/80/120℃ 都用同一套代码
    /// - 强制衰减: 物料过头时把 I 项往 IntegralFloor 拉(温和,0.5~5%/秒)
    /// - 预反积分: 升温阶段接近目标时主动卸 I
    /// - D 项增强: 临门一脚刹得更狠
    /// </summary>
    public class CascadePidController
    {
        // ================ 不可变配置 ================
        private readonly string _deviceId;
        private readonly string _circulatorDeviceId;
        private readonly CascadePidConfig _config;
        private readonly IDeviceConnectionManager _connectionManager;
        private readonly ILogService _logger;
        private readonly SmartControlLogger _controlLogger;
        private readonly bool _isSimulationMode;
        private readonly ConcurrentDictionary<string, double> _deviceData;
        private readonly SystemStabilityStatus _stabilityStatus;

        public const string Sensor1 = "TT1001";
        public const string Sensor2 = "TT1002";
        public const string Sensor3 = "TT1003";

        // ================ 运行时状态 ================
        private CancellationTokenSource _cts;
        private Task _controlTask;
        private volatile bool _isRunning;
        private volatile bool _isPaused;
        private volatile ControlPhase _currentPhase = ControlPhase.Initializing;
        private string _failureReason = "";

        private string _masterSensor;

        // PID 状态
        private double _integralAccum = 0.0;
        private double _previousError = double.NaN;
        private double _previousMasterTemp = double.NaN;
        private DateTime _lastPidTime = DateTime.MinValue;
        private bool _wasOutputClamped = false;
        private int _feedforwardSign = 0;

        // 日志辅助
        private double _lastDTerm = 0.0;
        private double _lastBleedAmount = 0.0;
        private double _lastWorkingTarget = double.NaN;
        private string _lastRampStage = "";

        // 物料温度滑动平均
        private readonly Queue<double> _masterTempHistory = new Queue<double>();
        private const int SmoothingWindow = 3;

        // 稳定性判定
        private DateTime _stableStartTime = DateTime.MinValue;
        private bool _hasEnteredStable = false;
        // [v5.3 新增] 粘性稳态标记: 一旦进过稳态,即使失稳也保持 true
        // 用于让 Ramp 在失稳后继续瞄准最终目标,而不是回到"贴住物料"模式
        private bool _hasEverStable = false;

        // 安全监测
        private readonly Dictionary<string, SafetyMonitor> _safetyMonitors;

        private int _controlCycleCount = 0;
        private double _lastJacketTarget = double.NaN;

        // ================ [v4 新增] 自适应实际参数 ================
        // 这些值在 StartAsync 中根据 targetDelta 计算,而不是直接用 _config 里的"基线值"
        private double _adaptiveRampFarBand;
        private double _adaptiveRampNearBand;
        private double _adaptiveRampMidLead;
        private double _adaptiveRampNearLead;
        private double _adaptivePreBleedMaxRate;
        private double _adaptiveDerivativeBoostFactor;
        // [v5.3 新增] I 项下限保护: 稳态需要的最小 I 项估值
        //   稳态时循环器需要 ~目标+散热补偿,这个补偿由 I 项贡献
        //   根据温差和经验估算: 温差 25℃ → I≈1.8, 温差 55℃ → I≈2.5, 温差 75℃ → I≈3.0
        private double _adaptiveIntegralFloor;
        private double _startMasterTemp;     // 启动时的物料温度
        private double _targetDelta;          // |目标 - 启动温度|
        private double _adaptiveScale;        // 缩放系数 k

        // ================ 公共属性 ================
        public ControlPhase CurrentPhase => _currentPhase;
        public string MasterSensor => _masterSensor;
        public string FailureReason => _failureReason;
        public bool IsSystemStable => CheckStableOR();
        public double LastJacketTarget => _lastJacketTarget;
        public double IntegralAccum => _integralAccum;

        public CascadePidController(
            string deviceId,
            string circulatorDeviceId,
            CascadePidConfig config,
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
        // [v4 核心] 自适应参数计算
        // ============================================================

        /// <summary>
        /// 根据"目标温度 - 起始物料温度"自动调整 Ramp 参数。
        /// 单一驱动变量: targetDelta = |目标 - 起始温度|
        /// 归一化: k = clamp(targetDelta / 50, 0.3, 1.5)
        ///   - 升 50℃(温差 25):   k=0.5,参数温和
        ///   - 升 80℃(温差 55):   k=1.1,参数激进
        ///   - 升 100℃(温差 75):  k=1.5(封顶),参数最激进
        /// </summary>
        private void ComputeAdaptiveParameters(double startTemp)
        {
            _startMasterTemp = startTemp;
            _targetDelta = Math.Abs(_config.TargetTemperature - startTemp);

            // 归一化系数 [0.3, 1.5]
            _adaptiveScale = Math.Clamp(_targetDelta / 50.0, 0.3, 1.5);
            double k = _adaptiveScale;

            // ========= Ramp 阶段切换阈值 =========
            // 温差越大,远段阈值越大 (越早从远段切到中段,提前介入压制)
            // 基线: k=1.0 时 FarBand=6, NearBand=2.5
            _adaptiveRampFarBand = _config.RampFarBand * (0.5 + 0.8 * k);  // k=0.5→0.9× | k=1.1→1.38×
            _adaptiveRampNearBand = _config.RampNearBand * (0.6 + 0.7 * k);  // k=0.5→0.95× | k=1.1→1.37×

            // 不允许中段阈值 >= 远段阈值
            if (_adaptiveRampNearBand >= _adaptiveRampFarBand)
                _adaptiveRampNearBand = _adaptiveRampFarBand * 0.4;

            // ========= 虚拟目标领先量 =========
            // 温差越大,中段领先量应越小 (虚拟目标越贴近物料,压制越强)
            // 反比关系: k 大时 lead 小
            _adaptiveRampMidLead = _config.RampMidLead / Math.Max(k, 0.5);  // k=0.5→2× | k=1.1→0.91×
            _adaptiveRampNearLead = _config.RampNearLead / Math.Max(k, 0.5);  // 同上

            // 钳制下限,避免过小
            _adaptiveRampMidLead = Math.Max(_adaptiveRampMidLead, 0.3);
            _adaptiveRampNearLead = Math.Max(_adaptiveRampNearLead, 0.15);

            // ========= 预反积分卸率 =========
            // 温差越大,卸 I 项要越狠 (因为远段累积的 I 项峰值更大)
            _adaptivePreBleedMaxRate = _config.PreBleedMaxRate * (0.5 + 0.7 * k);

            // ========= D 项放大系数 =========
            // 温差越大,临门一脚的 D 项放大越多
            _adaptiveDerivativeBoostFactor = 1.0 + (_config.DerivativeBoostFactor - 1.0) * (0.6 + 0.5 * k);

            // ========= [v5.3] I 项下限保护 =========
            // 稳态时循环器需要超过物料温度一些(补偿散热),这部分主要由 I 项贡献。
            // 根据经验:温差 50℃ 时稳态 I 项约 2.0,所以 floor = targetDelta * 0.04
            // 取 _targetDelta 的 4% 作为下限。例:温差 25→0.5、55→2.2、75→3.0
            // 强制衰减/预反积分都不准把 I 项卸到这个值之下
            _adaptiveIntegralFloor = _targetDelta * 0.04;
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

            _integralAccum = 0.0;
            _previousError = double.NaN;
            _previousMasterTemp = double.NaN;
            _lastPidTime = DateTime.MinValue;
            _wasOutputClamped = false;
            _hasEnteredStable = false;
            _hasEverStable = false;  // [v5.3]
            _stableStartTime = DateTime.MinValue;
            _feedforwardSign = 0;
            _lastJacketTarget = double.NaN;
            _lastDTerm = 0.0;
            _lastBleedAmount = 0.0;
            _lastWorkingTarget = double.NaN;
            _lastRampStage = "";

            _masterTempHistory.Clear();

            // [v4] 读一次当前物料温度,作为"起始温度"用于自适应计算
            double startTemp;
            try
            {
                startTemp = await ReadOneTemperatureAsync(_masterSensor);
                if (double.IsNaN(startTemp) || startTemp < _config.SensorMinValid || startTemp > _config.SensorMaxValid)
                {
                    // 传感器故障时,假设起始温度 = 室温
                    startTemp = 25.0;
                    _controlLogger?.LogWarning("读取启动温度失败,使用默认 25℃ 作为起始温度");
                }
            }
            catch
            {
                startTemp = 25.0;
            }

            ComputeAdaptiveParameters(startTemp);

            _deviceData[$"{_deviceId}_{Sensor1}_Target"] = _config.TargetTemperature;
            _deviceData[$"{_deviceId}_{Sensor2}_Target"] = _config.TargetTemperature;
            _deviceData[$"{_deviceId}_{Sensor3}_Target"] = _config.TargetTemperature;

            _controlTask = Task.Run(async () => await ControlLoopAsync(_cts.Token));

            _controlLogger?.LogInfo("====== 经典串级 PID 温度控制器启动 (v5.3 粘性稳态+死区版) ======",
                $"目标温度: {_config.TargetTemperature:F2}℃\n" +
                $"起始温度: {_startMasterTemp:F2}℃\n" +
                $"温差: {_targetDelta:F2}℃ → 自适应系数 k={_adaptiveScale:F2}\n" +
                $"容差: ±{_config.Tolerance:F2}℃\n" +
                $"过冲上限: +{_config.AcceptableOvershoot:F2}℃ (物料 ≤ {_config.TargetTemperature + _config.AcceptableOvershoot:F2}℃)\n" +
                $"主控传感器: {_masterSensor}\n" +
                $"循环器: {_circulatorDeviceId}\n" +
                $"夹套油温寄存器: {GetJacketTempRegister()}\n" +
                $"控制周期: {_config.ReadingInterval.TotalSeconds:F0}秒\n" +
                $"PID 参数: Kp={_config.Kp:F3}, Ki(秒)={_config.KiSeconds:F0}, Kd(秒)={_config.KdSeconds:F0}\n" +
                $"前馈偏置: 升温+{_config.FeedforwardBias:F1}℃ / 降温-{_config.FeedforwardBias:F1}℃\n" +
                $"最大偏置: ±{_config.MaxBias:F1}℃\n" +
                $"========== Setpoint Ramp 自适应参数 ==========\n" +
                $"  基线 → 自适应后:\n" +
                $"  RampFarBand:  {_config.RampFarBand:F2} → {_adaptiveRampFarBand:F2}℃\n" +
                $"  RampNearBand: {_config.RampNearBand:F2} → {_adaptiveRampNearBand:F2}℃\n" +
                $"  RampMidLead:  {_config.RampMidLead:F2} → {_adaptiveRampMidLead:F2}℃\n" +
                $"  RampNearLead: {_config.RampNearLead:F2} → {_adaptiveRampNearLead:F2}℃\n" +
                $"  PreBleedMaxRate: {_config.PreBleedMaxRate:F3} → {_adaptivePreBleedMaxRate:F3}/秒\n" +
                $"  D 项放大系数: {_config.DerivativeBoostFactor:F2} → {_adaptiveDerivativeBoostFactor:F2}\n" +
                $"  I 项下限保护: {_adaptiveIntegralFloor:F2} (强制衰减不跌破此值)\n" +
                $"  死区: ±{_config.DeadBand:F2}℃ (此范围内 I 项冻结)\n" +
                $"  钳制硬限: 虚拟目标 ≤ {_config.TargetTemperature + _config.AcceptableOvershoot:F2}℃\n" +
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
            {
                await StopCirculatorAsync();
            }
            else
            {
                _controlLogger?.LogInfo("控制器停止,循环器保持运行(平滑切换)");
            }

            UpdateStabilityStatusForAllSensors(false);

            _controlLogger?.LogInfo("PID 控制器停止",
                $"总周期: {_controlCycleCount}, 末次循环器命令: {_lastJacketTarget:F2}℃, 末次积分: {_integralAccum:F2}");
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

        public async Task<bool> ChangeTargetAsync(double newTarget)
        {
            if (!_isRunning)
            {
                _controlLogger?.LogWarning("ChangeTargetAsync 失败: 控制器未运行");
                return false;
            }

            double oldTarget = _config.TargetTemperature;
            _controlLogger?.LogInfo("====== 平滑切换目标温度 ======",
                $"{oldTarget:F2}℃ → {newTarget:F2}℃\n主控:{_masterSensor},循环器保持运行");

            bool wasPaused = _isPaused;
            _isPaused = true;

            try
            {
                _config.TargetTemperature = newTarget;

                _deviceData[$"{_deviceId}_{Sensor1}_Target"] = newTarget;
                _deviceData[$"{_deviceId}_{Sensor2}_Target"] = newTarget;
                _deviceData[$"{_deviceId}_{Sensor3}_Target"] = newTarget;

                _integralAccum = 0.0;
                _previousError = double.NaN;
                _previousMasterTemp = double.NaN;
                _feedforwardSign = 0;
                _hasEnteredStable = false;
                _hasEverStable = false;  // [v5.3] 切换目标 = 新任务,粘性标记也清零
                _stableStartTime = DateTime.MinValue;
                _masterTempHistory.Clear();
                _lastDTerm = 0.0;
                _lastBleedAmount = 0.0;
                _lastWorkingTarget = double.NaN;
                _lastRampStage = "";

                // [v4] 平滑切换目标时,以"当前物料温度"作为新起点重新自适应
                double curMasterTemp;
                try
                {
                    curMasterTemp = await ReadOneTemperatureAsync(_masterSensor);
                    if (double.IsNaN(curMasterTemp)) curMasterTemp = _startMasterTemp;
                }
                catch
                {
                    curMasterTemp = _startMasterTemp;
                }

                ComputeAdaptiveParameters(curMasterTemp);
                _controlLogger?.LogInfo("[切换目标自适应重算]",
                    $"新起点 {curMasterTemp:F2}℃ → 新温差 {_targetDelta:F2}℃ → 系数 k={_adaptiveScale:F2}");

                foreach (var monitor in _safetyMonitors.Values)
                {
                    monitor.Reset();
                }

                UpdateStabilityStatusForAllSensors(false);

                _currentPhase = ControlPhase.Initializing;
                _failureReason = "";

                _controlLogger?.LogSuccess("目标切换完成,PID 状态已重置");
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
            UpdateFeedforwardSign(errorToFinal);

            // [v3/v4] 计算虚拟目标
            double workingTarget = ComputeWorkingTarget(smoothedMasterTemp, errorToFinal);
            _lastWorkingTarget = workingTarget;

            double workingError = workingTarget - smoothedMasterTemp;
            double pidOutput = ComputePidOutput(workingError, errorToFinal, smoothedMasterTemp, workingTarget);
            _lastJacketTarget = pidOutput;

            await WriteCirculatorCommandAsync(pidOutput);

            UpdateStabilityState(absErrToFinal);
            UpdateStabilityStatusOR();

            if (_controlCycleCount % 5 == 0)
            {
                LogControlState(temps, smoothedMasterTemp, errorToFinal, workingTarget,
                    rawJacketTemp, pidOutput, safetyResults);
            }

            if (_controlCycleCount % 6 == 0)
            {
                LogTuningDiagnosis(errorToFinal, smoothedMasterTemp, workingTarget);
            }
        }

        // ============================================================
        // [v3 核心] Setpoint Ramp:计算虚拟目标 (v4 用自适应参数)
        // ============================================================

        private double ComputeWorkingTarget(double currentTemp, double errorToFinal)
        {
            double finalTarget = _config.TargetTemperature;
            double absErr = Math.Abs(errorToFinal);
            double tolerance = _config.Tolerance;
            double overshootCap = _config.AcceptableOvershoot;

            string stage;
            double workingTarget;

            // [v5 修复] 大原则:物料已经超过最终目标(errorToFinal < 0)时,
            // 虚拟目标永远 = 最终目标(强制工作误差为负,逼 I 项往下走)。
            // 不再用"物料 - NearLead"这种贴住物料的逻辑,因为这会让虚拟目标也在目标上方,
            // 导致工作误差很小、I 项卡住、系统继续加热的死循环。

            // 阶段判定
            if (absErr <= tolerance)
            {
                // 稳态: 物料在容差内
                if (currentTemp <= finalTarget)
                {
                    // 物料 ≤ 目标: 稳态偏置,让物料稳在目标+overshoot/2
                    workingTarget = finalTarget + overshootCap / 2.0;
                }
                else
                {
                    // 物料已 > 目标: 不加正偏置,直接瞄准最终目标
                    workingTarget = finalTarget;
                }
                stage = "稳态";
            }
            else if (absErr > _adaptiveRampFarBand)
            {
                workingTarget = finalTarget;
                stage = "远段";
            }
            else if (absErr > _adaptiveRampNearBand)
            {
                // [v5.3 修复] 用 _hasEverStable(粘性): 进过一次稳态就永久保持瞄准最终目标
                // v5.2 用的 _hasEnteredStable 会因失稳变 false,导致退回"贴物料"模式,I 项卡死
                if (_hasEverStable)
                    workingTarget = finalTarget;
                else if (errorToFinal > 0)
                    workingTarget = currentTemp + _adaptiveRampMidLead;
                else
                    workingTarget = finalTarget;  // 物料过头时,虚拟目标 = 最终目标
                stage = "中段";
            }
            else
            {
                // [v5.3 修复] 同上,用粘性标记
                if (_hasEverStable)
                    workingTarget = finalTarget;
                else if (errorToFinal > 0)
                    workingTarget = currentTemp + _adaptiveRampNearLead;
                else
                    workingTarget = finalTarget;  // 物料过头时,虚拟目标 = 最终目标
                stage = "近段";
            }

            // 硬钳制
            // 升温场景: 虚拟目标 ≤ 最终目标 + 过冲上限
            if (errorToFinal > 0)
            {
                double upperBound = finalTarget + overshootCap;
                workingTarget = Math.Min(workingTarget, upperBound);
            }
            // 已稳态/过头场景: 物料 > 目标时,虚拟目标 ≤ 最终目标(不允许加正偏置)
            else if (currentTemp > finalTarget)
            {
                workingTarget = Math.Min(workingTarget, finalTarget);
            }

            if (stage != _lastRampStage)
            {
                _controlLogger?.LogInfo($"[Ramp 阶段切换] {_lastRampStage}→{stage}",
                    $"物料 {currentTemp:F2}℃, 误差 {errorToFinal:+0.00;-0.00}℃, " +
                    $"虚拟目标 {workingTarget:F2}℃ (最终 {finalTarget:F2}℃)");
                _lastRampStage = stage;
            }

            return workingTarget;
        }

        // ============================================================
        // 前馈方向锁定 + 衰减
        // ============================================================

        private void UpdateFeedforwardSign(double error)
        {
            if (_feedforwardSign == 0)
            {
                if (error > 0.5) _feedforwardSign = +1;
                else if (error < -0.5) _feedforwardSign = -1;
                else _feedforwardSign = 0;
            }
        }

        private double GetEffectiveFeedforward(double errorToFinal)
        {
            if (_feedforwardSign == 0) return 0;

            const double FullScaleThreshold = 5.0;
            double absError = Math.Abs(errorToFinal);
            double scale = Math.Min(1.0, absError / FullScaleThreshold);

            int actualSign = (errorToFinal > 0) ? +1 : (errorToFinal < 0 ? -1 : 0);

            if (actualSign != _feedforwardSign && actualSign != 0)
            {
                return 0;
            }

            return _feedforwardSign * _config.FeedforwardBias * scale;
        }

        // ============================================================
        // PID 核心 (v4: 用自适应参数)
        // ============================================================

        private double ComputePidOutput(double workingError, double errorToFinal,
            double currentMasterTemp, double workingTarget)
        {
            DateTime now = DateTime.Now;
            double dt = _config.ReadingInterval.TotalSeconds;

            if (_lastPidTime != DateTime.MinValue)
            {
                dt = (now - _lastPidTime).TotalSeconds;
                if (dt <= 0 || dt > 60) dt = _config.ReadingInterval.TotalSeconds;
            }
            _lastPidTime = now;

            double kp = _config.Kp;
            double ki = (_config.KiSeconds > 0) ? (kp / _config.KiSeconds) : 0;
            double kd = (_config.KdSeconds > 0) ? (kp * _config.KdSeconds) : 0;

            double absErrFinal = Math.Abs(errorToFinal);

            // P 项
            double pTerm = kp * workingError;

            // D 项 (用自适应 Boost 系数)
            double dTerm = 0;
            double tempChangePerSec = 0;
            if (kd > 0 && !double.IsNaN(_previousMasterTemp))
            {
                tempChangePerSec = (currentMasterTemp - _previousMasterTemp) / dt;
                double baseDTerm = -kd * tempChangePerSec;

                double boostThr = _config.DerivativeBoostThreshold;
                if (absErrFinal < boostThr && boostThr > 0)
                {
                    double t = 1.0 - (absErrFinal / boostThr);
                    double factor = 1.0 + (_adaptiveDerivativeBoostFactor - 1.0) * t;
                    dTerm = baseDTerm * factor;
                }
                else
                {
                    dTerm = baseDTerm;
                }
            }
            _lastDTerm = dTerm;

            // I 项 (用自适应 Bleed Rate)
            _lastBleedAmount = 0;
            if (ki > 0)
            {
                // [v5.3 新增] 死区控制: 进过稳态后,误差在 ±DeadBand 内时完全冻结 I 项
                // 这是消除小幅振荡的关键 — 不让 I 项对传感器噪声做反应
                bool inDeadBand = _hasEverStable && absErrFinal < _config.DeadBand;

                if (!inDeadBand)
                {
                    // [v5 新增,v5.1 调温和] 物料过头时的强制衰减:
                    // 当物料 > 最终目标(errorToFinal < 0)且 I 项还是正的(说明在加热方向),
                    // 主动把 I 项往 0 拉,加速系统从"加热"切换到"刹车"。
                    // v5.3: 衰减不准跌破 _adaptiveIntegralFloor (稳态所需的散热补偿)
                    if (errorToFinal < 0 && _integralAccum > _adaptiveIntegralFloor)
                    {
                        // 过头越多,衰减越狠(但温和)
                        double overshootAmount = -errorToFinal;  // > 0
                        // 每秒衰减比例:过头 0.1℃ → 0.5%/秒, 过头 0.5℃ → 2.5%/秒, 过头 1℃ → 5%/秒(封顶)
                        double forceDecayPerSec = Math.Min(0.05, overshootAmount * 0.05);
                        double decayThisStep = forceDecayPerSec * dt;
                        double oldI = _integralAccum;
                        double afterDecay = _integralAccum * (1.0 - decayThisStep);
                        // [v5.3] 下限保护: 不准衰减到 IntegralFloor 之下
                        _integralAccum = Math.Max(afterDecay, _adaptiveIntegralFloor);
                        _lastBleedAmount += (oldI - _integralAccum);
                    }
                    // 对称: 物料"过冷"(低于目标且 I 项是负的)时,也主动往 0 拉
                    else if (errorToFinal > 0 && _integralAccum < -_adaptiveIntegralFloor)
                    {
                        double undershootAmount = errorToFinal;
                        double forceDecayPerSec = Math.Min(0.05, undershootAmount * 0.05);
                        double decayThisStep = forceDecayPerSec * dt;
                        double oldI = _integralAccum;
                        double afterDecay = _integralAccum * (1.0 - decayThisStep);
                        _integralAccum = Math.Min(afterDecay, -_adaptiveIntegralFloor);
                        _lastBleedAmount += (oldI - _integralAccum);
                    }

                    // 原有的"接近目标快速衰减"
                    // [v5.3] 进过稳态后这条不再生效(系统已经稳过,不需要再激进卸 I)
                    if (!_hasEverStable && absErrFinal < _config.PreBleedThreshold
                        && kd > 0 && !double.IsNaN(_previousMasterTemp))
                    {
                        double approachRate = tempChangePerSec;
                        bool approachingFast =
                            (errorToFinal > 0 && approachRate > _config.PreBleedApproachRate) ||
                            (errorToFinal < 0 && approachRate < -_config.PreBleedApproachRate);

                        if (approachingFast && Math.Abs(_integralAccum) > 0.1)
                        {
                            double proximityFactor = (_config.PreBleedThreshold - absErrFinal) / _config.PreBleedThreshold;
                            double bleedPerSec = _adaptivePreBleedMaxRate * proximityFactor;
                            double bleedThisStep = bleedPerSec * dt;
                            bleedThisStep = Math.Min(bleedThisStep, 0.5);

                            double oldIntegral = _integralAccum;
                            // [v5.3] 卸 I 时也保留 IntegralFloor
                            double afterBleed = _integralAccum * (1.0 - bleedThisStep);
                            _integralAccum = (oldIntegral > 0)
                                ? Math.Max(afterBleed, _adaptiveIntegralFloor)
                                : Math.Min(afterBleed, -_adaptiveIntegralFloor);
                            _lastBleedAmount += (oldIntegral - _integralAccum);
                        }
                    }

                    // 条件积分(原有)
                    if (!_wasOutputClamped)
                    {
                        double absWorkErr = Math.Abs(workingError);
                        double integralScale = Math.Min(1.0, absWorkErr / 1.0);
                        _integralAccum += ki * workingError * dt * integralScale;
                    }
                }
                // else 在死区内: I 项保持不变,不累积也不衰减
            }
            double iTerm = _integralAccum;

            double feedforward = GetEffectiveFeedforward(errorToFinal);

            double totalCommand = workingTarget + feedforward + pTerm + iTerm + dTerm;

            double clampedCommand = ClampCirculatorCommand(totalCommand, out bool wasClampedNow);
            if (wasClampedNow && ki > 0)
            {
                double overshoot = totalCommand - clampedCommand;
                _integralAccum -= overshoot;
            }
            _wasOutputClamped = wasClampedNow;

            _previousError = workingError;
            _previousMasterTemp = currentMasterTemp;

            if (_currentPhase == ControlPhase.Initializing)
            {
                _currentPhase = ControlPhase.Adjusting;
            }

            return clampedCommand;
        }

        private double ClampCirculatorCommand(double rawTarget, out bool wasClamped)
        {
            double minAllowed = _config.TargetTemperature - _config.MaxBias;
            double maxAllowed = _config.TargetTemperature + _config.MaxBias;
            double clamped = Math.Clamp(rawTarget, minAllowed, maxAllowed);
            wasClamped = Math.Abs(clamped - rawTarget) > 0.001;
            return clamped;
        }

        // ============================================================
        // 稳定性判定
        // ============================================================

        private void UpdateStabilityState(double absError)
        {
            bool inTolerance = absError <= _config.Tolerance;

            if (inTolerance)
            {
                if (_stableStartTime == DateTime.MinValue)
                {
                    _stableStartTime = DateTime.Now;
                }

                TimeSpan stableDuration = DateTime.Now - _stableStartTime;
                if (stableDuration >= _config.StableConfirmDuration && !_hasEnteredStable)
                {
                    _hasEnteredStable = true;
                    _hasEverStable = true;  // [v5.3] 粘性标记: 一旦进过稳态永久 true,直到换目标
                    _currentPhase = ControlPhase.Stable;
                    _controlLogger?.LogSuccess("====== 进入稳定状态 ======",
                        $"主控 {_masterSensor} 偏差 {absError:F2}℃ 持续 {stableDuration.TotalSeconds:F0}秒\n" +
                        $"循环器命令 {_lastJacketTarget:F2}℃, 积分项 {_integralAccum:F2}");
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
        // 主控故障切换 + 监测路检查
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
            {
                return _masterTempHistory.Count > 0 ? _masterTempHistory.Average() : rawTemp;
            }
            _masterTempHistory.Enqueue(rawTemp);
            while (_masterTempHistory.Count > SmoothingWindow) _masterTempHistory.Dequeue();
            return _masterTempHistory.Average();
        }

        // ============================================================
        // 温度读取
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
                return GetSimulatedMaterialTemperature(sensorName);
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

        private async Task<double> ReadJacketTemperatureAsync()
        {
            if (_isSimulationMode)
            {
                return SimulateJacketTemperature();
            }

            try
            {
                string register = GetJacketTempRegister();
                return await _connectionManager.ReadAsync<double>(_circulatorDeviceId, register);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"读取夹套油温失败: {_circulatorDeviceId}");
                if (_deviceData.TryGetValue($"{_circulatorDeviceId}_JacketTemp", out double cached))
                {
                    return cached;
                }
                return double.NaN;
            }
        }

        private string GetJacketTempRegister() =>
            _circulatorDeviceId == "TC0101" ? "YY_PV_Real_2" : "YY_PV_Real_4";

        // ============================================================
        // 模拟器
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
            double noise = (rand.NextDouble() - 0.5) * 0.1;
            newJacket += noise;

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
            double noise = (rand.NextDouble() - 0.5) * 0.1;
            newMaterial += noise;

            double sensorBias = sensorName switch
            {
                Sensor1 => -0.1,
                Sensor2 => 0.0,
                Sensor3 => +0.1,
                _ => 0.0
            };
            newMaterial += sensorBias;

            _deviceData[actualKey] = newMaterial;
            return newMaterial;
        }

        // ============================================================
        // 循环器命令
        // ============================================================

        private async Task WriteCirculatorCommandAsync(double command)
        {
            try
            {
                if (_isSimulationMode)
                {
                    _deviceData[$"{_circulatorDeviceId}_CmdTarget"] = command;
                    return;
                }

                string cmd = GetCirculatorTempSetCommand();
                bool ok = await _connectionManager.WriteAsync(_circulatorDeviceId, cmd, command);
                if (!ok)
                {
                    _controlLogger?.LogError($"写循环器目标失败: {cmd} = {command:F2}", null);
                }
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
            double smoothedMaster, double errorToFinal, double workingTarget,
            double jacketActual, double pidOutput, Dictionary<string, SafetyAssessment> safety)
        {
            string m1 = (_masterSensor == Sensor1) ? "[主]" : "    ";
            string m2 = (_masterSensor == Sensor2) ? "[主]" : "    ";
            string m3 = (_masterSensor == Sensor3) ? "[主]" : "    ";

            string clampedTag = _wasOutputClamped ? "(限幅)" : "";
            double effectiveFf = GetEffectiveFeedforward(errorToFinal);
            string bleedTag = _lastBleedAmount > 0.01 ? $" [卸I:-{_lastBleedAmount:F2}]" : "";
            double workingError = workingTarget - smoothedMaster;
            // [v5.3] 显示粘性稳态状态 + 是否在死区
            string stableTag = _hasEverStable ? "[粘性稳态]" : "";
            string deadBandTag = (_hasEverStable && Math.Abs(errorToFinal) < _config.DeadBand) ? "[死区/I冻结]" : "";

            _controlLogger?.LogDebug($"周期#{_controlCycleCount}",
                $"阶段:{_currentPhase}/{_lastRampStage}{stableTag}{deadBandTag} | 主控{_masterSensor}:{smoothedMaster:F2}℃\n" +
                $"最终目标:{_config.TargetTemperature:F2}℃ 误差:{errorToFinal:+0.00;-0.00}℃ | 虚拟目标:{workingTarget:F2}℃ 工作误差:{workingError:+0.00;-0.00}℃\n" +
                $"循环器命令:{pidOutput:F2}℃{clampedTag} | 夹套实际:{jacketActual:F2}℃ (差{(pidOutput - jacketActual):+0.00;-0.00})\n" +
                $"PID: P={_config.Kp * workingError:+0.00;-0.00} I={_integralAccum:+0.00;-0.00}(下限{_adaptiveIntegralFloor:F2}) D={_lastDTerm:+0.00;-0.00} 前馈={effectiveFf:+0.0;-0.0}{bleedTag}\n" +
                $"三路: {m1}TT1001:{temps[Sensor1]:F2} {m2}TT1002:{temps[Sensor2]:F2} {m3}TT1003:{temps[Sensor3]:F2}\n" +
                $"安全: {safety[_masterSensor]}");
        }

        private double _lastDiagnosisError = double.NaN;
        private void LogTuningDiagnosis(double currentErrorToFinal, double currentMaster, double workingTarget)
        {
            string lastErrStr = double.IsNaN(_lastDiagnosisError) ? "NaN" : $"{_lastDiagnosisError:F2}";
            double workingError = workingTarget - currentMaster;

            double pTerm = _config.Kp * workingError;
            double feedforward = GetEffectiveFeedforward(currentErrorToFinal);
            double total = workingTarget + feedforward + pTerm + _integralAccum + _lastDTerm;

            string clampedNote = _wasOutputClamped ? " (限幅,积分已抗饱和)" : "";
            string bleedNote = _lastBleedAmount > 0.01 ? $" [上周期卸I:-{_lastBleedAmount:F2}]" : "";

            string diagnosis;
            if (Math.Abs(currentErrorToFinal) <= _config.Tolerance)
            {
                diagnosis = "已在容差内,系统稳定。当前参数合适";
            }
            else if (!double.IsNaN(_lastDiagnosisError))
            {
                double improvement = Math.Abs(_lastDiagnosisError) - Math.Abs(currentErrorToFinal);
                double improvementRate = improvement / 0.5;

                if (improvement < 0.05 && Math.Abs(currentErrorToFinal) > _config.Tolerance * 2)
                {
                    diagnosis = $"误差卡住({Math.Abs(currentErrorToFinal):F2}℃),系统未收敛";
                }
                else if (improvement < 0)
                {
                    diagnosis = $"误差在扩大({-improvementRate:F2}℃/min),系统朝错误方向";
                }
                else if (improvementRate > 4)
                {
                    diagnosis = $"误差快速收敛中({improvementRate:F1}℃/min),Ramp 在守门";
                }
                else if (improvementRate > 1)
                {
                    diagnosis = $"误差正常收敛({improvementRate:F1}℃/min),参数合适";
                }
                else
                {
                    diagnosis = $"误差缓慢收敛({improvementRate:F2}℃/min),小心翼翼接近目标";
                }
            }
            else
            {
                diagnosis = "首次诊断,等下一周期评估";
            }

            _controlLogger?.LogInfo($"[调参诊断 周期#{_controlCycleCount}]",
                $"Kp={_config.Kp:F3} Ki(秒)={_config.KiSeconds:F0} Kd(秒)={_config.KdSeconds:F0} | 自适应 k={_adaptiveScale:F2}\n" +
                $"对最终目标误差: {currentErrorToFinal:F2}℃ (上次{lastErrStr}℃){bleedNote}\n" +
                $"Ramp 阶段: {_lastRampStage} | 虚拟目标: {workingTarget:F2}℃ | 工作误差: {workingError:+0.00;-0.00}℃\n" +
                $"虚拟目标 {workingTarget:F1} + 前馈 {feedforward:+0.0;-0.0} + P项 {pTerm:+0.00;-0.00} + I项 {_integralAccum:+0.00;-0.00} + D项 {_lastDTerm:+0.00;-0.00} = 循环器命令 {total:F1}℃{clampedNote}\n" +
                $"诊断: {diagnosis}");

            _lastDiagnosisError = currentErrorToFinal;
        }

        public string GetCurrentStateInfo()
        {
            return _currentPhase switch
            {
                ControlPhase.Initializing => "初始化",
                ControlPhase.Adjusting => $"调整中({_lastRampStage})",
                ControlPhase.Stable => "已稳定",
                ControlPhase.Failed => $"失败:{_failureReason}",
                ControlPhase.Stopped => "已停止",
                _ => "未知"
            };
        }
    }

    // ============================================================
    // PID 配置 (v4: 自适应基线参数)
    // ============================================================

    public class CascadePidConfig
    {
        // 工艺参数
        public double TargetTemperature { get; set; }
        public string MasterSensor { get; set; } = "TT1002";
        public double Tolerance { get; set; } = 0.2;

        // PID 三参数
        public double Kp { get; set; } = 0.25;
        public double KiSeconds { get; set; } = 600;
        public double KdSeconds { get; set; } = 60;

        // 前馈
        public double FeedforwardBias { get; set; } = 5.0;

        // 限幅
        public double MaxBias { get; set; } = 30.0;

        // ====== Setpoint Ramp 基线参数(实际值由自适应计算) ======
        // 这些是"基线值",对应温差 50℃(k=1.0)的情况
        // 启动时根据实际温差自动缩放,代码用 _adaptive* 字段

        public double AcceptableOvershoot { get; set; } = 0.2;

        /// <summary>Ramp 远段阈值基线 (k=1.0 时的值)</summary>
        public double RampFarBand { get; set; } = 6.0;

        /// <summary>Ramp 近段阈值基线 (k=1.0 时的值)</summary>
        public double RampNearBand { get; set; } = 2.5;

        /// <summary>中段领先量基线 (k=1.0 时的值)</summary>
        public double RampMidLead { get; set; } = 0.8;

        /// <summary>近段领先量基线 (k=1.0 时的值)</summary>
        public double RampNearLead { get; set; } = 0.2;

        // ====== 预反积分基线参数 ======
        public double PreBleedThreshold { get; set; } = 3.0;
        public double PreBleedApproachRate { get; set; } = 0.05;
        public double PreBleedMaxRate { get; set; } = 0.03;

        // ====== D 项增强基线参数 ======
        public double DerivativeBoostThreshold { get; set; } = 3.0;
        public double DerivativeBoostFactor { get; set; } = 2.5;

        // ====== [v5.3 新增] 死区 ======
        /// <summary>
        /// 死区(℃): 进过稳态后,|误差| < 此值时完全冻结 I 项(不累积也不衰减)
        /// 用于消除稳态附近的小幅振荡(对传感器噪声不做反应)
        /// 默认 0.1: 误差在 ±0.1℃ 内视为"足够好"
        /// </summary>
        public double DeadBand { get; set; } = 0.1;

        // 控制周期
        public TimeSpan ReadingInterval { get; set; } = TimeSpan.FromSeconds(5);

        // 稳定性确认
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

    public enum ControlPhase
    {
        Initializing,
        Adjusting,
        Stable,
        Failed,
        Stopped
    }
}