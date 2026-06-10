using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.IO;
using System.IO.Ports;
using System.Linq;
using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;

namespace SampleReactorDevice.Devices.Samples
{
    [Export(typeof(IDevice))]
    public class SampleReactorDevice : Device
    {
        private SerialPort _serialPort;
        private readonly ILogService _logger;
        public SampleReactorDevice()
        {
            Name = "储液罐";
            Manufacturer = "SmartProcess反应釜";
            Category = DeviceCategories.Reactors;
            AllowedRegions = new List<RegionType>() { RegionType.Reaction };
            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/SampleReactor.png";
            // 添加设备级参数（这些是设备配置参数，不是命令参数）
            Parameters.Variables.Add(new StringParameter("COM Port", SerialPort.GetPortNames().FirstOrDefault())
            {
                Options = new ObservableCollection<string>(SerialPort.GetPortNames()),
                HelpText = "设备连接的COM端口"
            });

            Parameters.Variables.Add(new NumberParameter("Baudrate", 1200, 115200, 9600, "波特率")
            {
                HelpText = "串口通信波特率"
            });

            // 设置温度命令
            Commands.Add(new DeviceCommand
            {
                Name = "设置温度",
                ImageLocation = ImageLocation,
                HelpText = "设置反应釜的目标温度",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("目标温度", 0, 200, 25)
                        {
                            HelpText = "要设置的目标温度值 (°C)"
                        }
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("执行结果")
                        {
                            HelpText = "命令执行结果"
                        },
                        new NumberParameter("设置温度值")
                        {
                            HelpText = "实际设置的温度值"
                        }
                    }
                },
                Action = SetTemperature
            });

            // 设置压力命令
            Commands.Add(new DeviceCommand
            {
                Name = "设置压力",
                ImageLocation = ImageLocation,
                HelpText = "设置反应釜的目标压力",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("目标压力", 0, 10, 1)
                        {
                            HelpText = "要设置的目标压力值 (bar)"
                        }
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("执行结果")
                        {
                            HelpText = "命令执行结果"
                        },
                        new NumberParameter("设置压力值")
                        {
                            HelpText = "实际设置的压力值"
                        }
                    }
                },
                Action = SetPressure
            });

            // 开始反应命令
            Commands.Add(new DeviceCommand
            {
                Name = "开始反应",
                ImageLocation = ImageLocation,
                HelpText = "启动反应过程",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("反应温度", 0, 200, 80)
                        {
                            HelpText = "反应温度 (°C)"
                        },
                        new NumberParameter("反应压力", 0, 10, 2)
                        {
                            HelpText = "反应压力 (bar)"
                        },
                        new NumberParameter("反应时间", 1, 1440, 60)
                        {
                            HelpText = "反应时间 (分钟)"
                        }
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("执行结果")
                        {
                            HelpText = "命令执行结果"
                        },
                        new StringParameter("反应状态")
                        {
                            HelpText = "当前反应状态"
                        },
                        new NumberParameter("当前温度")
                        {
                            HelpText = "当前实际温度"
                        },
                        new NumberParameter("当前压力")
                        {
                            HelpText = "当前实际压力"
                        }
                    }
                },
                Action = StartReaction
            });

            // 停止反应命令
            Commands.Add(new DeviceCommand
            {
                Name = "停止反应",
                ImageLocation = ImageLocation,
                HelpText = "停止反应过程",
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("执行结果")
                        {
                            HelpText = "命令执行结果"
                        },
                        new StringParameter("反应状态")
                        {
                            HelpText = "当前反应状态"
                        }
                    }
                },
                Action = StopReaction
            });

            // 读取状态命令
            Commands.Add(new DeviceCommand
            {
                Name = "读取状态",
                ImageLocation = ImageLocation,
                HelpText = "读取反应釜当前状态",
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("当前温度")
                        {
                            HelpText = "当前实际温度 (°C)"
                        },
                        new NumberParameter("当前压力")
                        {
                            HelpText = "当前实际压力 (bar)"
                        },
                        new StringParameter("设备状态")
                        {
                            HelpText = "设备运行状态"
                        },
                        new BooleanParameter("加热状态")
                        {
                            HelpText = "是否正在加热"
                        },
                        new BooleanParameter("搅拌状态")
                        {
                            HelpText = "是否正在搅拌"
                        }
                    }
                },
                Action = ReadStatus
            });
        }

        private DeviceParameters SetTemperature(DeviceParameters inputParams)
        {
            try
            {
                double targetTemp = inputParams.GetValue<double>("目标温度");

                _logger.LogInformation($"设置温度到 {targetTemp}°C");

                // 模拟发送命令到设备
                if (_serialPort?.IsOpen == true)
                {
                    string command = $"SET_TEMP:{targetTemp}\r\n";
                    _serialPort.Write(command);

                    // 模拟等待设备响应
                    System.Threading.Thread.Sleep(100);
                }

                return new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("执行结果") { Value = "成功" },
                        new NumberParameter("设置温度值") { Value = targetTemp }
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogInformation($"设置温度失败: {ex.Message}");
                return new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("执行结果") { Value = $"失败: {ex.Message}" },
                        new NumberParameter("设置温度值") { Value = 0 }
                    }
                };
            }
        }

        private DeviceParameters SetPressure(DeviceParameters inputParams)
        {
            try
            {
                double targetPressure = inputParams.GetValue<double>("目标压力");

                _logger.LogInformation($"设置压力到 {targetPressure} bar");

                // 模拟发送命令到设备
                if (_serialPort?.IsOpen == true)
                {
                    string command = $"SET_PRESS:{targetPressure}\r\n";
                    _serialPort.Write(command);

                    // 模拟等待设备响应
                    System.Threading.Thread.Sleep(100);
                }

                return new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("执行结果") { Value = "成功" },
                        new NumberParameter("设置压力值") { Value = targetPressure }
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogInformation($"设置压力失败: {ex.Message}");
                return new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("执行结果") { Value = $"失败: {ex.Message}" },
                        new NumberParameter("设置压力值") { Value = 0 }
                    }
                };
            }
        }

        private DeviceParameters StartReaction(DeviceParameters inputParams)
        {
            try
            {
                double reactionTemp = inputParams.GetValue<double>("反应温度");
                double reactionPressure = inputParams.GetValue<double>("反应压力");
                double reactionTime = inputParams.GetValue<double>("反应时间");

                _logger.LogInformation($"开始反应: 温度{reactionTemp}°C, 压力{reactionPressure}bar, 时间{reactionTime}分钟");

                // 模拟发送命令到设备
                if (_serialPort?.IsOpen == true)
                {
                    string command = $"START_REACTION:{reactionTemp}:{reactionPressure}:{reactionTime}\r\n";
                    _serialPort.Write(command);

                    // 模拟等待设备响应
                    System.Threading.Thread.Sleep(200);
                }

                // 模拟当前读数
                Random rand = new Random();
                double currentTemp = reactionTemp - 5 + rand.NextDouble() * 10; // 模拟温度波动
                double currentPressure = reactionPressure - 0.2 + rand.NextDouble() * 0.4; // 模拟压力波动

                return new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("执行结果") { Value = "成功" },
                        new StringParameter("反应状态") { Value = "反应中" },
                        new NumberParameter("当前温度") { Value = Math.Round(currentTemp, 1) },
                        new NumberParameter("当前压力") { Value = Math.Round(currentPressure, 2) }
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogInformation($"启动反应失败: {ex.Message}");
                return new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("执行结果") { Value = $"失败: {ex.Message}" },
                        new StringParameter("反应状态") { Value = "故障" },
                        new NumberParameter("当前温度") { Value = 0 },
                        new NumberParameter("当前压力") { Value = 0 }
                    }
                };
            }
        }

        private DeviceParameters StopReaction(DeviceParameters inputParams)
        {
            try
            {
                _logger.LogInformation("停止反应");

                // 模拟发送命令到设备
                if (_serialPort?.IsOpen == true)
                {
                    _serialPort.Write("STOP_REACTION\r\n");
                    System.Threading.Thread.Sleep(100);
                }

                return new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("执行结果") { Value = "成功" },
                        new StringParameter("反应状态") { Value = "已停止" }
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogInformation($"停止反应失败: {ex.Message}");
                return new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("执行结果") { Value = $"失败: {ex.Message}" },
                        new StringParameter("反应状态") { Value = "未知" }
                    }
                };
            }
        }

        private DeviceParameters ReadStatus(DeviceParameters inputParams)
        {
            try
            {
                _logger.LogInformation("读取设备状态");

                // 模拟发送命令到设备
                if (_serialPort?.IsOpen == true)
                {
                    _serialPort.Write("READ_STATUS\r\n");
                    System.Threading.Thread.Sleep(100);
                }

                // 模拟读取设备状态数据
                Random rand = new Random();
                double currentTemp = 20 + rand.NextDouble() * 80; // 20-100°C
                double currentPressure = 0.8 + rand.NextDouble() * 0.4; // 0.8-1.2 bar
                bool isHeating = rand.Next(2) == 1;
                bool isStirring = rand.Next(2) == 1;
                string deviceStatus = isHeating ? "运行中" : "待机";

                return new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("当前温度") { Value = Math.Round(currentTemp, 1) },
                        new NumberParameter("当前压力") { Value = Math.Round(currentPressure, 2) },
                        new StringParameter("设备状态") { Value = deviceStatus },
                        new BooleanParameter("加热状态") { Value = isHeating },
                        new BooleanParameter("搅拌状态") { Value = isStirring }
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogInformation($"读取状态失败: {ex.Message}");
                return new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("当前温度") { Value = 0 },
                        new NumberParameter("当前压力") { Value = 0 },
                        new StringParameter("设备状态") { Value = $"错误: {ex.Message}" },
                        new BooleanParameter("加热状态") { Value = false },
                        new BooleanParameter("搅拌状态") { Value = false }
                    }
                };
            }
        }

        public override void Initialize()
        {
            _logger.LogInformation("蠕动泵设备初始化");
            base.Initialize();

            string comPort = Parameters.GetValue<string>("COM Port");
            int baudrate = Parameters.GetValue<int>("Baudrate");

            try
            {
                _serialPort = new SerialPort(comPort, baudrate);
                _serialPort.Open();
                _logger.LogInformation($"已连接到 {comPort}, 波特率: {baudrate}");
            }
            catch (Exception ex)
            {
                _logger.LogInformation($"连接 {comPort} 失败: {ex.Message}");
            }
        }

        public override void Shutdown()
        {
            base.Shutdown();

            if (_serialPort != null && _serialPort.IsOpen)
            {
                _serialPort.Close();
                _logger.LogInformation("串口连接已关闭");
            }
        }
    }
}