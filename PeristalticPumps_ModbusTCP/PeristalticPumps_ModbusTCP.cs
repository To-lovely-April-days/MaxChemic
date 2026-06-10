using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;

namespace PeristalticPumps_ModbusTCP
{
    [Export(typeof(IDevice))]
    public class PeristalticPumps_ModbusTCP : Device, IDeviceUIInteraction, IControllableDevice, IFlowLifecycleAware
    {
        public event EventHandler<DeviceControlStateChangedEventArgs> ControlStateChanged;
        private readonly ILogService _logger;
        private bool _isRunning = false;
        private double _currentSpeed = 0;
        private DateTime _startTime;
        private readonly object _stateLock = new object();

        // *** 状态跟踪相关字段 ***
        private readonly List<DeviceActiveState> _activeStates = new List<DeviceActiveState>();
        private readonly List<DeviceActiveState> _pausedStates = new List<DeviceActiveState>();

        // 泵速轮询任务和任务取消令牌
        private Task _dataPollingTask;
        private CancellationTokenSource _dataPollingCts;

        private readonly object _statesLock = new object();

        private CancellationTokenSource _statusPollingCts;
        private Task _statusPollingTask;
        private const int StatusPollingIntervalMs = 1000; // 每1秒轮询一次
        // ═══════════════════════════════════════════════════════
        //  单位常量(全项目统一标准)
        // ═══════════════════════════════════════════════════════
        private const string UnitTemperature = "℃";
        private const string UnitPressure = "MPa";
        private const string UnitFlow = "mL/min";
        private const string UnitPumpSpeed = "RPM";

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
                    InfoLog("ModbusTCP蠕动泵没有活跃状态需要暂停");
                    return true;
                }
                InfoLog($"暂停ModbusTCP蠕动泵的 {_activeStates.Count} 个活跃状态");
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
                InfoLog(allSuccess ? "ModbusTCP蠕动泵所有活跃状态已暂停" : "ModbusTCP蠕动泵部分活跃状态暂停失败");
                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP蠕动泵暂停活跃状态异常", ex);
                return false;
            }
        }

        public virtual async Task<bool> ResumeAllPausedStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_pausedStates.Any())
                {
                    InfoLog("ModbusTCP蠕动泵没有暂停的状态需要恢复");
                    return true;
                }
                InfoLog($"恢复ModbusTCP蠕动泵的 {_pausedStates.Count} 个暂停的状态");
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
                InfoLog(allSuccess ? "ModbusTCP蠕动泵所有暂停状态已恢复" : "ModbusTCP蠕动泵部分暂停状态恢复失败");
                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP蠕动泵恢复暂停状态异常", ex);
                return false;
            }
        }

        public virtual async Task<bool> StopAllActiveStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_activeStates.Any() && !_pausedStates.Any())
                {
                    InfoLog("ModbusTCP蠕动泵没有活跃或暂停的状态需要停止");
                    return true;
                }
                InfoLog($"停止ModbusTCP蠕动泵的 {_activeStates.Count} 个活跃状态和 {_pausedStates.Count} 个暂停状态");
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

                InfoLog(allSuccess ? "ModbusTCP蠕动泵所有状态已停止" : "ModbusTCP蠕动泵部分状态停止失败");
                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP蠕动泵停止状态异常", ex);
                return false;
            }
        }

        #endregion

        #region IFlowLifecycleAware Implementation

        public virtual async Task<bool> OnFlowStartedAsync(FlowStartContext context)
        {
            try
            {
                InfoLog($"========== 流程启动 ==========");
                InfoLog($"设备ID: {this.DeviceId}");        // ⭐ 新增
                InfoLog($"ComId: {this.ComId}");            // ⭐ 新增
                InfoLog($"设备哈希: {this.GetHashCode()}"); // ⭐ 新增
                InfoLog($"流程名称: {context.FlowName}");
                InfoLog($"================================");

                InfoLog($"ModbusTCP蠕动泵流程开始: {context.FlowName}");

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
                    WarnLog("ModbusTCP蠕动泵设备未连接，但流程已开始");
                }

                // ⭐ 启动状态轮询
                StartStatusPolling();

                //
                await StartDataPollingAsync();

                InfoLog("ModbusTCP蠕动泵设备已准备好执行流程");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP蠕动泵流程开始处理失败", ex);
                return false;
            }
        }


        public virtual async Task<bool> OnFlowCompletedAsync(FlowCompletionContext context)
        {
            try
            {
                InfoLog($"ModbusTCP蠕动泵流程完成: {context.FlowName}, 成功: {context.IsSuccessful}");

                // 停止状态轮询
                await StopStatusPollingAsync();
                await StopDataPollingAsync();
                await StopAllActiveStatesAsync();
                InfoLog("ModbusTCP蠕动泵流程完成处理结束");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP蠕动泵流程完成处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowFailedAsync(FlowFailureContext context)
        {
            try
            {
                ErrorLog($"ModbusTCP蠕动泵流程失败: {context.FlowName}, 错误: {context.ErrorMessage}");

                //  停止状态轮询
                await StopStatusPollingAsync();
                await StopDataPollingAsync();
                await StopAllActiveStatesAsync();
                InfoLog("ModbusTCP蠕动泵流程失败处理完成");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP蠕动泵流程失败处理异常", ex);
                return false;
            }
        }


        #endregion
        // ========== 新增：状态轮询方法 ==========
        #region 状态轮询相关

        /// <summary>
        /// 启动状态轮询
        /// </summary>
        private void StartStatusPolling()
        {
            try
            {
                // 如果已经在轮询，先停止
                if (_statusPollingTask != null && !_statusPollingTask.IsCompleted)
                {
                    InfoLog("检测到已有轮询任务，先停止");
                    StopStatusPollingAsync().Wait(TimeSpan.FromSeconds(2));
                }

                // 创建新的取消令牌
                _statusPollingCts = new CancellationTokenSource();

                // 启动后台轮询任务
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

        /// <summary>
        /// 停止状态轮询
        /// </summary>
        private async Task StopStatusPollingAsync()
        {
            try
            {
                if (_statusPollingCts != null)
                {
                    InfoLog("正在停止状态轮询...");
                    _statusPollingCts.Cancel();

                    // 等待轮询任务结束（最多等待2秒）
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

        /// <summary>
        /// 状态轮询循环（后台线程）
        /// </summary>
        private async Task StatusPollingLoopAsync(CancellationToken cancellationToken)
        {
            InfoLog("状态轮询线程已启动");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // 1️⃣ 读取泵启动状态
                        await PollPumpRunningStateAsync(cancellationToken);

                        // 2   等待下次轮询（带取消检查）
                        await Task.Delay(StatusPollingIntervalMs, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        InfoLog("状态轮询被取消");
                        break;
                    }
                    catch (Exception ex)
                    {
                        // 单次轮询失败不中断整个循环
                        ErrorLog($"状态轮询异常: {ex.Message}");

                        // 失败后稍微等待，避免疯狂重试
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

        /// <summary>
        /// 轮询泵启动状态
        /// </summary>
        private async Task PollPumpRunningStateAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested) return;

                bool isRunning = false;

                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                    ConnectionManager != null && !IsSimulationMode)
                {
                    // 读取设备启动状态
                    isRunning = await ConnectionManager.ReadAsync<bool>(ComId, "P0101启动");
                    //InfoLog($"轮询泵启动状态: {isRunning}");
                }
                else if (IsSimulationMode)
                {
                    lock (_stateLock)
                    {
                        isRunning = _isRunning;
                    }
                }

                // 检查状态是否变化
                bool stateChanged = false;
                lock (_stateLock)
                {
                    if (_isRunning != isRunning)
                    {
                        _isRunning = isRunning;
                        stateChanged = true;
                    }
                }

                // 状态变化时通知UI
                if (stateChanged)
                {
                    InfoLog($"泵运行状态变化: {isRunning}");
                    RaiseControlStateChanged("IsRunning", isRunning);

                    // 同步更新活跃状态列表
                    if (isRunning)
                    {
                        AddActiveState("PumpRunning", "启动泵", "停止泵");
                    }
                    else
                    {
                        RemoveActiveState("PumpRunning");
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLog($"轮询泵启动状态失败: {ex.Message}");
            }
        }

        #endregion

        #region 泵速轮询

        private async Task StartDataPollingAsync()
        {
            try
            {
                InfoLog("启动泵速轮询");
                if (_dataPollingTask != null && !_dataPollingTask.IsCompleted)
                {
                    InfoLog("已存在泵速轮询任务，先停止");
                    await StopDataPollingAsync();
                }

                _dataPollingCts = new CancellationTokenSource();
                _dataPollingTask = Task.Run(async () => { await DataPollingAsync(_dataPollingCts.Token); }, _dataPollingCts.Token);
                InfoLog("泵速轮询已启动");
            }
            catch (Exception e)
            {
                ErrorLog("泵速轮询启动失败", e);
            }
        }

        private async Task StopDataPollingAsync()
        {
            try
            {
                if (_dataPollingCts != null)
                {
                    InfoLog("正在停止泵速轮询");
                    _dataPollingCts.Cancel();
                    if (_dataPollingTask != null)
                    {
                        try
                        {
                            await _dataPollingTask.WaitAsync(TimeSpan.FromSeconds(2));
                        }
                        catch (Exception)
                        {

                        }
                    }
                    _dataPollingCts.Dispose();
                    _dataPollingCts = null;
                    _dataPollingTask = null;
                    InfoLog("泵速轮询已停止");
                }
            }
            catch (Exception e)
            {
                ErrorLog("泵速轮询停止失败", e);
            }
        }

        private async Task DataPollingAsync(CancellationToken token)
        {
            InfoLog("泵速轮询任务已启动");
            while (!token.IsCancellationRequested)
            {
                try
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double speed = 0;

                    if (IsSimulationMode)
                    {
                        speed = 20 + new Random().NextDouble() * 60; // 模拟20-80
                    }
                    else if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                    {
                        speed = await ConnectionManager.ReadAsync<double>(ComId, "P0101泵速");
                    }
                    else
                    {
                        return;
                    }
                    // 数据是否发生变化
                    bool changed = false;
                    lock (_stateLock)
                    {
                        if (_currentSpeed != speed)
                        {
                            _currentSpeed = speed;
                            changed = true;
                        }
                    }

                    // 更新UI显示
                    if (changed)
                    {
                        RaiseControlStateChanged("PumpSpeed", (int)speed);
                    }
                    // 等待间隔
                    await Task.Delay(1000, token);
                }
                catch (OperationCanceledException)
                {
                    InfoLog("泵速轮询任务已取消");
                    break;
                }
                catch (Exception e)
                {
                    ErrorLog("泵速轮询任务失败", e);
                    try
                    {
                        await Task.Delay(1000, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        #endregion

        #region  新增: IDeviceUIInteraction 实现

        /// <summary>
        /// 执行 UI 命令 (UI → 设备驱动)
        /// </summary>
        public async Task<CommandExecutionResult> ExecuteUICommandAsync(
            string commandName,
            Dictionary<string, object> parameters = null)
        {
            try
            {
                InfoLog($"收到UI命令: {commandName}");
              
                switch (commandName)
                {
                    case "启动泵":
                        return await StartPumpAsync();

                    case "停止泵":
                        return await StopPumpAsync();

                    case "设置泵速":
                        if (parameters == null || !parameters.ContainsKey("TargetSpeed"))
                        {
                            return new CommandExecutionResult
                            {
                                Success = false,
                                Message = "缺少目标速度参数"
                            };
                        }

                        // 关键修复：正确转换参数类型
                        var targetSpeed = Convert.ToDouble(parameters["TargetSpeed"]);
                        InfoLog($"📥 从UI接收到目标速度: {targetSpeed}"); //  添加日志

                        return await SetSpeedAsync(targetSpeed);


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

        /// <summary>
        /// 获取当前设备状态 (设备驱动 → UI)
        /// </summary>
        public override DeviceState GetCurrentState()
        {
            return new DeviceState
            {
                States = new Dictionary<string, object>
                {
                    ["IsRunning"] = _isRunning,
                    ["PumpSpeed"] = _currentSpeed,
                    ["ComId"] = this.ComId
                }
            };
        }

        #endregion

        #region  新增: UI 命令内部实现

        /// <summary>
        /// 启动泵 (内部实现)
        /// </summary>
        private async Task<CommandExecutionResult> StartPumpAsync()
        {
            try
            {
                // 1. 通知UI：开始加载
                RaiseControlStateChanged("CommandState_启动泵", CommandExecutionState.Running);

                ComId = Parameters.GetValue<string>("DeviceId");
                bool success = false;

                // 2. 执行实际命令
                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                    ConnectionManager != null && !IsSimulationMode)
                {
                    success = await ConnectionManager.WriteAsync(ComId, "P0101启动", true);
                }
                else if (IsSimulationMode)
                {
                    await Task.Delay(3000);
                    lock (_stateLock)
                    {
                        _isRunning = true;
                    }
                    success = true;
                    await Task.Delay(500); // 模拟执行时间
                }

                // 3. 更新内部状态并通知UI
                if (success)
                {
                    lock (_stateLock)
                    {
                        _isRunning = true;
                    }

                    //  通知UI：泵已启动
                    RaiseControlStateChanged("IsRunning", true);

                    //  通知UI：命令执行成功
                    RaiseControlStateChanged("CommandState_启动泵", CommandExecutionState.Succeeded);

                    AddActiveState("PumpRunning", "启动泵", "停止泵");
                }
                else
                {
                    //  通知UI：命令执行失败
                    RaiseControlStateChanged("CommandState_启动泵", CommandExecutionState.Failed);
                }

                return new CommandExecutionResult
                {
                    Success = success,
                    Message = success ? "启动成功" : "启动失败",
                    OutputData = new Dictionary<string, object>
                    {
                        ["IsRunning"] = success,
                        ["Timestamp"] = DateTime.Now
                    }
                };
            }
            catch (Exception ex)
            {
                //  通知UI：命令执行异常
                RaiseControlStateChanged("CommandState_启动泵", CommandExecutionState.Error,
                    new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });

                ErrorLog($"启动泵失败: {ex.Message}");
                return new CommandExecutionResult
                {
                    Success = false,
                    Message = ex.Message,
                    Exception = ex
                };
            }
        }


        /// <summary>
        /// 停止泵 (内部实现)
        /// </summary>
        private async Task<CommandExecutionResult> StopPumpAsync()
        {
            try
            {
                RaiseControlStateChanged("CommandState_停止泵", CommandExecutionState.Running);
                ComId = Parameters.GetValue<string>("DeviceId");
                bool success = false;

                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                    ConnectionManager != null && !IsSimulationMode)
                {
                    success = await ConnectionManager.WriteAsync(ComId, "P0101停止", false);
                    InfoLog($"停止蠕动泵（ModbusTCP），结果: {success}");
                }
                else if (IsSimulationMode)
                {
                    await Task.Delay(3000);
                    lock (_stateLock)
                    {
                        _isRunning = false;
                        _currentSpeed = 0;
                    }
                    success = true;
                    InfoLog("模拟模式停止蠕动泵");
                }

                if (success)
                {
                    RemoveActiveState("PumpRunning");
                }
                //  通知UI：泵已停止
                RaiseControlStateChanged("IsRunning", false);

                //  通知UI：命令执行成功
                RaiseControlStateChanged("CommandState_停止泵", CommandExecutionState.Succeeded);
                return new CommandExecutionResult
                {
                    Success = success,
                    Message = success ? "停止成功" : "停止失败",
                    OutputData = new Dictionary<string, object>
                    {
                        ["IsRunning"] = false,
                        ["Timestamp"] = DateTime.Now
                    }
                };
            }
            catch (Exception ex)
            {   //  通知UI：命令执行异常
                RaiseControlStateChanged("CommandState_停止泵", CommandExecutionState.Error,
                    new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });
                ErrorLog($"停止泵失败: {ex.Message}");
                return new CommandExecutionResult
                {
                    Success = false,
                    Message = ex.Message,
                    Exception = ex
                };
            }
        }

        /// <summary>
        /// 设置泵速 (内部实现)
        /// </summary>
        private async Task<CommandExecutionResult> SetSpeedAsync(double targetSpeed)
        {
            try
            {
                // 1. 通知UI：开始加载
                RaiseControlStateChanged("CommandState_设置泵速", CommandExecutionState.Running);

                ComId = Parameters.GetValue<string>("DeviceId");
                bool success = false;
                targetSpeed = Math.Max(0, Math.Min(100, targetSpeed));

                // 2. 执行实际命令
                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                    ConnectionManager != null && !IsSimulationMode)
                {
                    await ConnectionManager.WriteAsync(ComId, "P0101泵基准模式", true);
                    success = await ConnectionManager.WriteAsync(ComId, "P0101泵基准泵速", targetSpeed);
                }
                else if (IsSimulationMode)
                {
                    await Task.Delay(3000);
                    lock (_stateLock)
                    {
                        _currentSpeed = targetSpeed;
                        _isRunning = targetSpeed > 0;
                    }
                    success = true;
                    await Task.Delay(300); // 模拟执行时间
                }

                // 3. 更新内部状态并通知UI
                if (success)
                {
                    lock (_stateLock)
                    {
                        _currentSpeed = targetSpeed;
                    }

                    //  通知UI：泵速已更新
                    RaiseControlStateChanged("PumpSpeed", (int)targetSpeed);

                    //  通知UI：命令执行成功
                    RaiseControlStateChanged("CommandState_设置泵速", CommandExecutionState.Succeeded);
                }
                else
                {
                    //  通知UI：命令执行失败
                    RaiseControlStateChanged("CommandState_设置泵速", CommandExecutionState.Failed);
                }

                return new CommandExecutionResult
                {
                    Success = success,
                    Message = success ? $"速度设置为 {targetSpeed}" : "设置速度失败",
                    OutputData = new Dictionary<string, object>
                    {
                        ["PumpSpeed"] = targetSpeed,
                        ["Timestamp"] = DateTime.Now
                    }
                };
            }
            catch (Exception ex)
            {
                RaiseControlStateChanged("CommandState_设置泵速", CommandExecutionState.Error,
                    new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });

                ErrorLog($"设置泵速失败: {ex.Message}");
                return new CommandExecutionResult
                {
                    Success = false,
                    Message = ex.Message,
                    Exception = ex
                };
            }
        }


        #endregion

        //  触发状态变化事件的辅助方法
        private void RaiseControlStateChanged(string stateName, object stateValue, Dictionary<string, object> additionalData = null)
        {
            ControlStateChanged?.Invoke(this, new DeviceControlStateChangedEventArgs
            {
                DeviceId = this.DeviceId,
                ComId=this.ComId,
                StateName = stateName,
                StateValue = stateValue,
                Timestamp = DateTime.Now,
                AdditionalData = additionalData ?? new Dictionary<string, object>()
            });

            InfoLog($"状态变化通知: {stateName} = {stateValue}");
        }

        public PeristalticPumps_ModbusTCP()
        {
            _logger = LogManager.GetLogger<PeristalticPumps_ModbusTCP>();

            Name = "柱塞泵";
            Manufacturer = "ModbusTCP柱塞泵";
            Category = DeviceCategories.Pumps;
            ComId = "P0101"; // 对应ModbusTCP设备映射ID

            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();

            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            //ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/Pistonpumps.png";
            ImageLocation = "CUSTOM_CONTROL:PistonPumpControl";
            Parameters.Variables.Add(new StringParameter("DeviceId", "P0101")
            {
                Options = new ObservableCollection<string>(new List<string>() { "P0101", "P0111", "P0121" }),
                HelpText = "ModbusTCP设备ID"
            });

            InitializeCommands();
        }

        private void InitializeCommands()
        {
            //启动泵 - 写入40018设置为true
            Commands.Add(new DeviceCommand
            {
                Name = "启动泵",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/open.png",
                HelpText = "启动柱塞泵（设置泵速为0）",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("Success", false, "启动成功") { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("启动泵", async delegate (DeviceParameters parameters)
                {
                    AddActiveState(
                    stateName: "PumpRunning",
                    activateCommand: "启动泵", //激活命令 - 如何启动这个状态
                    deactivateCommand: "停止泵",  // 停止命令 - 如何完全停止这个状态
                    pauseCommand: "停止泵",    // 蠕动泵暂停时直接关闭
                    resumeCommand: "启动泵"   // 恢复时重新开启
                );
                    // 1. 通知UI：开始加载
                    RaiseControlStateChanged("CommandState_启动泵", CommandExecutionState.Running);
                    ComId = Parameters.GetValue<string>("DeviceId");
                    bool success = false;
                   
                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                    ConnectionManager != null && !IsSimulationMode)
                        {
                            success = await ConnectionManager.WriteAsync(ComId, "P0101启动",true);
                            InfoLog($"启动蠕动泵（ModbusTCP地址400018设置为1）, 结果: {success}");
                        }
                        else if (IsSimulationMode)
                        {
                            await Task.Delay(3000);
                            lock (_stateLock)
                             {
                                _currentSpeed = 0;
                                _isRunning = false;
                            }
                            success = true;
                            InfoLog("模拟模式启动蠕动泵");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }
                        //  通知UI：泵已启动
                        RaiseControlStateChanged("IsRunning", true);

                        //  通知UI：命令执行成功
                        RaiseControlStateChanged("CommandState_启动泵", CommandExecutionState.Succeeded);
                        // *** 移除活跃状态 ***
                        //if (success)
                        //{
                        //
                        //    RemoveActiveState("PumpRunning");
                        //}
                    }
                    catch (Exception ex)
                    {
                        RaiseControlStateChanged("CommandState_启动泵", CommandExecutionState.Failed);
                        ErrorLog($"启动蠕动泵失败: {ex.Message}");
                        throw;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new BooleanParameter("Success", success, "启动蠕动成功") { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });
            // 1. 获取泵速 - 读取40001
            Commands.Add(new DeviceCommand
            {
                Name = "获取泵速",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/speed.png",
                HelpText = "读取ModbusTCP地址40001获取泵速",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("PumpSpeed", 0, 5000, 0, "泵速", true, UnitPumpSpeed)
                    }
                },
                Action = WrapAction("获取泵速", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double pumpSpeed = 0;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null && !IsSimulationMode)
                        {
                            pumpSpeed = await ConnectionManager.ReadAsync<double>(ComId, "P0101泵速");
                            InfoLog($"从ModbusTCP地址40001获取泵速: {pumpSpeed} RPM");
                        }
                        else if (IsSimulationMode)
                        {
                            lock (_stateLock)
                            {
                                pumpSpeed = _currentSpeed;
                            }
                            InfoLog($"模拟模式获取泵速: {pumpSpeed} RPM");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"获取泵速失败: {ex.Message}");
                        throw;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new NumberParameter("PumpSpeed", 0, 5000, Math.Round(pumpSpeed, 1), "泵速", true, UnitPumpSpeed)
                        }
                    };
                })
            });

            // 2. 设置泵速 - 写入40001
            Commands.Add(new DeviceCommand
            {
                Name = "设置泵速",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/speed_set.png",
                HelpText = "写入ModbusTCP地址40001设置泵速",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TargetSpeed", 0, 200, 50, "目标泵速(RPM)")
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("Success", false, "设置成功") { IsVisibleInVariableManager = true },
                        new NumberParameter("SetSpeed", 0, 5000, 0, "设置的泵速", true, UnitPumpSpeed)
                    }
                },
                Action = WrapAction("设置泵速", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double targetSpeed = parameters.GetValue<double>("TargetSpeed");
                    bool success = false;
                   
                    // 1. 通知UI：开始加载
                    RaiseControlStateChanged("CommandState_设置泵速", CommandExecutionState.Running);
                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null&&!IsSimulationMode)
                        {
                            //1泵基准
                            success = await ConnectionManager.WriteAsync(ComId, "P0101泵基准模式",true);
                            InfoLog($"向ModbusTCP地址40001设置泵基准: {true}, 结果: {success}");
                            success = await ConnectionManager.WriteAsync(ComId, "P0101泵基准泵速", targetSpeed);
                            InfoLog($"向ModbusTCP地址40001设置泵速: {targetSpeed} RPM, 结果: {success}");
                        }
                        else if (IsSimulationMode)
                        {
                            await Task.Delay(3000);
                            lock (_stateLock)
                            {
                                _currentSpeed = targetSpeed;
                                _isRunning = targetSpeed > 0;
                                if (_isRunning && _startTime == default)
                                {
                                    _startTime = DateTime.Now;
                                }
                            }
                            success = true;
                            InfoLog($"模拟模式设置泵速: {targetSpeed} RPM");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        // *** 状态跟踪 ***
                        if (success && targetSpeed > 0)
                        {
                            AddActiveState("PumpRunning", "设置泵速", "停止泵", stateData: targetSpeed);
                        }
                        else if (success && targetSpeed == 0)
                        {
                            RemoveActiveState("PumpRunning");
                        }
                        //  通知UI：泵速已更新
                        RaiseControlStateChanged("PumpSpeed", (int)targetSpeed);

                        //  通知UI：命令执行成功
                        RaiseControlStateChanged("CommandState_设置泵速", CommandExecutionState.Succeeded);
                    }
                    catch (Exception ex)
                    {
                        RaiseControlStateChanged("CommandState_设置泵速", CommandExecutionState.Error,
                    new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });
                        ErrorLog($"设置泵速失败: {ex.Message}");
                        throw;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new BooleanParameter("Success", success, "设置成功") { IsVisibleInVariableManager = true },
                            new NumberParameter("SetSpeed", 0, 5000, targetSpeed, "设置的泵速", true, UnitPumpSpeed)
                        }
                    };
                })
            });

            // 3. 停止泵 - 写入40018设置为false
            Commands.Add(new DeviceCommand
            {
                Name = "停止泵",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/off.png",
                HelpText = "停止蠕动泵（设置泵速为0）",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("Success", false, "停止成功") { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("停止泵", async delegate (DeviceParameters parameters)
                {
                    RaiseControlStateChanged("CommandState_停止泵", CommandExecutionState.Running);
                    ComId = Parameters.GetValue<string>("DeviceId");
                    bool success = false;
                  
                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null&&!IsSimulationMode)
                        {
                            success = await ConnectionManager.WriteAsync(ComId, "P0101停止", false);
                            InfoLog($"停止蠕动泵（ModbusTCP地址400018设置为0）, 结果: {success}");
                        }
                        else if (IsSimulationMode)
                        {
                            await Task.Delay(3000);
                            lock (_stateLock)
                            {
                                _currentSpeed = 0;
                                _isRunning = false;
                            }
                            success = true;
                            InfoLog("模拟模式停止蠕动泵");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        // *** 移除活跃状态 ***
                        if (success)
                        {
                            RemoveActiveState("PumpRunning");
                        }
                        //  通知UI：泵已停止
                        RaiseControlStateChanged("IsRunning", false);

                        //  通知UI：命令执行成功
                        RaiseControlStateChanged("CommandState_停止泵", CommandExecutionState.Succeeded);
                    }
                    catch (Exception ex)
                    { //  通知UI：命令执行异常
                        RaiseControlStateChanged("CommandState_停止泵", CommandExecutionState.Error,
                            new Dictionary<string, object> { ["ErrorMessage"] = ex.Message });
                        ErrorLog($"停止泵失败: {ex.Message}");
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

            // 4. 获取出口压力 - 读取40006
            Commands.Add(new DeviceCommand
            {
                Name = "获取出口压力",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/pressure.png",
                HelpText = "读取ModbusTCP地址40006获取出口压力",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("Pressure", 0, 10, 0, "出口压力", true, UnitPressure)
                    }
                },
                Action = WrapAction("获取出口压力", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double pressure = 0;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            pressure = await ConnectionManager.ReadAsync<double>(ComId, "P0101出口压力");
                            InfoLog($"从ModbusTCP地址40006获取出口压力: {pressure} MPa");
                        }
                        else if (IsSimulationMode)
                        {
                            pressure = 0.1 + new Random().NextDouble() * 0.5; // 模拟0.1-0.6MPa
                            InfoLog($"模拟模式获取出口压力: {pressure} MPa");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"获取出口压力失败: {ex.Message}");
                        throw;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new NumberParameter("Pressure", 0, 10, Math.Round(pressure, 3), "出口压力", true, UnitPressure)
                        }
                    };
                })
            });

            // 5. 获取出口温度 - 读取40008
            Commands.Add(new DeviceCommand
            {
                Name = "获取出口温度",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/temperature.png",
                HelpText = "读取ModbusTCP地址40008获取出口温度",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("Temperature", -50, 200, 0, "出口温度", true, UnitTemperature)
                    }
                },
                Action = WrapAction("获取出口温度", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double temperature = 0;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            temperature = await ConnectionManager.ReadAsync<double>(ComId, "P0101出口温度");
                            InfoLog($"从ModbusTCP地址40008获取出口温度: {temperature} ℃");
                        }
                        else if (IsSimulationMode)
                        {
                            temperature = 20 + new Random().NextDouble() * 60; // 模拟20-80℃
                            InfoLog($"模拟模式获取出口温度: {temperature} ℃");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"获取出口温度失败: {ex.Message}");
                        throw;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new NumberParameter("Temperature", -50, 200, Math.Round(temperature, 1), "出口温度", true, UnitTemperature)
                        }
                    };
                })
            });

            // 6. 获取出口流量 - 读取40010
            Commands.Add(new DeviceCommand
            {
                Name = "获取出口流量",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/flowrate.png",
                HelpText = "读取ModbusTCP地址40010获取出口流量",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("FlowRate", 0, 1000, 0, "出口流量", true, UnitFlow)
                    }
                },
                Action = WrapAction("获取出口流量", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double flowRate = 0;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            flowRate = await ConnectionManager.ReadAsync<double>(ComId, "P0101出口流量");
                            InfoLog($"从ModbusTCP地址40010获取出口流量: {flowRate} mL/min");
                        }
                        else if (IsSimulationMode)
                        {
                            lock (_stateLock)
                            {
                                flowRate = _currentSpeed * 0.085; // 简单的转速-流量转换
                            }
                            InfoLog($"模拟模式获取出口流量: {flowRate} mL/min");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"获取出口流量失败: {ex.Message}");
                        throw;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new NumberParameter("FlowRate", 0, 1000, Math.Round(flowRate, 2), "出口流量", true, UnitFlow)
                        }
                    };
                })
            });

            //// 7. 获取压力报警状态 - 读取40003
            //Commands.Add(new DeviceCommand
            //{
            //    Name = "获取压力报警",
            //    ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/alarm.png",
            //    HelpText = "读取ModbusTCP地址40003获取出口泄压压力上限报警状态",
            //    InputParameters = new DeviceParameters(),
            //    OutputParameters = new DeviceParameters
            //    {
            //        Variables = new ObservableCollection<ParameterBase>
            //        {
            //            new BooleanParameter("AlarmStatus", false, "压力报警状态") { IsVisibleInVariableManager = true }
            //        }
            //    },
            //    Action = WrapAction("获取压力报警", async delegate (DeviceParameters parameters)
            //    {
            //        bool alarmStatus = false;

            //        try
            //        {
            //            if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
            //            {
            //                alarmStatus = await ConnectionManager.ReadAsync<bool>(ComId, "P0101出口泄压压力_上限报警");
            //                InfoLog($"从ModbusTCP地址40003获取压力报警状态: {alarmStatus}");
            //            }
            //            else if (IsSimulationMode)
            //            {
            //                alarmStatus = new Random().NextDouble() < 0.1; // 10%概率报警
            //                InfoLog($"模拟模式获取压力报警状态: {alarmStatus}");
            //            }
            //            else
            //            {
            //                throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
            //            }
            //        }
            //        catch (Exception ex)
            //        {
            //            ErrorLog($"获取压力报警状态失败: {ex.Message}");
            //            throw;
            //        }

            //        return new DeviceParameters
            //        {
            //            Variables = new ObservableCollection<ParameterBase>
            //            {
            //                new BooleanParameter("AlarmStatus", alarmStatus, "压力报警状态") { IsVisibleInVariableManager = true }
            //            }
            //        };
            //    })
            //});

            //// 8. 获取温度报警状态 - 读取40005
            //Commands.Add(new DeviceCommand
            //{
            //    Name = "获取温度报警",
            //    ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/alarm.png",
            //    HelpText = "读取ModbusTCP地址40005获取出口温度上限报警状态",
            //    InputParameters = new DeviceParameters(),
            //    OutputParameters = new DeviceParameters
            //    {
            //        Variables = new ObservableCollection<ParameterBase>
            //        {
            //            new BooleanParameter("AlarmStatus", false, "温度报警状态") { IsVisibleInVariableManager = true }
            //        }
            //    },
            //    Action = WrapAction("获取温度报警", async delegate (DeviceParameters parameters)
            //    {
            //        bool alarmStatus = false;

            //        try
            //        {
            //            if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
            //            {
            //                alarmStatus = await ConnectionManager.ReadAsync<bool>(ComId, "P0101出口温度上限报警");
            //                InfoLog($"从ModbusTCP地址40005获取温度报警状态: {alarmStatus}");
            //            }
            //            else if (IsSimulationMode)
            //            {
            //                alarmStatus = new Random().NextDouble() < 0.05; // 5%概率报警
            //                InfoLog($"模拟模式获取温度报警状态: {alarmStatus}");
            //            }
            //            else
            //            {
            //                throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
            //            }
            //        }
            //        catch (Exception ex)
            //        {
            //            ErrorLog($"获取温度报警状态失败: {ex.Message}");
            //            throw;
            //        }

            //        return new DeviceParameters
            //        {
            //            Variables = new ObservableCollection<ParameterBase>
            //            {
            //                new BooleanParameter("AlarmStatus", alarmStatus, "温度报警状态") { IsVisibleInVariableManager = true }
            //            }
            //        };
            //    })
            //});
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
                InfoLog($"ModbusTCP蠕动泵添加活跃状态: {stateName}");
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
                    InfoLog($"ModbusTCP蠕动泵移除活跃状态: {stateName}");
                }

                var pausedState = _pausedStates.FirstOrDefault(s => s.StateName == stateName);
                if (pausedState != null)
                {
                    _pausedStates.Remove(pausedState);
                    InfoLog($"ModbusTCP蠕动泵移除暂停状态: {stateName}");
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
                InfoLog($"暂停ModbusTCP蠕动泵状态: {state.StateName}");

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
                ErrorLog($"ModbusTCP蠕动泵暂停状态失败: {state.StateName}", ex);
                return false;
            }
        }

        private async Task<bool> ResumeStateAsync(DeviceActiveState state)
        {
            try
            {
                InfoLog($"恢复ModbusTCP蠕动泵状态: {state.StateName}");

                if (!string.IsNullOrEmpty(state.ResumeCommand))
                {
                    var resumeParams = new DeviceParameters();
                    if (state.StateData is double speed)
                    {
                        resumeParams.Variables.Add(new NumberParameter("TargetSpeed", 0, 5000, speed, "目标泵速"));
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
                ErrorLog($"ModbusTCP蠕动泵恢复状态失败: {state.StateName}", ex);
                return false;
            }
        }

        private async Task<bool> StopStateAsync(DeviceActiveState state)
        {
            try
            {
                InfoLog($"停止ModbusTCP蠕动泵状态: {state.StateName}");

                if (!string.IsNullOrEmpty(state.DeactivateCommand))
                {
                    await ExecuteDeviceCommandByNameAsync(state.DeactivateCommand);
                }

                return true;
            }
            catch (Exception ex)
            {
                ErrorLog($"ModbusTCP蠕动泵停止状态失败: {state.StateName}", ex);
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
                InfoLog($"ModbusTCP蠕动泵执行命令: {commandName}");
            }
            else
            {
                WarnLog($"ModbusTCP蠕动泵未找到命令: {commandName}");
            }
        }

        #endregion

        #region 基类重写方法

        public override void Initialize()
        {
            InfoLog("开始初始化ModbusTCP蠕动泵设备");

            try
            {
                base.Initialize();

                lock (_stateLock)
                {
                    _isRunning = false;
                    _currentSpeed = 0;
                }

                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                InfoLog("ModbusTCP蠕动泵设备初始化完成");
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP蠕动泵设备初始化失败", ex);
                throw;
            }
        }

        public override void Shutdown()
        {
            InfoLog("开始关闭ModbusTCP蠕动泵设备");

            try
            {
                // 先停止状态轮询
                var stopPollingTask = StopStatusPollingAsync();
                stopPollingTask.Wait(TimeSpan.FromSeconds(3));

                var stopTask = StopAllActiveStatesAsync();
                stopTask.Wait(TimeSpan.FromSeconds(5));

                lock (_stateLock)
                {
                    if (_isRunning)
                    {
                        _isRunning = false;
                        _currentSpeed = 0;
                        InfoLog("ModbusTCP蠕动泵设备运行已停止");
                    }
                }

                base.Shutdown();
                InfoLog("ModbusTCP蠕动泵设备关闭完成");
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP蠕动泵设备关闭失败", ex);
                throw;
            }
        }

        protected override async Task<bool> OnConnectAsync()
        {
            InfoLog("ModbusTCP蠕动泵连接");
            await Task.Delay(100);
            return true;
        }

        protected override async Task<bool> OnDisconnectAsync()
        {
            InfoLog("ModbusTCP蠕动泵断开连接");
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
                _logger.LogInformation($"[ModbusTCP蠕动泵命令-开始] Device={Name} Command={commandName}");

                var sw = Stopwatch.StartNew();
                try
                {
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();

                    _logger.LogInformation($"[ModbusTCP蠕动泵命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[ModbusTCP蠕动泵命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }
    }
}