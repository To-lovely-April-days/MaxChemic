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
    public class PeristalticPumps_ModbusTCP : Device, IControllableDevice, IFlowLifecycleAware
    {
        private readonly ILogService _logger;
        private bool _isRunning = false;
        private double _currentSpeed = 0;
        private DateTime _startTime;
        private readonly object _stateLock = new object();

        // *** 状态跟踪相关字段 ***
        private readonly List<DeviceActiveState> _activeStates = new List<DeviceActiveState>();
        private readonly List<DeviceActiveState> _pausedStates = new List<DeviceActiveState>();
        private readonly object _statesLock = new object();

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

            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/Pistonpumps.png";

            Parameters.Variables.Add(new StringParameter("DeviceId", "P0101")
            {
                Options = new ObservableCollection<string>(new List<string>() { "P0101", "P0111", "P0121" }),
                HelpText = "ModbusTCP设备ID"
            });

            InitializeCommands();
        }

        private void InitializeCommands()
        {
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
                        new NumberParameter("PumpSpeed", 0, 5000, 0, "泵速(RPM)") { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("获取泵速", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double pumpSpeed = 0;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
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
                            new NumberParameter("PumpSpeed", 0, 5000, Math.Round(pumpSpeed, 1), "泵速(RPM)") { IsVisibleInVariableManager = true }
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
                        new NumberParameter("TargetSpeed", 0, 200, 100, "目标泵速(RPM)")
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("Success", false, "设置成功") { IsVisibleInVariableManager = true },
                        new NumberParameter("SetSpeed", 0, 5000, 0, "设置的泵速(RPM)") { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("设置泵速", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double targetSpeed = parameters.GetValue<double>("TargetSpeed");
                    bool success = false;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            success = await ConnectionManager.WriteAsync(ComId, "P0101泵速", targetSpeed);
                            InfoLog($"向ModbusTCP地址40001设置泵速: {targetSpeed} RPM, 结果: {success}");
                        }
                        else if (IsSimulationMode)
                        {
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
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"设置泵速失败: {ex.Message}");
                        throw;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new BooleanParameter("Success", success, "设置成功") { IsVisibleInVariableManager = true },
                            new NumberParameter("SetSpeed", 0, 5000, targetSpeed, "设置的泵速(RPM)") { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });

            // 3. 停止泵 - 写入40001设置为0
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
                    ComId = Parameters.GetValue<string>("DeviceId");
                    bool success = false;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            success = await ConnectionManager.WriteAsync(ComId, "P0101泵速", 0.0);
                            InfoLog($"停止蠕动泵（ModbusTCP地址40001设置为0）, 结果: {success}");
                        }
                        else if (IsSimulationMode)
                        {
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
                    }
                    catch (Exception ex)
                    {
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
                        new NumberParameter("Pressure", 0, 10, 0, "出口压力(MPa)") { IsVisibleInVariableManager = true }
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
                            new NumberParameter("Pressure", 0, 10, Math.Round(pressure, 3), "出口压力(MPa)") { IsVisibleInVariableManager = true }
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
                        new NumberParameter("Temperature", -50, 200, 0, "出口温度(℃)") { IsVisibleInVariableManager = true }
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
                            new NumberParameter("Temperature", -50, 200, Math.Round(temperature, 1), "出口温度(℃)") { IsVisibleInVariableManager = true }
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
                        new NumberParameter("FlowRate", 0, 1000, 0, "出口流量(mL/min)") { IsVisibleInVariableManager = true }
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
                            new NumberParameter("FlowRate", 0, 1000, Math.Round(flowRate, 2), "出口流量(mL/min)") { IsVisibleInVariableManager = true }
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