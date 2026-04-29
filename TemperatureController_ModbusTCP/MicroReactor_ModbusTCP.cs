using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;

namespace MicroReactor_ModbusTCP
{
    [Export(typeof(IDevice))]
    public class MicroReactor_ModbusTCP : Device, IControllableDevice, IFlowLifecycleAware, IDeviceUIInteraction
    {
        private readonly ILogService _logger;
        private readonly object _stateLock = new object();

        // *** 独立的温度控制日志记录器 ***
        private readonly SmartControlLogger _controlLogger;

        // *** 状态跟踪相关字段 ***
        private readonly List<DeviceActiveState> _activeStates = new List<DeviceActiveState>();
        private readonly List<DeviceActiveState> _pausedStates = new List<DeviceActiveState>();
        private readonly object _statesLock = new object();

        // 微反应器设备数据缓存 - 使用线程安全集合
        private readonly ConcurrentDictionary<string, double> _deviceData = new ConcurrentDictionary<string, double>();
        // ═══════════════════════════════════════════════════════
        //  单位常量集中定义,与监控图表的轴分类对齐
        //   - ℃    → "温度轴" (T)
        //   - MPa  → "压力轴" (P)
        //  注:本设备的全部数值参数都是温度或压力,完美适配多轴图表
        // ═══════════════════════════════════════════════════════
        private const string UnitTemperature = "℃";
        private const string UnitPressure = "MPa";
        // *** 智能温度控制相关字段 ***
        private readonly ConcurrentDictionary<string, IntelligentTemperatureController> _temperatureControllers = new ConcurrentDictionary<string, IntelligentTemperatureController>();

        // 系统稳定性状态 - 使用线程安全访问
        private volatile SystemStabilityStatus _stabilityStatus = new SystemStabilityStatus();
        // ============ 新增：IDeviceUIInteraction 所需字段 ============

        /// <summary>
        /// 画布 GUID，由 ProcessDesignerView 通过反射注入
        /// </summary>
        public string DeviceId { get; set; }

        /// <summary>
        /// IDeviceUIInteraction 核心事件
        /// </summary>
        public event EventHandler<DeviceControlStateChangedEventArgs> ControlStateChanged;

        // 温度轮询相关字段
        private CancellationTokenSource _temperaturePollingCts;
        private Task _temperaturePollingTask;
        private static readonly TimeSpan TemperaturePollingInterval = TimeSpan.FromSeconds(2);

        // ============================================================
        // IDeviceUIInteraction 接口实现（与蠕动泵保持一致的风格）
        // ============================================================

        /// <summary>
        /// 触发状态变化事件（与蠕动泵完全一致的辅助方法）
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
        /// 执行UI命令（微反应器暂不支持从UI直接触发命令）
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
        /// 获取当前设备状态（与蠕动泵的 override 风格一致）
        /// </summary>
        public override DeviceState GetCurrentState()
        {
            var deviceId = ComId;
            var states = new Dictionary<string, object>();

            if (_deviceData.TryGetValue($"{deviceId}_TT1001_Actual", out double t1))
                states["TT1001_ActualTemp"] = t1;
            if (_deviceData.TryGetValue($"{deviceId}_TT1002_Actual", out double t2))
                states["TT1002_ActualTemp"] = t2;
            if (_deviceData.TryGetValue($"{deviceId}_TT1003_Actual", out double t3))
                states["TT1003_ActualTemp"] = t3;

            if (_stabilityStatus != null)
            {
                states["TT1001_Stable"] = _stabilityStatus.TT1001_Stable;
                states["TT1002_Stable"] = _stabilityStatus.TT1002_Stable;
                states["TT1003_Stable"] = _stabilityStatus.TT1003_Stable;
                states["SystemStable"] = _stabilityStatus.IsSystemStable;
            }

            return new DeviceState { States = states };
        }
        // ============================================================
        // 温度轮询（与蠕动泵的状态轮询风格完全一致）
        // ============================================================

        /// <summary>
        /// 启动温度轮询（对应蠕动泵的 StartStatusPolling）
        /// </summary>
        private void StartTemperaturePolling()
        {
            try
            {
                // 如果已经在轮询，先停止
                if (_temperaturePollingTask != null && !_temperaturePollingTask.IsCompleted)
                {
                    InfoLog("检测到已有温度轮询任务，先停止");
                    StopTemperaturePollingAsync().Wait(TimeSpan.FromSeconds(2));
                }

                _temperaturePollingCts = new CancellationTokenSource();

                _temperaturePollingTask = Task.Run(async () =>
                {
                    await TemperaturePollingLoopAsync(_temperaturePollingCts.Token);
                }, _temperaturePollingCts.Token);

                InfoLog("温度轮询已启动");
            }
            catch (Exception ex)
            {
                ErrorLog("启动温度轮询失败", ex);
            }
        }

        /// <summary>
        /// 停止温度轮询（对应蠕动泵的 StopStatusPollingAsync）
        /// </summary>
        private async Task StopTemperaturePollingAsync()
        {
            try
            {
                if (_temperaturePollingCts != null)
                {
                    InfoLog("正在停止温度轮询...");
                    _temperaturePollingCts.Cancel();

                    if (_temperaturePollingTask != null)
                    {
                        await Task.WhenAny(_temperaturePollingTask, Task.Delay(2000));
                    }

                    _temperaturePollingCts.Dispose();
                    _temperaturePollingCts = null;
                    _temperaturePollingTask = null;

                    InfoLog("温度轮询已停止");
                }
            }
            catch (Exception ex)
            {
                ErrorLog("停止温度轮询失败", ex);
            }
        }

        /// <summary>
        /// 温度轮询主循环（对应蠕动泵的 StatusPollingLoopAsync）
        /// </summary>
        private async Task TemperaturePollingLoopAsync(CancellationToken cancellationToken)
        {
            InfoLog("温度轮询线程已启动");

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        await PollAndPublishTemperaturesAsync(cancellationToken);

                        await Task.Delay((int)TemperaturePollingInterval.TotalMilliseconds, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        InfoLog("温度轮询被取消");
                        break;
                    }
                    catch (Exception ex)
                    {
                        // 单次失败不中断整个循环
                        ErrorLog($"温度轮询异常: {ex.Message}");

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
                ErrorLog("温度轮询线程异常退出", ex);
            }
            finally
            {
                InfoLog("温度轮询线程已退出");
            }
        }

        /// <summary>
        /// 单次轮询：读取三个温度并通过事件推送到UI
        /// （对应蠕动泵的 PollPumpRunningStateAsync）
        /// </summary>
        private async Task PollAndPublishTemperaturesAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested) return;

            ComId = Parameters.GetValue<string>("DeviceId");
            string deviceId = ComId;

            double tt1001 = 0, tt1002 = 0, tt1003 = 0;

            if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp
                && ConnectionManager != null && !IsSimulationMode)
            {
                var t1Task = ConnectionManager.ReadAsync<double>(deviceId, "TT1001_℃");
                var t2Task = ConnectionManager.ReadAsync<double>(deviceId, "TT1002_℃");
                var t3Task = ConnectionManager.ReadAsync<double>(deviceId, "TT1003_℃");

                await Task.WhenAll(t1Task, t2Task, t3Task);

                tt1001 = await t1Task;
                tt1002 = await t2Task;
                tt1003 = await t3Task;
            }
            else if (IsSimulationMode)
            {
                var random = new Random();
                tt1001 = GetSimulatedTemperature(deviceId, "TT1001", random);
                tt1002 = GetSimulatedTemperature(deviceId, "TT1002", random);
                tt1003 = GetSimulatedTemperature(deviceId, "TT1003", random);
            }
            else
            {
                return;
            }

            // 缓存实际温度
            _deviceData[$"{deviceId}_TT1001_Actual"] = tt1001;
            _deviceData[$"{deviceId}_TT1002_Actual"] = tt1002;
            _deviceData[$"{deviceId}_TT1003_Actual"] = tt1003;

            // 通过事件推送到UI（与蠕动泵的 RaiseControlStateChanged 完全一致）
            RaiseControlStateChanged("TT1001_ActualTemp", tt1001);
            RaiseControlStateChanged("TT1002_ActualTemp", tt1002);
            RaiseControlStateChanged("TT1003_ActualTemp", tt1003);
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

            // 暂停所有温度控制器
            var pauseTasks = _temperatureControllers.Values.Select(controller => controller.PauseAsync());
            await Task.WhenAll(pauseTasks);

            _controlLogger.LogInfo("所有智能温度控制器已暂停");
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

            // 恢复所有温度控制器
            var resumeTasks = _temperatureControllers.Values.Select(controller => controller.ResumeAsync());
            await Task.WhenAll(resumeTasks);

            _controlLogger.LogInfo("所有智能温度控制器已恢复");
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
                    // 即使没有活跃状态，也确保停止轮询
                    StopTemperaturePollingAsync().Wait(TimeSpan.FromSeconds(2));
                    return true;
                }
                InfoLog($"停止 {_activeStates.Count} 个活跃状态和 {_pausedStates.Count} 个暂停状态");
                _activeStates.Clear();
                _pausedStates.Clear();
            }

            //  停止温度轮询
            await StopTemperaturePollingAsync();

            var stopTasks = _temperatureControllers.Values.Select(c => c.StopAsync());
            await Task.WhenAll(stopTasks);
            _temperatureControllers.Clear();

            _controlLogger.LogInfo("所有智能温度控制器已停止");
            _controlLogger.Close();
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

                _deviceData.Clear();
                _temperatureControllers.Clear();
                _stabilityStatus = new SystemStabilityStatus();
                _controlLogger.Initialize(context.FlowName);

                //  启动温度轮询（与蠕动泵的 StartStatusPolling 对应）
                StartTemperaturePolling();

                InfoLog("ModbusTCP微反应器设备已准备好执行流程，温度实时轮询已启动");
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

                //  停止温度轮询
                await StopTemperaturePollingAsync();

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

                //  停止温度轮询
                await StopTemperaturePollingAsync();

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
            _controlLogger = new SmartControlLogger();

            Name = "微反应器";
            Manufacturer = "ModbusTCP微反应器";
            Category = DeviceCategories.Reactors;
            ComId = "R0101";

            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();

            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            //ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/MicroReactor.png";
            ImageLocation = "CUSTOM_CONTROL:MicroreactorControl";
            Parameters.Variables.Add(new StringParameter("DeviceId", "R0101")
            {
                Options = new ObservableCollection<string>(new List<string>()
                {
                    "R0101","R0201"
                }),
                HelpText = "ModbusTCP微反应器设备ID"
            });
            Parameters.Variables.Add(new StringParameter("CirculatorDeviceId", "TC0101")
            {
                Options = new ObservableCollection<string>(new List<string>()
                 {
                     "TC0101", "TC0201"
                 }),
                HelpText = "选择用于温度控制的高低温循环器设备ID"
            });

            InitializeCommands();
        }

        private void InitializeCommands()
        {
            // 1. 智能设置温度命令
            Commands.Add(new DeviceCommand
            {
                Name = "智能设置温度",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/temp_set.png",
                HelpText = "使用智能算法设置微反应器温度：98%快速接近 → 稳定性检测 → 精确微调",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TT1001_Temperature", -100, 500, 25, "TT1001目标温度(℃)")
                        {
                            HelpText = "TT1001温度传感器的目标温度值，系统将智能控制使其稳定在此温度"
                        },
                        new NumberParameter("TT1002_Temperature", -100, 500, 25, "TT1002目标温度(℃)")
                        {
                            HelpText = "TT1002温度传感器的目标温度值，系统将智能控制使其稳定在此温度"
                        },
                        new NumberParameter("TT1003_Temperature", -100, 500, 25, "TT1003目标温度(℃)")
                        {
                            HelpText = "TT1003温度传感器的目标温度值，系统将智能控制使其稳定在此温度"
                        },
                        new NumberParameter("ReadingInterval", 2, 10, 3, "读数间隔(秒)")
                        {
                            HelpText = "温度读取的时间间隔，建议2-5秒"
                        },
                        new NumberParameter("TemperatureTolerance", 0.1, 3.0, 0.5, "温度容差(℃)")
                        {
                            HelpText = "温度控制的允许偏差范围，±容差值内认为温度已达标"
                        },
                        new NumberParameter("StabilityTime", 10, 60, 20, "稳定检测时间(秒)")
                        {
                            HelpText = "判断温度稳定所需的最少时间，建议15-30秒"
                        },
                        new NumberParameter("FineAdjustStep", 0.1, 2.0, 0.5, "微调步长(℃)")
                        {
                            HelpText = "精确调节时的温度调整步长，建议0.2-1.0℃"
                        }
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("DeviceId", "", "设备ID") { IsVisibleInVariableManager = true },
                        new StringParameter("CirculatorId", "", "循环器ID") { IsVisibleInVariableManager = true },
                        new BooleanParameter("TT1001_ControlStarted", false, "TT1001智能控制启动") { IsVisibleInVariableManager = true },
                        new BooleanParameter("TT1002_ControlStarted", false, "TT1002智能控制启动") { IsVisibleInVariableManager = true },
                        new BooleanParameter("TT1003_ControlStarted", false, "TT1003智能控制启动") { IsVisibleInVariableManager = true },
                        new NumberParameter("TT1001_TargetTemp", -100, 500, 0, "TT1001目标温度", true, UnitTemperature),
                        new NumberParameter("TT1002_TargetTemp", -100, 500, 0, "TT1002目标温度", true, UnitTemperature),
                        new NumberParameter("TT1003_TargetTemp", -100, 500, 0, "TT1003目标温度", true, UnitTemperature),
                        new BooleanParameter("Success", false, "智能温度控制启动成功") { IsVisibleInVariableManager = true }
                    }
                },

                Action = WrapAction("智能设置温度", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    string deviceId = ComId;
                    string circulatorId = Parameters.GetValue<string>("CirculatorDeviceId");

                    var tt1001Temp = parameters.GetNullableValue<double>("TT1001_Temperature");
                    var tt1002Temp = parameters.GetNullableValue<double>("TT1002_Temperature");
                    var tt1003Temp = parameters.GetNullableValue<double>("TT1003_Temperature");

                    int readingInterval = (int)parameters.GetValue<double>("ReadingInterval");
                    double temperatureTolerance = parameters.GetValue<double>("TemperatureTolerance");
                    int stabilityTime = (int)parameters.GetValue<double>("StabilityTime");
                    double fineAdjustStep = parameters.GetValue<double>("FineAdjustStep");

                    bool tt1001Started = false, tt1002Started = false, tt1003Started = false;
                    double tt1001Target = 0, tt1002Target = 0, tt1003Target = 0;
                    bool overallSuccess = false;

                    try
                    {
                        InfoLog($"启动微反应器智能温度控制系统: {deviceId}, 循环器: {circulatorId}");
                        _controlLogger.LogControlStart(deviceId, circulatorId);

                        // 停止现有的控制器（如果有的话）
                        var existingControllers = _temperatureControllers.Keys
                            .Where(k => k.StartsWith(deviceId + "_"))
                            .ToList();

                        foreach (var key in existingControllers)
                        {
                            if (_temperatureControllers.TryRemove(key, out var existingController))
                            {
                                await existingController.StopAsync();
                                InfoLog($"停止现有智能温度控制器: {key}");
                                _controlLogger.LogInfo($"停止现有控制器: {key}");
                            }
                        }

                        // 🔧 按顺序启动循环器功能
                        InfoLog("开始按顺序启动循环器功能...");
                        _controlLogger.LogInfo("开始按顺序启动循环器功能");

                        await EnsureCirculatorCycleRunning(circulatorId);
                        await Task.Delay(3000);

                        if (!IsSimulationMode)
                        {
                            string heatCommand = GetCirculatorHeatingCommand(circulatorId);
                            await ConnectionManager.WriteAsync(circulatorId, heatCommand, true);
                            InfoLog($"启动循环器{circulatorId}加热功能: {heatCommand} = true");
                            await Task.Delay(500);

                            string coolingCommand = GetCirculatorCoolingCommand(circulatorId);
                            await ConnectionManager.WriteAsync(circulatorId, coolingCommand, true);
                            InfoLog($"启动循环器{circulatorId}制冷功能: {coolingCommand} = true");
                            await Task.Delay(500);
                        }

                        InfoLog($"循环器{circulatorId}全部功能已启动: 循环+加热+制冷");
                        _controlLogger.LogSuccess($"循环器{circulatorId}全部功能已启动: 循环+加热+制冷");

                        // 启动TT1001智能控制
                        if (tt1001Temp.HasValue && Math.Abs(tt1001Temp.Value) > 0.1)
                        {
                            tt1001Target = tt1001Temp.Value;
                            string controllerKey = $"{deviceId}_TT1001";

                            var controllerConfig = new IntelligentControllerConfig
                            {
                                TargetTemperature = tt1001Target,
                                Tolerance = temperatureTolerance,
                                ReadingInterval = TimeSpan.FromSeconds(readingInterval),
                                StabilityTime = TimeSpan.FromSeconds(stabilityTime),
                                FineAdjustStep = fineAdjustStep,
                                ApproachPercentage = 0.98
                            };

                            var controller = new IntelligentTemperatureController(
                                deviceId: deviceId,
                                sensorName: "TT1001",
                                config: controllerConfig,
                                circulatorDeviceId: circulatorId,
                                connectionManager: ConnectionManager,
                                logger: _logger,
                                controlLogger: _controlLogger,
                                isSimulationMode: IsSimulationMode,
                                deviceData: _deviceData,
                                stabilityStatus: _stabilityStatus
                            );

                            _temperatureControllers[controllerKey] = controller;
                            await controller.StartAsync();
                            tt1001Started = true;

                            InfoLog($"启动TT1001智能温度控制: 目标={tt1001Target}℃, 容差=±{temperatureTolerance}℃");
                            _controlLogger.LogControllerStart("TT1001", tt1001Target, temperatureTolerance, readingInterval);
                        }

                        // 启动TT1002智能控制
                        if (tt1002Temp.HasValue && Math.Abs(tt1002Temp.Value) > 0.1)
                        {
                            tt1002Target = tt1002Temp.Value;
                            string controllerKey = $"{deviceId}_TT1002";

                            var controllerConfig = new IntelligentControllerConfig
                            {
                                TargetTemperature = tt1002Target,
                                Tolerance = temperatureTolerance,
                                ReadingInterval = TimeSpan.FromSeconds(readingInterval),
                                StabilityTime = TimeSpan.FromSeconds(stabilityTime),
                                FineAdjustStep = fineAdjustStep,
                                ApproachPercentage = 0.98
                            };

                            var controller = new IntelligentTemperatureController(
                                deviceId: deviceId,
                                sensorName: "TT1002",
                                config: controllerConfig,
                                circulatorDeviceId: circulatorId,
                                connectionManager: ConnectionManager,
                                logger: _logger,
                                controlLogger: _controlLogger,
                                isSimulationMode: IsSimulationMode,
                                deviceData: _deviceData,
                                stabilityStatus: _stabilityStatus
                            );

                            _temperatureControllers[controllerKey] = controller;
                            await controller.StartAsync();
                            tt1002Started = true;

                            InfoLog($"启动TT1002智能温度控制: 目标={tt1002Target}℃, 容差=±{temperatureTolerance}℃");
                            _controlLogger.LogControllerStart("TT1002", tt1002Target, temperatureTolerance, readingInterval);
                        }

                        // 启动TT1003智能控制
                        if (tt1003Temp.HasValue && Math.Abs(tt1003Temp.Value) > 0.1)
                        {
                            tt1003Target = tt1003Temp.Value;
                            string controllerKey = $"{deviceId}_TT1003";

                            var controllerConfig = new IntelligentControllerConfig
                            {
                                TargetTemperature = tt1003Target,
                                Tolerance = temperatureTolerance,
                                ReadingInterval = TimeSpan.FromSeconds(readingInterval),
                                StabilityTime = TimeSpan.FromSeconds(stabilityTime),
                                FineAdjustStep = fineAdjustStep,
                                ApproachPercentage = 0.98
                            };

                            var controller = new IntelligentTemperatureController(
                                deviceId: deviceId,
                                sensorName: "TT1003",
                                config: controllerConfig,
                                circulatorDeviceId: circulatorId,
                                connectionManager: ConnectionManager,
                                logger: _logger,
                                controlLogger: _controlLogger,
                                isSimulationMode: IsSimulationMode,
                                deviceData: _deviceData,
                                stabilityStatus: _stabilityStatus
                            );

                            _temperatureControllers[controllerKey] = controller;
                            await controller.StartAsync();
                            tt1003Started = true;

                            InfoLog($"启动TT1003智能温度控制: 目标={tt1003Target}℃, 容差=±{temperatureTolerance}℃");
                            _controlLogger.LogControllerStart("TT1003", tt1003Target, temperatureTolerance, readingInterval);
                        }

                        overallSuccess = tt1001Started || tt1002Started || tt1003Started;

                        if (!overallSuccess)
                        {
                            WarnLog($"智能设置温度命令: {deviceId} - 没有提供有效的温度目标值");
                            _controlLogger.LogWarning("没有提供有效的温度目标值");
                        }

                        // *** 状态跟踪 - 记录智能控制启动 ***
                        if (overallSuccess)
                        {
                            AddActiveState(
                                stateName: $"IntelligentTempControl_{deviceId}",
                                activateCommand: "智能设置温度",
                                deactivateCommand: "停止温度控制",
                                stateData: new
                                {
                                    DeviceId = deviceId,
                                    CirculatorId = circulatorId,
                                    TT1001_Target = tt1001Started ? tt1001Target : (double?)null,
                                    TT1002_Target = tt1002Started ? tt1002Target : (double?)null,
                                    TT1003_Target = tt1003Started ? tt1003Target : (double?)null,
                                    ReadingInterval = readingInterval,
                                    Tolerance = temperatureTolerance,
                                    StabilityTime = stabilityTime,
                                    FineAdjustStep = fineAdjustStep,
                                    StartTime = DateTime.Now,
                                }
                            );

                            InfoLog($"微反应器智能温度控制启动成功: {deviceId}, 控制器数量: {_temperatureControllers.Count}");
                            _controlLogger.LogSuccess($"智能温度控制启动成功, 控制器数量: {_temperatureControllers.Count}");
                        }
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"启动微反应器智能温度控制失败: {deviceId} - {ex.Message}");
                        _controlLogger.LogError($"启动智能温度控制失败: {ex.Message}", ex);
                        overallSuccess = false;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("DeviceId", deviceId, "设备ID") { IsVisibleInVariableManager = true },
                            new StringParameter("CirculatorId", circulatorId, "循环器ID") { IsVisibleInVariableManager = true },
                            new BooleanParameter("TT1001_ControlStarted", tt1001Started, "TT1001智能控制启动") { IsVisibleInVariableManager = true },
                            new BooleanParameter("TT1002_ControlStarted", tt1002Started, "TT1002智能控制启动") { IsVisibleInVariableManager = true },
                            new BooleanParameter("TT1003_ControlStarted", tt1003Started, "TT1003智能控制启动") { IsVisibleInVariableManager = true },
                            new NumberParameter("TT1001_TargetTemp", -100, 500, Math.Round(tt1001Target, 2), "TT1001目标温度", true, UnitTemperature),
                            new NumberParameter("TT1002_TargetTemp", -100, 500, Math.Round(tt1002Target, 2), "TT1002目标温度", true, UnitTemperature),
                            new NumberParameter("TT1003_TargetTemp", -100, 500, Math.Round(tt1003Target, 2), "TT1003目标温度", true, UnitTemperature),
                            new BooleanParameter("Success", overallSuccess, "智能温度控制启动成功") { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });

            // 2. 获取微反应器数据命令（保持不变）
            Commands.Add(new DeviceCommand
            {
                Name = "反应器数据",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/cumulative.png",
                HelpText = "读取微反应器的实际温度、压力数据和智能控制状态",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("DeviceId", "", "设备ID") { IsVisibleInVariableManager = true },
                        new NumberParameter("TT1001_ActualTemp",   -100, 500, 0, "TT1001实际温度", true, UnitTemperature),
                        new NumberParameter("TT1002_ActualTemp",   -100, 500, 0, "TT1002实际温度", true, UnitTemperature),
                        new NumberParameter("TT1003_ActualTemp",   -100, 500, 0, "TT1003实际温度", true, UnitTemperature),
                        new NumberParameter("PT0112_Pressure",     0, 100,    0, "出口压力",       true, UnitPressure),
                        new NumberParameter("TT0112_Temperature",  -100, 500, 0, "出口温度",       true, UnitTemperature),
                        new BooleanParameter("TT1001_Stable", false, "TT1001温度稳定") { IsVisibleInVariableManager = true },
                        new BooleanParameter("TT1002_Stable", false, "TT1002温度稳定") { IsVisibleInVariableManager = true },
                        new BooleanParameter("TT1003_Stable", false, "TT1003温度稳定") { IsVisibleInVariableManager = true },
                        new BooleanParameter("SystemStable", false, "系统整体稳定") { IsVisibleInVariableManager = true },
                        new StringParameter("ControlStatus", "", "智能控制状态") { IsVisibleInVariableManager = true },
                        new BooleanParameter("Success", false, "读取成功") { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("反应器数据", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    string deviceId = ComId;
                    double tt1001Actual = 0, tt1002Actual = 0, tt1003Actual = 0, pt0112 = 0, tt0112 = 0;
                    bool success = false;

                    try
                    {
                        if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            // 并行读取微反应器数据
                            var tt1001Task = ConnectionManager.ReadAsync<double>(ComId, "TT1001_℃");
                            var tt1002Task = ConnectionManager.ReadAsync<double>(ComId, "TT1002_℃");
                            var tt1003Task = ConnectionManager.ReadAsync<double>(ComId, "TT1003_℃");
                            var pt0112Task = ConnectionManager.ReadAsync<double>(ComId, "PT0112_Mpa");
                            var tt0112Task = ConnectionManager.ReadAsync<double>(ComId, "TT0112_℃");

                            await Task.WhenAll(tt1001Task, tt1002Task, tt1003Task, pt0112Task, tt0112Task);

                            tt1001Actual = await tt1001Task;
                            tt1002Actual = await tt1002Task;
                            tt1003Actual = await tt1003Task;
                            pt0112 = await pt0112Task;
                            tt0112 = await tt0112Task;

                            InfoLog($"读取 {deviceId} 微反应器实际数据: TT1001={tt1001Actual}℃, TT1002={tt1002Actual}℃, TT1003={tt1003Actual}℃");
                            success = true;
                        }
                        else if (IsSimulationMode)
                        {
                            // 模拟微反应器数据
                            var random = new Random();

                            tt1001Actual = GetSimulatedTemperature(deviceId, "TT1001", random);
                            tt1002Actual = GetSimulatedTemperature(deviceId, "TT1002", random);
                            tt1003Actual = GetSimulatedTemperature(deviceId, "TT1003", random);

                            pt0112 = 0.1 + random.NextDouble() * 5;
                            tt0112 = 20 + random.NextDouble() * 200;

                            _deviceData[$"{deviceId}_TT1001_Set"] = tt1001Actual;
                            _deviceData[$"{deviceId}_TT1002_Set"] = tt1002Actual;
                            _deviceData[$"{deviceId}_TT1003_Set"] = tt1003Actual;
                            _deviceData[$"{deviceId}_PT0112"] = pt0112;
                            _deviceData[$"{deviceId}_TT0112"] = tt0112;

                            success = true;
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }
                        if (success)
                        {
                            //  新增：命令执行成功时也推送一次，立即刷新UI
                            RaiseControlStateChanged("TT1001_ActualTemp", tt1001Actual);
                            RaiseControlStateChanged("TT1002_ActualTemp", tt1002Actual);
                            RaiseControlStateChanged("TT1003_ActualTemp", tt1003Actual);

                            // 同时更新缓存
                            _deviceData[$"{deviceId}_TT1001_Actual"] = tt1001Actual;
                            _deviceData[$"{deviceId}_TT1002_Actual"] = tt1002Actual;
                            _deviceData[$"{deviceId}_TT1003_Actual"] = tt1003Actual;
                        }
                        // 获取智能控制状态
                        string controlStatus = GetIntelligentControlStatus();
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
                            new NumberParameter("TT1001_ActualTemp",  -100, 500, Math.Round(tt1001Actual, 2), "TT1001实际温度", true, UnitTemperature),
                            new NumberParameter("TT1002_ActualTemp",  -100, 500, Math.Round(tt1002Actual, 2), "TT1002实际温度", true, UnitTemperature),
                            new NumberParameter("TT1003_ActualTemp",  -100, 500, Math.Round(tt1003Actual, 2), "TT1003实际温度", true, UnitTemperature),
                            new NumberParameter("PT0112_Pressure",    0, 100,    Math.Round(pt0112, 3),       "出口压力",       true, UnitPressure),
                            new NumberParameter("TT0112_Temperature", -100, 500, Math.Round(tt0112, 2),       "出口温度",       true, UnitTemperature),
                            new BooleanParameter("TT1001_Stable", _stabilityStatus.TT1001_Stable, "TT1001温度稳定") { IsVisibleInVariableManager = true },
                            new BooleanParameter("TT1002_Stable", _stabilityStatus.TT1002_Stable, "TT1002温度稳定") { IsVisibleInVariableManager = true },
                            new BooleanParameter("TT1003_Stable", _stabilityStatus.TT1003_Stable, "TT1003温度稳定") { IsVisibleInVariableManager = true },
                            new BooleanParameter("SystemStable", _stabilityStatus.IsSystemStable, "系统整体稳定") { IsVisibleInVariableManager = true },
                            new StringParameter("ControlStatus", GetIntelligentControlStatus(), "智能控制状态") { IsVisibleInVariableManager = true },
                            new BooleanParameter("Success", success, "读取成功") { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });

            // 3. 停止温度控制命令
            Commands.Add(new DeviceCommand
            {
                Name = "停止温度控制",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/stop_control.png",
                HelpText = "停止智能自动温度控制并完全关闭循环器功能",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("StopTT1001", true, "停止TT1001控制"),
                        new BooleanParameter("StopTT1002", true, "停止TT1002控制"),
                        new BooleanParameter("StopTT1003", true, "停止TT1003控制"),
                        new BooleanParameter("StopCirculator", true, "停止循环器")
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("DeviceId", "", "设备ID") { IsVisibleInVariableManager = true },
                        new BooleanParameter("TT1001_Stopped", false, "TT1001控制已停止") { IsVisibleInVariableManager = true },
                        new BooleanParameter("TT1002_Stopped", false, "TT1002控制已停止") { IsVisibleInVariableManager = true },
                        new BooleanParameter("TT1003_Stopped", false, "TT1003控制已停止") { IsVisibleInVariableManager = true },
                        new BooleanParameter("Circulator_Stopped", false, "循环器已停止") { IsVisibleInVariableManager = true },
                        new BooleanParameter("Success", false, "停止成功") { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("停止温度控制", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    string deviceId = ComId;
                    string circulatorId = Parameters.GetValue<string>("CirculatorDeviceId");

                    bool stopTT1001 = parameters.GetValue<bool>("StopTT1001");
                    bool stopTT1002 = parameters.GetValue<bool>("StopTT1002");
                    bool stopTT1003 = parameters.GetValue<bool>("StopTT1003");
                    bool stopCirculator = parameters.GetValue<bool>("StopCirculator");

                    bool tt1001Stopped = false, tt1002Stopped = false, tt1003Stopped = false, circulatorStopped = false;
                    bool success = false;

                    try
                    {
                        _controlLogger.LogInfo("开始停止智能温度控制");

                        if (stopTT1001)
                        {
                            string controllerKey = $"{deviceId}_TT1001";
                            if (_temperatureControllers.TryRemove(controllerKey, out var controller))
                            {
                                await controller.StopAsync();
                                tt1001Stopped = true;
                                InfoLog($"停止TT1001智能温度控制");
                                _controlLogger.LogInfo($"停止TT1001智能温度控制");
                            }
                        }

                        if (stopTT1002)
                        {
                            string controllerKey = $"{deviceId}_TT1002";
                            if (_temperatureControllers.TryRemove(controllerKey, out var controller))
                            {
                                await controller.StopAsync();
                                tt1002Stopped = true;
                                InfoLog($"停止TT1002智能温度控制");
                                _controlLogger.LogInfo($"停止TT1002智能温度控制");
                            }
                        }

                        if (stopTT1003)
                        {
                            string controllerKey = $"{deviceId}_TT1003";
                            if (_temperatureControllers.TryRemove(controllerKey, out var controller))
                            {
                                await controller.StopAsync();
                                tt1003Stopped = true;
                                InfoLog($"停止TT1003智能温度控制");
                                _controlLogger.LogInfo($"停止TT1003智能温度控制");
                            }
                        }

                        // 停止循环器
                        if (stopCirculator)
                        {
                            _controlLogger.LogInfo("开始完全停止循环器");
                            circulatorStopped = await StopCirculator(circulatorId);
                        }

                        success = true;

                        // 重置稳定性状态
                        _stabilityStatus = new SystemStabilityStatus();

                        // *** 移除状态跟踪 ***
                        RemoveActiveState($"IntelligentTempControl_{deviceId}");

                        _controlLogger.LogSuccess("智能温度控制停止完成");
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"停止智能温度控制失败: {deviceId} - {ex.Message}");
                        _controlLogger.LogError($"停止智能温度控制失败: {ex.Message}", ex);
                        success = false;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("DeviceId", deviceId, "设备ID") { IsVisibleInVariableManager = true },
                            new BooleanParameter("TT1001_Stopped", tt1001Stopped, "TT1001控制已停止") { IsVisibleInVariableManager = true },
                            new BooleanParameter("TT1002_Stopped", tt1002Stopped, "TT1002控制已停止") { IsVisibleInVariableManager = true },
                            new BooleanParameter("TT1003_Stopped", tt1003Stopped, "TT1003控制已停止") { IsVisibleInVariableManager = true },
                            new BooleanParameter("Circulator_Stopped", circulatorStopped, "循环器已停止") { IsVisibleInVariableManager = true },
                            new BooleanParameter("Success", success, "停止成功") { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });
        }

        #region 辅助方法

        private double GetSimulatedTemperature(string deviceId, string sensorName, Random random)
        {
            string targetKey = $"{deviceId}_{sensorName}_Target";
            string actualKey = $"{deviceId}_{sensorName}_Set";
            string circulatorKey = $"{deviceId}_{sensorName}_CirculatorTemp";

            if (_deviceData.TryGetValue(targetKey, out double targetTemp))
            {
                double currentTemp = _deviceData.TryGetValue(actualKey, out double existing) ? existing : 25.0;

                // 🔧 使用实际发送的循环器温度进行智能模拟
                double effectiveCirculatorTemp = _deviceData.TryGetValue(circulatorKey, out double circTemp)
                    ? circTemp : targetTemp;

                // 🔧 改进的热传递物理模型
                double tempDiff = effectiveCirculatorTemp - currentTemp;

                // 传热系数：根据温差动态调整
                double heatTransferCoeff;
                if (Math.Abs(tempDiff) > 30)
                {
                    heatTransferCoeff = 0.2; // 大温差时快速传热
                }
                else if (Math.Abs(tempDiff) > 15)
                {
                    heatTransferCoeff = 0.15; // 中等温差
                }
                else if (Math.Abs(tempDiff) > 5)
                {
                    heatTransferCoeff = 0.1; // 小温差
                }
                else
                {
                    heatTransferCoeff = 0.05; // 微小温差时缓慢传热
                }

                // 计算温度变化（基于牛顿冷却定律）
                double tempChange = heatTransferCoeff * tempDiff;

                // 限制单次最大变化（防止数值不稳定）
                tempChange = Math.Max(-3.0, Math.Min(3.0, tempChange));

                double newTemp = currentTemp + tempChange;

                // 添加小幅随机波动（模拟传感器噪声和环境干扰）
                newTemp += (random.NextDouble() - 0.5) * 0.3; // 增加波动

                _deviceData[actualKey] = newTemp;
                return newTemp;
            }
            else
            {
                return 25.0 + (random.NextDouble() - 0.5) * 2;
            }
        }

        private string GetIntelligentControlStatus()
        {
            var statusLines = new List<string> { "智能温度控制状态:" };

            foreach (var kvp in _temperatureControllers)
            {
                var controller = kvp.Value;
                string sensorName = controller.SensorName;
                string phase = controller.CurrentPhase.ToString();
                string stateInfo = controller.GetCurrentStateInfo();

                statusLines.Add($"├─ {sensorName}: {phase} - {stateInfo}");
            }

            statusLines.Add($"└─ 整体稳定: {(_stabilityStatus.IsSystemStable ? "🎉 全部稳定" : " 调节中...")}");

            return string.Join("\n", statusLines);
        }

        private async Task EnsureCirculatorCycleRunning(string circulatorId)
        {
            try
            {
                if (IsSimulationMode)
                {
                    InfoLog($"模拟模式：启动循环器{circulatorId}的循环功能");
                    _controlLogger.LogInfo($"模拟模式：启动循环器{circulatorId}的循环功能");
                }
                else
                {
                    string cycleCommand = GetCirculatorCycleCommand(circulatorId);
                    await ConnectionManager.WriteAsync(circulatorId, cycleCommand, true);
                    InfoLog($"启动循环器{circulatorId}的循环功能: {cycleCommand} = true");
                    _controlLogger.LogInfo($"启动循环器{circulatorId}的循环功能: {cycleCommand} = true");
                }
            }
            catch (Exception ex)
            {
                ErrorLog($"启动循环器循环功能失败: {circulatorId}", ex);
                _controlLogger.LogError($"启动循环器循环功能失败: {circulatorId}", ex);
            }
        }

        private async Task<bool> StopCirculator(string circulatorId)
        {
            try
            {
                if (IsSimulationMode)
                {
                    InfoLog($"模拟模式：完全停止循环器{circulatorId}");
                    _controlLogger.LogInfo($"模拟模式：完全停止循环器{circulatorId}");
                    return true;
                }
                else
                {
                    // 停止加热、制冷、循环功能
                    await ConnectionManager.WriteAsync(circulatorId, GetCirculatorHeatingCommand(circulatorId), false);
                    await Task.Delay(300);
                    await ConnectionManager.WriteAsync(circulatorId, GetCirculatorCoolingCommand(circulatorId), false);
                    await Task.Delay(300);
                    await ConnectionManager.WriteAsync(circulatorId, GetCirculatorCycleCommand(circulatorId), false);

                    InfoLog($"完全停止循环器{circulatorId}：温度设为25℃，所有功能关闭");
                    _controlLogger.LogSuccess($"完全停止循环器{circulatorId}：温度设为25℃，所有功能关闭");
                    return true;
                }
            }
            catch (Exception ex)
            {
                ErrorLog($"停止循环器失败: {circulatorId}", ex);
                _controlLogger.LogError($"停止循环器失败: {circulatorId}", ex);
                return false;
            }
        }

        private string GetCirculatorHeatingCommand(string circulatorId)
        {
            return circulatorId == "TC0101" ? "YY_Heat_1" : "YY_Heat_2";
        }

        private string GetCirculatorCoolingCommand(string circulatorId)
        {
            return circulatorId == "TC0101" ? "YY_Cool_1" : "YY_Cool_2";
        }

        private string GetCirculatorCycleCommand(string circulatorId)
        {
            return circulatorId == "TC0101" ? "YY_Cycle_1" : "YY_Cycle_2";
        }

        private string GetCirculatorTempSetCommand(string circulatorId)
        {
            return circulatorId == "TC0101" ? "YY_SV_Set_1" : "YY_SV_Set_2";
        }

        #endregion

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

                _deviceData.Clear();

                lock (_statesLock)
                {
                    _activeStates.Clear();
                    _pausedStates.Clear();
                }

                _temperatureControllers.Clear();
                _stabilityStatus = new SystemStabilityStatus();

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

    #region 智能控制配置类

    public class IntelligentControllerConfig
    {
        public double TargetTemperature { get; set; }
        public double Tolerance { get; set; } = 0.5;
        public TimeSpan ReadingInterval { get; set; } = TimeSpan.FromSeconds(3);
        public TimeSpan StabilityTime { get; set; } = TimeSpan.FromSeconds(20);
        public double FineAdjustStep { get; set; } = 0.5;
        public double ApproachPercentage { get; set; } = 0.98;
        public int MinStabilityDataPoints { get; set; } = 8;
        public double StabilityStdDevThreshold { get; set; } = 0.15;
        public double StabilityRangeThreshold { get; set; } = 0.4;

        // 新增配置
        public TimeSpan MinWaitAfterAdjustment { get; set; } = TimeSpan.FromSeconds(30);
        public double StagnantThreshold { get; set; } = 0.3;
        public int MinDataPointsForStagnantCheck { get; set; } = 5;
    }

    #endregion

    #region 控制阶段枚举（重构）

    public enum ControlPhase
    {
        Initializing,           // 初始化
        SettingCirculator,      // 设置循环器温度
        WaitingForEffect,       // 等待温度生效
        CollectingData,         // 收集稳定性数据
        CheckingStability,      // 检查稳定性
        CheckingTarget,         // 检查目标达成
        FineAdjusting,          // 精确微调
        Stable,                 // 已稳定
        Stopped                 // 已停止
    }

    #endregion

    #region 稳定性检查结果类

    public class StabilityResult
    {
        public bool IsStable { get; set; }
        public double StandardDeviation { get; set; }
        public double TemperatureRange { get; set; }
        public bool StdDevOK { get; set; }
        public bool RangeOK { get; set; }
        public bool TrendOK { get; set; }
        public bool RecentPointsOK { get; set; }
        public bool TimeOK { get; set; }
        public string Details { get; set; }

        public static StabilityResult InsufficientData()
        {
            return new StabilityResult
            {
                IsStable = false,
                Details = "数据点不足"
            };
        }
    }

    #endregion

    #region 智能温度控制器类（重构）

    public class IntelligentTemperatureController
    {
        private readonly string _deviceId;
        private readonly string _sensorName;
        private readonly IntelligentControllerConfig _config;
        private readonly string _circulatorDeviceId;
        private readonly IDeviceConnectionManager _connectionManager;
        private readonly ILogService _logger;
        private readonly SmartControlLogger _controlLogger;
        private readonly bool _isSimulationMode;
        private readonly ConcurrentDictionary<string, double> _deviceData;
        private readonly SystemStabilityStatus _stabilityStatus;

        private CancellationTokenSource _cancellationTokenSource;
        private Task _controlTask;
        private volatile bool _isRunning = false;
        private volatile bool _isPaused = false;

        // 控制状态
        private volatile ControlPhase _currentPhase = ControlPhase.Initializing;
        private readonly object _stateLock = new object();
        private int _controlCycleCount = 0;
        private double _lastSentCirculatorTemp = double.NaN;
        private DateTime _lastCirculatorAdjustTime = DateTime.MinValue;

        // 温度数据收集
        private readonly ConcurrentQueue<(DateTime time, double temp)> _tempHistory = new ConcurrentQueue<(DateTime, double)>();
        private readonly List<double> _stabilityCheckData = new List<double>();
        private DateTime _stabilityCheckStartTime = DateTime.MinValue;

        // 公共属性
        public string SensorName => _sensorName;
        public ControlPhase CurrentPhase => _currentPhase;

        public IntelligentTemperatureController(
            string deviceId,
            string sensorName,
            IntelligentControllerConfig config,
            string circulatorDeviceId,
            IDeviceConnectionManager connectionManager,
            ILogService logger,
            SmartControlLogger controlLogger,
            bool isSimulationMode,
            ConcurrentDictionary<string, double> deviceData,
            SystemStabilityStatus stabilityStatus)
        {
            _deviceId = deviceId;
            _sensorName = sensorName;
            _config = config;
            _circulatorDeviceId = circulatorDeviceId;
            _connectionManager = connectionManager;
            _logger = logger;
            _controlLogger = controlLogger;
            _isSimulationMode = isSimulationMode;
            _deviceData = deviceData;
            _stabilityStatus = stabilityStatus;
        }

        public async Task StartAsync()
        {
            if (_isRunning)
                return;

            _cancellationTokenSource = new CancellationTokenSource();
            _isRunning = true;
            _isPaused = false;
            _currentPhase = ControlPhase.Initializing;

            // 存储目标温度到缓存
            _deviceData[$"{_deviceId}_{_sensorName}_Target"] = _config.TargetTemperature;

            _controlTask = Task.Run(async () => await IntelligentControlLoopAsync(_cancellationTokenSource.Token));

            _logger.LogInformation($"智能温度控制器启动: {_deviceId}_{_sensorName}, 目标温度: {_config.TargetTemperature}℃");
            _controlLogger.LogInfo($"智能温度控制器启动 {_sensorName}",
                $"目标温度: {_config.TargetTemperature}℃, 98%策略开始");
        }

        public async Task StopAsync()
        {
            if (!_isRunning)
                return;

            _cancellationTokenSource?.Cancel();

            if (_controlTask != null)
            {
                try
                {
                    await _controlTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancelling
                }
            }

            _isRunning = false;
            _currentPhase = ControlPhase.Stopped;

            // 完全停止循环器控制
            await StopCirculatorControlAsync();

            int totalCycles;
            lock (_stateLock)
            {
                totalCycles = _controlCycleCount;
            }

            // 更新稳定性状态为未稳定
            UpdateStabilityStatus(false);

            _logger.LogInformation($"智能温度控制器停止: {_deviceId}_{_sensorName}");
            _controlLogger.LogInfo($"智能温度控制器停止 {_sensorName}", $"总控制周期: {totalCycles}");
        }

        public async Task PauseAsync()
        {
            _isPaused = true;
            _logger.LogInformation($"智能温度控制器暂停: {_deviceId}_{_sensorName}");
            _controlLogger.LogInfo($"智能温度控制器暂停 {_sensorName}");
        }

        public async Task ResumeAsync()
        {
            _isPaused = false;
            _logger.LogInformation($"智能温度控制器恢复: {_deviceId}_{_sensorName}");
            _controlLogger.LogInfo($"智能温度控制器恢复 {_sensorName}");
        }

        public string GetCurrentStateInfo()
        {
            lock (_stateLock)
            {
                switch (_currentPhase)
                {
                    case ControlPhase.Initializing:
                        return "初始化中...";
                    case ControlPhase.SettingCirculator:
                        return "设置循环器温度";
                    case ControlPhase.WaitingForEffect:
                        var waitTime = (DateTime.Now - _lastCirculatorAdjustTime).TotalSeconds;
                        var requiredWait = _config.MinWaitAfterAdjustment.TotalSeconds;
                        return $" 等待温度生效 ({waitTime:F0}/{requiredWait}秒)";
                    case ControlPhase.CollectingData:
                        return $" 收集数据 ({_stabilityCheckData.Count}/{_config.MinStabilityDataPoints}点)";
                    case ControlPhase.CheckingStability:
                        return "🔍 检查稳定性";
                    case ControlPhase.CheckingTarget:
                        return "🎯 检查目标达成";
                    case ControlPhase.FineAdjusting:
                        return "🔧 精确微调中";
                    case ControlPhase.Stable:
                        return " 已稳定达标";
                    case ControlPhase.Stopped:
                        return "已停止";
                    default:
                        return "未知状态";
                }
            }
        }

        private async Task IntelligentControlLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!_isPaused)
                    {
                        await PerformIntelligentTemperatureControlAsync();
                    }

                    await Task.Delay(_config.ReadingInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"智能温度控制循环异常: {_deviceId}_{_sensorName}");
                    _controlLogger.LogError($"智能温度控制循环异常 {_sensorName}: {ex.Message}", ex);
                    await Task.Delay(_config.ReadingInterval, cancellationToken);
                }
            }
        }

        private async Task PerformIntelligentTemperatureControlAsync()
        {
            int currentCycle;
            lock (_stateLock)
            {
                _controlCycleCount++;
                currentCycle = _controlCycleCount;
            }

            // 1. 读取当前物料温度
            double currentTemp = await ReadCurrentTemperatureAsync();
            double tempError = currentTemp - _config.TargetTemperature;
            var now = DateTime.Now;

            // 2. 更新温度历史
            UpdateTemperatureHistory(now, currentTemp);

            // 3. 记录温度读取
            if (currentCycle % 5 == 0)
            {
                _controlLogger.LogTemperatureReading(_sensorName, currentTemp, _config.TargetTemperature, tempError);
                _controlLogger.LogDebug($"智能控制状态 {_sensorName}",
                    $"阶段: {_currentPhase}, 周期#{currentCycle}, 物料温度: {currentTemp:F2}℃, 偏差: {tempError:F2}℃");
            }

            // 4. 根据当前阶段执行相应逻辑
            switch (_currentPhase)
            {
                case ControlPhase.Initializing:
                    await HandleInitializingPhase(currentTemp, now);
                    break;

                case ControlPhase.WaitingForEffect:
                    await HandleWaitingForEffectPhase(currentTemp, now);
                    break;

                case ControlPhase.CollectingData:
                    await HandleCollectingDataPhase(currentTemp, now);
                    break;

                case ControlPhase.CheckingStability:
                    await HandleCheckingStabilityPhase(currentTemp, now);
                    break;

                case ControlPhase.CheckingTarget:
                    await HandleCheckingTargetPhase(currentTemp, tempError, now);
                    break;

                case ControlPhase.FineAdjusting:
                    await HandleFineAdjustingPhase(currentTemp, tempError, now);
                    break;

                case ControlPhase.Stable:
                    await HandleStablePhase(currentTemp, tempError, now);
                    break;
            }
        }

        /// <summary>
        /// 阶段1: 初始化 - 设置循环器温度到98%接近位置
        /// </summary>
        private async Task HandleInitializingPhase(double currentTemp, DateTime now)
        {
            _controlLogger.LogInfo($" 开始智能控制 {_sensorName}",
                $"物料当前: {currentTemp:F2}℃ → 目标: {_config.TargetTemperature:F2}℃");

            // 计算98%接近温度
            double targetCirculatorTemp = Calculate98PercentApproachTemperature(currentTemp);

            // 设置循环器温度
            bool success = await SetCirculatorTemperatureAsync(targetCirculatorTemp);

            if (success)
            {
                _currentPhase = ControlPhase.WaitingForEffect;
                _lastCirculatorAdjustTime = now;

                _controlLogger.LogSuccess($"🎯 启动98%快速接近 {_sensorName}",
                    $"循环器设定: {targetCirculatorTemp:F1}℃");
            }
            else
            {
                _controlLogger.LogError($" 启动快速接近失败 {_sensorName}", null);
            }
        }

        /// <summary>
        /// 阶段2: 等待温度生效 - 等待足够时间让热传递发生
        /// </summary>
        private async Task HandleWaitingForEffectPhase(double currentTemp, DateTime now)
        {
            var timeSinceAdjust = now - _lastCirculatorAdjustTime;

            if (timeSinceAdjust >= _config.MinWaitAfterAdjustment)
            {
                // 等待时间够了，开始收集稳定性数据
                _currentPhase = ControlPhase.CollectingData;
                _stabilityCheckData.Clear();
                _stabilityCheckStartTime = now;

                _controlLogger.LogInfo($"⏰ 温度生效等待完成 {_sensorName}",
                    $"等待时间: {timeSinceAdjust.TotalSeconds:F0}秒, 开始收集稳定性数据");
            }
            // 在等待期间只记录温度，不做任何调整
        }

        /// <summary>
        /// 阶段3: 收集稳定性数据 - 收集足够的数据点用于稳定性分析
        /// </summary>
        private async Task HandleCollectingDataPhase(double currentTemp, DateTime now)
        {
            // 收集稳定性检查数据
            lock (_stabilityCheckData)
            {
                _stabilityCheckData.Add(currentTemp);
            }

            // 限制数据点数量
            if (_stabilityCheckData.Count > _config.MinStabilityDataPoints * 2)
            {
                lock (_stabilityCheckData)
                {
                    _stabilityCheckData.RemoveAt(0);
                }
            }

            // 检查是否有足够的数据点进行稳定性分析
            if (_stabilityCheckData.Count >= _config.MinStabilityDataPoints)
            {
                double timeElapsed = (now - _stabilityCheckStartTime).TotalSeconds;

                if (timeElapsed >= _config.StabilityTime.TotalSeconds)
                {
                    _currentPhase = ControlPhase.CheckingStability;
                    _controlLogger.LogInfo($" 数据收集完成 {_sensorName}",
                        $"收集了 {_stabilityCheckData.Count} 个数据点, 时长: {timeElapsed:F0}秒");
                }
            }
        }

        /// <summary>
        /// 阶段4: 检查稳定性 - 分析收集的数据判断是否稳定
        /// </summary>
        private async Task HandleCheckingStabilityPhase(double currentTemp, DateTime now)
        {
            var stabilityResult = CheckTemperatureStability();

            if (stabilityResult.IsStable)
            {
                _currentPhase = ControlPhase.CheckingTarget;
                _controlLogger.LogSuccess($" 物料温度已稳定 {_sensorName}", stabilityResult.Details);
            }
            else
            {
                // 未稳定，重新收集数据
                _currentPhase = ControlPhase.CollectingData;
                _stabilityCheckStartTime = now;
                lock (_stabilityCheckData)
                {
                    _stabilityCheckData.Clear();
                }

                _controlLogger.LogWarning($" 物料温度未稳定,重新收集数据 {_sensorName}", stabilityResult.Details);
            }
        }

        /// <summary>
        /// 阶段5: 检查目标 - 判断稳定的温度是否在目标范围内
        /// </summary>
        private async Task HandleCheckingTargetPhase(double currentTemp, double tempError, DateTime now)
        {
            if (Math.Abs(tempError) <= _config.Tolerance)
            {
                // 已达到目标，标记为稳定
                _currentPhase = ControlPhase.Stable;
                UpdateStabilityStatus(true);

                _controlLogger.LogSuccess($"🎉 达到目标温度 {_sensorName}",
                    $"物料温度: {currentTemp:F2}℃, 目标: {_config.TargetTemperature:F2}℃, 偏差: {tempError:F2}℃");
            }
            else
            {
                // 需要微调
                _currentPhase = ControlPhase.FineAdjusting;
                _controlLogger.LogInfo($"🔧 需要精确微调 {_sensorName}",
                    $"物料偏差: {tempError:F2}℃, 超出容差: ±{_config.Tolerance}℃");
            }
        }

        /// <summary>
        /// 阶段6: 精确微调 - 小步长调整循环器温度
        /// </summary>
        private async Task HandleFineAdjustingPhase(double currentTemp, double tempError, DateTime now)
        {
            // 计算微调后的循环器温度
            double adjustedCirculatorTemp = CalculateFineAdjustCirculatorTemp(currentTemp, tempError);

            bool success = await SetCirculatorTemperatureAsync(adjustedCirculatorTemp);

            if (success)
            {
                // 调整后重新进入等待温度生效阶段
                _currentPhase = ControlPhase.WaitingForEffect;
                _lastCirculatorAdjustTime = now;

                _controlLogger.LogInfo($"🎛️ 精确微调循环器 {_sensorName}",
                    $"循环器: {adjustedCirculatorTemp:F1}℃, 物料偏差: {tempError:F2}℃");
            }
        }

        /// <summary>
        /// 阶段7: 稳定状态 - 监控物料温度，防止失控
        /// </summary>
        private async Task HandleStablePhase(double currentTemp, double tempError, DateTime now)
        {
            // 检查是否仍然稳定
            if (Math.Abs(tempError) > _config.Tolerance * 1.5)
            {
                // 失去稳定，重新微调
                _currentPhase = ControlPhase.FineAdjusting;
                UpdateStabilityStatus(false);

                _controlLogger.LogWarning($" 失去稳定状态 {_sensorName}",
                    $"物料偏差: {tempError:F2}℃ > 容差: ±{_config.Tolerance * 1.5:F2}℃");
            }
            else
            {
                // 保持稳定状态
                UpdateStabilityStatus(true);
            }
        }

        /// <summary>
        /// 🔧 修正后的98%计算逻辑
        /// </summary>
        private double Calculate98PercentApproachTemperature(double currentMaterialTemp)
        {
            double tempDifference = _config.TargetTemperature - currentMaterialTemp;

            if (Math.Abs(tempDifference) <= 1.0)
            {
                // 已经很接近，循环器温度 = 目标温度
                return _config.TargetTemperature;
            }

            // 98%接近：循环器温度 = 当前物料温度 + 98% * 温差
            double circulatorTemp = currentMaterialTemp + (tempDifference * _config.ApproachPercentage);

            _controlLogger.LogInfo($"98%计算 {_sensorName}",
                $"物料: {currentMaterialTemp:F1}℃, 目标: {_config.TargetTemperature:F1}℃, 温差: {tempDifference:F1}℃, 循环器: {circulatorTemp:F1}℃");

            return circulatorTemp;
        }

        /// <summary>
        /// 🔧 改进的微调计算
        /// </summary>
        private double CalculateFineAdjustCirculatorTemp(double currentTemp, double tempError)
        {
            double adjustStep = _config.FineAdjustStep;

            // 根据偏差大小动态调整步长
            if (Math.Abs(tempError) > 2.0)
                adjustStep = 1.0;
            else if (Math.Abs(tempError) > 1.0)
                adjustStep = 0.5;
            else
                adjustStep = 0.3;

            double newCirculatorTemp;

            if (tempError < 0)
            {
                // 物料温度低于目标，需要升温
                newCirculatorTemp = _config.TargetTemperature + adjustStep;
            }
            else
            {
                // 物料温度高于目标，需要降温
                newCirculatorTemp = _config.TargetTemperature - adjustStep;
            }

            _controlLogger.LogInfo($"微调计算 {_sensorName}",
                $"物料偏差: {tempError:F2}℃, 步长: {adjustStep:F1}℃, 新循环器温度: {newCirculatorTemp:F1}℃");

            return newCirculatorTemp;
        }

        /// <summary>
        /// 🔧 改进的稳定性检查
        /// </summary>
        private StabilityResult CheckTemperatureStability()
        {
            lock (_stabilityCheckData)
            {
                if (_stabilityCheckData.Count < _config.MinStabilityDataPoints)
                {
                    return StabilityResult.InsufficientData();
                }

                var result = new StabilityResult();

                // 1. 计算标准差
                result.StandardDeviation = CalculateStandardDeviation(_stabilityCheckData);
                result.StdDevOK = result.StandardDeviation <= _config.StabilityStdDevThreshold;

                // 2. 计算温度范围
                result.TemperatureRange = _stabilityCheckData.Max() - _stabilityCheckData.Min();
                result.RangeOK = result.TemperatureRange <= _config.StabilityRangeThreshold;

                // 3. 趋势检查
                result.TrendOK = CheckTemperatureTrend(_stabilityCheckData);

                // 4. 近期点稳定性
                result.RecentPointsOK = CheckRecentPointsStability(_stabilityCheckData);

                // 5. 时间要求（已在上层检查）
                result.TimeOK = true;

                // 6. 综合判断
                result.IsStable = result.StdDevOK && result.RangeOK && result.TrendOK && result.RecentPointsOK;

                result.Details = $"标准差:{result.StandardDeviation:F3}℃(≤{_config.StabilityStdDevThreshold}) " +
                               $"范围:{result.TemperatureRange:F2}℃(≤{_config.StabilityRangeThreshold}) " +
                               $"趋势:{(result.TrendOK ? "✓" : "✗")} " +
                               $"近期点:{(result.RecentPointsOK ? "✓" : "✗")}";

                return result;
            }
        }

        private bool CheckTemperatureTrend(List<double> data)
        {
            if (data.Count < 6) return true;

            var recentData = data.TakeLast(6).ToList();
            double slope = CalculateSlope(recentData);

            // 斜率绝对值 < 0.05℃/读数 认为无明显趋势
            return Math.Abs(slope) < 0.05;
        }

        private bool CheckRecentPointsStability(List<double> data)
        {
            if (data.Count < 4) return false;

            var recentPoints = data.TakeLast(4).ToList();

            //  正确逻辑：检查最近4个点之间的波动
            double recentRange = recentPoints.Max() - recentPoints.Min();
            bool smallFluctuation = recentRange <= 0.2; // 最近4个点波动 ≤ 0.2℃

            // 额外检查：避免单调趋势（持续上升或下降）
            bool noMonotonicTrend = true;
            if (recentPoints.Count >= 4)
            {
                // 检查是否严格单调
                bool isIncreasing = true;
                bool isDecreasing = true;

                for (int i = 1; i < recentPoints.Count; i++)
                {
                    if (recentPoints[i] <= recentPoints[i - 1]) isIncreasing = false;
                    if (recentPoints[i] >= recentPoints[i - 1]) isDecreasing = false;
                }

                noMonotonicTrend = !(isIncreasing || isDecreasing);
            }

            return smallFluctuation && noMonotonicTrend;
        }

        private double CalculateSlope(List<double> data)
        {
            if (data.Count < 2) return 0;

            double n = data.Count;
            double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;

            for (int i = 0; i < data.Count; i++)
            {
                sumX += i;
                sumY += data[i];
                sumXY += i * data[i];
                sumX2 += i * i;
            }

            // 线性回归斜率计算
            double slope = (n * sumXY - sumX * sumY) / (n * sumX2 - sumX * sumX);
            return slope;
        }

        private double CalculateStandardDeviation(List<double> values)
        {
            if (values == null || values.Count < 2)
                return 999.0;

            try
            {
                double mean = values.Average();
                double sumOfSquares = values.Sum(x => Math.Pow(x - mean, 2));
                double variance = sumOfSquares / (values.Count - 1);

                if (double.IsNaN(variance) || double.IsInfinity(variance))
                    return 999.0;

                double stdDev = Math.Sqrt(variance);

                if (double.IsNaN(stdDev) || double.IsInfinity(stdDev))
                    return 999.0;

                return stdDev;
            }
            catch
            {
                return 999.0;
            }
        }

        /// <summary>
        /// 设置循环器温度
        /// </summary>
        private async Task<bool> SetCirculatorTemperatureAsync(double temperature)
        {
            try
            {
                if (_isSimulationMode)
                {
                    // 模拟模式下保存循环器温度
                    string circulatorKey = $"{_deviceId}_{_sensorName}_CirculatorTemp";
                    _deviceData[circulatorKey] = temperature;

                    _lastSentCirculatorTemp = temperature;
                    return true;
                }

                string tempSetCommand = GetCirculatorTempSetCommand();
                bool result = await _connectionManager.WriteAsync(_circulatorDeviceId, tempSetCommand, temperature);

                if (result)
                {
                    _lastSentCirculatorTemp = temperature;
                    _controlLogger.LogInfo($"设置循环器温度成功 {_sensorName}",
                        $"循环器{_circulatorDeviceId}: {temperature:F1}℃");
                }
                else
                {
                    _controlLogger.LogError($"设置循环器温度失败 {_sensorName}", null);
                }

                return result;
            }
            catch (Exception ex)
            {
                _controlLogger.LogError($"设置循环器温度异常 {_sensorName}", ex);
                return false;
            }
        }

        /// <summary>
        /// 读取物料当前温度
        /// </summary>
        private async Task<double> ReadCurrentTemperatureAsync()
        {
            if (_isSimulationMode)
            {
                string key = $"{_deviceId}_{_sensorName}_Set";
                if (_deviceData.TryGetValue(key, out double existingTemp))
                {
                    return existingTemp;
                }
                else
                {
                    double simulatedTemp = 25.0 + new Random().NextDouble() * 5;
                    _deviceData[key] = simulatedTemp;
                    return simulatedTemp;
                }
            }
            else
            {
                try
                {
                    return await _connectionManager.ReadAsync<double>(_deviceId, $"{_sensorName}_℃");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"读取物料温度传感器失败: {_deviceId}_{_sensorName}");
                    _controlLogger.LogError($"读取物料温度传感器失败 {_sensorName}", ex);

                    // 返回上次读取的温度
                    if (_tempHistory.TryPeek(out var lastReading))
                    {
                        _controlLogger.LogWarning($"使用上次读取的温度: {lastReading.temp:F2}℃");
                        return lastReading.temp;
                    }

                    throw new Exception($"无法读取物料温度且没有历史数据: {_sensorName}");
                }
            }
        }

        private async Task StopCirculatorControlAsync()
        {
            try
            {
                if (_isSimulationMode)
                {
                    _controlLogger.LogInfo($"模拟模式：{_sensorName}控制器完全停止循环器");
                    return;
                }

                _controlLogger.LogInfo($"开始完全停止循环器{_circulatorDeviceId}...");

                // 停止加热功能
                string heatCommand = GetCirculatorHeatingCommand();
                bool heatStopResult = await _connectionManager.WriteAsync(_circulatorDeviceId, heatCommand, false);

                if (heatStopResult)
                {
                    _controlLogger.LogInfo($" 循环器加热功能已停止: {heatCommand} = false");
                }

                await Task.Delay(300);

                // 停止制冷功能
                string coolCommand = GetCirculatorCoolingCommand();
                bool coolStopResult = await _connectionManager.WriteAsync(_circulatorDeviceId, coolCommand, false);

                if (coolStopResult)
                {
                    _controlLogger.LogInfo($" 循环器制冷功能已停止: {coolCommand} = false");
                }

                await Task.Delay(300);

                // 停止循环功能
                string cycleCommand = GetCirculatorCycleCommand();
                bool cycleStopResult = await _connectionManager.WriteAsync(_circulatorDeviceId, cycleCommand, false);

                if (cycleStopResult)
                {
                    _controlLogger.LogInfo($" 循环器循环功能已停止: {cycleCommand} = false");
                }

                _controlLogger.LogSuccess($"🎉 循环器{_circulatorDeviceId}已完全停止",
                    $"温度设为25℃，循环+加热+制冷功能全部关闭");
            }
            catch (Exception ex)
            {
                _controlLogger.LogError($"完全停止循环器控制异常: {_circulatorDeviceId}", ex);
                _logger.LogError(ex, $"完全停止循环器控制异常: {_deviceId}_{_sensorName}");
            }
        }

        private void UpdateTemperatureHistory(DateTime now, double currentTemp)
        {
            _tempHistory.Enqueue((now, currentTemp));

            // 清理过期数据（保留最近5分钟）
            while (_tempHistory.TryPeek(out var oldest) &&
                   (now - oldest.time).TotalMinutes > 5)
            {
                _tempHistory.TryDequeue(out _);
            }
        }

        private void UpdateStabilityStatus(bool isStable)
        {
            bool wasStable = false;

            switch (_sensorName)
            {
                case "TT1001":
                    wasStable = _stabilityStatus.TT1001_Stable;
                    _stabilityStatus.TT1001_Stable = isStable;
                    break;
                case "TT1002":
                    wasStable = _stabilityStatus.TT1002_Stable;
                    _stabilityStatus.TT1002_Stable = isStable;
                    break;
                case "TT1003":
                    wasStable = _stabilityStatus.TT1003_Stable;
                    _stabilityStatus.TT1003_Stable = isStable;
                    break;
            }

            // 记录稳定性状态变化
            if (!wasStable && isStable)
            {
                _controlLogger.LogSuccess($"{_sensorName}物料温度达到稳定状态");
            }
            else if (wasStable && !isStable)
            {
                _controlLogger.LogWarning($"{_sensorName}物料温度失去稳定状态");
            }

            // 检查系统整体稳定性
            if (_stabilityStatus.IsSystemStable)
            {
                _controlLogger.LogSystemStable();
            }
        }

        private string GetCirculatorTempSetCommand()
        {
            return _circulatorDeviceId == "TC0101" ? "YY_SV_Set_1" : "YY_SV_Set_2";
        }

        private string GetCirculatorHeatingCommand()
        {
            return _circulatorDeviceId == "TC0101" ? "YY_Heat_1" : "YY_Heat_2";
        }

        private string GetCirculatorCoolingCommand()
        {
            return _circulatorDeviceId == "TC0101" ? "YY_Cool_1" : "YY_Cool_2";
        }

        private string GetCirculatorCycleCommand()
        {
            return _circulatorDeviceId == "TC0101" ? "YY_Cycle_1" : "YY_Cycle_2";
        }
    }

    #endregion

    #region 专用智能控制日志记录器

    public class SmartControlLogger
    {
        private string _logFilePath;
        private readonly object _logLock = new object();
        private StreamWriter _logWriter;
        private bool _isInitialized = false;

        public void Initialize(string flowName)
        {
            try
            {
                // 创建专用的智能控制日志目录
                string logDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "MaxChemical",
                    "IntelligentTempControl",
                    DateTime.Now.ToString("yyyy-MM-dd")
                );

                Directory.CreateDirectory(logDirectory);

                // 创建以流程名和时间命名的日志文件
                string fileName = $"IntelligentControl_{flowName}_{DateTime.Now:HHmmss}.log";
                _logFilePath = Path.Combine(logDirectory, fileName);

                lock (_logLock)
                {
                    _logWriter?.Dispose();
                    _logWriter = new StreamWriter(_logFilePath, append: true, encoding: System.Text.Encoding.UTF8)
                    {
                        AutoFlush = true
                    };

                    _isInitialized = true;

                    // 写入日志文件头信息
                    _logWriter.WriteLine("=".PadRight(80, '='));
                    _logWriter.WriteLine($"智能温度控制系统日志 (改进版)");
                    _logWriter.WriteLine($"流程名称: {flowName}");
                    _logWriter.WriteLine($"开始时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    _logWriter.WriteLine($"日志文件: {fileName}");
                    _logWriter.WriteLine($"控制策略: 98%快速接近 → 等待生效 → 稳定性检测 → 精确微调");
                    _logWriter.WriteLine($"核心改进: 每次调整循环器后等待30秒，避免频繁调整");
                    _logWriter.WriteLine("=".PadRight(80, '='));
                    _logWriter.WriteLine();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"智能控制日志初始化失败: {ex.Message}");
            }
        }

        public void LogControlStart(string deviceId, string circulatorId)
        {
            WriteLog("INFO", $" 启动智能温度控制系统", $"设备ID: {deviceId}, 循环器ID: {circulatorId}");
        }

        public void LogControllerStart(string sensorName, double targetTemp, double tolerance, int interval)
        {
            WriteLog("INFO", $" 启动{sensorName}智能控制器",
                $"目标温度: {targetTemp}℃, 容差: ±{tolerance}℃, 间隔: {interval}秒");
        }

        public void LogTemperatureReading(string sensorName, double actualTemp, double targetTemp, double error)
        {
            WriteLog("DATA", $"🌡️ 温度读取 {sensorName}",
                $"物料温度: {actualTemp:F2}℃, 目标: {targetTemp:F2}℃, 偏差: {error:F2}℃");
        }

        public void LogCirculatorControl(string circulatorId, double targetTemp, string reason)
        {
            WriteLog("CONTROL", $"🎛️ 循环器控制 {circulatorId}",
                $"设定温度: {targetTemp:F1}℃, 原因: {reason}");
        }

        public void LogStabilityCheck(string sensorName, bool isStable, string details)
        {
            string emoji = isStable ? "" : "";
            WriteLog("STABILITY", $"{emoji} {sensorName}稳定性检查",
                $"稳定: {(isStable ? "是" : "否")}, 详情: {details}");
        }

        public void LogSystemStable()
        {
            WriteLog("SUCCESS", "🎉 系统整体稳定", "所有物料温度已达到稳定状态,可以开始进料反应");
        }

        public void LogWarning(string message, string details = "")
        {
            WriteLog("WARN", $" {message}", details);
        }

        public void LogError(string message, Exception ex = null)
        {
            string details = ex != null ? $"异常信息: {ex.Message}\n堆栈: {ex.StackTrace}" : "";
            WriteLog("ERROR", $" {message}", details);
        }

        public void LogInfo(string message, string details = "")
        {
            WriteLog("INFO", $"ℹ️ {message}", details);
        }

        public void LogSuccess(string message, string details = "")
        {
            WriteLog("SUCCESS", $" {message}", details);
        }

        public void LogDebug(string message, string details = "")
        {
            WriteLog("DEBUG", $"🔍 {message}", details);
        }

        private void WriteLog(string level, string message, string details = "")
        {
            if (!_isInitialized || _logWriter == null)
                return;

            try
            {
                lock (_logLock)
                {
                    var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
                    var threadId = Thread.CurrentThread.ManagedThreadId.ToString().PadLeft(3);

                    _logWriter.WriteLine($"[{timestamp}] [{level.PadRight(8)}] [T{threadId}] {message}");

                    if (!string.IsNullOrEmpty(details))
                    {
                        foreach (var line in details.Split('\n'))
                        {
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                _logWriter.WriteLine($"{"".PadRight(25)} │ {line.Trim()}");
                            }
                        }
                    }

                    _logWriter.WriteLine();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"写入智能控制日志失败: {ex.Message}");
            }
        }

        public void Close()
        {
            if (!_isInitialized)
                return;

            try
            {
                lock (_logLock)
                {
                    if (_logWriter != null)
                    {
                        _logWriter.WriteLine("=".PadRight(80, '='));
                        _logWriter.WriteLine($"智能温度控制系统日志结束");
                        _logWriter.WriteLine($"结束时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        _logWriter.WriteLine("=".PadRight(80, '='));

                        _logWriter.Dispose();
                        _logWriter = null;
                    }

                    _isInitialized = false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"关闭智能控制日志失败: {ex.Message}");
            }
        }
    }

    #endregion

    #region 系统稳定性状态类

    public class SystemStabilityStatus
    {
        private volatile bool _tt1001Stable = false;
        private volatile bool _tt1002Stable = false;
        private volatile bool _tt1003Stable = false;

        public bool TT1001_Stable
        {
            get => _tt1001Stable;
            set => _tt1001Stable = value;
        }

        public bool TT1002_Stable
        {
            get => _tt1002Stable;
            set => _tt1002Stable = value;
        }

        public bool TT1003_Stable
        {
            get => _tt1003Stable;
            set => _tt1003Stable = value;
        }

        public bool IsSystemStable => _tt1001Stable && _tt1002Stable && _tt1003Stable;

        public string GetStatusReport()
        {
            return $@"智能控制系统稳定性状态:
├─ TT1001: {(_tt1001Stable ? "✓ 稳定" : "⚠ 调节中")}
├─ TT1002: {(_tt1002Stable ? "✓ 稳定" : "⚠ 调节中")}
├─ TT1003: {(_tt1003Stable ? "✓ 稳定" : "⚠ 调节中")}
└─ 整体: {(IsSystemStable ? "🎉 全部稳定，可开始反应" : " 智能调节中...")}";
        }
    }

    #endregion
}