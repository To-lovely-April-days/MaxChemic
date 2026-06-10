using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;

namespace PeristalticPumps_CY
{
    [Export(typeof(IDevice))]
    public class PeristalticPumps_CY : Device, IControllableDevice, IFlowLifecycleAware
    {
        private SerialPort _serialPort;
        private readonly ILogService _logger;
        private bool _isRunning = false;
        private double _currentSpeed = 0;
        private double _currentFlowRate = 0;
        private double _currentPressure = 0;
        private double _currentTemperature = 25.0;
        private DateTime _startTime;
        private double _totalVolume = 0;
        private readonly object _stateLock = new object();

        // *** 状态跟踪相关字段 ***
        private readonly List<DeviceActiveState> _activeStates = new List<DeviceActiveState>();
        private readonly List<DeviceActiveState> _pausedStates = new List<DeviceActiveState>();
        private readonly SemaphoreSlim _serialPortSemaphore = new SemaphoreSlim(1, 1);
        private readonly object _serialLock = new object();

        private readonly object _statesLock = new object();
        private string _lastReceivedData = string.Empty;
        // ═══════════════════════════════════════════════════════
        //  单位常量(全项目统一标准:温度=℃,流量=mL/min,压力=MPa)
        // ═══════════════════════════════════════════════════════
        private const string UnitTemperature = "℃";
        private const string UnitFlow = "mL/min";
        private const string UnitVolume = "mL";
        private const string UnitPressure = "MPa";
        private const string UnitPumpSpeed = "RPM";
        private const string UnitTimeMin = "min";
        // 响应分隔符配置
        private const string RESPONSE_DELIMITER = "\r\n"; // 根据您的设备调整
        private const int MAX_RESPONSE_LENGTH = 1024;

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
                    InfoLog("蠕动泵没有活跃状态需要暂停");
                    return true;
                }

                InfoLog($"暂停蠕动泵的 {_activeStates.Count} 个活跃状态");
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

                if (allSuccess)
                {
                    InfoLog("蠕动泵所有活跃状态已暂停");
                }
                else
                {
                    WarnLog("蠕动泵部分活跃状态暂停失败");
                }

                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("蠕动泵暂停活跃状态异常", ex);
                return false;
            }
        }

        public virtual async Task<bool> ResumeAllPausedStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_pausedStates.Any())
                {
                    InfoLog("蠕动泵没有暂停的状态需要恢复");
                    return true;
                }

                InfoLog($"恢复蠕动泵的 {_pausedStates.Count} 个暂停的状态");
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
                    InfoLog("蠕动泵所有暂停状态已恢复");
                }
                else
                {
                    WarnLog("蠕动泵部分暂停状态恢复失败");
                }

                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("蠕动泵恢复暂停状态异常", ex);
                return false;
            }
        }

        public virtual async Task<bool> StopAllActiveStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_activeStates.Any() && !_pausedStates.Any())
                {
                    InfoLog("蠕动泵没有活跃或暂停的状态需要停止");
                    return true;
                }

                InfoLog($"停止蠕动泵的 {_activeStates.Count} 个活跃状态和 {_pausedStates.Count} 个暂停状态");
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

                // 清空所有状态
                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                if (allSuccess)
                {
                    InfoLog("蠕动泵所有状态已停止");
                }
                else
                {
                    WarnLog("蠕动泵部分状态停止失败");
                }

                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("蠕动泵停止状态异常", ex);
                return false;
            }
        }

        #endregion

        #region IFlowLifecycleAware Implementation

        /// <summary>
        /// 流程开始时的处理
        /// </summary>
        public virtual async Task<bool> OnFlowStartedAsync(FlowStartContext context)
        {
            ComId = Parameters.GetValue<string>("COM Port");
            try
            {
                InfoLog($"流程开始: {context.FlowName}, 预计节点数: {context.ExpectedNodeCount}");

                // 1. 清空之前的状态
                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                // 2. 重置设备状态
                lock (_stateLock)
                {
                    _totalVolume = 0;
                    _startTime = DateTime.Now;
                }

                // 3. 检查设备连接状态
                if (!IsConnected)
                {
                    WarnLog("设备未连接，但流程已开始");
                }

                // 4. 执行设备预热或初始化序列（如果需要）
                await ExecutePreparationSequenceAsync();

                InfoLog("设备已准备好执行流程");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("流程开始处理失败", ex);
                return false;
            }
        }

        /// <summary>
        /// 流程完成时的处理 - 关键方法
        /// </summary>
        public virtual async Task<bool> OnFlowCompletedAsync(FlowCompletionContext context)
        {
            try
            {
                InfoLog($"流程完成处理开始: {context.FlowName}, 成功: {context.IsSuccessful}, 持续时间: {context.Duration}");

                // 1. 停止所有活跃状态
                InfoLog("停止所有活跃状态");
                await StopAllActiveStatesAsync();

                // 2. 执行设备特定的完成逻辑
                //await ExecuteCompletionSequenceAsync(context);

                // 3. 记录统计信息
                RecordFlowStatistics(context);

                // 4. 清理和重置
                await CleanupAfterFlowAsync();

                InfoLog($"流程完成处理结束: {context.FlowName}");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("流程完成处理失败", ex);
                return false;
            }
        }

        /// <summary>
        /// 流程失败时的处理
        /// </summary>
        public virtual async Task<bool> OnFlowFailedAsync(FlowFailureContext context)
        {
            try
            {
                ErrorLog($"流程失败处理: {context.FlowName}, 失败节点: {context.FailedNodeName}, 错误: {context.ErrorMessage}");

                // 1. 紧急停止所有操作
                await EmergencyStopAsync();

                // 2. 记录失败信息
                RecordFailureStatistics(context);

                // 3. 清理状态
                await CleanupAfterFlowAsync();

                InfoLog("流程失败处理完成");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("流程失败处理异常", ex);
                return false;
            }
        }

        #endregion

        #region 私有辅助方法

        /// <summary>
        /// 执行准备序列
        /// </summary>
        private async Task ExecutePreparationSequenceAsync()
        {
            try
            {
                InfoLog("执行设备准备序列");

                // 1. 检查设备状态
                if (IsSimulationMode)
                {
                    InfoLog("模拟模式：跳过硬件检查");
                }
                else
                {
                    // 实际模式下的硬件检查
                    if (_serialPort?.IsOpen == true)
                    {
                        // 发送状态查询命令
                        byte[] statusCmd = System.Text.Encoding.GetEncoding("GBK").GetBytes("STATUS?");
                        _serialPort.Write(statusCmd, 0, statusCmd.Length);
                        await Task.Delay(100);
                    }
                }

                // 2. 初始化温度监控
                lock (_stateLock)
                {
                    _currentTemperature = 25.0; // 初始室温
                }

                InfoLog("设备准备序列完成");
            }
            catch (Exception ex)
            {
                ErrorLog("设备准备序列失败", ex);
            }
        }


        /// <summary>
        /// 紧急停止
        /// </summary>
        private async Task EmergencyStopAsync()
        {
            try
            {
                InfoLog("执行紧急停止");

                // 立即停止所有操作
                lock (_stateLock)
                {
                    _isRunning = false;
                    _currentSpeed = 0;
                    _currentFlowRate = 0;
                }

                // 发送紧急停止指令
                if (_serialPort?.IsOpen == true)
                {
                    byte[] emergencyStopCmd = System.Text.Encoding.GetEncoding("GBK").GetBytes("EMERGENCY_STOP");
                    _serialPort.Write(emergencyStopCmd, 0, emergencyStopCmd.Length);
                }

                await Task.Delay(100);
                InfoLog("紧急停止完成");
            }
            catch (Exception ex)
            {
                ErrorLog("紧急停止失败", ex);
            }
        }

        /// <summary>
        /// 记录流程统计信息
        /// </summary>
        private void RecordFlowStatistics(FlowCompletionContext context)
        {
            try
            {
                var stats = new
                {
                    FlowName = context.FlowName,
                    Duration = context.Duration,
                    IsSuccessful = context.IsSuccessful,
                    TotalVolume = _totalVolume,
                    AverageFlowRate = context.Duration.TotalMinutes > 0 ? _totalVolume / context.Duration.TotalMinutes : 0,
                    MaxTemperature = _currentTemperature,
                    CompletionTime = context.CompletionTime
                };

                InfoLog($"流程统计: {System.Text.Json.JsonSerializer.Serialize(stats)}");
            }
            catch (Exception ex)
            {
                ErrorLog("记录统计信息失败", ex);
            }
        }

        /// <summary>
        /// 记录失败统计信息
        /// </summary>
        private void RecordFailureStatistics(FlowFailureContext context)
        {
            try
            {
                var failureStats = new
                {
                    FlowName = context.FlowName,
                    FailedNodeId = context.FailedNodeId,
                    FailedNodeName = context.FailedNodeName,
                    ErrorMessage = context.ErrorMessage,
                    PartialVolume = _totalVolume,
                    PartialDuration = context.Duration,
                    FailureTime = context.CompletionTime
                };

                ErrorLog($"流程失败统计: {System.Text.Json.JsonSerializer.Serialize(failureStats)}");
            }
            catch (Exception ex)
            {
                ErrorLog("记录失败统计失败", ex);
            }
        }

        /// <summary>
        /// 流程后清理
        /// </summary>
        private async Task CleanupAfterFlowAsync()
        {
            try
            {
                InfoLog("执行流程后清理");

                // 1. 清空所有状态
                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                // 2. 重置统计数据
                lock (_stateLock)
                {
                    _totalVolume = 0;
                }

                // 3. 等待一段时间确保所有操作完成
                await Task.Delay(1000);
                //if (!DeviceConnectionConfig.UsePLCMode)
                //{
                //    _serialPort.Close();
                //    _serialPort.Dispose();
                //}
                InfoLog("流程后清理完成");
            }
            catch (Exception ex)
            {
                ErrorLog("流程后清理失败", ex);
            }
        }

        #endregion
        #region 状态管理内部方法

        /// <summary>
        /// 添加活跃状态
        /// </summary>
        protected void AddActiveState(string stateName, string activateCommand, string deactivateCommand,
            string pauseCommand = null, string resumeCommand = null, object stateData = null)
        {
            lock (_statesLock)
            {
                // 检查是否已经存在相同状态
                var existing = _activeStates.FirstOrDefault(s => s.StateName == stateName);
                if (existing != null)
                {
                    WarnLog($"蠕动泵状态已存在，更新时间: {stateName}");
                    existing.ActivatedTime = DateTime.Now;
                    existing.StateData = stateData;
                    return;
                }

                var newState = new DeviceActiveState
                {
                    StateName = stateName,
                    ActivateCommand = activateCommand,
                    DeactivateCommand = deactivateCommand,
                    PauseCommand = pauseCommand,
                    ResumeCommand = resumeCommand,
                    ActivatedTime = DateTime.Now,
                    IsPaused = false,
                    StateData = stateData
                };

                _activeStates.Add(newState);
                InfoLog($"蠕动泵添加活跃状态: {stateName}");
            }

            StateChanged?.Invoke(this, new DeviceStateChangedEventArgs
            {
                StateName = stateName,
                IsActive = true,
                Timestamp = DateTime.Now
            });
        }

        /// <summary>
        /// 移除活跃状态
        /// </summary>
        protected void RemoveActiveState(string stateName)
        {
            lock (_statesLock)
            {
                var state = _activeStates.FirstOrDefault(s => s.StateName == stateName);
                if (state != null)
                {
                    _activeStates.Remove(state);
                    InfoLog($"蠕动泵移除活跃状态: {stateName}");
                }

                // 同时从暂停状态中移除
                var pausedState = _pausedStates.FirstOrDefault(s => s.StateName == stateName);
                if (pausedState != null)
                {
                    _pausedStates.Remove(pausedState);
                    InfoLog($"蠕动泵移除暂停状态: {stateName}");
                }
            }

            StateChanged?.Invoke(this, new DeviceStateChangedEventArgs
            {
                StateName = stateName,
                IsActive = false,
                Timestamp = DateTime.Now
            });
        }

        /// <summary>
        /// 暂停单个状态
        /// </summary>
        private async Task<bool> PauseStateAsync(DeviceActiveState state)
        {
            try
            {
                InfoLog($"暂停蠕动泵状态: {state.StateName}");

                // 执行暂停命令或停止命令
                if (!string.IsNullOrEmpty(state.PauseCommand))
                {
                    await ExecuteDeviceCommandByNameAsync(state.PauseCommand);
                }
                else if (!string.IsNullOrEmpty(state.DeactivateCommand))
                {
                    await ExecuteDeviceCommandByNameAsync(state.DeactivateCommand);
                }

                // 移动到暂停状态列表
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
                ErrorLog($"蠕动泵暂停状态失败: {state.StateName}", ex);
                return false;
            }
        }

        /// <summary>
        /// 恢复单个状态
        /// </summary>
        private async Task<bool> ResumeStateAsync(DeviceActiveState state)
        {
            try
            {
                InfoLog($"恢复蠕动泵状态: {state.StateName}");

                // 准备恢复参数
                var resumeParams = new DeviceParameters();

                // 如果有保存的状态数据，恢复参数
                if (state.StateData is PumpStateData pumpData)
                {
                    resumeParams.Variables.Add(new NumberParameter("TargetSpeed", 0, 5000, pumpData.TargetSpeed, "目标转速(RPM)"));
                    resumeParams.Variables.Add(new NumberParameter("RampTime", 0, 60, pumpData.RampTime, "加速时间(秒)"));
                    resumeParams.Variables.Add(new EnumParameter("Direction", typeof(PumpDirection), pumpData.Direction, "泵送方向"));
                    resumeParams.Variables.Add(new BooleanParameter("EnablePulseFree", pumpData.EnablePulseFree, "启用无脉动模式"));
                }

                // 执行恢复命令或重新激活命令
                if (!string.IsNullOrEmpty(state.ResumeCommand))
                {
                    await ExecuteDeviceCommandByNameAsync(state.ResumeCommand, resumeParams);
                }
                else if (!string.IsNullOrEmpty(state.ActivateCommand))
                {
                    await ExecuteDeviceCommandByNameAsync(state.ActivateCommand, resumeParams);
                }

                // 移动回活跃状态列表
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
                ErrorLog($"蠕动泵恢复状态失败: {state.StateName}", ex);
                return false;
            }
        }

        /// <summary>
        /// 停止单个状态
        /// </summary>
        private async Task<bool> StopStateAsync(DeviceActiveState state)
        {
            try
            {
                InfoLog($"停止蠕动泵状态: {state.StateName}");

                if (!string.IsNullOrEmpty(state.DeactivateCommand))
                {
                    await ExecuteDeviceCommandByNameAsync(state.DeactivateCommand);
                }

                return true;
            }
            catch (Exception ex)
            {
                ErrorLog($"蠕动泵停止状态失败: {state.StateName}", ex);
                return false;
            }
        }

        /// <summary>
        /// 按名称执行设备命令
        /// </summary>
        private async Task ExecuteDeviceCommandByNameAsync(string commandName, DeviceParameters parameters = null)
        {
            var command = Commands?.FirstOrDefault(c => c.Name == commandName);
            if (command?.Action != null)
            {
                var inputParams = parameters ?? new DeviceParameters();
                await Task.Run(() => command.Action(inputParams));
                InfoLog($"蠕动泵执行命令: {commandName}");
            }
            else
            {
                WarnLog($"蠕动泵未找到命令: {commandName}");
            }
        }

        #endregion

        // 蠕动泵状态数据类
        private class PumpStateData
        {
            public double TargetSpeed { get; set; }
            public double RampTime { get; set; }
            public PumpDirection Direction { get; set; }
            public bool EnablePulseFree { get; set; }
        }

        public PeristalticPumps_CY()
        {
            _logger = LogManager.GetLogger<PeristalticPumps_CY>();

            Name = "YT600-1J";
            Manufacturer = "蠕动泵_YT600-1J＆KZ35-13-A";
            Category = DeviceCategories.Pumps;
            ComId = "COM1"; // 这个ID需要与PLC映射中的设备ID匹配
                                   // 初始化连接管理器

            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();
            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/PeristalticPumps_CY.png";

            // 添加设备参数
            Parameters.Variables.Add(new StringParameter("COM Port", "COM1")
            {
                Options = new ObservableCollection<string>(SerialPort.GetPortNames()),
                HelpText = "The COM Port that the device is connected to."
            });

            InitializeCommands();
        }

        private void InitializeCommands()
        {
            // 1. 获取温度值命令
            Commands.Add(new DeviceCommand
            {
                Name = "获取温度值",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/temperature.png",
                HelpText = "获取蠕动泵当前温度值",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("UseCalibration", true, "是否使用校准值"),
                        new NumberParameter("Offset", -10, 10, 0, "温度偏移校准值")
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("Status", "PENDING", "执行状态") { IsVisibleInVariableManager = false },
                        new NumberParameter("Temperature", -50, 200, 0, "当前温度", true, UnitTemperature),
                        new StringParameter("Unit", "℃", "温度单位") { IsVisibleInVariableManager = false },
                        new StringParameter("Timestamp", "", "采集时间") { IsVisibleInVariableManager = false },
                        new BooleanParameter("IsWarning", false, "是否超温警告") { IsVisibleInVariableManager = true },
                        new StringParameter("Message", "", "附加信息") { IsVisibleInVariableManager = false }
                    }
                },
                Action = WrapAction("获取温度值", async delegate (DeviceParameters parameters)
                {
                    

                    bool useCalibration = parameters.GetValue<bool>("UseCalibration");
                    double offset = parameters.GetValue<double>("Offset");

                    double actualTemperature = 25.0;
                    string rawResponse = "";

                    try
                    {
                        if (DeviceConnectionConfig.UsePLCMode && ConnectionManager != null)
                        {
                            await ConnectionManager.WriteAsync<double>(ComId, "设置温度值", offset);
                            // PLC模式：从PLC读取温度
                            actualTemperature = await ConnectionManager.ReadAsync<double>(ComId, "获取温度值");
                            InfoLog($"从PLC获取到温度: {actualTemperature}°C");
                        }
                        else
                        {
                            // 串口模式：使用原有逻辑
                            rawResponse = await SendCommandAndWaitResponseAsync("获取温度指令", 5000);
                            actualTemperature = ParseTemperatureFromResponse(rawResponse);
                            InfoLog($"从串口获取到温度: {actualTemperature}°C");
                        }

                        // 更新内部状态
                        lock (_stateLock)
                        {
                            _currentTemperature = actualTemperature;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (IsSimulationMode)
                        {
                            // 模拟模式下使用模拟温度
                            lock (_stateLock)
                            {
                                if (_isRunning)
                                {
                                    _currentTemperature += new Random().NextDouble() * 2 - 1;
                                    _currentTemperature = Math.Min(_currentTemperature, 85);
                                }
                                else
                                {
                                    _currentTemperature += (25 - _currentTemperature) * 0.1;
                                }
                                actualTemperature = _currentTemperature;
                            }
                            WarnLog($"模拟模式获取温度: {actualTemperature}°C");
                        }
                        else
                        {
                            ErrorLog($"获取温度失败: {ex.Message}");
                            throw new InvalidOperationException($"获取设备温度失败: {ex.Message}", ex);
                        }
                    }

                    // 应用校准
                    double finalTemp = offset;
                    bool isWarning = finalTemp > 80;

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                {
                    new StringParameter("Status", "OK", "") { IsVisibleInVariableManager = false },
                    new NumberParameter("Temperature", -50, 200, Math.Round(_currentTemperature, 2), "当前温度", true, UnitTemperature),
                    new StringParameter("Unit", "℃", "") { IsVisibleInVariableManager = false },
                    new StringParameter("Timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), "") { IsVisibleInVariableManager = false },
                    new BooleanParameter("IsWarning", isWarning, "") { IsVisibleInVariableManager = true },
                    new StringParameter("Message", isWarning ? "温度过高警告！" : "温度正常", "") { IsVisibleInVariableManager = false },
                    new StringParameter("RawResponse", rawResponse, "原始响应数据") { IsVisibleInVariableManager = false }
                }
                    };
                })
            });

            // 2. 获取流量值命令
            Commands.Add(new DeviceCommand
            {
                Name = "获取流量值",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/flowrate.png",
                HelpText = "获取蠕动泵当前流量值",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new EnumParameter("Unit", typeof(FlowRateUnit), FlowRateUnit.MLPerMin, "流量单位"),
                        new NumberParameter("AveragingTime", 1, 60, 5, "平均时间(秒)")
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("Status", "PENDING", "执行状态") { IsVisibleInVariableManager = false },
                        new NumberParameter("FlowRate",     0, 1000,   0, "当前流量",     true, UnitFlow),
                        new StringParameter("Unit", "mL/min", "流量单位") { IsVisibleInVariableManager = false },
                        new NumberParameter("TotalVolume",  0, 999999, 0, "累计流量",     true, UnitVolume),
                        new NumberParameter("CurrentSpeed", 0, 10000,  0, "当前转速",     true, UnitPumpSpeed),
                        new StringParameter("Timestamp", "", "采集时间") { IsVisibleInVariableManager = false }
                    }
                },
                Action = WrapAction("获取流量值", async delegate (DeviceParameters parameters)
                {
                    await Task.Delay(new Random().Next(100, 300));

                    var unit = parameters.GetValue<FlowRateUnit>("Unit");

                    double currentFlowRate;
                    double totalVolume;
                    double currentSpeed;

                    lock (_stateLock)
                    {
                        if (_isRunning)
                        {
                            // 简单的流量计算：转速 * 0.085 mL/min/RPM
                            _currentFlowRate = _currentSpeed * 0.085;

                            // 累计体积
                            var runTime = (DateTime.Now - _startTime).TotalMinutes;
                            _totalVolume = _currentFlowRate * runTime;
                        }
                        else
                        {
                            _currentFlowRate = 0;
                        }

                        currentFlowRate = _currentFlowRate;
                        totalVolume = _totalVolume;
                        currentSpeed = _currentSpeed;
                    }

                    // 单位转换
                    // 单位转换(仅供 Unit 字段显示给用户用,FlowRate 输出始终为 mL/min)
                    string unitStr = unit switch
                    {
                        FlowRateUnit.LPerMin => "L/min",
                        FlowRateUnit.MLPerSec => "mL/s",
                        _ => "mL/min"
                    };

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("Status", "OK", "") { IsVisibleInVariableManager = false },
                            new NumberParameter("FlowRate",     0, 1000,   Math.Round(currentFlowRate, 3), "当前流量",     true, UnitFlow),
                            new StringParameter("Unit", unitStr, "") { IsVisibleInVariableManager = false },
                            new NumberParameter("TotalVolume",  0, 999999, Math.Round(totalVolume, 2),     "累计流量",     true, UnitVolume),
                            new NumberParameter("CurrentSpeed", 0, 10000,  Math.Round(currentSpeed, 1),    "当前转速",     true, UnitPumpSpeed),
                            new StringParameter("Timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), "") { IsVisibleInVariableManager = false }
                        }
                    };
                })
            });

            // 3. 获取压力值命令
            Commands.Add(new DeviceCommand
            {
                Name = "获取压力值",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/pressure.png",
                HelpText = "获取蠕动泵当前压力值",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new EnumParameter("Unit", typeof(PressureUnit), PressureUnit.KPa, "压力单位"),
                        new BooleanParameter("RelativePressure", false, "是否为相对压力")
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("Status", "PENDING", "执行状态") { IsVisibleInVariableManager = false },
                        new NumberParameter("Pressure", -1, 10, 0, "当前压力", true, UnitPressure),
                        new StringParameter("Unit", "MPa", "压力单位") { IsVisibleInVariableManager = false },
                        new BooleanParameter("IsOverPressure", false, "是否超压") { IsVisibleInVariableManager = true },
                        new StringParameter("Timestamp", "", "采集时间") { IsVisibleInVariableManager = false }
                    }
                },
                Action = WrapAction("获取压力值", async delegate (DeviceParameters parameters)
                {
                    await Task.Delay(new Random().Next(100, 300));

                    var unit = parameters.GetValue<PressureUnit>("Unit");
                    bool isRelative = parameters.GetValue<bool>("RelativePressure");

                    // 简单的压力模拟
                    lock (_stateLock)
                    {
                        if (_isRunning)
                        {
                            var basePressure = isRelative ? 0 : 101.325;
                            var flowPressure = _currentFlowRate * 0.8; // 流量产生的压力
                            _currentPressure = basePressure + flowPressure + new Random().NextDouble() * 10 - 5; // ±5kPa噪声
                        }
                        else
                        {
                            _currentPressure = isRelative ? 0 : 101.325;
                        }
                    }

                    // 内部 _currentPressure 单位是 kPa,统一转换为 MPa 输出(与全项目压力单位对齐)
                    double pressureInMPa = _currentPressure / 1000.0;

                    bool isOverPressure = _currentPressure > 500;  // 判定阈值仍以内部 kPa 为准

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("Status", "OK", "") { IsVisibleInVariableManager = false },
                            new NumberParameter("Pressure", -1, 10, Math.Round(pressureInMPa, 4), "当前压力", true, UnitPressure),
                            new StringParameter("Unit", "MPa", "") { IsVisibleInVariableManager = false },
                            new BooleanParameter("IsOverPressure", isOverPressure, "") { IsVisibleInVariableManager = true },
                            new StringParameter("Timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), "") { IsVisibleInVariableManager = false }
                        }
                    };
                })
            });

            // 4. 开启命令 - 带状态跟踪
            Commands.Add(new DeviceCommand
            {
                Name = "开启",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/open.png",
                HelpText = "开启蠕动泵",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TargetSpeed", 0, 5000, 1000, "目标转速(RPM)"),
                        new NumberParameter("RampTime", 0, 60, 5, "加速时间(秒)"),
                        new EnumParameter("Direction", typeof(PumpDirection), PumpDirection.Forward, "泵送方向"),
                        new BooleanParameter("EnablePulseFree", false, "启用无脉动模式")
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("Status", "PENDING", "执行状态") { IsVisibleInVariableManager = false },
                        new BooleanParameter("IsRunning", false, "运行状态") { IsVisibleInVariableManager = true },
                        new NumberParameter("ActualSpeed", 0, 10000, 0, "实际转速", true, UnitPumpSpeed),
                        new StringParameter("Direction", "Forward", "运行方向") { IsVisibleInVariableManager = false },
                        new StringParameter("StartTime", "", "启动时间") { IsVisibleInVariableManager = false },
                        new StringParameter("Message", "", "状态信息") { IsVisibleInVariableManager = false }
                    }
                },
                Action = WrapAction("开启", async delegate (DeviceParameters parameters)
                {
                    double targetSpeed = parameters.GetValue<double>("TargetSpeed");
                    double rampTime = parameters.GetValue<double>("RampTime");
                    var direction = parameters.GetValue<PumpDirection>("Direction");
                    bool pulseFree = parameters.GetValue<bool>("EnablePulseFree");

                    // 模拟加速时间
                    int simulatedRampTime = Math.Min((int)(rampTime * 1000), 5000);
                    await Task.Delay(simulatedRampTime);

                    double actualSpeed;
                    lock (_stateLock)
                    {
                        _isRunning = true;
                        _currentSpeed = targetSpeed * (0.95 + new Random().NextDouble() * 0.1); // 95-105%效率
                        _startTime = DateTime.Now;
                        _totalVolume = 0; // 重置累计流量
                        actualSpeed = _currentSpeed;
                    }

                    // *** 关键：添加活跃状态跟踪 ***
                    var stateData = new PumpStateData
                    {
                        TargetSpeed = 500,
                        RampTime = 20.0,
                        Direction = direction,
                        EnablePulseFree = true
                    };

                    AddActiveState(
                        stateName: "PumpRunning",
                        activateCommand: "开启", //激活命令 - 如何启动这个状态
                        deactivateCommand: "关闭",  // 停止命令 - 如何完全停止这个状态
                        pauseCommand: "关闭",    // 蠕动泵暂停时直接关闭
                        resumeCommand: "开启",   // 恢复时重新开启
                        stateData: stateData
                    );
                    byte[] data = System.Text.Encoding.GetEncoding("GBK").GetBytes("开启指令");
                    _serialPort.Write(data, 0, data.Length);
                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("Status", "OK", "") { IsVisibleInVariableManager = false },
                            new BooleanParameter("IsRunning", true, "") { IsVisibleInVariableManager = true },
                            new NumberParameter("ActualSpeed", 0, 10000, Math.Round(actualSpeed, 1), "实际转速", true, UnitPumpSpeed),
                            new StringParameter("Direction", direction.ToString(), "") { IsVisibleInVariableManager = false },
                            new StringParameter("StartTime", _startTime.ToString("yyyy-MM-dd HH:mm:ss"), "") { IsVisibleInVariableManager = false },
                            new StringParameter("Message", $"泵已启动，{(pulseFree ? "无脉动模式" : "标准模式")}", "") { IsVisibleInVariableManager = false }
                        }
                    };
                })
            });

            // 5. 关闭命令 - 带状态跟踪
            Commands.Add(new DeviceCommand
            {
                Name = "关闭",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/off.png",
                HelpText = "关闭蠕动泵",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("QuickStop", false, "是否紧急停止"),
                        new NumberParameter("DecelerationTime", 0, 30, 3, "减速时间(秒)")
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("Status", "PENDING", "执行状态") { IsVisibleInVariableManager = false },
                        new BooleanParameter("IsRunning", true, "运行状态") { IsVisibleInVariableManager = true },
                        new StringParameter("StopTime", "", "停止时间") { IsVisibleInVariableManager = false },
                        new NumberParameter("TotalRunTime", 0, 999999, 0, "总运行时间", false, UnitTimeMin),
                        new NumberParameter("TotalVolume",  0, 999999, 0, "总泵送量",   true,  UnitVolume),
                        new StringParameter("Message", "", "状态信息") { IsVisibleInVariableManager = false }
                    }
                },
                Action = WrapAction("关闭", async delegate (DeviceParameters parameters)
                {
                    bool quickStop = parameters.GetValue<bool>("QuickStop");
                    double decelTime = parameters.GetValue<double>("DecelerationTime");

                    double totalRunTime = 0;
                    double totalVolume = 0;

                    lock (_stateLock)
                    {
                        if (_isRunning)
                        {
                            TimeSpan runTime = DateTime.Now - _startTime;
                            totalRunTime = runTime.TotalMinutes;
                            totalVolume = _totalVolume;
                        }
                    }

                    // 模拟减速时间
                    if (!quickStop)
                    {
                        int simulatedDecelTime = Math.Min((int)(decelTime * 1000), 3000);
                        await Task.Delay(simulatedDecelTime);
                    }
                    else
                    {
                        await Task.Delay(100);
                    }

                    lock (_stateLock)
                    {
                        _isRunning = false;
                        _currentSpeed = 0;
                        _currentFlowRate = 0;
                    }
                    byte[] data = System.Text.Encoding.GetEncoding("GBK").GetBytes("关闭指令");
                    _serialPort.Write(data, 0, data.Length);
                    // *** 关键：移除活跃状态跟踪 ***
                    RemoveActiveState("PumpRunning");

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("Status", "OK", "") { IsVisibleInVariableManager = false },
                            new BooleanParameter("IsRunning", false, "") { IsVisibleInVariableManager = true },
                            new StringParameter("StopTime", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "") { IsVisibleInVariableManager = false },
                            new NumberParameter("TotalRunTime", 0, 999999, Math.Round(totalRunTime, 2), "总运行时间", false, UnitTimeMin),
                            new NumberParameter("TotalVolume",  0, 999999, Math.Round(totalVolume, 2),  "总泵送量",   true,  UnitVolume),
                            new StringParameter("Message", quickStop ? "紧急停止完成" : "正常停止完成", "") { IsVisibleInVariableManager = false }
                        }
                    };
                })
            });
        }
        /// <summary>
        /// 发送命令并等待响应（线程安全版本）
        /// </summary>
        private async Task<string> SendCommandAndWaitResponseAsync(string command, int timeoutMs = 5000)
        {
            if (IsSimulationMode)
            {
                // 模拟模式下也要有一点延时，模拟真实通信
                await Task.Delay(new Random().Next(50, 200));
                return GenerateSimulatedResponse(command);
            }

            if (_serialPort?.IsOpen != true)
            {
                throw new InvalidOperationException("串口未连接");
            }

            // *** 关键：只对串口通信加锁，保证数据包完整性 ***
            await _serialPortSemaphore.WaitAsync(timeoutMs);

            try
            {
                DebugLog($"[串口通信] 开始发送命令: {command}");

                // 清空接收缓冲区，避免上次的残留数据
                lock (_serialLock)
                {
                    try
                    {
                        _serialPort.DiscardInBuffer();
                        _serialPort.DiscardOutBuffer();
                    }
                    catch (Exception ex)
                    {
                        WarnLog($"清空串口缓冲区失败: {ex.Message}");
                    }
                }

                // 短暂等待确保缓冲区清空
                await Task.Delay(50);

                // 发送命令
                await SendCommandAsync(command);

                // 等待响应
                var response = await WaitForResponseAsync(timeoutMs);

                DebugLog($"[串口通信] 命令响应完成: {command} -> {response}");
                return response;
            }
            finally
            {
                // *** 立即释放串口锁，允许其他命令使用串口 ***
                _serialPortSemaphore.Release();
                DebugLog($"[串口通信] 释放串口锁: {command}");
            }
        }
        /// <summary>
        /// 生成模拟响应
        /// </summary>
        private string GenerateSimulatedResponse(string command)
        {
            return command switch
            {
                "获取温度指令" => $"TEMP:{25.0 + new Random().NextDouble() * 10:F1}",
                "获取流量指令" => $"FLOW:{_currentFlowRate:F2}",
                "获取压力指令" => $"PRESSURE:{_currentPressure:F1}",
                "开启指令" => "START:OK",
                "关闭指令" => "STOP:OK",
                _ => "OK"
            };
        }
        /// <summary>
        /// 发送命令（不等待响应）
        /// </summary>
        private async Task SendCommandAsync(string command)
        {
            await Task.Run(() =>
            {
                lock (_serialLock)
                {
                    try
                    {
                        byte[] data = System.Text.Encoding.GetEncoding("GBK").GetBytes(command + "\r\n");
                        _serialPort.Write(data, 0, data.Length);
                        _serialPort.BaseStream.Flush();

                        DebugLog($"[串口发送] {command} ({data.Length} bytes)");
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"发送命令失败: {ex.Message}", ex);
                    }
                }
            });
        }

        /// <summary>
        /// 等待串口响应（改进版本）
        /// </summary>
        private async Task<string> WaitForResponseAsync(int timeoutMs)
        {
            var startTime = DateTime.Now;
            var response = new StringBuilder();
            var lastDataTime = DateTime.Now;
            const int NO_DATA_TIMEOUT = 500; // 500ms内没有新数据认为接收完成

            while ((DateTime.Now - startTime).TotalMilliseconds < timeoutMs)
            {
                try
                {
                    bool hasNewData = false;

                    lock (_serialLock)
                    {
                        if (_serialPort?.IsOpen == true && _serialPort.BytesToRead > 0)
                        {
                            string newData = _serialPort.ReadExisting();
                            response.Append(newData);
                            lastDataTime = DateTime.Now;
                            hasNewData = true;

                            DebugLog($"[串口接收] 数据片段: '{newData}' (累计长度: {response.Length})");
                        }
                    }

                    // 检查是否接收到完整响应
                    var responseStr = response.ToString();
                    if (IsCompleteResponse(responseStr))
                    {
                        var finalResponse = CleanupResponse(responseStr);
                        DebugLog($"[串口接收] 完整响应: '{finalResponse}'");
                        return finalResponse;
                    }

                    // 如果有数据但不完整，继续等待
                    if (hasNewData)
                    {
                        await Task.Delay(50);
                        continue;
                    }

                    // 如果一段时间没有新数据且有部分数据，可能接收完成
                    if (response.Length > 0 && (DateTime.Now - lastDataTime).TotalMilliseconds > NO_DATA_TIMEOUT)
                    {
                        var partialResponse = CleanupResponse(responseStr);
                        WarnLog($"[串口接收] 可能完成（超时）: '{partialResponse}'");
                        return partialResponse;
                    }

                    await Task.Delay(50);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"读取响应失败: {ex.Message}", ex);
                }
            }

            var timeoutResponse = response.ToString();
            throw new TimeoutException($"等待响应超时({timeoutMs}ms)，已接收: '{timeoutResponse}'");
        }

        /// <summary>
        /// 判断是否为完整响应
        /// </summary>
        private bool IsCompleteResponse(string response)
        {
            if (string.IsNullOrEmpty(response))
                return false;

            // 方案1：检查结束符
            if (response.Contains(RESPONSE_DELIMITER) || response.Contains("\n") || response.Contains("\r"))
            {
                return true;
            }

            // 方案2：检查特定格式的完整性
            if (response.Contains("TEMP:") && response.Length >= 8) // 例如 "TEMP:25.6"
            {
                return true;
            }

            // 方案3：纯数字格式，检查是否包含小数点和足够长度
            if (response.Length >= 4 &&
                System.Text.RegularExpressions.Regex.IsMatch(response, @"^\d+\.?\d*$"))
            {
                return true;
            }

            // 方案4：包含温度单位
            if (response.Contains("°C") || response.Contains("℃"))
            {
                return true;
            }

            // 方案5：长度限制，防止无限等待
            if (response.Length >= MAX_RESPONSE_LENGTH)
            {
                WarnLog($"响应长度达到最大限制({MAX_RESPONSE_LENGTH})，强制认为完整");
                return true;
            }

            return false;
        }

        /// <summary>
        /// 清理响应数据
        /// </summary>
        private string CleanupResponse(string response)
        {
            if (string.IsNullOrEmpty(response))
                return string.Empty;

            // 移除常见的控制字符和空白
            var cleaned = response
                .Replace("\r", "")
                .Replace("\n", "")
                .Trim();

            // 如果有多个响应粘连，尝试提取第一个有效响应
            if (cleaned.Contains("TEMP:"))
            {
                var tempIndex = cleaned.IndexOf("TEMP:");
                var tempMatch = System.Text.RegularExpressions.Regex.Match(
                    cleaned.Substring(tempIndex), @"TEMP:[\d\.\-]+");
                if (tempMatch.Success)
                {
                    return tempMatch.Value;
                }
            }

            return cleaned;
        }

        /// <summary>
        /// 从设备响应中解析温度值（改进版本）
        /// </summary>
        private double ParseTemperatureFromResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                throw new InvalidOperationException("设备响应为空");
            }

            try
            {
                response = response.Trim();
                InfoLog($"解析温度响应: '{response}'");

                // 处理粘包情况：如果包含多个响应，提取最后一个有效的
                var responses = SplitMultipleResponses(response); // 确保这里返回List<string>

                // *** 修正：确保 responses 是可枚举的 ***
                if (responses != null && responses.Any())
                {
                    // 从最后一个开始尝试
                    for (int i = responses.Count - 1; i >= 0; i--)
                    {
                        if (TryParseTemperature(responses[i], out double temperature))
                        {
                            InfoLog($"成功解析温度: {temperature}°C (来源: '{responses[i]}')");
                            return temperature;
                        }
                    }
                }

                throw new FormatException($"无法从任何响应中解析温度值: '{response}'");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"解析温度响应失败: {ex.Message}, 响应内容: '{response}'", ex);
            }
        }

        /// <summary>
        /// 分割可能粘连的多个响应
        /// </summary>
        private List<string> SplitMultipleResponses(string response)
        {
            var responses = new List<string>();

            // 方案1：按换行符分割
            var lines = response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            responses.AddRange(lines);

            // 方案2：按TEMP:分割
            if (response.Contains("TEMP:"))
            {
                var tempParts = response.Split(new[] { "TEMP:" }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 1; i < tempParts.Length; i++) // 跳过第一个空元素
                {
                    responses.Add("TEMP:" + tempParts[i]);
                }
            }

            // 如果没有分割出任何内容，返回原始响应
            if (!responses.Any())
            {
                responses.Add(response);
            }

            return responses.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        }

        /// <summary>
        /// 尝试解析单个温度值
        /// </summary>
        private bool TryParseTemperature(string singleResponse, out double temperature)
        {
            temperature = 0;

            if (string.IsNullOrWhiteSpace(singleResponse))
                return false;

            singleResponse = singleResponse.Trim();

            try
            {
                // 方案1: TEMP:25.6 格式
                if (singleResponse.StartsWith("TEMP:", StringComparison.OrdinalIgnoreCase))
                {
                    var tempStr = singleResponse.Substring(5).Trim();
                    // 提取数字部分
                    var match = System.Text.RegularExpressions.Regex.Match(tempStr, @"[-+]?\d*\.?\d+");
                    if (match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out temperature))
                    {
                        return true;
                    }
                }

                // 方案2: 25.6°C 或 25.6℃ 格式
                if (singleResponse.Contains("°C") || singleResponse.Contains("℃"))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(singleResponse, @"[-+]?\d*\.?\d+");
                    if (match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out temperature))
                    {
                        return true;
                    }
                }

                // 方案3: 纯数字格式
                var numberMatch = System.Text.RegularExpressions.Regex.Match(singleResponse, @"^[-+]?\d*\.?\d+$");
                if (numberMatch.Success && double.TryParse(numberMatch.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out temperature))
                {
                    return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }
        // 枚举定义
        public enum FlowRateUnit
        {
            MLPerMin,
            LPerMin,
            MLPerSec
        }

        public enum PressureUnit
        {
            KPa,
            Bar,
            PSI,
            MPa
        }

        public enum PumpDirection
        {
            Forward,
            Reverse
        }

        public override void Initialize()
        {
            InfoLog("开始初始化蠕动泵设备");

            try
            {
                base.Initialize();

                lock (_stateLock)
                {
                    _isRunning = false;
                    _currentSpeed = 0;
                    _currentFlowRate = 0;
                    _currentPressure = 0;
                    _currentTemperature = 25.0;
                    _totalVolume = 0;
                }

                // 清空状态跟踪
                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                InfoLog("蠕动泵设备初始化完成");
            }
            catch (Exception ex)
            {
                ErrorLog("设备初始化失败", ex);
                throw;
            }
        }

        public override void Shutdown()
        {
            InfoLog("开始关闭蠕动泵设备");

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
                        InfoLog("设备运行已停止");
                    }
                }

                // *** 释放串口信号量 ***
                _serialPortSemaphore?.Dispose();

                base.Shutdown();
                InfoLog("蠕动泵设备关闭完成");
            }
            catch (Exception ex)
            {
                ErrorLog("设备关闭失败", ex);
                throw;
            }
        }

        protected override async Task<bool> OnConnectAsync()
        {
            if (_serialPort != null)
            {
                _serialPort.Close();
                _serialPort.Dispose();
                _serialPort = null;
            }

            string ?comPort = Parameters.GetValue<string>("COM Port");

            if (IsSimulationMode)
            {
                InfoLog("以仿真模式连接");
                await Task.Delay(1000);
                return true;
            }

            try
            {
                if (string.IsNullOrEmpty(comPort))
                {
                    ErrorLog("未指定COM端口");
                    return false;
                }

                InfoLog($"正在连接到 {comPort}");

                _serialPort = new SerialPort(comPort, 9600)
                {
                    DataBits = 8,
                    Parity = Parity.None,
                    StopBits = StopBits.One,
                    ReadTimeout = 3000,
                    WriteTimeout = 3000,
                    Encoding = System.Text.Encoding.GetEncoding("GBK") // 设置编码
                };

                await Task.Run(() => _serialPort.Open());

                // 等待串口稳定
                await Task.Delay(100);
                return true;
                //if (await TestConnection())
                //{
                //    InfoLog($"成功连接到 {comPort}");
                //    return true;
                //}
                //else
                //{
                //    _serialPort?.Close();
                //    _serialPort?.Dispose();
                //    _serialPort = null;
                //    ErrorLog("设备连接测试失败");
                //    return false;
                //}
            }
            catch (Exception ex)
            {
                ErrorLog($"连接失败: {ex.Message}");
                _serialPort?.Close();
                _serialPort?.Dispose();
                _serialPort = null;
                return false;
            }
        }

        protected override async Task<bool> OnDisconnectAsync()
        {
            try
            {
                // 断开前停止所有活跃状态
                await StopAllActiveStatesAsync();

                lock (_stateLock)
                {
                    if (_isRunning)
                    {
                        _isRunning = false;
                        _currentSpeed = 0;
                        _currentFlowRate = 0;
                        InfoLog("设备已停止运行");
                    }
                }

                if (_serialPort != null && _serialPort.IsOpen)
                {
                    await Task.Run(() =>
                    {
                        _serialPort.Close();
                        _serialPort.Dispose();
                    });
                    InfoLog("串口连接已关闭");
                }

                InfoLog("设备连接已断开");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog($"断开连接时发生错误: {ex.Message}");
                return false;
            }
        }

        protected override async Task<bool> OnCheckConnectionAsync()
        {
            try
            {
                if (IsSimulationMode)
                {
                    await Task.Delay(50);
                    return new Random().NextDouble() > 0.05; // 95%概率正常
                }

                if (_serialPort == null || !_serialPort.IsOpen)
                {
                    return false;
                }

                return await TestConnection();
            }
            catch (Exception ex)
            {
                ErrorLog($"检查连接状态失败: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> TestConnection()
        {
            try
            {
                if (IsSimulationMode)
                {
                    await Task.Delay(100);
                    return true;
                }

                if (_serialPort == null || !_serialPort.IsOpen)
                    return false;

                // 发送测试命令
                try
                {
                    var testResponse = await SendCommandAndWaitResponseAsync("STATUS?", 2000);
                    InfoLog($"连接测试响应: {testResponse}");
                    return true;
                }
                catch (TimeoutException)
                {
                    WarnLog("连接测试超时，但串口已打开，可能设备不支持STATUS命令");
                    return true; // 串口能打开就认为连接成功
                }
                catch (Exception ex)
                {
                    ErrorLog($"连接测试失败: {ex.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                ErrorLog($"测试连接异常: {ex.Message}");
                return false;
            }
        }

        private Func<DeviceParameters, DeviceParameters> WrapAction(string commandName, Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return (DeviceParameters parameters) =>
            {
                var paramText = FormatParameters(parameters);
                _logger.LogInformation($"[设备命令-开始] Device={Name} Command={commandName} Params={paramText}");

                var sw = Stopwatch.StartNew();
                try
                {
                    // *** 关键：设备命令可以并行执行，锁只在串口通信时生效 ***
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();

                    // 检查输出参数中的 Status 字段
                    var statusParam = output?.Variables?.OfType<StringParameter>()
                        ?.FirstOrDefault(p => p.Name == "Status");

                    if (statusParam?.Value?.ToString() == "ERROR")
                    {
                        var messageParam = output?.Variables?.OfType<StringParameter>()
                            ?.FirstOrDefault(p => p.Name == "Message");
                        var errorMessage = messageParam?.Value?.ToString() ?? "设备命令执行失败";

                        _logger.LogError($"[设备命令-错误] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds} Error={errorMessage}");
                        throw new DeviceCommandExecutionException(Name, commandName, errorMessage);
                    }

                    var outputText = FormatParameters(output);
                    _logger.LogInformation($"[设备命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds} Output={outputText}");
                    return output;
                }
                catch (DeviceCommandExecutionException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[设备命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds} Params={paramText}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        private static string FormatParameters(DeviceParameters p)
        {
            if (p?.Variables == null || p.Variables.Count == 0) return "(none)";

            var parts = new List<string>(p.Variables.Count);
            foreach (var v in p.Variables)
            {
                string one = v switch
                {
                    NumberParameter np => $"{np.Name}={FormatNumber(np)}",
                    BooleanParameter bp => $"{bp.Name}={(bp.Value is bool b && b ? "true" : "false")}",
                    EnumParameter ep => $"{ep.Name}={ep.Value}",
                    StringParameter sp => $"{sp.Name}=\"{sp.Value}\"",
                    _ => $"{v.Name}={v.Value}"
                };
                parts.Add(one);
            }
            return string.Join(", ", parts);
        }

      
        private static string FormatNumber(NumberParameter np)
        {
            if (np.Value is double d)
                return d.ToString($"F{Math.Max(0, np.DecimalPlaces)}", CultureInfo.InvariantCulture);

            try
            {
                var dv = Convert.ToDouble(np.Value ?? 0, CultureInfo.InvariantCulture);
                return dv.ToString($"F{Math.Max(0, np.DecimalPlaces)}", CultureInfo.InvariantCulture);
            }
            catch
            {
                return np.Value?.ToString() ?? "0";
            }
        }
    }
}