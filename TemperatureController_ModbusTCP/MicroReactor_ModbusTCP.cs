using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using MicroReactor_ModbusTCP.MpcCore;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MicroReactor_ModbusTCP
{
    /// <summary>
    /// 微反应器 (ModbusTCP) - 经典串级 PID 控制版
    /// 
    /// 控制策略: 工业标准串级 PID
    /// - 外环 PI(D) + 前馈 + 抗饱和: 控制物料温度,输出循环器目标
    /// - 内环: 由循环器自带 PID 处理(夹套油温)
    /// - 加入 Kd 微分项: 提前预判趋势,减少过冲
    /// - 简单限幅 ±MaxBias,不做动态限幅
    /// - 让 PID 自由发挥,靠反应釜热惯性过滤夹套振荡
    /// 
    /// 调参流程:
    /// 1. 第一次用保守参数(Kp=0.5, Ki秒=300, Kd秒=30, MaxBias=30)
    /// 2. 跑实验,贴日志
    /// 3. 根据日志诊断,每次只改一个参数
    /// 4. 5~10 次实验后调到工业级表现
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

        private CascadeMpcReactorController  _activeController;
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
                HelpText = "ModbusTCP 微反应器设备 ID"
            });

            Parameters.Variables.Add(new StringParameter("CirculatorDeviceId", "TC0101")
            {
                Options = new ObservableCollection<string>(new List<string>()
                {
                    "TC0101", "TC0201"
                }),
                HelpText = "选择用于温度控制的高低温循环器设备 ID"
            });

            Parameters.Variables.Add(new StringParameter("MasterSensor", "TT1002")
            {
                Options = new ObservableCollection<string>(new List<string>()
                {
                    "TT1001", "TT1002", "TT1003"
                }),
                HelpText = "主控传感器: 控制器跟踪此路温度,其他两路作为监测使用"
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
                    states["JacketCommand"] = _activeController.LastJacketTarget;
                    states["IntegralAccum"] = _activeController.IntegralAccum;
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
            CascadeMpcReactorController controller;
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
            CascadeMpcReactorController controller;
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
                _controlLogger.EnsureInitialized(context.FlowName);
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
                        _controlLogger.LogInfo("====== 流程结束,温度控制继续后台运行 ======",
                            $"阶段:{_activeController.CurrentPhase}\n" +
                            $"主控:{_activeController.MasterSensor}\n" +
                            $"日志保持打开,继续记录后台运行");
                    }
                    else
                    {
                        _controlLogger.LogInfo("流程结束,无活跃温度控制器");
                    }
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
        // 命令 1: 智能设置温度 (经典串级 PID)
        // ============================================================
        private void InitializeIntelligentSetTemperatureCommand()
        {
            Commands.Add(new DeviceCommand
            {
                Name = "智能设置温度",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/temp_set.png",
                HelpText = "经典串级 PID 控制(工业标准)。" +
                           "外环 PI(D) + 前馈 + 抗饱和。" +
                           "靠反应釜热惯性过滤夹套振荡,物料温度天然平滑。" +
                           "支持平滑切换(同一批物料分阶段升温)。",

                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        // ============ 工艺参数 ============
                        new NumberParameter("TargetTemperature", -100, 500, 25, "目标温度(℃)")
                        {
                            HelpText = "三路温度的统一目标值"
                        },
                        new NumberParameter("Tolerance", 0.1, 5.0, 3.0, "温度容差(℃)")
                        {
                            HelpText = "稳定容差: 主控温度在 ±此值内持续 30 秒即视为达标"
                        },
                        new NumberParameter("ControlInterval", 1, 30, 5, "控制周期(秒)")
                        {
                            HelpText = "PID 计算频率。慢响应循环器建议 5 秒"
                        },

                        // ============ PID 三参数 ============
                        new NumberParameter("Kp", 0.05, 5.0, 0.5, "Kp 比例增益")
                        {
                            HelpText = "比例项: 大→响应快但易过冲;小→响应慢但稳。第一次用 0.5"
                        },
                        new NumberParameter("KiSeconds", 30, 3000, 300, "Ki(秒) 积分时间")
                        {
                            HelpText = "积分时间(秒): 大→积分慢、抗扰差;小→积分快、易过冲。第一次用 300"
                        },
                        new NumberParameter("KdSeconds", 0, 300, 30, "Kd(秒) 微分时间")
                        {
                            HelpText = "微分时间(秒): 防过冲利器,但放大噪声。0=不用微分。第一次用 30"
                        },
                        new NumberParameter("FeedforwardBias", 0, 20, 5, "前馈偏置(℃)")
                        {
                            HelpText = "升温时循环器命令额外加 +N℃,降温时 -N℃。给 PID 一个起始推力。第一次用 5"
                        },
                        new NumberParameter("MaxBias", 5, 60, 30, "最大偏置(℃)")
                        {
                            HelpText = "循环器命令最多偏离目标 ±此值。让 PID 充分发挥。第一次用 30"
                        },

                        // ============ [安全] 参数 ============
                        new NumberParameter("[安全]稳定确认时长秒", 10, 300, 30, "[安全]稳定确认时长(秒)")
                        {
                            HelpText = "误差进容差后,持续此时间才确认稳定"
                        },
                        new NumberParameter("[安全]大偏差报警阈值", 5, 50, 10, "[安全]大偏差报警阈值(℃)")
                        {
                            HelpText = "物料偏离目标超过此值且收敛性异常时报警"
                        },
                        new NumberParameter("[安全]收敛性检查窗口", 60, 600, 120, "[安全]收敛性检查窗口(秒)")
                        {
                            HelpText = "偏差超阈值且不再缩小持续此时间 → 报警"
                        },
                        new NumberParameter("[安全]温度速率上限", 1, 50, 10, "[安全]温度变化速率上限(℃/分钟)")
                        {
                            HelpText = "物料温度变化速率超过此值 → 报警"
                        },
                        new NumberParameter("[安全]监测路偏差", 5, 30, 10, "[安全]监测路偏差报警(℃)")
                        {
                            HelpText = "监测传感器与主控偏差超过此值 → 仅报警不停止"
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

            // 工艺参数
            double targetTemp = parameters.GetValue<double>("TargetTemperature");
            double tolerance = parameters.GetValue<double>("Tolerance");
            int controlInterval = (int)parameters.GetValue<double>("ControlInterval");

            // PID
            double kp = parameters.GetValue<double>("Kp");
            double kiSec = parameters.GetValue<double>("KiSeconds");
            double kdSec = parameters.GetValue<double>("KdSeconds");
            double feedforward = parameters.GetValue<double>("FeedforwardBias");
            double maxBias = parameters.GetValue<double>("MaxBias");

            // 安全
            double stableConfirmSec = parameters.GetValue<double>("[安全]稳定确认时长秒");
            double alarmDeviation = parameters.GetValue<double>("[安全]大偏差报警阈值");
            double alarmDuration = parameters.GetValue<double>("[安全]收敛性检查窗口");
            double maxRate = parameters.GetValue<double>("[安全]温度速率上限");
            double monitorMaxDev = parameters.GetValue<double>("[安全]监测路偏差");

            bool overallSuccess = false;
            string message = "";

            try
            {
                string flowName = "PID";
                _controlLogger.ForceReinitialize(flowName);

                InfoLog($"启动经典 PID 控制: 设备={deviceId}, 循环器={circulatorId}, 主控={masterSensor}, " +
                        $"目标={targetTemp}℃, Kp={kp}, Ki(秒)={kiSec}, Kd(秒)={kdSec}");

                _controlLogger.LogControlStart(deviceId, circulatorId);

                // 1. 处理已有控制器
                CascadeMpcReactorController existingController;
                lock (_controllerLock)
                {
                    existingController = _activeController;
                }

                if (existingController != null)
                {
                    bool canSmoothSwitch =
                        existingController.MasterSensor == masterSensor &&
                        existingController.CurrentPhase != ControlPhase.Stopped &&
                        existingController.CurrentPhase != ControlPhase.Failed;

                    if (canSmoothSwitch)
                    {
                        _controlLogger.LogInfo("检测到运行中的控制器,执行平滑切换",
                            $"目标温度 → {targetTemp:F2}℃");

                        bool ok = await existingController.ChangeTargetAsync(targetTemp);
                        if (ok)
                        {
                            return BuildResult(deviceId, circulatorId, masterSensor, targetTemp, true,
                                $"平滑切换成功 - 主控{masterSensor}, 新目标{targetTemp:F2}℃");
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

                    lock (_controllerLock)
                    {
                        _activeController = null;
                    }
                    await existingController.StopAsync(stopCirculator: true);
                }

                // 2. 启动循环器
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
                var config = new CascadeMpcConfig
                {
                    TargetTemperature = targetTemp,
                    MasterSensor = masterSensor,
                    Tolerance = tolerance,                       // 见改动 B,默认值改 3.0
                    ReadingInterval = TimeSpan.FromSeconds(10),  // 强制 10 秒(MPC Ts=10)
                    StableConfirmDuration = TimeSpan.FromSeconds(stableConfirmSec),
                    AlarmDeviationDeg = alarmDeviation,
                    AlarmDurationSec = alarmDuration,
                    MaxRateDegPerMin = maxRate,
                    MonitorMaxDeviation = monitorMaxDev,
                    // 循环器上下限沿用原 Safety 配置(若有);否则用默认 5/90
                    MinCirculatorCommand = 5.0,
                    MaxCirculatorCommand = 90.0,
                };
                // 4. 创建并启动 controller
                var controller = new CascadeMpcReactorController(
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

                _controlLogger.LogSuccess("经典 PID 温度控制启动成功");

                overallSuccess = true;
                message = $"启动成功 - 主控{masterSensor}, 目标{targetTemp:F2}℃, " +
                          $"Kp={kp}, Ki秒={kiSec}, Kd秒={kdSec}";
            }
            catch (Exception ex)
            {
                ErrorLog($"启动失败: {ex.Message}");
                _controlLogger.LogError($"启动失败: {ex.Message}", ex);
                overallSuccess = false;
                message = $"启动失败: {ex.Message}";
            }

            return BuildResult(deviceId, circulatorId, masterSensor, targetTemp, overallSuccess, message);
        }

        private DeviceParameters BuildResult(string deviceId, string circulatorId,
            string masterSensor, double targetTemp, bool success, string message)
        {
            return new DeviceParameters
            {
                Variables = new ObservableCollection<ParameterBase>
                {
                    new StringParameter("DeviceId", deviceId, "设备ID") { IsVisibleInVariableManager = true },
                    new StringParameter("CirculatorId", circulatorId, "循环器ID") { IsVisibleInVariableManager = true },
                    new StringParameter("MasterSensor", masterSensor, "主控传感器") { IsVisibleInVariableManager = true },
                    new NumberParameter("TargetTemp", -100, 500, Math.Round(targetTemp, 2), "目标温度", true, UnitTemperature),
                    new BooleanParameter("ControlStarted", success, "智能控制启动") { IsVisibleInVariableManager = true },
                    new BooleanParameter("Success", success, "命令成功") { IsVisibleInVariableManager = true },
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
                HelpText = "停止智能温度控制并完全关闭循环器",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("StopCirculator", true, "停止循环器")
                        {
                            HelpText = "true=完全停止循环器;false=只停 controller,循环器保持"
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
                _controlLogger.EnsureInitialized("StopCmd");
                _controlLogger.LogInfo("====== 停止智能温度控制 ======");

                CascadeMpcReactorController controller;
                lock (_controllerLock)
                {
                    controller = _activeController;
                    _activeController = null;
                }

                if (controller != null)
                {
                    await controller.StopAsync();
                    controllerStopped = true;
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

                _controlLogger.Close();
            }
            catch (Exception ex)
            {
                ErrorLog($"停止失败: {deviceId} - {ex.Message}");
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
        // 循环器辅助
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

        // ============================================================
        // 基类重写
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
    // SmartControlLogger - 修复版,保证日志永远在记录
    // ============================================================
    public class SmartControlLogger
    {
        private string _logFilePath;
        private readonly object _logLock = new object();
        private StreamWriter _logWriter;
        private bool _isInitialized = false;
        private string _currentFlowName = "";

        public void EnsureInitialized(string flowName)
        {
            if (_isInitialized) return;
            Initialize(flowName);
        }

        public void ForceReinitialize(string flowName)
        {
            try
            {
                lock (_logLock)
                {
                    if (_isInitialized && _logWriter != null)
                    {
                        try
                        {
                            _logWriter.WriteLine();
                            _logWriter.WriteLine("=".PadRight(80, '='));
                            _logWriter.WriteLine($"日志重建于: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                            _logWriter.WriteLine("=".PadRight(80, '='));
                            _logWriter.Dispose();
                        }
                        catch { }
                        _logWriter = null;
                    }
                    _isInitialized = false;
                }

                Initialize(flowName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"日志强制重建失败: {ex.Message}");
            }
        }

        public void Initialize(string flowName)
        {
            try
            {
                string logDirectory = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "ControlLogs",
                    DateTime.Now.ToString("yyyy-MM-dd")
                );

                Directory.CreateDirectory(logDirectory);

                string fileName = $"TempControl_{flowName}_{DateTime.Now:HHmmss}.log";
                _logFilePath = Path.Combine(logDirectory, fileName);
                _currentFlowName = flowName;

                lock (_logLock)
                {
                    _logWriter?.Dispose();
                    _logWriter = new StreamWriter(_logFilePath, append: true, encoding: System.Text.Encoding.UTF8)
                    {
                        AutoFlush = true
                    };

                    _isInitialized = true;

                    _logWriter.WriteLine("=".PadRight(80, '='));
                    _logWriter.WriteLine("经典串级 PID 温度控制系统日志");
                    _logWriter.WriteLine($"流程/任务名称: {flowName}");
                    _logWriter.WriteLine($"开始时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    _logWriter.WriteLine($"日志文件: {fileName}");
                    _logWriter.WriteLine("控制策略: 经典串级 PID (PI(D) + 前馈 + 抗饱和)");
                    _logWriter.WriteLine("内环说明: 由循环器自带 PID 处理(夹套油温),本驱动只控外环");
                    _logWriter.WriteLine("Kd 用'测量值变化率',防止目标突变时微分冲击");
                    _logWriter.WriteLine("简单限幅,不做动态限幅,让 PID 自由发挥");
                    _logWriter.WriteLine("=".PadRight(80, '='));
                    _logWriter.WriteLine();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"日志初始化失败: {ex.Message}");
            }
        }

        public void LogControlStart(string deviceId, string circulatorId)
        {
            EnsureInitialized("AutoStart");
            WriteLog("INFO", "启动智能温度控制系统", $"设备ID: {deviceId}, 循环器ID: {circulatorId}");
        }

        public void LogTemperatureReading(string sensorName, double actualTemp, double targetTemp, double error)
        {
            EnsureInitialized("AutoStart");
            WriteLog("DATA", $"温度读取 {sensorName}",
                $"物料温度: {actualTemp:F2}℃, 目标: {targetTemp:F2}℃, 偏差: {error:F2}℃");
        }

        public void LogStabilityCheck(string sensorName, bool isStable, string details)
        {
            EnsureInitialized("AutoStart");
            WriteLog("STABILITY", $"{sensorName}稳定性",
                $"稳定: {(isStable ? "是" : "否")}, 详情: {details}");
        }

        public void LogSystemStable()
        {
            EnsureInitialized("AutoStart");
            WriteLog("SUCCESS", "系统整体稳定", "可以开始进料反应");
        }

        public void LogWarning(string message, string details = "")
        {
            EnsureInitialized("AutoStart");
            WriteLog("WARN", message, details);
        }

        public void LogError(string message, Exception ex = null)
        {
            EnsureInitialized("AutoStart");
            string details = ex != null ? $"异常: {ex.Message}\n堆栈: {ex.StackTrace}" : "";
            WriteLog("ERROR", message, details);
        }

        public void LogInfo(string message, string details = "")
        {
            EnsureInitialized("AutoStart");
            WriteLog("INFO", message, details);
        }

        public void LogSuccess(string message, string details = "")
        {
            EnsureInitialized("AutoStart");
            WriteLog("SUCCESS", message, details);
        }

        public void LogDebug(string message, string details = "")
        {
            EnsureInitialized("AutoStart");
            WriteLog("DEBUG", message, details);
        }

        private void WriteLog(string level, string message, string details = "")
        {
            if (!_isInitialized || _logWriter == null) return;

            try
            {
                lock (_logLock)
                {
                    if (_logWriter == null) return;
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
    // SystemStabilityStatus
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

        public bool IsSystemStable => _tt1001Stable || _tt1002Stable || _tt1003Stable;

        public string GetStatusReport()
        {
            return string.Format(
                "经典 PID 控制系统稳定性状态(OR 判据):\n" +
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