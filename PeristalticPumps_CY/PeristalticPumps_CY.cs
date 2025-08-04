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

namespace PeristalticPumps_CY
{
    [Export(typeof(IDevice))]
    public class PeristalticPumps_CY : Device
    {
        private SerialPort _serialPort;
        private readonly ILogService _logger;
        public PeristalticPumps_CY()
        {
            _logger = LogManager.GetLogger<PeristalticPumps_CY>();
            Name = "YT600-1J";
            Manufacturer = "蠕动泵_YT600-1J＆KZ35-13-A";
            Category = DeviceCategories.Pumps;
            AllowedRegions = new List<RegionType>() { RegionType.Feed, RegionType.PreHeat, RegionType.Reaction, RegionType.Quench, RegionType.PostProcess };
            // 也可以尝试直接指向输出目录中的文件
            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/PeristalticPumps_CY.png";
            // 添加参数
            Parameters.Variables.Add(new StringParameter("COM Port", SerialPort.GetPortNames().FirstOrDefault())
            {
                Options = new ObservableCollection<string>(SerialPort.GetPortNames()),
                HelpText = "The COM Port that the device is connected to."
            });
            Commands.Add(new DeviceCommand
            {
                Name = "获取温度值",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/temperature.png",
                HelpText = "测试",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                       new NumberParameter("Speed", 0, 10000, 25000, "Target Speed in Rpm")
                    }
                },
                Action = delegate (DeviceParameters p)
                {
                    double Speed = Parameters.GetValue<double>("Speed");
                    var basevalue = Tuple.Create("1", "2", "3", "4", "5", "6", "7");
                    DateTime startTime = DateTime.Now;
                    while ((DateTime.Now - startTime).TotalSeconds < 3)
                    {
                        // 执行一些无意义的计算来消耗CPU时间
                        double result = 0;
                        for (int i = 0; i < 10000000; i++)
                        {
                            result += Math.Sqrt(i);
                        }
                    }
                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                            {
                                new StringParameter("Result")
                                {
                                    Value = "OK"
                                },

                            }
                    };
                },

            });
            Commands.Add(new DeviceCommand
            {
                Name = "获取流量值",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/flowrate.png",
                HelpText = "执行此命令可以开启蠕动泵",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                       new NumberParameter("Speed", 0, 10000, 25000, "Target Speed in Rpm")
                    }
                },
                Action = delegate (DeviceParameters p)
                {
                    double Speed = Parameters.GetValue<double>("Speed");
                    var basevalue = Tuple.Create("1", "2", "3", "4", "5", "6", "7");
                    DateTime startTime = DateTime.Now;
                    while ((DateTime.Now - startTime).TotalSeconds < 3)
                    {
                        // 执行一些无意义的计算来消耗CPU时间
                        double result = 0;
                        for (int i = 0; i < 10000000; i++)
                        {
                            result += Math.Sqrt(i);
                        }
                    }
                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                            {
                                new StringParameter("Result")
                                {
                                    Value = "OK"
                                },

                            }
                    };
                },

            });
            Commands.Add(new DeviceCommand
            {
                Name = "获取压力值",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/pressure.png",
                HelpText = "执行此命令可以开启蠕动泵",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                       new NumberParameter("Speed", 0, 10000, 25000, "Target Speed in Rpm")
                    }
                },
                Action = delegate (DeviceParameters p)
                {
                    double Speed = Parameters.GetValue<double>("Speed");
                    var basevalue = Tuple.Create("1", "2", "3", "4", "5", "6", "7");
                    DateTime startTime = DateTime.Now;
                    while ((DateTime.Now - startTime).TotalSeconds < 3)
                    {
                        // 执行一些无意义的计算来消耗CPU时间
                        double result = 0;
                        for (int i = 0; i < 10000000; i++)
                        {
                            result += Math.Sqrt(i);
                        }
                    }
                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                            {
                                new StringParameter("Result")
                                {
                                    Value = "OK"
                                },

                            }
                    };
                },

            });
            Commands.Add(new DeviceCommand
            {
                Name = "开启",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/open.png",
                HelpText = "执行此命令可以开启蠕动泵",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                       new NumberParameter("Speed", 0, 10000, 25000, "Target Speed in Rpm")
                    }
                },
                Action = delegate (DeviceParameters p)
                {
                    double Speed = Parameters.GetValue<double>("Speed");
                    var basevalue = Tuple.Create("1", "2", "3", "4", "5", "6", "7");
                    DateTime startTime = DateTime.Now;
                    while ((DateTime.Now - startTime).TotalSeconds < 3)
                    {
                        // 执行一些无意义的计算来消耗CPU时间
                        double result = 0;
                        for (int i = 0; i < 10000000; i++)
                        {
                            result += Math.Sqrt(i);
                        }
                    }
                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                            {
                                new StringParameter("Result")
                                {
                                    Value = "OK"
                                },

                            }
                    };
                },

            });
            Commands.Add(new DeviceCommand
            {
                Name = "关闭",
                ImageLocation = "pack://siteoforigin:,,,/Resources/CommadIcon/off.png",
                HelpText = "执行此命令可以关闭蠕动泵",
                Action = delegate (DeviceParameters p)
                {
                    var basevalue = Tuple.Create("1", "2", "3", "4", "5", "6", "7");
                    DateTime startTime = DateTime.Now;
                    while ((DateTime.Now - startTime).TotalSeconds < 3)
                    {
                        // 执行一些无意义的计算来消耗CPU时间
                        double result = 0;
                        for (int i = 0; i < 10000000; i++)
                        {
                            result += Math.Sqrt(i);
                        }
                    }
                    return new DeviceParameters
                    {
                        Variables = new ObservableCollection<ParameterBase>
                            {
                                new StringParameter("Result")
                                {
                                    Value = "OK"
                                },

                            }
                    };
                },
            });
        }

        public override void Initialize()
        {
            base.Initialize();

            string comPort = Parameters.GetValue<string>("COM Port");

            try
            {
                _serialPort = new SerialPort(comPort, 9600);
                _serialPort.Open();
                  _logger.LogInformation($"Connected to {comPort}");
            }
            catch (Exception ex)
            {
                  _logger.LogInformation($"Error connecting to {comPort}: {ex.Message}");
            }
        }

        public override void Shutdown()
        {
            base.Shutdown();

            if (_serialPort != null && _serialPort.IsOpen)
            {
                _serialPort.Close();
                  _logger.LogInformation("Serial port closed");
            }
        }
    }
}
