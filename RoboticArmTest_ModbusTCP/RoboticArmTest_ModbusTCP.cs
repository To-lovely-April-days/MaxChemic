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

namespace RoboticArm_ModbusTCP
{
    [Export(typeof(IDevice))]
    public class RoboticArm_ModbusTCP : Device, IControllableDevice, IFlowLifecycleAware
    {
        private readonly ILogService _logger;
        private readonly object _stateLock = new object();

        // *** 状态跟踪相关字段 ***
        private readonly List<DeviceActiveState> _activeStates = new List<DeviceActiveState>();
        private readonly List<DeviceActiveState> _pausedStates = new List<DeviceActiveState>();
        private readonly object _statesLock = new object();

        // 机械手状态跟踪
        private readonly Dictionary<string, bool> _armStates = new Dictionary<string, bool>();

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
                    InfoLog("ModbusTCP机械手没有活跃状态需要暂停");
                    return true;
                }
                InfoLog($"暂停ModbusTCP机械手的 {_activeStates.Count} 个活跃状态");
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
                InfoLog(allSuccess ? "ModbusTCP机械手所有活跃状态已暂停" : "ModbusTCP机械手部分活跃状态暂停失败");
                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP机械手暂停活跃状态异常", ex);
                return false;
            }
        }

        public virtual async Task<bool> ResumeAllPausedStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_pausedStates.Any())
                {
                    InfoLog("ModbusTCP机械手没有暂停的状态需要恢复");
                    return true;
                }
                InfoLog($"恢复ModbusTCP机械手的 {_pausedStates.Count} 个暂停的状态");
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
                InfoLog(allSuccess ? "ModbusTCP机械手所有暂停状态已恢复" : "ModbusTCP机械手部分暂停状态恢复失败");
                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP机械手恢复暂停状态异常", ex);
                return false;
            }
        }

        public virtual async Task<bool> StopAllActiveStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_activeStates.Any() && !_pausedStates.Any())
                {
                    InfoLog("ModbusTCP机械手没有活跃或暂停的状态需要停止");
                    return true;
                }
                InfoLog($"停止ModbusTCP机械手的 {_activeStates.Count} 个活跃状态和 {_pausedStates.Count} 个暂停状态");
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

                InfoLog(allSuccess ? "ModbusTCP机械手所有状态已停止" : "ModbusTCP机械手部分状态停止失败");
                return allSuccess;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP机械手停止状态异常", ex);
                return false;
            }
        }

        #endregion

        #region IFlowLifecycleAware Implementation

        public virtual async Task<bool> OnFlowStartedAsync(FlowStartContext context)
        {
            try
            {
                InfoLog($"ModbusTCP机械手流程开始: {context.FlowName}");

                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                lock (_stateLock)
                {
                    _armStates.Clear();
                }

                InfoLog("ModbusTCP机械手设备已准备好执行流程");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP机械手流程开始处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowCompletedAsync(FlowCompletionContext context)
        {
            try
            {
                InfoLog($"ModbusTCP机械手流程完成: {context.FlowName}, 成功: {context.IsSuccessful}");
                InfoLog("ModbusTCP机械手流程完成处理结束");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP机械手流程完成处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowFailedAsync(FlowFailureContext context)
        {
            try
            {
                ErrorLog($"ModbusTCP机械手流程失败: {context.FlowName}, 错误: {context.ErrorMessage}");
                await StopAllActiveStatesAsync();
                InfoLog("ModbusTCP机械手流程失败处理完成");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP机械手流程失败处理异常", ex);
                return false;
            }
        }

        #endregion

        public RoboticArm_ModbusTCP()
        {
            _logger = LogManager.GetLogger<RoboticArm_ModbusTCP>();

            Name = "机械手";
            Manufacturer = "ModbusTCP机械手";
            Category = DeviceCategories.PublicWorks;
            ComId = "RA0101"; // 默认机械手ID

            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();

            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/RoboticArm.png";

            Parameters.Variables.Add(new StringParameter("DeviceId", "RA0101")
            {
                Options = new ObservableCollection<string>(new List<string>()
                {
                    "RA0101", "RA0102", "RA0201", "RA0202"
                }),
                HelpText = "ModbusTCP机械手设备ID"
            });

            Parameters.Variables.Add(new NumberParameter("MaxLoad", 0, 10000, 500, "最大负载(g)")
            {
                HelpText = "机械手的最大承载重量"
            });

            Parameters.Variables.Add(new NumberParameter("Speed", 1, 100, 50, "移动速度(%)")
            {
                HelpText = "机械手移动速度百分比"
            });

            InitializeCommands();
        }

        private void InitializeCommands()
        {
            // 搬运命令
            Commands.Add(new DeviceCommand
            {
                Name = "搬运",
                ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/RoboticArm.png",
                HelpText = "执行物料搬运操作",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("SourcePosition", "A1", "起始位置") { IsVisibleInVariableManager = true },
                        new StringParameter("TargetPosition", "B1", "目标位置") { IsVisibleInVariableManager = true },
                        new NumberParameter("TransferTime", 1, 60, 5, "搬运时间(秒)") { IsVisibleInVariableManager = true }
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("Success", false, "搬运成功") { IsVisibleInVariableManager = true },
                        new StringParameter("ArmId", "", "机械手ID") { IsVisibleInVariableManager = true },
                        new StringParameter("SourcePosition", "", "起始位置") { IsVisibleInVariableManager = true },
                        new StringParameter("TargetPosition", "", "目标位置") { IsVisibleInVariableManager = true },
                        new NumberParameter("ActualTime", 0, 0, 0, "实际耗时(秒)") { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("搬运", async delegate (DeviceParameters parameters)
                {
                    string armId = Parameters.GetValue<string>("DeviceId");
                    ComId = armId; // 设置通信ID为当前设备ID

                    string sourcePosition = parameters.GetValue<string>("SourcePosition");
                    string targetPosition = parameters.GetValue<string>("TargetPosition");
                    double transferTime = parameters.GetValue<double>("TransferTime");

                    bool success = false;
                    double actualTime = 0;

                    var stateName = $"Transfer_{armId}_{DateTime.Now.Ticks}";

                    try
                    {
                        // 添加活跃状态
                        AddActiveState(
                            stateName: stateName,
                            activateCommand: "搬运",
                            deactivateCommand: "停止搬运",
                            pauseCommand: "暂停搬运",
                            resumeCommand: "恢复搬运",
                            stateData: new { ArmId = armId, Source = sourcePosition, Target = targetPosition }
                        );

                        InfoLog($"开始搬运操作: {armId} 从 {sourcePosition} 到 {targetPosition}");

                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            // ModbusTCP模式 - 实际执行CPU密集型操作
                            var stopwatch = Stopwatch.StartNew();

                            await Task.Run(() =>
                            {
                                // CPU密集型计算
                                long iterations = (long)(transferTime * 100000000);
                                double result = 0;

                                for (long i = 0; i < iterations; i++)
                                {
                                    result += Math.Sqrt(i) * Math.Sin(i) * Math.Cos(i);

                                    if (i % 10000000 == 0)
                                    {
                                        Thread.Sleep(1);
                                    }
                                }

                                if (result == double.MaxValue)
                                {
                                    InfoLog("Calculation complete");
                                }
                            });

                            stopwatch.Stop();
                            actualTime = stopwatch.Elapsed.TotalSeconds;
                            success = true;

                            InfoLog($"ModbusTCP模式搬运完成: {armId}, 耗时: {actualTime:F2}秒");
                        }
                        else if (IsSimulationMode)
                        {
                            // 模拟模式 - 简单延时
                            var stopwatch = Stopwatch.StartNew();
                            await Task.Delay(TimeSpan.FromSeconds(transferTime));
                            stopwatch.Stop();

                            actualTime = stopwatch.Elapsed.TotalSeconds;
                            success = true;

                            lock (_stateLock)
                            {
                                _armStates[armId] = true;
                            }

                            InfoLog($"模拟模式搬运完成: {armId}, 耗时: {actualTime:F2}秒");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"搬运操作失败: {armId} - {ex.Message}", ex);
                        success = false;
                    }
                    finally
                    {
                        // 移除活跃状态
                        RemoveActiveState(stateName);
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new BooleanParameter("Success", success, "搬运成功") { IsVisibleInVariableManager = true },
                            new StringParameter("ArmId", armId, "机械手ID") { IsVisibleInVariableManager = true },
                            new StringParameter("SourcePosition", sourcePosition, "起始位置") { IsVisibleInVariableManager = true },
                            new StringParameter("TargetPosition", targetPosition, "目标位置") { IsVisibleInVariableManager = true },
                            new NumberParameter("ActualTime", 0, 0, actualTime, "实际耗时(秒)") { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });

            // 获取机械手状态命令
            Commands.Add(new DeviceCommand
            {
                Name = "获取机械手状态",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/status.png",
                HelpText = "获取当前机械手的状态",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("ArmId", "", "机械手ID") { IsVisibleInVariableManager = true },
                        new BooleanParameter("IsIdle", true, "是否空闲") { IsVisibleInVariableManager = true },
                        new NumberParameter("ActiveTaskCount", 0, 0, 0, "活跃任务数") { IsVisibleInVariableManager = true },
                        new BooleanParameter("Success", false, "读取成功") { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("获取机械手状态", async delegate (DeviceParameters parameters)
                {
                    string armId = Parameters.GetValue<string>("DeviceId");
                    ComId = armId;

                    bool success = false;
                    bool isIdle = true;
                    int activeTaskCount = 0;

                    try
                    {
                        lock (_statesLock)
                        {
                            activeTaskCount = _activeStates.Count;
                            isIdle = activeTaskCount == 0;
                        }

                        success = true;
                        InfoLog($"读取机械手 {armId} 状态: 空闲={isIdle}, 活跃任务={activeTaskCount}");
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"读取机械手状态失败: {armId} - {ex.Message}", ex);
                        success = false;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("ArmId", armId, "机械手ID") { IsVisibleInVariableManager = true },
                            new BooleanParameter("IsIdle", isIdle, "是否空闲") { IsVisibleInVariableManager = true },
                            new NumberParameter("ActiveTaskCount", 0, 0, activeTaskCount, "活跃任务数") { IsVisibleInVariableManager = true },
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
                InfoLog($"ModbusTCP机械手添加活跃状态: {stateName}");
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
                    InfoLog($"ModbusTCP机械手移除活跃状态: {stateName}");
                }

                var pausedState = _pausedStates.FirstOrDefault(s => s.StateName == stateName);
                if (pausedState != null)
                {
                    _pausedStates.Remove(pausedState);
                    InfoLog($"ModbusTCP机械手移除暂停状态: {stateName}");
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
                InfoLog($"暂停ModbusTCP机械手状态: {state.StateName}");

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
                ErrorLog($"ModbusTCP机械手暂停状态失败: {state.StateName}", ex);
                return false;
            }
        }

        private async Task<bool> ResumeStateAsync(DeviceActiveState state)
        {
            try
            {
                InfoLog($"恢复ModbusTCP机械手状态: {state.StateName}");

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
                ErrorLog($"ModbusTCP机械手恢复状态失败: {state.StateName}", ex);
                return false;
            }
        }

        private async Task<bool> StopStateAsync(DeviceActiveState state)
        {
            try
            {
                InfoLog($"停止ModbusTCP机械手状态: {state.StateName}");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog($"ModbusTCP机械手停止状态失败: {state.StateName}", ex);
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
                InfoLog($"ModbusTCP机械手执行命令: {commandName}");
            }
            else
            {
                WarnLog($"ModbusTCP机械手未找到命令: {commandName}");
            }
        }

        #endregion

        #region 基类重写方法

        public override void Initialize()
        {
            InfoLog("开始初始化ModbusTCP机械手设备");

            try
            {
                base.Initialize();

                lock (_stateLock)
                {
                    _armStates.Clear();
                }

                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                InfoLog("ModbusTCP机械手设备初始化完成");
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP机械手设备初始化失败", ex);
                throw;
            }
        }

        public override void Shutdown()
        {
            InfoLog("开始关闭ModbusTCP机械手设备");

            try
            {
                var stopTask = StopAllActiveStatesAsync();
                stopTask.Wait(TimeSpan.FromSeconds(5));

                InfoLog("ModbusTCP机械手设备关闭完成");
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP机械手设备关闭失败", ex);
                throw;
            }
        }

        protected override async Task<bool> OnConnectAsync()
        {
            InfoLog("ModbusTCP机械手连接");
            await Task.Delay(100);
            return true;
        }

        protected override async Task<bool> OnDisconnectAsync()
        {
            InfoLog("ModbusTCP机械手断开连接");
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

        #region 日志辅助方法

        private void InfoLog(string message)
        {
            _logger?.LogInformation($"[{ComId}] {message}");
        }

        private void ErrorLog(string message, Exception ex = null)
        {
            if (ex != null)
            {
                _logger?.LogError($"[{ComId}] {message}", ex);
            }
            else
            {
                _logger?.LogError($"[{ComId}] {message}");
            }
        }

        private void WarnLog(string message)
        {
            _logger?.LogWarning($"[{ComId}] {message}");
        }

        #endregion

        private Func<DeviceParameters, DeviceParameters> WrapAction(string commandName, Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return (DeviceParameters parameters) =>
            {
                _logger.LogInformation($"[ModbusTCP机械手命令-开始] Device={Name} Command={commandName}");

                var sw = Stopwatch.StartNew();
                try
                {
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();

                    _logger.LogInformation($"[ModbusTCP机械手命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[ModbusTCP机械手命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }
    }
}