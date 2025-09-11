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
    public class FlowMeter_ModbusTCP : Device, IControllableDevice, IFlowLifecycleAware
    {
        private readonly ILogService _logger;
        private double _currentFlow = 0;
        private double _targetFlow = 0; // 基准流量设定值
        private readonly object _stateLock = new object();

        // *** 高精度控制参数 ***
        private bool _autoControlEnabled = false;
        private Timer _controlTimer = null;
        private readonly object _controlLock = new object();
        private const int CONTROL_INTERVAL_MS = 20000;         // 20秒控制周期
        private const double FLOW_TOLERANCE_PERCENT = 0.5;     // ±0.5%容差
        private const double DEAD_ZONE_PERCENT = 0.2;          // ±0.2%死区

        private const double FLOW_TO_SPEED_RATIO = 1.0;
        private const int MAX_PUMP_SPEED = 100;
        private const int MAX_FLOW_RATE = 100;

        // *** 高精度PID参数 ***
        private double _previousError = 0;
        private double _integralError = 0;
        private const double KP = 0.8;
        private const double KI = 0.05;
        private const double KD = 0.08;

        // *** 更严格的变化限制和滤波 ***
        private double _lastPumpSpeedOutput = 0;
        private double _previousPumpSpeed = 0; // 记录上次泵速
        private const double MAX_SPEED_CHANGE_PER_CYCLE = 2.0;
        private const double SPEED_FILTER_FACTOR = 0.95;

        // *** 更严格的稳定性检测 ***
        private int _stableCount = 0;
        private const int STABLE_CYCLES_REQUIRED = 3;

        // *** 智能稳定性检测参数 ***
        private readonly Queue<double> _flowHistory = new Queue<double>(); // 流量历史队列
        private const int FLOW_HISTORY_SIZE = 10; // 保持最近10个读数
        private const double STABILITY_THRESHOLD = 0.3; // 稳定性阈值0.3%
        private const int MIN_STABLE_READINGS = 3; // 至少3次稳定读数
        private const int MAX_MONITORING_TIME_MS = 30000; // 最大监测30秒
        private const int FLOW_READING_INTERVAL_MS = 1000; // 每1秒读取一次流量

        // *** 动态等待时间参数 ***
        private const int MIN_STABILIZE_DELAY_MS = 3000;   // 最小等待3秒
        private const int MAX_STABILIZE_DELAY_MS = 25000;  // 最大等待25秒
        private const int BASE_STABILIZE_DELAY_MS = 8000;  // 基础等待8秒

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

        #region 智能稳定性检测系统

        /// <summary>
        /// 智能等待流量稳定 - 实时监测变化率
        /// </summary>
        private async Task<(double stableFlow, double waitTimeSeconds, int readingCount)> WaitForFlowStabilityAsync(double targetFlow)
        {
            var startTime = DateTime.Now;
            int readingCount = 0;
            double lastFlow = 0;
            bool hasValidReading = false;

            _flowHistory.Clear();

            AddCycleLog($"│ 开始智能稳定检测 (目标: {targetFlow:F2} g/min)");
            AddCycleLog($"│ 检测参数: 阈值±{STABILITY_THRESHOLD}%, 需要{MIN_STABLE_READINGS}次稳定, 最大{MAX_MONITORING_TIME_MS / 1000}秒");

            try
            {
                while ((DateTime.Now - startTime).TotalMilliseconds < MAX_MONITORING_TIME_MS)
                {
                    // 读取当前流量
                    double currentFlow = await ReadCurrentFlowAsync();
                    readingCount++;

                    // 记录流量历史
                    _flowHistory.Enqueue(currentFlow);
                    if (_flowHistory.Count > FLOW_HISTORY_SIZE)
                    {
                        _flowHistory.Dequeue();
                    }

                    // 计算变化率和趋势
                    var (isStable, changeRate, trend) = AnalyzeFlowStability(currentFlow, targetFlow);

                    AddCycleLog($"│ 读数#{readingCount}: {currentFlow:F2} g/min, 变化率: {changeRate:F3}%/s, 趋势: {trend}");

                    // 检查是否稳定
                    if (isStable && _flowHistory.Count >= MIN_STABLE_READINGS)
                    {
                        var avgFlow = _flowHistory.Average();
                        var waitTime = (DateTime.Now - startTime).TotalSeconds;

                        AddCycleLog($"│ ✅ 检测到流量稳定!");
                        AddCycleLog($"│ 平均流量: {avgFlow:F2} g/min (最近{_flowHistory.Count}次读数)");
                        AddCycleLog($"│ 稳定时间: {waitTime:F1}秒");

                        return (avgFlow, waitTime, readingCount);
                    }

                    // 异常情况检测
                    if (currentFlow < 0 || currentFlow > MAX_FLOW_RATE * 1.5)
                    {
                        AddCycleLog($"│ ⚠️ 异常读数: {currentFlow:F2} g/min");
                        continue;
                    }

                    // 系统响应超时检测
                    if (readingCount > 5 && hasValidReading)
                    {
                        var avgRecentChange = CalculateRecentChangeRate();
                        if (Math.Abs(avgRecentChange) < 0.1 && Math.Abs(currentFlow - targetFlow) > targetFlow * 0.1)
                        {
                            AddCycleLog($"│ 🔍 系统可能卡住，平均变化率: {avgRecentChange:F3}%/s");
                        }
                    }

                    lastFlow = currentFlow;
                    hasValidReading = true;

                    // 间隔等待
                    await Task.Delay(FLOW_READING_INTERVAL_MS);
                }

                // 超时处理
                var timeoutFlow = _flowHistory.Count > 0 ? _flowHistory.Average() : lastFlow;
                var totalWaitTime = (DateTime.Now - startTime).TotalSeconds;

                AddCycleLog($"│ ⏰ 监测超时 ({totalWaitTime:F1}秒)");
                AddCycleLog($"│ 使用最后平均值: {timeoutFlow:F2} g/min");

                return (timeoutFlow, totalWaitTime, readingCount);
            }
            catch (Exception ex)
            {
                AddCycleLog($"│ ❌ 智能检测异常: {ex.Message}");
                var errorWaitTime = (DateTime.Now - startTime).TotalSeconds;
                return (lastFlow, errorWaitTime, readingCount);
            }
        }

        /// <summary>
        /// 分析流量稳定性
        /// </summary>
        private (bool isStable, double changeRate, string trend) AnalyzeFlowStability(double currentFlow, double targetFlow)
        {
            if (_flowHistory.Count < 3)
                return (false, 0, "数据不足");

            var recentFlows = _flowHistory.TakeLast(3).ToArray();

            // 计算变化率 (每秒变化百分比)
            double maxChange = 0;
            for (int i = 1; i < recentFlows.Length; i++)
            {
                if (recentFlows[i - 1] > 0)
                {
                    double change = Math.Abs(recentFlows[i] - recentFlows[i - 1]) / recentFlows[i - 1] * 100;
                    maxChange = Math.Max(maxChange, change);
                }
            }

            // 计算趋势
            string trend = "稳定";
            if (recentFlows.Length >= 3)
            {
                if (recentFlows[2] > recentFlows[0] * 1.02) trend = "上升";
                else if (recentFlows[2] < recentFlows[0] * 0.98) trend = "下降";
            }

            // 判断是否稳定
            bool isStable = maxChange < STABILITY_THRESHOLD &&
                           recentFlows.All(f => Math.Abs(f - currentFlow) / Math.Max(currentFlow, 0.1) * 100 < STABILITY_THRESHOLD);

            return (isStable, maxChange, trend);
        }

        /// <summary>
        /// 读取当前流量
        /// </summary>
        private async Task<double> ReadCurrentFlowAsync()
        {
            try
            {
                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                {
                    return await ConnectionManager.ReadAsync<double>(ComId, "_FT0101_g_m");
                }
                else if (IsSimulationMode)
                {
                    lock (_stateLock)
                    {
                        // 模拟更真实的流量响应
                        var targetResponse = _lastPumpSpeedOutput * 0.95;
                        var random = new Random();

                        // 模拟系统响应延迟和噪声
                        _currentFlow = _currentFlow * 0.85 + targetResponse * 0.15;
                        _currentFlow += (random.NextDouble() - 0.5) * 0.1; // 小噪声
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

        /// <summary>
        /// 计算最近变化率
        /// </summary>
        private double CalculateRecentChangeRate()
        {
            if (_flowHistory.Count < 3) return 0;

            var recent = _flowHistory.TakeLast(3).ToArray();
            double totalChange = 0;
            int changeCount = 0;

            for (int i = 1; i < recent.Length; i++)
            {
                if (recent[i - 1] > 0)
                {
                    totalChange += Math.Abs(recent[i] - recent[i - 1]) / recent[i - 1] * 100;
                    changeCount++;
                }
            }

            return changeCount > 0 ? totalChange / changeCount : 0;
        }

        /// <summary>
        /// 根据泵速变化幅度和系统状态计算动态等待时间（备用方案）
        /// </summary>
        private int CalculateDynamicStabilizeTime(double targetFlow)
        {
            try
            {
                // 获取当前泵速（如果可能）
                double currentPumpSpeed = _lastPumpSpeedOutput;
                double expectedPumpSpeed = targetFlow * FLOW_TO_SPEED_RATIO;

                // 计算泵速变化幅度
                double speedChange = Math.Abs(expectedPumpSpeed - _previousPumpSpeed);
                double speedChangePercent = _previousPumpSpeed > 0 ?
                    (speedChange / _previousPumpSpeed) * 100 : 100;

                // 基于变化幅度计算等待时间
                int waitTime = BASE_STABILIZE_DELAY_MS;

                if (speedChange > 15)
                {
                    // 大幅度变化：需要更长时间
                    waitTime = (int)(BASE_STABILIZE_DELAY_MS * 2.5);
                    AddCycleLog($"│ 大幅度泵速变化: {speedChange:F1} RPM (>{15})");
                }
                else if (speedChange > 8)
                {
                    // 中等变化
                    waitTime = (int)(BASE_STABILIZE_DELAY_MS * 1.8);
                    AddCycleLog($"│ 中等泵速变化: {speedChange:F1} RPM ({8}-{15})");
                }
                else if (speedChange > 3)
                {
                    // 小变化
                    waitTime = (int)(BASE_STABILIZE_DELAY_MS * 1.3);
                    AddCycleLog($"│ 小幅泵速变化: {speedChange:F1} RPM ({3}-{8})");
                }
                else if (speedChange > 1)
                {
                    // 微调
                    waitTime = (int)(BASE_STABILIZE_DELAY_MS * 1.0);
                    AddCycleLog($"│ 微调: {speedChange:F1} RPM ({1}-{3})");
                }
                else
                {
                    // 几乎无变化
                    waitTime = MIN_STABILIZE_DELAY_MS;
                    AddCycleLog($"│ 极小变化: {speedChange:F1} RPM (<{1})");
                }

                // 考虑当前误差大小进行调整
                double currentFlow = 0;
                lock (_stateLock)
                {
                    currentFlow = _currentFlow;
                }

                if (currentFlow > 0)
                {
                    double errorPercent = Math.Abs(currentFlow - targetFlow) / targetFlow * 100;
                    if (errorPercent > 20)
                    {
                        waitTime = (int)(waitTime * 1.5); // 误差大时延长等待
                        AddCycleLog($"│ 误差较大({errorPercent:F1}%)，延长等待时间");
                    }
                }

                // 首次启动特殊处理
                if (_controlCycleCount <= 1)
                {
                    waitTime = (int)(waitTime * 1.5);
                    AddCycleLog($"│ 首次启动，延长等待时间");
                }

                // 限制在合理范围内
                waitTime = Math.Max(MIN_STABILIZE_DELAY_MS, Math.Min(MAX_STABILIZE_DELAY_MS, waitTime));

                AddCycleLog($"│ 上次泵速: {_previousPumpSpeed:F1} RPM");
                AddCycleLog($"│ 预期泵速: {expectedPumpSpeed:F1} RPM");
                AddCycleLog($"│ 速度变化: {speedChange:F1} RPM ({speedChangePercent:F1}%)");
                AddCycleLog($"│ 最终等待: {waitTime}ms");

                return waitTime;
            }
            catch (Exception ex)
            {
                AddCycleLog($"│ 计算等待时间异常: {ex.Message}，使用默认值");
                return BASE_STABILIZE_DELAY_MS;
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
                _logFilePath = Path.Combine(logDir, $"FlowControl_Smart_{timestamp}.log");

                WriteDetailedLog("=".PadRight(100, '='));
                WriteDetailedLog($"智能流量控制详细日志启动 - 超智能版本");
                WriteDetailedLog($"设备: {Name} ({ComId})");
                WriteDetailedLog($"日志文件: {_logFilePath}");
                WriteDetailedLog($"启动时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                WriteDetailedLog($"智能稳定检测: 阈值±{STABILITY_THRESHOLD}%, 最小{MIN_STABLE_READINGS}次读数");
                WriteDetailedLog($"控制周期间隔: {CONTROL_INTERVAL_MS}ms");
                WriteDetailedLog($"高精度控制: 容差±{FLOW_TOLERANCE_PERCENT}%, 死区±{DEAD_ZONE_PERCENT}%");
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
            _currentCycleLog.AppendLine($"┌─ 控制周期 #{_controlCycleCount} 开始 ─ {DateTime.Now:HH:mm:ss.fff} ────");
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
            _currentCycleLog.AppendLine($"└─ 控制周期 #{_controlCycleCount} 结束 ─ {DateTime.Now:HH:mm:ss.fff} ────");
            _currentCycleLog.AppendLine("");

            WriteDetailedLog(_currentCycleLog.ToString());
        }

        /// <summary>
        /// 记录PID计算详情
        /// </summary>
        private void LogPIDDetails(double targetFlow, double actualFlow, double currentError,
            double proportional, double integral, double derivative, double pidOutput,
            double currentPumpSpeed, double rawNewSpeed, double speedChange,
            double filteredSpeed, double finalSpeed)
        {
            AddCycleLog($"┌── PID计算详情 ──");
            AddCycleLog($"│   目标流量: {targetFlow:F2} g/min");
            AddCycleLog($"│   实际流量: {actualFlow:F2} g/min");
            AddCycleLog($"│   流量误差: {currentError:F2} g/min");
            AddCycleLog($"│   误差百分比: {Math.Abs(currentError / targetFlow) * 100:F3}%");
            AddCycleLog($"│   ───");
            AddCycleLog($"│   P项 (Kp={KP}): {proportional:F3}");
            AddCycleLog($"│   I项 (Ki={KI}): {integral:F3} (累积误差={_integralError:F3})");
            AddCycleLog($"│   D项 (Kd={KD}): {derivative:F3}");
            AddCycleLog($"│   PID输出: {pidOutput:F3}");
            AddCycleLog($"│   ───");
            AddCycleLog($"│   当前泵速: {currentPumpSpeed:F1} RPM");
            AddCycleLog($"│   原始计算: {rawNewSpeed:F1} RPM");
            AddCycleLog($"│   变化限制: {speedChange:F1} RPM (最大±{MAX_SPEED_CHANGE_PER_CYCLE})");
            AddCycleLog($"│   输出滤波: {filteredSpeed:F1} RPM (系数={SPEED_FILTER_FACTOR})");
            AddCycleLog($"│   最终输出: {finalSpeed:F1} RPM");
            AddCycleLog($"└──────────────");
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
                    InfoLog("ModbusTCP流量计没有活跃状态需要暂停");
                    return true;
                }
                InfoLog($"暂停ModbusTCP流量计的 {_activeStates.Count} 个活跃状态");
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
                InfoLog(allSuccess ? "ModbusTCP流量计所有活跃状态已暂停" : "ModbusTCP流量计部分活跃状态暂停失败");
                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP流量计暂停活跃状态异常", ex);
                return false;
            }
        }

        public virtual async Task<bool> ResumeAllPausedStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_pausedStates.Any())
                {
                    InfoLog("ModbusTCP流量计没有暂停的状态需要恢复");
                    return true;
                }
                InfoLog($"恢复ModbusTCP流量计的 {_pausedStates.Count} 个暂停的状态");
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

                InfoLog(allSuccess ? "ModbusTCP流量计所有暂停状态已恢复" : "ModbusTCP流量计部分暂停状态恢复失败");
                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP流量计恢复暂停状态异常", ex);
                return false;
            }
        }

        public virtual async Task<bool> StopAllActiveStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_activeStates.Any() && !_pausedStates.Any())
                {
                    InfoLog("ModbusTCP流量计没有活跃或暂停的状态需要停止");
                    return true;
                }
                InfoLog($"停止ModbusTCP流量计的 {_activeStates.Count} 个活跃状态和 {_pausedStates.Count} 个暂停状态");
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

                InfoLog(allSuccess ? "ModbusTCP流量计所有状态已停止" : "ModbusTCP流量计部分状态停止失败");
                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP流量计停止状态异常", ex);
                return false;
            }
        }

        #endregion

        #region IFlowLifecycleAware Implementation

        public virtual async Task<bool> OnFlowStartedAsync(FlowStartContext context)
        {
            try
            {
                InfoLog($"ModbusTCP流量计流程开始: {context.FlowName}");

                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                if (!IsConnected)
                {
                    WarnLog("ModbusTCP流量计设备未连接，但流程已开始");
                }

                InfoLog("ModbusTCP流量计设备已准备好执行流程");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP流量计流程开始处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowCompletedAsync(FlowCompletionContext context)
        {
            try
            {
                InfoLog($"ModbusTCP流量计流程完成: {context.FlowName}, 成功: {context.IsSuccessful}");
                StopAutoControl();
                await StopAllActiveStatesAsync();
                InfoLog("ModbusTCP流量计流程完成处理结束");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP流量计流程完成处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowFailedAsync(FlowFailureContext context)
        {
            try
            {
                ErrorLog($"ModbusTCP流量计流程失败: {context.FlowName}, 错误: {context.ErrorMessage}");
                StopAutoControl();
                await StopAllActiveStatesAsync();
                InfoLog("ModbusTCP流量计流程失败处理完成");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP流量计流程失败处理异常", ex);
                return false;
            }
        }

        #endregion

        public FlowMeter_ModbusTCP()
        {
            _logger = LogManager.GetLogger<FlowMeter_ModbusTCP>();

            Name = "P0101流量计";
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

            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/D07-19BMassFlowMeter.png";

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

        private void InitializeCommands()
        {
            // 1. 获取基准流量
            Commands.Add(new DeviceCommand
            {
                Name = "获取基准流量",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/flowrate.png",
                HelpText = "读取ModbusTCP地址40030获取基准流量",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("BaseFlow", 0, MAX_FLOW_RATE, 0, "基准流量(g/min)") { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("获取基准流量", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double baseFlow = 0;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            baseFlow = await ConnectionManager.ReadAsync<double>(ComId, "_FT0101_g_m");
                            InfoLog($"从ModbusTCP地址40030获取基准流量: {baseFlow} g/min");
                        }
                        else if (IsSimulationMode)
                        {
                            lock (_stateLock)
                            {
                                baseFlow = _currentFlow;
                            }
                            InfoLog($"模拟模式获取基准流量: {baseFlow} g/min");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"获取基准流量失败: {ex.Message}");
                        throw;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new NumberParameter("BaseFlow", 0, MAX_FLOW_RATE, Math.Round(baseFlow, 2), "基准流量(g/min)") { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });

            // 2. 设置基准流量
            Commands.Add(new DeviceCommand
            {
                Name = "设置基准流量",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/speed_set.png",
                HelpText = "设置目标流量并启动智能PID控制，使用实时稳定性检测，精度±0.2%",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TargetFlow", 0, MAX_FLOW_RATE, 50.0, "目标基准流量(g/min)"),
                        new BooleanParameter("EnableAutoControl", true, "启用智能自动控制")
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("Success", false, "设置成功") { IsVisibleInVariableManager = true },
                        new NumberParameter("SetFlow", 0, MAX_FLOW_RATE, 0, "设置的流量(g/min)") { IsVisibleInVariableManager = true },
                        new NumberParameter("CalculatedSpeed", 0, MAX_PUMP_SPEED, 0, "计算的泵速(RPM)") { IsVisibleInVariableManager = true },
                        new BooleanParameter("AutoControlStarted", false, "智能控制已启动") { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("设置基准流量", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double targetFlow = parameters.GetValue<double>("TargetFlow");
                    bool enableAutoControl = parameters.GetValue<bool>("EnableAutoControl");
                    bool success = false;
                    double calculatedSpeed = 0;
                    bool autoControlStarted = false;

                    targetFlow = Math.Max(0, Math.Min(MAX_FLOW_RATE, targetFlow));

                    try
                    {
                        lock (_stateLock)
                        {
                            _targetFlow = targetFlow;
                        }

                        calculatedSpeed = CalculateRequiredPumpSpeed(targetFlow);
                        InfoLog($"计算所需初始泵速: {calculatedSpeed} RPM (基于目标流量: {targetFlow} g/min)");

                        var pumpDeviceId = Parameters.GetValue<string>("PumpDeviceId");
                        var pumpSetSuccess = await SetPumpSpeedAsync(pumpDeviceId, calculatedSpeed);

                        if (pumpSetSuccess)
                        {
                            InfoLog($"初始泵速设置成功: {calculatedSpeed} RPM");
                            success = true;

                            if (enableAutoControl && targetFlow > 0)
                            {
                                StartAutoControl();
                                autoControlStarted = true;
                                InfoLog($"智能PID控制已启动 - 目标流量: {targetFlow} g/min, 容差: ±{FLOW_TOLERANCE_PERCENT}%, 死区: ±{DEAD_ZONE_PERCENT}%");
                            }
                        }
                        else
                        {
                            WarnLog($"初始泵速设置失败: {calculatedSpeed} RPM");
                        }

                        if (success && targetFlow > 0)
                        {
                            AddActiveState("FlowControlActive", "设置基准流量", "停止流量控制", stateData: targetFlow);
                        }
                        else if (success && targetFlow == 0)
                        {
                            StopAutoControl();
                            RemoveActiveState("FlowControlActive");
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"设置基准流量失败: {ex.Message}");
                        throw;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new BooleanParameter("Success", success, "设置成功") { IsVisibleInVariableManager = true },
                            new NumberParameter("SetFlow", 0, MAX_FLOW_RATE, targetFlow, "设置的流量(g/min)") { IsVisibleInVariableManager = true },
                            new NumberParameter("CalculatedSpeed", 0, MAX_PUMP_SPEED, Math.Round(calculatedSpeed, 1), "计算的泵速(RPM)") { IsVisibleInVariableManager = true },
                            new BooleanParameter("AutoControlStarted", autoControlStarted, "智能控制已启动") { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });

            // 3. 停止流量控制
            Commands.Add(new DeviceCommand
            {
                Name = "停止流量控制",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/off.png",
                HelpText = "停止智能流量控制并设置泵速为0",
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
                    ComId = Parameters.GetValue<string>("DeviceId");
                    bool success = false;

                    try
                    {
                        StopAutoControl();
                        InfoLog("智能自动控制已停止");

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
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"停止流量控制失败: {ex.Message}");
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

            // 4. 获取控制状态
            Commands.Add(new DeviceCommand
            {
                Name = "获取控制状态",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/status.png",
                HelpText = "获取智能流量控制系统当前状态",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("AutoControlEnabled", false, "智能控制启用") { IsVisibleInVariableManager = true },
                        new NumberParameter("TargetFlow", 0, MAX_FLOW_RATE, 0, "目标流量(g/min)") { IsVisibleInVariableManager = true },
                        new NumberParameter("CurrentFlow", 0, MAX_FLOW_RATE, 0, "当前流量(g/min)") { IsVisibleInVariableManager = true },
                        new NumberParameter("FlowDeviation", -MAX_FLOW_RATE, MAX_FLOW_RATE, 0, "流量偏差(g/min)") { IsVisibleInVariableManager = true },
                        new NumberParameter("DeviationPercent", -100, 100, 0, "偏差百分比(%)") { IsVisibleInVariableManager = true },
                        new NumberParameter("CurrentPumpSpeed", 0, MAX_PUMP_SPEED, 0, "当前泵速(RPM)") { IsVisibleInVariableManager = true },
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
                    double currentPumpSpeed = 0;
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

                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            try
                            {
                                currentFlow = await ConnectionManager.ReadAsync<double>(ComId, "_FT0101_g_m");
                                lock (_stateLock)
                                {
                                    _currentFlow = currentFlow;
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

                        controlStatus = autoControlEnabled ?
                            $"智能PID控制中 (目标:{targetFlow:F1}g/min, 偏差:{deviationPercent:F3}%, 容差:±{FLOW_TOLERANCE_PERCENT}%, 死区:±{DEAD_ZONE_PERCENT}%)" :
                            "手动模式";

                        InfoLog($"智能流量控制状态: 自动={autoControlEnabled}, 目标={targetFlow:F2}, 实际={currentFlow:F2}, " +
                               $"偏差={deviationPercent:F3}%, 泵速={currentPumpSpeed:F1}");
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"获取控制状态失败: {ex.Message}");
                        controlStatus = $"状态获取失败: {ex.Message}";
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new BooleanParameter("AutoControlEnabled", autoControlEnabled, "智能控制启用") { IsVisibleInVariableManager = true },
                            new NumberParameter("TargetFlow", 0, MAX_FLOW_RATE, Math.Round(targetFlow, 3), "目标流量(g/min)") { IsVisibleInVariableManager = true },
                            new NumberParameter("CurrentFlow", 0, MAX_FLOW_RATE, Math.Round(currentFlow, 3), "当前流量(g/min)") { IsVisibleInVariableManager = true },
                            new NumberParameter("FlowDeviation", -MAX_FLOW_RATE, MAX_FLOW_RATE, Math.Round(flowDeviation, 3), "流量偏差(g/min)") { IsVisibleInVariableManager = true },
                            new NumberParameter("DeviationPercent", -100, 100, Math.Round(deviationPercent, 3), "偏差百分比(%)") { IsVisibleInVariableManager = true },
                            new NumberParameter("CurrentPumpSpeed", 0, MAX_PUMP_SPEED, Math.Round(currentPumpSpeed, 1), "当前泵速(RPM)") { IsVisibleInVariableManager = true },
                            new StringParameter("ControlStatus", controlStatus, "控制状态") { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });
        }

        #region 智能自动控制逻辑 - 超智能版本

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
                _flowHistory.Clear();

                _autoControlEnabled = true;
                _controlTimer = new Timer(AutoControlCallback, null, CONTROL_INTERVAL_MS, CONTROL_INTERVAL_MS);

                var startDetails = new
                {
                    间隔 = $"{CONTROL_INTERVAL_MS}ms",
                    容差 = $"±{FLOW_TOLERANCE_PERCENT}%",
                    死区 = $"±{DEAD_ZONE_PERCENT}%",
                    PID参数 = $"Kp={KP}, Ki={KI}, Kd={KD}",
                    智能检测 = $"阈值±{STABILITY_THRESHOLD}%, 最小{MIN_STABLE_READINGS}次读数",
                    目标流量 = $"{_targetFlow}g/min"
                };

                LogControlStateChange("智能流量控制启动 (超智能版本)", startDetails);

                WriteDetailedLog("");
                WriteDetailedLog("★".PadRight(100, '★'));
                WriteDetailedLog($"智能控制启动 (超智能版本) - {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                WriteDetailedLog($"目标流量: {_targetFlow} g/min");
                WriteDetailedLog($"控制参数: 间隔={CONTROL_INTERVAL_MS}ms, 容差=±{FLOW_TOLERANCE_PERCENT}%, 死区=±{DEAD_ZONE_PERCENT}%");
                WriteDetailedLog($"PID参数: Kp={KP}, Ki={KI}, Kd={KD}");
                WriteDetailedLog($"智能检测: 阈值±{STABILITY_THRESHOLD}%, 最小{MIN_STABLE_READINGS}次读数, 最大{MAX_MONITORING_TIME_MS / 1000}秒");
                WriteDetailedLog("★".PadRight(100, '★'));
                WriteDetailedLog("");
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
                    版本 = "超智能版本"
                };

                LogControlStateChange("智能流量控制停止", stopDetails);

                WriteDetailedLog("");
                WriteDetailedLog("■".PadRight(100, '■'));
                WriteDetailedLog($"智能控制停止 - {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                WriteDetailedLog($"运行时长: {duration.TotalMinutes:F1}分钟");
                WriteDetailedLog($"总控制周期: {_controlCycleCount}次");
                WriteDetailedLog($"平均周期间隔: {duration.TotalMilliseconds / Math.Max(_controlCycleCount, 1):F0}ms");
                WriteDetailedLog($"版本: 超智能实时检测版本");
                WriteDetailedLog("■".PadRight(100, '■'));
                WriteDetailedLog("");

                _previousError = 0;
                _integralError = 0;
                _stableCount = 0;
                _lastPumpSpeedOutput = 0;
                _previousPumpSpeed = 0;
                _flowHistory.Clear();
            }
        }

        /// <summary>
        /// 智能自动控制回调
        /// </summary>
        private async void AutoControlCallback(object state)
        {
            try
            {
                await PerformSmartAutoControlAsync();
            }
            catch (Exception ex)
            {
                WriteDetailedLog($"[ERROR] 智能控制执行异常: {ex.Message}");
                WriteDetailedLog($"[ERROR] 异常堆栈: {ex.StackTrace}");
                ErrorLog("智能控制执行异常", ex);
            }
        }

        /// <summary>
        /// 执行智能自动控制逻辑 - 超智能版本
        /// </summary>
        private async Task PerformSmartAutoControlAsync()
        {
            StartCycleLog();

            double targetFlow;
            bool controlEnabled;

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
            }

            if (targetFlow <= 0)
            {
                AddCycleLog($"目标流量无效 ({targetFlow})，跳过本周期");
                EndCycleLog("目标流量无效");
                return;
            }

            AddCycleLog($"目标流量: {targetFlow:F2} g/min");

            try
            {
                // *** 智能等待流量稳定 ***
                AddCycleLog("┌── 步骤1: 智能等待流量稳定 ──");
                var (finalFlow, stabilizeTime, readings) = await WaitForFlowStabilityAsync(targetFlow);
                AddCycleLog($"│ 流量已稳定: {finalFlow:F2} g/min");
                AddCycleLog($"│ 实际等待: {stabilizeTime:F1}秒");
                AddCycleLog($"│ 监测次数: {readings}次");
                AddCycleLog($"└─ 智能稳定检测完成");

                // 直接使用检测到的稳定流量
                double actualFlow = finalFlow;

                // 3. 计算流量偏差
                AddCycleLog("┌── 步骤2: 计算流量偏差 ──");
                double flowError = actualFlow - targetFlow;
                double errorPercent = Math.Abs(flowError / targetFlow) * 100.0;

                AddCycleLog($"│ 流量误差: {flowError:F2} g/min");
                AddCycleLog($"│ 误差百分比: {errorPercent:F3}%");
                AddCycleLog($"│ 死区范围: ±{DEAD_ZONE_PERCENT}%");
                AddCycleLog($"│ 容差范围: ±{FLOW_TOLERANCE_PERCENT}%");

                // 4. 高精度死区控制
                if (errorPercent <= DEAD_ZONE_PERCENT)
                {
                    _stableCount++;
                    AddCycleLog($"│ ✅ 判断: 在死区内 ({errorPercent:F3}% ≤ {DEAD_ZONE_PERCENT}%)");
                    AddCycleLog($"│ 稳定计数: {_stableCount}");

                    var oldIntegral = _integralError;
                    _integralError *= 0.9;
                    AddCycleLog($"│ 积分误差衰减: {oldIntegral:F3} → {_integralError:F3}");
                    AddCycleLog($"│ 🎯 高精度死区稳定 - 保持当前泵速");

                    EndCycleLog($"高精度死区稳定 (偏差{errorPercent:F3}% ≤ {DEAD_ZONE_PERCENT}%) - 连续{_stableCount}次");
                    return;
                }

                // 5. 高精度容差控制
                if (errorPercent <= FLOW_TOLERANCE_PERCENT)
                {
                    _stableCount++;
                    AddCycleLog($"│ ✅ 判断: 在容差内 ({errorPercent:F3}% ≤ {FLOW_TOLERANCE_PERCENT}%)");
                    AddCycleLog($"│ 稳定计数: {_stableCount}/{STABLE_CYCLES_REQUIRED}");

                    if (_stableCount >= STABLE_CYCLES_REQUIRED)
                    {
                        AddCycleLog($"│ 🎯 达到连续稳定要求 ({_stableCount} ≥ {STABLE_CYCLES_REQUIRED})");
                        _integralError = 0;
                        AddCycleLog($"│ 积分误差已清零 - 系统高精度稳定");

                        EndCycleLog($"高精度流量稳定 (连续{_stableCount}次在容差内)");
                        return;
                    }
                    else
                    {
                        AddCycleLog($"│ ⏳ 需要连续{STABLE_CYCLES_REQUIRED}次稳定，当前{_stableCount}次");
                    }
                }
                else
                {
                    AddCycleLog($"│ ⚠️ 判断: 超出容差 ({errorPercent:F3}% > {FLOW_TOLERANCE_PERCENT}%)");
                    _stableCount = 0;
                    AddCycleLog($"│ 稳定计数已重置: {_stableCount}");
                }

                AddCycleLog($"└─ 需要调整泵速");

                // 6. 获取当前泵速
                AddCycleLog("┌── 步骤3: 获取当前泵速 ──");
                var pumpDeviceId = Parameters.GetValue<string>("PumpDeviceId");
                double currentPumpSpeed;
                try
                {
                    var pumpReadStart = DateTime.Now;
                    currentPumpSpeed = await ConnectionManager.ReadAsync<double>(pumpDeviceId, "P0101泵速");
                    var pumpReadDuration = DateTime.Now - pumpReadStart;
                    AddCycleLog($"│ 读取成功: {currentPumpSpeed:F1} RPM (耗时: {pumpReadDuration.TotalMilliseconds:F0}ms)");
                }
                catch (Exception ex)
                {
                    currentPumpSpeed = _lastPumpSpeedOutput;
                    AddCycleLog($"│ 读取失败，使用上次输出值: {currentPumpSpeed:F1} RPM");
                    AddCycleLog($"│ 错误: {ex.Message}");
                }
                AddCycleLog($"└─ 当前泵速: {currentPumpSpeed:F1} RPM");

                // 7. 高精度PID控制计算
                AddCycleLog("┌── 步骤4: 高精度PID控制计算 ──");
                double newPumpSpeed;

                if (Math.Abs(errorPercent) < 1.0) // 高精度精细调整
                {
                    AddCycleLog($"│ 模式: 高精度精细调整 (误差{errorPercent:F3}% < 1%)");
                    newPumpSpeed = CalculateHighPrecisionFineAdjustment(targetFlow, actualFlow, flowError, currentPumpSpeed);
                }
                else
                {
                    AddCycleLog($"│ 模式: 高精度PID控制 (误差{errorPercent:F3}% ≥ 1%)");
                    newPumpSpeed = CalculateHighPrecisionPIDPumpSpeed(targetFlow, actualFlow, flowError, currentPumpSpeed);
                }

                // *** 高精度稳定性保护 ***
                if (Math.Abs(flowError) > targetFlow * 0.3) // 误差超过30%时
                {
                    AddCycleLog($"│ ⚠️ 误差过大({errorPercent:F1}% > 30%)，启用高精度保护模式");

                    // 使用更保守的调整
                    double conservativeAdjustment = Math.Sign(flowError) * Math.Min(1.0, Math.Abs(flowError) * 0.05);
                    newPumpSpeed = currentPumpSpeed - conservativeAdjustment;
                    newPumpSpeed = Math.Max(0, Math.Min(MAX_PUMP_SPEED, newPumpSpeed));

                    AddCycleLog($"│ 高精度保护模式调整: {currentPumpSpeed:F1} → {newPumpSpeed:F1} RPM (变化: {conservativeAdjustment:F1})");
                }
                AddCycleLog($"└─ 计算的新泵速: {newPumpSpeed:F1} RPM");

                // 8. 设置新泵速
                AddCycleLog("┌── 步骤5: 设置新泵速 ──");
                var pumpSetStart = DateTime.Now;
                bool adjustSuccess = await SetPumpSpeedAsync(pumpDeviceId, newPumpSpeed);
                var pumpSetDuration = DateTime.Now - pumpSetStart;

                if (adjustSuccess)
                {
                    _lastPumpSpeedOutput = newPumpSpeed;
                    _previousPumpSpeed = newPumpSpeed; // 更新历史泵速
                    AddCycleLog($"│ ✅ 设置成功: {newPumpSpeed:F1} RPM (耗时: {pumpSetDuration.TotalMilliseconds:F0}ms)");
                    AddCycleLog($"│ 泵速变化: {currentPumpSpeed:F1} → {newPumpSpeed:F1} RPM");

                    var summary = $"智能调整: {currentPumpSpeed:F1}→{newPumpSpeed:F1}RPM, 流量偏差: {errorPercent:F3}%";
                    EndCycleLog(summary);
                }
                else
                {
                    AddCycleLog($"│ ❌ 设置失败: {newPumpSpeed:F1} RPM");
                    EndCycleLog($"泵速设置失败: {newPumpSpeed:F1} RPM");
                }
                AddCycleLog($"└─ 设置结果: {(adjustSuccess ? "成功" : "失败")}");
            }
            catch (Exception ex)
            {
                AddCycleLog($"[ERROR] 智能控制异常: {ex.Message}");
                EndCycleLog($"异常: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 高精度精细调整PID计算
        /// </summary>
        private double CalculateHighPrecisionFineAdjustment(double targetFlow, double actualFlow, double currentError, double currentPumpSpeed)
        {
            // 使用更保守的高精度参数
            double fineKp = KP * 0.15; // 更小的比例增益 0.12
            double fineKi = KI * 0.2;  // 更小的积分增益 0.01
            double fineKd = KD * 0.15; // 更小的微分增益 0.012

            AddCycleLog($"│ 高精度精细参数: Kp={fineKp:F3}, Ki={fineKi:F3}, Kd={fineKd:F3}");

            // 计算PID输出
            double proportional = fineKp * currentError;
            _integralError += currentError;
            _integralError = Math.Max(-2, Math.Min(2, _integralError)); // 更小的积分限制
            double integral = fineKi * _integralError;
            double derivative = fineKd * (currentError - _previousError);
            _previousError = currentError;

            double pidOutput = proportional + integral + derivative;
            double rawNewSpeed = currentPumpSpeed - pidOutput;

            // 更小的变化限制
            double speedChange = rawNewSpeed - currentPumpSpeed;
            if (Math.Abs(speedChange) > 0.5) // 限制为0.5 RPM
            {
                speedChange = Math.Sign(speedChange) * 0.5;
                rawNewSpeed = currentPumpSpeed + speedChange;
            }

            double newPumpSpeed = Math.Max(0, Math.Min(MAX_PUMP_SPEED, rawNewSpeed));

            AddCycleLog($"│ 高精度PID: P={proportional:F3}, I={integral:F3}, D={derivative:F3}");
            AddCycleLog($"│ 高精度调整: {currentPumpSpeed:F1} → {newPumpSpeed:F1} RPM (变化: {speedChange:F1})");

            return newPumpSpeed;
        }

        /// <summary>
        /// 高精度PID控制算法
        /// </summary>
        private double CalculateHighPrecisionPIDPumpSpeed(double targetFlow, double actualFlow, double currentError, double currentPumpSpeed)
        {
            try
            {
                // 高精度PID控制计算
                double proportional = KP * currentError;

                var oldIntegralError = _integralError;
                _integralError += currentError;

                // 更严格的积分限幅
                double maxIntegral = 5.0; // 从15降低到5
                _integralError = Math.Max(-maxIntegral, Math.Min(maxIntegral, _integralError));

                // 更强的积分衰减
                if (Math.Abs(currentError) < targetFlow * 0.002) // 从0.05降低到0.002
                {
                    _integralError *= 0.95; // 更强的衰减
                }

                double integral = KI * _integralError;
                double derivative = KD * (currentError - _previousError);
                _previousError = currentError;

                double pidOutput = proportional + integral + derivative;
                double rawNewSpeed = currentPumpSpeed - pidOutput;

                // 限制变化幅度
                double speedChange = rawNewSpeed - currentPumpSpeed;
                if (Math.Abs(speedChange) > MAX_SPEED_CHANGE_PER_CYCLE)
                {
                    speedChange = Math.Sign(speedChange) * MAX_SPEED_CHANGE_PER_CYCLE;
                    rawNewSpeed = currentPumpSpeed + speedChange;
                }

                // 更强的输出滤波
                double filteredSpeed = _lastPumpSpeedOutput * (1 - SPEED_FILTER_FACTOR) + rawNewSpeed * SPEED_FILTER_FACTOR;
                double finalSpeed = Math.Max(0, Math.Min(MAX_PUMP_SPEED, filteredSpeed));

                // 记录详细的PID计算过程
                LogPIDDetails(targetFlow, actualFlow, currentError, proportional, integral, derivative,
                    pidOutput, currentPumpSpeed, rawNewSpeed, speedChange, filteredSpeed, finalSpeed);

                return finalSpeed;
            }
            catch (Exception ex)
            {
                AddCycleLog($"[ERROR] 高精度PID计算异常: {ex.Message}");
                return currentPumpSpeed;
            }
        }

        /// <summary>
        /// 计算所需泵速
        /// </summary>
        private double CalculateRequiredPumpSpeed(double targetFlow, double actualFlow = 0, double deviation = 0)
        {
            // 更保守的初始设置，为目标的80%，让系统缓慢接近
            double baseSpeed = targetFlow * FLOW_TO_SPEED_RATIO * 0.8;
            baseSpeed = Math.Max(0, Math.Min(MAX_PUMP_SPEED, baseSpeed));

            WriteDetailedLog($"[初始泵速计算] 目标流量={targetFlow:F2}g/min → 基础泵速={baseSpeed:F1}RPM (比例={FLOW_TO_SPEED_RATIO * 0.8:F1}, 保守启动)");

            return baseSpeed;
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

                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
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
                        await Task.Delay(500); // 0.5秒响应延迟
                        lock (_stateLock)
                        {
                            var targetResponse = speed * 0.98; // 更精确的响应
                            var oldCurrentFlow = _currentFlow;
                            _currentFlow = _currentFlow * 0.7 + targetResponse * 0.3;
                            _currentFlow = Math.Max(0, Math.Min(MAX_FLOW_RATE, _currentFlow));

                            WriteDetailedLog($"[智能模拟响应] 泵速={speed:F1}RPM → 目标响应={targetResponse:F2}g/min → 流量更新: {oldCurrentFlow:F2} → {_currentFlow:F2}g/min");
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
                InfoLog($"ModbusTCP流量计添加活跃状态: {stateName}");
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
                    InfoLog($"ModbusTCP流量计移除活跃状态: {stateName}");
                }

                var pausedState = _pausedStates.FirstOrDefault(s => s.StateName == stateName);
                if (pausedState != null)
                {
                    _pausedStates.Remove(pausedState);
                    InfoLog($"ModbusTCP流量计移除暂停状态: {stateName}");
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
                InfoLog($"暂停ModbusTCP流量计状态: {state.StateName}");

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
                ErrorLog($"ModbusTCP流量计暂停状态失败: {state.StateName}", ex);
                return false;
            }
        }

        private async Task<bool> ResumeStateAsync(DeviceActiveState state)
        {
            try
            {
                InfoLog($"恢复ModbusTCP流量计状态: {state.StateName}");

                if (!string.IsNullOrEmpty(state.ResumeCommand))
                {
                    var resumeParams = new DeviceParameters();
                    if (state.StateData is double flow)
                    {
                        resumeParams.Variables.Add(new NumberParameter("TargetFlow", 0, MAX_FLOW_RATE, flow, "目标流量"));
                        resumeParams.Variables.Add(new BooleanParameter("EnableAutoControl", true, "启用智能自动控制"));
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
                ErrorLog($"ModbusTCP流量计恢复状态失败: {state.StateName}", ex);
                return false;
            }
        }

        private async Task<bool> StopStateAsync(DeviceActiveState state)
        {
            try
            {
                InfoLog($"停止ModbusTCP流量计状态: {state.StateName}");

                if (!string.IsNullOrEmpty(state.DeactivateCommand))
                {
                    await ExecuteDeviceCommandByNameAsync(state.DeactivateCommand);
                }

                return true;
            }
            catch (Exception ex)
            {
                ErrorLog($"ModbusTCP流量计停止状态失败: {state.StateName}", ex);
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
                InfoLog($"ModbusTCP流量计执行命令: {commandName}");
            }
            else
            {
                WarnLog($"ModbusTCP流量计未找到命令: {commandName}");
            }
        }

        #endregion

        #region 基类重写方法

        public override void Initialize()
        {
            WriteDetailedLog($"[Initialize] 开始初始化ModbusTCP流量计设备 (超智能版本)");

            try
            {
                base.Initialize();

                lock (_stateLock)
                {
                    _currentFlow = 0;
                    _targetFlow = 0;
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

                _flowHistory.Clear();

                WriteDetailedLog($"[Initialize] ModbusTCP流量计设备初始化完成 (超智能版本)");
                InfoLog("ModbusTCP流量计设备初始化完成 (超智能版本)");
            }
            catch (Exception ex)
            {
                WriteDetailedLog($"[Initialize ERROR] ModbusTCP流量计设备初始化失败: {ex.Message}");
                ErrorLog("ModbusTCP流量计设备初始化失败", ex);
                throw;
            }
        }

        public override void Shutdown()
        {
            WriteDetailedLog($"[Shutdown] 开始关闭ModbusTCP流量计设备");

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

                _flowHistory.Clear();

                base.Shutdown();
                WriteDetailedLog($"[Shutdown] ModbusTCP流量计设备关闭完成");
                InfoLog("ModbusTCP流量计设备关闭完成");
            }
            catch (Exception ex)
            {
                WriteDetailedLog($"[Shutdown ERROR] ModbusTCP流量计设备关闭失败: {ex.Message}");
                ErrorLog("ModbusTCP流量计设备关闭失败", ex);
                throw;
            }
        }

        protected override async Task<bool> OnConnectAsync()
        {
            WriteDetailedLog($"[OnConnect] ModbusTCP流量计连接");
            InfoLog("ModbusTCP流量计连接");
            await Task.Delay(100);
            return true;
        }

        protected override async Task<bool> OnDisconnectAsync()
        {
            WriteDetailedLog($"[OnDisconnect] ModbusTCP流量计断开连接");
            InfoLog("ModbusTCP流量计断开连接");
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
                _logger.LogInformation($"[ModbusTCP流量计命令-开始] Device={Name} Command={commandName}");

                var sw = Stopwatch.StartNew();
                try
                {
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();

                    WriteDetailedLog($"[Command] 命令执行完成: {commandName} (耗时: {sw.ElapsedMilliseconds}ms)");
                    _logger.LogInformation($"[ModbusTCP流量计命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    WriteDetailedLog($"[Command ERROR] 命令执行异常: {commandName} (耗时: {sw.ElapsedMilliseconds}ms) - {ex.Message}");
                    _logger.LogError(ex, $"[ModbusTCP流量计命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
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
    }
}