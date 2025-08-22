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

namespace PeristalticPumps_CY
{
    [Export(typeof(IDevice))]
    public class PeristalticPumps_CY : Device
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
        // 高质量随机数生成器 - 线程安全
        private static readonly ThreadLocal<Random> ThreadLocalRandom = new ThreadLocal<Random>(
            () => new Random(Guid.NewGuid().GetHashCode()));

        // 状态变化模拟器
        private readonly SimulationEngine _simulationEngine;
        private static readonly Random GlobalRandom = new Random();
        public PeristalticPumps_CY()
        {
            _logger = LogManager.GetLogger<PeristalticPumps_CY>();
            _simulationEngine = new SimulationEngine();

            Name = "YT600-1J";
            Manufacturer = "蠕动泵_YT600-1J＆KZ35-13-A";
            Category = DeviceCategories.Pumps;
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
                        new StringParameter("Status", "PENDING", "执行状态")
                        {
                            IsVisibleInVariableManager = false
                        },
                        new NumberParameter("Temperature", -50, 200, 0, "当前温度")
                        {
                            IsVisibleInVariableManager = true
                        },
                        new StringParameter("Unit", "℃", "温度单位")
                        {
                            IsVisibleInVariableManager = false
                        },
                        new StringParameter("Timestamp", "", "采集时间")
                        {
                            IsVisibleInVariableManager = false
                        },
                        new BooleanParameter("IsWarning", false, "是否超温警告")
                        {
                            IsVisibleInVariableManager = true
                        },
                        new StringParameter("Message", "", "附加信息")
                        {
                            IsVisibleInVariableManager = false
                        }
                    }
                },
                Action = WrapAction("获取温度值", async delegate (DeviceParameters parameters)
                {
                    //await Task.Delay(ThreadLocalRandom.Value.Next(200, 500));
                    int simulatedDelayMs;
                    lock (GlobalRandom) // 简单加锁保证线程安全
                    {
                        simulatedDelayMs = GlobalRandom.Next(5000, 20000);
                    }

                    await Task.Delay(simulatedDelayMs);
                    bool useCalibration = parameters.GetValue<bool>("UseCalibration");
                    double offset = parameters.GetValue<double>("Offset");

                    lock (_stateLock)
                    {
                        _currentTemperature = _simulationEngine.UpdateTemperature(_currentTemperature, _isRunning);
                    }

                    double finalTemp = useCalibration ? _currentTemperature + offset : _currentTemperature;
                    bool isWarning = finalTemp > 80;

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("Status", "OK", "") { IsVisibleInVariableManager = false },
                            new NumberParameter("Temperature", -50, 200, Math.Round(finalTemp, 2), "当前温度") { IsVisibleInVariableManager = true },
                            new StringParameter("Unit", "℃", "") { IsVisibleInVariableManager = false },
                            new StringParameter("Timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), "") { IsVisibleInVariableManager = false },
                            new BooleanParameter("IsWarning", isWarning, "") { IsVisibleInVariableManager = true },
                            new StringParameter("Message", isWarning ? "温度过高警告！" : "温度正常", "") { IsVisibleInVariableManager = false }
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
                        new StringParameter("Status", "PENDING", "执行状态")
                        {
                            IsVisibleInVariableManager = false
                        },
                        new NumberParameter("FlowRate", 0, 1000, 0, "当前流量")
                        {
                            IsVisibleInVariableManager = true
                        },
                        new StringParameter("Unit", "mL/min", "流量单位")
                        {
                            IsVisibleInVariableManager = false
                        },
                        new NumberParameter("TotalVolume", 0, 999999, 0, "累计流量(mL)")
                        {
                            IsVisibleInVariableManager = true
                        },
                        new NumberParameter("CurrentSpeed", 0, 10000, 0, "当前转速(RPM)")
                        {
                            IsVisibleInVariableManager = true
                        },
                        new StringParameter("Timestamp", "", "采集时间")
                        {
                            IsVisibleInVariableManager = false
                        }
                    }
                },
                Action = WrapAction("获取流量值", async delegate (DeviceParameters parameters)
                {
                    //await Task.Delay(ThreadLocalRandom.Value.Next(300, 600));


                    var unit = parameters.GetValue<FlowRateUnit>("Unit");
                    double avgTime = parameters.GetValue<double>("AveragingTime");

                    double currentFlowRate;
                    double totalVolume;
                    double currentSpeed;

                    lock (_stateLock)
                    {
                        var flowData = _simulationEngine.UpdateFlowRate(_currentSpeed, _isRunning, _startTime);
                        _currentFlowRate = flowData.FlowRate;
                        _totalVolume = flowData.TotalVolume;

                        currentFlowRate = _currentFlowRate;
                        totalVolume = _totalVolume;
                        currentSpeed = _currentSpeed;
                    }

                    // 单位转换
                    double displayFlowRate = unit switch
                    {
                        FlowRateUnit.LPerMin => currentFlowRate / 1000,
                        FlowRateUnit.MLPerSec => currentFlowRate / 60,
                        _ => currentFlowRate
                    };

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
                            new NumberParameter("FlowRate", 0, 1000, Math.Round(displayFlowRate, 3), "当前流量") { IsVisibleInVariableManager = true },
                            new StringParameter("Unit", unitStr, "") { IsVisibleInVariableManager = false },
                            new NumberParameter("TotalVolume", 0, 999999, Math.Round(totalVolume, 2), "累计流量(mL)") { IsVisibleInVariableManager = true },
                            new NumberParameter("CurrentSpeed", 0, 10000, Math.Round(currentSpeed, 1), "当前转速(RPM)") { IsVisibleInVariableManager = true },
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
                        new StringParameter("Status", "PENDING", "执行状态")
                        {
                            IsVisibleInVariableManager = false
                        },
                        new NumberParameter("Pressure", -100, 1000, 0, "当前压力")
                        {
                            IsVisibleInVariableManager = true
                        },
                        new StringParameter("Unit", "kPa", "压力单位")
                        {
                            IsVisibleInVariableManager = false
                        },
                        new NumberParameter("MaxPressure", 0, 1000, 0, "最大压力记录")
                        {
                            IsVisibleInVariableManager = false
                        },
                        new NumberParameter("MinPressure", 0, 1000, 0, "最小压力记录")
                        {
                            IsVisibleInVariableManager = false
                        },
                        new BooleanParameter("IsOverPressure", false, "是否超压")
                        {
                            IsVisibleInVariableManager = true
                        },
                        new StringParameter("Timestamp", "", "采集时间")
                        {
                            IsVisibleInVariableManager = false
                        }
                    }
                },
                Action = WrapAction("获取压力值", async delegate (DeviceParameters parameters)
                {
                    //await Task.Delay(ThreadLocalRandom.Value.Next(250, 550));

                    var unit = parameters.GetValue<PressureUnit>("Unit");
                    bool isRelative = parameters.GetValue<bool>("RelativePressure");

                    double currentPressure;
                    lock (_stateLock)
                    {
                        _currentPressure = _simulationEngine.UpdatePressure(_currentFlowRate, _isRunning, isRelative);
                        currentPressure = _currentPressure;
                    }

                    // 单位转换
                    double displayPressure = unit switch
                    {
                        PressureUnit.Bar => currentPressure / 100,
                        PressureUnit.PSI => currentPressure * 0.145038,
                        PressureUnit.MPa => currentPressure / 1000,
                        _ => currentPressure
                    };

                    string unitStr = unit.ToString();
                    bool isOverPressure = currentPressure > 500;

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("Status", "OK", "") { IsVisibleInVariableManager = false },
                            new NumberParameter("Pressure", -100, 1000, Math.Round(displayPressure, 2), "当前压力") { IsVisibleInVariableManager = true },
                            new StringParameter("Unit", unitStr, "") { IsVisibleInVariableManager = false },
                            new NumberParameter("MaxPressure", 0, 1000, Math.Round(displayPressure * 1.1, 2), "最大压力记录") { IsVisibleInVariableManager = false },
                            new NumberParameter("MinPressure", 0, 1000, Math.Round(displayPressure * 0.9, 2), "最小压力记录") { IsVisibleInVariableManager = false },
                            new BooleanParameter("IsOverPressure", isOverPressure, "") { IsVisibleInVariableManager = true },
                            new StringParameter("Timestamp", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), "") { IsVisibleInVariableManager = false }
                        }
                    };
                })
            });

            // 4. 开启命令
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
                        new StringParameter("Status", "PENDING", "执行状态")
                        {
                            IsVisibleInVariableManager = false
                        },
                        new BooleanParameter("IsRunning", false, "运行状态")
                        {
                            IsVisibleInVariableManager = true
                        },
                        new NumberParameter("ActualSpeed", 0, 10000, 0, "实际转速(RPM)")
                        {
                            IsVisibleInVariableManager = true
                        },
                        new StringParameter("Direction", "Forward", "运行方向")
                        {
                            IsVisibleInVariableManager = false
                        },
                        new StringParameter("StartTime", "", "启动时间")
                        {
                            IsVisibleInVariableManager = false
                        },
                        new StringParameter("Message", "", "状态信息")
                        {
                            IsVisibleInVariableManager = false
                        }
                    }
                },
                Action = WrapAction("开启", async delegate (DeviceParameters parameters)
                {
                    double targetSpeed = parameters.GetValue<double>("TargetSpeed");
                    double rampTime = parameters.GetValue<double>("RampTime");
                    var direction = parameters.GetValue<PumpDirection>("Direction");
                    bool pulseFree = parameters.GetValue<bool>("EnablePulseFree");

                    int simulatedRampTime = Math.Min((int)(rampTime * 1000), 5000);
                    await Task.Delay(simulatedRampTime);

                    double actualSpeed;
                    lock (_stateLock)
                    {
                        _isRunning = true;
                        _currentSpeed = targetSpeed;
                        _startTime = DateTime.Now;
                        _totalVolume = 0; // 重置累计流量

                        actualSpeed = _simulationEngine.GetActualSpeed(targetSpeed);
                        _currentSpeed = actualSpeed;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("Status", "OK", "") { IsVisibleInVariableManager = false },
                            new BooleanParameter("IsRunning", true, "") { IsVisibleInVariableManager = true },
                            new NumberParameter("ActualSpeed", 0, 10000, Math.Round(actualSpeed, 1), "实际转速(RPM)") { IsVisibleInVariableManager = true },
                            new StringParameter("Direction", direction.ToString(), "") { IsVisibleInVariableManager = false },
                            new StringParameter("StartTime", _startTime.ToString("yyyy-MM-dd HH:mm:ss"), "") { IsVisibleInVariableManager = false },
                            new StringParameter("Message", $"泵已启动，{(pulseFree ? "无脉动模式" : "标准模式")}", "") { IsVisibleInVariableManager = false }
                        }
                    };
                })
            });

            // 5. 关闭命令
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
                        new StringParameter("Status", "PENDING", "执行状态")
                        {
                            IsVisibleInVariableManager = false
                        },
                        new BooleanParameter("IsRunning", true, "运行状态")
                        {
                            IsVisibleInVariableManager = true
                        },
                        new StringParameter("StopTime", "", "停止时间")
                        {
                            IsVisibleInVariableManager = false
                        },
                        new NumberParameter("TotalRunTime", 0, 999999, 0, "总运行时间(分钟)")
                        {
                            IsVisibleInVariableManager = false
                        },
                        new NumberParameter("TotalVolume", 0, 999999, 0, "总泵送量(mL)")
                        {
                            IsVisibleInVariableManager = true
                        },
                        new StringParameter("Message", "", "状态信息")
                        {
                            IsVisibleInVariableManager = false
                        }
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

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("Status", "OK", "") { IsVisibleInVariableManager = false },
                            new BooleanParameter("IsRunning", false, "") { IsVisibleInVariableManager = true },
                            new StringParameter("StopTime", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "") { IsVisibleInVariableManager = false },
                            new NumberParameter("TotalRunTime", 0, 999999, Math.Round(totalRunTime, 2), "总运行时间(分钟)") { IsVisibleInVariableManager = false },
                            new NumberParameter("TotalVolume", 0, 999999, Math.Round(totalVolume, 2), "总泵送量(mL)") { IsVisibleInVariableManager = true },
                            new StringParameter("Message", quickStop ? "紧急停止完成" : "正常停止完成", "") { IsVisibleInVariableManager = false }
                        }
                    };
                })
            });
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

        public enum PumpMode
        {
            Continuous,
            Timed,
            Volume,
            Intermittent
        }

        public override void Initialize()
        {
            InfoLog("开始初始化蠕动泵设备");

            try
            {
                // 调用基类初始化
                base.Initialize();

                // 初始化设备状态
                lock (_stateLock)
                {
                    _isRunning = false;
                    _currentSpeed = 0;
                    _currentFlowRate = 0;
                    _currentPressure = 0;
                    _currentTemperature = 25.0;
                    _totalVolume = 0;
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
                // 先停止设备运行
                lock (_stateLock)
                {
                    if (_isRunning)
                    {
                        _isRunning = false;
                        _currentSpeed = 0;
                        InfoLog("设备运行已停止");
                    }
                }

                // 调用基类关闭方法（会自动断开连接）
                base.Shutdown();

                // 释放资源
                ThreadLocalRandom?.Dispose();

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
            string comPort = Parameters.GetValue<string>("COM Port");
            int baudRate = Parameters.GetValue<int>("BaudRate");
            bool simulationMode = Parameters.GetValue<bool>("SimulationMode");

            if (simulationMode)
            {
                IsSimulationMode = true;
                InfoLog("以仿真模式连接");
                await Task.Delay(1000); // 模拟连接延迟
                return true;
            }

            try
            {
                if (string.IsNullOrEmpty(comPort))
                {
                    ErrorLog("未指定COM端口");
                    return false;
                }

                InfoLog($"正在连接到 {comPort}，波特率: {baudRate}");

                _serialPort = new SerialPort(comPort, baudRate)
                {
                    DataBits = 8,
                    Parity = Parity.None,
                    StopBits = StopBits.One,
                    ReadTimeout = 5000,
                    WriteTimeout = 5000
                };

                await Task.Run(() => _serialPort.Open());

                // 发送测试命令验证连接
                if (await TestConnection())
                {
                    IsSimulationMode = false;
                    InfoLog($"成功连接到 {comPort}");
                    return true;
                }
                else
                {
                    _serialPort?.Close();
                    ErrorLog("设备连接测试失败");
                    return false;
                }
            }
            catch (Exception ex)
            {
                ErrorLog($"连接失败: {ex.Message}");
                _serialPort?.Close();
                return false;
            }
        }
        protected override async Task<bool> OnDisconnectAsync()
        {
            try
            {
                // 如果设备正在运行，先停止
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

                IsSimulationMode = false;
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
                    // 仿真模式下随机模拟连接状态（95%概率正常）
                    await Task.Delay(50);
                    return ThreadLocalRandom.Value.NextDouble() > 0.05;
                }

                if (_serialPort == null || !_serialPort.IsOpen)
                {
                    return false;
                }

                // 发送心跳命令检查连接
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

                // 发送状态查询命令（具体命令根据设备协议调整）
                await Task.Run(() =>
                {
                    _serialPort.WriteLine("STATUS?");
                    Thread.Sleep(100);

                    // 简单检查是否有响应
                    if (_serialPort.BytesToRead > 0)
                    {
                        _serialPort.ReadExisting();
                        return true;
                    }
                    return false;
                });

                return true;
            }
            catch
            {
                return false;
            }
        }

        // 统一包装命令执行，记录开始/结束/异常日志
        private Func<DeviceParameters, DeviceParameters> WrapAction(string commandName, Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return (DeviceParameters parameters) =>
            {
                var paramText = FormatParameters(parameters);
                _logger.LogInformation($"[DeviceCommand-START] Device={Name} Command={commandName} Params={paramText}");

                var sw = Stopwatch.StartNew();
                try
                {
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();

                    var outputText = FormatParameters(output);
                    _logger.LogInformation($"[DeviceCommand-END]   Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds} Output={outputText}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[DeviceCommand-ERROR] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds} Params={paramText}");

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("Status", "ERROR", "") { IsVisibleInVariableManager = false },
                            new StringParameter("Message", ex.Message, "") { IsVisibleInVariableManager = false }
                        }
                    };
                }
            };
        }

        // 将参数格式化成易读的字符串
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

    /// <summary>
    /// 模拟引擎 - 生成真实的物理模拟数据
    /// </summary>
    public class SimulationEngine
    {
        private static long _globalCounter = 0;
        private readonly object _lock = new object();

        // 噪声生成器
        private readonly PerlinNoise _temperatureNoise;
        private readonly PerlinNoise _flowRateNoise;
        private readonly PerlinNoise _pressureNoise;

        // 设备状态历史
        private double _lastTemperature = 25.0;
        private double _lastPressure = 101.325;
        private double _accumulatedVolume = 0;
        private DateTime _lastUpdateTime = DateTime.Now;

        public SimulationEngine()
        {
            // 使用不同的种子确保各参数独立变化
            var baseHash = Guid.NewGuid().GetHashCode();
            _temperatureNoise = new PerlinNoise(baseHash);
            _flowRateNoise = new PerlinNoise(baseHash + 1);
            _pressureNoise = new PerlinNoise(baseHash + 2);
        }

        public double UpdateTemperature(double currentTemp, bool isRunning)
        {
            lock (_lock)
            {
                var time = GetUniqueTimeValue();
                var noise = _temperatureNoise.Sample(time * 0.01) * 2; // ±2度噪声

                if (isRunning)
                {
                    // 运行时温度上升，有一定惯性
                    var targetTemp = 45 + _temperatureNoise.Sample(time * 0.005) * 10; // 35-55度目标范围
                    _lastTemperature += (targetTemp - _lastTemperature) * 0.02 + noise * 0.5;
                    _lastTemperature = Math.Min(_lastTemperature, 85); // 限制最高温度
                }
                else
                {
                    // 停机时向环境温度回归
                    var ambientTemp = 22 + _temperatureNoise.Sample(time * 0.002) * 3; // 19-25度环境温度
                    _lastTemperature += (ambientTemp - _lastTemperature) * 0.01 + noise * 0.3;
                    _lastTemperature = Math.Max(_lastTemperature, 15); // 限制最低温度
                }

                return Math.Round(_lastTemperature, 2);
            }
        }

        public (double FlowRate, double TotalVolume) UpdateFlowRate(double targetSpeed, bool isRunning, DateTime startTime)
        {
            lock (_lock)
            {
                var now = DateTime.Now;
                var deltaTime = (now - _lastUpdateTime).TotalMinutes;
                _lastUpdateTime = now;

                if (!isRunning)
                {
                    return (0, _accumulatedVolume);
                }

                var time = GetUniqueTimeValue();
                var noise = _flowRateNoise.Sample(time * 0.02) * 5; // ±5 mL/min 噪声

                // 基于转速计算理论流量，加入脉动和磨损效应
                var theoreticalFlow = targetSpeed * 0.085; // 假设每RPM产生0.085 mL/min
                var pulsationFactor = 1 + Math.Sin(time * 0.1) * 0.05; // ±5% 脉动
                var actualFlow = theoreticalFlow * pulsationFactor + noise;

                // 累计体积计算
                if (deltaTime > 0)
                {
                    _accumulatedVolume += actualFlow * deltaTime;
                }

                return (Math.Max(0, Math.Round(actualFlow, 3)), Math.Round(_accumulatedVolume, 2));
            }
        }

        public double UpdatePressure(double currentFlowRate, bool isRunning, bool isRelativePressure)
        {
            lock (_lock)
            {
                var time = GetUniqueTimeValue();
                var noise = _pressureNoise.Sample(time * 0.03) * 8; // ±8 kPa 噪声

                if (!isRunning)
                {
                    _lastPressure = isRelativePressure ? 0 : 101.325 + noise * 0.1;
                }
                else
                {
                    // 压力与流量相关，模拟管路阻力
                    var basePressure = isRelativePressure ? 0 : 101.325;
                    var flowPressure = currentFlowRate * 0.8 + Math.Pow(currentFlowRate, 1.2) * 0.02; // 非线性阻力
                    var systemPressure = basePressure + flowPressure;

                    // 添加系统响应延迟
                    _lastPressure += (systemPressure - _lastPressure) * 0.1 + noise;
                }

                return Math.Round(Math.Max(0, _lastPressure), 2);
            }
        }

        public double GetActualSpeed(double targetSpeed)
        {
            var time = GetUniqueTimeValue();
            var noise = _flowRateNoise.Sample(time * 0.05) * 15; // ±15 RPM 噪声
            var efficiency = 0.96 + _flowRateNoise.Sample(time * 0.01) * 0.06; // 90-102% 效率

            return Math.Max(0, Math.Round(targetSpeed * efficiency + noise, 1));
        }

        private double GetUniqueTimeValue()
        {
            // 生成永不重复的时间值
            var counter = Interlocked.Increment(ref _globalCounter);
            var ticks = DateTime.UtcNow.Ticks;
            var threadId = Thread.CurrentThread.ManagedThreadId;

            // 结合时间戳、计数器和线程ID生成唯一值
            return (ticks / 10000.0) + counter * 0.001 + threadId * 0.0001;
        }
    }

    /// <summary>
    /// Perlin噪声生成器 - 生成自然的连续噪声
    /// </summary>
    public class PerlinNoise
    {
        private readonly int[] _permutation;
        private readonly int[] _p;

        public PerlinNoise(int seed = 0)
        {
            var random = new Random(seed);
            _permutation = new int[256];

            for (int i = 0; i < 256; i++)
            {
                _permutation[i] = i;
            }

            // Fisher-Yates shuffle
            for (int i = 255; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (_permutation[i], _permutation[j]) = (_permutation[j], _permutation[i]);
            }

            _p = new int[512];
            for (int i = 0; i < 512; i++)
            {
                _p[i] = _permutation[i % 256];
            }
        }

        public double Sample(double x)
        {
            int X = (int)Math.Floor(x) & 255;
            x -= Math.Floor(x);
            double u = Fade(x);

            return Lerp(u, Grad(_p[X], x), Grad(_p[X + 1], x - 1));
        }

        private static double Fade(double t)
        {
            return t * t * t * (t * (t * 6 - 15) + 10);
        }

        private static double Lerp(double t, double a, double b)
        {
            return a + t * (b - a);
        }

        private static double Grad(int hash, double x)
        {
            return (hash & 1) == 0 ? x : -x;
        }
    }
}