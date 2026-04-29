using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;

namespace TemperatureCirculator_ModbusTCP
{
    [Export(typeof(IDevice))]
    public class TemperatureCirculator_ModbusTCP : Device, IDeviceUIInteraction, IControllableDevice, IFlowLifecycleAware
    {
        public event EventHandler<DeviceControlStateChangedEventArgs> ControlStateChanged;
        private readonly ILogService _logger;

        // 设备运行状态
        private double _setTemperature = 25.0;
        private double _actualTemperature = 25.0;
        private bool _isCycleRunning = false;
        private bool _isHeatingRunning = false;
        private bool _isCoolingRunning = false;
        private DateTime _startTime;
        private readonly object _stateLock = new object();

        // 状态跟踪相关字段
        private readonly List<DeviceActiveState> _activeStates = new List<DeviceActiveState>();
        private readonly List<DeviceActiveState> _pausedStates = new List<DeviceActiveState>();
        private readonly object _statesLock = new object();

        // 状态轮询相关
        private CancellationTokenSource _statusPollingCts;
        private Task _statusPollingTask;
        private const int StatusPollingIntervalMs = 1000;
        // ═══ 命令节流(防止短时间连续发送命令导致设备拒绝/回滚)═══
        private DateTime _lastCommandTime = DateTime.MinValue;
        // ═══════════════════════════════════════════════════════
        //  单位常量(全项目统一标准)
        // ═══════════════════════════════════════════════════════
        private const string UnitTemperature = "℃";
        private readonly TimeSpan _minCommandInterval = TimeSpan.FromMilliseconds(800); // 命令最小间隔
        private readonly SemaphoreSlim _commandThrottleSemaphore = new SemaphoreSlim(1, 1);

        /// <summary>
        /// 命令节流:确保两次命令之间至少间隔 _minCommandInterval
        /// 用于防止高低温循环器在短时间内收到多条命令时静默回滚寄存器
        /// </summary>
        private async Task EnsureCommandIntervalAsync(string commandName)
        {
            await _commandThrottleSemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                var elapsed = DateTime.Now - _lastCommandTime;
                if (elapsed < _minCommandInterval)
                {
                    var waitTime = _minCommandInterval - elapsed;
                    InfoLog($"[命令节流] {commandName} 等待 {waitTime.TotalMilliseconds:F0}ms 以保证最小命令间隔");
                    await Task.Delay(waitTime).ConfigureAwait(false);
                }
                _lastCommandTime = DateTime.Now;
            }
            finally
            {
                _commandThrottleSemaphore.Release();
            }
        }
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
                    InfoLog("高低温循环器没有活跃状态需要暂停");
                    return true;
                }
                InfoLog($"暂停高低温循环器的 {_activeStates.Count} 个活跃状态");
            }

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
                InfoLog(allSuccess ? "高低温循环器所有活跃状态已暂停" : "高低温循环器部分活跃状态暂停失败");
                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("高低温循环器暂停活跃状态异常", ex);
                return false;
            }
        }

        public virtual async Task<bool> ResumeAllPausedStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_pausedStates.Any())
                {
                    InfoLog("高低温循环器没有暂停的状态需要恢复");
                    return true;
                }
                InfoLog($"恢复高低温循环器的 {_pausedStates.Count} 个暂停的状态");
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
                InfoLog(allSuccess ? "高低温循环器所有暂停状态已恢复" : "高低温循环器部分暂停状态恢复失败");
                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("高低温循环器恢复暂停状态异常", ex);
                return false;
            }
        }

        public virtual async Task<bool> StopAllActiveStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_activeStates.Any() && !_pausedStates.Any())
                {
                    InfoLog("高低温循环器没有活跃或暂停的状态需要停止");
                    return true;
                }
                InfoLog($"停止高低温循环器的 {_activeStates.Count} 个活跃状态和 {_pausedStates.Count} 个暂停状态");
            }

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

                InfoLog(allSuccess ? "高低温循环器所有状态已停止" : "高低温循环器部分状态停止失败");
                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("高低温循环器停止状态异常", ex);
                return false;
            }
        }

        #endregion

        #region IFlowLifecycleAware Implementation

        public virtual async Task<bool> OnFlowStartedAsync(FlowStartContext context)
        {
            try
            {
                InfoLog("========== 流程启动 ==========");
                InfoLog($"设备ID: {this.DeviceId}");
                InfoLog($"ComId: {this.ComId}");
                InfoLog($"设备哈希: {this.GetHashCode()}");
                InfoLog($"流程名称: {context.FlowName}");
                InfoLog("================================");

                InfoLog($"高低温循环器流程开始: {context.FlowName}");

                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                lock (_stateLock)
                {
                    _startTime = DateTime.Now;
                }

                if (!IsConnected)
                {
                    WarnLog("高低温循环器设备未连接，但流程已开始");
                }

                // ① 先立即读取一次当前设定温度，推送给 UI
                await ReadAndPublishSetTemperatureAsync();

                // ② 启动周期轮询（包含设定温度）
                StartStatusPolling();

                InfoLog("高低温循环器设备已准备好执行流程");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("高低温循环器流程开始处理失败", ex);
                return false;
            }
        }

        /// <summary>
        /// 单次读取设定温度并通过事件推送到 UI
        /// 在流程启动时调用，确保 TemperatureControlSystemControl 立即显示正确的设定值
        /// </summary>
        private async Task ReadAndPublishSetTemperatureAsync()
        {
            try
            {
                double setTemp = _setTemperature; // 默认回退到内存值

                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                    ConnectionManager != null && !IsSimulationMode)
                {
                    var deviceId = Parameters.GetValue<string>("DeviceId");
                    // TC0101 读 YY_SV_Set_1，TC0201 读 YY_SV_Set_2
                    string register = deviceId == "TC0101" ? "YY_SV_Set_1" : "YY_SV_Set_2";

                    setTemp = await ConnectionManager.ReadAsync<double>(ComId, register);
                    InfoLog($"流程启动时读取设定温度: 寄存器={register}, 值={setTemp:F1}℃");
                }
                else if (IsSimulationMode)
                {
                    lock (_stateLock)
                    {
                        setTemp = _setTemperature;
                    }
                    InfoLog($"模拟模式：使用内存设定温度 {setTemp:F1}℃");
                }
                else
                {
                    InfoLog("设备未连接且非模拟模式，跳过设定温度初始读取");
                    return;
                }

                // 更新内存值
                lock (_stateLock)
                {
                    _setTemperature = setTemp;
                }

                // 推送到 UI（触发 HandleTemperatureCirculatorControlState → SetTargetTemperature）
                RaiseControlStateChanged("SetTemperature", setTemp);
                InfoLog($"已推送设定温度到UI: {setTemp:F1}℃");
            }
            catch (Exception ex)
            {
                ErrorLog($"读取初始设定温度失败: {ex.Message}");
            }
        }

        public virtual async Task<bool> OnFlowCompletedAsync(FlowCompletionContext context)
        {
            try
            {
                InfoLog($"高低温循环器流程完成: {context.FlowName}, 成功: {context.IsSuccessful}");

                await StopStatusPollingAsync();
                //await StopAllActiveStatesAsync();

                InfoLog("高低温循环器流程完成处理结束");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("高低温循环器流程完成处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowFailedAsync(FlowFailureContext context)
        {
            try
            {
                ErrorLog($"高低温循环器流程失败: {context.FlowName}, 错误: {context.ErrorMessage}");

                await StopStatusPollingAsync();
                await StopAllActiveStatesAsync();

                InfoLog("高低温循环器流程失败处理完成");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("高低温循环器流程失败处理异常", ex);
                return false;
            }
        }

        #endregion

        #region 状态轮询相关

        private void StartStatusPolling()
        {
            try
            {
                if (_statusPollingTask != null && !_statusPollingTask.IsCompleted)
                {
                    InfoLog("检测到已有轮询任务，先停止");
                    StopStatusPollingAsync().Wait(TimeSpan.FromSeconds(2));
                }

                _statusPollingCts = new CancellationTokenSource();

                _statusPollingTask = Task.Run(async () =>
                {
                    await StatusPollingLoopAsync(_statusPollingCts.Token);
                }, _statusPollingCts.Token);

                InfoLog("状态轮询已启动");
            }
            catch (Exception ex)
            {
                ErrorLog("启动状态轮询失败", ex);
            }
        }

        private async Task StopStatusPollingAsync()
        {
            try
            {
                if (_statusPollingCts != null)
                {
                    InfoLog("正在停止状态轮询...");
                    _statusPollingCts.Cancel();

                    if (_statusPollingTask != null)
                    {
                        await Task.WhenAny(_statusPollingTask, Task.Delay(2000));
                    }

                    _statusPollingCts.Dispose();
                    _statusPollingCts = null;
                    _statusPollingTask = null;

                    InfoLog("状态轮询已停止");
                }
            }
            catch (Exception ex)
            {
                ErrorLog("停止状态轮询失败", ex);
            }
        }

        private async Task StatusPollingLoopAsync(CancellationToken cancellationToken)
        {
            InfoLog("状态轮询线程已启动");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await PollCirculatorStateAsync(cancellationToken);
                        await Task.Delay(StatusPollingIntervalMs, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        InfoLog("状态轮询被取消");
                        break;
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"状态轮询异常: {ex.Message}");
                        try
                        {
                            await Task.Delay(2000, cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLog("状态轮询线程异常退出", ex);
            }
            finally
            {
                InfoLog("状态轮询线程已退出");
            }
        }

        private async Task PollCirculatorStateAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested) return;

                bool cycleRunning = false;
                bool heatingRunning = false;
                bool coolingRunning = false;
                double actualTemp = 25.0;
                double setTemp = _setTemperature; // 默认保持内存值

                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                    ConnectionManager != null && !IsSimulationMode)
                {
                    var deviceId = Parameters.GetValue<string>("DeviceId");

                    // 根据设备ID选择对应的命令前缀
                    string prefix = deviceId == "TC0101" ? "_1" : "_2";

                    cycleRunning = await ConnectionManager.ReadAsync<bool>(ComId, $"YY_Cycle{prefix}");
                    heatingRunning = await ConnectionManager.ReadAsync<bool>(ComId, $"YY_Heat{prefix}");
                    coolingRunning = await ConnectionManager.ReadAsync<bool>(ComId, $"YY_Cool{prefix}");

                    // 读取实际温度
                    actualTemp = await ConnectionManager.ReadAsync<double>(
                        ComId,
                        deviceId == "TC0101" ? "YY_PV_Real_2" : "YY_PV_Real_4");

                    //  新增：同步轮询设定温度
                    string setRegister = deviceId == "TC0101" ? "YY_SV_Set_1" : "YY_SV_Set_2";
                    setTemp = await ConnectionManager.ReadAsync<double>(ComId, setRegister);
                }
                else if (IsSimulationMode)
                {
                    lock (_stateLock)
                    {
                        cycleRunning = _isCycleRunning;
                        heatingRunning = _isHeatingRunning;
                        coolingRunning = _isCoolingRunning;
                        actualTemp = _actualTemperature;
                        setTemp = _setTemperature;
                    }
                }

                bool stateChanged = false;
                lock (_stateLock)
                {
                    if (_isCycleRunning != cycleRunning ||
                        _isHeatingRunning != heatingRunning ||
                        _isCoolingRunning != coolingRunning ||
                        Math.Abs(_actualTemperature - actualTemp) > 0.1 ||
                        Math.Abs(_setTemperature - setTemp) > 0.1)   //  新增设定温度变化检测
                    {
                        _isCycleRunning = cycleRunning;
                        _isHeatingRunning = heatingRunning;
                        _isCoolingRunning = coolingRunning;
                        _actualTemperature = actualTemp;
                        _setTemperature = setTemp;   //  更新内存设定温度
                        stateChanged = true;
                    }
                }

                if (stateChanged)
                {
                    InfoLog($"循环器状态变化 - 循环:{cycleRunning} 加热:{heatingRunning} " +
                            $"制冷:{coolingRunning} 实际温度:{actualTemp:F1}℃ 设定温度:{setTemp:F1}℃");

                    RaiseControlStateChanged("IsCycleRunning", cycleRunning);
                    RaiseControlStateChanged("IsHeatingRunning", heatingRunning);
                    RaiseControlStateChanged("IsCoolingRunning", coolingRunning);
                    RaiseControlStateChanged("ActualTemperature", actualTemp);
                    RaiseControlStateChanged("SetTemperature", setTemp);   //  推送设定温度变化

                    if (cycleRunning)
                        AddActiveState("CirculatorRunning", "启动循环", "停止循环");
                    else
                        RemoveActiveState("CirculatorRunning");

                    if (heatingRunning)
                        AddActiveState("HeatingRunning", "启动加热", "停止加热");
                    else
                        RemoveActiveState("HeatingRunning");

                    if (coolingRunning)
                        AddActiveState("CoolingRunning", "启动制冷", "停止制冷");
                    else
                        RemoveActiveState("CoolingRunning");
                }
            }
            catch (Exception ex)
            {
                ErrorLog($"轮询循环器状态失败: {ex.Message}");
            }
        }

        #endregion

        #region IDeviceUIInteraction Implementation

        public async Task<CommandExecutionResult> ExecuteUICommandAsync(
            string commandName,
            Dictionary<string, object> parameters = null)
        {
            try
            {
                InfoLog($"收到UI命令: {commandName}");

                switch (commandName)
                {
                    case "启动循环":
                        return await StartCycleAsync();

                    case "停止循环":
                        return await StopCycleAsync();

                    case "启动加热":
                        return await StartHeatingAsync();

                    case "停止加热":
                        return await StopHeatingAsync();

                    case "启动制冷":
                        return await StartCoolingAsync();

                    case "停止制冷":
                        return await StopCoolingAsync();

                    case "设置温度":
                        if (parameters == null || !parameters.ContainsKey("TargetTemperature"))
                        {
                            return new CommandExecutionResult
                            {
                                Success = false,
                                Message = "缺少目标温度参数"
                            };
                        }

                        var targetTemp = Convert.ToDouble(parameters["TargetTemperature"]);
                        InfoLog($"从UI接收到目标温度: {targetTemp}");

                        return await SetTemperatureAsync(targetTemp);

                    default:
                        return new CommandExecutionResult
                        {
                            Success = false,
                            Message = $"未识别的命令: {commandName}"
                        };
                }
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

        public override DeviceState GetCurrentState()
        {
            return new DeviceState
            {
                States = new Dictionary<string, object>
                {
                    ["IsCycleRunning"] = _isCycleRunning,
                    ["IsHeatingRunning"] = _isHeatingRunning,
                    ["IsCoolingRunning"] = _isCoolingRunning,
                    ["SetTemperature"] = _setTemperature,
                    ["ActualTemperature"] = _actualTemperature,
                    ["ComId"] = this.ComId
                }
            };
        }

        #endregion

        #region UI命令内部实现

        private async Task<CommandExecutionResult> StartCycleAsync()
        {
            try
            {
                RaiseControlStateChanged("CommandState_启动循环", CommandExecutionState.Running);
                ComId = Parameters.GetValue<string>("DeviceId");
                bool success = false;

                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                    ConnectionManager != null && !IsSimulationMode)
                {
                    var deviceId = Parameters.GetValue<string>("DeviceId");
                    string command = deviceId == "TC0101" ? "YY_Cycle_1" : "YY_Cycle_2";
                    success = await ConnectionManager.WriteAsync(ComId, command, true);
                }
                else if (IsSimulationMode)
                {
                    await Task.Delay(1000);
                    lock (_stateLock) { _isCycleRunning = true; }
                    success = true;
                }

                if (success)
                {
                    lock (_stateLock) { _isCycleRunning = true; }
                    RaiseControlStateChanged("IsCycleRunning", true);
                    RaiseControlStateChanged("CommandState_启动循环", CommandExecutionState.Succeeded);
                    AddActiveState("CirculatorRunning", "启动循环", "停止循环");
                }
                else
                {
                    RaiseControlStateChanged("CommandState_启动循环", CommandExecutionState.Failed);
                }

                return new CommandExecutionResult
                {
                    Success = success,
                    Message = success ? "循环启动成功" : "循环启动失败",
                    OutputData = new Dictionary<string, object>
                    {
                        ["IsCycleRunning"] = success,
                        ["Timestamp"] = DateTime.Now
                    }
                };
            }
            catch (Exception ex)
            {
                RaiseControlStateChanged("CommandState_启动循环", CommandExecutionState.Error,
                    new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });
                ErrorLog($"启动循环失败: {ex.Message}");
                return new CommandExecutionResult { Success = false, Message = ex.Message, Exception = ex };
            }
        }

        private async Task<CommandExecutionResult> StopCycleAsync()
        {
            try
            {
                RaiseControlStateChanged("CommandState_停止循环", CommandExecutionState.Running);
                ComId = Parameters.GetValue<string>("DeviceId");
                bool success = false;

                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                    ConnectionManager != null && !IsSimulationMode)
                {
                    var deviceId = Parameters.GetValue<string>("DeviceId");
                    string command = deviceId == "TC0101" ? "YY_Cycle_1" : "YY_Cycle_2";
                    success = await ConnectionManager.WriteAsync(ComId, command, false);
                }
                else if (IsSimulationMode)
                {
                    await Task.Delay(1000);
                    lock (_stateLock) { _isCycleRunning = false; }
                    success = true;
                }

                if (success) RemoveActiveState("CirculatorRunning");

                RaiseControlStateChanged("IsCycleRunning", false);
                RaiseControlStateChanged("CommandState_停止循环", CommandExecutionState.Succeeded);

                return new CommandExecutionResult
                {
                    Success = success,
                    Message = success ? "循环停止成功" : "循环停止失败",
                    OutputData = new Dictionary<string, object>
                    {
                        ["IsCycleRunning"] = false,
                        ["Timestamp"] = DateTime.Now
                    }
                };
            }
            catch (Exception ex)
            {
                RaiseControlStateChanged("CommandState_停止循环", CommandExecutionState.Error,
                    new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });
                ErrorLog($"停止循环失败: {ex.Message}");
                return new CommandExecutionResult { Success = false, Message = ex.Message, Exception = ex };
            }
        }

        private async Task<CommandExecutionResult> StartHeatingAsync()
        {
            try
            {
                RaiseControlStateChanged("CommandState_启动加热", CommandExecutionState.Running);
                ComId = Parameters.GetValue<string>("DeviceId");
                bool success = false;

                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                    ConnectionManager != null && !IsSimulationMode)
                {
                    var deviceId = Parameters.GetValue<string>("DeviceId");
                    string command = deviceId == "TC0101" ? "YY_Heat_1" : "YY_Heat_2";
                    success = await ConnectionManager.WriteAsync(ComId, command, true);
                }
                else if (IsSimulationMode)
                {
                    await Task.Delay(1000);
                    lock (_stateLock) { _isHeatingRunning = true; }
                    success = true;
                }

                if (success)
                {
                    lock (_stateLock) { _isHeatingRunning = true; }
                    RaiseControlStateChanged("IsHeatingRunning", true);
                    RaiseControlStateChanged("CommandState_启动加热", CommandExecutionState.Succeeded);
                    AddActiveState("HeatingRunning", "启动加热", "停止加热");
                }
                else
                {
                    RaiseControlStateChanged("CommandState_启动加热", CommandExecutionState.Failed);
                }

                return new CommandExecutionResult
                {
                    Success = success,
                    Message = success ? "加热启动成功" : "加热启动失败",
                    OutputData = new Dictionary<string, object>
                    {
                        ["IsHeatingRunning"] = success,
                        ["Timestamp"] = DateTime.Now
                    }
                };
            }
            catch (Exception ex)
            {
                RaiseControlStateChanged("CommandState_启动加热", CommandExecutionState.Error,
                    new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });
                ErrorLog($"启动加热失败: {ex.Message}");
                return new CommandExecutionResult { Success = false, Message = ex.Message, Exception = ex };
            }
        }

        private async Task<CommandExecutionResult> StopHeatingAsync()
        {
            try
            {
                RaiseControlStateChanged("CommandState_停止加热", CommandExecutionState.Running);
                ComId = Parameters.GetValue<string>("DeviceId");
                bool success = false;

                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                    ConnectionManager != null && !IsSimulationMode)
                {
                    var deviceId = Parameters.GetValue<string>("DeviceId");
                    string command = deviceId == "TC0101" ? "YY_Heat_1" : "YY_Heat_2";
                    success = await ConnectionManager.WriteAsync(ComId, command, false);
                }
                else if (IsSimulationMode)
                {
                    await Task.Delay(1000);
                    lock (_stateLock) { _isHeatingRunning = false; }
                    success = true;
                }

                if (success) RemoveActiveState("HeatingRunning");

                RaiseControlStateChanged("IsHeatingRunning", false);
                RaiseControlStateChanged("CommandState_停止加热", CommandExecutionState.Succeeded);

                return new CommandExecutionResult
                {
                    Success = success,
                    Message = success ? "加热停止成功" : "加热停止失败",
                    OutputData = new Dictionary<string, object>
                    {
                        ["IsHeatingRunning"] = false,
                        ["Timestamp"] = DateTime.Now
                    }
                };
            }
            catch (Exception ex)
            {
                RaiseControlStateChanged("CommandState_停止加热", CommandExecutionState.Error,
                    new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });
                ErrorLog($"停止加热失败: {ex.Message}");
                return new CommandExecutionResult { Success = false, Message = ex.Message, Exception = ex };
            }
        }

        private async Task<CommandExecutionResult> StartCoolingAsync()
        {
            try
            {
                RaiseControlStateChanged("CommandState_启动制冷", CommandExecutionState.Running);
                ComId = Parameters.GetValue<string>("DeviceId");
                bool success = false;

                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                    ConnectionManager != null && !IsSimulationMode)
                {
                    var deviceId = Parameters.GetValue<string>("DeviceId");
                    string command = deviceId == "TC0101" ? "YY_Cool_1" : "YY_Cool_2";
                    success = await ConnectionManager.WriteAsync(ComId, command, true);
                }
                else if (IsSimulationMode)
                {
                    await Task.Delay(1000);
                    lock (_stateLock) { _isCoolingRunning = true; }
                    success = true;
                }

                if (success)
                {
                    lock (_stateLock) { _isCoolingRunning = true; }
                    RaiseControlStateChanged("IsCoolingRunning", true);
                    RaiseControlStateChanged("CommandState_启动制冷", CommandExecutionState.Succeeded);
                    AddActiveState("CoolingRunning", "启动制冷", "停止制冷");
                }
                else
                {
                    RaiseControlStateChanged("CommandState_启动制冷", CommandExecutionState.Failed);
                }

                return new CommandExecutionResult
                {
                    Success = success,
                    Message = success ? "制冷启动成功" : "制冷启动失败",
                    OutputData = new Dictionary<string, object>
                    {
                        ["IsCoolingRunning"] = success,
                        ["Timestamp"] = DateTime.Now
                    }
                };
            }
            catch (Exception ex)
            {
                RaiseControlStateChanged("CommandState_启动制冷", CommandExecutionState.Error,
                    new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });
                ErrorLog($"启动制冷失败: {ex.Message}");
                return new CommandExecutionResult { Success = false, Message = ex.Message, Exception = ex };
            }
        }

        private async Task<CommandExecutionResult> StopCoolingAsync()
        {
            try
            {
                RaiseControlStateChanged("CommandState_停止制冷", CommandExecutionState.Running);
                ComId = Parameters.GetValue<string>("DeviceId");
                bool success = false;

                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                    ConnectionManager != null && !IsSimulationMode)
                {
                    var deviceId = Parameters.GetValue<string>("DeviceId");
                    string command = deviceId == "TC0101" ? "YY_Cool_1" : "YY_Cool_2";
                    success = await ConnectionManager.WriteAsync(ComId, command, false);
                }
                else if (IsSimulationMode)
                {
                    await Task.Delay(1000);
                    lock (_stateLock) { _isCoolingRunning = false; }
                    success = true;
                }

                if (success) RemoveActiveState("CoolingRunning");

                RaiseControlStateChanged("IsCoolingRunning", false);
                RaiseControlStateChanged("CommandState_停止制冷", CommandExecutionState.Succeeded);

                return new CommandExecutionResult
                {
                    Success = success,
                    Message = success ? "制冷停止成功" : "制冷停止失败",
                    OutputData = new Dictionary<string, object>
                    {
                        ["IsCoolingRunning"] = false,
                        ["Timestamp"] = DateTime.Now
                    }
                };
            }
            catch (Exception ex)
            {
                RaiseControlStateChanged("CommandState_停止制冷", CommandExecutionState.Error,
                    new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });
                ErrorLog($"停止制冷失败: {ex.Message}");
                return new CommandExecutionResult { Success = false, Message = ex.Message, Exception = ex };
            }
        }

        private async Task<CommandExecutionResult> SetTemperatureAsync(double targetTemperature)
        {
            try
            {
                RaiseControlStateChanged("CommandState_设置温度", CommandExecutionState.Running);
                ComId = Parameters.GetValue<string>("DeviceId");
                bool success = false;
                targetTemperature = Math.Max(-80, Math.Min(200, targetTemperature));

                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                    ConnectionManager != null && !IsSimulationMode)
                {
                    var deviceId = Parameters.GetValue<string>("DeviceId");
                    string command = deviceId == "TC0101" ? "YY_SV_Set_1" : "YY_SV_Set_2";
                    success = await ConnectionManager.WriteAsync(ComId, command, targetTemperature);
                }
                else if (IsSimulationMode)
                {
                    await Task.Delay(1000);
                    lock (_stateLock) { _setTemperature = targetTemperature; }
                    success = true;
                }

                if (success)
                {
                    lock (_stateLock) { _setTemperature = targetTemperature; }
                    RaiseControlStateChanged("SetTemperature", targetTemperature);
                    RaiseControlStateChanged("CommandState_设置温度", CommandExecutionState.Succeeded);
                }
                else
                {
                    RaiseControlStateChanged("CommandState_设置温度", CommandExecutionState.Failed);
                }

                return new CommandExecutionResult
                {
                    Success = success,
                    Message = success ? $"温度设置为 {targetTemperature}℃" : "设置温度失败",
                    OutputData = new Dictionary<string, object>
                    {
                        ["SetTemperature"] = targetTemperature,
                        ["Timestamp"] = DateTime.Now
                    }
                };
            }
            catch (Exception ex)
            {
                RaiseControlStateChanged("CommandState_设置温度", CommandExecutionState.Error,
                    new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });
                ErrorLog($"设置温度失败: {ex.Message}");
                return new CommandExecutionResult { Success = false, Message = ex.Message, Exception = ex };
            }
        }

        #endregion

        private void RaiseControlStateChanged(string stateName, object stateValue,
            Dictionary<string, object> additionalData = null)
        {// ★ 加这一行诊断日志
            InfoLog($"[诊断] this.DeviceId='{this.DeviceId}' | this.ComId='{this.ComId}' | this.HashCode={this.GetHashCode()} | stateName={stateName} | stateValue={stateValue}");
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

        public TemperatureCirculator_ModbusTCP()
        {
            _logger = LogManager.GetLogger<TemperatureCirculator_ModbusTCP>();

            Name = "高低温循环器";
            Manufacturer = "ModbusTCP高低温循环器";
            Category = DeviceCategories.PublicWorks;
            ComId = "TC0101";

            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();

            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            ImageLocation = "CUSTOM_CONTROL:TemperatureControlSystemControl";

            Parameters.Variables.Add(new StringParameter("DeviceId", "TC0101")
            {
                Options = new ObservableCollection<string>(new List<string>() { "TC0101", "TC0201" }),
                HelpText = "ModbusTCP设备ID"
            });

            InitializeCommands();
        }

        private void InitializeCommands()
        {
            // 1. 设置温度
            Commands.Add(new DeviceCommand
            {
                Name = "设置温度",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/temperature_set.png",
                HelpText = "设置循环器目标温度",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TargetTemperature", -80, 200, 25, "目标温度(℃)")
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("Success", false, "设置成功") { IsVisibleInVariableManager = true },
                        new NumberParameter("SetTemperature", -80, 200, 0, "设置的温度", true, UnitTemperature)
                    }
                },
                AsyncAction = WrapAction("设置温度", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double targetTemp = parameters.GetValue<double>("TargetTemperature");
                    bool success = false;

                    RaiseControlStateChanged("CommandState_设置温度", CommandExecutionState.Running);

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                            ConnectionManager != null && !IsSimulationMode)
                        {
                            var deviceId = Parameters.GetValue<string>("DeviceId");
                            string command = deviceId == "TC0101" ? "YY_SV_Set_1" : "YY_SV_Set_2";
                            success = await ConnectionManager.WriteAsync(ComId, command, targetTemp);
                            InfoLog($"设置目标温度: {targetTemp}℃, 结果: {success}");
                        }
                        else if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            lock (_stateLock) { _setTemperature = targetTemp; }
                            success = true;
                            InfoLog($"模拟模式设置温度: {targetTemp}℃");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        RaiseControlStateChanged("SetTemperature", targetTemp);
                        RaiseControlStateChanged("CommandState_设置温度", CommandExecutionState.Succeeded);
                    }
                    catch (Exception ex)
                    {
                        RaiseControlStateChanged("CommandState_设置温度", CommandExecutionState.Error,
                            new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });
                        ErrorLog($"设置温度失败: {ex.Message}");
                        throw;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new BooleanParameter("Success", success, "设置成功") { IsVisibleInVariableManager = true },
                            new NumberParameter("SetTemperature", -80, 200, targetTemp, "设置的温度", true, UnitTemperature)
                        }
                    };
                })
            });

            // 2. 获取实际温度
            Commands.Add(new DeviceCommand
            {
                Name = "获取实际温度",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/temperature.png",
                HelpText = "读取循环器当前实际温度",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("ActualTemperature", -80, 200, 0, "实际温度", true, UnitTemperature)
                    }
                },
                AsyncAction = WrapAction("获取实际温度", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double actualTemp = 0;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                            ConnectionManager != null && !IsSimulationMode)
                        {
                            var deviceId = Parameters.GetValue<string>("DeviceId");
                            string command = deviceId == "TC0101" ? "YY_PV_Real_2" : "YY_PV_Real_4";
                            actualTemp = await ConnectionManager.ReadAsync<double>(ComId, command);
                            InfoLog($"获取实际温度: {actualTemp}℃");
                        }
                        else if (IsSimulationMode)
                        {
                            lock (_stateLock) { actualTemp = _actualTemperature; }
                            InfoLog($"模拟模式获取实际温度: {actualTemp}℃");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"获取实际温度失败: {ex.Message}");
                        throw;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new NumberParameter("ActualTemperature", -80, 200,
                                Math.Round(actualTemp, 1), "实际温度", true, UnitTemperature)
                        }
                    };
                })
            });

            // 3. 启动循环
            Commands.Add(new DeviceCommand
            {
                Name = "启动循环",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/cycle_on.png",
                HelpText = "启动循环器循环功能",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("Success", false, "启动成功") { IsVisibleInVariableManager = true }
                    }
                },
                AsyncAction = WrapAction("启动循环", async delegate (DeviceParameters parameters)
                {
                    AddActiveState("CirculatorRunning", "启动循环", "停止循环");
                    RaiseControlStateChanged("CommandState_启动循环", CommandExecutionState.Running);
                    ComId = Parameters.GetValue<string>("DeviceId");
                    bool success = false;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                            ConnectionManager != null && !IsSimulationMode)
                        {
                            var deviceId = Parameters.GetValue<string>("DeviceId");
                            string command = deviceId == "TC0101" ? "YY_Cycle_1" : "YY_Cycle_2";
                            success = await ConnectionManager.WriteAsync(ComId, command, true);
                            InfoLog($"启动循环, 结果: {success}");
                        }
                        else if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            lock (_stateLock) { _isCycleRunning = true; }
                            success = true;
                            InfoLog("模拟模式启动循环");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        RaiseControlStateChanged("IsCycleRunning", true);
                        RaiseControlStateChanged("CommandState_启动循环", CommandExecutionState.Succeeded);
                    }
                    catch (Exception ex)
                    {
                        RaiseControlStateChanged("CommandState_启动循环", CommandExecutionState.Failed);
                        ErrorLog($"启动循环失败: {ex.Message}");
                        throw;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new BooleanParameter("Success", success, "启动成功") { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });

            // 4. 停止循环
            Commands.Add(new DeviceCommand
            {
                Name = "停止循环",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/cycle_off.png",
                HelpText = "停止循环器循环功能",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("Success", false, "停止成功") { IsVisibleInVariableManager = true }
                    }
                },
                AsyncAction = WrapAction("停止循环", async delegate (DeviceParameters parameters)
                {
                    RaiseControlStateChanged("CommandState_停止循环", CommandExecutionState.Running);
                    ComId = Parameters.GetValue<string>("DeviceId");
                    bool success = false;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                            ConnectionManager != null && !IsSimulationMode)
                        {
                            var deviceId = Parameters.GetValue<string>("DeviceId");
                            string command = deviceId == "TC0101" ? "YY_Cycle_1" : "YY_Cycle_2";
                            success = await ConnectionManager.WriteAsync(ComId, command, false);
                            InfoLog($"停止循环, 结果: {success}");
                        }
                        else if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            lock (_stateLock) { _isCycleRunning = false; }
                            success = true;
                            InfoLog("模拟模式停止循环");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        if (success) RemoveActiveState("CirculatorRunning");

                        RaiseControlStateChanged("IsCycleRunning", false);
                        RaiseControlStateChanged("CommandState_停止循环", CommandExecutionState.Succeeded);
                    }
                    catch (Exception ex)
                    {
                        RaiseControlStateChanged("CommandState_停止循环", CommandExecutionState.Error,
                            new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });
                        ErrorLog($"停止循环失败: {ex.Message}");
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

            // 5. 启动加热
            Commands.Add(new DeviceCommand
            {
                Name = "启动加热",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/heat_on.png",
                HelpText = "启动循环器加热功能",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("Success", false, "启动成功") { IsVisibleInVariableManager = true }
                    }
                },
                AsyncAction = WrapAction("启动加热", async delegate (DeviceParameters parameters)
                {
                    AddActiveState("HeatingRunning", "启动加热", "停止加热");
                    RaiseControlStateChanged("CommandState_启动加热", CommandExecutionState.Running);
                    ComId = Parameters.GetValue<string>("DeviceId");
                    bool success = false;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                            ConnectionManager != null && !IsSimulationMode)
                        {
                            var deviceId = Parameters.GetValue<string>("DeviceId");
                            string command = deviceId == "TC0101" ? "YY_Heat_1" : "YY_Heat_2";
                            success = await ConnectionManager.WriteAsync(ComId, command, true);
                            InfoLog($"启动加热, 结果: {success}");
                        }
                        else if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            lock (_stateLock) { _isHeatingRunning = true; }
                            success = true;
                            InfoLog("模拟模式启动加热");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        RaiseControlStateChanged("IsHeatingRunning", true);
                        RaiseControlStateChanged("CommandState_启动加热", CommandExecutionState.Succeeded);
                    }
                    catch (Exception ex)
                    {
                        RaiseControlStateChanged("CommandState_启动加热", CommandExecutionState.Error,
                            new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });
                        ErrorLog($"启动加热失败: {ex.Message}");
                        throw;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new BooleanParameter("Success", success, "启动成功") { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });

            // 6. 停止加热
            Commands.Add(new DeviceCommand
            {
                Name = "停止加热",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/heat_off.png",
                HelpText = "停止循环器加热功能",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("Success", false, "停止成功") { IsVisibleInVariableManager = true }
                    }
                },
                AsyncAction = WrapAction("停止加热", async delegate (DeviceParameters parameters)
                {
                    RaiseControlStateChanged("CommandState_停止加热", CommandExecutionState.Running);
                    ComId = Parameters.GetValue<string>("DeviceId");
                    bool success = false;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                            ConnectionManager != null && !IsSimulationMode)
                        {
                            var deviceId = Parameters.GetValue<string>("DeviceId");
                            string command = deviceId == "TC0101" ? "YY_Heat_1" : "YY_Heat_2";
                            success = await ConnectionManager.WriteAsync(ComId, command, false);
                            InfoLog($"停止加热, 结果: {success}");
                        }
                        else if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            lock (_stateLock) { _isHeatingRunning = false; }
                            success = true;
                            InfoLog("模拟模式停止加热");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        if (success) RemoveActiveState("HeatingRunning");

                        RaiseControlStateChanged("IsHeatingRunning", false);
                        RaiseControlStateChanged("CommandState_停止加热", CommandExecutionState.Succeeded);
                    }
                    catch (Exception ex)
                    {
                        RaiseControlStateChanged("CommandState_停止加热", CommandExecutionState.Error,
                            new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });
                        ErrorLog($"停止加热失败: {ex.Message}");
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

            // 7. 启动制冷
            Commands.Add(new DeviceCommand
            {
                Name = "启动制冷",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/cool_on.png",
                HelpText = "启动循环器制冷功能",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("Success", false, "启动成功") { IsVisibleInVariableManager = true }
                    }
                },
                AsyncAction = WrapAction("启动制冷", async delegate (DeviceParameters parameters)
                {
                    AddActiveState("CoolingRunning", "启动制冷", "停止制冷");
                    RaiseControlStateChanged("CommandState_启动制冷", CommandExecutionState.Running);
                    ComId = Parameters.GetValue<string>("DeviceId");
                    bool success = false;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                            ConnectionManager != null && !IsSimulationMode)
                        {
                            var deviceId = Parameters.GetValue<string>("DeviceId");
                            string command = deviceId == "TC0101" ? "YY_Cool_1" : "YY_Cool_2";
                            success = await ConnectionManager.WriteAsync(ComId, command, true);
                            InfoLog($"启动制冷, 结果: {success}");
                        }
                        else if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            lock (_stateLock) { _isCoolingRunning = true; }
                            success = true;
                            InfoLog("模拟模式启动制冷");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        RaiseControlStateChanged("IsCoolingRunning", true);
                        RaiseControlStateChanged("CommandState_启动制冷", CommandExecutionState.Succeeded);
                    }
                    catch (Exception ex)
                    {
                        RaiseControlStateChanged("CommandState_启动制冷", CommandExecutionState.Error,
                            new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });
                        ErrorLog($"启动制冷失败: {ex.Message}");
                        throw;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new BooleanParameter("Success", success, "启动成功") { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });

            // 8. 停止制冷
            Commands.Add(new DeviceCommand
            {
                Name = "停止制冷",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/cool_off.png",
                HelpText = "停止循环器制冷功能",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("Success", false, "停止成功") { IsVisibleInVariableManager = true }
                    }
                },
                AsyncAction = WrapAction("停止制冷", async delegate (DeviceParameters parameters)
                {
                    RaiseControlStateChanged("CommandState_停止制冷", CommandExecutionState.Running);
                    ComId = Parameters.GetValue<string>("DeviceId");
                    bool success = false;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                            ConnectionManager != null && !IsSimulationMode)
                        {
                            var deviceId = Parameters.GetValue<string>("DeviceId");
                            string command = deviceId == "TC0101" ? "YY_Cool_1" : "YY_Cool_2";
                            success = await ConnectionManager.WriteAsync(ComId, command, false);
                            InfoLog($"停止制冷, 结果: {success}");
                        }
                        else if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            lock (_stateLock) { _isCoolingRunning = false; }
                            success = true;
                            InfoLog("模拟模式停止制冷");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        if (success) RemoveActiveState("CoolingRunning");

                        RaiseControlStateChanged("IsCoolingRunning", false);
                        RaiseControlStateChanged("CommandState_停止制冷", CommandExecutionState.Succeeded);
                    }
                    catch (Exception ex)
                    {
                        RaiseControlStateChanged("CommandState_停止制冷", CommandExecutionState.Error,
                            new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });
                        ErrorLog($"停止制冷失败: {ex.Message}");
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
        }

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
                InfoLog($"高低温循环器添加活跃状态: {stateName}");
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
                    InfoLog($"高低温循环器移除活跃状态: {stateName}");
                }

                var pausedState = _pausedStates.FirstOrDefault(s => s.StateName == stateName);
                if (pausedState != null)
                {
                    _pausedStates.Remove(pausedState);
                    InfoLog($"高低温循环器移除暂停状态: {stateName}");
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
                InfoLog($"暂停高低温循环器状态: {state.StateName}");

                if (!string.IsNullOrEmpty(state.PauseCommand))
                    await ExecuteDeviceCommandByNameAsync(state.PauseCommand);

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
                ErrorLog($"高低温循环器暂停状态失败: {state.StateName}", ex);
                return false;
            }
        }

        private async Task<bool> ResumeStateAsync(DeviceActiveState state)
        {
            try
            {
                InfoLog($"恢复高低温循环器状态: {state.StateName}");

                if (!string.IsNullOrEmpty(state.ResumeCommand))
                    await ExecuteDeviceCommandByNameAsync(state.ResumeCommand);

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
                ErrorLog($"高低温循环器恢复状态失败: {state.StateName}", ex);
                return false;
            }
        }

        private async Task<bool> StopStateAsync(DeviceActiveState state)
        {
            try
            {
                InfoLog($"停止高低温循环器状态: {state.StateName}");

                if (!string.IsNullOrEmpty(state.DeactivateCommand))
                    await ExecuteDeviceCommandByNameAsync(state.DeactivateCommand);

                return true;
            }
            catch (Exception ex)
            {
                ErrorLog($"高低温循环器停止状态失败: {state.StateName}", ex);
                return false;
            }
        }

        private async Task ExecuteDeviceCommandByNameAsync(string commandName,
            DeviceParameters parameters = null)
        {
            var command = Commands?.FirstOrDefault(c => c.Name == commandName);
            if (command?.Action != null)
            {
                var inputParams = parameters ?? new DeviceParameters();
                await Task.Run(() => command.Action(inputParams));
                InfoLog($"高低温循环器执行命令: {commandName}");
            }
            else
            {
                WarnLog($"高低温循环器未找到命令: {commandName}");
            }
        }

        #endregion

        #region 基类重写方法

        public override void Initialize()
        {
            InfoLog("开始初始化高低温循环器设备");

            try
            {
                base.Initialize();

                lock (_stateLock)
                {
                    _setTemperature = 25.0;
                    _actualTemperature = 25.0;
                    _isCycleRunning = false;
                    _isHeatingRunning = false;
                    _isCoolingRunning = false;
                }

                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                InfoLog("高低温循环器设备初始化完成");
            }
            catch (Exception ex)
            {
                ErrorLog("高低温循环器设备初始化失败", ex);
                throw;
            }
        }

        public override void Shutdown()
        {
            InfoLog("开始关闭高低温循环器设备");

            try
            {
                var stopPollingTask = StopStatusPollingAsync();
                stopPollingTask.Wait(TimeSpan.FromSeconds(3));

                var stopTask = StopAllActiveStatesAsync();
                stopTask.Wait(TimeSpan.FromSeconds(5));

                lock (_stateLock)
                {
                    _isCycleRunning = false;
                    _isHeatingRunning = false;
                    _isCoolingRunning = false;
                    InfoLog("高低温循环器设备运行已停止");
                }

                base.Shutdown();
                InfoLog("高低温循环器设备关闭完成");
            }
            catch (Exception ex)
            {
                ErrorLog("高低温循环器设备关闭失败", ex);
                throw;
            }
        }

        protected override async Task<bool> OnConnectAsync()
        {
            InfoLog("高低温循环器连接");
            await Task.Delay(100);
            return true;
        }

        protected override async Task<bool> OnDisconnectAsync()
        {
            InfoLog("高低温循环器断开连接");
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

        private Func<DeviceParameters, Task<DeviceParameters>> WrapAction(
     string commandName,
     Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return async (DeviceParameters parameters) =>
            {
                // ★ 节流:确保命令之间有最小间隔
                await EnsureCommandIntervalAsync(commandName).ConfigureAwait(false);

                _logger.LogInformation("[命令-开始] Device={Name} Command={CommandName}", Name, commandName);
                var sw = Stopwatch.StartNew();
                try
                {
                    var output = await innerAsync(parameters).ConfigureAwait(false);
                    sw.Stop();
                    _logger.LogInformation("[命令-完成] Device={Name} Command={CommandName} DurationMs={Ms}",
                        Name, commandName, sw.ElapsedMilliseconds);
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, "[命令-异常] Device={Name} Command={CommandName} DurationMs={Ms}",
                        Name, commandName, sw.ElapsedMilliseconds);
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }
    }
}