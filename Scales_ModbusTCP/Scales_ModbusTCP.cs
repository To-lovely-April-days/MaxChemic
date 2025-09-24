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

namespace Scales_ModbusTCP
{
    [Export(typeof(IDevice))]
    public class Scales_ModbusTCP : Device, IControllableDevice, IFlowLifecycleAware
    {
        private readonly ILogService _logger;
        private readonly object _stateLock = new object();

        // *** 状态跟踪相关字段 ***
        private readonly List<DeviceActiveState> _activeStates = new List<DeviceActiveState>();
        private readonly List<DeviceActiveState> _pausedStates = new List<DeviceActiveState>();
        private readonly object _statesLock = new object();

        // 天平数据缓存
        private readonly Dictionary<string, double> _scaleData = new Dictionary<string, double>();

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
            // 天平是只读设备，暂停操作只是停止可能的连续读取
            lock (_statesLock)
            {
                if (!_activeStates.Any())
                {
                    InfoLog("ModbusTCP天平没有活跃状态需要暂停");
                    return true;
                }
                InfoLog($"暂停ModbusTCP天平的 {_activeStates.Count} 个活跃状态");

                foreach (var state in _activeStates.ToArray())
                {
                    _activeStates.Remove(state);
                    state.IsPaused = true;
                    _pausedStates.Add(state);
                }
            }

            InfoLog("ModbusTCP天平所有活跃状态已暂停");
            return true;
        }

        public virtual async Task<bool> ResumeAllPausedStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_pausedStates.Any())
                {
                    InfoLog("ModbusTCP天平没有暂停的状态需要恢复");
                    return true;
                }
                InfoLog($"恢复ModbusTCP天平的 {_pausedStates.Count} 个暂停的状态");

                foreach (var state in _pausedStates.ToArray())
                {
                    _pausedStates.Remove(state);
                    state.IsPaused = false;
                    state.ActivatedTime = DateTime.Now;
                    _activeStates.Add(state);
                }
            }

            InfoLog("ModbusTCP天平所有暂停状态已恢复");
            return true;
        }

        public virtual async Task<bool> StopAllActiveStatesAsync()
        {
            lock (_statesLock)
            {
                if (!_activeStates.Any() && !_pausedStates.Any())
                {
                    InfoLog("ModbusTCP天平没有活跃或暂停的状态需要停止");
                    return true;
                }
                InfoLog($"停止ModbusTCP天平的 {_activeStates.Count} 个活跃状态和 {_pausedStates.Count} 个暂停状态");

                _activeStates.Clear();
                _pausedStates.Clear();
            }

            InfoLog("ModbusTCP天平所有状态已停止");
            return true;
        }

        #endregion

        #region IFlowLifecycleAware Implementation

        public virtual async Task<bool> OnFlowStartedAsync(FlowStartContext context)
        {
            try
            {
                InfoLog($"ModbusTCP天平流程开始: {context.FlowName}");

                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                lock (_stateLock)
                {
                    _scaleData.Clear();
                }

                InfoLog("ModbusTCP天平设备已准备好执行流程");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP天平流程开始处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowCompletedAsync(FlowCompletionContext context)
        {
            try
            {
                InfoLog($"ModbusTCP天平流程完成: {context.FlowName}, 成功: {context.IsSuccessful}");
                await StopAllActiveStatesAsync();
                InfoLog("ModbusTCP天平流程完成处理结束");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP天平流程完成处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowFailedAsync(FlowFailureContext context)
        {
            try
            {
                ErrorLog($"ModbusTCP天平流程失败: {context.FlowName}, 错误: {context.ErrorMessage}");
                await StopAllActiveStatesAsync();
                InfoLog("ModbusTCP天平流程失败处理完成");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP天平流程失败处理异常", ex);
                return false;
            }
        }

        #endregion

        public Scales_ModbusTCP()
        {
            _logger = LogManager.GetLogger<Scales_ModbusTCP>();

            Name = "天平";
            Manufacturer = "ModbusTCP天平";
            Category = DeviceCategories.Sensors;
            ComId = "S0101"; // 默认天平ID

            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();

            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/Scale.png";

            Parameters.Variables.Add(new StringParameter("DeviceId", "S0101")
            {
                Options = new ObservableCollection<string>(new List<string>()
                {
                    "S0101", "S0111", "S0121"
                }),
                HelpText = "ModbusTCP天平设备ID"
            });

            InitializeCommands();
        }

        private void InitializeCommands()
        {
            // 1. 获取天平称重命令 - 只读取当前设备
            Commands.Add(new DeviceCommand
            {
                Name = "获取天平称重",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/weight.png",
                HelpText = "读取当前天平的称重值",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
            {
                new StringParameter("ScaleId", "", "天平ID") { IsVisibleInVariableManager = true },
                new NumberParameter("Weight", 0, 10000, 0, "称重值(g)") { IsVisibleInVariableManager = true },
                new BooleanParameter("Success", false, "读取成功") { IsVisibleInVariableManager = true }
            }
                },
                Action = WrapAction("获取天平称重", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    string scaleId = ComId; // 使用设备自身的ID
                    double weight = 0;
                    bool success = false;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            weight = await ConnectionManager.ReadAsync<double>(ComId, $"_{scaleId}_g");
                            InfoLog($"读取天平 {scaleId} 称重值: {weight}g");
                            success = true;
                        }
                        else if (IsSimulationMode)
                        {
                            weight = 10 + new Random().NextDouble() * 1000;
                            lock (_stateLock)
                            {
                                _scaleData[$"{scaleId}_weight"] = weight;
                            }
                            success = true;
                            InfoLog($"模拟模式读取天平 {scaleId} 称重值: {weight:F2}g");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        if (success)
                        {
                            AddActiveState(
                                stateName: $"Reading_{scaleId}",
                                activateCommand: "获取天平称重",
                                deactivateCommand: "停止读取",
                                stateData: new { ScaleId = scaleId, LastWeight = weight, ReadTime = DateTime.Now }
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"读取天平称重失败: {scaleId} - {ex.Message}");
                        success = false;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                {
                    new StringParameter("ScaleId", scaleId, "天平ID") { IsVisibleInVariableManager = true },
                    new NumberParameter("Weight", 0, 10000, Math.Round(weight, 3), "称重值(g)") { IsVisibleInVariableManager = true },
                    new BooleanParameter("Success", success, "读取成功") { IsVisibleInVariableManager = true }
                }
                    };
                })
            });

            // 2. 获取天平累计值命令 - 只读取当前设备
            Commands.Add(new DeviceCommand
            {
                Name = "获取天平累计",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/cumulative.png",
                HelpText = "读取当前天平的累计值",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
            {
                new StringParameter("ScaleId", "", "天平ID") { IsVisibleInVariableManager = true },
                new NumberParameter("CumulativeWeight", 0, 100000, 0, "累计值(g)") { IsVisibleInVariableManager = true },
                new BooleanParameter("Success", false, "读取成功") { IsVisibleInVariableManager = true }
            }
                },
                Action = WrapAction("获取天平累计", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    string scaleId = ComId; // 使用设备自身的ID
                    double cumulativeWeight = 0;
                    bool success = false;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            cumulativeWeight = await ConnectionManager.ReadAsync<double>(ComId, $"_{scaleId}_Add_g");
                            InfoLog($"读取天平 {scaleId} 累计值: {cumulativeWeight}g");
                            success = true;
                        }
                        else if (IsSimulationMode)
                        {
                            lock (_stateLock)
                            {
                                string key = $"{scaleId}_cumulative";
                                if (_scaleData.ContainsKey(key))
                                {
                                    _scaleData[key] += new Random().NextDouble() * 10;
                                }
                                else
                                {
                                    _scaleData[key] = 100 + new Random().NextDouble() * 5000;
                                }
                                cumulativeWeight = _scaleData[key];
                            }
                            success = true;
                            InfoLog($"模拟模式读取天平 {scaleId} 累计值: {cumulativeWeight:F2}g");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        if (success)
                        {
                            AddActiveState(
                                stateName: $"ReadingCumulative_{scaleId}",
                                activateCommand: "获取天平累计",
                                deactivateCommand: "停止读取",
                                stateData: new { ScaleId = scaleId, LastCumulative = cumulativeWeight, ReadTime = DateTime.Now }
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"读取天平累计值失败: {scaleId} - {ex.Message}");
                        success = false;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                {
                    new StringParameter("ScaleId", scaleId, "天平ID") { IsVisibleInVariableManager = true },
                    new NumberParameter("CumulativeWeight", 0, 100000, Math.Round(cumulativeWeight, 3), "累计值(g)") { IsVisibleInVariableManager = true },
                    new BooleanParameter("Success", success, "读取成功") { IsVisibleInVariableManager = true }
                }
                    };
                })
            });

            // 3. 获取天平全部数据命令 - 只读取当前设备
            Commands.Add(new DeviceCommand
            {
                Name = "天平全部数据",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/scale_all.png",
                HelpText = "一次性读取当前天平的称重值和累计值",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
            {
                new StringParameter("ScaleId", "", "天平ID") { IsVisibleInVariableManager = true },
                new NumberParameter("Weight", 0, 10000, 0, "称重值(g)") { IsVisibleInVariableManager = true },
                new NumberParameter("CumulativeWeight", 0, 100000, 0, "累计值(g)") { IsVisibleInVariableManager = true },
                new BooleanParameter("Success", false, "读取成功") { IsVisibleInVariableManager = true }
            }
                },
                Action = WrapAction("天平全部数据", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    string scaleId = ComId; // 使用设备自身的ID
                    double weight = 0;
                    double cumulativeWeight = 0;
                    bool success = false;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            // 并行读取两个数据
                            var weightTask = ConnectionManager.ReadAsync<double>(ComId, $"_{scaleId}_g");
                            var cumulativeTask = ConnectionManager.ReadAsync<double>(ComId, $"_{scaleId}_Add_g");

                            await Task.WhenAll(weightTask, cumulativeTask);

                            weight = await weightTask;
                            cumulativeWeight = await cumulativeTask;

                            InfoLog($"读取天平 {scaleId} 全部数据: 称重={weight}g, 累计={cumulativeWeight}g");
                            success = true;
                        }
                        else if (IsSimulationMode)
                        {
                            weight = 10 + new Random().NextDouble() * 1000;

                            lock (_stateLock)
                            {
                                string cumulativeKey = $"{scaleId}_cumulative";
                                if (_scaleData.ContainsKey(cumulativeKey))
                                {
                                    _scaleData[cumulativeKey] += weight * 0.1;
                                }
                                else
                                {
                                    _scaleData[cumulativeKey] = 100 + new Random().NextDouble() * 5000;
                                }
                                cumulativeWeight = _scaleData[cumulativeKey];
                            }

                            success = true;
                            InfoLog($"模拟模式读取天平 {scaleId} 全部数据: 称重={weight:F2}g, 累计={cumulativeWeight:F2}g");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        if (success)
                        {
                            AddActiveState(
                                stateName: $"ReadingAll_{scaleId}",
                                activateCommand: "获取天平全部数据",
                                deactivateCommand: "停止读取",
                                stateData: new
                                {
                                    ScaleId = scaleId,
                                    LastWeight = weight,
                                    LastCumulative = cumulativeWeight,
                                    ReadTime = DateTime.Now
                                }
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"读取天平全部数据失败: {scaleId} - {ex.Message}");
                        success = false;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                {
                    new StringParameter("ScaleId", scaleId, "天平ID") { IsVisibleInVariableManager = true },
                    new NumberParameter("Weight", 0, 10000, Math.Round(weight, 3), "称重值(g)") { IsVisibleInVariableManager = true },
                    new NumberParameter("CumulativeWeight", 0, 100000, Math.Round(cumulativeWeight, 3), "累计值(g)") { IsVisibleInVariableManager = true },
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
                InfoLog($"ModbusTCP天平添加活跃状态: {stateName}");
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
                    InfoLog($"ModbusTCP天平移除活跃状态: {stateName}");
                }

                var pausedState = _pausedStates.FirstOrDefault(s => s.StateName == stateName);
                if (pausedState != null)
                {
                    _pausedStates.Remove(pausedState);
                    InfoLog($"ModbusTCP天平移除暂停状态: {stateName}");
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
            InfoLog("开始初始化ModbusTCP天平设备");

            try
            {
                base.Initialize();

                lock (_stateLock)
                {
                    _scaleData.Clear();
                }

                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                InfoLog("ModbusTCP天平设备初始化完成");
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP天平设备初始化失败", ex);
                throw;
            }
        }

        public override void Shutdown()
        {
            InfoLog("开始关闭ModbusTCP天平设备");

            try
            {
                var stopTask = StopAllActiveStatesAsync();
                stopTask.Wait(TimeSpan.FromSeconds(5));

                InfoLog("ModbusTCP天平设备关闭完成");
            }
            catch (Exception ex)
            {
                ErrorLog("ModbusTCP天平设备关闭失败", ex);
                throw;
            }
        }

        protected override async Task<bool> OnConnectAsync()
        {
            InfoLog("ModbusTCP天平连接");
            await Task.Delay(100);
            return true;
        }

        protected override async Task<bool> OnDisconnectAsync()
        {
            InfoLog("ModbusTCP天平断开连接");
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
                _logger.LogInformation($"[ModbusTCP天平命令-开始] Device={Name} Command={commandName}");

                var sw = Stopwatch.StartNew();
                try
                {
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();

                    _logger.LogInformation($"[ModbusTCP天平命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[ModbusTCP天平命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }
    }
}