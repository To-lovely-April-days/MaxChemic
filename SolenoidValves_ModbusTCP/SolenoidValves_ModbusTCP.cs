using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;

namespace SolenoidValves_ModbusTCP
{
    [Export(typeof(IDevice))]
    public class SolenoidValves_ModbusTCP : Device, IDeviceUIInteraction, IControllableDevice, IFlowLifecycleAware
    {
        private readonly ILogService _logger;
        private readonly object _stateLock = new object();

        // *** 状态跟踪相关字段 ***
        private readonly List<DeviceActiveState> _activeStates = new List<DeviceActiveState>();
        private readonly List<DeviceActiveState> _pausedStates = new List<DeviceActiveState>();
        private readonly object _statesLock = new object();
        private bool _isOpen = false; //  新增：当前设备的开关状态
        // 电磁阀状态跟踪
        private readonly Dictionary<string, bool> _valveStates = new Dictionary<string, bool>();
        //  新增：当前状态字段
        private SolenoidValveState _currentState = SolenoidValveState.Closed;
        #region IDeviceUIInteraction 实现


        public async Task<CommandExecutionResult> ExecuteUICommandAsync(string commandName, Dictionary<string, object> parameters = null)
        {
            try
            {
                InfoLog($"收到UI命令: {commandName}");

                switch (commandName)
                {
                    case "打开电磁阀":
                    case "Open":
                        return await OpenValveAsync();

                    case "关闭电磁阀":
                    case "Close":
                        return await CloseValveAsync();

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
                    ["ValveState"] = _currentState, //  返回业务状态
                    ["IsOpen"] = _isOpen,
                    ["ComId"] = this.ComId
                }
            };
        }

        #endregion

        #region 内部方法 - UI命令执行

        /// <summary>
        /// 打开电磁阀（UI调用）
        /// </summary>
        private async Task<CommandExecutionResult> OpenValveAsync()
        {
            try
            {
                if (_isOpen)
                {
                    return new CommandExecutionResult
                    {
                        Success = true,
                        Message = "电磁阀已是打开状态"
                    };
                }

                string valveId = Parameters.GetValue<string>("DeviceId");

                // 构造命令参数
                var commandParams = new DeviceParameters();
                // 不需要传入 ValveId，因为设备自己知道

                // 执行设备的"打开电磁阀"命令
                var openCommand = Commands.FirstOrDefault(c => c.Name == "打开电磁阀");
                if (openCommand?.Action == null)
                {
                    return new CommandExecutionResult
                    {
                        Success = false,
                        Message = "未找到打开电磁阀命令"
                    };
                }

                // 执行命令
                var outputParams = await Task.Run(() => openCommand.Action(commandParams));

                // 检查执行结果
                var success = outputParams.GetValue<bool>("Success");

                if (success)
                {
                    _isOpen = true;

                    //  触发状态变化事件（通知UI更新）
                    OnStateChanged("IsOpen", true);

                    InfoLog($"电磁阀 {valveId} 已打开");
                    return new CommandExecutionResult
                    {
                        Success = true,
                        Message = "电磁阀打开成功",
                        OutputData = new Dictionary<string, object>
                        {
                            ["IsOpen"] = true,
                            ["ValveId"] = valveId,
                            ["Timestamp"] = DateTime.Now
                        }
                    };
                }
                else
                {
                    return new CommandExecutionResult
                    {
                        Success = false,
                        Message = "电磁阀打开失败"
                    };
                }
            }
            catch (Exception ex)
            {
                ErrorLog("打开电磁阀失败", ex);
                return new CommandExecutionResult
                {
                    Success = false,
                    Message = ex.Message,
                    Exception = ex
                };
            }
        }

        /// <summary>
        /// 关闭电磁阀（UI调用）
        /// </summary>
        private async Task<CommandExecutionResult> CloseValveAsync()
        {
            try
            {
                if (!_isOpen)
                {
                    return new CommandExecutionResult
                    {
                        Success = true,
                        Message = "电磁阀已是关闭状态"
                    };
                }

                string valveId = Parameters.GetValue<string>("DeviceId");

                var commandParams = new DeviceParameters();

                var closeCommand = Commands.FirstOrDefault(c => c.Name == "关闭电磁阀");
                if (closeCommand?.Action == null)
                {
                    return new CommandExecutionResult
                    {
                        Success = false,
                        Message = "未找到关闭电磁阀命令"
                    };
                }

                var outputParams = await Task.Run(() => closeCommand.Action(commandParams));

                var success = outputParams.GetValue<bool>("Success");

                if (success)
                {
                    _isOpen = false;

                    //  触发状态变化事件（通知UI更新）
                    OnStateChanged("IsOpen", false);

                    InfoLog($"电磁阀 {valveId} 已关闭");
                    return new CommandExecutionResult
                    {
                        Success = true,
                        Message = "电磁阀关闭成功",
                        OutputData = new Dictionary<string, object>
                        {
                            ["IsOpen"] = false,
                            ["ValveId"] = valveId,
                            ["Timestamp"] = DateTime.Now
                        }
                    };
                }
                else
                {
                    return new CommandExecutionResult
                    {
                        Success = false,
                        Message = "电磁阀关闭失败"
                    };
                }
            }
            catch (Exception ex)
            {
                ErrorLog("关闭电磁阀失败", ex);
                return new CommandExecutionResult
                {
                    Success = false,
                    Message = ex.Message,
                    Exception = ex
                };
            }
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
                    InfoLog("ModbusTCP电磁阀没有活跃状态需要暂停");
                    return true;
                }
                InfoLog($"暂停ModbusTCP电磁阀的 {_activeStates.Count} 个活跃状态");
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
                InfoLog(allSuccess ? "ModbusTCP电磁阀所有活跃状态已暂停" : "ModbusTCP电磁阀部分活跃状态暂停失败");
                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP电磁阀暂停活跃状态异常", ex);
                return false;
            }
        }

        public virtual async Task<bool> ResumeAllPausedStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_pausedStates.Any())
                {
                    InfoLog("ModbusTCP电磁阀没有暂停的状态需要恢复");
                    return true;
                }
                InfoLog($"恢复ModbusTCP电磁阀的 {_pausedStates.Count} 个暂停的状态");
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
                InfoLog(allSuccess ? "ModbusTCP电磁阀所有暂停状态已恢复" : "ModbusTCP电磁阀部分暂停状态恢复失败");
                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP电磁阀恢复暂停状态异常", ex);
                return false;
            }
        }

        public virtual async Task<bool> StopAllActiveStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_activeStates.Any() && !_pausedStates.Any())
                {
                    InfoLog("ModbusTCP电磁阀没有活跃或暂停的状态需要停止");
                    return true;
                }
                InfoLog($"停止ModbusTCP电磁阀的 {_activeStates.Count} 个活跃状态和 {_pausedStates.Count} 个暂停状态");
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

                InfoLog(allSuccess ? "ModbusTCP电磁阀所有状态已停止" : "ModbusTCP电磁阀部分状态停止失败");
                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP电磁阀停止状态异常", ex);
                return false;
            }
        }

        #endregion

        #region IFlowLifecycleAware Implementation

        public virtual async Task<bool> OnFlowStartedAsync(FlowStartContext context)
        {
            try
            {
                InfoLog($"ModbusTCP电磁阀流程开始: {context.FlowName}");

                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                lock (_stateLock)
                {
                    _valveStates.Clear();
                }
                //  自动获取当前状态
                var getStatusCommand = Commands.FirstOrDefault(c => c.Name == "获取电磁阀状态");
                if (getStatusCommand?.Action != null)
                {
                    var result = await Task.Run(() => getStatusCommand.Action(new DeviceParameters()));
                    InfoLog("流程启动时已自动获取电磁阀状态");
                }
                InfoLog("ModbusTCP电磁阀设备已准备好执行流程");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP电磁阀流程开始处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowCompletedAsync(FlowCompletionContext context)
        {
            try
            {
                InfoLog($"ModbusTCP电磁阀流程完成: {context.FlowName}, 成功: {context.IsSuccessful}");
                //await StopAllActiveStatesAsync();
                InfoLog("ModbusTCP电磁阀流程完成处理结束");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP电磁阀流程完成处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowFailedAsync(FlowFailureContext context)
        {
            try
            {
                ErrorLog($"ModbusTCP电磁阀流程失败: {context.FlowName}, 错误: {context.ErrorMessage}");
                await StopAllActiveStatesAsync();
                InfoLog("ModbusTCP电磁阀流程失败处理完成");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP电磁阀流程失败处理异常", ex);
                return false;
            }
        }

        #endregion

        public SolenoidValves_ModbusTCP()
        {
            _logger = LogManager.GetLogger<SolenoidValves_ModbusTCP>();

            Name = "电磁阀";
            Manufacturer = "ModbusTCP电磁阀";
            Category = DeviceCategories.Valves;
            ComId = "SV0101"; // 默认电磁阀ID

            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();

            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            //ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/SolenoidValves_ZBS_1RL.png";
            ImageLocation = "CUSTOM_CONTROL:SolenoidValveControl";
            Parameters.Variables.Add(new StringParameter("DeviceId", "SV0101")
            {
                Options = new ObservableCollection<string>(new List<string>()
        {
            "SV0101", "SV0102", "SV0111", "SV0112", "SV0121", "SV0122", "SV0113", "SV0114"
        }),
                HelpText = "ModbusTCP电磁阀设备ID"
            });
            // *** 新增：控制逻辑参数 ***
            Parameters.Variables.Add(new BooleanParameter("IsLogicReversed", false)
            {
                HelpText = "控制逻辑是否反向 (false: true=开/false=关, true: false=开/true=关)"
            });
            InitializeCommands();
        }

        private void InitializeCommands()
        {
            // 1. 打开电磁阀命令 - 超简洁版本
            Commands.Add(new DeviceCommand
            {
                Name = "打开电磁阀",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/open.png",
                HelpText = "打开当前电磁阀",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                {
                    new BooleanParameter("Success", false, "打开成功") { IsVisibleInVariableManager = true },
                    new StringParameter("ValveId", "", "电磁阀ID") { IsVisibleInVariableManager = true }
                }
                },
                Action = WrapActionWithAutoStateSync("打开电磁阀",
        async (parameters) =>
        {
            string valveId = Parameters.GetValue<string>("DeviceId");
            ComId = valveId;
            bool success = false;

            try
            {
                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                    ConnectionManager != null && !IsSimulationMode)
                {
                    bool controlValue = !Parameters.GetValue<bool>("IsLogicReversed");
                    success = await ConnectionManager.WriteAsync(ComId, $"{valveId}手动启停", controlValue);
                }
                else if (IsSimulationMode)
                {
                    await Task.Delay(3000);
                    lock (_stateLock)
                    {
                        _valveStates[valveId] = true;
                    }
                    success = true;
                }

                if (success)
                {
                    _isOpen = true;
                    AddActiveState(
                        stateName: $"ValveOpen_{valveId}",
                        activateCommand: "打开电磁阀",
                        deactivateCommand: "关闭电磁阀",
                        pauseCommand: "关闭电磁阀",
                        resumeCommand: "打开电磁阀",
                        stateData: valveId
                    );
                }
            }
            catch (Exception ex)
            {
                ErrorLog($"打开电磁阀失败: {valveId} - {ex.Message}");
            }

            return new DeviceParameters
            {
                Variables = new ObservableCollection<ParameterBase>
                {
                    new BooleanParameter("Success", success, "打开成功") { IsVisibleInVariableManager = true },
                    new StringParameter("ValveId", valveId, "电磁阀ID") { IsVisibleInVariableManager = true }
                }
            };
        },

        //  开始执行：正在打开
        onStartStateSync: () =>
        {
            _currentState = SolenoidValveState.Opening;
            OnStateChanged("ValveState", SolenoidValveState.Opening); //  使用枚举
            Debug.WriteLine("电磁阀开始打开");
        },

        //  成功：已打开
        onSuccessStateSync: (output) =>
        {
            _currentState = SolenoidValveState.Open;
            OnStateChanged("ValveState", SolenoidValveState.Open); //  使用枚举
            Debug.WriteLine(" 电磁阀成功打开");
        },

        //  失败：操作失败
        onFailureStateSync: (output) =>
        {
            _currentState = SolenoidValveState.Failed;
            OnStateChanged("ValveState", SolenoidValveState.Failed); //  使用枚举
            Debug.WriteLine(" 电磁阀打开失败");
        },

        //  异常：通讯错误
        onErrorStateSync: (errorMessage) =>
        {
            _currentState = SolenoidValveState.Failed;
            OnStateChanged("ValveState", SolenoidValveState.Error); //  使用枚举
            Debug.WriteLine($" 电磁阀打开异常: {errorMessage}");
        }
    )
            });

            // 2. 关闭电磁阀命令
            Commands.Add(new DeviceCommand
            {
                Name = "关闭电磁阀",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/off.png",
                HelpText = "关闭当前电磁阀",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                {
                    new BooleanParameter("Success", false, "关闭成功") { IsVisibleInVariableManager = true },
                    new StringParameter("ValveId", "", "电磁阀ID") { IsVisibleInVariableManager = true }
                }
                },
                Action = WrapActionWithAutoStateSync("关闭电磁阀",
                    async (parameters) =>
                    {
                        string valveId = Parameters.GetValue<string>("DeviceId");
                        ComId = valveId;
                        bool success = false;

                        try
                        {
                            if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp &&
                                ConnectionManager != null && !IsSimulationMode)
                            {
                                bool controlValue = Parameters.GetValue<bool>("IsLogicReversed");
                                success = await ConnectionManager.WriteAsync(ComId, $"{valveId}手动启停", controlValue);
                                InfoLog($"关闭电磁阀 {valveId}, 发送值: {controlValue}, 结果: {success}");
                            }
                            else if (IsSimulationMode)
                            {
                                await Task.Delay(3000);
                                lock (_stateLock)
                                {
                                    _valveStates[valveId] = false;
                                }
                                success = true;
                                InfoLog($"模拟模式关闭电磁阀: {valveId}");
                            }

                            if (success)
                            {
                                _isOpen = false;
                                RemoveActiveState($"ValveOpen_{valveId}");
                            }
                        }
                        catch (Exception ex)
                        {
                            ErrorLog($"关闭电磁阀失败: {valveId} - {ex.Message}");
                        }

                        return new DeviceParameters
                        {
                            Variables = new ObservableCollection<ParameterBase>
                            {
                            new BooleanParameter("Success", success, "关闭成功") { IsVisibleInVariableManager = true },
                            new StringParameter("ValveId", valveId, "电磁阀ID") { IsVisibleInVariableManager = true }
                            }
                        };
                    },
        //  开始执行：正在关闭
        onStartStateSync: () =>
        {
            _currentState = SolenoidValveState.Closing;
            OnStateChanged("ValveState", SolenoidValveState.Closing);
        },

        //  成功：已关闭
        onSuccessStateSync: (output) =>
        {
            _currentState = SolenoidValveState.Closed;
            OnStateChanged("ValveState", SolenoidValveState.Closed);
        },

        //  失败：操作失败
        onFailureStateSync: (output) =>
        {
            _currentState = SolenoidValveState.Failed;
            OnStateChanged("ValveState", SolenoidValveState.Failed);
        },

        //  异常：通讯错误
        onErrorStateSync: (errorMessage) =>
        {
            _currentState = SolenoidValveState.Error;
            OnStateChanged("ValveState", SolenoidValveState.Error);
        }
                )
            });


            // 3. 获取电磁阀状态命令（可选）
            Commands.Add(new DeviceCommand
            {
                Name = "获取电磁阀状态",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/status.png",
                HelpText = "获取当前电磁阀的状态",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
            {
                new StringParameter("ValveId", "", "电磁阀ID") { IsVisibleInVariableManager = true },
                new BooleanParameter("ValveState", false, "电磁阀状态") { IsVisibleInVariableManager = true },
                new StringParameter("StateDescription", "", "状态描述") { IsVisibleInVariableManager = true },
                new BooleanParameter("Success", false, "读取成功") { IsVisibleInVariableManager = true }
            }
                },
                Action = WrapAction("获取电磁阀状态", async delegate (DeviceParameters parameters)
                {
                    string valveId = Parameters.GetValue<string>("DeviceId");
                    ComId = valveId; // 设置通信ID为当前设备ID
                    bool valveState = false;
                    bool success = false;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            bool deviceValue = await ConnectionManager.ReadAsync<bool>(ComId, $"{valveId}手动启停");
                            // *** 使用转换后的状态值 ***

                            if (Parameters.GetValue<bool>("IsLogicReversed"))
                            {
                                deviceValue=!deviceValue;
                            }
                            valveState = deviceValue;
                            InfoLog($"读取电磁阀 {valveId} 原始值: {deviceValue}, 转换后状态: {valveState}");
                            success = true;
                            _isOpen = valveState;  // 更新内部状态
                            // 触发状态变化事件，通知UI更新
                            OnStateChanged("ValveState", valveState ? SolenoidValveState.Open : SolenoidValveState.Closed);
                            OnStateChanged("IsOpen", valveState);
                        }
                        else if (IsSimulationMode)
                        {
                            lock (_stateLock)
                            {
                                _valveStates.TryGetValue(valveId, out valveState); // 模拟模式下直接使用逻辑状态
                            }
                            InfoLog($"模拟模式读取电磁阀 {valveId} 状态: {valveState}");
                            success = true;
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"读取电磁阀状态失败: {valveId} - {ex.Message}");
                        success = false;
                    }

                    string stateDescription = valveState ? "打开" : "关闭";

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
        {
            new StringParameter("ValveId", valveId, "电磁阀ID") { IsVisibleInVariableManager = true },
            new BooleanParameter("ValveState", valveState, "电磁阀状态") { IsVisibleInVariableManager = true },
            new StringParameter("StateDescription", stateDescription, "状态描述") { IsVisibleInVariableManager = true },
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
                    PauseCommand = pauseCommand ?? deactivateCommand,
                    ResumeCommand = resumeCommand ?? activateCommand,
                    ActivatedTime = DateTime.Now,
                    IsPaused = false,
                    StateData = stateData
                };

                _activeStates.Add(newState);
                InfoLog($"ModbusTCP电磁阀添加活跃状态: {stateName}");
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
                    InfoLog($"ModbusTCP电磁阀移除活跃状态: {stateName}");
                }

                var pausedState = _pausedStates.FirstOrDefault(s => s.StateName == stateName);
                if (pausedState != null)
                {
                    _pausedStates.Remove(pausedState);
                    InfoLog($"ModbusTCP电磁阀移除暂停状态: {stateName}");
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
                InfoLog($"暂停ModbusTCP电磁阀状态: {state.StateName}");

                // 暂停时关闭电磁阀
                if (state.StateData is string valveId)
                {
                    var pauseParams = new DeviceParameters();
                    pauseParams.Variables.Add(new StringParameter("ValveId", valveId, "电磁阀ID"));
                    await ExecuteDeviceCommandByNameAsync("关闭电磁阀", pauseParams);
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
                ErrorLog($"ModbusTCP电磁阀暂停状态失败: {state.StateName}", ex);
                return false;
            }
        }

        private async Task<bool> ResumeStateAsync(DeviceActiveState state)
        {
            try
            {
                InfoLog($"恢复ModbusTCP电磁阀状态: {state.StateName}");

                // 恢复时重新打开电磁阀
                if (state.StateData is string valveId)
                {
                    var resumeParams = new DeviceParameters();
                    resumeParams.Variables.Add(new StringParameter("ValveId", valveId, "电磁阀ID"));
                    await ExecuteDeviceCommandByNameAsync("打开电磁阀", resumeParams);
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
                ErrorLog($"ModbusTCP电磁阀恢复状态失败: {state.StateName}", ex);
                return false;
            }
        }

        private async Task<bool> StopStateAsync(DeviceActiveState state)
        {
            try
            {
                InfoLog($"停止ModbusTCP电磁阀状态: {state.StateName}");

                // 停止时关闭电磁阀
                if (state.StateData is string valveId)
                {
                    var stopParams = new DeviceParameters();
                    stopParams.Variables.Add(new StringParameter("ValveId", valveId, "电磁阀ID"));
                    await ExecuteDeviceCommandByNameAsync("关闭电磁阀", stopParams);
                }

                return true;
            }
            catch (Exception ex)
            {
                ErrorLog($"ModbusTCP电磁阀停止状态失败: {state.StateName}", ex);
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
                InfoLog($"ModbusTCP电磁阀执行命令: {commandName}");
            }
            else
            {
                WarnLog($"ModbusTCP电磁阀未找到命令: {commandName}");
            }
        }

        #endregion

        #region 基类重写方法

        public override void Initialize()
        {
            InfoLog("开始初始化ModbusTCP电磁阀设备");

            try
            {
                base.Initialize();

                lock (_stateLock)
                {
                    _valveStates.Clear();
                }

                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                InfoLog("ModbusTCP电磁阀设备初始化完成");
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP电磁阀设备初始化失败", ex);
                throw;
            }
        }

        public override void Shutdown()
        {
            InfoLog("开始关闭ModbusTCP电磁阀设备");

            try
            {
                var stopTask = StopAllActiveStatesAsync();
                stopTask.Wait(TimeSpan.FromSeconds(5));

                InfoLog("ModbusTCP电磁阀设备关闭完成");
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP电磁阀设备关闭失败", ex);
                throw;
            }
        }

        protected override async Task<bool> OnConnectAsync()
        {
            InfoLog("ModbusTCP电磁阀连接");
            await Task.Delay(100);
            return true;
        }

        protected override async Task<bool> OnDisconnectAsync()
        {
            InfoLog("ModbusTCP电磁阀断开连接");
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
                _logger.LogInformation($"[ModbusTCP电磁阀命令-开始] Device={Name} Command={commandName}");

                var sw = Stopwatch.StartNew();
                try
                {
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();

                    _logger.LogInformation($"[ModbusTCP电磁阀命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[ModbusTCP电磁阀命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }
    }
}