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
    public class MicroReactor_ModbusTCP : Device, IControllableDevice, IFlowLifecycleAware
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

        // *** 温度控制相关字段 ***
        private readonly ConcurrentDictionary<string, SmartTemperatureController> _temperatureControllers = new ConcurrentDictionary<string, SmartTemperatureController>();

        // 系统稳定性状态 - 使用线程安全访问
        private volatile SystemStabilityStatus _stabilityStatus = new SystemStabilityStatus();

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
                    return true;
                }
                InfoLog($"停止ModbusTCP微反应器设备的 {_activeStates.Count} 个活跃状态和 {_pausedStates.Count} 个暂停状态");

                _activeStates.Clear();
                _pausedStates.Clear();
            }


            // 停止所有温度控制器
            var stopTasks = _temperatureControllers.Values.Select(controller => controller.StopAsync());

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

                // 初始化控制日志
                _controlLogger.Initialize(context.FlowName);

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

            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/MicroReactor.png";

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
            // 1. 设置温度命令 - 启动智能自动温度控制系统
            Commands.Add(new DeviceCommand
            {
                Name = "设置温度",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/temp_set.png",
                HelpText = "设置微反应器的物料目标温度并启动智能自动控制，系统将实时监控TT1001/TT1002/TT1003传感器，自动调节高低温循环器的温度和模式，确保温度稳定",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TT1001_Temperature", -100, 500, 25, "TT1001目标温度(℃)")
                        {
                            HelpText = "TT1001温度传感器的目标温度值，系统将自动控制使其稳定在此温度"
                        },
                        new NumberParameter("TT1002_Temperature", -100, 500, 25, "TT1002目标温度(℃)")
                        {
                            HelpText = "TT1002温度传感器的目标温度值，系统将自动控制使其稳定在此温度"
                        },
                        new NumberParameter("TT1003_Temperature", -100, 500, 25, "TT1003目标温度(℃)")
                        {
                            HelpText = "TT1003温度传感器的目标温度值，系统将自动控制使其稳定在此温度"
                        },
                        new NumberParameter("ControlInterval", 3, 30, 5, "控制间隔(秒)")
                        {
                            HelpText = "温度检查和控制的时间间隔，建议3-10秒"
                        },
                        new NumberParameter("TemperatureTolerance", 0.1, 5.0, 1.0, "温度容差(℃)")
                        {
                            HelpText = "温度控制的允许偏差范围，±容差值内认为温度已达标"
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
                        new NumberParameter("TT1001_TargetTemp", -100, 500, 0, "TT1001目标温度(℃)") { IsVisibleInVariableManager = true },
                        new NumberParameter("TT1002_TargetTemp", -100, 500, 0, "TT1002目标温度(℃)") { IsVisibleInVariableManager = true },
                        new NumberParameter("TT1003_TargetTemp", -100, 500, 0, "TT1003目标温度(℃)") { IsVisibleInVariableManager = true },
                        new BooleanParameter("Success", false, "智能温度控制启动成功") { IsVisibleInVariableManager = true }
                    }
                },
                Action = WrapAction("设置温度", async delegate (DeviceParameters parameters)
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    string deviceId = ComId;
                    string circulatorId = Parameters.GetValue<string>("CirculatorDeviceId");

                    var tt1001Temp = parameters.GetNullableValue<double>("TT1001_Temperature");
                    var tt1002Temp = parameters.GetNullableValue<double>("TT1002_Temperature");
                    var tt1003Temp = parameters.GetNullableValue<double>("TT1003_Temperature");

                    int controlInterval = (int)parameters.GetValue<double>("ControlInterval");
                    double temperatureTolerance = parameters.GetValue<double>("TemperatureTolerance");

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

                        // 启动循环器循环功能（始终保持开启）
                        await EnsureCirculatorCycleRunning(circulatorId);
                        //等待三秒
                        await Task.Delay(3000);
                        //部功能都开启
                        //启动加热
                        string heatCommand = GetCirculatorHeatingCommand(circulatorId);
                        await ConnectionManager.WriteAsync(circulatorId, heatCommand, true);
                        //启动制冷
                        string coolingCommad=GetCirculatorCoolingCommand(circulatorId);
                        await ConnectionManager.WriteAsync(circulatorId, coolingCommad, true);
                        // 启动TT1001智能控制
                        if (tt1001Temp.HasValue && Math.Abs(tt1001Temp.Value) > 0.1)
                        {
                            tt1001Target = tt1001Temp.Value;
                            string controllerKey = $"{deviceId}_TT1001";

                            var controller = new SmartTemperatureController(
                                deviceId: deviceId,
                                sensorName: "TT1001",
                                targetTemperature: tt1001Target,
                                circulatorDeviceId: circulatorId,
                                tolerance: temperatureTolerance,
                                controlInterval: TimeSpan.FromSeconds(controlInterval),
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

                            // 存储目标温度到缓存
                            _deviceData[$"{deviceId}_TT1001_Target"] = tt1001Target;

                            InfoLog($"启动TT1001智能温度控制: 目标={tt1001Target}℃, 容差=±{temperatureTolerance}℃, 间隔={controlInterval}秒");
                            _controlLogger.LogControllerStart("TT1001", tt1001Target, temperatureTolerance, controlInterval);
                        }

                        // 启动TT1002智能控制
                        if (tt1002Temp.HasValue && Math.Abs(tt1002Temp.Value) > 0.1)
                        {
                            tt1002Target = tt1002Temp.Value;
                            string controllerKey = $"{deviceId}_TT1002";

                            var controller = new SmartTemperatureController(
                                deviceId: deviceId,
                                sensorName: "TT1002",
                                targetTemperature: tt1002Target,
                                circulatorDeviceId: circulatorId,
                                tolerance: temperatureTolerance,
                                controlInterval: TimeSpan.FromSeconds(controlInterval),
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

                            _deviceData[$"{deviceId}_TT1002_Target"] = tt1002Target;

                            InfoLog($"启动TT1002智能温度控制: 目标={tt1002Target}℃, 容差=±{temperatureTolerance}℃, 间隔={controlInterval}秒");
                            _controlLogger.LogControllerStart("TT1002", tt1002Target, temperatureTolerance, controlInterval);
                        }

                        // 启动TT1003智能控制
                        if (tt1003Temp.HasValue && Math.Abs(tt1003Temp.Value) > 0.1)
                        {
                            tt1003Target = tt1003Temp.Value;
                            string controllerKey = $"{deviceId}_TT1003";

                            var controller = new SmartTemperatureController(
                                deviceId: deviceId,
                                sensorName: "TT1003",
                                targetTemperature: tt1003Target,
                                circulatorDeviceId: circulatorId,
                                tolerance: temperatureTolerance,
                                controlInterval: TimeSpan.FromSeconds(controlInterval),
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

                            _deviceData[$"{deviceId}_TT1003_Target"] = tt1003Target;

                            InfoLog($"启动TT1003智能温度控制: 目标={tt1003Target}℃, 容差=±{temperatureTolerance}℃, 间隔={controlInterval}秒");
                            _controlLogger.LogControllerStart("TT1003", tt1003Target, temperatureTolerance, controlInterval);
                        }

                        overallSuccess = tt1001Started || tt1002Started || tt1003Started;

                        if (!overallSuccess)
                        {
                            WarnLog($"设置温度命令: {deviceId} - 没有提供有效的温度目标值");
                            _controlLogger.LogWarning("没有提供有效的温度目标值");
                        }

                        // *** 状态跟踪 - 记录智能控制启动 ***
                        if (overallSuccess)
                        {

                            AddActiveState(
                                stateName: $"SmartTempControl_{deviceId}",
                                activateCommand: "设置温度",
                                deactivateCommand: "停止温度控制",
                                stateData: new
                                {
                                    DeviceId = deviceId,
                                    CirculatorId = circulatorId,
                                    TT1001_Target = tt1001Started ? tt1001Target : (double?)null,
                                    TT1002_Target = tt1002Started ? tt1002Target : (double?)null,
                                    TT1003_Target = tt1003Started ? tt1003Target : (double?)null,
                                    ControlInterval = controlInterval,
                                    Tolerance = temperatureTolerance,
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
                            new NumberParameter("TT1001_TargetTemp", -100, 500, Math.Round(tt1001Target, 2), "TT1001目标温度(℃)") { IsVisibleInVariableManager = true },
                            new NumberParameter("TT1002_TargetTemp", -100, 500, Math.Round(tt1002Target, 2), "TT1002目标温度(℃)") { IsVisibleInVariableManager = true },
                            new NumberParameter("TT1003_TargetTemp", -100, 500, Math.Round(tt1003Target, 2), "TT1003目标温度(℃)") { IsVisibleInVariableManager = true },
                            new BooleanParameter("Success", overallSuccess, "智能温度控制启动成功") { IsVisibleInVariableManager = true }
                        }
                    };
                })
            });

            // 2. 获取微反应器数据命令
            Commands.Add(new DeviceCommand
            {
                Name = "反应器数据",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/cumulative.png",
                HelpText = "读取微反应器的实际温度、压力数据和控制状态",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("DeviceId", "", "设备ID") { IsVisibleInVariableManager = true },
                        new NumberParameter("TT1001_ActualTemp", -100, 500, 0, "TT1001实际温度(℃)") { IsVisibleInVariableManager = true },
                        new NumberParameter("TT1002_ActualTemp", -100, 500, 0, "TT1002实际温度(℃)") { IsVisibleInVariableManager = true },
                        new NumberParameter("TT1003_ActualTemp", -100, 500, 0, "TT1003实际温度(℃)") { IsVisibleInVariableManager = true },
                        new NumberParameter("PT0112_Pressure", 0, 100, 0, "出口压力(MPa)") { IsVisibleInVariableManager = true },
                        new NumberParameter("TT0112_Temperature", -100, 500, 0, "出口温度(℃)") { IsVisibleInVariableManager = true },
                        new BooleanParameter("TT1001_Stable", false, "TT1001温度稳定") { IsVisibleInVariableManager = true },
                        new BooleanParameter("TT1002_Stable", false, "TT1002温度稳定") { IsVisibleInVariableManager = true },
                        new BooleanParameter("TT1003_Stable", false, "TT1003温度稳定") { IsVisibleInVariableManager = true },
                        new BooleanParameter("SystemStable", false, "系统整体稳定") { IsVisibleInVariableManager = true },
                        new StringParameter("ControlStatus", "", "控制状态") { IsVisibleInVariableManager = true },
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

                            InfoLog($"读取 {deviceId} 微反应器实际数据: TT1001={tt1001Actual}℃, TT1002={tt1002Actual}℃, TT1003={tt1003Actual}℃, PT0112={pt0112}MPa, TT0112={tt0112}℃");
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
                            InfoLog($"模拟模式读取 {deviceId} 微反应器数据: TT1001={tt1001Actual:F2}℃, TT1002={tt1002Actual:F2}℃, TT1003={tt1003Actual:F2}℃, PT0112={pt0112:F3}MPa, TT0112={tt0112:F2}℃");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        // 获取稳定性状态
                        string controlStatus = _stabilityStatus.GetStatusReport();

                        // *** 状态跟踪 - 记录读取操作 ***
                        //if (success)
                        //{
                        //    AddActiveState(
                        //        stateName: $"ReadingReactorData_{deviceId}_{DateTime.Now:HHmmss}",
                        //        activateCommand: "反应器数据",
                        //        deactivateCommand: "停止读取",
                        //        stateData: new
                        //        {
                        //            DeviceId = deviceId,
                        //            TT1001_Actual = tt1001Actual,
                        //            TT1002_Actual = tt1002Actual,
                        //            TT1003_Actual = tt1003Actual,
                        //            PT0112 = pt0112,
                        //            TT0112 = tt0112,
                        //            StabilityStatus = _stabilityStatus,
                        //            ReadTime = DateTime.Now
                        //        }
                        //    );
                        //}
                    }
                    catch (Exception ex)
                    {
                        ErrorLog($"读取微反应器数据失败: {deviceId} - {ex.Message}");
                        _controlLogger.LogError($"读取微反应器数据失败: {ex.Message}", ex);
                        success = false;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("DeviceId", deviceId, "设备ID") { IsVisibleInVariableManager = true },
                            new NumberParameter("TT1001_ActualTemp", -100, 500, Math.Round(tt1001Actual, 2), "TT1001实际温度(℃)") { IsVisibleInVariableManager = true },
                            new NumberParameter("TT1002_ActualTemp", -100, 500, Math.Round(tt1002Actual, 2), "TT1002实际温度(℃)") { IsVisibleInVariableManager = true },
                            new NumberParameter("TT1003_ActualTemp", -100, 500, Math.Round(tt1003Actual, 2), "TT1003实际温度(℃)") { IsVisibleInVariableManager = true },
                            new NumberParameter("PT0112_Pressure", 0, 100, Math.Round(pt0112, 3), "出口压力(MPa)") { IsVisibleInVariableManager = true },
                            new NumberParameter("TT0112_Temperature", -100, 500, Math.Round(tt0112, 2), "出口温度(℃)") { IsVisibleInVariableManager = true },
                            new BooleanParameter("TT1001_Stable", _stabilityStatus.TT1001_Stable, "TT1001温度稳定") { IsVisibleInVariableManager = true },
                            new BooleanParameter("TT1002_Stable", _stabilityStatus.TT1002_Stable, "TT1002温度稳定") { IsVisibleInVariableManager = true },
                            new BooleanParameter("TT1003_Stable", _stabilityStatus.TT1003_Stable, "TT1003温度稳定") { IsVisibleInVariableManager = true },
                            new BooleanParameter("SystemStable", _stabilityStatus.IsSystemStable, "系统整体稳定") { IsVisibleInVariableManager = true },
                            new StringParameter("ControlStatus", _stabilityStatus.GetStatusReport(), "控制状态") { IsVisibleInVariableManager = true },
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
                HelpText = "停止智能自动温度控制",
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

                    bool tt1001Stopped = false, tt1002Stopped = false, tt1003Stopped = false, circulatorStopped = false;
                    bool success = false;

                    try
                    {
                        //停止循环器
                        _controlLogger.LogInfo("开始停止循环器");
                        circulatorStopped = await StopCirculator(circulatorId);

                        _controlLogger.LogInfo("开始停止温度控制");

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

                     

                        success = true;

                        // 重置稳定性状态
                        _stabilityStatus = new SystemStabilityStatus();

                        // *** 移除状态跟踪 ***
                        RemoveActiveState($"SmartTempControl_{deviceId}");

                        _controlLogger.LogSuccess("温度控制停止完成");
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

            if (_deviceData.TryGetValue(targetKey, out double targetTemp))
            {
                double currentTemp = _deviceData.TryGetValue(actualKey, out double existing) ? existing : 25.0;

                double tempDiff = targetTemp - currentTemp;
                double adjustmentRate = Math.Min(0.2, Math.Abs(tempDiff) * 0.05);
                double newTemp = currentTemp + Math.Sign(tempDiff) * adjustmentRate;

                newTemp += (random.NextDouble() - 0.5) * 0.3;

                return newTemp;
            }
            else
            {
                return 25.0 + (random.NextDouble() - 0.5) * 2;
            }
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
                    InfoLog($"启动循环器{circulatorId}的循环功能: {cycleCommand} = 1");
                    _controlLogger.LogInfo($"启动循环器{circulatorId}的循环功能: {cycleCommand} = 1");
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
                    InfoLog($"模拟模式：停止循环器{circulatorId}");
                    _controlLogger.LogInfo($"模拟模式：停止循环器{circulatorId}");
                    return true;
                }
                else
                {
                    await ConnectionManager.WriteAsync(circulatorId, GetCirculatorHeatingCommand(circulatorId), false);
                    await ConnectionManager.WriteAsync(circulatorId, GetCirculatorCoolingCommand(circulatorId), false);
                    await ConnectionManager.WriteAsync(circulatorId, GetCirculatorCycleCommand(circulatorId), false);
                    InfoLog($"停止循环器{circulatorId}所有功能");
                    _controlLogger.LogInfo($"停止循环器{circulatorId}所有功能");
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
            return circulatorId == "TC0101" ? "TempSet_1" : "TempSet_2";
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
                    "SmartTempControl",
                    DateTime.Now.ToString("yyyy-MM-dd")
                );

                Directory.CreateDirectory(logDirectory);

                // 创建以流程名和时间命名的日志文件
                string fileName = $"SmartControl_{flowName}_{DateTime.Now:HHmmss}.log";
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
                    _logWriter.WriteLine($"智能温度控制系统日志");
                    _logWriter.WriteLine($"流程名称: {flowName}");
                    _logWriter.WriteLine($"开始时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    _logWriter.WriteLine($"日志文件: {fileName}");
                    _logWriter.WriteLine("=".PadRight(80, '='));
                    _logWriter.WriteLine();
                }
            }
            catch (Exception ex)
            {
                // 如果日志初始化失败，至少要在系统日志中记录
                Console.WriteLine($"智能控制日志初始化失败: {ex.Message}");
            }
        }

        public void LogControlStart(string deviceId, string circulatorId)
        {
            WriteLog("INFO", $"🚀 启动智能温度控制系统", $"设备ID: {deviceId}, 循环器ID: {circulatorId}");
        }

        public void LogControllerStart(string sensorName, double targetTemp, double tolerance, int interval)
        {
            WriteLog("INFO", $"📊 启动{sensorName}控制器", $"目标温度: {targetTemp}℃, 容差: ±{tolerance}℃, 间隔: {interval}秒");
        }

        public void LogTemperatureReading(string sensorName, double actualTemp, double targetTemp, double error)
        {
            WriteLog("DATA", $"🌡️ 温度读取 {sensorName}", $"实际: {actualTemp:F2}℃, 目标: {targetTemp:F2}℃, 偏差: {error:F2}℃");
        }

        public void LogPhaseChange(string sensorName, string fromPhase, string toPhase, double currentTemp)
        {
            WriteLog("PHASE", $"🔄 {sensorName}阶段变化", $"{fromPhase} → {toPhase}, 当前温度: {currentTemp:F2}℃");
        }

        public void LogCirculatorControl(string circulatorId, double targetTemp, string mode, string reason)
        {
            WriteLog("CONTROL", $"🎛️ 循环器控制 {circulatorId}", $"设定温度: {targetTemp:F1}℃, 模式: {mode}, 原因: {reason}");
        }

        public void LogStabilityCheck(string sensorName, bool isStable, string details)
        {
            string emoji = isStable ? "✅" : "⏳";
            WriteLog("STABILITY", $"{emoji} {sensorName}稳定性检查", $"稳定: {(isStable ? "是" : "否")}, 详情: {details}");
        }

        public void LogSystemStable()
        {
            WriteLog("SUCCESS", "🎉 系统整体稳定", "所有温度传感器已达到稳定状态，可以开始进料反应");
        }

        public void LogReactionDetected(string sensorName, double tempChange, string reactionType)
        {
            WriteLog("REACTION", $"🧪 检测到反应热效应 {sensorName}", $"温度变化: {tempChange:F2}℃, 反应类型: {reactionType}");
        }

        public void LogPIDControl(string sensorName, double pidOutput, double kp, double ki, double kd)
        {
            WriteLog("PID", $"🎯 PID控制 {sensorName}", $"输出: {pidOutput:F3}, Kp:{kp:F2}, Ki:{ki:F3}, Kd:{kd:F3}");
        }

        public void LogControlCycle(string sensorName, int cycleCount, double avgError, double maxError)
        {
            WriteLog("CYCLE", $"🔁 控制周期 {sensorName}", $"第{cycleCount}次, 平均偏差: {avgError:F2}℃, 最大偏差: {maxError:F2}℃");
        }

        public void LogWarning(string message, string details = "")
        {
            WriteLog("WARN", $"⚠️ {message}", details);
        }

        public void LogError(string message, Exception ex = null)
        {
            string details = ex != null ? $"异常信息: {ex.Message}\n堆栈: {ex.StackTrace}" : "";
            WriteLog("ERROR", $"❌ {message}", details);
        }

        public void LogInfo(string message, string details = "")
        {
            WriteLog("INFO", $"ℹ️ {message}", details);
        }

        public void LogSuccess(string message, string details = "")
        {
            WriteLog("SUCCESS", $"✅ {message}", details);
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
                // 避免日志记录本身引发异常
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

    #region 智能温度控制器类（修改版 - 无volatile double）

    public enum TemperatureControlPhase
    {
        Initializing,     // 初始化阶段
        RapidHeat,        // 快速加热阶段
        ApproachTarget,   // 接近目标阶段  
        FineControl,      // 精细控制阶段
        Stabilizing,      // 稳定判断阶段
        Stable,           // 已稳定
        Overshoot,        // 过冲修正阶段
        Reacting          // 反应控制阶段
    }

    public enum CirculatorMode
    {
        Heating,        // 加热模式
        Cooling,        // 制冷模式
        Maintaining,    // 维持模式
        Idle           // 空闲模式
    }

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
            return $@"系统稳定性状态:
├─ TT1001: {(_tt1001Stable ? "✓ 稳定" : "⚠ 调节中")}
├─ TT1002: {(_tt1002Stable ? "✓ 稳定" : "⚠ 调节中")}
├─ TT1003: {(_tt1003Stable ? "✓ 稳定" : "⚠ 调节中")}
└─ 整体: {(IsSystemStable ? "🎉 全部稳定，可开始反应" : "⏳ 等待稳定中...")}";
        }
    }

    // 修复PID控制器的积分饱和问题
    public class TemperaturePIDController
    {
        private double _kp = 1.0;
        private double _ki = 0.1;
        private double _kd = 0.05;

        // 添加积分限制
        private const double MAX_INTEGRAL = 50.0;  // 限制积分项
        private const double MAX_OUTPUT = 100.0;   // 限制总输出

        private readonly object _pidLock = new object();
        private double _integral = 0;
        private double _lastError = 0;
        private DateTime _lastTime = DateTime.Now;

        public double Calculate(double target, double actual)
        {
            lock (_pidLock)
            {
                var now = DateTime.Now;
                double dt = (now - _lastTime).TotalSeconds;
                if (dt <= 0) dt = 0.1;

                double error = target - actual;

                // 比例项
                double P = _kp * error;

                // 积分项 - 添加防饱和保护
                _integral += error * dt;

                // 🔧 修复：限制积分项防止饱和
                if (_integral > MAX_INTEGRAL) _integral = MAX_INTEGRAL;
                if (_integral < -MAX_INTEGRAL) _integral = -MAX_INTEGRAL;

                double I = _ki * _integral;

                // 微分项
                double derivative = (error - _lastError) / dt;
                double D = _kd * derivative;

                _lastError = error;
                _lastTime = now;

                double output = P + I + D;

                // 🔧 修复：限制总输出
                if (output > MAX_OUTPUT) output = MAX_OUTPUT;
                if (output < -MAX_OUTPUT) output = -MAX_OUTPUT;

                return output;
            }
        }

        public void Reset()
        {
            lock (_pidLock)
            {
                _integral = 0;
                _lastError = 0;
                _lastTime = DateTime.Now;
            }
        }

        public void SetParameters(double kp, double ki, double kd)
        {
            lock (_pidLock)
            {
                _kp = kp;
                _ki = ki;
                _kd = kd;
            }
        }
    }

    public class SmartTemperatureController
    {
        private readonly string _deviceId;
        private readonly string _sensorName;
        private readonly double _targetTemperature;
        private readonly string _circulatorDeviceId;
        private readonly double _tolerance;
        private readonly TimeSpan _controlInterval;
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

        // 智能控制相关参数
        private const double ROOM_TEMPERATURE = 25.0;
        private const int STABLE_TIME_SECONDS = 30;
        private const double MAX_TEMP_CHANGE_RATE = 0.5;
        private const int MIN_STABLE_SAMPLES = 10;

        // 温度历史数据和状态 - 使用线程安全集合
        private readonly ConcurrentQueue<(DateTime time, double temp)> _tempHistory = new ConcurrentQueue<(DateTime, double)>();
        private volatile TemperatureControlPhase _currentPhase = TemperatureControlPhase.Initializing;
        private volatile CirculatorMode _currentMode = CirculatorMode.Idle;

        // 使用锁保护的double字段
        private readonly object _stateLock = new object();
        private double _currentCirculatorTemp = ROOM_TEMPERATURE;
        private int _controlCycleCount = 0;

        // PID控制器
        private readonly TemperaturePIDController _pidController = new TemperaturePIDController();
        // 🔧 新增：控制参数优化
        private const double APPROACH_ZONE_RATIO = 0.8;  // 80%时进入接近区域
        private const double FINE_ZONE_RATIO = 0.95;     // 95%时进入精细控制
        private const double MAX_CIRCULATOR_OFFSET = 15; // 循环器最大偏移
        private const double OVERSHOOT_THRESHOLD = 0.3;  // 过冲阈值

        // 🔧 新增：温度变化率监控
        private readonly Queue<(DateTime time, double temp)> _recentTemps = new Queue<(DateTime, double)>();
        private double _lastCirculatorTemp = ROOM_TEMPERATURE;
        // 🔧 新增：控制优化相关字段
        private double _lastSentCirculatorTemp = double.NaN;
        private CirculatorMode _lastSentMode = CirculatorMode.Idle;
        private DateTime _lastControlTime = DateTime.MinValue;
        private const double MIN_TEMP_CHANGE_THRESHOLD =1; // 最小温度变化阈值
        private const int MIN_CONTROL_INTERVAL_SECONDS = 2;   // 最小控制间隔

        public SmartTemperatureController(
            string deviceId,
            string sensorName,
            double targetTemperature,
            string circulatorDeviceId,
            double tolerance,
            TimeSpan controlInterval,
            IDeviceConnectionManager connectionManager,
            ILogService logger,
            SmartControlLogger controlLogger,
            bool isSimulationMode,
            ConcurrentDictionary<string, double> deviceData,
            SystemStabilityStatus stabilityStatus)
        {
            _deviceId = deviceId;
            _sensorName = sensorName;
            _targetTemperature = targetTemperature;
            _circulatorDeviceId = circulatorDeviceId;
            _tolerance = tolerance;
            _controlInterval = controlInterval;
            _connectionManager = connectionManager;
            _logger = logger;
            _controlLogger = controlLogger;
            _isSimulationMode = isSimulationMode;
            _deviceData = deviceData;
            _stabilityStatus = stabilityStatus;

            InitializePIDParameters();
        }

        private void InitializePIDParameters()
        {
            double tempDiff = Math.Abs(_targetTemperature - ROOM_TEMPERATURE);

            if (tempDiff > 50)
            {
                _pidController.SetParameters(2.0, 0.2, 0.1);
            }
            else if (tempDiff > 20)
            {
                _pidController.SetParameters(1.5, 0.15, 0.075);
            }
            else
            {
                _pidController.SetParameters(1.0, 0.1, 0.05);
            }

            _controlLogger.LogInfo($"初始化PID参数 {_sensorName}", $"温差: {tempDiff:F1}℃, 参数: Kp=2.0, Ki=0.2, Kd=0.1");
        }

        public async Task StartAsync()
        {
            if (_isRunning)
                return;

            _cancellationTokenSource = new CancellationTokenSource();
            _isRunning = true;
            _isPaused = false;

            _controlTask = Task.Run(async () => await SmartControlLoopAsync(_cancellationTokenSource.Token));

            _logger.LogInformation($"智能温度控制器启动: {_deviceId}_{_sensorName}, 目标温度: {_targetTemperature}℃");
            _controlLogger.LogInfo($"智能温度控制器启动 {_sensorName}", $"目标温度: {_targetTemperature}℃");
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

            int totalCycles;
            lock (_stateLock)
            {
                totalCycles = _controlCycleCount;
            }
            string cycleCommand = GetCirculatorCycleCommand();
            await _connectionManager.WriteAsync(_circulatorDeviceId, cycleCommand, false);
            _controlLogger.LogInfo($"循环器器停止: {cycleCommand}命令的值_{false}");
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

        private async Task SmartControlLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!_isPaused)
                    {
                        await PerformSmartTemperatureControlAsync();
                    }

                    await Task.Delay(_controlInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"智能温度控制循环异常: {_deviceId}_{_sensorName}");
                    _controlLogger.LogError($"智能温度控制循环异常 {_sensorName}: {ex.Message}", ex);
                    await Task.Delay(_controlInterval, cancellationToken);
                }
            }
        }

        private async Task PerformSmartTemperatureControlAsync()
        {
            int currentCycle;
            lock (_stateLock)
            {
                _controlCycleCount++;
                currentCycle = _controlCycleCount;
            }

            // 1. 读取当前温度
            double currentTemp = await ReadCurrentTemperatureAsync();
            double tempError = currentTemp - _targetTemperature;

            // 2. 更新温度历史和变化率
            var now = DateTime.Now;
            UpdateTemperatureHistory(now, currentTemp);
            double tempChangeRate = CalculateTemperatureChangeRate(currentTemp, now);

            // 3. 记录温度读取
            _controlLogger.LogTemperatureReading(_sensorName, currentTemp, _targetTemperature, tempError);

            // 4. 🔧 新的智能相位分析
            var previousPhase = _currentPhase;
            AnalyzeSmartControlPhase(currentTemp, tempError, tempChangeRate, now);

            if (previousPhase != _currentPhase)
            {
                _controlLogger.LogPhaseChange(_sensorName, previousPhase.ToString(), _currentPhase.ToString(), currentTemp);
            }

            // 5. 🔧 新的智能循环器温度计算
            double optimalCirculatorTemp = CalculateSmartCirculatorTemp(currentTemp, tempError, tempChangeRate);

            // 6. 🔧 重要改进：只在必要时执行控制
            bool shouldExecuteControl = ShouldExecuteControl(optimalCirculatorTemp, tempError, now);

            if (shouldExecuteControl)
            {
                await ExecuteSmartControlAsync(optimalCirculatorTemp, tempError);
                _lastControlTime = now;
                _lastSentCirculatorTemp = optimalCirculatorTemp;
            }
            else
            {
                _controlLogger.LogDebug($"跳过控制 {_sensorName}",
                    $"温度变化小于阈值，当前: {optimalCirculatorTemp:F1}℃, 上次: {_lastSentCirculatorTemp:F1}℃");
            }

            // 7. 更新模拟数据中的循环器温度（无论是否执行控制都要更新）
            if (_isSimulationMode)
            {
                string circulatorKey = $"{_deviceId}_{_sensorName}_CirculatorTemp";
                _deviceData[circulatorKey] = optimalCirculatorTemp;
            }

            // 8. 更新稳定性状态
            UpdateStabilityStatus(currentTemp, now);

            // 9. 记录控制状态
            if (currentCycle % 10 == 0)
            {
                LogControlCycleStatus(currentTemp, tempError, optimalCirculatorTemp, tempChangeRate);
            }
        }
        // 🔧 新增：判断是否需要执行控制的方法
        private bool ShouldExecuteControl(double newCirculatorTemp, double tempError, DateTime now)
        {
            // 情况1：首次控制
            if (double.IsNaN(_lastSentCirculatorTemp))
            {
                _controlLogger.LogInfo($"首次控制 {_sensorName}", $"设定循环器温度: {newCirculatorTemp:F1}℃");
                return true;
            }

            // 情况2：距离上次控制时间太短，跳过（除非是紧急情况）
            double timeSinceLastControl = (now - _lastControlTime).TotalSeconds;
            if (timeSinceLastControl < MIN_CONTROL_INTERVAL_SECONDS)
            {
                // 紧急情况：温度偏差很大
                if (Math.Abs(tempError) > _tolerance * 3)
                {
                    _controlLogger.LogWarning($"紧急控制 {_sensorName}",
                        $"温度偏差过大: {tempError:F2}℃, 强制执行控制");
                    return true;
                }
                return false;
            }

            // 情况3：循环器温度变化超过阈值
            double tempDiff = Math.Abs(newCirculatorTemp - _lastSentCirculatorTemp);
            if (tempDiff >= MIN_TEMP_CHANGE_THRESHOLD)
            {
                _controlLogger.LogInfo($"温度变化执行控制 {_sensorName}",
                    $"循环器温度变化: {tempDiff:F1}℃ (阈值: {MIN_TEMP_CHANGE_THRESHOLD}℃)");
                return true;
            }

            // 情况4：相位发生重要变化
            if (_currentPhase == TemperatureControlPhase.Overshoot ||
                _currentPhase == TemperatureControlPhase.RapidHeat ||
                _currentPhase == TemperatureControlPhase.Reacting)
            {
                _controlLogger.LogInfo($"相位变化执行控制 {_sensorName}",
                    $"当前相位: {_currentPhase}, 需要积极控制");
                return true;
            }

            // 情况5：温度偏差较大，需要调整
            if (Math.Abs(tempError) > _tolerance * 1.5)
            {
                _controlLogger.LogInfo($"偏差控制 {_sensorName}",
                    $"温度偏差: {tempError:F2}℃ > {_tolerance * 1.5:F2}℃");
                return true;
            }

            // 情况6：长时间未控制，执行定期检查
            if (timeSinceLastControl > 30) // 30秒未控制
            {
                _controlLogger.LogInfo($"定期控制检查 {_sensorName}",
                    $"距离上次控制: {timeSinceLastControl:F1}秒");
                return true;
            }

            // 其他情况：跳过控制
            return false;
        }


        // 🔧 新增：计算温度变化率
        private double CalculateTemperatureChangeRate(double currentTemp, DateTime now)
        {
            _recentTemps.Enqueue((now, currentTemp));

            // 保留最近30秒的数据
            while (_recentTemps.Count > 0 && (now - _recentTemps.Peek().time).TotalSeconds > 30)
            {
                _recentTemps.Dequeue();
            }

            if (_recentTemps.Count < 3) return 0;

            var temps = _recentTemps.ToArray();
            var timeSpan = (temps.Last().time - temps.First().time).TotalMinutes;
            if (timeSpan <= 0) return 0;

            var tempChange = temps.Last().temp - temps.First().temp;
            return tempChange / timeSpan; // ℃/min
        }

        // 🔧 重构：智能相位分析
        private void AnalyzeSmartControlPhase(double currentTemp, double tempError, double tempChangeRate, DateTime now)
        {
            double distanceToTarget = Math.Abs(tempError);
            double targetDistance = Math.Abs(_targetTemperature - ROOM_TEMPERATURE);
            bool isApproaching = Math.Sign(tempError) != Math.Sign(tempChangeRate) && Math.Abs(tempChangeRate) > 0.1;

            switch (_currentPhase)
            {
                case TemperatureControlPhase.Initializing:
                    _currentPhase = TemperatureControlPhase.RapidHeat;
                    break;

                case TemperatureControlPhase.RapidHeat:
                    // 当接近目标80%时，切换到接近模式
                    if (distanceToTarget <= targetDistance * (1 - APPROACH_ZONE_RATIO))
                    {
                        _currentPhase = TemperatureControlPhase.ApproachTarget;
                    }
                    break;

                case TemperatureControlPhase.ApproachTarget:
                    // 当接近目标95%时，切换到精细控制
                    if (distanceToTarget <= targetDistance * (1 - FINE_ZONE_RATIO) || distanceToTarget <= 2.0)
                    {
                        _currentPhase = TemperatureControlPhase.FineControl;
                    }
                    // 如果远离目标，回到快速加热
                    else if (distanceToTarget > targetDistance * (1 - APPROACH_ZONE_RATIO) * 1.2)
                    {
                        _currentPhase = TemperatureControlPhase.RapidHeat;
                    }
                    break;

                case TemperatureControlPhase.FineControl:
                    // 检查是否过冲
                    if (Math.Abs(tempError) > _tolerance && Math.Sign(tempError) != Math.Sign(_targetTemperature - ROOM_TEMPERATURE))
                    {
                        _currentPhase = TemperatureControlPhase.Overshoot;
                        _controlLogger.LogWarning($"检测到温度过冲 {_sensorName}", $"偏差: {tempError:F2}℃");
                    }
                    // 进入容差范围，开始稳定判断
                    else if (Math.Abs(tempError) <= _tolerance)
                    {
                        _currentPhase = TemperatureControlPhase.Stabilizing;
                    }
                    break;

                case TemperatureControlPhase.Overshoot:
                    // 过冲修正完成，回到精细控制
                    if (Math.Abs(tempError) <= _tolerance * 2 && Math.Abs(tempChangeRate) < 0.5)
                    {
                        _currentPhase = TemperatureControlPhase.FineControl;
                    }
                    break;

                case TemperatureControlPhase.Stabilizing:
                    if (Math.Abs(tempError) > _tolerance)
                    {
                        _currentPhase = TemperatureControlPhase.FineControl;
                    }
                    else if (IsTemperatureStable(currentTemp, now))
                    {
                        _currentPhase = TemperatureControlPhase.Stable;
                        _controlLogger.LogSuccess($"温度已稳定 {_sensorName}", $"当前温度: {currentTemp:F2}℃");
                    }
                    break;

                case TemperatureControlPhase.Stable:
                    if (Math.Abs(tempError) > _tolerance * 1.5)
                    {
                        _currentPhase = TemperatureControlPhase.Reacting;
                        string reactionType = tempError < 0 ? "吸热反应" : "放热反应";
                        _controlLogger.LogReactionDetected(_sensorName, tempError, reactionType);
                    }
                    break;

                case TemperatureControlPhase.Reacting:
                    if (Math.Abs(tempError) <= _tolerance && IsTemperatureStable(currentTemp, now))
                    {
                        _currentPhase = TemperatureControlPhase.Stable;
                        _controlLogger.LogSuccess($"反应温度重新稳定 {_sensorName}");
                    }
                    break;
            }
        }

        // 🔧 重构：智能循环器温度计算
        private double CalculateSmartCirculatorTemp(double currentTemp, double tempError, double tempChangeRate)
        {
            double baseCirculatorTemp = _targetTemperature;

            switch (_currentPhase)
            {
                case TemperatureControlPhase.RapidHeat:
                    // 快速加热：循环器温度略高于目标，但不过分
                    double heatBoost = Math.Min(MAX_CIRCULATOR_OFFSET, Math.Abs(tempError) * 0.3);
                    baseCirculatorTemp = _targetTemperature + heatBoost;
                    break;

                case TemperatureControlPhase.ApproachTarget:
                    // 接近目标：根据距离和变化率动态调整
                    double approachOffset = Math.Abs(tempError) * 0.5;

                    // 🔧 关键：预测性制动
                    if (tempChangeRate > 1.0 && Math.Abs(tempError) < 3.0)
                    {
                        // 温度上升过快且接近目标，提前制动
                        approachOffset *= 0.3;
                        _controlLogger.LogInfo($"预测性制动 {_sensorName}", $"变化率: {tempChangeRate:F2}℃/min, 减小加热");
                    }

                    baseCirculatorTemp = _targetTemperature + Math.Sign(tempError) * approachOffset;
                    break;

                case TemperatureControlPhase.FineControl:
                    // 精细控制：限制范围的PID
                    double pidOutput = _pidController.Calculate(_targetTemperature, currentTemp);
                    pidOutput = Math.Max(-8, Math.Min(8, pidOutput)); // 限制在±8℃
                    baseCirculatorTemp = _targetTemperature + pidOutput;
                    break;

                case TemperatureControlPhase.Overshoot:
                    // 过冲修正：积极反向控制
                    double correctionOffset = Math.Min(MAX_CIRCULATOR_OFFSET, Math.Abs(tempError) * 1.5);
                    baseCirculatorTemp = _targetTemperature - Math.Sign(tempError) * correctionOffset;
                    _controlLogger.LogInfo($"过冲修正 {_sensorName}", $"偏差: {tempError:F2}℃, 循环器: {baseCirculatorTemp:F1}℃");
                    break;

                case TemperatureControlPhase.Stabilizing:
                    // 稳定阶段：微调
                    double stableOffset = Math.Abs(tempError) * 2;
                    baseCirculatorTemp = _targetTemperature + Math.Sign(tempError) * Math.Min(3, stableOffset);
                    break;

                case TemperatureControlPhase.Stable:
                    // 已稳定：保持当前设置
                    lock (_stateLock)
                    {
                        baseCirculatorTemp = _currentCirculatorTemp;
                    }
                    break;

                case TemperatureControlPhase.Reacting:
                    // 反应控制：快速补偿
                    double reactionOffset = Math.Min(10, Math.Abs(tempError) * 1.2);
                    baseCirculatorTemp = _targetTemperature + Math.Sign(tempError) * reactionOffset;
                    break;
            }

            // 🔧 防止循环器温度突变
            double tempDiff = baseCirculatorTemp - _lastCirculatorTemp;
            if (Math.Abs(tempDiff) > 10) // 单次最大变化10℃
            {
                baseCirculatorTemp = _lastCirculatorTemp + Math.Sign(tempDiff) * 10;
                _controlLogger.LogInfo($"限制循环器变化 {_sensorName}", $"限制变化: {tempDiff:F1}℃ → 10℃");
            }

            // 限制循环器温度范围
            baseCirculatorTemp = Math.Max(_targetTemperature - 30, Math.Min(_targetTemperature + 30, baseCirculatorTemp));

            _lastCirculatorTemp = baseCirculatorTemp;
            return baseCirculatorTemp;
        }
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
                    double simulatedTemp = ROOM_TEMPERATURE + new Random().NextDouble() * 5;
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
                    _logger.LogError(ex, $"读取温度传感器失败: {_deviceId}_{_sensorName}");
                    _controlLogger.LogError($"读取温度传感器失败 {_sensorName}", ex);
                    return _targetTemperature;
                }
            }
        }

        private void UpdateTemperatureHistory(DateTime now, double currentTemp)
        {
            _tempHistory.Enqueue((now, currentTemp));

            // 清理过期数据
            while (_tempHistory.TryPeek(out var oldest) &&
                   (now - oldest.time).TotalSeconds > STABLE_TIME_SECONDS * 2)
            {
                _tempHistory.TryDequeue(out _);
            }
        }

        // 🔧 改进：更智能的ExecuteSmartControlAsync
        private async Task ExecuteSmartControlAsync(double targetCirculatorTemp, double tempError)
        {
            double currentCirculator;
            lock (_stateLock)
            {
                currentCirculator = _currentCirculatorTemp;
            }

            double tempDiff = targetCirculatorTemp - currentCirculator;

            // 🔧 改进：更智能的模式判断
            CirculatorMode newMode = DetermineCirculatorMode(targetCirculatorTemp, tempError, tempDiff);

            // 🔧 只在模式或温度确实需要改变时才发送指令
            bool needTempUpdate = Math.Abs(targetCirculatorTemp - _lastSentCirculatorTemp) >= MIN_TEMP_CHANGE_THRESHOLD;
            bool needModeUpdate = newMode != _lastSentMode;

            if (needTempUpdate || needModeUpdate)
            {
                string reason = $"相位: {_currentPhase}, 温度偏差: {tempError:F2}℃";
                _controlLogger.LogCirculatorControl(_circulatorDeviceId, targetCirculatorTemp, newMode.ToString(), reason);

                // 只在需要时发送温度设定
                if (needTempUpdate)
                {
                    await SetCirculatorTemperatureAsync(targetCirculatorTemp);
                    _controlLogger.LogInfo($"更新循环器温度 {_sensorName}",
                        $"从 {_lastSentCirculatorTemp:F1}℃ 到 {targetCirculatorTemp:F1}℃");
                }

                // 只在需要时发送模式设定
                if (needModeUpdate)
                {
                    //await SetCirculatorModeAsync(newMode);
                    _controlLogger.LogInfo($"更新循环器模式 {_sensorName}",
                        $"从 {_lastSentMode} 到 {newMode}");
                    _lastSentMode = newMode;
                }

                lock (_stateLock)
                {
                    _currentCirculatorTemp = targetCirculatorTemp;
                }
                _currentMode = newMode;
            }
            else
            {
                _controlLogger.LogDebug($"控制状态无变化 {_sensorName}",
                    $"循环器: {targetCirculatorTemp:F1}℃, 模式: {newMode}");
            }
        }
        // 🔧 新增：智能确定循环器模式
        private CirculatorMode DetermineCirculatorMode(double targetCirculatorTemp, double tempError, double tempDiff)
        {
            // 根据当前相位和温度差异确定模式
            switch (_currentPhase)
            {
                case TemperatureControlPhase.RapidHeat:
                    return tempError < 0 ? CirculatorMode.Heating : CirculatorMode.Cooling;

                case TemperatureControlPhase.Overshoot:
                    // 过冲时需要快速反向控制
                    return tempError > 0 ? CirculatorMode.Cooling : CirculatorMode.Heating;

                case TemperatureControlPhase.Reacting:
                    // 反应控制时需要快速响应
                    return tempError < 0 ? CirculatorMode.Heating : CirculatorMode.Cooling;

                case TemperatureControlPhase.Stable:
                    // 稳定状态时保持维持模式
                    return CirculatorMode.Maintaining;

                default:
                    // 其他情况根据温度差异判断
                    if (Math.Abs(tempDiff) <= 1.0)
                    {
                        return CirculatorMode.Maintaining;
                    }
                    else if (tempDiff > 0)
                    {
                        return CirculatorMode.Heating;
                    }
                    else
                    {
                        return CirculatorMode.Cooling;
                    }
            }
        }
        private async Task SetCirculatorTemperatureAsync(double temperature)
        {
            try
            {
                if (_isSimulationMode)
                {
                    // 模拟模式下不需要记录每次设置
                }
                else
                {
                    string tempSetCommand = GetCirculatorTempSetCommand();
                    await _connectionManager.WriteAsync(_circulatorDeviceId, tempSetCommand, temperature);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"设置循环器温度失败: {_circulatorDeviceId}");
                _controlLogger.LogError($"设置循环器温度失败: {_circulatorDeviceId}", ex);
            }
        }

        private async Task SetCirculatorModeAsync(CirculatorMode mode)
        {
            try
            {
                if (_isSimulationMode)
                {
                    return;
                }

                string heatCommand = GetCirculatorHeatingCommand();
                string coolCommand = GetCirculatorCoolingCommand();
                string cycleCommand = GetCirculatorCycleCommand();

                //await _connectionManager.WriteAsync(_circulatorDeviceId, cycleCommand, true);

                switch (mode)
                {
                    case CirculatorMode.Heating:
                        await _connectionManager.WriteAsync(_circulatorDeviceId, heatCommand, true);
                        await _connectionManager.WriteAsync(_circulatorDeviceId, coolCommand, false);
                        break;

                    case CirculatorMode.Cooling:
                        await _connectionManager.WriteAsync(_circulatorDeviceId, heatCommand, false);
                        await _connectionManager.WriteAsync(_circulatorDeviceId, coolCommand, true);
                        break;

                    case CirculatorMode.Maintaining:
                    case CirculatorMode.Idle:
                        //await _connectionManager.WriteAsync(_circulatorDeviceId, heatCommand, 0);
                        //await _connectionManager.WriteAsync(_circulatorDeviceId, coolCommand, 0);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"设置循环器模式失败: {_circulatorDeviceId}");
                _controlLogger.LogError($"设置循环器模式失败: {_circulatorDeviceId}", ex);
            }
        }

        private bool IsTemperatureStable(double currentTemp, DateTime now)
        {
            var tempArray = _tempHistory.ToArray();

            if (tempArray.Length < MIN_STABLE_SAMPLES)
            {
                _controlLogger.LogStabilityCheck(_sensorName, false, $"数据点不足: {tempArray.Length}/{MIN_STABLE_SAMPLES}");
                return false;
            }

            var oldestTime = tempArray[0].time;
            if ((now - oldestTime).TotalSeconds < STABLE_TIME_SECONDS)
            {
                _controlLogger.LogStabilityCheck(_sensorName, false, $"时间不足: {(now - oldestTime).TotalSeconds:F1}s/{STABLE_TIME_SECONDS}s");
                return false;
            }

            bool allInRange = tempArray.All(point =>
                Math.Abs(point.temp - _targetTemperature) <= _tolerance);

            if (!allInRange)
            {
                _controlLogger.LogStabilityCheck(_sensorName, false, "存在超出容差范围的数据点");
                return false;
            }

            if (tempArray.Length >= 2)
            {
                var maxTemp = tempArray.Max(p => p.temp);
                var minTemp = tempArray.Min(p => p.temp);
                var timeSpan = (tempArray.Last().time - tempArray.First().time).TotalMinutes;

                if (timeSpan > 0)
                {
                    var changeRate = Math.Abs(maxTemp - minTemp) / timeSpan;
                    if (changeRate > MAX_TEMP_CHANGE_RATE)
                    {
                        _controlLogger.LogStabilityCheck(_sensorName, false, $"变化率过大: {changeRate:F2}℃/min > {MAX_TEMP_CHANGE_RATE}℃/min");
                        return false;
                    }
                }
            }

            string details = $"数据点: {tempArray.Length}, 时间: {(now - oldestTime).TotalSeconds:F1}s, 范围: {tempArray.Min(p => p.temp):F2}-{tempArray.Max(p => p.temp):F2}℃";
            _controlLogger.LogStabilityCheck(_sensorName, true, details);
            return true;
        }

        private void UpdateStabilityStatus(double currentTemp, DateTime now)
        {
            bool isStable = _currentPhase == TemperatureControlPhase.Stable &&
                           IsTemperatureStable(currentTemp, now);

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
                _controlLogger.LogSuccess($"{_sensorName}达到稳定状态", $"当前温度: {currentTemp:F2}℃");
            }
            else if (wasStable && !isStable)
            {
                _controlLogger.LogWarning($"{_sensorName}失去稳定状态", $"当前温度: {currentTemp:F2}℃");
            }

            // 检查系统整体稳定性
            if (_stabilityStatus.IsSystemStable)
            {
                _controlLogger.LogSystemStable();
            }
        }

        private void LogControlCycleStatus(double currentTemp, double tempError, double circulatorTemp, double changeRate)
        {
            var tempArray = _tempHistory.ToArray();
            if (tempArray.Length > 5)
            {
                var recentErrors = tempArray.TakeLast(5).Select(p => Math.Abs(p.temp - _targetTemperature));
                double avgError = recentErrors.Average();
                double maxError = recentErrors.Max();

                int currentCycle;
                lock (_stateLock)
                {
                    currentCycle = _controlCycleCount;
                }

                _controlLogger.LogControlCycle(_sensorName, currentCycle, avgError, maxError);
            }

            _controlLogger.LogDebug($"智能控制状态 {_sensorName}",
                $"当前: {currentTemp:F2}℃, 偏差: {tempError:F2}℃, 变化率: {changeRate:F2}℃/min, 阶段: {_currentPhase}, 循环器: {circulatorTemp:F1}℃");
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

        private string GetCirculatorTempSetCommand()
        {
            return _circulatorDeviceId == "TC0101" ? "YY_SV_Set_1" : "YY_SV_Set_2";
        }
    }

    #endregion
}