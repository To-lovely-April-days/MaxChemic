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
    /// <summary>
    /// 微反应器 (ModbusTCP) - 重构版
    /// 
    /// 核心架构:
    /// 1. 单 MasterSlaveTemperatureController 替代原来的 3 个独立 controller
    /// 2. 主从控制策略: UI 选主控传感器,其他作为监测路
    /// 3. 工艺偏置起步策略,不再用 98%
    /// 4. 微调累积上次循环器值,具有积分性质
    /// 5. 失稳迟滞 + 失控保护 + 安全监测三级报警(收敛性判据)
    /// 6. 流程结束不停控制器(跨流程持续运行)
    /// 7. 同一批物料分阶段升温:平滑切换不停循环器
    /// </summary>
    [Export(typeof(IDevice))]
    public class MicroReactor_ModbusTCP : Device, IControllableDevice, IFlowLifecycleAware
    {
        private readonly ILogService _logger;
        private readonly SmartControlLogger _controlLogger;

        private readonly ConcurrentDictionary<string, double> _deviceData
            = new ConcurrentDictionary<string, double>();

        private const string UnitTemperature = "℃";
        private const string UnitPressure = "MPa";

        private MasterSlaveTemperatureController _activeController;
        private readonly object _controllerLock = new object();

        private volatile SystemStabilityStatus _stabilityStatus = new SystemStabilityStatus();

        private CancellationTokenSource _temperaturePollingCts;
        private Task _temperaturePollingTask;
        private static readonly TimeSpan TemperaturePollingInterval = TimeSpan.FromSeconds(2);

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

            ImageLocation = "CUSTOM_CONTROL:MicroreactorControl";

            Parameters.Variables.Add(new StringParameter("DeviceId", "R0101")
            {
                Options = new ObservableCollection<string>(new List<string>()
                {
                    "R0101", "R0201"
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

            Parameters.Variables.Add(new StringParameter("MasterSensor", "TT1002")
            {
                Options = new ObservableCollection<string>(new List<string>()
                {
                    "TT1001", "TT1002", "TT1003"
                }),
                HelpText = "主控传感器:控制器跟踪此路温度,其他两路作为监测使用"
            });

            InitializeCommands();
        }

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
                states["IsStable"] = _stabilityStatus.IsSystemStable;
            }

            lock (_controllerLock)
            {
                if (_activeController != null)
                {
                    states["ControlPhase"] = _activeController.CurrentPhase.ToString();
                    states["MasterSensor"] = _activeController.MasterSensor;
                    states["ControlPhaseInfo"] = _activeController.GetCurrentStateInfo();
                    if (_activeController.CurrentPhase == ControlPhase.Failed)
                    {
                        states["FailureReason"] = _activeController.FailureReason;
                    }
                }
                else
                {
                    states["ControlPhase"] = "未启动";
                    states["IsStable"] = false;
                }
            }

            states["ComId"] = this.ComId;
            states["DeviceId"] = this.DeviceId;
            states["IsConnected"] = this.IsConnected;

            return new DeviceState { States = states };
        }

        #region IControllableDevice Implementation

        public override async Task<bool> PauseAllActiveStatesAsync()
        {
            lock (_controllerLock)
            {
                if (_activeController == null)
                {
                    InfoLog("没有活跃的温度控制器需要暂停");
                    return true;
                }
            }

            try
            {
                await _activeController.PauseAsync();
                _controlLogger.LogInfo("温度控制器已暂停");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("暂停温度控制器失败", ex);
                return false;
            }
        }

        public override async Task<bool> ResumeAllPausedStatesAsync()
        {
            MasterSlaveTemperatureController controller;
            lock (_controllerLock)
            {
                controller = _activeController;
            }

            if (controller == null)
            {
                InfoLog("没有暂停的温度控制器需要恢复");
                return true;
            }

            try
            {
                await controller.ResumeAsync();
                _controlLogger.LogInfo("温度控制器已恢复");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("恢复温度控制器失败", ex);
                return false;
            }
        }

        public override async Task<bool> StopAllActiveStatesAsync()
        {
            MasterSlaveTemperatureController controller;
            lock (_controllerLock)
            {
                controller = _activeController;
                _activeController = null;
            }

            await StopTemperaturePollingAsync();

            if (controller != null)
            {
                try
                {
                    await controller.StopAsync();
                    _controlLogger.LogInfo("温度控制器已停止");
                }
                catch (Exception ex)
                {
                    ErrorLog("停止温度控制器异常", ex);
                }
            }

            _controlLogger?.Close();
            return true;
        }

        #endregion

        #region IFlowLifecycleAware Implementation

        public virtual async Task<bool> OnFlowStartedAsync(FlowStartContext context)
        {
            try
            {
                InfoLog($"流程开始: {context.FlowName}");

                _controlLogger.Close();
                _controlLogger.Initialize(context.FlowName);

                StartTemperaturePolling();

                InfoLog("微反应器准备好执行流程,温度轮询已启动");

                lock (_controllerLock)
                {
                    if (_activeController != null)
                    {
                        _controlLogger.LogInfo("检测到已有运行中的温度控制器",
                            $"阶段:{_activeController.CurrentPhase}, 主控:{_activeController.MasterSensor}");
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("流程开始处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowCompletedAsync(FlowCompletionContext context)
        {
            try
            {
                InfoLog($"流程完成: {context.FlowName}, 成功:{context.IsSuccessful}");

                lock (_controllerLock)
                {
                    if (_activeController != null)
                    {
                        _controlLogger.LogInfo("流程已结束,温度控制继续后台运行",
                            $"阶段:{_activeController.CurrentPhase}, 主控:{_activeController.MasterSensor}");
                    }
                }

                bool hasRunning;
                lock (_controllerLock) { hasRunning = _activeController != null; }
                if (hasRunning)
                {
                    _controlLogger.Close();
                    _controlLogger.Initialize($"{context.FlowName}_BackgroundContinue");
                    _controlLogger.LogInfo("== 后台运行日志开始 ==", "流程已结束,但温度控制继续");
                }

                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("流程完成处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowFailedAsync(FlowFailureContext context)
        {
            try
            {
                ErrorLog($"流程失败: {context.FlowName}, 错误:{context.ErrorMessage}");
                _controlLogger?.LogError($"流程失败: {context.ErrorMessage}", null);

                await StopAllActiveStatesAsync();

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

        // ============================================================
        // 命令初始化
        // ============================================================
        private void InitializeCommands()
        {
            InitializeIntelligentSetTemperatureCommand();
            InitializeReadDataCommand();
            InitializeStopControlCommand();
        }

        // ============================================================
        // 命令 1: 智能设置温度
        // ============================================================
        private void InitializeIntelligentSetTemperatureCommand()
        {
            Commands.Add(new DeviceCommand
            {
                Name = "智能设置温度",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/temp_set.png",
                HelpText = "主从智能温度控制:单一控制器跟踪主控传感器,其他作为监测路。" +
                           "具备工艺偏置起步、循环器命令限幅、收敛性判据、安全监测三级报警。" +
                           "支持平滑切换目标温度(同一批物料分阶段升温)。",

                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        // ============ 工艺参数 ============
                        new NumberParameter("TargetTemperature", -100, 500, 25, "目标温度(℃)")
                        {
                            HelpText = "三路温度的统一目标值。三路必须设相同值(物理上一个循环器无法支持不同目标)"
                        },
                        new NumberParameter("Tolerance", 0.1, 5.0, 0.5, "温度容差(℃)")
                        {
                            HelpText = "稳定容差:主控温度在 ±此值内即视为达标。失稳阈值=3*容差(迟滞)"
                        },
                        new NumberParameter("ReadingInterval", 2, 10, 3, "读数间隔(秒)")
                        {
                            HelpText = "温度读取的时间间隔,建议 2~5 秒"
                        },
                        new NumberParameter("StabilityTime", 10, 60, 20, "稳定检测时间(秒)")
                        {
                            HelpText = "判断稳定所需的最少持续时间,建议 15~30 秒"
                        },
                        new NumberParameter("FineAdjustStep", 0.1, 2.0, 0.5, "微调步长(℃)")
                        {
                            HelpText = "微调时循环器温度的基础调整步长,根据偏差自适应放大"
                        },

                        // ============ 工艺偏置参数 ============
                        new NumberParameter("WarmingBias", 0, 30, 5, "升温偏置(℃)")
                        {
                            HelpText = "升温时:循环器目标 = 物料目标 + 此值。预留传热损失"
                        },
                        new NumberParameter("CoolingBias", 0, 30, 5, "降温偏置(℃)")
                        {
                            HelpText = "降温时:循环器目标 = 物料目标 - 此值。预留传热损失"
                        },

                        // ============ [安全] 参数 ============
                        new NumberParameter("[安全]循环器最大偏置", 5, 50, 15, "[安全]循环器命令限幅(℃)")
                        {
                            HelpText = "循环器命令值不允许超出物料目标 ± 此值的范围,防止控制器算错把循环器拉飞"
                        },
                        new NumberParameter("[安全]大偏差报警阈值", 5, 50, 10, "[安全]大偏差报警阈值(℃)")
                        {
                            HelpText = "物料偏离目标超过此值,且收敛性异常时才触发报警(配合下面的检查窗口)"
                        },
                        new NumberParameter("[安全]收敛性检查窗口", 60, 600, 120, "[安全]收敛性检查窗口(秒)")
                        {
                            HelpText = "偏差超过阈值且不再缩小持续此时间 → 报警。" +
                                       "升温过程中偏差缩小是正常的,不会触发"
                        },
                        new NumberParameter("[安全]温度速率上限", 1, 50, 10, "[安全]温度变化速率上限(℃/分钟)")
                        {
                            HelpText = "物料温度变化速率超过此值 → 触发报警(可能升温过快)"
                        },
                        new NumberParameter("[安全]监测路偏差", 5, 30, 10, "[安全]监测路偏差报警(℃)")
                        {
                            HelpText = "监测传感器与主控偏差超过此值 → 仅报警不停止控制"
                        },
                        new NumberParameter("[安全]最大微调次数", 5, 100, 20, "[安全]最大微调次数")
                        {
                            HelpText = "微调累计达到此次数仍未稳定 → 进入失败状态(系统未收敛)"
                        }
                    }
                },

                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("DeviceId", "", "设备ID") { IsVisibleInVariableManager = true },
                        new StringParameter("CirculatorId", "", "循环器ID") { IsVisibleInVariableManager = true },
                        new StringParameter("MasterSensor", "", "主控传感器") { IsVisibleInVariableManager = true },
                        new NumberParameter("TargetTemp", -100, 500, 0, "目标温度", true, UnitTemperature),
                        new BooleanParameter("ControlStarted", false, "智能控制启动") { IsVisibleInVariableManager = true },
                        new BooleanParameter("Success", false, "命令成功") { IsVisibleInVariableManager = true },
                        new StringParameter("Message", "", "执行说明") { IsVisibleInVariableManager = true }
                    }
                },

                AsyncAction = async (DeviceParameters parameters) =>
                {
                    return await ExecuteIntelligentSetTemperatureAsync(parameters);
                }
            });
        }

        private async Task<DeviceParameters> ExecuteIntelligentSetTemperatureAsync(DeviceParameters parameters)
        {
            ComId = Parameters.GetValue<string>("DeviceId");
            string deviceId = ComId;
            string circulatorId = Parameters.GetValue<string>("CirculatorDeviceId");
            string masterSensor = Parameters.GetValue<string>("MasterSensor");

            double targetTemp = parameters.GetValue<double>("TargetTemperature");
            double tolerance = parameters.GetValue<double>("Tolerance");
            int readingInterval = (int)parameters.GetValue<double>("ReadingInterval");
            int stabilityTime = (int)parameters.GetValue<double>("StabilityTime");
            double fineAdjustStep = parameters.GetValue<double>("FineAdjustStep");
            double warmingBias = parameters.GetValue<double>("WarmingBias");
            double coolingBias = parameters.GetValue<double>("CoolingBias");

            double maxBias = parameters.GetValue<double>("[安全]循环器最大偏置");
            double alarmDeviation = parameters.GetValue<double>("[安全]大偏差报警阈值");
            double alarmDuration = parameters.GetValue<double>("[安全]收敛性检查窗口");
            double maxRate = parameters.GetValue<double>("[安全]温度速率上限");
            double monitorMaxDev = parameters.GetValue<double>("[安全]监测路偏差");
            int maxFineAdjust = (int)parameters.GetValue<double>("[安全]最大微调次数");

            bool overallSuccess = false;
            string message = "";

            try
            {
                InfoLog($"启动主从智能温度控制: 设备={deviceId}, 循环器={circulatorId}, 主控={masterSensor}, 目标={targetTemp}℃");
                _controlLogger.LogControlStart(deviceId, circulatorId);

                // 1. 处理已有控制器
                MasterSlaveTemperatureController existingController;
                lock (_controllerLock)
                {
                    existingController = _activeController;
                }

                if (existingController != null)
                {
                    // 检查是否能平滑切换
                    bool canSmoothSwitch =
                        existingController.MasterSensor == masterSensor &&
                        existingController.CurrentPhase != ControlPhase.Stopped &&
                        existingController.CurrentPhase != ControlPhase.Failed;

                    if (canSmoothSwitch)
                    {
                        _controlLogger.LogInfo("检测到运行中的控制器,执行平滑切换",
                            $"目标温度 → {targetTemp:F1}℃");

                        bool ok = await existingController.ChangeTargetAsync(targetTemp);
                        if (ok)
                        {
                            return new DeviceParameters
                            {
                                Variables = new ObservableCollection<ParameterBase>
                                {
                                    new StringParameter("DeviceId", deviceId, "设备ID") { IsVisibleInVariableManager = true },
                                    new StringParameter("CirculatorId", circulatorId, "循环器ID") { IsVisibleInVariableManager = true },
                                    new StringParameter("MasterSensor", masterSensor, "主控传感器") { IsVisibleInVariableManager = true },
                                    new NumberParameter("TargetTemp", -100, 500, Math.Round(targetTemp, 2), "目标温度", true, UnitTemperature),
                                    new BooleanParameter("ControlStarted", true, "智能控制启动") { IsVisibleInVariableManager = true },
                                    new BooleanParameter("Success", true, "命令成功") { IsVisibleInVariableManager = true },
                                    new StringParameter("Message", $"平滑切换成功 - 主控{masterSensor}, 新目标{targetTemp:F1}℃", "执行说明") { IsVisibleInVariableManager = true }
                                }
                            };
                        }
                        else
                        {
                            _controlLogger.LogWarning("平滑切换失败,降级为停止+重启");
                        }
                    }
                    else
                    {
                        _controlLogger.LogInfo("控制器条件不允许平滑切换",
                            $"主控:{existingController.MasterSensor} vs {masterSensor}, 阶段:{existingController.CurrentPhase}");
                    }

                    // 不能平滑切换 → 停止旧控制器
                    lock (_controllerLock)
                    {
                        _activeController = null;
                    }
                    await existingController.StopAsync(stopCirculator: true);
                }

                // 2. 启动循环器各功能
                _controlLogger.LogInfo("开始按顺序启动循环器功能");
                await EnsureCirculatorCycleRunning(circulatorId);
                await Task.Delay(3000);

                if (!IsSimulationMode)
                {
                    string heatCmd = GetCirculatorHeatingCommand(circulatorId);
                    await ConnectionManager.WriteAsync(circulatorId, heatCmd, true);
                    InfoLog($"启动循环器加热: {heatCmd}=true");
                    await Task.Delay(500);

                    string coolCmd = GetCirculatorCoolingCommand(circulatorId);
                    await ConnectionManager.WriteAsync(circulatorId, coolCmd, true);
                    InfoLog($"启动循环器制冷: {coolCmd}=true");
                    await Task.Delay(500);
                }

                _controlLogger.LogSuccess($"循环器 {circulatorId} 全部功能已启动");

                // 3. 创建配置
                var config = new MasterSlaveControllerConfig
                {
                    TargetTemperature = targetTemp,
                    MasterSensor = masterSensor,
                    Tolerance = tolerance,
                    ReadingInterval = TimeSpan.FromSeconds(readingInterval),
                    StabilityTime = TimeSpan.FromSeconds(stabilityTime),
                    FineAdjustStep = fineAdjustStep,
                    WarmingBias = warmingBias,
                    CoolingBias = coolingBias,
                    MaxBias = maxBias,
                    AlarmDeviationDeg = alarmDeviation,
                    AlarmDurationSec = alarmDuration,
                    MaxRateDegPerMin = maxRate,
                    MonitorMaxDeviation = monitorMaxDev,
                    MaxFineAdjustments = maxFineAdjust
                };

                // 4. 创建并启动 controller
                var controller = new MasterSlaveTemperatureController(
                    deviceId: deviceId,
                    circulatorDeviceId: circulatorId,
                    config: config,
                    connectionManager: ConnectionManager,
                    logger: _logger,
                    controlLogger: _controlLogger,
                    isSimulationMode: IsSimulationMode,
                    deviceData: _deviceData,
                    stabilityStatus: _stabilityStatus
                );

                await controller.StartAsync();

                lock (_controllerLock)
                {
                    _activeController = controller;
                }

                _controlLogger.LogControllerStart(masterSensor, targetTemp, tolerance, readingInterval);
                _controlLogger.LogSuccess("主从智能温度控制启动成功");

                overallSuccess = true;
                message = $"启动成功 - 主控{masterSensor}, 目标{targetTemp:F1}℃";
            }
            catch (Exception ex)
            {
                ErrorLog($"启动主从智能温度控制失败: {ex.Message}");
                _controlLogger.LogError($"启动失败: {ex.Message}", ex);
                overallSuccess = false;
                message = $"启动失败: {ex.Message}";
            }

            return new DeviceParameters
            {
                Variables = new ObservableCollection<ParameterBase>
                {
                    new StringParameter("DeviceId", deviceId, "设备ID") { IsVisibleInVariableManager = true },
                    new StringParameter("CirculatorId", circulatorId, "循环器ID") { IsVisibleInVariableManager = true },
                    new StringParameter("MasterSensor", masterSensor, "主控传感器") { IsVisibleInVariableManager = true },
                    new NumberParameter("TargetTemp", -100, 500, Math.Round(targetTemp, 2), "目标温度", true, UnitTemperature),
                    new BooleanParameter("ControlStarted", overallSuccess, "智能控制启动") { IsVisibleInVariableManager = true },
                    new BooleanParameter("Success", overallSuccess, "命令成功") { IsVisibleInVariableManager = true },
                    new StringParameter("Message", message, "执行说明") { IsVisibleInVariableManager = true }
                }
            };
        }

        // ============================================================
        // 命令 2: 反应器数据
        // ============================================================
        private void InitializeReadDataCommand()
        {
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
                        new NumberParameter("TT1001_ActualTemp", -100, 500, 0, "TT1001实际温度", true, UnitTemperature),
                        new NumberParameter("TT1002_ActualTemp", -100, 500, 0, "TT1002实际温度", true, UnitTemperature),
                        new NumberParameter("TT1003_ActualTemp", -100, 500, 0, "TT1003实际温度", true, UnitTemperature),
                        new NumberParameter("PT0112_Pressure", 0, 100, 0, "出口压力", true, UnitPressure),
                        new NumberParameter("TT0112_Temperature", -100, 500, 0, "出口温度", true, UnitTemperature),
                        new BooleanParameter("TT1001_Stable", false, "TT1001稳定") { IsVisibleInVariableManager = true },
                        new BooleanParameter("TT1002_Stable", false, "TT1002稳定") { IsVisibleInVariableManager = true },
                        new BooleanParameter("TT1003_Stable", false, "TT1003稳定") { IsVisibleInVariableManager = true },
                        new BooleanParameter("SystemStable", false, "系统整体稳定") { IsVisibleInVariableManager = true },
                        new BooleanParameter("IsStable", false, "通用稳定接口") { IsVisibleInVariableManager = true },
                        new StringParameter("ControlStatus", "", "智能控制状态") { IsVisibleInVariableManager = true },
                        new BooleanParameter("Success", false, "读取成功") { IsVisibleInVariableManager = true }
                    }
                },
                AsyncAction = async (DeviceParameters parameters) =>
                {
                    return await ExecuteReadDataAsync(parameters);
                }
            });
        }

        private async Task<DeviceParameters> ExecuteReadDataAsync(DeviceParameters parameters)
        {
            ComId = Parameters.GetValue<string>("DeviceId");
            string deviceId = ComId;
            double tt1001 = 0, tt1002 = 0, tt1003 = 0, pt0112 = 0, tt0112 = 0;
            bool success = false;

            try
            {
                if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null && !IsSimulationMode)
                {
                    var t1Task = ConnectionManager.ReadAsync<double>(ComId, "TT1001_℃");
                    var t2Task = ConnectionManager.ReadAsync<double>(ComId, "TT1002_℃");
                    var t3Task = ConnectionManager.ReadAsync<double>(ComId, "TT1003_℃");
                    var pt0112Task = ConnectionManager.ReadAsync<double>(ComId, "PT0112_Mpa");
                    var tt0112Task = ConnectionManager.ReadAsync<double>(ComId, "TT0112_℃");

                    await Task.WhenAll(t1Task, t2Task, t3Task, pt0112Task, tt0112Task);

                    tt1001 = await t1Task;
                    tt1002 = await t2Task;
                    tt1003 = await t3Task;
                    pt0112 = await pt0112Task;
                    tt0112 = await tt0112Task;

                    InfoLog($"读取 {deviceId} 数据: TT1001={tt1001}, TT1002={tt1002}, TT1003={tt1003}");
                    success = true;
                }
                else if (IsSimulationMode)
                {
                    tt1001 = _deviceData.GetValueOrDefault($"{deviceId}_TT1001_Actual", 25.0);
                    tt1002 = _deviceData.GetValueOrDefault($"{deviceId}_TT1002_Actual", 25.0);
                    tt1003 = _deviceData.GetValueOrDefault($"{deviceId}_TT1003_Actual", 25.0);

                    var random = new Random();
                    pt0112 = 0.1 + random.NextDouble() * 5;
                    tt0112 = 20 + random.NextDouble() * 200;
                    success = true;
                }
                else
                {
                    throw new InvalidOperationException("设备未配置为 ModbusTcp 模式且非模拟模式");
                }

                if (success)
                {
                    OnStateChanged("TT1001_ActualTemp", tt1001);
                    OnStateChanged("TT1002_ActualTemp", tt1002);
                    OnStateChanged("TT1003_ActualTemp", tt1003);

                    _deviceData[$"{deviceId}_TT1001_Actual"] = tt1001;
                    _deviceData[$"{deviceId}_TT1002_Actual"] = tt1002;
                    _deviceData[$"{deviceId}_TT1003_Actual"] = tt1003;
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
                    new NumberParameter("TT1001_ActualTemp", -100, 500, Math.Round(tt1001, 2), "TT1001实际温度", true, UnitTemperature),
                    new NumberParameter("TT1002_ActualTemp", -100, 500, Math.Round(tt1002, 2), "TT1002实际温度", true, UnitTemperature),
                    new NumberParameter("TT1003_ActualTemp", -100, 500, Math.Round(tt1003, 2), "TT1003实际温度", true, UnitTemperature),
                    new NumberParameter("PT0112_Pressure", 0, 100, Math.Round(pt0112, 3), "出口压力", true, UnitPressure),
                    new NumberParameter("TT0112_Temperature", -100, 500, Math.Round(tt0112, 2), "出口温度", true, UnitTemperature),
                    new BooleanParameter("TT1001_Stable", _stabilityStatus.TT1001_Stable, "TT1001稳定") { IsVisibleInVariableManager = true },
                    new BooleanParameter("TT1002_Stable", _stabilityStatus.TT1002_Stable, "TT1002稳定") { IsVisibleInVariableManager = true },
                    new BooleanParameter("TT1003_Stable", _stabilityStatus.TT1003_Stable, "TT1003稳定") { IsVisibleInVariableManager = true },
                    new BooleanParameter("SystemStable", _stabilityStatus.IsSystemStable, "系统整体稳定") { IsVisibleInVariableManager = true },
                    new BooleanParameter("IsStable", _stabilityStatus.IsSystemStable, "通用稳定接口") { IsVisibleInVariableManager = true },
                    new StringParameter("ControlStatus", GetControlStatusString(), "智能控制状态") { IsVisibleInVariableManager = true },
                    new BooleanParameter("Success", success, "读取成功") { IsVisibleInVariableManager = true }
                }
            };
        }

        // ============================================================
        // 命令 3: 停止温度控制
        // ============================================================
        private void InitializeStopControlCommand()
        {
            Commands.Add(new DeviceCommand
            {
                Name = "停止温度控制",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/stop_control.png",
                HelpText = "停止智能温度控制并完全关闭循环器(加热/制冷/循环)",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("StopCirculator", true, "停止循环器")
                        {
                            HelpText = "true=完全停止循环器(加热/制冷/循环);false=只停 controller,循环器保持运行"
                        }
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("DeviceId", "", "设备ID") { IsVisibleInVariableManager = true },
                        new BooleanParameter("ControllerStopped", false, "控制器已停止") { IsVisibleInVariableManager = true },
                        new BooleanParameter("CirculatorStopped", false, "循环器已停止") { IsVisibleInVariableManager = true },
                        new BooleanParameter("Success", false, "停止成功") { IsVisibleInVariableManager = true }
                    }
                },
                AsyncAction = async (DeviceParameters parameters) =>
                {
                    return await ExecuteStopControlAsync(parameters);
                }
            });
        }

        private async Task<DeviceParameters> ExecuteStopControlAsync(DeviceParameters parameters)
        {
            ComId = Parameters.GetValue<string>("DeviceId");
            string deviceId = ComId;
            string circulatorId = Parameters.GetValue<string>("CirculatorDeviceId");
            bool stopCirculator = parameters.GetValue<bool>("StopCirculator");

            bool controllerStopped = false;
            bool circulatorStopped = false;
            bool success = false;

            try
            {
                _controlLogger.LogInfo("开始停止智能温度控制");

                MasterSlaveTemperatureController controller;
                lock (_controllerLock)
                {
                    controller = _activeController;
                    _activeController = null;
                }

                if (controller != null)
                {
                    await controller.StopAsync();
                    controllerStopped = true;
                    InfoLog("温度控制器已停止");
                    _controlLogger.LogInfo("温度控制器已停止");
                }

                if (stopCirculator)
                {
                    _controlLogger.LogInfo("开始完全停止循环器");
                    circulatorStopped = await StopCirculatorFully(circulatorId);
                }

                _stabilityStatus.TT1001_Stable = false;
                _stabilityStatus.TT1002_Stable = false;
                _stabilityStatus.TT1003_Stable = false;

                success = true;
                _controlLogger.LogSuccess("智能温度控制停止完成");
            }
            catch (Exception ex)
            {
                ErrorLog($"停止智能温度控制失败: {deviceId} - {ex.Message}");
                _controlLogger.LogError($"停止失败: {ex.Message}", ex);
                success = false;
            }

            return new DeviceParameters
            {
                Variables = new ObservableCollection<ParameterBase>
                {
                    new StringParameter("DeviceId", deviceId, "设备ID") { IsVisibleInVariableManager = true },
                    new BooleanParameter("ControllerStopped", controllerStopped, "控制器已停止") { IsVisibleInVariableManager = true },
                    new BooleanParameter("CirculatorStopped", circulatorStopped, "循环器已停止") { IsVisibleInVariableManager = true },
                    new BooleanParameter("Success", success, "停止成功") { IsVisibleInVariableManager = true }
                }
            };
        }

        private string GetControlStatusString()
        {
            lock (_controllerLock)
            {
                if (_activeController == null)
                    return "未启动";

                return $"{_activeController.GetCurrentStateInfo()} (主控:{_activeController.MasterSensor})";
            }
        }

        // ============================================================
        // 温度轮询(用于 UI 实时显示)
        // ============================================================

        private void StartTemperaturePolling()
        {
            try
            {
                if (_temperaturePollingTask != null && !_temperaturePollingTask.IsCompleted)
                {
                    InfoLog("检测到已有温度轮询任务,先停止");
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
                        ErrorLog($"温度轮询异常: {ex.Message}");
                        try { await Task.Delay(2000, cancellationToken); }
                        catch (OperationCanceledException) { break; }
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
                tt1001 = _deviceData.GetValueOrDefault($"{deviceId}_TT1001_Actual", 25.0);
                tt1002 = _deviceData.GetValueOrDefault($"{deviceId}_TT1002_Actual", 25.0);
                tt1003 = _deviceData.GetValueOrDefault($"{deviceId}_TT1003_Actual", 25.0);
            }
            else
            {
                return;
            }

            _deviceData[$"{deviceId}_TT1001_Actual"] = tt1001;
            _deviceData[$"{deviceId}_TT1002_Actual"] = tt1002;
            _deviceData[$"{deviceId}_TT1003_Actual"] = tt1003;

            OnStateChanged("TT1001_ActualTemp", tt1001);
            OnStateChanged("TT1002_ActualTemp", tt1002);
            OnStateChanged("TT1003_ActualTemp", tt1003);
        }

        // ============================================================
        // 循环器辅助方法
        // ============================================================

        private async Task EnsureCirculatorCycleRunning(string circulatorId)
        {
            try
            {
                if (IsSimulationMode)
                {
                    InfoLog($"模拟模式: 启动循环器 {circulatorId} 循环功能");
                    _controlLogger.LogInfo($"模拟模式: 启动循环器 {circulatorId} 循环功能");
                }
                else
                {
                    string cmd = GetCirculatorCycleCommand(circulatorId);
                    await ConnectionManager.WriteAsync(circulatorId, cmd, true);
                    InfoLog($"启动循环器 {circulatorId} 循环功能: {cmd}=true");
                    _controlLogger.LogInfo($"启动循环器 {circulatorId} 循环功能: {cmd}=true");
                }
            }
            catch (Exception ex)
            {
                ErrorLog($"启动循环器循环功能失败: {circulatorId}", ex);
                _controlLogger.LogError($"启动循环器循环功能失败: {circulatorId}", ex);
            }
        }

        private async Task<bool> StopCirculatorFully(string circulatorId)
        {
            try
            {
                if (IsSimulationMode)
                {
                    InfoLog($"模拟模式: 完全停止循环器 {circulatorId}");
                    _controlLogger.LogInfo($"模拟模式: 完全停止循环器 {circulatorId}");
                    return true;
                }

                await ConnectionManager.WriteAsync(circulatorId, GetCirculatorHeatingCommand(circulatorId), false);
                await Task.Delay(300);
                await ConnectionManager.WriteAsync(circulatorId, GetCirculatorCoolingCommand(circulatorId), false);
                await Task.Delay(300);
                await ConnectionManager.WriteAsync(circulatorId, GetCirculatorCycleCommand(circulatorId), false);

                InfoLog($"完全停止循环器 {circulatorId}");
                _controlLogger.LogSuccess($"完全停止循环器 {circulatorId}");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog($"停止循环器失败: {circulatorId}", ex);
                _controlLogger.LogError($"停止循环器失败: {circulatorId}", ex);
                return false;
            }
        }

        private string GetCirculatorHeatingCommand(string circulatorId) =>
            circulatorId == "TC0101" ? "YY_Heat_1" : "YY_Heat_2";

        private string GetCirculatorCoolingCommand(string circulatorId) =>
            circulatorId == "TC0101" ? "YY_Cool_1" : "YY_Cool_2";

        private string GetCirculatorCycleCommand(string circulatorId) =>
            circulatorId == "TC0101" ? "YY_Cycle_1" : "YY_Cycle_2";

        private string GetCirculatorTempSetCommand(string circulatorId) =>
            circulatorId == "TC0101" ? "YY_SV_Set_1" : "YY_SV_Set_2";

        // ============================================================
        // 基类重写方法
        // ============================================================

        public override void Initialize()
        {
            InfoLog("开始初始化 ModbusTCP 微反应器设备");

            try
            {
                base.Initialize();

                _deviceData.Clear();
                _stabilityStatus = new SystemStabilityStatus();

                lock (_controllerLock)
                {
                    _activeController = null;
                }

                InfoLog("ModbusTCP 微反应器设备初始化完成");
            }
            catch (Exception ex)
            {
                ErrorLog("初始化失败", ex);
                throw;
            }
        }

        public override void Shutdown()
        {
            InfoLog("开始关闭 ModbusTCP 微反应器设备");
            try
            {
                var stopTask = StopAllActiveStatesAsync();
                stopTask.Wait(TimeSpan.FromSeconds(5));
                InfoLog("ModbusTCP 微反应器设备关闭完成");
            }
            catch (Exception ex)
            {
                ErrorLog("关闭失败", ex);
                throw;
            }
        }

        protected override async Task<bool> OnConnectAsync()
        {
            InfoLog("ModbusTCP 微反应器设备连接");
            await Task.Delay(100);
            return true;
        }

        protected override async Task<bool> OnDisconnectAsync()
        {
            InfoLog("ModbusTCP 微反应器设备断开连接");
            await StopAllActiveStatesAsync();
            await Task.Delay(100);
            return true;
        }

        protected override async Task<bool> OnCheckConnectionAsync()
        {
            await Task.Delay(50);
            return true;
        }
    }

    // ============================================================
    // SmartControlLogger - 智能控制专用日志(写入程序运行目录)
    // ============================================================
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
                // 写入程序运行目录下的 ControlLogs 文件夹(与流量计驱动一致)
                string logDirectory = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "ControlLogs",
                    DateTime.Now.ToString("yyyy-MM-dd")
                );

                Directory.CreateDirectory(logDirectory);

                string fileName = $"TempControl_{flowName}_{DateTime.Now:HHmmss}.log";
                _logFilePath = Path.Combine(logDirectory, fileName);

                lock (_logLock)
                {
                    _logWriter?.Dispose();
                    _logWriter = new StreamWriter(_logFilePath, append: true, encoding: System.Text.Encoding.UTF8)
                    {
                        AutoFlush = true
                    };

                    _isInitialized = true;

                    _logWriter.WriteLine("=".PadRight(80, '='));
                    _logWriter.WriteLine("主从智能温度控制系统日志");
                    _logWriter.WriteLine($"流程名称: {flowName}");
                    _logWriter.WriteLine($"开始时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    _logWriter.WriteLine($"日志文件: {fileName}");
                    _logWriter.WriteLine("控制策略: 主从控制 + 工艺偏置起步 + 微调累积 + 失稳迟滞");
                    _logWriter.WriteLine("安全保护: 收敛性判据 + 速率监测 + 趋势预测 + 失控保护");
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
            WriteLog("INFO", "启动智能温度控制系统", $"设备ID: {deviceId}, 循环器ID: {circulatorId}");
        }

        public void LogControllerStart(string sensorName, double targetTemp, double tolerance, int interval)
        {
            WriteLog("INFO", $"启动控制器, 主控: {sensorName}",
                $"目标温度: {targetTemp}℃, 容差: ±{tolerance}℃, 间隔: {interval}秒");
        }

        public void LogTemperatureReading(string sensorName, double actualTemp, double targetTemp, double error)
        {
            WriteLog("DATA", $"温度读取 {sensorName}",
                $"物料温度: {actualTemp:F2}℃, 目标: {targetTemp:F2}℃, 偏差: {error:F2}℃");
        }

        public void LogStabilityCheck(string sensorName, bool isStable, string details)
        {
            WriteLog("STABILITY", $"{sensorName}稳定性",
                $"稳定: {(isStable ? "是" : "否")}, 详情: {details}");
        }

        public void LogSystemStable()
        {
            WriteLog("SUCCESS", "系统整体稳定", "可以开始进料反应");
        }

        public void LogWarning(string message, string details = "")
        {
            WriteLog("WARN", message, details);
        }

        public void LogError(string message, Exception ex = null)
        {
            string details = ex != null ? $"异常: {ex.Message}\n堆栈: {ex.StackTrace}" : "";
            WriteLog("ERROR", message, details);
        }

        public void LogInfo(string message, string details = "")
        {
            WriteLog("INFO", message, details);
        }

        public void LogSuccess(string message, string details = "")
        {
            WriteLog("SUCCESS", message, details);
        }

        public void LogDebug(string message, string details = "")
        {
            WriteLog("DEBUG", message, details);
        }

        private void WriteLog(string level, string message, string details = "")
        {
            if (!_isInitialized || _logWriter == null) return;

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
                                _logWriter.WriteLine($"{"".PadRight(25)} | {line.Trim()}");
                            }
                        }
                    }
                    _logWriter.WriteLine();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"写日志失败: {ex.Message}");
            }
        }

        public void Close()
        {
            if (!_isInitialized) return;

            try
            {
                lock (_logLock)
                {
                    if (_logWriter != null)
                    {
                        _logWriter.WriteLine("=".PadRight(80, '='));
                        _logWriter.WriteLine($"日志结束: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        _logWriter.WriteLine("=".PadRight(80, '='));

                        _logWriter.Dispose();
                        _logWriter = null;
                    }
                    _isInitialized = false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"关闭日志失败: {ex.Message}");
            }
        }
    }

    // ============================================================
    // SystemStabilityStatus - 系统稳定状态
    // ============================================================
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

        /// <summary>
        /// 系统是否稳定 - OR 判据(任一路稳定即为系统稳定)
        /// </summary>
        public bool IsSystemStable => _tt1001Stable || _tt1002Stable || _tt1003Stable;

        public string GetStatusReport()
        {
            return string.Format(
                "主从智能控制系统稳定性状态(OR 判据):\n" +
                "  TT1001: {0}\n" +
                "  TT1002: {1}\n" +
                "  TT1003: {2}\n" +
                "  整体: {3}",
                _tt1001Stable ? "稳定" : "调节中",
                _tt1002Stable ? "稳定" : "调节中",
                _tt1003Stable ? "稳定" : "调节中",
                IsSystemStable ? "已稳定(任一路达标)" : "调节中");
        }
    }
}