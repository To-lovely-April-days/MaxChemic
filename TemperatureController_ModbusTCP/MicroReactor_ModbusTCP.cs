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

namespace MicroReactor_ModbusTCP
{
    [Export(typeof(IDevice))]
    public class MicroReactor_ModbusTCP : Device, IControllableDevice, IFlowLifecycleAware
    {
        private readonly ILogService _logger;
        private readonly object _stateLock = new object();

        // *** 状态跟踪相关字段 ***
        private readonly List<DeviceActiveState> _activeStates = new List<DeviceActiveState>();
        private readonly List<DeviceActiveState> _pausedStates = new List<DeviceActiveState>();
        private readonly object _statesLock = new object();

        // 微反应器设备数据缓存
        private readonly Dictionary<string, double> _deviceData = new Dictionary<string, double>();

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
                    InfoLog("ModbusTCP微反应器设备没有活跃状态需要暂停");
                    return true;
                }
                InfoLog($"暂停ModbusTCP微反应器设备的 {_activeStates.Count} 个活跃状态");

                foreach (var state in _activeStates.ToArray())
                {
                    _activeStates.Remove(state);
                    state.IsPaused = true;
                    _pausedStates.Add(state);
                }
            }

            InfoLog("ModbusTCP微反应器设备所有活跃状态已暂停");
            return true;
        }

        public virtual async Task<bool> ResumeAllPausedStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_pausedStates.Any())
                {
                    InfoLog("ModbusTCP微反应器设备没有暂停的状态需要恢复");
                    return true;
                }
                InfoLog($"恢复ModbusTCP微反应器设备的 {_pausedStates.Count} 个暂停的状态");

                foreach (var state in _pausedStates.ToArray())
                {
                    _pausedStates.Remove(state);
                    state.IsPaused = false;
                    state.ActivatedTime = DateTime.Now;
                    _activeStates.Add(state);
                }
            }

            InfoLog("ModbusTCP微反应器设备所有暂停状态已恢复");
            return true;
        }

        public virtual async Task<bool> StopAllActiveStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_activeStates.Any() && !_pausedStates.Any())
                {
                    InfoLog("ModbusTCP微反应器设备没有活跃或暂停的状态需要停止");
                    return true;
                }
                InfoLog($"停止ModbusTCP微反应器设备的 {_activeStates.Count} 个活跃状态和 {_pausedStates.Count} 个暂停状态");

                _activeStates.Clear();
                _pausedStates.Clear();
            }

            InfoLog("ModbusTCP微反应器设备所有状态已停止");
            return true;
        }

        #endregion

        #region IFlowLifecycleAware Implementation

        public virtual async Task<bool> OnFlowStartedAsync(FlowStartContext context)
        {
            try
            {
                InfoLog($"ModbusTCP微反应器设备流程开始: {context.FlowName}");

                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                lock (_stateLock)
                {
                    _deviceData.Clear();
                }

                InfoLog("ModbusTCP微反应器设备已准备好执行流程");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP微反应器设备流程开始处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowCompletedAsync(FlowCompletionContext context)
        {
            try
            {
                InfoLog($"ModbusTCP微反应器设备流程完成: {context.FlowName}, 成功: {context.IsSuccessful}");
                await StopAllActiveStatesAsync();
                InfoLog("ModbusTCP微反应器设备流程完成处理结束");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP微反应器设备流程完成处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowFailedAsync(FlowFailureContext context)
        {
            try
            {
                ErrorLog($"ModbusTCP微反应器设备流程失败: {context.FlowName}, 错误: {context.ErrorMessage}");
                await StopAllActiveStatesAsync();
                InfoLog("ModbusTCP微反应器设备流程失败处理完成");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP微反应器设备流程失败处理异常", ex);
                return false;
            }
        }

        #endregion

        public MicroReactor_ModbusTCP()
        {
            _logger = LogManager.GetLogger<MicroReactor_ModbusTCP>();

            Name = "微反应器";
            Manufacturer = "ModbusTCP微反应器";
            Category = DeviceCategories.Reactors;
            ComId = "R0101"; // 默认设备ID

            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();

            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/MicroReactor.png";

            Parameters.Variables.Add(new StringParameter("DeviceId", "R0101")
            {
                Options = new ObservableCollection<string>(new List<string>()
                {
                    "R0101","R0201"
                }),
                HelpText = "ModbusTCP微反应器设备ID"
            });

            InitializeCommands();
        }

        private void InitializeCommands()
        {
            // 1. 设置温度命令 - 支持三个温度参数
            Commands.Add(new DeviceCommand
            {
                Name = "设置温度",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/temp_set.png",
                HelpText = "设置微反应器的温度值，可以单独或同时设置多个温度点",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TT1001_Temperature", -100, 500, 25, "TT1001温度(℃)")
                        {
                            HelpText = "设置的目标温度值"
                        },
                        new NumberParameter("TT1002_Temperature", -100, 500, 25, "TT1002温度(℃)")
                        {
                            HelpText = "设置的目标温度值"
                        },
                        new NumberParameter("TT1003_Temperature", -100, 500, 25, "TT1003温度(℃)")
                        {
                            HelpText = "设置的目标温度值"
                        }
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("DeviceId", "", "设备ID") { IsVisibleInVariableManager = true },
                        new BooleanParameter("TT1001_Set", false, "TT1001设置成功") { IsVisibleInVariableManager = true },
                        new BooleanParameter("TT1002_Set", false, "TT1002设置成功") { IsVisibleInVariableManager = true },
                        new BooleanParameter("TT1003_Set", false, "TT1003设置成功") { IsVisibleInVariableManager = true },
                        new NumberParameter("TT1001_Value", -100, 500, 0, "TT1001设置值(℃)") { IsVisibleInVariableManager = true },
                        new NumberParameter("TT1002_Value", -100, 500, 0, "TT1002设置值(℃)") { IsVisibleInVariableManager = true },
                        new NumberParameter("TT1003_Value", -100, 500, 0, "TT1003设置值(℃)") { IsVisibleInVariableManager = true },
                        new BooleanParameter("Success", false, "整体操作成功") { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("设置温度", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    string deviceId = ComId;

                    var tt1001Temp = parameters.GetNullableValue<double>("TT1001_Temperature");
                    var tt1002Temp = parameters.GetNullableValue<double>("TT1002_Temperature");
                    var tt1003Temp = parameters.GetNullableValue<double>("TT1003_Temperature");

                    bool tt1001Set = false, tt1002Set = false, tt1003Set = false;
                    double tt1001Value = 0, tt1002Value = 0, tt1003Value = 0;
                    bool overallSuccess = false;
                    var results = new List<bool>();

                    try
                    {
                        // 设置TT1001温度
                        if (tt1001Temp.HasValue) // 假设0表示不设置
                        {
                            tt1001Value = tt1001Temp.Value;
                            if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                            {
                                tt1001Set = await ConnectionManager.WriteAsync(ComId, "TT1001_℃", tt1001Value);
                                InfoLog($"设置 {deviceId} TT1001温度: {tt1001Value}℃, 结果: {tt1001Set}");
                            }
                            else if (IsSimulationMode)
                            {
                                lock (_stateLock)
                                {
                                    _deviceData[$"{deviceId}_TT1001"] = tt1001Value;
                                }
                                tt1001Set = true;
                                InfoLog($"模拟模式设置 {deviceId} TT1001温度: {tt1001Value}℃");
                            }
                            results.Add(tt1001Set);
                        }

                        // 设置TT1002温度
                        if (tt1002Temp.HasValue) // 假设0表示不设置
                        {
                            tt1002Value = tt1002Temp.Value;
                            if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                            {
                                tt1002Set = await ConnectionManager.WriteAsync(ComId, "TT1002_℃", tt1002Value);
                                InfoLog($"设置 {deviceId} TT1002温度: {tt1002Value}℃, 结果: {tt1002Set}");
                            }
                            else if (IsSimulationMode)
                            {
                                lock (_stateLock)
                                {
                                    _deviceData[$"{deviceId}_TT1002"] = tt1002Value;
                                }
                                tt1002Set = true;
                                InfoLog($"模拟模式设置 {deviceId} TT1002温度: {tt1002Value}℃");
                            }
                            results.Add(tt1002Set);
                        }

                        // 设置TT1003温度
                        if (tt1003Temp.HasValue) // 假设0表示不设置
                        {
                            tt1003Value = tt1003Temp.Value;
                            if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                            {
                                tt1003Set = await ConnectionManager.WriteAsync(ComId, "TT1003_℃", tt1003Value);
                                InfoLog($"设置 {deviceId} TT1003温度: {tt1003Value}℃, 结果: {tt1003Set}");
                            }
                            else if (IsSimulationMode)
                            {
                                lock (_stateLock)
                                {
                                    _deviceData[$"{deviceId}_TT1003"] = tt1003Value;
                                }
                                tt1003Set = true;
                                InfoLog($"模拟模式设置 {deviceId} TT1003温度: {tt1003Value}℃");
                            }
                            results.Add(tt1003Set);
                        }

                        // 判断整体成功：有设置操作且所有设置都成功
                        overallSuccess = results.Count > 0 && results.All(r => r);

                        if (tt1001Temp == 0 && tt1002Temp == 0 && tt1003Temp == 0)
                        {
                            WarnLog($"设置温度命令: {deviceId} - 没有提供任何温度值");
                            overallSuccess = false;
                        }
                        else if (DeviceConnectionConfig.CommunicationMode != CommunicationMode.ModbusTcp && !IsSimulationMode)
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        // *** 状态跟踪 - 为成功设置的温度点添加活跃状态 ***
                        if (tt1001Set)
                        {
                            AddActiveState(
                                stateName: $"TT1001_Set_{deviceId}",
                                activateCommand: "设置温度",
                                deactivateCommand: "停止温度控制",
                                stateData: new { DeviceId = deviceId, Temperature = tt1001Value, Sensor = "TT1001" }
                            );
                        }
                        if (tt1002Set)
                        {
                            AddActiveState(
                                stateName: $"TT1002_Set_{deviceId}",
                                activateCommand: "设置温度",
                                deactivateCommand: "停止温度控制",
                                stateData: new { DeviceId = deviceId, Temperature = tt1002Value, Sensor = "TT1002" }
                            );
                        }
                        if (tt1003Set)
                        {
                            AddActiveState(
                                stateName: $"TT1003_Set_{deviceId}",
                                activateCommand: "设置温度",
                                deactivateCommand: "停止温度控制",
                                stateData: new { DeviceId = deviceId, Temperature = tt1003Value, Sensor = "TT1003" }
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"设置温度失败: {deviceId} - {ex.Message}");
                        overallSuccess = false;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("DeviceId", deviceId, "设备ID") { IsVisibleInVariableManager = true },
                            new BooleanParameter("TT1001_Set", tt1001Set, "TT1001设置成功") { IsVisibleInVariableManager = true },
                            new BooleanParameter("TT1002_Set", tt1002Set, "TT1002设置成功") { IsVisibleInVariableManager = true },
                            new BooleanParameter("TT1003_Set", tt1003Set, "TT1003设置成功") { IsVisibleInVariableManager = true },
                            new NumberParameter("TT1001_Value", -100, 500, Math.Round(tt1001Value, 2), "TT1001设置值(℃)") { IsVisibleInVariableManager = true },
                            new NumberParameter("TT1002_Value", -100, 500, Math.Round(tt1002Value, 2), "TT1002设置值(℃)") { IsVisibleInVariableManager = true },
                            new NumberParameter("TT1003_Value", -100, 500, Math.Round(tt1003Value, 2), "TT1003设置值(℃)") { IsVisibleInVariableManager = true },
                            new BooleanParameter("Success", overallSuccess, "整体操作成功") { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });

            // 2. 获取微反应器数据命令 - 只包含PT0112和TT0112
            Commands.Add(new DeviceCommand
            {
                Name = "反应器数据",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/cumulative.png",
                HelpText = "读取微反应器的压力和温度数据",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("DeviceId", "", "设备ID") { IsVisibleInVariableManager = true },
                        new NumberParameter("PT0112_Pressure", 0, 100, 0, "出口压力(MPa)") { IsVisibleInVariableManager = true },
                        new NumberParameter("TT0112_Temperature", -100, 500, 0, "出口温度(℃)") { IsVisibleInVariableManager = true },
                        new BooleanParameter("Success", false, "读取成功") { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("反应器数据", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    string deviceId = ComId;
                    double pt0112 = 0, tt0112 = 0;
                    bool success = false;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            // 并行读取微反应器数据
                            var pt0112Task = ConnectionManager.ReadAsync<double>(ComId, "PT0112_Mpa");
                            var tt0112Task = ConnectionManager.ReadAsync<double>(ComId, "TT0112_℃");

                            await Task.WhenAll(pt0112Task, tt0112Task);

                            pt0112 = await pt0112Task;
                            tt0112 = await tt0112Task;

                            InfoLog($"读取 {deviceId} 微反应器数据: 出口压力PT0112={pt0112}MPa, 出口温度TT0112={tt0112}℃");
                            success = true;
                        }
                        else if (IsSimulationMode)
                        {
                            // 模拟微反应器数据
                            pt0112 = 0.1 + new Random().NextDouble() * 5; // 0.1-5.1 MPa
                            tt0112 = 20 + new Random().NextDouble() * 200;  // 20-220℃

                            lock (_stateLock)
                            {
                                _deviceData[$"{deviceId}_PT0112"] = pt0112;
                                _deviceData[$"{deviceId}_TT0112"] = tt0112;
                            }

                            success = true;
                            InfoLog($"模拟模式读取 {deviceId} 微反应器数据: 出口压力PT0112={pt0112:F3}MPa, 出口温度TT0112={tt0112:F2}℃");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        // *** 状态跟踪 - 记录读取操作 ***
                        if (success)
                        {
                            AddActiveState(
                                stateName: $"ReadingReactorData_{deviceId}",
                                activateCommand: "获取微反应器数据",
                                deactivateCommand: "停止读取",
                                stateData: new
                                {
                                    DeviceId = deviceId,
                                    PT0112 = pt0112,
                                    TT0112 = tt0112,
                                    ReadTime = DateTime.Now
                                }
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"读取微反应器数据失败: {deviceId} - {ex.Message}");
                        success = false;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("DeviceId", deviceId, "设备ID") { IsVisibleInVariableManager = true },
                            new NumberParameter("PT0112_Pressure", 0, 100, Math.Round(pt0112, 3), "出口压力(MPa)") { IsVisibleInVariableManager = true },
                            new NumberParameter("TT0112_Temperature", -100, 500, Math.Round(tt0112, 2), "出口温度(℃)") { IsVisibleInVariableManager = true },
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
                    PauseCommand = pauseCommand ?? "停止温度控制",
                    ResumeCommand = resumeCommand ?? activateCommand,
                    ActivatedTime = DateTime.Now,
                    IsPaused = false,
                    StateData = stateData
                };

                _activeStates.Add(newState);
                InfoLog($"ModbusTCP微反应器设备添加活跃状态: {stateName}");
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
                    InfoLog($"ModbusTCP微反应器设备移除活跃状态: {stateName}");
                }

                var pausedState = _pausedStates.FirstOrDefault(s => s.StateName == stateName);
                if (pausedState != null)
                {
                    _pausedStates.Remove(pausedState);
                    InfoLog($"ModbusTCP微反应器设备移除暂停状态: {stateName}");
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
            InfoLog("开始初始化ModbusTCP微反应器设备");

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

                InfoLog("ModbusTCP微反应器设备初始化完成");
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP微反应器设备初始化失败", ex);
                throw;
            }
        }

        public override void Shutdown()
        {
            InfoLog("开始关闭ModbusTCP微反应器设备");

            try
            {
                var stopTask = StopAllActiveStatesAsync();
                stopTask.Wait(TimeSpan.FromSeconds(5));

                InfoLog("ModbusTCP微反应器设备关闭完成");
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP微反应器设备关闭失败", ex);
                throw;
            }
        }

        protected override async Task<bool> OnConnectAsync()
        {
            InfoLog("ModbusTCP微反应器设备连接");
            await Task.Delay(100);
            return true;
        }

        protected override async Task<bool> OnDisconnectAsync()
        {
            InfoLog("ModbusTCP微反应器设备断开连接");
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
                _logger.LogInformation($"[ModbusTCP微反应器设备命令-开始] Device={Name} Command={commandName}");

                var sw = Stopwatch.StartNew();
                try
                {
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();

                    _logger.LogInformation($"[ModbusTCP微反应器设备命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[ModbusTCP微反应器设备命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }
    }
}