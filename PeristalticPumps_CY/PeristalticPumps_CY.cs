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
        private Random _random = new Random();

        public PeristalticPumps_CY()
        {
            _logger = LogManager.GetLogger<PeristalticPumps_CY>();
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
            Parameters.Variables.Add(new StringParameter("COM Port", SerialPort.GetPortNames().FirstOrDefault())
            {
                Options = new ObservableCollection<string>(SerialPort.GetPortNames()),
                HelpText = "The COM Port that the device is connected to."
            });

            Parameters.Variables.Add(new NumberParameter("Max Speed", 0, 10000, 5000, "Maximum speed in RPM"));
            Parameters.Variables.Add(new NumberParameter("Max Pressure", 0, 1000, 500, "Maximum pressure in kPa"));

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
                            IsVisibleInVariableManager = true  // 温度值需要在变量管理器中显示
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
                            IsVisibleInVariableManager = true  // 警告状态需要在变量管理器中显示
                        },
                        new StringParameter("Message", "", "附加信息")
                        {
                            IsVisibleInVariableManager = false
                        }
                    }
                },
                Action = WrapAction("获取温度值", async delegate (DeviceParameters parameters)
                {
                    // 模拟执行时间 200-500ms
                    await Task.Delay(_random.Next(200, 500));

                    bool useCalibration = parameters.GetValue<bool>("UseCalibration");
                    double offset = parameters.GetValue<double>("Offset");

                    // 模拟温度变化（如果泵在运行，温度会上升）
                    if (_isRunning)
                    {
                        _currentTemperature += _random.NextDouble() * 0.5;
                        _currentTemperature = Math.Min(_currentTemperature, 85); // 最高85度
                    }
                    else
                    {
                        _currentTemperature -= _random.NextDouble() * 0.2;
                        _currentTemperature = Math.Max(_currentTemperature, 20); // 最低20度
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
                            IsVisibleInVariableManager = true  // 流量值需要在变量管理器中显示
                        },
                        new StringParameter("Unit", "mL/min", "流量单位")
                        {
                            IsVisibleInVariableManager = false
                        },
                        new NumberParameter("TotalVolume", 0, 999999, 0, "累计流量(mL)")
                        {
                            IsVisibleInVariableManager = true  // 累计流量需要在变量管理器中显示
                        },
                        new NumberParameter("CurrentSpeed", 0, 10000, 0, "当前转速(RPM)")
                        {
                            IsVisibleInVariableManager = true  // 转速需要在变量管理器中显示
                        },
                        new StringParameter("Timestamp", "", "采集时间")
                        {
                            IsVisibleInVariableManager = false
                        }
                    }
                },
                Action = WrapAction("获取流量值", async delegate (DeviceParameters parameters)
                {
                    // 模拟执行时间 300-600ms
                    await Task.Delay(_random.Next(300, 600));

                    var unit = parameters.GetValue<FlowRateUnit>("Unit");
                    double avgTime = parameters.GetValue<double>("AveragingTime");

                    // 模拟流量计算（基于转速）
                    _currentFlowRate = _isRunning ? _currentSpeed * 0.1 + _random.NextDouble() * 5 : 0;

                    // 单位转换
                    double displayFlowRate = unit switch
                    {
                        FlowRateUnit.LPerMin => _currentFlowRate / 1000,
                        FlowRateUnit.MLPerSec => _currentFlowRate / 60,
                        _ => _currentFlowRate
                    };

                    string unitStr = unit switch
                    {
                        FlowRateUnit.LPerMin => "L/min",
                        FlowRateUnit.MLPerSec => "mL/s",
                        _ => "mL/min"
                    };

                    // 计算累计流量
                    double totalVolume = 0;
                    if (_isRunning)
                    {
                        TimeSpan runTime = DateTime.Now - _startTime;
                        totalVolume = _currentFlowRate * runTime.TotalMinutes;
                    }

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("Status", "OK", "") { IsVisibleInVariableManager = false },
                            new NumberParameter("FlowRate", 0, 1000, Math.Round(displayFlowRate, 3), "当前流量") { IsVisibleInVariableManager = true },
                            new StringParameter("Unit", unitStr, "") { IsVisibleInVariableManager = false },
                            new NumberParameter("TotalVolume", 0, 999999, Math.Round(totalVolume, 2), "累计流量(mL)") { IsVisibleInVariableManager = true },
                            new NumberParameter("CurrentSpeed", 0, 10000, _currentSpeed, "当前转速(RPM)") { IsVisibleInVariableManager = true },
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
                            IsVisibleInVariableManager = true  // 压力值需要在变量管理器中显示
                        },
                        new StringParameter("Unit", "kPa", "压力单位")
                        {
                            IsVisibleInVariableManager = false
                        },
                        new NumberParameter("MaxPressure", 0, 1000, 0, "最大压力记录")
                        {
                            IsVisibleInVariableManager = false  // 记录值不需要显示
                        },
                        new NumberParameter("MinPressure", 0, 1000, 0, "最小压力记录")
                        {
                            IsVisibleInVariableManager = false  // 记录值不需要显示
                        },
                        new BooleanParameter("IsOverPressure", false, "是否超压")
                        {
                            IsVisibleInVariableManager = true  // 超压警告需要在变量管理器中显示
                        },
                        new StringParameter("Timestamp", "", "采集时间")
                        {
                            IsVisibleInVariableManager = false
                        }
                    }
                },
                Action = WrapAction("获取压力值", async delegate (DeviceParameters parameters)
                {
                    // 模拟执行时间 250-550ms
                    await Task.Delay(_random.Next(250, 550));

                    var unit = parameters.GetValue<PressureUnit>("Unit");
                    bool isRelative = parameters.GetValue<bool>("RelativePressure");

                    // 模拟压力（基于流量和运行状态）
                    if (_isRunning)
                    {
                        _currentPressure = 100 + _currentFlowRate * 0.5 + _random.NextDouble() * 20;
                    }
                    else
                    {
                        _currentPressure = isRelative ? 0 : 101.325; // 大气压
                    }

                    // 单位转换
                    double displayPressure = unit switch
                    {
                        PressureUnit.Bar => _currentPressure / 100,
                        PressureUnit.PSI => _currentPressure * 0.145038,
                        PressureUnit.MPa => _currentPressure / 1000,
                        _ => _currentPressure
                    };

                    string unitStr = unit.ToString();
                    bool isOverPressure = _currentPressure > 500;

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
                            IsVisibleInVariableManager = true  // 运行状态需要在变量管理器中显示
                        },
                        new NumberParameter("ActualSpeed", 0, 10000, 0, "实际转速(RPM)")
                        {
                            IsVisibleInVariableManager = true  // 实际转速需要在变量管理器中显示
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

                    // 模拟加速过程，执行时间与加速时间相关
                    int simulatedRampTime = Math.Min((int)(rampTime * 1000), 5000); // 最多5秒
                    await Task.Delay(simulatedRampTime);

                    _isRunning = true;
                    _currentSpeed = targetSpeed;
                    _startTime = DateTime.Now;

                    // 添加一些随机偏差
                    double actualSpeed = targetSpeed + (_random.NextDouble() - 0.5) * 10;

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("Status", "OK", "") { IsVisibleInVariableManager = false },
                            new BooleanParameter("IsRunning", _isRunning, "") { IsVisibleInVariableManager = true },
                            new NumberParameter("ActualSpeed", 0, 10000, actualSpeed, "实际转速(RPM)") { IsVisibleInVariableManager = true },
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
                            IsVisibleInVariableManager = true  // 运行状态需要在变量管理器中显示
                        },
                        new StringParameter("StopTime", "", "停止时间")
                        {
                            IsVisibleInVariableManager = false
                        },
                        new NumberParameter("TotalRunTime", 0, 999999, 0, "总运行时间(分钟)")
                        {
                            IsVisibleInVariableManager = false  // 统计信息不需要显示
                        },
                        new NumberParameter("TotalVolume", 0, 999999, 0, "总泵送量(mL)")
                        {
                            IsVisibleInVariableManager = true  // 总泵送量可能需要在变量管理器中显示
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

                    if (_isRunning)
                    {
                        TimeSpan runTime = DateTime.Now - _startTime;
                        totalRunTime = runTime.TotalMinutes;
                        totalVolume = _currentFlowRate * totalRunTime;

                        if (!quickStop)
                        {
                            // 模拟减速过程
                            int simulatedDecelTime = Math.Min((int)(decelTime * 1000), 3000); // 最多3秒
                            await Task.Delay(simulatedDecelTime);
                        }
                        else
                        {
                            // 紧急停止也需要一点时间
                            await Task.Delay(100);
                        }
                    }

                    _isRunning = false;
                    _currentSpeed = 0;
                    _currentFlowRate = 0;

                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                        {
                            new StringParameter("Status", "OK", "") { IsVisibleInVariableManager = false },
                            new BooleanParameter("IsRunning", _isRunning, "") { IsVisibleInVariableManager = true },
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
            base.Initialize();

            string comPort = Parameters.GetValue<string>("COM Port");

            try
            {
                if (!string.IsNullOrEmpty(comPort))
                {
                    _serialPort = new SerialPort(comPort, 9600);
                    _serialPort.Open();
                    _logger.LogInformation($"Connected to {comPort}");
                }
                else
                {
                    _logger.LogWarning("No COM port specified, running in simulation mode");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error connecting to {comPort}: {ex.Message}");
                _logger.LogInformation("Running in simulation mode");
            }
        }

        public override void Shutdown()
        {
            base.Shutdown();

            // 如果泵还在运行，先停止
            if (_isRunning)
            {
                _isRunning = false;
                _currentSpeed = 0;
                _logger.LogInformation("Pump stopped during shutdown");
            }

            if (_serialPort != null && _serialPort.IsOpen)
            {
                _serialPort.Close();
                _logger.LogInformation("Serial port closed");
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
                    // 同步等待异步方法完成
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

                    // 返回错误状态的输出参数
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
}