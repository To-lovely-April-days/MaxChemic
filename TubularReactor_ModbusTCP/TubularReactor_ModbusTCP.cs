using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;

namespace TubularReactor_ModbusTCP
{
    [Export(typeof(IDevice))]
    public class TubularReactor_ModbusTCP : Device, IControllableDevice, IFlowLifecycleAware
    {
        private readonly ILogService _logger;
        private readonly object _stateLock = new object();

        // *** 状态跟踪相关字段 ***
        private readonly List<DeviceActiveState> _activeStates = new List<DeviceActiveState>();
        private readonly List<DeviceActiveState> _pausedStates = new List<DeviceActiveState>();
        private readonly object _statesLock = new object();

        // 管式反应器设备数据缓存
        private readonly ConcurrentDictionary<string, double> _deviceData = new ConcurrentDictionary<string, double>();
        //  单位常量(全项目统一标准)
        // ═══════════════════════════════════════════════════════
        private const string UnitTemperature = "℃";
        private const string UnitPressure = "MPa";
        // ============ IDeviceUIInteraction 所需字段 ============

        /// <summary>
        /// 画布 GUID，由 ProcessDesignerView 通过反射注入
        /// </summary>
        public string DeviceId { get; set; }

        /// <summary>
        /// IDeviceUIInteraction 核心事件
        /// </summary>
        public event EventHandler<DeviceControlStateChangedEventArgs> ControlStateChanged;

        // 轮询相关字段
        private CancellationTokenSource _pollingCts;
        private Task _pollingTask;
        private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(2);

        // ============================================================
        // IDeviceUIInteraction 接口实现
        // ============================================================

        /// <summary>
        /// 触发状态变化事件
        /// </summary>
        private void RaiseControlStateChanged(
            string stateName,
            object stateValue,
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

        /// <summary>
        /// 执行UI命令（管式反应器不支持从UI直接触发命令）
        /// </summary>
        public async Task<CommandExecutionResult> ExecuteUICommandAsync(
            string commandName,
            Dictionary<string, object> parameters = null)
        {
            return new CommandExecutionResult
            {
                Success = false,
                Message = $"命令 {commandName} 不支持通过UI直接调用"
            };
        }

        /// <summary>
        /// 获取当前设备状态
        /// </summary>
        public override DeviceState GetCurrentState()
        {
            var deviceId = ComId;
            var states = new Dictionary<string, object>();

            if (_deviceData.TryGetValue($"{deviceId}_PT0113_Actual", out double pressure))
                states["PT0113_Pressure"] = pressure;

            if (_deviceData.TryGetValue($"{deviceId}_TT0113_Actual", out double temperature))
                states["TT0113_Temperature"] = temperature;

            return new DeviceState { States = states };
        }

        // ============================================================
        // 数据轮询（与微反应器的温度轮询风格完全一致）
        // ============================================================

        /// <summary>
        /// 启动实时数据轮询
        /// </summary>
        private void StartPolling()
        {
            try
            {
                if (_pollingTask != null && !_pollingTask.IsCompleted)
                {
                    InfoLog("检测到已有轮询任务，先停止");
                    StopPollingAsync().Wait(TimeSpan.FromSeconds(2));
                }

                _pollingCts = new CancellationTokenSource();

                _pollingTask = Task.Run(async () =>
                {
                    await PollingLoopAsync(_pollingCts.Token);
                }, _pollingCts.Token);

                InfoLog("管式反应器实时数据轮询已启动");
            }
            catch (Exception ex)
            {
                ErrorLog("启动实时数据轮询失败", ex);
            }
        }

        /// <summary>
        /// 停止实时数据轮询
        /// </summary>
        private async Task StopPollingAsync()
        {
            try
            {
                if (_pollingCts != null)
                {
                    InfoLog("正在停止实时数据轮询...");
                    _pollingCts.Cancel();

                    if (_pollingTask != null)
                    {
                        await Task.WhenAny(_pollingTask, Task.Delay(2000));
                    }

                    _pollingCts.Dispose();
                    _pollingCts = null;
                    _pollingTask = null;

                    InfoLog("管式反应器实时数据轮询已停止");
                }
            }
            catch (Exception ex)
            {
                ErrorLog("停止实时数据轮询失败", ex);
            }
        }

        /// <summary>
        /// 轮询主循环
        /// </summary>
        private async Task PollingLoopAsync(CancellationToken cancellationToken)
        {
            InfoLog("管式反应器轮询线程已启动");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await PollAndPublishDataAsync(cancellationToken);

                        await Task.Delay((int)PollingInterval.TotalMilliseconds, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        InfoLog("管式反应器轮询被取消");
                        break;
                    }
                    catch (Exception ex)
                    {
                        // 单次失败不中断整个循环
                        ErrorLog($"管式反应器轮询异常: {ex.Message}");

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
                ErrorLog("管式反应器轮询线程异常退出", ex);
            }
            finally
            {
                InfoLog("管式反应器轮询线程已退出");
            }
        }

        /// <summary>
        /// 单次轮询：读取出口温度和压力并通过事件推送到UI
        /// </summary>
        private async Task PollAndPublishDataAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return;

            ComId = Parameters.GetValue<string>("DeviceId");
            string deviceId = ComId;

            double pt0113 = 0, tt0113 = 0;

            if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp
                && ConnectionManager != null && !IsSimulationMode)
            {
                var pressureTask = ConnectionManager.ReadAsync<double>(deviceId, "PT0113_Mpa");
                var tempTask = ConnectionManager.ReadAsync<double>(deviceId, "TT0113_℃");

                await Task.WhenAll(pressureTask, tempTask);

                pt0113 = await pressureTask;
                tt0113 = await tempTask;
            }
            else if (IsSimulationMode)
            {
                var random = new Random();
                pt0113 = GetSimulatedPressure(deviceId, random);
                tt0113 = GetSimulatedTemperature(deviceId, random);
            }
            else
            {
                return;
            }

            // 缓存数据
            _deviceData[$"{deviceId}_PT0113_Actual"] = pt0113;
            _deviceData[$"{deviceId}_TT0113_Actual"] = tt0113;

            //// 通过事件推送到UI
            //RaiseControlStateChanged("PT0113_Pressure", pt0113);
            //RaiseControlStateChanged("TT0113_Temperature", tt0113);
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
                    InfoLog("管式反应器设备没有活跃状态需要暂停");
                    return true;
                }

                InfoLog($"暂停管式反应器设备的 {_activeStates.Count} 个活跃状态");

                foreach (var state in _activeStates.ToArray())
                {
                    _activeStates.Remove(state);
                    state.IsPaused = true;
                    _pausedStates.Add(state);
                }
            }

            InfoLog("管式反应器设备所有活跃状态已暂停");
            return true;
        }

        public virtual async Task<bool> ResumeAllPausedStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_pausedStates.Any())
                {
                    InfoLog("管式反应器设备没有暂停的状态需要恢复");
                    return true;
                }

                InfoLog($"恢复管式反应器设备的 {_pausedStates.Count} 个暂停的状态");

                foreach (var state in _pausedStates.ToArray())
                {
                    _pausedStates.Remove(state);
                    state.IsPaused = false;
                    state.ActivatedTime = DateTime.Now;
                    _activeStates.Add(state);
                }
            }

            InfoLog("管式反应器设备所有暂停状态已恢复");
            return true;
        }

        public virtual async Task<bool> StopAllActiveStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_activeStates.Any() && !_pausedStates.Any())
                {
                    InfoLog("管式反应器设备没有活跃或暂停的状态需要停止");
                    StopPollingAsync().Wait(TimeSpan.FromSeconds(2));
                    return true;
                }

                InfoLog($"停止 {_activeStates.Count} 个活跃状态和 {_pausedStates.Count} 个暂停状态");
                _activeStates.Clear();
                _pausedStates.Clear();
            }

            // 停止实时轮询
            await StopPollingAsync();

            InfoLog("管式反应器设备所有状态已停止");
            return true;
        }

        #endregion

        #region IFlowLifecycleAware Implementation

        public virtual async Task<bool> OnFlowStartedAsync(FlowStartContext context)
        {
            try
            {
                InfoLog($"管式反应器设备流程开始: {context.FlowName}");

                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                _deviceData.Clear();

                // 启动实时数据轮询
                StartPolling();

                InfoLog("管式反应器设备已准备好执行流程，实时数据轮询已启动");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("管式反应器设备流程开始处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowCompletedAsync(FlowCompletionContext context)
        {
            try
            {
                InfoLog($"管式反应器设备流程完成: {context.FlowName}, 成功: {context.IsSuccessful}");

                await StopPollingAsync();
                await StopAllActiveStatesAsync();

                InfoLog("管式反应器设备流程完成处理结束");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("管式反应器设备流程完成处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowFailedAsync(FlowFailureContext context)
        {
            try
            {
                ErrorLog($"管式反应器设备流程失败: {context.FlowName}, 错误: {context.ErrorMessage}");

                await StopPollingAsync();
                await StopAllActiveStatesAsync();

                InfoLog("管式反应器设备流程失败处理完成");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("管式反应器设备流程失败处理异常", ex);
                return false;
            }
        }

        #endregion

        public TubularReactor_ModbusTCP()
        {
            _logger = LogManager.GetLogger<TubularReactor_ModbusTCP>();

            Name = "管式反应器";
            Manufacturer = "ModbusTCP管式反应器";
            Category = DeviceCategories.Reactors;
            ComId = "R0102";

            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();

            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            ImageLocation = "CUSTOM_CONTROL:TubularReactorControl";

            Parameters.Variables.Add(new StringParameter("DeviceId", "R0102")
            {
                Options = new ObservableCollection<string>(new List<string>()
                {
                    "R0102"
                }),
                HelpText = "ModbusTCP管式反应器设备ID"
            });

            InitializeCommands();
        }

        private void InitializeCommands()
        {
            // ----------------------------------------------------------------
            // 1. 获取出口压力
            // ----------------------------------------------------------------
            Commands.Add(new DeviceCommand
            {
                Name = "获取出口压力",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/pressure_read.png",
                HelpText = "读取管式反应器出口压力",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter ("DeviceId",        "",    "设备ID")  { IsVisibleInVariableManager = true },
                        new NumberParameter ("PT0113_Pressure", 0, 100, 0, "出口压力", true, UnitPressure),
                        new BooleanParameter("Success",         false, "读取成功") { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("获取出口压力", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    string deviceId = ComId;

                    double pt0113 = 0;
                    bool success = false;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp
                            && ConnectionManager != null && !IsSimulationMode)
                        {
                            pt0113 = await ConnectionManager.ReadAsync<double>(deviceId, "PT0113_Mpa");
                            success = true;

                            InfoLog($"读取 {deviceId} 出口压力: PT0113={pt0113:F3} MPa");
                        }
                        else if (IsSimulationMode)
                        {
                            pt0113 = GetSimulatedPressure(deviceId, new Random());
                            success = true;

                            InfoLog($"模拟 {deviceId} 出口压力: PT0113={pt0113:F3} MPa");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        if (success)
                        {
                            // 更新缓存并推送UI
                            _deviceData[$"{deviceId}_PT0113_Actual"] = pt0113;
                            //RaiseControlStateChanged("PT0113_Pressure", pt0113);
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"读取出口压力失败: {deviceId} - {ex.Message}");
                        success = false;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter ("DeviceId",        deviceId, "设备ID") { IsVisibleInVariableManager = true },
                            new NumberParameter ("PT0113_Pressure", 0, 100, Math.Round(pt0113, 3), "出口压力", true, UnitPressure),
                            new BooleanParameter("Success",         success,  "读取成功") { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });

            // ----------------------------------------------------------------
            // 2. 获取出口温度
            // ----------------------------------------------------------------
            Commands.Add(new DeviceCommand
            {
                Name = "获取出口温度",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/temp_read.png",
                HelpText = "读取管式反应器出口温度（TT0113，寄存器40138）",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter ("DeviceId",           "",     "设备ID")  { IsVisibleInVariableManager = true },
                        new NumberParameter ("TT0113_Temperature", -100, 500, 0, "出口温度", true, UnitTemperature),
                        new BooleanParameter("Success",            false,  "读取成功") { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("获取出口温度", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    string deviceId = ComId;

                    double tt0113 = 0;
                    bool success = false;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp
                            && ConnectionManager != null && !IsSimulationMode)
                        {
                            tt0113 = await ConnectionManager.ReadAsync<double>(deviceId, "TT0113_℃");
                            success = true;

                            InfoLog($"读取 {deviceId} 出口温度: TT0113={tt0113:F2}℃");
                        }
                        else if (IsSimulationMode)
                        {
                            tt0113 = GetSimulatedTemperature(deviceId, new Random());
                            success = true;

                            InfoLog($"模拟 {deviceId} 出口温度: TT0113={tt0113:F2}℃");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        if (success)
                        {
                            // 更新缓存并推送UI
                            _deviceData[$"{deviceId}_TT0113_Actual"] = tt0113;
                            //RaiseControlStateChanged("TT0113_Temperature", tt0113);
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"读取出口温度失败: {deviceId} - {ex.Message}");
                        success = false;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter ("DeviceId",           deviceId, "设备ID") { IsVisibleInVariableManager = true },
                            new NumberParameter ("TT0113_Temperature", -100, 500, Math.Round(tt0113, 2), "出口温度", true, UnitTemperature),
                            new BooleanParameter("Success",            success,  "读取成功") { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });
        }

        #region 辅助方法

        /// <summary>
        /// 模拟出口压力（随机波动，范围 0.1 ~ 5.0 MPa）
        /// </summary>
        private double GetSimulatedPressure(string deviceId, Random random)
        {
            string key = $"{deviceId}_PT0113_Actual";

            double current = _deviceData.TryGetValue(key, out double existing) ? existing : 1.0;

            // 小幅随机游走，模拟传感器噪声
            double newVal = current + (random.NextDouble() - 0.5) * 0.05;

            // 限制在合理范围内
            newVal = Math.Max(0.1, Math.Min(5.0, newVal));

            _deviceData[key] = newVal;
            return newVal;
        }

        /// <summary>
        /// 模拟出口温度（随机波动，范围 20 ~ 300 ℃）
        /// </summary>
        private double GetSimulatedTemperature(string deviceId, Random random)
        {
            string key = $"{deviceId}_TT0113_Actual";

            double current = _deviceData.TryGetValue(key, out double existing) ? existing : 80.0;

            // 小幅随机游走，模拟传感器噪声
            double newVal = current + (random.NextDouble() - 0.5) * 0.5;

            // 限制在合理范围内
            newVal = Math.Max(20.0, Math.Min(300.0, newVal));

            _deviceData[key] = newVal;
            return newVal;
        }

        #endregion

        #region 状态管理内部方法

        protected void AddActiveState(
            string stateName,
            string activateCommand,
            string deactivateCommand,
            string pauseCommand = null,
            string resumeCommand = null,
            object stateData = null)
        {
            lock (_statesLock)
            {
                var existing = _activeStates.Find(s => s.StateName == stateName);
                if (existing != null)
                {
                    existing.ActivatedTime = DateTime.Now;
                    existing.StateData = stateData;
                    return;
                }

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

                InfoLog($"管式反应器设备添加活跃状态: {stateName}");
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
                var state = _activeStates.Find(s => s.StateName == stateName);
                if (state != null)
                {
                    _activeStates.Remove(state);
                    InfoLog($"管式反应器设备移除活跃状态: {stateName}");
                }

                var pausedState = _pausedStates.Find(s => s.StateName == stateName);
                if (pausedState != null)
                {
                    _pausedStates.Remove(pausedState);
                    InfoLog($"管式反应器设备移除暂停状态: {stateName}");
                }
            }

            StateChanged?.Invoke(this, new DeviceStateChangedEventArgs
            {
                StateName = stateName,
                IsActive = false,
                Timestamp = DateTime.Now
            });
        }

        #endregion

        #region 基类重写方法

        public override void Initialize()
        {
            InfoLog("开始初始化ModbusTCP管式反应器设备");

            try
            {
                base.Initialize();

                _deviceData.Clear();

                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                InfoLog("ModbusTCP管式反应器设备初始化完成");
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP管式反应器设备初始化失败", ex);
                throw;
            }
        }

        public override void Shutdown()
        {
            InfoLog("开始关闭ModbusTCP管式反应器设备");

            try
            {
                StopAllActiveStatesAsync().Wait(TimeSpan.FromSeconds(5));

                InfoLog("ModbusTCP管式反应器设备关闭完成");
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP管式反应器设备关闭失败", ex);
                throw;
            }
        }

        protected override async Task<bool> OnConnectAsync()
        {
            InfoLog("ModbusTCP管式反应器设备连接");
            await Task.Delay(100);
            return true;
        }

        protected override async Task<bool> OnDisconnectAsync()
        {
            InfoLog("ModbusTCP管式反应器设备断开连接");
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

        // ----------------------------------------------------------------
        // WrapAction：与微反应器完全一致的命令包装器
        // ----------------------------------------------------------------
        private Func<DeviceParameters, DeviceParameters> WrapAction(
            string commandName,
            Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return (DeviceParameters parameters) =>
            {
                _logger.LogInformation(
                    $"[ModbusTCP管式反应器设备命令-开始] Device={Name} Command={commandName}");

                var sw = Stopwatch.StartNew();
                try
                {
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();

                    _logger.LogInformation(
                        $"[ModbusTCP管式反应器设备命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");

                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex,
                        $"[ModbusTCP管式反应器设备命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");

                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }
    }
}