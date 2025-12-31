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

namespace TemperatureCirculator_ModbusTCP
{
    [Export(typeof(IDevice))]
    public class TemperatureCirculator_ModbusTCP : Device, IControllableDevice, IFlowLifecycleAware
    {
        private readonly ILogService _logger;
        private readonly object _stateLock = new object();

        // *** 状态跟踪相关字段 ***
        private readonly List<DeviceActiveState> _activeStates = new List<DeviceActiveState>();
        private readonly List<DeviceActiveState> _pausedStates = new List<DeviceActiveState>();
        private readonly object _statesLock = new object();

        // 高低温循环器设备数据缓存
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
                    InfoLog("高低温循环器设备没有活跃状态需要暂停");
                    return true;
                }
                InfoLog($"暂停高低温循环器设备的 {_activeStates.Count} 个活跃状态");

                foreach (var state in _activeStates.ToArray())
                {
                    _activeStates.Remove(state);
                    state.IsPaused = true;
                    _pausedStates.Add(state);
                }
            }

            InfoLog("高低温循环器设备所有活跃状态已暂停");
            return true;
        }

        public virtual async Task<bool> ResumeAllPausedStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_pausedStates.Any())
                {
                    InfoLog("高低温循环器设备没有暂停的状态需要恢复");
                    return true;
                }
                InfoLog($"恢复高低温循环器设备的 {_pausedStates.Count} 个暂停的状态");

                foreach (var state in _pausedStates.ToArray())
                {
                    _pausedStates.Remove(state);
                    state.IsPaused = false;
                    state.ActivatedTime = DateTime.Now;
                    _activeStates.Add(state);
                }
            }

            InfoLog("高低温循环器设备所有暂停状态已恢复");
            return true;
        }

        public virtual async Task<bool> StopAllActiveStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_activeStates.Any() && !_pausedStates.Any())
                {
                    InfoLog("高低温循环器设备没有活跃或暂停的状态需要停止");
                    return true;
                }
                InfoLog($"停止高低温循环器设备的 {_activeStates.Count} 个活跃状态和 {_pausedStates.Count} 个暂停状态");

                _activeStates.Clear();
                _pausedStates.Clear();
            }

            InfoLog("高低温循环器设备所有状态已停止");
            return true;
        }

        #endregion

        #region IFlowLifecycleAware Implementation

        public virtual async Task<bool> OnFlowStartedAsync(FlowStartContext context)
        {
            try
            {
                InfoLog($"高低温循环器设备流程开始: {context.FlowName}");

                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                lock (_stateLock)
                {
                    _deviceData.Clear();
                }

                InfoLog("高低温循环器设备已准备好执行流程");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("高低温循环器设备流程开始处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowCompletedAsync(FlowCompletionContext context)
        {
            try
            {
                InfoLog($"高低温循环器设备流程完成: {context.FlowName}, 成功: {context.IsSuccessful}");
                await StopAllActiveStatesAsync();
                InfoLog("高低温循环器设备流程完成处理结束");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("高低温循环器设备流程完成处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowFailedAsync(FlowFailureContext context)
        {
            try
            {
                ErrorLog($"高低温循环器设备流程失败: {context.FlowName}, 错误: {context.ErrorMessage}");
                await StopAllActiveStatesAsync();
                InfoLog("高低温循环器设备流程失败处理完成");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("高低温循环器设备流程失败处理异常", ex);
                return false;
            }
        }

        #endregion

        public TemperatureCirculator_ModbusTCP()
        {
            _logger = LogManager.GetLogger<TemperatureCirculator_ModbusTCP>();

            Name = "高低温循环器";
            Manufacturer = "ModbusTCP高低温循环器";
            Category = DeviceCategories.PublicWorks;
            ComId = "TC0101"; // 默认设备ID

            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();

            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            //ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/TemperatureCirculator.png";
            ImageLocation = "CUSTOM_CONTROL:TemperatureControlSystemControl";
            Parameters.Variables.Add(new StringParameter("DeviceId", "TC0101")
            {
                Options = new ObservableCollection<string>(new List<string>()
                {
                    "TC0101", "TC0201"
                }),
                HelpText = "高低温循环器设备ID"
            });

            InitializeCommands();
        }

        private void InitializeCommands()
        {
            // 1. 设定温度命令
            Commands.Add(new DeviceCommand
            {
                Name = "设定温度",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/temp_set.png",
                HelpText = "设定高低温循环器的目标温度",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("SetTemperature", -80, 200, 25, "设定温度(℃)")
                        {
                            HelpText = "设定的目标温度值"
                        }
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("DeviceId", "", "设备ID") { IsVisibleInVariableManager = true },
                        new NumberParameter("SetTemperature", -80, 200, 0, "设定温度(℃)") { IsVisibleInVariableManager = true },
                        new BooleanParameter("Success", false, "设定成功") { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("设定温度", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    string deviceId = ComId;
                    double setTemperature = parameters.GetValue<double>("SetTemperature");
                    bool success = false;

                    try
                    {
                        // 根据设备ID确定命令
                        string command = deviceId == "TC0101" ? "YY_SV_Set_1" : "YY_SV_Set_2";

                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            success = await ConnectionManager.WriteAsync(ComId, command, setTemperature);
                            InfoLog($"设定 {deviceId} 温度: {setTemperature}℃, 结果: {success}");
                        }
                        else if (IsSimulationMode)
                        {
                            lock (_stateLock)
                            {
                                _deviceData[$"{deviceId}_SetTemp"] = setTemperature;
                            }
                            success = true;
                            InfoLog($"模拟模式设定 {deviceId} 温度: {setTemperature}℃");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        // *** 状态跟踪 ***
                        if (success)
                        {
                            AddActiveState(
                                stateName: $"TempSet_{deviceId}",
                                activateCommand: "设定温度",
                                deactivateCommand: "停止控制",
                                stateData: new { DeviceId = deviceId, SetTemperature = setTemperature }
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"设定温度失败: {deviceId} - {ex.Message}");
                        success = false;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("DeviceId", deviceId, "设备ID") { IsVisibleInVariableManager = true },
                            new NumberParameter("SetTemperature", -80, 200, Math.Round(setTemperature, 2), "设定温度(℃)") { IsVisibleInVariableManager = true },
                            new BooleanParameter("Success", success, "设定成功") { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });

            // 2. 获取出口温度命令
            Commands.Add(new DeviceCommand
            {
                Name = "获取出口温度",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/temp_read.png",
                HelpText = "读取高低温循环器的出口温度",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("DeviceId", "", "设备ID") { IsVisibleInVariableManager = true },
                        new NumberParameter("OutletTemperature", -80, 200, 0, "出口温度(℃)") { IsVisibleInVariableManager = true },
                        new BooleanParameter("Success", false, "读取成功") { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("获取出口温度", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    string deviceId = ComId;
                    double outletTemperature = 0;
                    bool success = false;

                    try
                    {
                        // 根据设备ID确定命令
                        string command = deviceId == "TC0101" ? "YY_PV_Real_2" : "YY_PV_Real_4";

                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            outletTemperature = await ConnectionManager.ReadAsync<double>(ComId, command);
                            InfoLog($"读取 {deviceId} 出口温度: {outletTemperature}℃");
                            success = true;
                        }
                        else if (IsSimulationMode)
                        {
                            // 模拟温度数据
                            outletTemperature = -10 + new Random().NextDouble() * 210;  // -10 到 200℃
                            lock (_stateLock)
                            {
                                _deviceData[$"{deviceId}_OutletTemp"] = outletTemperature;
                            }
                            success = true;
                            InfoLog($"模拟模式读取 {deviceId} 出口温度: {outletTemperature:F2}℃");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        // *** 状态跟踪 ***
                        if (success)
                        {
                            AddActiveState(
                                stateName: $"TempRead_{deviceId}",
                                activateCommand: "获取出口温度",
                                deactivateCommand: "停止读取",
                                stateData: new
                                {
                                    DeviceId = deviceId,
                                    OutletTemperature = outletTemperature,
                                    ReadTime = DateTime.Now
                                }
                            );
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
                            new StringParameter("DeviceId", deviceId, "设备ID") { IsVisibleInVariableManager = true },
                            new NumberParameter("OutletTemperature", -80, 200, Math.Round(outletTemperature, 2), "出口温度(℃)") { IsVisibleInVariableManager = true },
                            new BooleanParameter("Success", success, "读取成功") { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });

            // 3. 循环启动命令
            Commands.Add(new DeviceCommand
            {
                Name = "循环启动",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/circulation_start.png",
                HelpText = "启动或停止高低温循环器的循环功能",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("StartCycle", true, "启动循环")
                        {
                            HelpText = "true=启动循环, false=停止循环"
                        }
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("DeviceId", "", "设备ID") { IsVisibleInVariableManager = true },
                        new BooleanParameter("CycleStatus", false, "循环状态") { IsVisibleInVariableManager = true },
                        new BooleanParameter("Success", false, "操作成功") { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("循环启动", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    string deviceId = ComId;
                    bool startCycle = parameters.GetValue<bool>("StartCycle");
                    bool success = false;

                    try
                    {
                        // 根据设备ID确定命令
                        string command = deviceId == "TC0101" ? "YY_Cycle_1" : "YY_Cycle_2";
                        int value = startCycle ? 1 : 0;

                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            success = await ConnectionManager.WriteAsync(ComId, command, value);
                            InfoLog($"{deviceId} 循环{(startCycle ? "启动" : "停止")}, 结果: {success}");
                        }
                        else if (IsSimulationMode)
                        {
                            lock (_stateLock)
                            {
                                _deviceData[$"{deviceId}_Cycle"] = value;
                            }
                            success = true;
                            InfoLog($"模拟模式 {deviceId} 循环{(startCycle ? "启动" : "停止")}");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        // *** 状态跟踪 ***
                        if (success)
                        {
                            if (startCycle)
                            {
                                AddActiveState(
                                    stateName: $"Cycle_{deviceId}",
                                    activateCommand: "循环启动",
                                    deactivateCommand: "循环停止",
                                    stateData: new { DeviceId = deviceId, CycleStatus = true }
                                );
                            }
                            else
                            {
                                RemoveActiveState($"Cycle_{deviceId}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"循环控制失败: {deviceId} - {ex.Message}");
                        success = false;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("DeviceId", deviceId, "设备ID") { IsVisibleInVariableManager = true },
                            new BooleanParameter("CycleStatus", startCycle, "循环状态") { IsVisibleInVariableManager = true },
                            new BooleanParameter("Success", success, "操作成功") { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });

            // 4. 加热启动命令
            Commands.Add(new DeviceCommand
            {
                Name = "加热启动",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/heating_start.png",
                HelpText = "启动或停止高低温循环器的加热功能",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("StartHeating", true, "启动加热")
                        {
                            HelpText = "true=启动加热, false=停止加热"
                        }
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("DeviceId", "", "设备ID") { IsVisibleInVariableManager = true },
                        new BooleanParameter("HeatingStatus", false, "加热状态") { IsVisibleInVariableManager = true },
                        new BooleanParameter("Success", false, "操作成功") { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("加热启动", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    string deviceId = ComId;
                    bool startHeating = parameters.GetValue<bool>("StartHeating");
                    bool success = false;

                    try
                    {
                        // 根据设备ID确定命令
                        string command = deviceId == "TC0101" ? "YY_Heat_1" : "YY_Heat_2";
                        int value = startHeating ? 1 : 0;

                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            success = await ConnectionManager.WriteAsync(ComId, command, value);
                            InfoLog($"{deviceId} 加热{(startHeating ? "启动" : "停止")}, 结果: {success}");
                        }
                        else if (IsSimulationMode)
                        {
                            lock (_stateLock)
                            {
                                _deviceData[$"{deviceId}_Heat"] = value;
                            }
                            success = true;
                            InfoLog($"模拟模式 {deviceId} 加热{(startHeating ? "启动" : "停止")}");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        // *** 状态跟踪 ***
                        if (success)
                        {
                            if (startHeating)
                            {
                                AddActiveState(
                                    stateName: $"Heating_{deviceId}",
                                    activateCommand: "加热启动",
                                    deactivateCommand: "加热停止",
                                    stateData: new { DeviceId = deviceId, HeatingStatus = true }
                                );
                            }
                            else
                            {
                                RemoveActiveState($"Heating_{deviceId}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"加热控制失败: {deviceId} - {ex.Message}");
                        success = false;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("DeviceId", deviceId, "设备ID") { IsVisibleInVariableManager = true },
                            new BooleanParameter("HeatingStatus", startHeating, "加热状态") { IsVisibleInVariableManager = true },
                            new BooleanParameter("Success", success, "操作成功") { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });

            // 5. 制冷启动命令
            Commands.Add(new DeviceCommand
            {
                Name = "制冷启动",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/cooling_start.png",
                HelpText = "启动或停止高低温循环器的制冷功能",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("StartCooling", true, "启动制冷")
                        {
                            HelpText = "true=启动制冷, false=停止制冷"
                        }
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("DeviceId", "", "设备ID") { IsVisibleInVariableManager = true },
                        new BooleanParameter("CoolingStatus", false, "制冷状态") { IsVisibleInVariableManager = true },
                        new BooleanParameter("Success", false, "操作成功") { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("制冷启动", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    string deviceId = ComId;
                    bool startCooling = parameters.GetValue<bool>("StartCooling");
                    bool success = false;

                    try
                    {
                        // 根据设备ID确定命令
                        string command = deviceId == "TC0101" ? "YY_Cool_1" : "YY_Cool_2";
                        int value = startCooling ? 1 : 0;

                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            success = await ConnectionManager.WriteAsync(ComId, command, value);
                            InfoLog($"{deviceId} 制冷{(startCooling ? "启动" : "停止")}, 结果: {success}");
                        }
                        else if (IsSimulationMode)
                        {
                            lock (_stateLock)
                            {
                                _deviceData[$"{deviceId}_Cool"] = value;
                            }
                            success = true;
                            InfoLog($"模拟模式 {deviceId} 制冷{(startCooling ? "启动" : "停止")}");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        // *** 状态跟踪 ***
                        if (success)
                        {
                            if (startCooling)
                            {
                                AddActiveState(
                                    stateName: $"Cooling_{deviceId}",
                                    activateCommand: "制冷启动",
                                    deactivateCommand: "制冷停止",
                                    stateData: new { DeviceId = deviceId, CoolingStatus = true }
                                );
                            }
                            else
                            {
                                RemoveActiveState($"Cooling_{deviceId}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"制冷控制失败: {deviceId} - {ex.Message}");
                        success = false;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("DeviceId", deviceId, "设备ID") { IsVisibleInVariableManager = true },
                            new BooleanParameter("CoolingStatus", startCooling, "制冷状态") { IsVisibleInVariableManager = true },
                            new BooleanParameter("Success", success, "操作成功") { IsVisibleInVariableManager = true }
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
                    PauseCommand = pauseCommand ?? "停止控制",
                    ResumeCommand = resumeCommand ?? activateCommand,
                    ActivatedTime = DateTime.Now,
                    IsPaused = false,
                    StateData = stateData
                };

                _activeStates.Add(newState);
                InfoLog($"高低温循环器设备添加活跃状态: {stateName}");
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
                    InfoLog($"高低温循环器设备移除活跃状态: {stateName}");
                }

                var pausedState = _pausedStates.FirstOrDefault(s => s.StateName == stateName);
                if (pausedState != null)
                {
                    _pausedStates.Remove(pausedState);
                    InfoLog($"高低温循环器设备移除暂停状态: {stateName}");
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
            InfoLog("开始初始化高低温循环器设备");

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
                var stopTask = StopAllActiveStatesAsync();
                stopTask.Wait(TimeSpan.FromSeconds(5));

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
            InfoLog("高低温循环器设备连接");
            await Task.Delay(100);
            return true;
        }

        protected override async Task<bool> OnDisconnectAsync()
        {
            InfoLog("高低温循环器设备断开连接");
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
                _logger.LogInformation($"[高低温循环器设备命令-开始] Device={Name} Command={commandName}");

                var sw = Stopwatch.StartNew();
                try
                {
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();

                    _logger.LogInformation($"[高低温循环器设备命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[高低温循环器设备命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }
    }
}