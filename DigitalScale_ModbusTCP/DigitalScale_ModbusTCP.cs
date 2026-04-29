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

namespace DigitalScale_ModbusTCP
{
    /// <summary>
    /// 电子秤驱动 v9.2
    /// 核心：一阶动态模型 MPC + 增广卡尔曼观测器（Flow/Disturbance/Weight）
    ///       + 流速稳定后自动物理计量标定 Kcoeff
    ///
    /// 状态：
    ///   x = [F(g/min), D(g/min), W(g)]^T
    ///
    /// 物理标定流程（v9.2 新增）：
    ///   1. 流速宣布稳定后触发
    ///   2. Freezing 阶段：冻结泵速，等 1 个周期让管路稳定
    ///   3. Measuring 阶段：记录起始重量，累积 PHYS_CALIB_CYCLES 个周期
    ///   4. Done 阶段：用真实重量差计算实际流量，通过 EMA 修正 Kcoeff
    ///   5. 恢复 MPC 正常控制
    /// </summary>
    [Export(typeof(IDevice))]
    public class DigitalScale_ModbusTCP : Device, IDeviceUIInteraction, IControllableDevice, IFlowLifecycleAware
    {

        private readonly ILogService _logger;

        #region ── 基础状态字段 ───────────────────────────────────────

        private double _currentWeight = 0.0;
        private double _totalWeight = 0.0;
        private double _tareWeight = 0.0;
        private double _initialTotalWeight = 0.0;
        private string _currentUnit = "g";
        private bool _isTared = false;
        private bool _isPoweredOn = false;
        private readonly object _stateLock = new object();

        private readonly List<DeviceActiveState> _activeStates = new List<DeviceActiveState>();
        private readonly List<DeviceActiveState> _pausedStates = new List<DeviceActiveState>();
        private readonly object _statesLock = new object();

        private string _scaleId = "S0101";
        private CancellationTokenSource _statusPollingCts;
        private Task _statusPollingTask;
        private const int WEIGHT_POLLING_MS = 500;

        #endregion

        #region ── 流速控制核心字段 ───────────────────────────────────

        private double _targetFlowRate = 0.0;
        private double _estimatedFlowRate = 0.0;
        private bool _flowControlEnabled = false;
        private readonly object _flowControlLock = new object();

        private CancellationTokenSource _flowControlCts;
        private Task _flowControlTask;
        private TaskCompletionSource<bool> _flowStabilizedTcs;

        // 增广观测器状态 x = [F, D, W]
        private double[] _obsX = new double[3];
        private double[,] _obsP = new double[3, 3];
        private bool _observerInitialized = false;

        // 模型 / 观测器参数
        private const double OBS_Q_FLOW_BASE = 0.08;
        private const double OBS_Q_DIST_BASE = 0.02;
        private const double OBS_Q_WEIGHT_BASE = 0.005;
        private const double OBS_R_MEAS = 0.25;

        // MPC 参数
        private const int MPC_N = 8;
        private const double MPC_Q_FLOW = 18.0;
        private const double MPC_Q_FLOW_TERM = 24.0;
        private const double MPC_R_CTRL = 1.2;
        private const double MPC_R_ABS = 0.02;
        private const double MPC_DU_RATE_MAX = 0.35;
        private const double MPC_W_MIN = 100.0;

        // 自适应参数（旧路径保留，主要标定走物理标定）
        private const double ADAPTIVE_ALPHA_PRE = 0.02;
        private const double ADAPTIVE_ALPHA = 0.08;

        // 稳定判据
        private const double TOLERANCE_PCT = 2.5;
        private const int STABLE_REQUIRED = 3;
        private const double HYSTERESIS_PCT = 5.0;
        private const double STABLE_SIGMA_F_MAX = 0.45;
        private const double STABLE_DU_MAX = 0.3;

        private const int MAX_PUMP_SPEED = 100;
        private const double LOW_WEIGHT_WARN_G = 100.0;
        // ═══════════════════════════════════════════════════════
        //  单位常量集中定义,与监控图表的轴分类对齐
        //   - g/min 自动归属"流量轴"
        //   - 其他单位(g, RPM, s, %, ...)归"未分类",不绘制在图表上
        // ═══════════════════════════════════════════════════════
        private const string UnitGram = "g";
        private const string UnitFlowRate = "g/min";
        private const string UnitPumpSpeed = "RPM";
        private const string UnitTimeSec = "s";
        private const string UnitPercent = "%";
        private const string UnitKcoeff = "(g/min)/RPM";
        // 运行状态
        private double _lastPumpSpeed = 0.0;
        private bool _stabilityDeclared = false;
        private int _stableCount = 0;
        private int _cycleCount = 0;
        private double _adaptiveCoeff = 0.0;
        private string _controlPhase = "Idle";
        private DateTime _controlStartTime = DateTime.Now;
        private DateTime _lastFlowSimReadTime = DateTime.Now;

        private double _disturbanceEstimate = 0.0;
        private double[] _lastMpcPlan = new double[MPC_N];

        // 仿真
        private readonly Random _simRandom = new Random();
        private double _simFlowRate = 0.0;
        private double _simDisturbance = 0.0;

        // v9.1
        private const double DIST_LEAK_TAU_SEC = 300.0;
        private const int FD_WINDOW_POINTS = 4;
        private const double MAX_EFFECTIVE_DT_SEC = 30.0;

        private readonly Queue<(DateTime TsUtc, double Weight)> _rawFlowWindow
            = new Queue<(DateTime TsUtc, double Weight)>();

        private double? _rawFiniteDiffFlow = null;
        private DateTime _lastControlSampleTime = DateTime.MinValue;

        // ── v9.2 物理标定状态机 ──────────────────────────────────────
        private enum PhysCalibState { Idle, Freezing, Measuring, Done }

        private PhysCalibState _physCalibState = PhysCalibState.Idle;
        private double _physCalibFrozenSpeed = 0.0;
        private double _physCalibWeightStart = 0.0;
        private DateTime _physCalibTimeStart = DateTime.MinValue;
        private int _physCalibCyclesLeft = 0;

        // 物理标定参数
        private const int PHYS_CALIB_CYCLES = 6;     // 标定持续周期数（6×10s=60s）
        private const double PHYS_CALIB_ALPHA = 0.30;  // EMA 步长
        private const double PHYS_CALIB_MAX_DELTA_PCT = 20.0;  // 单次修正幅度上限(%)
        private const double PHYS_CALIB_MIN_FLOW = 2.0;   // 最低有效流量 g/min

        #endregion

        #region ── 流速日志 ───────────────────────────────────────────

        private StreamWriter _flowLogWriter = null;
        private string _flowLogFilePath = null;
        private readonly object _flowLogLock = new object();
        private const string FLOW_LOG_SUBDIR = "Logs\\FlowControl";

        private void StartFlowLogFile(double targetFlow, int samplingMs, string pumpId, double coeff, double tauSec)
        {
            CloseFlowLogFile();
            try
            {
                string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FLOW_LOG_SUBDIR);
                Directory.CreateDirectory(dir);

                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _flowLogFilePath = Path.Combine(dir,
                    $"FlowControl_{_scaleId}_{ts}_{targetFlow:F2}gmin.log");

                _flowLogWriter = new StreamWriter(_flowLogFilePath, false, new UTF8Encoding(false))
                {
                    AutoFlush = true
                };

                string border = new string('═', 190);
                _flowLogWriter.WriteLine(border);
                _flowLogWriter.WriteLine("  电子秤流速控制日志  v9.2（自动物理标定 Kcoeff）");
                _flowLogWriter.WriteLine($"  秤 ID                : {_scaleId}");
                _flowLogWriter.WriteLine($"  目标流速             : {targetFlow:F2} g/min");
                _flowLogWriter.WriteLine($"  采样间隔(名义)       : {samplingMs / 1000.0:F0} s");
                _flowLogWriter.WriteLine($"  Kcoeff(有效)         : {coeff:F4} (g/min)/RPM");
                _flowLogWriter.WriteLine($"  模型时间常数 τ       : {tauSec:F1} s");
                _flowLogWriter.WriteLine($"  扰动泄漏时间常数 τd  : {DIST_LEAK_TAU_SEC:F1} s");
                _flowLogWriter.WriteLine($"  观测器 R_meas        : {OBS_R_MEAS}");
                _flowLogWriter.WriteLine($"  MPC 步长 N           : {MPC_N}");
                _flowLogWriter.WriteLine($"  MPC Qf / Qf_term     : {MPC_Q_FLOW} / {MPC_Q_FLOW_TERM}");
                _flowLogWriter.WriteLine($"  MPC Rdu / Ru         : {MPC_R_CTRL} / {MPC_R_ABS}");
                _flowLogWriter.WriteLine($"  Δu 速率上限          : {MPC_DU_RATE_MAX} RPM/s");
                _flowLogWriter.WriteLine($"  稳定判据             : |e_est|≤{TOLERANCE_PCT}% 且 |e_fd|≤{TOLERANCE_PCT}% 且 σF≤{STABLE_SIGMA_F_MAX} 且 |Δu|≤{STABLE_DU_MAX}");
                _flowLogWriter.WriteLine($"  滞回阈值             : {HYSTERESIS_PCT}%");
                _flowLogWriter.WriteLine($"  物理标定             : 稳定后冻结泵速 {PHYS_CALIB_CYCLES} 个周期，EMA α={PHYS_CALIB_ALPHA}");
                _flowLogWriter.WriteLine($"  低料告警             : {LOW_WEIGHT_WARN_G} g");
                _flowLogWriter.WriteLine($"  开始时间             : {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                _flowLogWriter.WriteLine(border);
                _flowLogWriter.WriteLine();

                _flowLogWriter.WriteLine(
                    $"{"时间",-13} {"周期",-5} {"dt(s)",-7} {"W_raw",-10} {"W_est",-10} {"F_est",-12} {"F_fd",-12} " +
                    $"{"σF",-8} {"D_est",-10} {"目标",-8} {"e_est%",-8} {"e_fd%",-8} {"u_ref",-8} {"Δu",-8} {"泵速",-8} {"判稳(E/F/S/U)",-15} {"动作"}");
                _flowLogWriter.WriteLine(new string('-', 205));

                FlowInfoLog($"[流速日志] 已创建: {_flowLogFilePath}");
            }
            catch (Exception ex)
            {
                WarnLog($"[流速日志] 创建失败: {ex.Message}");
                _flowLogWriter = null;
            }
        }

        private void WriteFlowLogRow(
            int cycle, double dtSec, double wRaw, double wEst,
            double fEst, double? fFd, double sigmaF, double dEst,
            double target, double errorEstPct, double? errorFdPct,
            double uRef, double deltaU, double pumpSpeed,
            string stableFlags, string action)
        {
            lock (_flowLogLock)
            {
                if (_flowLogWriter == null) return;
                try
                {
                    string timeText = DateTime.Now.ToString("HH:mm:ss.fff");
                    string fFdText = fFd.HasValue ? fFd.Value.ToString("F4") : "-";
                    string eFdText = errorFdPct.HasValue ? errorFdPct.Value.ToString("F3") : "-";

                    _flowLogWriter.WriteLine(
                        $"{timeText,-13} {cycle,-5} {dtSec,-7:F2} {wRaw,-10:F3} {wEst,-10:F3} {fEst,-12:F4} {fFdText,-12} " +
                        $"{sigmaF,-8:F4} {dEst,-10:F4} {target,-8:F2} {errorEstPct,-8:F3} {eFdText,-8} {uRef,-8:F2} " +
                        $"{deltaU,-8:F3} {pumpSpeed,-8:F1} {stableFlags,-15} {action}");
                }
                catch { }
            }
        }

        private void CloseFlowLogFile(double finalFlow = 0, double finalSpeed = 0, int cycles = 0, double totalSec = 0)
        {
            double? rawFd;
            lock (_flowControlLock) { rawFd = _rawFiniteDiffFlow; }

            lock (_flowLogLock)
            {
                if (_flowLogWriter == null) return;
                try
                {
                    string border = new string('═', 190);
                    _flowLogWriter.WriteLine();
                    _flowLogWriter.WriteLine(border);
                    _flowLogWriter.WriteLine("  流速控制汇总 v9.2");
                    _flowLogWriter.WriteLine($"  结束时间           : {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                    _flowLogWriter.WriteLine($"  总运行时长         : {totalSec:F1} s / {cycles} 周期");
                    _flowLogWriter.WriteLine($"  最终估算流速       : {finalFlow:F4} g/min");
                    _flowLogWriter.WriteLine($"  最终差分流速       : {(rawFd.HasValue ? rawFd.Value.ToString("F4") : "N/A")} g/min");
                    _flowLogWriter.WriteLine($"  最终泵速           : {finalSpeed:F1} RPM");
                    _flowLogWriter.WriteLine($"  稳定已声明         : {_stabilityDeclared}");
                    _flowLogWriter.WriteLine($"  自适应系数         : {_adaptiveCoeff:F4} (g/min)/RPM");
                    _flowLogWriter.WriteLine($"  最终扰动估计       : {_disturbanceEstimate:+0.0000;-0.0000} g/min");
                    _flowLogWriter.WriteLine($"  物理标定状态       : {_physCalibState}");
                    _flowLogWriter.WriteLine(border);

                    _flowLogWriter.Flush();
                    _flowLogWriter.Dispose();
                    _flowLogWriter = null;

                    InfoLog($"[流速日志] 已关闭: {_flowLogFilePath}");
                }
                catch
                {
                    try { _flowLogWriter?.Dispose(); } catch { }
                    _flowLogWriter = null;
                }
            }
        }

        private void FlowInfoLog(string msg)
        {
            InfoLog(msg);
            lock (_flowLogLock)
            {
                try { _flowLogWriter?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}][INFO ] {msg}"); } catch { }
            }
        }

        private void FlowWarnLog(string msg)
        {
            WarnLog(msg);
            lock (_flowLogLock)
            {
                try { _flowLogWriter?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}][WARN ] {msg}"); } catch { }
            }
        }

        private void FlowErrorLog(string msg, Exception ex = null)
        {
            ErrorLog(msg, ex);
            lock (_flowLogLock)
            {
                try
                {
                    _flowLogWriter?.WriteLine(
                        $"[{DateTime.Now:HH:mm:ss.fff}][ERROR] {msg}" +
                        (ex != null ? $" | {ex.GetType().Name}: {ex.Message}" : ""));
                }
                catch { }
            }
        }

        private void FlowDebugLog(string msg)
        {
            DebugLog(msg);
            lock (_flowLogLock)
            {
                try { _flowLogWriter?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}][DEBUG] {msg}"); } catch { }
            }
        }

        #endregion

        #region ── IDeviceUIInteraction ────────────────────────────────

        public event EventHandler<DeviceControlStateChangedEventArgs> ControlStateChanged;

        public async Task<CommandExecutionResult> ExecuteUICommandAsync(
            string commandName,
            Dictionary<string, object> parameters = null)
        {
            try
            {
                InfoLog($"收到UI命令: {commandName}");
                switch (commandName)
                {
                    case "开机": return await PowerOnAsync();
                    case "关机": return await PowerOffAsync();
                    case "去皮": return await TareAsync();
                    case "取消去皮": return await CancelTareAsync();
                    case "切换单位":
                        if (parameters == null || !parameters.ContainsKey("TargetUnit"))
                            return new CommandExecutionResult { Success = false, Message = "缺少目标单位参数" };
                        return await SwitchUnitAsync(parameters["TargetUnit"]);
                    default:
                        return new CommandExecutionResult { Success = false, Message = $"未识别的命令: {commandName}" };
                }
            }
            catch (Exception ex)
            {
                ErrorLog($"执行UI命令失败: {commandName}", ex);
                return new CommandExecutionResult { Success = false, Message = ex.Message, Exception = ex };
            }
        }

        public override DeviceState GetCurrentState()
        {
            double estimated, target;
            bool enabled;
            double? rawFd;

            lock (_flowControlLock)
            {
                estimated = _estimatedFlowRate;
                target = _targetFlowRate;
                enabled = _flowControlEnabled;
                rawFd = _rawFiniteDiffFlow;
            }

            double fEst = _observerInitialized ? _obsX[0] : 0.0;
            double dEst = _observerInitialized ? _obsX[1] : 0.0;
            double wEst = _observerInitialized ? _obsX[2] : 0.0;
            double fSigma = _observerInitialized ? Math.Sqrt(Math.Max(0.0, _obsP[0, 0])) : 0.0;

            return new DeviceState
            {
                States = new Dictionary<string, object>
                {
                    ["IsPoweredOn"] = _isPoweredOn,
                    ["WeightValue"] = _currentWeight,
                    ["CurrentUnit"] = _currentUnit,
                    ["IsTared"] = _isTared,
                    ["TareValue"] = _tareWeight,
                    ["TotalWeight"] = _totalWeight,
                    ["EstimatedFlowRate"] = estimated,
                    ["TargetFlowRate"] = target,
                    ["FlowControlEnabled"] = enabled,
                    ["ControlPhase"] = _controlPhase,
                    ["StableCount"] = _stableCount,
                    ["StabilityDeclared"] = _stabilityDeclared,
                    ["AdaptiveCoeff"] = _adaptiveCoeff,
                    ["CycleCount"] = _cycleCount,
                    ["KF_FlowEstimate"] = fEst,
                    ["KF_DistEstimate"] = dEst,
                    ["KF_WeightEstimate"] = wEst,
                    ["KF_FlowSigma"] = fSigma,
                    ["KF_Initialized"] = _observerInitialized,
                    ["RawFiniteDiffFlow"] = rawFd ?? 0.0,
                    ["RawFiniteDiffFlowValid"] = rawFd.HasValue,
                    ["DOB_DisturbanceEst"] = _disturbanceEstimate,
                    ["PhysCalibState"] = _physCalibState.ToString(),
                }
            };
        }

        #endregion

        #region ── UI 命令内部实现 ────────────────────────────────────

        private async Task<CommandExecutionResult> PowerOnAsync()
        {
            try
            {
                RaiseControlStateChanged("CommandState_开机", CommandExecutionState.Running);
                await Task.Delay(300);
                lock (_stateLock) { _isPoweredOn = true; }
                RaiseControlStateChanged("IsPoweredOn", true);
                RaiseControlStateChanged("CommandState_开机", CommandExecutionState.Succeeded);
                InfoLog("电子秤已开机");
                return new CommandExecutionResult
                {
                    Success = true,
                    Message = "开机成功",
                    OutputData = new Dictionary<string, object> { ["IsPoweredOn"] = true, ["Timestamp"] = DateTime.Now }
                };
            }
            catch (Exception ex)
            {
                ErrorLog($"开机失败: {ex.Message}");
                return new CommandExecutionResult { Success = false, Message = ex.Message, Exception = ex };
            }
        }

        private async Task<CommandExecutionResult> PowerOffAsync()
        {
            try
            {
                RaiseControlStateChanged("CommandState_关机", CommandExecutionState.Running);
                await Task.Delay(300);
                lock (_stateLock) { _isPoweredOn = false; }
                RaiseControlStateChanged("IsPoweredOn", false);
                RaiseControlStateChanged("CommandState_关机", CommandExecutionState.Succeeded);
                InfoLog("电子秤已关机");
                return new CommandExecutionResult
                {
                    Success = true,
                    Message = "关机成功",
                    OutputData = new Dictionary<string, object> { ["IsPoweredOn"] = false, ["Timestamp"] = DateTime.Now }
                };
            }
            catch (Exception ex)
            {
                ErrorLog($"关机失败: {ex.Message}");
                return new CommandExecutionResult { Success = false, Message = ex.Message, Exception = ex };
            }
        }

        private async Task<CommandExecutionResult> TareAsync()
        {
            try
            {
                RaiseControlStateChanged("CommandState_去皮", CommandExecutionState.Running);
                double currentWeight = 0;
                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp
                    && ConnectionManager != null && !IsSimulationMode)
                    currentWeight = await ConnectionManager.ReadAsync<double>(ComId, $"_{_scaleId}_g");
                else if (IsSimulationMode)
                    lock (_stateLock) { currentWeight = _currentWeight; }

                lock (_stateLock) { _tareWeight = currentWeight; _isTared = true; }

                RaiseControlStateChanged("IsTared", true);
                RaiseControlStateChanged("CommandState_去皮", CommandExecutionState.Succeeded);
                AddActiveState($"ScaleTared_{_scaleId}", "去皮", "取消去皮",
                    stateData: new ScaleStateData { TareValue = _tareWeight, ScaleId = _scaleId });
                InfoLog($"电子秤已去皮 - 去皮值:{_tareWeight}g");

                return new CommandExecutionResult
                {
                    Success = true,
                    Message = "去皮成功",
                    OutputData = new Dictionary<string, object>
                    { ["TareValue"] = _tareWeight, ["IsTared"] = true, ["Timestamp"] = DateTime.Now }
                };
            }
            catch (Exception ex)
            {
                ErrorLog($"去皮失败: {ex.Message}");
                return new CommandExecutionResult { Success = false, Message = ex.Message, Exception = ex };
            }
        }

        private async Task<CommandExecutionResult> CancelTareAsync()
        {
            try
            {
                await Task.Delay(50);
                lock (_stateLock) { _tareWeight = 0; _isTared = false; }
                RaiseControlStateChanged("IsTared", false);
                RemoveActiveState($"ScaleTared_{_scaleId}");
                InfoLog("电子秤已取消去皮");
                return new CommandExecutionResult
                {
                    Success = true,
                    Message = "取消去皮成功",
                    OutputData = new Dictionary<string, object> { ["IsTared"] = false, ["Timestamp"] = DateTime.Now }
                };
            }
            catch (Exception ex)
            {
                ErrorLog($"取消去皮失败: {ex.Message}");
                return new CommandExecutionResult { Success = false, Message = ex.Message, Exception = ex };
            }
        }

        private async Task<CommandExecutionResult> SwitchUnitAsync(object targetUnit)
        {
            try
            {
                await Task.Delay(50);
                string newUnit = targetUnit switch
                {
                    int i => i switch { 0 => "g", 1 => "kg", 2 => "lb", 3 => "oz", _ => "g" },
                    WeightUnit w => GetUnitString(w),
                    string s => s,
                    _ => "g"
                };
                lock (_stateLock) { _currentUnit = newUnit; }
                RaiseControlStateChanged("CurrentUnit", newUnit);
                InfoLog($"单位已切换为: {newUnit}");
                return new CommandExecutionResult
                {
                    Success = true,
                    Message = $"单位已切换为 {newUnit}",
                    OutputData = new Dictionary<string, object> { ["CurrentUnit"] = newUnit, ["Timestamp"] = DateTime.Now }
                };
            }
            catch (Exception ex)
            {
                ErrorLog($"切换单位失败: {ex.Message}");
                return new CommandExecutionResult { Success = false, Message = ex.Message, Exception = ex };
            }
        }

        #endregion

        #region ── 重量状态轮询 ───────────────────────────────────────

        private void StartWeightPolling()
        {
            try
            {
                if (_statusPollingTask != null && !_statusPollingTask.IsCompleted)
                    StopWeightPollingAsync().Wait(TimeSpan.FromSeconds(2));

                _statusPollingCts = new CancellationTokenSource();
                _statusPollingTask = Task.Run(() => WeightPollingLoopAsync(_statusPollingCts.Token));
                InfoLog("重量轮询已启动");
            }
            catch (Exception ex) { ErrorLog("启动重量轮询失败", ex); }
        }

        private async Task StopWeightPollingAsync()
        {
            try
            {
                if (_statusPollingCts == null) return;
                _statusPollingCts.Cancel();
                if (_statusPollingTask != null)
                    await Task.WhenAny(_statusPollingTask, Task.Delay(2000));
                _statusPollingCts.Dispose();
                _statusPollingCts = null;
                _statusPollingTask = null;
                InfoLog("重量轮询已停止");
            }
            catch (Exception ex) { ErrorLog("停止重量轮询失败", ex); }
        }

        private async Task WeightPollingLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    double w = 0, total = 0;
                    if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp
                        && ConnectionManager != null && !IsSimulationMode)
                    {
                        w = await ConnectionManager.ReadAsync<double>(_scaleId, $"_{_scaleId}_g");
                        total = await ConnectionManager.ReadAsync<double>(_scaleId, $"_{_scaleId}_Add_g");
                    }
                    else if (IsSimulationMode)
                    {
                        lock (_stateLock) { w = _currentWeight; total = _totalWeight; }
                    }

                    bool changed = false;
                    lock (_stateLock)
                    {
                        if (Math.Abs(_currentWeight - w) >= 0.01 || Math.Abs(_totalWeight - total) > 0.01)
                        {
                            _currentWeight = w; _totalWeight = total; changed = true;
                        }
                    }
                    if (changed) RaiseControlStateChanged("WeightValue", w);
                    await Task.Delay(WEIGHT_POLLING_MS, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    ErrorLog($"重量轮询异常: {ex.Message}");
                    try { await Task.Delay(2000, ct); } catch { break; }
                }
            }
        }

        #endregion

        #region ── 流速控制 启动 / 停止 ──────────────────────────────

        private void StartFlowControl(
            double targetFlowRate, int samplingIntervalMs,
            string pumpDeviceId, double initialPumpSpeed)
        {
            lock (_flowControlLock)
            {
                if (_flowControlEnabled)
                {
                    FlowInfoLog("[FC] 检测到已有控制任务，先取消...");
                    _flowControlCts?.Cancel();
                    _flowControlTask?.Wait(1000);
                }

                _targetFlowRate = targetFlowRate;
                _lastPumpSpeed = initialPumpSpeed;
                _stableCount = 0;
                _stabilityDeclared = false;
                _cycleCount = 0;
                _controlStartTime = DateTime.Now;
                _controlPhase = "MPC_Control";

                _observerInitialized = false;
                _disturbanceEstimate = 0.0;
                ResetRawFiniteDiffEstimator();
                _lastControlSampleTime = DateTime.MinValue;

                _lastMpcPlan = Enumerable.Repeat(initialPumpSpeed, MPC_N).ToArray();

                // v9.2：重置物理标定状态机
                _physCalibState = PhysCalibState.Idle;
                _physCalibFrozenSpeed = 0.0;
                _physCalibWeightStart = 0.0;
                _physCalibTimeStart = DateTime.MinValue;
                _physCalibCyclesLeft = 0;

                _flowStabilizedTcs = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                _flowControlEnabled = true;
                _flowControlCts = new CancellationTokenSource();
                _flowControlTask = Task.Run(() => FlowControlLoopAsync(
                    _flowControlCts.Token, samplingIntervalMs, pumpDeviceId));
            }
        }

        private async Task StopFlowControlAsync()
        {
            CancellationTokenSource cts = null;
            Task task = null;

            lock (_flowControlLock)
            {
                if (!_flowControlEnabled) return;
                _flowControlEnabled = false;
                cts = _flowControlCts;
                task = _flowControlTask;
                _flowControlCts = null;
                _flowControlTask = null;
            }

            try
            {
                _flowStabilizedTcs?.TrySetCanceled();
                cts?.Cancel();
                if (task != null) await Task.WhenAny(task, Task.Delay(3000));
                cts?.Dispose();

                double totalSec = (DateTime.Now - _controlStartTime).TotalSeconds;
                FlowInfoLog(
                    $"[FC] 已停止 | 运行:{totalSec:F1}s | 周期:{_cycleCount} | " +
                    $"最终流速:{_estimatedFlowRate:F4}g/min | 最终泵速:{_lastPumpSpeed:F1}RPM");

                CloseFlowLogFile(_estimatedFlowRate, _lastPumpSpeed, _cycleCount, totalSec);
                _controlPhase = "Idle";
            }
            catch (Exception ex) { FlowErrorLog($"停止流速控制异常: {ex.Message}", ex); }
        }

        #endregion

        #region ── 原始重量差分流速 F_fd ─────────────────────────────

        private void ResetRawFiniteDiffEstimator()
        {
            lock (_flowControlLock)
            {
                _rawFlowWindow.Clear();
                _rawFiniteDiffFlow = null;
            }
        }

        private double? UpdateRawFiniteDiffFlow(DateTime tsUtc, double weight)
        {
            lock (_flowControlLock)
            {
                _rawFlowWindow.Enqueue((tsUtc, weight));
                while (_rawFlowWindow.Count > FD_WINDOW_POINTS)
                    _rawFlowWindow.Dequeue();

                if (_rawFlowWindow.Count < 2)
                {
                    _rawFiniteDiffFlow = null;
                    return null;
                }

                var pts = _rawFlowWindow.ToArray();
                var first = pts[0];
                var last = pts[pts.Length - 1];

                double dtMin = (last.TsUtc - first.TsUtc).TotalMinutes;
                if (dtMin <= 1e-9) { _rawFiniteDiffFlow = null; return null; }

                _rawFiniteDiffFlow = Math.Max(0.0, (first.Weight - last.Weight) / dtMin);
                return _rawFiniteDiffFlow;
            }
        }

        private static string BoolFlag(bool value) => value ? "Y" : "N";

        #endregion

        #region ── 一阶动态模型 / 增广观测器 ─────────────────────────

        private double GetFlowTimeConstantSec()
        {
            try { return Math.Clamp(Parameters.GetValue<double>("FlowTimeConstantSec"), 3.0, 300.0); }
            catch { return 15.0; }
        }

        private static void BuildDiscreteModel(
            double dtSec, double tauSec, double distLeakTauSec, double kcoeff,
            out double[,] A, out double[] B)
        {
            dtSec = Math.Max(dtSec, 1e-3);
            tauSec = Math.Max(tauSec, 1e-3);
            distLeakTauSec = Math.Max(distLeakTauSec, 1e-3);
            kcoeff = Math.Max(kcoeff, 1e-6);

            double a = Math.Exp(-dtSec / tauSec);
            double rho = Math.Exp(-dtSec / distLeakTauSec);

            double cD, intDMin;
            if (Math.Abs(distLeakTauSec - tauSec) < 1e-6)
            {
                cD = (dtSec / tauSec) * a;
                intDMin = tauSec * (1.0 - (1.0 + dtSec / tauSec) * a) / 60.0;
            }
            else
            {
                cD = distLeakTauSec / (distLeakTauSec - tauSec) * (rho - a);
                intDMin = distLeakTauSec / (distLeakTauSec - tauSec) *
                          (distLeakTauSec * (1.0 - rho) - tauSec * (1.0 - a)) / 60.0;
            }

            double intFMin = tauSec * (1.0 - a) / 60.0;
            double intUMin = (dtSec - tauSec * (1.0 - a)) / 60.0;

            A = new double[3, 3];
            B = new double[3];

            A[0, 0] = a; A[0, 1] = cD; A[0, 2] = 0.0;
            A[1, 0] = 0.0; A[1, 1] = rho; A[1, 2] = 0.0;
            A[2, 0] = -intFMin; A[2, 1] = -intDMin; A[2, 2] = 1.0;

            B[0] = kcoeff * (1.0 - a);
            B[1] = 0.0;
            B[2] = -kcoeff * intUMin;
        }

        private void ObserverInit(double initialWeight, double initialFlowEstimate)
        {
            _obsX[0] = Math.Max(0.0, initialFlowEstimate);
            _obsX[1] = 0.0;
            _obsX[2] = Math.Max(0.0, initialWeight);

            Clear3x3(_obsP);
            _obsP[0, 0] = 9.0;
            _obsP[1, 1] = 4.0;
            _obsP[2, 2] = Math.Max(1.0, OBS_R_MEAS * 4.0);

            _observerInitialized = true;
            _disturbanceEstimate = 0.0;
        }

        private void ObserverUpdate(double measuredWeight, double uApplied, double dtSec, double kcoeff)
        {
            double tauSec = GetFlowTimeConstantSec();
            BuildDiscreteModel(dtSec, tauSec, DIST_LEAK_TAU_SEC, kcoeff, out double[,] A, out double[] B);

            double[] xPred = Mul3x3Vec(A, _obsX);
            for (int i = 0; i < 3; i++) xPred[i] += B[i] * uApplied;

            double[,] AP = Mul3x3(A, _obsP);
            double[,] PPred = Mul3x3ByTranspose(AP, A);

            double qScale = Math.Max(dtSec / 60.0, 1e-4);
            PPred[0, 0] += OBS_Q_FLOW_BASE * qScale;
            PPred[1, 1] += OBS_Q_DIST_BASE * qScale;
            PPred[2, 2] += OBS_Q_WEIGHT_BASE * qScale;

            double S = PPred[2, 2] + OBS_R_MEAS;
            if (Math.Abs(S) < 1e-12) S = 1e-12;

            double k0 = PPred[0, 2] / S;
            double k1 = PPred[1, 2] / S;
            double k2 = PPred[2, 2] / S;

            double innovation = measuredWeight - xPred[2];

            _obsX[0] = Math.Max(0.0, xPred[0] + k0 * innovation);
            _obsX[1] = xPred[1] + k1 * innovation;
            _obsX[2] = Math.Max(0.0, xPred[2] + k2 * innovation);

            double[,] PNew = new double[3, 3];
            double[] K = { k0, k1, k2 };
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    PNew[i, j] = PPred[i, j] - K[i] * PPred[2, j];

            Symmetrize3x3(PNew);
            for (int i = 0; i < 3; i++)
                PNew[i, i] = Math.Max(PNew[i, i], 1e-9);

            _obsP = PNew;
            _disturbanceEstimate = _obsX[1];
        }

        #endregion

        #region ── 真动态 MPC ────────────────────────────────────────

        private double SolveDynamicMpc(double targetFlow, double uPrev, double kcoeff, double dtSec)
        {
            try
            {
                if (!_observerInitialized) return RoundPumpSpeed(uPrev);

                double tauSec = GetFlowTimeConstantSec();
                BuildDiscreteModel(dtSec, tauSec, DIST_LEAK_TAU_SEC, kcoeff,
                    out double[,] A, out double[] B);

                double[] x0 = new double[3];
                Array.Copy(_obsX, x0, 3);

                BuildPredictionMatrices(A, B, x0, MPC_N,
                    out double[,] gFlow, out double[] fBase,
                    out double[,] gWeight, out double[] wBase);

                double dEst = x0[1];
                double dPersistMid = dEst * Math.Exp(-(0.5 * MPC_N * dtSec) / DIST_LEAK_TAU_SEC);
                double uSteady = Math.Clamp(
                    (targetFlow - dPersistMid) / Math.Max(kcoeff, 1e-6),
                    0.0, (double)MAX_PUMP_SPEED);

                double dtMin = dtSec / 60.0;
                BuildQuadraticCost(gFlow, fBase, targetFlow, uPrev, uSteady, dtMin,
                    out double[,] H, out double[] g);

                double duMax = Math.Max(0.2, MPC_DU_RATE_MAX * dtSec);
                double[] u = BuildWarmStart(uPrev, uSteady, duMax);
                double currentCost = EvaluateQuadraticCost(u, H, g);
                double lipschitz = EstimateLipschitz(H);
                double alphaBase = 0.9 / Math.Max(lipschitz, 1e-6);

                for (int iter = 0; iter < 40; iter++)
                {
                    double[] grad = ComputeGradient(H, g, u);
                    if (NormInf(grad) < 1e-4) break;

                    bool improved = false;
                    double step = alphaBase;
                    for (int ls = 0; ls < 8; ls++)
                    {
                        double[] cand = new double[MPC_N];
                        for (int i = 0; i < MPC_N; i++) cand[i] = u[i] - step * grad[i];
                        cand = ProjectControlSequence(cand, uPrev, duMax);
                        double candCost = EvaluateQuadraticCost(cand, H, g);
                        if (candCost <= currentCost - 1e-8)
                        {
                            u = cand; currentCost = candCost; improved = true; break;
                        }
                        step *= 0.5;
                    }
                    if (!improved) break;
                }

                if (x0[2] < MPC_W_MIN * 1.5)
                {
                    double safetyRatio = Math.Clamp(
                        (x0[2] - MPC_W_MIN) / (MPC_W_MIN * 0.5), 0.0, 1.0);
                    for (int i = 0; i < u.Length; i++) u[i] *= safetyRatio;
                    u = ProjectControlSequence(u, uPrev, duMax);
                }

                double minPredWeight = PredictMinWeight(gWeight, wBase, u);
                if (minPredWeight < MPC_W_MIN)
                {
                    double scale = Math.Clamp(
                        (x0[2] - MPC_W_MIN) / Math.Max(1.0, MPC_W_MIN * 0.6), 0.0, 1.0);
                    u[0] *= scale;
                    u = ProjectControlSequence(u, uPrev, duMax);
                }

                _lastMpcPlan = (double[])u.Clone();
                return RoundPumpSpeed(u[0]);
            }
            catch (Exception ex)
            {
                FlowWarnLog($"[MPC] 求解失败，回退到上一步泵速: {ex.Message}");
                return RoundPumpSpeed(uPrev);
            }
        }

        private double[] BuildWarmStart(double uPrev, double uSteady, double duMax)
        {
            double[] guess = new double[MPC_N];
            if (_lastMpcPlan != null && _lastMpcPlan.Length == MPC_N)
            {
                for (int i = 0; i < MPC_N - 1; i++) guess[i] = _lastMpcPlan[i + 1];
                guess[MPC_N - 1] = _lastMpcPlan[MPC_N - 1];
            }
            else
            {
                for (int i = 0; i < MPC_N; i++) guess[i] = uSteady;
            }
            for (int i = 0; i < MPC_N; i++) guess[i] = 0.5 * guess[i] + 0.5 * uSteady;
            return ProjectControlSequence(guess, uPrev, duMax);
        }

        private static void BuildPredictionMatrices(
            double[,] A, double[] B, double[] x0, int horizon,
            out double[,] gFlow, out double[] fBase,
            out double[,] gWeight, out double[] wBase)
        {
            gFlow = new double[horizon, horizon];
            gWeight = new double[horizon, horizon];
            fBase = new double[horizon];
            wBase = new double[horizon];

            double[][,] powers = new double[horizon + 1][,];
            powers[0] = Identity3x3();
            for (int i = 1; i <= horizon; i++)
                powers[i] = Mul3x3(powers[i - 1], A);

            for (int i = 0; i < horizon; i++)
            {
                fBase[i] = DotRowVec(powers[i + 1], 0, x0);
                wBase[i] = DotRowVec(powers[i + 1], 2, x0);
                for (int j = 0; j <= i; j++)
                {
                    double[] influence = Mul3x3Vec(powers[i - j], B);
                    gFlow[i, j] = influence[0];
                    gWeight[i, j] = influence[2];
                }
            }
        }

        private static void BuildQuadraticCost(
            double[,] gFlow, double[] fBase, double targetFlow,
            double uPrev, double uSteady, double tsMin,
            out double[,] H, out double[] g)
        {
            int n = gFlow.GetLength(1);
            H = new double[n, n];
            g = new double[n];

            double qStage = MPC_Q_FLOW * Math.Max(tsMin, 1e-4);
            double qTerm = MPC_Q_FLOW_TERM;
            double rMove = MPC_R_CTRL / Math.Max(tsMin, 1e-4);
            double rAbs = MPC_R_ABS;

            for (int k = 0; k < n; k++)
            {
                double qk = qStage + (k == n - 1 ? qTerm : 0.0);
                double errBase = fBase[k] - targetFlow;
                for (int i = 0; i < n; i++)
                {
                    g[i] += 2.0 * qk * gFlow[k, i] * errBase;
                    for (int j = 0; j < n; j++)
                        H[i, j] += 2.0 * qk * gFlow[k, i] * gFlow[k, j];
                }
            }

            double[,] E = BuildMoveMatrix(n);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    double sum = 0.0;
                    for (int k = 0; k < n; k++) sum += E[k, i] * E[k, j];
                    H[i, j] += 2.0 * rMove * sum;
                }

            g[0] += -2.0 * rMove * uPrev;
            for (int i = 0; i < n; i++) { H[i, i] += 2.0 * rAbs; g[i] += -2.0 * rAbs * uSteady; }
        }

        private static double[,] BuildMoveMatrix(int n)
        {
            double[,] e = new double[n, n];
            if (n <= 0) return e;
            e[0, 0] = 1.0;
            for (int i = 1; i < n; i++) { e[i, i - 1] = -1.0; e[i, i] = 1.0; }
            return e;
        }

        private static double EstimateLipschitz(double[,] h)
        {
            int n = h.GetLength(0);
            double maxRowSum = 0.0;
            for (int i = 0; i < n; i++)
            {
                double rowSum = 0.0;
                for (int j = 0; j < n; j++) rowSum += Math.Abs(h[i, j]);
                if (rowSum > maxRowSum) maxRowSum = rowSum;
            }
            return Math.Max(maxRowSum, 1e-6);
        }

        private static double[] ComputeGradient(double[,] h, double[] g, double[] u)
        {
            int n = u.Length;
            double[] grad = new double[n];
            for (int i = 0; i < n; i++)
            {
                double s = g[i];
                for (int j = 0; j < n; j++) s += h[i, j] * u[j];
                grad[i] = s;
            }
            return grad;
        }

        private static double EvaluateQuadraticCost(double[] u, double[,] h, double[] g)
        {
            int n = u.Length;
            double cost = 0.0;
            for (int i = 0; i < n; i++)
            {
                double hu = 0.0;
                for (int j = 0; j < n; j++) hu += h[i, j] * u[j];
                cost += 0.5 * u[i] * hu + g[i] * u[i];
            }
            return cost;
        }

        private static double[] ProjectControlSequence(double[] seq, double uPrev, double duMax)
        {
            double[] u = (double[])seq.Clone();
            double prev = uPrev;
            for (int i = 0; i < u.Length; i++)
            {
                double low = Math.Max(0.0, prev - duMax);
                double high = Math.Min((double)MAX_PUMP_SPEED, prev + duMax);
                if (high < low) high = low;
                u[i] = Math.Clamp(u[i], low, high);
                prev = u[i];
            }
            return u;
        }

        private static double PredictMinWeight(double[,] gWeight, double[] wBase, double[] u)
        {
            int n = wBase.Length;
            double minW = double.MaxValue;
            for (int i = 0; i < n; i++)
            {
                double w = wBase[i];
                for (int j = 0; j < n; j++) w += gWeight[i, j] * u[j];
                if (w < minW) minW = w;
            }
            return minW;
        }

        #endregion

        #region ── 泵速取整 ───────────────────────────────────────────

        private static double RoundPumpSpeed(double speed)
            => Math.Round(speed * 10.0, MidpointRounding.AwayFromZero) / 10.0;

        #endregion

        #region ── 流速控制主循环 v9.2 ───────────────────────────────

        private async Task FlowControlLoopAsync(CancellationToken ct, int samplingIntervalMs, string pumpDeviceId)
        {
            double targetFlow;
            lock (_flowControlLock) { targetFlow = _targetFlowRate; }

            double pumpCoeff = Parameters.GetValue<double>("PumpSpeedPerFlowRate");
            double effectiveCoeff = _adaptiveCoeff > 0.01 ? _adaptiveCoeff : pumpCoeff;
            double tauSec = GetFlowTimeConstantSec();

            FlowInfoLog("[MPC] ▶ v9.2 控制循环启动（自动物理标定 Kcoeff）");
            FlowInfoLog($"[MPC]   目标流速       : {targetFlow:F2} g/min");
            FlowInfoLog($"[MPC]   采样间隔       : {samplingIntervalMs / 1000.0:F0} s");
            FlowInfoLog($"[MPC]   Kcoeff         : {effectiveCoeff:F4} (g/min)/RPM");
            FlowInfoLog($"[MPC]   时间常数 τ     : {tauSec:F1} s");
            FlowInfoLog($"[MPC]   扰动泄漏 τd    : {DIST_LEAK_TAU_SEC:F1} s");
            FlowInfoLog($"[MPC]   初始泵速       : {_lastPumpSpeed:F1} RPM");
            FlowInfoLog($"[MPC]   物理标定       : 稳定后冻结 {PHYS_CALIB_CYCLES} 周期，α={PHYS_CALIB_ALPHA}");

            try
            {
                _controlPhase = "Observer_Init";
                double w0 = await ReadWeightAsync();
                double f0 = _lastPumpSpeed > 1e-6 ? _lastPumpSpeed * effectiveCoeff : 0.0;
                ObserverInit(w0, f0);

                ResetRawFiniteDiffEstimator();
                _lastControlSampleTime = DateTime.UtcNow;
                UpdateRawFiniteDiffFlow(_lastControlSampleTime, w0);

                FlowInfoLog($"[MPC] 观测器初始化完成: W0={w0:F3}g  F0={f0:F4}g/min");
                _controlPhase = "MPC_Control";
                FlowInfoLog("[MPC] ══ 进入真动态 MPC 主循环");

                DateTime nextDueUtc = _lastControlSampleTime;

                while (!ct.IsCancellationRequested && _flowControlEnabled)
                {
                    nextDueUtc = nextDueUtc.AddMilliseconds(samplingIntervalMs);
                    TimeSpan wait = nextDueUtc - DateTime.UtcNow;

                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait, ct);
                    else if (wait < TimeSpan.FromMilliseconds(-samplingIntervalMs))
                        nextDueUtc = DateTime.UtcNow;

                    if (!_flowControlEnabled) break;

                    _cycleCount++;
                    double uPrev = _lastPumpSpeed;

                    // ── 读重量 ──────────────────────────────────────────────────
                    double currWeight;
                    try { currWeight = await ReadWeightAsync(); }
                    catch (Exception ex)
                    {
                        FlowErrorLog($"[MPC|C{_cycleCount:D3}] 读重量失败，跳过: {ex.Message}");
                        continue;
                    }

                    DateTime nowUtc = DateTime.UtcNow;
                    double rawDtSec = _lastControlSampleTime == DateTime.MinValue
                        ? samplingIntervalMs / 1000.0
                        : (nowUtc - _lastControlSampleTime).TotalSeconds;

                    double dtSec = Math.Clamp(rawDtSec, 1e-3, MAX_EFFECTIVE_DT_SEC);
                    _lastControlSampleTime = nowUtc;

                    double elapsedSec = (DateTime.Now - _controlStartTime).TotalSeconds;

                    if (currWeight > 0 && currWeight < LOW_WEIGHT_WARN_G)
                        FlowWarnLog($"[MPC|C{_cycleCount:D3}] ⚠ 原料不足！当前:{currWeight:F1}g");

                    // ── 观测器更新 ───────────────────────────────────────────────
                    ObserverUpdate(currWeight, uPrev, dtSec, effectiveCoeff);

                    double? fFd = UpdateRawFiniteDiffFlow(nowUtc, currWeight);
                    double fEst = _obsX[0];
                    double dEst = _obsX[1];
                    double wEst = _obsX[2];
                    double fSigma = Math.Sqrt(Math.Max(0.0, _obsP[0, 0]));
                    _disturbanceEstimate = dEst;

                    lock (_flowControlLock) { _estimatedFlowRate = fEst; }
                    RaiseControlStateChanged("EstimatedFlowRate", fEst);

                    lock (_flowControlLock) { targetFlow = _targetFlowRate; }
                    if (targetFlow <= 0) continue;

                    // ── 预学习 Kcoeff（物理标定前的粗学习，保留原逻辑） ───────────
                    if (_cycleCount > 3 && uPrev > 1.0 && !_stabilityDeclared
                        && _physCalibState == PhysCalibState.Idle)
                    {
                        double correctedFlow = fEst - dEst;
                        if (correctedFlow > 0.5 && fSigma < 1.0)
                        {
                            double measKcoeff = correctedFlow / uPrev;
                            if (measKcoeff > pumpCoeff * 0.3 && measKcoeff < pumpCoeff * 3.0)
                            {
                                double errorPctForLearn = targetFlow > 1e-6
                                    ? Math.Abs(targetFlow - fEst) / targetFlow * 100.0 : 0.0;

                                if (errorPctForLearn < 15.0)
                                {
                                    double oldCoeff = effectiveCoeff;
                                    _adaptiveCoeff = ADAPTIVE_ALPHA_PRE * measKcoeff
                                                    + (1.0 - ADAPTIVE_ALPHA_PRE) * oldCoeff;
                                    effectiveCoeff = _adaptiveCoeff;
                                    FlowDebugLog(
                                        $"[A9|预学习] Kcoeff: {oldCoeff:F4}→{_adaptiveCoeff:F4} " +
                                        $"(实测={measKcoeff:F4} correctedF={correctedFlow:F4} u={uPrev:F1})");
                                }
                            }
                        }
                    }

                    // ── 物理标定状态机 ───────────────────────────────────────────
                    // 在标定期间强制使用冻结泵速，跳过 MPC 求解
                    bool isCalibCycle = false;
                    double newSpeed;

                    switch (_physCalibState)
                    {
                        case PhysCalibState.Freezing:
                            {
                                // 等 1 个周期让管路从 MPC 动态调整恢复稳态
                                isCalibCycle = true;
                                newSpeed = _physCalibFrozenSpeed;
                                _physCalibCyclesLeft--;

                                FlowInfoLog(
                                    $"[CALIB|C{_cycleCount:D3}] Freezing 阶段，" +
                                    $"冻结泵速={newSpeed:F1}RPM，剩余等待={_physCalibCyclesLeft}周期");

                                if (_physCalibCyclesLeft <= 0)
                                {
                                    // 进入 Measuring 阶段，记录起始重量
                                    _physCalibState = PhysCalibState.Measuring;
                                    _physCalibWeightStart = currWeight;
                                    _physCalibTimeStart = nowUtc;
                                    _physCalibCyclesLeft = PHYS_CALIB_CYCLES;

                                    FlowInfoLog(
                                        $"[CALIB|C{_cycleCount:D3}] ▶ 进入 Measuring 阶段  " +
                                        $"W_start={_physCalibWeightStart:F3}g  " +
                                        $"将累积 {PHYS_CALIB_CYCLES} 个周期（≈{PHYS_CALIB_CYCLES * samplingIntervalMs / 1000}s）");
                                    RaiseControlStateChanged("CalibrationState", "Measuring");
                                }
                                break;
                            }

                        case PhysCalibState.Measuring:
                            {
                                // 持续冻结泵速，等待计量周期结束
                                isCalibCycle = true;
                                newSpeed = _physCalibFrozenSpeed;
                                _physCalibCyclesLeft--;

                                double measuredDtMin = (nowUtc - _physCalibTimeStart).TotalMinutes;
                                double midFlow = measuredDtMin > 1e-9
                                    ? (_physCalibWeightStart - currWeight) / measuredDtMin : 0;

                                FlowInfoLog(
                                    $"[CALIB|C{_cycleCount:D3}] Measuring 周期 " +
                                    $"{PHYS_CALIB_CYCLES - _physCalibCyclesLeft}/{PHYS_CALIB_CYCLES}  " +
                                    $"W={currWeight:F3}g  消耗={_physCalibWeightStart - currWeight:F3}g  " +
                                    $"瞬时流速≈{midFlow:F2}g/min  剩余={_physCalibCyclesLeft}周期");

                                if (_physCalibCyclesLeft <= 0)
                                {
                                    // 标定周期结束，计算实测流量和 Kcoeff
                                    _physCalibState = PhysCalibState.Done;

                                    double calibDtMin = (nowUtc - _physCalibTimeStart).TotalMinutes;
                                    double consumed = _physCalibWeightStart - currWeight;
                                    double measuredFlow = calibDtMin > 1e-9 ? consumed / calibDtMin : 0;
                                    double measuredKcoeff = _physCalibFrozenSpeed > 1e-6
                                        ? measuredFlow / _physCalibFrozenSpeed : 0;

                                    double oldKcoeff = effectiveCoeff;
                                    double errorPct = targetFlow > 1e-6
                                        ? (measuredFlow - targetFlow) / targetFlow * 100.0 : 0;
                                    double changePct = oldKcoeff > 1e-6
                                        ? Math.Abs(measuredKcoeff - oldKcoeff) / oldKcoeff * 100.0 : 100.0;

                                    FlowInfoLog(
                                        $"[CALIB|C{_cycleCount:D3}] ══ 物理标定结果 ══");
                                    FlowInfoLog(
                                        $"[CALIB] W_start={_physCalibWeightStart:F3}g  " +
                                        $"W_end={currWeight:F3}g  消耗={consumed:F3}g");
                                    FlowInfoLog(
                                        $"[CALIB] 计量时长={calibDtMin * 60:F1}s  " +
                                        $"实测流量={measuredFlow:F4}g/min  目标={targetFlow:F2}g/min  " +
                                        $"误差={errorPct:+0.00;-0.00}%");
                                    FlowInfoLog(
                                        $"[CALIB] 泵速={_physCalibFrozenSpeed:F1}RPM  " +
                                        $"实测Kcoeff={measuredKcoeff:F4}  旧Kcoeff={oldKcoeff:F4}  " +
                                        $"变化幅度={changePct:F2}%");

                                    // 置信度检查
                                    bool valid = measuredFlow > PHYS_CALIB_MIN_FLOW
                                              && consumed > 0
                                              && measuredKcoeff > 0
                                              && changePct <= PHYS_CALIB_MAX_DELTA_PCT;

                                    if (valid)
                                    {
                                        // EMA 更新
                                        double newKcoeff = PHYS_CALIB_ALPHA * measuredKcoeff
                                                         + (1.0 - PHYS_CALIB_ALPHA) * oldKcoeff;

                                        _adaptiveCoeff = newKcoeff;
                                        effectiveCoeff = newKcoeff;

                                        // 写回设备参数，下次启动直接使用
                                        var p = Parameters.Variables
                                            .OfType<NumberParameter>()
                                            .FirstOrDefault(x => x.Name == "PumpSpeedPerFlowRate");
                                        if (p != null) p.Value = newKcoeff;

                                        FlowInfoLog(
                                            $"[CALIB]  Kcoeff 已修正: {oldKcoeff:F4} → {newKcoeff:F4}  " +
                                            $"(EMA α={PHYS_CALIB_ALPHA}  实测={measuredKcoeff:F4})");

                                        RaiseControlStateChanged("AdaptiveCoeff", newKcoeff);
                                        RaiseControlStateChanged("CalibrationState", "Done");
                                    }
                                    else
                                    {
                                        FlowWarnLog(
                                            $"[CALIB] ⚠ 置信度检查未通过，Kcoeff 未修改  " +
                                            $"(flow={measuredFlow:F2} consumed={consumed:F2} " +
                                            $"kcoeff={measuredKcoeff:F4} changePct={changePct:F1}%)");
                                        RaiseControlStateChanged("CalibrationState", "Failed");
                                    }

                                    FlowInfoLog("[CALIB] 物理标定结束，恢复 MPC 正常控制");
                                }
                                break;
                            }

                        default:
                            {
                                // Idle / Done：正常 MPC 求解
                                newSpeed = SolveDynamicMpc(targetFlow, uPrev, effectiveCoeff, dtSec);
                                break;
                            }
                    }

                    double deltaU = newSpeed - uPrev;

                    // ── 误差计算 ─────────────────────────────────────────────────
                    double error = targetFlow - fEst;
                    double errorEstPct = Math.Abs(targetFlow) > 1e-6
                        ? Math.Abs(error / targetFlow) * 100.0 : 0.0;
                    double? errorFdPct = (fFd.HasValue && targetFlow > 1e-6)
                        ? Math.Abs((targetFlow - fFd.Value) / targetFlow) * 100.0
                        : (double?)null;

                    double dPersistMid = dEst * Math.Exp(-(0.5 * MPC_N * dtSec) / DIST_LEAK_TAU_SEC);
                    double uRef = Math.Clamp(
                        (targetFlow - dPersistMid) / Math.Max(effectiveCoeff, 1e-6),
                        0.0, (double)MAX_PUMP_SPEED);

                    string action = isCalibCycle ? "CALIB    " :
                        errorEstPct <= TOLERANCE_PCT / 2.0 ? "DEAD_ZONE" :
                        errorEstPct <= TOLERANCE_PCT ? "IN_TOL   " :
                        fEst > targetFlow ? "OVERSHOOT" :
                        "RAMPUP   ";

                    if (!_stabilityDeclared) _controlPhase = action.Trim();

                    // ── 稳定判据 ─────────────────────────────────────────────────
                    bool stableByEstimator = errorEstPct <= TOLERANCE_PCT;
                    bool stableByFiniteDiff = errorFdPct.HasValue && errorFdPct.Value <= TOLERANCE_PCT;
                    bool stableBySigma = fSigma <= STABLE_SIGMA_F_MAX;
                    bool stableByDeltaU = Math.Abs(deltaU) <= STABLE_DU_MAX;

                    // 标定期间不参与稳定判断，避免冻结泵速后 deltaU=0 干扰计数
                    bool stableThisCycle = !isCalibCycle
                        && stableByEstimator
                        && stableByFiniteDiff
                        && stableBySigma
                        && stableByDeltaU;

                    string stableFlags =
                        $"{BoolFlag(stableByEstimator)}/" +
                        $"{(errorFdPct.HasValue ? BoolFlag(stableByFiniteDiff) : "-")}/" +
                        $"{BoolFlag(stableBySigma)}/" +
                        $"{BoolFlag(stableByDeltaU)}";

                    string fFdText = fFd.HasValue ? fFd.Value.ToString("F4") : "-";
                    string eFdText = errorFdPct.HasValue ? errorFdPct.Value.ToString("F2") : "-";

                    FlowInfoLog(
                        $"[MPC|C{_cycleCount:D3}|{action}|T+{elapsedSec,6:F1}s] " +
                        $"dt={dtSec,5:F2}s  W_raw={currWeight,9:F3}g  W_est={wEst,9:F3}g  " +
                        $"F_est={fEst,9:F4}±{fSigma:F4}g/min  F_fd={fFdText,9}g/min  " +
                        $"D_est={dEst:+0.0000;-0.0000}  " +
                        $"Tgt={targetFlow:F2}  e_est={error:+0.0000;-0.0000}({errorEstPct,6:F2}%)  " +
                        $"e_fd={eFdText,6}%  u_ref={uRef:F2}  Δu={deltaU:+0.000;-0.000}  " +
                        $"Pump:{uPrev:F1}→{newSpeed:F1}RPM  Kcoeff:{effectiveCoeff:F4}  " +
                        $"Stbl:{_stableCount}/{STABLE_REQUIRED} [{stableFlags}]  " +
                        $"Calib:{_physCalibState}");

                    WriteFlowLogRow(
                        _cycleCount, dtSec, currWeight, wEst, fEst, fFd, fSigma, dEst,
                        targetFlow, errorEstPct, errorFdPct, uRef, deltaU, newSpeed,
                        stableFlags, action);

                    // ── 写泵速 ───────────────────────────────────────────────────
                    bool ok = await SetPumpSpeedAsync(pumpDeviceId, newSpeed);
                    if (!ok)
                    {
                        FlowErrorLog($"[MPC|C{_cycleCount:D3}] 泵速写入失败，保持 {uPrev:F1}RPM");
                        continue;
                    }
                    _lastPumpSpeed = newSpeed;
                    RaiseControlStateChanged("PumpSpeed", newSpeed);

                    // ── 稳定状态机 ───────────────────────────────────────────────
                    if (stableThisCycle)
                    {
                        _stableCount++;

                        // 首次宣布稳定
                        if (_stableCount >= STABLE_REQUIRED && !_stabilityDeclared)
                        {
                            _stabilityDeclared = true;
                            _controlPhase = "Stable";
                            double totalTime = (DateTime.Now - _controlStartTime).TotalSeconds;

                            FlowInfoLog(
                                $"[MPC|C{_cycleCount:D3}|STABLE|T+{elapsedSec:F1}s] " +
                                $"✓ 流速已稳定！总耗时:{totalTime:F1}s  " +
                                $"F̂={fEst:F4}g/min  F_fd={fFdText}g/min  " +
                                $"Tgt={targetFlow:F2}g/min  " +
                                $"Ratio={fEst / Math.Max(targetFlow, 1e-6) * 100:F2}%  " +
                                $"泵速:{newSpeed:F1}RPM  D̂={dEst:+0.0000;-0.0000}  判稳:{stableFlags}");

                            _flowStabilizedTcs?.TrySetResult(true);
                            RaiseControlStateChanged("FlowStabilized", true);

                            // ── 触发物理标定 ──────────────────────────────────
                            _physCalibState = PhysCalibState.Freezing;
                            _physCalibFrozenSpeed = newSpeed;
                            _physCalibCyclesLeft = 1; // 等 1 个周期让管路稳定

                            FlowInfoLog(
                                $"[CALIB|C{_cycleCount:D3}] ▶ 物理标定已触发  " +
                                $"冻结泵速={_physCalibFrozenSpeed:F1}RPM  " +
                                $"等 1 个周期后开始计量...");
                            RaiseControlStateChanged("CalibrationState", "Freezing");
                        }
                        // 已稳定且标定完成后，持续小步学习（轻量 EMA，权重很小）
                        else if (_stabilityDeclared && _physCalibState == PhysCalibState.Done)
                        {
                            if (newSpeed > 0.5 && fSigma < 0.35)
                            {
                                // 用 F_fd（物理差分）而非 fEst 来持续微调，避免观测器偏差累积
                                if (fFd.HasValue && fFd.Value > PHYS_CALIB_MIN_FLOW)
                                {
                                    double measKcoeff = fFd.Value / newSpeed;
                                    double oldCoeff = effectiveCoeff;

                                    // 用很小的步长持续微调（0.02），不影响稳定性
                                    _adaptiveCoeff = 0.02 * measKcoeff + 0.98 * oldCoeff;
                                    effectiveCoeff = _adaptiveCoeff;

                                    var p = Parameters.Variables.OfType<NumberParameter>()
                                        .FirstOrDefault(x => x.Name == "PumpSpeedPerFlowRate");
                                    if (p != null) p.Value = _adaptiveCoeff;

                                    FlowDebugLog(
                                        $"[A9|持续微调] Kcoeff: {oldCoeff:F4}→{_adaptiveCoeff:F4} " +
                                        $"(F_fd={fFd.Value:F4} u={newSpeed:F1})");
                                }
                            }
                        }
                    }
                    else
                    {
                        // 标定期间不重置稳定计数
                        if (isCalibCycle) continue;

                        bool withinDualHysteresis =
                            errorEstPct < HYSTERESIS_PCT &&
                            (!errorFdPct.HasValue || errorFdPct.Value < HYSTERESIS_PCT);

                        if (_stabilityDeclared && withinDualHysteresis)
                        {
                            FlowDebugLog(
                                $"[A9-滞回] e_est={errorEstPct:F2}% " +
                                $"e_fd={(errorFdPct.HasValue ? errorFdPct.Value.ToString("F2") : "-")}% " +
                                $"∈滞回区间，保持稳定");
                        }
                        else
                        {
                            if (_stableCount > 0)
                            {
                                FlowInfoLog(
                                    $"[MPC|C{_cycleCount:D3}] 偏离稳定条件，计数重置: {_stableCount}→0 " +
                                    $"(e_est={errorEstPct:F2}% " +
                                    $"e_fd={(errorFdPct.HasValue ? errorFdPct.Value.ToString("F2") : "-")}% " +
                                    $"σF={fSigma:F3} Δu={Math.Abs(deltaU):F3} 判稳={stableFlags})");
                            }
                            _stableCount = 0;

                            if (_stabilityDeclared)
                            {
                                _stabilityDeclared = false;
                                _controlPhase = "MPC_Control";
                                FlowWarnLog(
                                    $"[MPC|C{_cycleCount:D3}] ⚡ 超出滞回阈值，退出稳定状态 " +
                                    $"(e_est={errorEstPct:F2}% " +
                                    $"e_fd={(errorFdPct.HasValue ? errorFdPct.Value.ToString("F2") : "-")}% " +
                                    $"判稳={stableFlags})");

                                // 退出稳定后重置标定状态，下次重新稳定时再标定
                                if (_physCalibState != PhysCalibState.Measuring)
                                {
                                    _physCalibState = PhysCalibState.Idle;
                                    FlowInfoLog("[CALIB] 退出稳定，标定状态重置为 Idle");
                                }
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                FlowInfoLog("[MPC] 控制循环已被取消");
            }
            catch (Exception ex)
            {
                FlowErrorLog("[MPC] 控制循环异常退出", ex);
            }
            finally
            {
                _flowStabilizedTcs?.TrySetCanceled();
                _controlPhase = "Idle";

                double totalSec = (DateTime.Now - _controlStartTime).TotalSeconds;
                FlowInfoLog(
                    $"[MPC] ■ 线程退出  总运行:{totalSec:F1}s  周期:{_cycleCount}  " +
                    $"最终流速:{_estimatedFlowRate:F4}g/min  最终泵速:{_lastPumpSpeed:F1}RPM  " +
                    $"F_fd:{(_rawFiniteDiffFlow.HasValue ? _rawFiniteDiffFlow.Value.ToString("F4") : "-")}  " +
                    $"自适应Kcoeff:{_adaptiveCoeff:F4}  扰动估计:{_disturbanceEstimate:+0.0000;-0.0000}  " +
                    $"物理标定:{_physCalibState}");
            }
        }

        #endregion

        #region ── 辅助：读重量 / 写泵速 ─────────────────────────────

        private async Task<double> ReadWeightAsync()
        {
            if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp
                && ConnectionManager != null && !IsSimulationMode)
                return await ConnectionManager.ReadAsync<double>(_scaleId, $"_{_scaleId}_g");

            if (IsSimulationMode)
            {
                lock (_stateLock)
                {
                    if (_currentWeight <= 0) _currentWeight = 5000.0;

                    var now = DateTime.Now;
                    double elapsedMin = Math.Max((now - _lastFlowSimReadTime).TotalMinutes, 1e-6);
                    _lastFlowSimReadTime = now;

                    double kcoeff = _adaptiveCoeff > 0.01
                        ? _adaptiveCoeff : Parameters.GetValue<double>("PumpSpeedPerFlowRate");
                    double tauSec = GetFlowTimeConstantSec();
                    double tauMin = Math.Max(tauSec / 60.0, 1e-6);
                    double a = Math.Exp(-elapsedMin / tauMin);
                    double oneMa = 1.0 - a;
                    double tauEff = tauMin * oneMa;
                    double steady = Math.Max(0.0, elapsedMin - tauEff);

                    _simDisturbance += (_simRandom.NextDouble() - 0.5) * 0.03;
                    _simDisturbance = Math.Clamp(_simDisturbance, -2.0, 2.0);

                    double flowOld = _simFlowRate;
                    double flowSS = kcoeff * _lastPumpSpeed + _simDisturbance;
                    double consumed = tauEff * flowOld + steady * flowSS;
                    _simFlowRate = a * flowOld + oneMa * flowSS;

                    double noise = (_simRandom.NextDouble() - 0.5) * 0.4;
                    _currentWeight = Math.Max(0.0, _currentWeight - consumed + noise);
                    return _currentWeight;
                }
            }

            throw new InvalidOperationException("设备未配置为 ModbusTCP 模式且非模拟模式");
        }

        private async Task<bool> SetPumpSpeedAsync(string pumpDeviceId, double speed)
        {
            try
            {
                speed = Math.Clamp(RoundPumpSpeed(speed), 0.0, MAX_PUMP_SPEED);

                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp
                    && ConnectionManager != null && !IsSimulationMode)
                {
                    await ConnectionManager.WriteAsync(pumpDeviceId, "P0101泵基准模式", true);
                    return await ConnectionManager.WriteAsync<float>(
                        pumpDeviceId, "P0101泵基准泵速", (float)speed);
                }

                if (IsSimulationMode)
                {
                    FlowDebugLog($"[SIM] 泵速设置: {speed:F1}RPM");
                    return true;
                }

                FlowErrorLog("设备未配置为有效通信模式");
                return false;
            }
            catch (Exception ex)
            {
                FlowErrorLog($"设置泵速异常: {ex.Message}", ex);
                return false;
            }
        }

        #endregion

        #region ── 事件触发 ───────────────────────────────────────────

        private void RaiseControlStateChanged(
            string stateName, object stateValue,
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

        #region ── IControllableDevice ────────────────────────────────

        public List<DeviceActiveState> ActiveStates
        {
            get { lock (_statesLock) { return new List<DeviceActiveState>(_activeStates); } }
        }

        public event EventHandler<DeviceStateChangedEventArgs> StateChanged;

        public virtual async Task<bool> PauseAllActiveStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_activeStates.Any()) return true;
                foreach (var s in _activeStates.ToArray())
                {
                    _activeStates.Remove(s);
                    s.IsPaused = true;
                    _pausedStates.Add(s);
                }
            }
            InfoLog("所有活跃状态已暂停");
            return true;
        }

        public virtual async Task<bool> ResumeAllPausedStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_pausedStates.Any()) return true;
                foreach (var s in _pausedStates.ToArray())
                {
                    _pausedStates.Remove(s);
                    s.IsPaused = false;
                    _activeStates.Add(s);
                }
            }
            InfoLog("所有暂停状态已恢复");
            return true;
        }

        public virtual async Task<bool> StopAllActiveStatesAsync()
        {
            try
            {
                await StopFlowControlAsync();
                await StopWeightPollingAsync();
                lock (_statesLock) { _activeStates.Clear(); _pausedStates.Clear(); }
                InfoLog("所有状态及后台任务已停止");
                return true;
            }
            catch (Exception ex) { ErrorLog("停止所有状态失败", ex); return false; }
        }

        #endregion

        #region ── IFlowLifecycleAware ────────────────────────────────

        public virtual async Task<bool> OnFlowStartedAsync(FlowStartContext context)
        {
            try
            {
                lock (_statesLock) { _activeStates.Clear(); _pausedStates.Clear(); }
                lock (_stateLock) { _tareWeight = 0; _isTared = false; }

                _scaleId = Parameters.GetValue<string>("ScaleId");

                try
                {
                    if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp
                        && ConnectionManager != null && !IsSimulationMode)
                    {
                        _initialTotalWeight = await ConnectionManager.ReadAsync<double>(
                            _scaleId, $"_{_scaleId}_Add_g");
                        InfoLog($"记录初始累计重量: {_initialTotalWeight}g");
                    }
                }
                catch (Exception ex) { WarnLog($"读取初始累计值失败: {ex.Message}"); }

                StartWeightPolling();
                InfoLog("电子秤已准备好执行流程");
                return true;
            }
            catch (Exception ex) { ErrorLog("流程开始处理失败", ex); return false; }
        }

        public virtual async Task<bool> OnFlowCompletedAsync(FlowCompletionContext context)
        {
            try
            {
                InfoLog($"流程完成: {context.FlowName}");
                await StopFlowControlAsync();
                await StopWeightPollingAsync();
                await StopAllActiveStatesAsync();

                try
                {
                    if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp
                        && ConnectionManager != null && !IsSimulationMode)
                    {
                        var finalTotal = await ConnectionManager.ReadAsync<double>(ComId, $"_{_scaleId}_Add_g");
                        InfoLog($"流程汇总 — 初始:{_initialTotalWeight:F2}g  最终:{finalTotal:F2}g  增量:{finalTotal - _initialTotalWeight:F2}g");
                    }
                }
                catch (Exception ex) { WarnLog($"读取最终累计值失败: {ex.Message}"); }

                lock (_stateLock) { _tareWeight = 0; _isTared = false; _initialTotalWeight = 0; }
                lock (_flowControlLock) { _targetFlowRate = 0; _estimatedFlowRate = 0; }
                return true;
            }
            catch (Exception ex) { ErrorLog("流程完成处理失败", ex); return false; }
        }

        public virtual async Task<bool> OnFlowFailedAsync(FlowFailureContext context)
        {
            try
            {
                ErrorLog($"流程失败: {context.FlowName}  节点:{context.FailedNodeName}  错误:{context.ErrorMessage}");
                await StopFlowControlAsync();
                await StopWeightPollingAsync();
                await StopAllActiveStatesAsync();
                lock (_stateLock) { _tareWeight = 0; _isTared = false; _initialTotalWeight = 0; }
                lock (_flowControlLock) { _targetFlowRate = 0; _estimatedFlowRate = 0; }
                return true;
            }
            catch (Exception ex) { ErrorLog("流程失败处理异常", ex); return false; }
        }

        #endregion

        #region ── 构造函数 ───────────────────────────────────────────

        public DigitalScale_ModbusTCP()
        {
            _logger = LogManager.GetLogger<DigitalScale_ModbusTCP>();

            Name = "电子秤";
            Manufacturer = "电子秤_CY";
            Category = DeviceCategories.ElectronicScales;
            ComId = "S0101";
            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();

            AllowedRegions = new List<RegionType>
            {
                RegionType.Feed, RegionType.PreHeat, RegionType.Reaction,
                RegionType.Quench, RegionType.PostProcess
            };

            ImageLocation = "CUSTOM_CONTROL:DigitalScaleControl";

            Parameters.Variables.Add(new StringParameter("ScaleId", "S0101")
            {
                Options = new ObservableCollection<string> { "S0101", "S0111", "S0121" },
                HelpText = "选择要控制的电子秤（S0101/S0111/S0121）"
            });

            Parameters.Variables.Add(new StringParameter("PumpDeviceId", "P0101")
            {
                Options = new ObservableCollection<string> { "P0101", "P0111", "P0121" },
                HelpText = "关联的蠕动泵设备 ID"
            });

            Parameters.Variables.Add(new NumberParameter(
                "PumpSpeedPerFlowRate", 0.1, 20.0, 0.985,
                "泵流量增益 Kcoeff = (g/min)/RPM")
            {
                HelpText = "MPC 模型中的稳态增益 K。系统稳定后会自动进行物理标定修正。"
            });

            Parameters.Variables.Add(new NumberParameter(
                "FlowTimeConstantSec", 3.0, 300.0, 15.0,
                "一阶模型时间常数 τ (s)")
            {
                HelpText = "建议根据实机阶跃响应整定：流量达到约63%目标所需时间。"
            });

            InitializeCommands();
        }

        #endregion

        #region ── 命令初始化 ─────────────────────────────────────────

        private void InitializeCommands()
        {
            // ── 命令 1：获取称重值 ─────────────────────────────────────────────
            Commands.Add(new DeviceCommand
            {
                Name = "获取称重值",
                HelpText = "获取电子秤当前重量和累计重量",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new EnumParameter("Unit", typeof(WeightUnit), WeightUnit.Gram, "重量单位"),
                        new BooleanParameter("ApplyTare", true, "是否应用去皮值")
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
        {
            new StringParameter("Status",        "PENDING", "执行状态")    { IsVisibleInVariableManager = false },
            new NumberParameter("CurrentWeight", -1000, 10000, 0, "当前重量", true, UnitGram),
            new NumberParameter("TotalWeight",   0, 999999, 0, "累计重量",   true, UnitGram),
            new StringParameter("Unit",          "g",       "重量单位")    { IsVisibleInVariableManager = false },
            new StringParameter("Timestamp",     "",        "采集时间")    { IsVisibleInVariableManager = false }
        }
                },
                Action = WrapAction("获取称重值", async delegate (DeviceParameters parameters)
                {
                    var unit = parameters.GetValue<WeightUnit>("Unit");
                    _scaleId = Parameters.GetValue<string>("ScaleId");

                    double currentWeight = 0, totalWeight = 0;
                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp
                            && ConnectionManager != null && !IsSimulationMode)
                        {
                            currentWeight = await ConnectionManager.ReadAsync<double>(_scaleId, $"_{_scaleId}_g");
                            totalWeight = await ConnectionManager.ReadAsync<double>(_scaleId, $"_{_scaleId}_Add_g");
                        }
                        else if (IsSimulationMode)
                        {
                            lock (_stateLock) { currentWeight = _currentWeight; totalWeight = _totalWeight; }
                        }
                        else throw new InvalidOperationException("设备未连接且非模拟模式");

                        lock (_stateLock) { _currentWeight = currentWeight; _totalWeight = totalWeight; }
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"获取称重值失败: {ex.Message}", ex);
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
        {
            new StringParameter("Status", "OK", "") { IsVisibleInVariableManager = false },
            new NumberParameter("CurrentWeight", -1000, 10000,
                Math.Round(ConvertWeight(currentWeight, WeightUnit.Gram, unit), 3), "当前重量",
                true, GetUnitString(unit)),
            new NumberParameter("TotalWeight", 0, 999999,
                Math.Round(ConvertWeight(totalWeight, WeightUnit.Gram, unit), 2), "累计重量",
                true, GetUnitString(unit)),
            new StringParameter("Unit",      GetUnitString(unit), "") { IsVisibleInVariableManager = false },
            new StringParameter("Timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), "")
                { IsVisibleInVariableManager = false }
        }
                    };
                }),
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/flowrate.png",
            });

            // ── 命令 2：设置目标流速 ───────────────────────────────────────────
            Commands.Add(new DeviceCommand
            {
                Name = "设置目标流速",
                HelpText =
                    "启动流速闭环控制 v9.2（一阶动态模型 MPC + 增广卡尔曼观测器 + 自动物理标定 Kcoeff）\n\n" +
                    "物理标定流程（自动触发）：\n" +
                    "  1. 流速宣布稳定后自动触发\n" +
                    "  2. 冻结当前泵速 1 个周期（管路稳定）\n" +
                    $"  3. 冻结泵速持续 {PHYS_CALIB_CYCLES} 个周期（≈{PHYS_CALIB_CYCLES * 10}s），记录真实重量差\n" +
                    $"  4. 用物理计量结果通过 EMA（α={PHYS_CALIB_ALPHA}）修正 Kcoeff 并写回参数\n" +
                    "  5. 恢复 MPC 正常控制",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TargetFlowRate",       0, 500,  20.0, "目标流速 (g/min)"),
                        new NumberParameter("SamplingInterval",     3, 300,  10.0, "采样间隔 (秒)"),
                        new NumberParameter("StabilizationTimeout", 60, 3600, 300.0, "稳定等待超时 (秒)"),
                        new BooleanParameter("EnableAutoControl",   true, "启用自动控制")
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
        {
            new BooleanParameter("Success",          false, "设置成功")              { IsVisibleInVariableManager = true },
            new NumberParameter("SetFlowRate",       0, 500,  0, "目标流速",         true, UnitFlowRate),
            new NumberParameter("InitialPumpSpeed",  0, 100,  0, "初始泵速",         true, UnitPumpSpeed),
            new BooleanParameter("ControlStarted",   false, "控制已启动")            { IsVisibleInVariableManager = true },
            new BooleanParameter("Stabilized",       false, "流速已稳定")            { IsVisibleInVariableManager = true },
            new NumberParameter("FinalFlow",         0, 500,  0, "稳定时估算流速",   true, UnitFlowRate),
            new NumberParameter("FinalRatio",        0, 200,  0, "稳定时 Ratio",     true, UnitPercent),
            new NumberParameter("ElapsedSec",        0, 9999, 0, "稳定耗时",         true, UnitTimeSec),
            new NumberParameter("AdaptiveCoeff",     0, 20,   0, "当前 Kcoeff",      true, UnitKcoeff)
        }
                },
                Action = WrapAction("设置目标流速", async delegate (DeviceParameters parameters)
                {
                    RaiseControlStateChanged("CommandState_设置目标流速", CommandExecutionState.Running);

                    _scaleId = Parameters.GetValue<string>("ScaleId");
                    string pumpDeviceId = Parameters.GetValue<string>("PumpDeviceId");
                    double pumpCoeff = Parameters.GetValue<double>("PumpSpeedPerFlowRate");
                    double effectiveCoeff = _adaptiveCoeff > 0.01 ? _adaptiveCoeff : pumpCoeff;
                    double tauSec = GetFlowTimeConstantSec();

                    double targetFlow = Math.Clamp(parameters.GetValue<double>("TargetFlowRate"), 0, 500);
                    double samplingSec = Math.Clamp(parameters.GetValue<double>("SamplingInterval"), 3, 300);
                    double timeoutSec = Math.Clamp(parameters.GetValue<double>("StabilizationTimeout"), 60, 3600);
                    bool enableAuto = parameters.GetValue<bool>("EnableAutoControl");
                    int samplingMs = (int)(samplingSec * 1000);

                    bool success = false, controlStarted = false, stabilized = false;
                    double initialPumpSpeed = 0, elapsedSec = 0, finalRatio = 0;

                    try
                    {
                        lock (_flowControlLock) { _targetFlowRate = targetFlow; }
                        RaiseControlStateChanged("TargetFlowRate", targetFlow);

                        if (targetFlow > 0)
                        {
                            double initBySteadyState = targetFlow / Math.Max(effectiveCoeff, 1e-6);
                            initialPumpSpeed = RoundPumpSpeed(
                                Math.Clamp(initBySteadyState * 0.85, 0, MAX_PUMP_SPEED));

                            FlowInfoLog("╔══════════════════════════════════════════════════════╗");
                            FlowInfoLog("║ [设置目标流速] v9.2 真动态 MPC + 自动物理标定       ║");
                            FlowInfoLog("╠══════════════════════════════════════════════════════╣");
                            FlowInfoLog($"║  目标流速         : {targetFlow:F2} g/min");
                            FlowInfoLog($"║  采样间隔         : {samplingSec:F0} s");
                            FlowInfoLog($"║  Kcoeff(有效)     : {effectiveCoeff:F4}");
                            FlowInfoLog($"║  时间常数 τ       : {tauSec:F1} s");
                            FlowInfoLog($"║  初始泵速         : {initialPumpSpeed:F1} RPM");
                            FlowInfoLog($"║  物理标定         : 稳定后自动触发，冻结{PHYS_CALIB_CYCLES}周期");
                            FlowInfoLog($"║  稳定等待超时     : {timeoutSec:F0} s");
                            FlowInfoLog("╚══════════════════════════════════════════════════════╝");

                            if (!IsSimulationMode
                                && DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp)
                            {
                                try
                                {
                                    bool pumpOn = await ConnectionManager.ReadAsync<bool>(pumpDeviceId, "P0101启动");
                                    if (!pumpOn)
                                    {
                                        await ConnectionManager.WriteAsync(pumpDeviceId, "P0101启动", true);
                                        FlowInfoLog($"[设置目标流速] 启动泵: {pumpDeviceId}");
                                    }
                                }
                                catch (Exception ex) { FlowWarnLog($"读取泵状态失败，继续: {ex.Message}"); }
                            }

                            bool initOk = await SetPumpSpeedAsync(pumpDeviceId, initialPumpSpeed);

                            if (initOk)
                            {
                                _lastPumpSpeed = initialPumpSpeed;
                                success = true;
                                FlowInfoLog($"[设置目标流速] ✓ 初始泵速已写入: {initialPumpSpeed:F1}RPM");
                                await Task.Delay(60 * 1000);
                                FlowInfoLog("[设置目标流速] 等待流速稳定1分钟....");

                                if (enableAuto)
                                {
                                    StartFlowLogFile(targetFlow, samplingMs, pumpDeviceId, effectiveCoeff, tauSec);
                                    StartFlowControl(targetFlow, samplingMs, pumpDeviceId, initialPumpSpeed);
                                    controlStarted = true;

                                    AddActiveState("FlowRateControl", "设置目标流速", "停止流速控制",
                                        stateData: new { TargetFlowRate = targetFlow, PumpDeviceId = pumpDeviceId });

                                    FlowInfoLog($"[设置目标流速] ⏳ 等待流速稳定，超时:{timeoutSec:F0}s ...");

                                    var sw = Stopwatch.StartNew();
                                    try
                                    {
                                        TaskCompletionSource<bool> tcs;
                                        lock (_flowControlLock) { tcs = _flowStabilizedTcs; }

                                        var winner = await Task.WhenAny(
                                            tcs?.Task ?? Task.FromResult(false),
                                            Task.Delay(TimeSpan.FromSeconds(timeoutSec)));

                                        sw.Stop();
                                        elapsedSec = sw.Elapsed.TotalSeconds;

                                        double finalEst;
                                        lock (_flowControlLock) { finalEst = _estimatedFlowRate; }
                                        finalRatio = targetFlow > 0 ? finalEst / targetFlow * 100.0 : 0;

                                        var stTask = tcs?.Task;
                                        if (stTask != null
                                            && stTask.Status == TaskStatus.RanToCompletion
                                            && stTask.Result)
                                        {
                                            stabilized = true;
                                            FlowInfoLog(
                                                $"[设置目标流速] ✓ 流速已稳定 | 耗时:{elapsedSec:F1}s | " +
                                                $"最终:{finalEst:F4}g/min | Ratio:{finalRatio:F2}% | " +
                                                $"泵速:{_lastPumpSpeed:F1}RPM | Kcoeff:{_adaptiveCoeff:F4}");
                                            RaiseControlStateChanged("FlowStabilized", true);
                                        }
                                        else if (stTask != null && stTask.IsCanceled)
                                        {
                                            FlowWarnLog(
                                                $"[设置目标流速] ⚠ 稳定信号被取消 | 耗时:{elapsedSec:F1}s");
                                        }
                                        else
                                        {
                                            FlowWarnLog(
                                                $"[设置目标流速] ⏰ 超时({timeoutSec:F0}s) | " +
                                                $"当前:{finalEst:F4}g/min  Ratio:{finalRatio:F2}% | " +
                                                $"控制继续后台运行");
                                            RaiseControlStateChanged("FlowStabilized", false);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        sw.Stop();
                                        elapsedSec = sw.Elapsed.TotalSeconds;
                                        FlowWarnLog($"等待稳定时异常: {ex.Message}");
                                    }
                                }

                                RaiseControlStateChanged("FlowControlEnabled", true);
                                RaiseControlStateChanged("CommandState_设置目标流速", CommandExecutionState.Succeeded);
                            }
                            else
                            {
                                FlowWarnLog($"初始泵速写入失败: {initialPumpSpeed:F1}RPM");
                                RaiseControlStateChanged("CommandState_设置目标流速", CommandExecutionState.Failed);
                            }
                        }
                        else
                        {
                            await StopFlowControlAsync();
                            await SetPumpSpeedAsync(pumpDeviceId, 0);
                            RemoveActiveState("FlowRateControl");
                            success = true;
                            RaiseControlStateChanged("FlowControlEnabled", false);
                            RaiseControlStateChanged("CommandState_设置目标流速", CommandExecutionState.Succeeded);
                            FlowInfoLog("[设置目标流速] 目标流速=0，控制已停止，泵速已归零");
                        }
                    }
                    catch (Exception ex)
                    {
                        FlowErrorLog($"设置目标流速失败: {ex.Message}", ex);
                        RaiseControlStateChanged("CommandState_设置目标流速", CommandExecutionState.Error,
                            new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });
                        throw;
                    }

                    double finalEstLog;
                    lock (_flowControlLock) { finalEstLog = _estimatedFlowRate; }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
        {
            new BooleanParameter("Success",         success,                           "") { IsVisibleInVariableManager = true },
            new NumberParameter("SetFlowRate",      0, 500,  targetFlow,               "", true, UnitFlowRate),
            new NumberParameter("InitialPumpSpeed", 0, 100,  Math.Round(initialPumpSpeed, 1), "", true, UnitPumpSpeed),
            new BooleanParameter("ControlStarted",  controlStarted,                    "") { IsVisibleInVariableManager = true },
            new BooleanParameter("Stabilized",      stabilized,                        "") { IsVisibleInVariableManager = true },
            new NumberParameter("FinalFlow",        0, 500,  Math.Round(finalEstLog, 4), "", true, UnitFlowRate),
            new NumberParameter("FinalRatio",       0, 200,  Math.Round(finalRatio, 2), "", true, UnitPercent),
            new NumberParameter("ElapsedSec",       0, 9999, Math.Round(elapsedSec, 1), "", true, UnitTimeSec),
            new NumberParameter("AdaptiveCoeff",    0, 20,   Math.Round(_adaptiveCoeff, 4), "", true, UnitKcoeff)
        }
                    };
                }),
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/flowrate.png",
            });

            // ── 命令 3：停止流速控制 ───────────────────────────────────────────
            Commands.Add(new DeviceCommand
            {
                Name = "停止流速控制",
                HelpText = "停止流速闭环控制，关联泵速归零",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("Success", false, "停止成功") { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("停止流速控制", async delegate (DeviceParameters parameters)
                {
                    RaiseControlStateChanged("CommandState_停止流速控制", CommandExecutionState.Running);
                    string pumpDeviceId = Parameters.GetValue<string>("PumpDeviceId");
                    bool success = false;

                    try
                    {
                        await StopFlowControlAsync();
                        success = await SetPumpSpeedAsync(pumpDeviceId, 0);
                        lock (_flowControlLock) { _targetFlowRate = 0; _estimatedFlowRate = 0; }
                        RemoveActiveState("FlowRateControl");
                        RaiseControlStateChanged("FlowControlEnabled", false);
                        RaiseControlStateChanged("TargetFlowRate", 0.0);
                        RaiseControlStateChanged("EstimatedFlowRate", 0.0);
                        RaiseControlStateChanged("CommandState_停止流速控制",
                            success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);
                        InfoLog("[停止流速控制] 已停止，泵速已归零");
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"停止流速控制失败: {ex.Message}");
                        RaiseControlStateChanged("CommandState_停止流速控制", CommandExecutionState.Error,
                            new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });
                        throw;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new BooleanParameter("Success", success, "") { IsVisibleInVariableManager = true }
                        }
                    };
                }),
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/flowrate.png",
            });

            // ── 命令 4：获取流速控制状态 ──────────────────────────────────────
            Commands.Add(new DeviceCommand
            {
                Name = "获取流速控制状态",
                HelpText = "获取流速闭环控制当前运行状态 v9.2",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
        {
            new BooleanParameter("FlowControlEnabled",  false, "控制运行中")           { IsVisibleInVariableManager = true },
            new StringParameter("ControlPhase",         "",    "控制阶段")              { IsVisibleInVariableManager = true },
            new NumberParameter("TargetFlowRate",       0, 500,  0, "目标流速",        true, UnitFlowRate),
            new NumberParameter("EstimatedFlowRate",    0, 500,  0, "估算流速",        true, UnitFlowRate),
            new NumberParameter("KF_WeightEst",         0, 99999,0, "估算重量",        true, UnitGram),
            new NumberParameter("KF_FlowSigma",         0, 100,  0, "流速标准差",      true, UnitFlowRate),
            new NumberParameter("DOB_DistEst",          -100,100,0, "扰动估计",        true, UnitFlowRate),
            new NumberParameter("Ratio",                0, 200,  0, "Ratio",          true, UnitPercent),
            new NumberParameter("Deviation",            -500,500,0, "偏差",           true, UnitFlowRate),
            new NumberParameter("DeviationPct",         -100,100,0, "偏差百分比",      true, UnitPercent),
            new NumberParameter("CurrentPumpSpeed",     0, 100,  0, "当前泵速",        true, UnitPumpSpeed),
            new NumberParameter("StableCount",          0, 10,   0, "稳定计数")         { IsVisibleInVariableManager = true },
            new BooleanParameter("StabilityDeclared",   false, "已声明稳定")            { IsVisibleInVariableManager = true },
            new NumberParameter("AdaptiveCoeff",        0, 20,   0, "Kcoeff",         true, UnitKcoeff),
            new NumberParameter("CurrentWeight",        0, 99999,0, "天平实时重量",    true, UnitGram),
            new NumberParameter("ElapsedSeconds",       0, 9999, 0, "已运行秒数",      true, UnitTimeSec),
            new NumberParameter("CycleCount",           0, 9999, 0, "MPC 周期数")       { IsVisibleInVariableManager = true },
            new StringParameter("PhysCalibState",       "",    "物理标定状态")          { IsVisibleInVariableManager = true },
            new StringParameter("LogFilePath",          "",    "日志文件路径")          { IsVisibleInVariableManager = true },
            new StringParameter("StatusSummary",        "",    "状态摘要")              { IsVisibleInVariableManager = true }
        }
                },
                Action = WrapAction("获取流速控制状态", async delegate (DeviceParameters parameters)
                {
                    _scaleId = Parameters.GetValue<string>("ScaleId");
                    string pumpDeviceId = Parameters.GetValue<string>("PumpDeviceId");

                    bool enabled; double target, estimated;
                    lock (_flowControlLock)
                    {
                        enabled = _flowControlEnabled;
                        target = _targetFlowRate;
                        estimated = _estimatedFlowRate;
                    }

                    double fEst = _observerInitialized ? _obsX[0] : 0.0;
                    double dEst = _observerInitialized ? _obsX[1] : 0.0;
                    double wEst = _observerInitialized ? _obsX[2] : 0.0;
                    double fSig = _observerInitialized ? Math.Sqrt(Math.Max(0.0, _obsP[0, 0])) : 0.0;

                    double deviation = estimated - target;
                    double deviationPct = target > 0 ? deviation / target * 100.0 : 0.0;
                    double ratio = target > 0 ? estimated / target * 100.0 : 0.0;
                    double currentW = 0;
                    double currentPump = _lastPumpSpeed;
                    double elapsedSec = enabled ? (DateTime.Now - _controlStartTime).TotalSeconds : 0.0;

                    string logPath;
                    lock (_flowLogLock) { logPath = _flowLogFilePath ?? "(未启动)"; }

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp
                            && ConnectionManager != null && !IsSimulationMode)
                        {
                            currentW = await ConnectionManager.ReadAsync<double>(_scaleId, $"_{_scaleId}_g");
                            currentPump = await ConnectionManager.ReadAsync<double>(pumpDeviceId, "P0101泵速");
                        }
                        else if (IsSimulationMode)
                        {
                            lock (_stateLock) { currentW = _currentWeight; }
                        }
                    }
                    catch (Exception ex) { WarnLog($"读取状态失败: {ex.Message}"); }

                    string summary = enabled
                        ? $"[{_controlPhase}] 目标:{target:F1}  流速:{fEst:F4}±{fSig:F3}  " +
                          $"扰动:{dEst:+0.000;-0.000}  Ratio:{ratio:F2}%  偏差:{deviationPct:+0.000;-0.000}%  " +
                          $"泵:{currentPump:F1}RPM  T+{elapsedSec:F0}s  C{_cycleCount}  " +
                          $"稳定:{_stableCount}/{STABLE_REQUIRED}" +
                          (_stabilityDeclared ? " [已稳定]" : "") +
                          $"  标定:{_physCalibState}"
                        : "未启动";

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
        {
            new BooleanParameter("FlowControlEnabled", enabled,                              "") { IsVisibleInVariableManager = true },
            new StringParameter("ControlPhase",        _controlPhase,                        "") { IsVisibleInVariableManager = true },
            new NumberParameter("TargetFlowRate",      0, 500,  Math.Round(target, 2),       "", true, UnitFlowRate),
            new NumberParameter("EstimatedFlowRate",   0, 500,  Math.Round(estimated, 4),    "", true, UnitFlowRate),
            new NumberParameter("KF_WeightEst",        0, 99999,Math.Round(wEst, 2),         "", true, UnitGram),
            new NumberParameter("KF_FlowSigma",        0, 100,  Math.Round(fSig, 4),         "", true, UnitFlowRate),
            new NumberParameter("DOB_DistEst",         -100,100,Math.Round(dEst, 4),         "", true, UnitFlowRate),
            new NumberParameter("Ratio",               0, 200,  Math.Round(ratio, 2),        "", true, UnitPercent),
            new NumberParameter("Deviation",           -500,500,Math.Round(deviation, 4),    "", true, UnitFlowRate),
            new NumberParameter("DeviationPct",        -100,100,Math.Round(deviationPct, 3), "", true, UnitPercent),
            new NumberParameter("CurrentPumpSpeed",    0, 100,  Math.Round(currentPump, 1),  "", true, UnitPumpSpeed),
            new NumberParameter("StableCount",         0, 10,   _stableCount,                "") { IsVisibleInVariableManager = true },
            new BooleanParameter("StabilityDeclared",  _stabilityDeclared,                   "") { IsVisibleInVariableManager = true },
            new NumberParameter("AdaptiveCoeff",       0, 20,   Math.Round(_adaptiveCoeff, 4),"", true, UnitKcoeff),
            new NumberParameter("CurrentWeight",       0, 99999,Math.Round(currentW, 2),     "", true, UnitGram),
            new NumberParameter("ElapsedSeconds",      0, 9999, Math.Round(elapsedSec, 1),   "", true, UnitTimeSec),
            new NumberParameter("CycleCount",          0, 9999, _cycleCount,                 "") { IsVisibleInVariableManager = true },
            new StringParameter("PhysCalibState",      _physCalibState.ToString(),           "") { IsVisibleInVariableManager = true },
            new StringParameter("LogFilePath",         logPath,                              "") { IsVisibleInVariableManager = true },
            new StringParameter("StatusSummary",       summary,                              "") { IsVisibleInVariableManager = true }
        }
                    };

                }),
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/flowrate.png",
            });

            // ── 命令 5：定时计量验证（手动测试工具，不修改 Kcoeff） ───────────
            Commands.Add(new DeviceCommand
            {
                Name = "定时计量验证",
                HelpText = "手动精确计时，验证实际流量（仅读取，不修改 Kcoeff）",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("MeasureDurationSec", 10, 300, 60, "计量时长(秒)")
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
        {
            new NumberParameter("W1",             -1000, 99999, 0, "起始重量",      true, UnitGram),
            new NumberParameter("W2",             -1000, 99999, 0, "结束重量",      true, UnitGram),
            new NumberParameter("Consumed",           0,  9999, 0, "消耗重量",      true, UnitGram),
            new NumberParameter("ActualDtSec",        0,  9999, 0, "实际计时",      true, UnitTimeSec),
            new NumberParameter("ActualFlowRate",     0,   500, 0, "实际流量",      true, UnitFlowRate),
            new NumberParameter("TargetFlowRate",     0,   500, 0, "目标流量",      true, UnitFlowRate),
            new NumberParameter("ErrorPct",        -100,   100, 0, "误差",          true, UnitPercent)
        }
                },
                Action = WrapAction("定时计量验证", async delegate (DeviceParameters parameters)
                {
                    double duration = parameters.GetValue<double>("MeasureDurationSec");
                    _scaleId = Parameters.GetValue<string>("ScaleId");

                    double w1 = await ReadWeightAsync();
                    var t1 = DateTime.UtcNow;
                    InfoLog($"[计量验证] 开始 W1={w1:F3}g，等待{duration:F0}秒...");

                    await Task.Delay(TimeSpan.FromSeconds(duration));

                    double w2 = await ReadWeightAsync();
                    var t2 = DateTime.UtcNow;

                    double actualDtSec = (t2 - t1).TotalSeconds;
                    double actualDtMin = actualDtSec / 60.0;
                    double consumed = w1 - w2;
                    double actualFlow = consumed / actualDtMin;

                    InfoLog($"[计量验证] 结束 W2={w2:F3}g");
                    InfoLog($"[计量验证] 实际耗时={actualDtSec:F2}s  消耗={consumed:F3}g  实际流量={actualFlow:F4}g/min");

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
        {
            new NumberParameter("W1",           -1000, 99999, Math.Round(w1, 3),         "起始重量",      true, UnitGram),
            new NumberParameter("W2",           -1000, 99999, Math.Round(w2, 3),         "结束重量",      true, UnitGram),
            new NumberParameter("Consumed",         0,  9999, Math.Round(consumed, 3),   "消耗重量",      true, UnitGram),
            new NumberParameter("ActualDtSec",      0,  9999, Math.Round(actualDtSec, 2),"实际计时",      true, UnitTimeSec),
            new NumberParameter("ActualFlowRate",   0,   500, Math.Round(actualFlow, 4), "实际流量",      true, UnitFlowRate),
            new NumberParameter("TargetFlowRate",   0,   500, _targetFlowRate,           "目标流量",      true, UnitFlowRate),
            new NumberParameter("ErrorPct",      -100,   100,
                Math.Round(_targetFlowRate > 0
                    ? (actualFlow - _targetFlowRate) / _targetFlowRate * 100 : 0, 3),
                "误差",          true, UnitPercent)
        }
                    };


                }),
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/flowrate.png",
            });
        }

        #endregion

        #region ── 设备生命周期 ───────────────────────────────────────

        public override void Initialize()
        {
            InfoLog("初始化电子秤 v9.2");
            try
            {
                base.Initialize();
                _scaleId = Parameters.GetValue<string>("ScaleId");
                ComId = _scaleId;

                lock (_stateLock)
                {
                    _currentWeight = 0; _totalWeight = 0; _tareWeight = 0;
                    _initialTotalWeight = 0; _isTared = false; _isPoweredOn = false;
                }
                lock (_statesLock) { _activeStates.Clear(); _pausedStates.Clear(); }
                lock (_flowControlLock) { _targetFlowRate = 0; _estimatedFlowRate = 0; }

                _obsX = new double[3]; _obsP = new double[3, 3];
                _observerInitialized = false;
                ResetRawFiniteDiffEstimator();
                _lastControlSampleTime = DateTime.MinValue;

                _disturbanceEstimate = 0.0;
                _lastPumpSpeed = 0.0;
                _stableCount = 0;
                _stabilityDeclared = false;
                _cycleCount = 0;
                _controlPhase = "Idle";
                _lastMpcPlan = new double[MPC_N];
                _simFlowRate = 0.0;
                _simDisturbance = 0.0;
                _lastFlowSimReadTime = DateTime.Now;

                _physCalibState = PhysCalibState.Idle;
                _physCalibFrozenSpeed = 0.0;
                _physCalibWeightStart = 0.0;
                _physCalibTimeStart = DateTime.MinValue;
                _physCalibCyclesLeft = 0;

                double savedCoeff = Parameters.GetValue<double>("PumpSpeedPerFlowRate");
                if (_adaptiveCoeff < 0.01 && savedCoeff > 0.01)
                {
                    _adaptiveCoeff = savedCoeff;
                    InfoLog($"[A9] 从参数恢复学习系数: {_adaptiveCoeff:F4}");
                }

                InfoLog(
                    $"电子秤初始化完成 v9.2 | 秤:{_scaleId} | " +
                    $"Kcoeff:{savedCoeff:F3} | 自适应:{_adaptiveCoeff:F4} | " +
                    $"τ={GetFlowTimeConstantSec():F1}s | 物理标定:{PHYS_CALIB_CYCLES}周期 α={PHYS_CALIB_ALPHA}");
            }
            catch (Exception ex) { ErrorLog("设备初始化失败", ex); throw; }
        }

        public override void Shutdown()
        {
            InfoLog("关闭电子秤 v9.2");
            try
            {
                StopFlowControlAsync().Wait(TimeSpan.FromSeconds(3));
                StopWeightPollingAsync().Wait(TimeSpan.FromSeconds(2));
                StopAllActiveStatesAsync().Wait(TimeSpan.FromSeconds(5));
                CloseFlowLogFile();
                base.Shutdown();
                InfoLog("电子秤已关闭");
            }
            catch (Exception ex) { ErrorLog("设备关闭失败", ex); throw; }
        }

        protected override async Task<bool> OnConnectAsync()
        {
            try
            {
                if (IsSimulationMode)
                {
                    lock (_stateLock) { if (_currentWeight <= 0) _currentWeight = 5000.0; }
                    await Task.Delay(500);
                    InfoLog("仿真模式连接成功");
                    return true;
                }
                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp
                    && ConnectionManager != null)
                    return await ConnectionManager.ConnectAsync(ComId);

                ErrorLog("需要 PLC 模式或仿真模式");
                return false;
            }
            catch (Exception ex) { ErrorLog($"连接失败: {ex.Message}"); return false; }
        }

        protected override async Task<bool> OnDisconnectAsync()
        {
            try
            {
                await StopFlowControlAsync();
                await StopAllActiveStatesAsync();
                if (ConnectionManager != null) await ConnectionManager.DisconnectAsync(ComId);
                InfoLog("连接已断开");
                return true;
            }
            catch (Exception ex) { ErrorLog($"断开连接失败: {ex.Message}"); return false; }
        }

        protected override async Task<bool> OnCheckConnectionAsync()
        {
            try
            {
                if (IsSimulationMode) { await Task.Delay(50); return true; }
                return ConnectionManager != null && await ConnectionManager.CheckConnectionAsync(ComId);
            }
            catch { return false; }
        }

        #endregion

        #region ── 单位转换 ───────────────────────────────────────────

        private double ConvertWeight(double valueInGrams, WeightUnit fromUnit, WeightUnit toUnit)
        {
            if (fromUnit == toUnit) return valueInGrams;
            double grams = fromUnit switch
            {
                WeightUnit.Gram => valueInGrams,
                WeightUnit.Kilogram => valueInGrams * 1000,
                WeightUnit.Pound => valueInGrams * 453.592,
                WeightUnit.Ounce => valueInGrams * 28.3495,
                _ => valueInGrams
            };
            return toUnit switch
            {
                WeightUnit.Gram => grams,
                WeightUnit.Kilogram => grams / 1000,
                WeightUnit.Pound => grams / 453.592,
                WeightUnit.Ounce => grams / 28.3495,
                _ => grams
            };
        }

        private string GetUnitString(WeightUnit unit) => unit switch
        {
            WeightUnit.Gram => "g",
            WeightUnit.Kilogram => "kg",
            WeightUnit.Pound => "lb",
            WeightUnit.Ounce => "oz",
            _ => "g"
        };

        #endregion

        #region ── 状态管理 ───────────────────────────────────────────

        protected void AddActiveState(
            string stateName, string activateCommand, string deactivateCommand,
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
                    PauseCommand = pauseCommand,
                    ResumeCommand = resumeCommand,
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

        #region ── 日志封装 ───────────────────────────────────────────

        private Func<DeviceParameters, DeviceParameters> WrapAction(
            string commandName,
            Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return (DeviceParameters parameters) =>
            {
                _logger.LogInformation($"[{Name}] 命令开始: {commandName}");
                var sw = Stopwatch.StartNew();
                try
                {
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();
                    _logger.LogInformation($"[{Name}] 命令完成: {commandName}  耗时:{sw.ElapsedMilliseconds}ms");
                    return output;
                }
                catch (DeviceCommandExecutionException) { throw; }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[{Name}] 命令异常: {commandName}  耗时:{sw.ElapsedMilliseconds}ms");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        protected void InfoLog(string msg) => _logger.LogInformation($"[{Name}({_scaleId})] {msg}");
        protected void WarnLog(string msg) => _logger.LogWarning($"[{Name}({_scaleId})] {msg}");
        protected void ErrorLog(string msg, Exception ex = null) => _logger.LogError(ex, $"[{Name}({_scaleId})] {msg}");
        protected void DebugLog(string msg) => _logger.LogDebug($"[{Name}({_scaleId})] {msg}");

        #endregion

        #region ── 小矩阵工具 ─────────────────────────────────────────

        private static void Clear3x3(double[,] m)
        {
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    m[i, j] = 0.0;
        }

        private static double[,] Identity3x3() => new double[,]
            { { 1.0, 0.0, 0.0 }, { 0.0, 1.0, 0.0 }, { 0.0, 0.0, 1.0 } };

        private static double[,] Mul3x3(double[,] a, double[,] b)
        {
            var r = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    double s = 0.0;
                    for (int k = 0; k < 3; k++) s += a[i, k] * b[k, j];
                    r[i, j] = s;
                }
            return r;
        }

        private static double[,] Mul3x3ByTranspose(double[,] a, double[,] b)
        {
            var r = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                {
                    double s = 0.0;
                    for (int k = 0; k < 3; k++) s += a[i, k] * b[j, k];
                    r[i, j] = s;
                }
            return r;
        }

        private static double[] Mul3x3Vec(double[,] a, double[] x) => new double[]
        {
            a[0,0]*x[0] + a[0,1]*x[1] + a[0,2]*x[2],
            a[1,0]*x[0] + a[1,1]*x[1] + a[1,2]*x[2],
            a[2,0]*x[0] + a[2,1]*x[1] + a[2,2]*x[2]
        };

        private static double DotRowVec(double[,] a, int row, double[] x)
            => a[row, 0] * x[0] + a[row, 1] * x[1] + a[row, 2] * x[2];

        private static void Symmetrize3x3(double[,] m)
        {
            for (int i = 0; i < 3; i++)
                for (int j = i + 1; j < 3; j++)
                {
                    double s = 0.5 * (m[i, j] + m[j, i]);
                    m[i, j] = s; m[j, i] = s;
                }
        }

        private static double NormInf(double[] v)
        {
            double m = 0.0;
            for (int i = 0; i < v.Length; i++) m = Math.Max(m, Math.Abs(v[i]));
            return m;
        }

        #endregion

        #region ── 枚举与数据类 ───────────────────────────────────────

        public enum WeightUnit { Gram, Kilogram, Pound, Ounce }

        private class ScaleStateData
        {
            public string ScaleId { get; set; }
            public double TareValue { get; set; }
        }

        #endregion
    }
}