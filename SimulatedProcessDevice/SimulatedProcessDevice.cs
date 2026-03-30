using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;

namespace DevicePlugins.Simulated
{
    [Export(typeof(IDevice))]
    public class SimulatedProcessDevice : Device, IControllableDevice, IFlowLifecycleAware
    {
        private readonly ILogService _logger;
        private readonly object _stateLock = new object();

        // *** 模拟设备内部状态 ***
        private bool _isRunning = false;
        private double _currentTemperature = 25.0;
        private double _targetTemperature = 25.0;

        private double _currentLiquidFlow = 0.0;
        private double _targetLiquidFlow = 0.0;

        private double _currentH2Flow = 0.0;
        private double _targetH2Flow = 0.0;

        // *** 状态跟踪相关字段 (保留框架所需) ***
        private readonly List<DeviceActiveState> _activeStates = new List<DeviceActiveState>();
        private readonly List<DeviceActiveState> _pausedStates = new List<DeviceActiveState>();
        private readonly object _statesLock = new object();

        public SimulatedProcessDevice()
        {
            _logger = LogManager.GetLogger<SimulatedProcessDevice>();

            Name = "Simulated_Reactor_V1";
            Manufacturer = "Virtual Devices Inc.";
            Category = DeviceCategories.Reactors; // 或者根据你的框架调整分类
            ComId = "SIM_PORT";

            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.PostProcess
            };

            // 使用默认图标或你自己的图标
            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/Default.png";

            InitializeCommands();
        }

        private void InitializeCommands()
        {
            // ==========================================
            // 1. 设置温度命令
            // ==========================================
            Commands.Add(new DeviceCommand
            {
                Name = "设置温度",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/temperature.png",
                HelpText = "设置反应器目标温度",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TargetTemperature", -50, 300, 25.0, "目标温度(℃)")
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("Status", "PENDING", "执行状态") { IsVisibleInVariableManager = false },
                        new NumberParameter("CurrentTemperature", -50, 300, 0, "当前温度(℃)") { IsVisibleInVariableManager = true },
                        new StringParameter("Message", "", "状态信息") { IsVisibleInVariableManager = false }
                    }
                },
                Action = WrapAction("设置温度", async delegate (DeviceParameters parameters)
                {
                    double targetTemp = parameters.GetValue<double>("TargetTemperature");

                    // 模拟硬件通信延迟
                    await Task.Delay(new Random().Next(200, 500));

                    lock (_stateLock)
                    {
                        _targetTemperature = targetTemp;
                        // 模拟温度瞬间达到目标值附近，并加入±0.5的随机噪声
                        _currentTemperature = targetTemp + (new Random().NextDouble() - 0.5);
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("Status", "OK", "") { IsVisibleInVariableManager = false },
                            new NumberParameter("CurrentTemperature", -50, 300, Math.Round(_currentTemperature, 2), "") { IsVisibleInVariableManager = true },
                            new StringParameter("Message", $"温度已设定为 {targetTemp}℃", "") { IsVisibleInVariableManager = false }
                        }
                    };
                })
            });

            // ==========================================
            // 2. 设置流量命令 (液体)
            // ==========================================
            Commands.Add(new DeviceCommand
            {
                Name = "设置流量",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/flowrate.png",
                HelpText = "设置主泵液体流量",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TargetFlow", 0, 100, 1.0, "目标流量(mL/min)")
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("Status", "PENDING", "执行状态") { IsVisibleInVariableManager = false },
                        new NumberParameter("CurrentFlow", 0, 100, 0, "当前流量(mL/min)") { IsVisibleInVariableManager = true },
                        new StringParameter("Message", "", "状态信息") { IsVisibleInVariableManager = false }
                    }
                },
                Action = WrapAction("设置流量", async delegate (DeviceParameters parameters)
                {
                    double targetFlow = parameters.GetValue<double>("TargetFlow");

                    // 模拟硬件通信延迟
                    await Task.Delay(new Random().Next(100, 300));

                    lock (_stateLock)
                    {
                        _targetLiquidFlow = targetFlow;
                        // 模拟流量波动 (±0.05)
                        _currentLiquidFlow = targetFlow > 0 ? targetFlow + (new Random().NextDouble() * 0.1 - 0.05) : 0;
                        _isRunning = _currentLiquidFlow > 0;
                    }

                    // 记录活跃状态 (如果流量大于0)
                    if (targetFlow > 0)
                    {
                        AddActiveState("LiquidPumping", "设置流量", "停止", stateData: targetFlow);
                    }
                    else
                    {
                        RemoveActiveState("LiquidPumping");
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("Status", "OK", "") { IsVisibleInVariableManager = false },
                            new NumberParameter("CurrentFlow", 0, 100, Math.Round(_currentLiquidFlow, 3), "") { IsVisibleInVariableManager = true },
                            new StringParameter("Message", $"液体流量已设定为 {targetFlow} mL/min", "") { IsVisibleInVariableManager = false }
                        }
                    };
                })
            });

            // ==========================================
            // 3. 设置氢气流量命令 (气体质量流量计 MFC 模拟)
            // ==========================================
            Commands.Add(new DeviceCommand
            {
                Name = "设置氢气流量",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/gas.png",
                HelpText = "设置氢气质量流量控制器(MFC)流量",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TargetH2Flow", 0, 500, 50.0, "目标氢气流量(sccm)")
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("Status", "PENDING", "执行状态") { IsVisibleInVariableManager = false },
                        new NumberParameter("CurrentH2Flow", 0, 500, 0, "当前氢气流量(sccm)") { IsVisibleInVariableManager = true },
                        new StringParameter("Message", "", "状态信息") { IsVisibleInVariableManager = false }
                    }
                },
                Action = WrapAction("设置氢气流量", async delegate (DeviceParameters parameters)
                {
                    double targetH2 = parameters.GetValue<double>("TargetH2Flow");

                    // 模拟硬件通信延迟
                    await Task.Delay(new Random().Next(100, 300));

                    lock (_stateLock)
                    {
                        _targetH2Flow = targetH2;
                        // 气体流量计通常比较精准，加入极小波动 (±0.5 sccm)
                        _currentH2Flow = targetH2 > 0 ? targetH2 + (new Random().NextDouble() - 0.5) : 0;
                    }

                    if (targetH2 > 0)
                    {
                        AddActiveState("H2Flowing", "设置氢气流量", "停止", stateData: targetH2);
                    }
                    else
                    {
                        RemoveActiveState("H2Flowing");
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("Status", "OK", "") { IsVisibleInVariableManager = false },
                            new NumberParameter("CurrentH2Flow", 0, 500, Math.Round(_currentH2Flow, 1), "") { IsVisibleInVariableManager = true },
                            new StringParameter("Message", $"氢气流量已设定为 {targetH2} sccm", "") { IsVisibleInVariableManager = false }
                        }
                    };
                })
            });

            // ==========================================
            // 4. 停止命令 (用于紧急停止或清空状态)
            // ==========================================
            Commands.Add(new DeviceCommand
            {
                Name = "停止",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/off.png",
                HelpText = "停止所有流量",
                InputParameters = new DeviceParameters(),
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("Status", "PENDING", "执行状态") { IsVisibleInVariableManager = false }
                    }
                },
                Action = WrapAction("停止", async delegate (DeviceParameters parameters)
                {
                    await Task.Delay(100);
                    lock (_stateLock)
                    {
                        _isRunning = false;
                        _targetLiquidFlow = 0;
                        _currentLiquidFlow = 0;
                        _targetH2Flow = 0;
                        _currentH2Flow = 0;
                        // 温度通常不会瞬间归零，保留当前温度
                    }

                    RemoveActiveState("LiquidPumping");
                    RemoveActiveState("H2Flowing");

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("Status", "OK", "") { IsVisibleInVariableManager = false }
                        }
                    };
                })
            });
        }

        #region 生命周期与连接模拟 (纯模拟，全部返回True)

        public override void Initialize()
        {
            InfoLog("初始化模拟设备");
            base.Initialize();
            lock (_stateLock)
            {
                _isRunning = false;
                _currentTemperature = 25.0;
                _currentLiquidFlow = 0;
                _currentH2Flow = 0;
            }
        }

        public override void Shutdown()
        {
            InfoLog("关闭模拟设备");
            StopAllActiveStatesAsync().Wait();
            base.Shutdown();
        }

        protected override async Task<bool> OnConnectAsync()
        {
            InfoLog("模拟设备连接成功");
            await Task.Delay(200); // 模拟连接耗时
            return true;
        }

        protected override async Task<bool> OnDisconnectAsync()
        {
            InfoLog("模拟设备断开连接");
            await Task.Delay(100);
            return true;
        }

        protected override async Task<bool> OnCheckConnectionAsync()
        {
            // 模拟设备永远在线
            return true;
        }

        #endregion

        #region IControllableDevice 状态管理 (保留原逻辑)

        public List<DeviceActiveState> ActiveStates
        {
            get { lock (_statesLock) { return new List<DeviceActiveState>(_activeStates); } }
        }

        public event EventHandler<DeviceStateChangedEventArgs> StateChanged;

        protected void AddActiveState(string stateName, string activateCommand, string deactivateCommand, object stateData = null)
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

                _activeStates.Add(new DeviceActiveState
                {
                    StateName = stateName,
                    ActivateCommand = activateCommand,
                    DeactivateCommand = deactivateCommand,
                    ActivatedTime = DateTime.Now,
                    IsPaused = false,
                    StateData = stateData
                });
            }
            StateChanged?.Invoke(this, new DeviceStateChangedEventArgs { StateName = stateName, IsActive = true, Timestamp = DateTime.Now });
        }

        protected void RemoveActiveState(string stateName)
        {
            lock (_statesLock)
            {
                _activeStates.RemoveAll(s => s.StateName == stateName);
                _pausedStates.RemoveAll(s => s.StateName == stateName);
            }
            StateChanged?.Invoke(this, new DeviceStateChangedEventArgs { StateName = stateName, IsActive = false, Timestamp = DateTime.Now });
        }

        public virtual async Task<bool> PauseAllActiveStatesAsync() { return true; }
        public virtual async Task<bool> ResumeAllPausedStatesAsync() { return true; }

        public virtual async Task<bool> StopAllActiveStatesAsync()
        {
            lock (_statesLock)
            {
                _activeStates.Clear();
                _pausedStates.Clear();
            }
            return true;
        }

        #endregion

        #region IFlowLifecycleAware 流程生命周期

        public virtual async Task<bool> OnFlowStartedAsync(FlowStartContext context)
        {
            InfoLog($"模拟流程开始: {context.FlowName}");
            return true;
        }

        public virtual async Task<bool> OnFlowCompletedAsync(FlowCompletionContext context)
        {
            InfoLog($"模拟流程完成: {context.FlowName}");
            await StopAllActiveStatesAsync();
            return true;
        }

        public virtual async Task<bool> OnFlowFailedAsync(FlowFailureContext context)
        {
            ErrorLog($"模拟流程失败: {context.FlowName}");
            await StopAllActiveStatesAsync();
            return true;
        }

        #endregion

        #region 辅助方法

        private Func<DeviceParameters, DeviceParameters> WrapAction(string commandName, Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return (DeviceParameters parameters) =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();
                    _logger.LogInformation($"[模拟命令执行] {commandName} 耗时: {sw.ElapsedMilliseconds}ms");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[模拟命令异常] {commandName}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        private void InfoLog(string msg) => _logger?.LogInformation(msg);
        private void WarnLog(string msg) => _logger?.LogWarning(msg);
        private void ErrorLog(string msg, Exception ex = null)
        {
            if (ex != null) _logger?.LogError(ex, msg);
            else _logger?.LogError(msg);
        }

        #endregion
    }
}