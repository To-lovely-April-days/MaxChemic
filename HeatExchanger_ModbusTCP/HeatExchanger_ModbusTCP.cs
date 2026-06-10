using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;

namespace HeatExchanger_ModbusTCP
{
    [Export(typeof(IDevice))]
    public class HeatExchanger_ModbusTCP : Device, IControllableDevice, IFlowLifecycleAware
    {
        private readonly ILogService _logger;
        private readonly object _stateLock = new object();

        // *** 状态跟踪相关字段 ***
        private readonly List<DeviceActiveState> _activeStates = new List<DeviceActiveState>();
        private readonly List<DeviceActiveState> _pausedStates = new List<DeviceActiveState>();
        private readonly object _statesLock = new object();

        // 换热器设备数据缓存
        private readonly Dictionary<string, double> _deviceData = new Dictionary<string, double>();
        // ═══════════════════════════════════════════════════════
        //  单位常量集中定义,与监控图表的轴分类对齐
        //   - MPa → "压力轴" (P)
        //   - ℃   → "温度轴" (T)
        // ═══════════════════════════════════════════════════════
        private const string UnitPressure = "MPa";
        private const string UnitTemperature = "℃";


        private Task _dataPollingTask;
        private CancellationTokenSource _dataPollingCts;

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
                    InfoLog("ModbusTCP换热器设备没有活跃状态需要暂停");
                    return true;
                }
                InfoLog($"暂停ModbusTCP换热器设备的 {_activeStates.Count} 个活跃状态");

                foreach (var state in _activeStates.ToArray())
                {
                    _activeStates.Remove(state);
                    state.IsPaused = true;
                    _pausedStates.Add(state);
                }
            }

            InfoLog("ModbusTCP换热器设备所有活跃状态已暂停");
            return true;
        }

        public virtual async Task<bool> ResumeAllPausedStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_pausedStates.Any())
                {
                    InfoLog("ModbusTCP换热器设备没有暂停的状态需要恢复");
                    return true;
                }
                InfoLog($"恢复ModbusTCP换热器设备的 {_pausedStates.Count} 个暂停的状态");

                foreach (var state in _pausedStates.ToArray())
                {
                    _pausedStates.Remove(state);
                    state.IsPaused = false;
                    state.ActivatedTime = DateTime.Now;
                    _activeStates.Add(state);
                }
            }

            InfoLog("ModbusTCP换热器设备所有暂停状态已恢复");
            return true;
        }

        public virtual async Task<bool> StopAllActiveStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_activeStates.Any() && !_pausedStates.Any())
                {
                    InfoLog("ModbusTCP换热器设备没有活跃或暂停的状态需要停止");
                    return true;
                }
                InfoLog($"停止ModbusTCP换热器设备的 {_activeStates.Count} 个活跃状态和 {_pausedStates.Count} 个暂停状态");

                _activeStates.Clear();
                _pausedStates.Clear();
            }

            InfoLog("ModbusTCP换热器设备所有状态已停止");
            return true;
        }

        #endregion

        #region IFlowLifecycleAware Implementation

        public virtual async Task<bool> OnFlowStartedAsync(FlowStartContext context)
        {
            try
            {
                InfoLog($"ModbusTCP换热器设备流程开始: {context.FlowName}");

                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                lock (_stateLock)
                {
                    _deviceData.Clear();
                }

                // 启动温度压力数据轮询
                await StartPollingAsync();

                InfoLog("ModbusTCP换热器设备已准备好执行流程");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP换热器设备流程开始处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowCompletedAsync(FlowCompletionContext context)
        {
            try
            {
                InfoLog($"ModbusTCP换热器设备流程完成: {context.FlowName}, 成功: {context.IsSuccessful}");
                await StopPollingAsync();
                await StopAllActiveStatesAsync();
                InfoLog("ModbusTCP换热器设备流程完成处理结束");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP换热器设备流程完成处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowFailedAsync(FlowFailureContext context)
        {
            try
            {
                ErrorLog($"ModbusTCP换热器设备流程失败: {context.FlowName}, 错误: {context.ErrorMessage}");
                await StopPollingAsync();
                await StopAllActiveStatesAsync();
                InfoLog("ModbusTCP换热器设备流程失败处理完成");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP换热器设备流程失败处理异常", ex);
                return false;
            }
        }

        #endregion

        public HeatExchanger_ModbusTCP()
        {
            _logger = LogManager.GetLogger<HeatExchanger_ModbusTCP>();

            Name = "换热器";
            Manufacturer = "ModbusTCP换热器";
            Category = DeviceCategories.HeatExchanger;
            ComId = "H0101"; // 默认设备ID

            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();

            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            //ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/HeatExchanger.png";
            ImageLocation = "CUSTOM_CONTROL:HeatExchangerControl";
            Parameters.Variables.Add(new StringParameter("DeviceId", "H0101")
            {
                Options = new ObservableCollection<string>(new List<string>()
                {
                    "H0101", "H0201"
                }),
                HelpText = "ModbusTCP换热器设备ID"
            });

            InitializeCommands();
        }

        private void InitializeCommands()
        {
            // 获取换热器数据命令 - 只包含PT0113和TT0113
            Commands.Add(new DeviceCommand
            {
                Name = "换热器数据",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/heat_exchanger_data.png",
                HelpText = "读取换热器的出口压力和温度数据",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
        {
            new StringParameter("DeviceId", "", "设备ID") { IsVisibleInVariableManager = true },
            new NumberParameter("PT0113_Pressure",    0, 100,  0, "出口压力", true, UnitPressure),
            new NumberParameter("TT0113_Temperature", -100, 500, 0, "出口温度", true, UnitTemperature),
            new BooleanParameter("Success", false, "读取成功") { IsVisibleInVariableManager = true }
        }
                },
                Action = WrapAction("换热器数据", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    string deviceId = ComId;
                    double pt0113 = 0, tt0113 = 0;
                    bool success = false;

                    try
                    {
                        if (IsSimulationMode)
                        {
                            // 模拟换热器数据
                            pt0113 = 0.1 + new Random().NextDouble() * 5; // 0.1-5.1 MPa
                            tt0113 = 20 + new Random().NextDouble() * 200;  // 20-220℃

                            lock (_stateLock)
                            {
                                _deviceData[$"{deviceId}_PT0113"] = pt0113;
                                _deviceData[$"{deviceId}_TT0113"] = tt0113;
                            }

                            success = true;
                            InfoLog($"模拟模式读取 {deviceId} 换热器数据: 出口压力PT0113={pt0113:F3}MPa, 出口温度TT0113={tt0113:F2}℃");
                        }
                        else if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            // 并行读取换热器数据
                            var pt0113Task = ConnectionManager.ReadAsync<double>(ComId, "PT0113_Mpa");
                            var tt0113Task = ConnectionManager.ReadAsync<double>(ComId, "TT0113_℃");

                            await Task.WhenAll(pt0113Task, tt0113Task);

                            pt0113 = await pt0113Task;
                            tt0113 = await tt0113Task;

                            InfoLog($"读取 {deviceId} 换热器数据: 出口压力PT0113={pt0113}MPa, 出口温度TT0113={tt0113}℃");
                            success = true;
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        // *** 状态跟踪 - 记录读取操作 ***
                        if (success)
                        {
                            AddActiveState(
                                stateName: $"ReadingHeatExchangerData_{deviceId}",
                                activateCommand: "获取换热器数据",
                                deactivateCommand: "停止读取",
                                stateData: new
                                {
                                    DeviceId = deviceId,
                                    PT0113 = pt0113,
                                    TT0113 = tt0113,
                                    ReadTime = DateTime.Now
                                }
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"读取换热器数据失败: {deviceId} - {ex.Message}");
                        success = false;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
        {
            new StringParameter("DeviceId", deviceId, "设备ID") { IsVisibleInVariableManager = true },
            new NumberParameter("PT0113_Pressure",    0, 100,  Math.Round(pt0113, 3), "出口压力", true, UnitPressure),
            new NumberParameter("TT0113_Temperature", -100, 500, Math.Round(tt0113, 2), "出口温度", true, UnitTemperature),
            new BooleanParameter("Success", success, "读取成功") { IsVisibleInVariableManager = true }
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
                    PauseCommand = pauseCommand ?? "停止读取",
                    ResumeCommand = resumeCommand ?? activateCommand,
                    ActivatedTime = DateTime.Now,
                    IsPaused = false,
                    StateData = stateData
                };

                _activeStates.Add(newState);
                InfoLog($"ModbusTCP换热器设备添加活跃状态: {stateName}");
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
                    InfoLog($"ModbusTCP换热器设备移除活跃状态: {stateName}");
                }

                var pausedState = _pausedStates.FirstOrDefault(s => s.StateName == stateName);
                if (pausedState != null)
                {
                    _pausedStates.Remove(pausedState);
                    InfoLog($"ModbusTCP换热器设备移除暂停状态: {stateName}");
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
            InfoLog("开始初始化ModbusTCP换热器设备");

            try
            {
                base.Initialize();

                lock (_stateLock)
                {
                    _deviceData.Clear();
                }

                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                InfoLog("ModbusTCP换热器设备初始化完成");
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP换热器设备初始化失败", ex);
                throw;
            }
        }

        public override void Shutdown()
        {
            InfoLog("开始关闭ModbusTCP换热器设备");

            try
            {
                var stopTask = StopAllActiveStatesAsync();
                stopTask.Wait(TimeSpan.FromSeconds(5));

                InfoLog("ModbusTCP换热器设备关闭完成");
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP换热器设备关闭失败", ex);
                throw;
            }
        }

        protected override async Task<bool> OnConnectAsync()
        {
            InfoLog("ModbusTCP换热器设备连接");
            await Task.Delay(100);
            return true;
        }

        protected override async Task<bool> OnDisconnectAsync()
        {
            InfoLog("ModbusTCP换热器设备断开连接");
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
                _logger.LogInformation($"[ModbusTCP换热器设备命令-开始] Device={Name} Command={commandName}");

                var sw = Stopwatch.StartNew();
                try
                {
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();

                    _logger.LogInformation($"[ModbusTCP换热器设备命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[ModbusTCP换热器设备命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        #region 换热器数据轮询

        private async Task StartPollingAsync()
        {
            InfoLog("换热器数据轮询启动");
            try
            {
                if (_dataPollingTask != null && !_dataPollingTask.IsCompleted)
                {
                    InfoLog("换热器数据轮询任务已存在，先停止");
                    await StopPollingAsync();
                }

                _dataPollingCts = new CancellationTokenSource();
                _dataPollingTask = Task.Run(async () => { await DataPollingAsync(_dataPollingCts.Token); }, _dataPollingCts.Token);
                InfoLog("换热器数据轮询已启动");
            }
            catch (Exception e)
            {
                ErrorLog("换热器数据轮询启动失败", e);
            }
        }

        private async Task StopPollingAsync()
        {
            InfoLog("换热器数据轮询停止");
            try
            {
                if (_dataPollingCts != null)
                {
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
                    InfoLog("换热器数据轮询已停止");
                }
            }
            catch (Exception e)
            {
                ErrorLog("换热器数据轮询停止失败", e);
            }
        }

        private async Task DataPollingAsync(CancellationToken token)
        {
            InfoLog("换热器数据轮询任务已启动");
            while (!token.IsCancellationRequested)
            {
                try
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    string deviceId = Parameters.GetValue<string>("DeviceId");
                    double temperature = 0;
                    double pressure = 0;

                    if (IsSimulationMode)
                    {
                        pressure = 0.1 + new Random().NextDouble() * 5; // 0.1-5.1 MPa
                        temperature = 20 + new Random().NextDouble() * 200;  // 20-220℃
                    }
                    else if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                    {
                        var tTask = ConnectionManager.ReadAsync<double>(ComId, "TT0113_℃");
                        var pTask = ConnectionManager.ReadAsync<double>(ComId, "PT0113_Mpa");
                        await Task.WhenAll(tTask, pTask);
                        temperature = await tTask;
                        pressure = await pTask;
                    }
                    else
                    {

                    }

                    bool changed;
                    lock (_stateLock)
                    {
                        _deviceData[$"{deviceId}_TT0113"] = temperature;
                        _deviceData[$"{deviceId}_PT0113"] = pressure;
                        changed = true;
                    }
                    // 更新UI显示
                    if (changed)
                    {
                        OnStateChanged("TemperatureValue", temperature);
                        OnStateChanged("PressureValue", pressure);
                    }
                    // 等待执行
                    await Task.Delay(1000, token);
                }
                catch (OperationCanceledException)
                {
                    InfoLog("换热器数据轮询任务已取消");
                    break;
                }
                catch (Exception e)
                {
                    ErrorLog("换热器数据轮询任务失败", e);
                    try
                    {
                        await Task.Delay(1000, token);
                    }
                    catch (Exception)
                    {
                        break;
                    }
                }
            }
        }

        #endregion
    }
}