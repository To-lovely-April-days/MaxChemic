using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO.Ports;

namespace HighPreactor_ModbusRTU
{
    [Export(typeof(IDevice))]
    public class HighPreactor_ModbusRTU : Device, IFlowLifecycleAware, IDeviceScreen
    {
        private readonly ILogService _logger;

        // 通信层重试参数
        private const int MaxRetries = 3;       // 总共尝试 3 次
        private const int RetryDelayMs = 100;   // 重试间隔 100ms

        // 实例级 TCP 互斥锁:同一设备实例的所有 IO 串行化,避免与轮询/心跳撞车
        // 用静态字典按 DeviceId 隔离不同设备实例
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _instanceLocks = new ConcurrentDictionary<string, SemaphoreSlim>();


        public HighPreactor_ModbusRTU()
        {
            _logger = LogManager.GetLogger<HighPreactor_ModbusRTU>();

            DeviceId = "HP0001";
            Name = "微型反应釜";
            Manufacturer = "ModbusRTU 微型反应釜";
            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/PeristalticPumps_CY.png";
            Category = DeviceCategories.Reactors;
            ComId = "HP0001"; // 默认设备ID
            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();
            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            Parameters.Variables.Add(new StringParameter("DeviceId", "HP0001", "ModbusRTU设备ID")
            {
                Options = new ObservableCollection<string>() { "HP0001", "HP0002", "HP0003", "HP0004", "HP0005" },
            });

            Parameters.Variables.Add(new NumberParameter("SlaveId", 1, 254, 1, "从站ID"));

            Parameters.Variables.Add(new StringParameter("串口号", "COM1")
            {
                Options = new ObservableCollection<string>() { "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7" }
            });

            Parameters.Variables.Add(new StringParameter("波特率", "9600")
            {
                Options = new ObservableCollection<string>() { "4800", "9600", "19200", "38400", "57600", "115200", "230400" }
            });

            Parameters.Variables.Add(new StringParameter("校验位", "None")
            {
                Options = new ObservableCollection<string>() { "None", "Odd", "Even", "Mark", "Space" }
            });

            Parameters.Variables.Add(new StringParameter("数据位", "8")
            {
                Options = new ObservableCollection<string>() { "5", "6", "7", "8" }
            });

            Parameters.Variables.Add(new StringParameter("停止位", "One")
            {
                Options = new ObservableCollection<string>() { "None", "One", "Two", "OnePointFive" }
            });

            // 通信方式
            Parameters.Variables.Add(new StringParameter("通信方式", "Direct", "通信方式")
            {
                Options = new ObservableCollection<string>() { "Direct", "PLC", "ModbusTcp", "ZLanGateway" }
            });

            // Modbus 从机站号 (1~254)
            Parameters.Variables.Add(new NumberParameter("Modbus站号", 1, 254, 1, "Modbus 从机站号 (1~254)"));

            // 初始化命令
            InitializeCommands();
        }


        /// <summary>
        /// 声明本驱动支持通过 ZLAN 网关通信。
        /// </summary>
        public override bool SupportsZLanGateway => true;

        public string ScreenKey => "HighPreactorScreen";


        /// <summary>
        /// 初始化设备命令
        /// </summary>
        private void InitializeCommands()
        {
            // 获取釜温 PV
            Commands.Add(new DeviceCommand()
            {
                Name = "获取釜温",
                HelpText = "获取微型反应釜的温度",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("BathTemperature",-200,600,0,"反应釜釜温",true,"℃")
                    }
                },

                Action = WrapAction("获取釜温", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        float temperature = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            temperature = new Random().Next(-3, 6) * 100; // 模拟 -200 到 600 度
                            InfoLog($"模拟模式获取微型反应釜温度，结果（{temperature} ℃）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x00, 0x00, 0x00, 0x02 };
                            byte[] frame = BuildReadCmd(0, 2);

                            temperature = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt32Async);

                            InfoLog($"获取微型反应釜温度，结果（{temperature / 100} ℃）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("BathTemperature",-200,600,temperature/100,"反应釜釜温",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜的温度失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取炉温 PV
            Commands.Add(new DeviceCommand()
            {
                Name = "获取炉温",
                HelpText = "获取微型反应釜的炉温",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("FurnaceTemperature",-200,600,0,"反应釜炉温",true,"℃")
                    }
                },

                Action = WrapAction("获取炉温", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        int temperature = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            temperature = new Random().Next(-3, 6) * 100; // 模拟 -200 到 600 度
                            InfoLog($"模拟模式获取微型反应釜炉温，结果（{temperature} ℃）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x00, 0x04, 0x00, 0x02 };
                            byte[] frame = BuildReadCmd(4, 2);
                            temperature = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt32Async);

                            InfoLog($"获取微型反应釜炉温，结果（{temperature / 100.0} ℃）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("FurnaceTemperature",-200,600,temperature/100.0,"反应釜炉温",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜的炉温失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取压力 PV
            Commands.Add(new DeviceCommand()
            {
                Name = "获取压力",
                HelpText = "获取微型反应釜的压力",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("Pressure",-200,600,0,"反应釜压力",true,"Bar")
                    }
                },

                Action = WrapAction("获取压力", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        int temperature = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            temperature = new Random().Next(-1, 160);
                            InfoLog($"模拟模式获取微型反应釜压力，结果（{temperature} Bar）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x01, 0x06, 0x00, 0x01 };
                            byte[] frame = BuildReadCmd(0x0106, 1);
                            temperature = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt16Async);

                            InfoLog($"获取微型反应釜压力，结果（{temperature / 100.0} Bar）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("Pressure",-200,600,temperature/100.0,"反应釜压力",true,"Bar")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜的压力失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取搅拌速度 PV
            Commands.Add(new DeviceCommand()
            {
                Name = "获取搅拌速度",
                HelpText = "获取微型反应釜的搅拌速度",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("Speed PV", 0, 1500,0,"反应釜搅拌速度",true,"RPM")
                    }
                },

                Action = WrapAction("获取搅拌速度", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        int temperature = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            temperature = new Random().Next(-1, 15)*100;
                            InfoLog($"模拟模式获取微型反应釜搅拌速度，结果（{temperature} RPM）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x00, 0xFB, 0x00, 0x01 };
                            byte[] frame = BuildReadCmd(0x00FB, 1);
                            temperature = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt16Async);

                            InfoLog($"获取微型反应釜搅拌速度，结果（{temperature} RPM）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("Speed PV", 0, 1500,temperature,"反应釜搅拌速度",true,"RPM")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜的搅拌速度失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取设定温度
            Commands.Add(new DeviceCommand()
            {
                Name = "获取设定温度",
                HelpText = "获取微型反应釜的设定温度",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("SettingTemperature",-200,600,0,"反应釜设定温度",true,"℃")
                    }
                },

                Action = WrapAction("获取设定温度", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        int temperature = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            temperature = new Random().Next(-3, 6) * 100; // 模拟 -200 到 600 度
                            InfoLog($"模拟模式获取微型反应釜设定温度，结果（{temperature} ℃）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x00, 0x32, 0x00, 0x02 };
                            byte[] frame = BuildReadCmd(0x0032, 2);
                            temperature = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt32Async);

                            InfoLog($"获取微型反应釜设定温度，结果（{temperature / 100.0} ℃）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("SettingTemperature",-200,600,temperature/100.0,"反应釜设定温度",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜的设定温度失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置设定温度
            Commands.Add(new DeviceCommand()
            {
                Name = "设置设定温度",
                HelpText = "设置微型反应釜的设定温度",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetTemperature", -200, 600, 0, "设定温度的目标值", true, "℃")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue",-200,600,0,"设定温度的设定值",true,"℃")
                    }
                },

                Action = WrapAction("设置设定温度", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        float temperature = parameter.GetValue<float>("TargetTemperature");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置微型反应釜设定温度，结果（成功：{success}，设定值：{temperature} ℃）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x10, 0x00, 0x32, 0x00, 0x02, 0x04, data[2], data[3], data[0], data[1] };
                            byte[] frame = BuildMultWriteCmd(0x0032, (int)(temperature * 100));

                            // 写入指令
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜设定温度，结果（成功：{success}，设定值：{temperature} ℃）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", false, "设置成功", true),
                                new NumberParameter("SetTargetValue",-200,600,temperature,"设定温度的设定值",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微型反应釜的设定温度失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取搅拌速度 SV
            Commands.Add(new DeviceCommand()
            {
                Name = "获取搅拌速度 SV",
                HelpText = "获取微型反应釜的搅拌速度 SV",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("Speed SV", 0, 1500,0,"反应釜搅拌速度 SV",true,"RPM")
                    }
                },

                Action = WrapAction("获取搅拌速度 SV", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        int temperature = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            temperature = new Random().Next(-1, 15) * 100;
                            InfoLog($"模拟模式获取微型反应釜搅拌速度 SV，结果（{temperature} RPM）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x00, 0xFC, 0x00, 0x01 };
                            byte[] frame = BuildReadCmd(0x00FC, 1);
                            temperature = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt16Async);

                            InfoLog($"获取微型反应釜搅拌速度 SV，结果（{temperature} RPM）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("Speed SV", 0, 1500,temperature,"反应釜搅拌速度 SV",true,"RPM")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜的搅拌速度 SV 失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置搅拌速度 SV
            Commands.Add(new DeviceCommand()
            {
                Name = "设置搅拌速度 SV",
                HelpText = "设置微型反应釜的搅拌速度 SV",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetSpeed", 0, 1500, 0, "搅拌速度 SV 目标值", true, "RPM")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false,"设置成功",true),
                        new NumberParameter("SetTargetSpeed", 0, 1500, 0, "搅拌速度 SV 设置值", true, "RPM")
                    }
                },

                Action = WrapAction("设置搅拌速度 SV", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        ushort temperature = parameter.GetValue<ushort>("TargetSpeed");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            InfoLog($"模拟模式设置微型反应釜搅拌速度 SV，结果（成功：{success}，设定值：{temperature} RPM）");
                        }
                        else
                        {
                            // 创建指令
                            //byte[] frame = { slaveId, 0x06, 0x00, 0xFC, data[0], data[1] };
                            byte[] frame = BuildWriteCmd(0x00FC, temperature);

                            // 写入指令
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜设定温度，结果（成功：{success}，设定值：{temperature} RPM）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success,"设置成功",true),
                                new NumberParameter("SetTargetSpeed", 0, 1500, temperature, "搅拌速度 SV 设置值", true, "RPM")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微型反应釜的搅拌速度 SV 失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取釜温上限
            Commands.Add(new DeviceCommand()
            {
                Name = "获取釜温上限",
                HelpText = "获取微型反应釜的温度上限",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("BathUpperLimit",-200,600,0,"反应釜釜温上限",true,"℃")
                    }
                },

                Action = WrapAction("获取釜温上限", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        int temperature = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            temperature = new Random().Next(-3, 6) * 100; // 模拟 -200 到 600 度
                            InfoLog($"模拟模式获取微型反应釜温度上限，结果（{temperature} ℃）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x01, 0x0E, 0x00, 0x02 };
                            byte[] frame = BuildReadCmd(0x010E, 2);

                            temperature = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt32Async);

                            InfoLog($"获取微型反应釜温度上限，结果（{temperature / 100.0} ℃）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("BathUpperLimit",-200,600,temperature/100.0,"反应釜釜温上限",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜的温度上限失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置釜温上限
            Commands.Add(new DeviceCommand()
            {
                Name = "设置釜温上限",
                HelpText = "设置微型反应釜的温度上限",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetTemperature", -200, 600, 0, "温度上限目标值", true, "℃")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetUpperLimit",-200,600,0,"反应釜釜温上限",true,"℃")
                    }
                },

                Action = WrapAction("设置釜温上限", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        float temperature = parameter.GetValue<float>("TargetTemperature");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置微型反应釜温度上限，结果（成功：{success}，设定值：{temperature} ℃）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x10, 0x01, 0x0E, 0x00, 0x02, 0x04, data[2], data[3], data[0], data[1] };
                            byte[] frame = BuildMultWriteCmd(0x010E, (int)(temperature * 100));

                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜温度上限，结果（成功：{success}，设定值：{temperature} ℃）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetUpperLimit",-200,600,temperature,"反应釜釜温上限",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微型反应釜的温度上限失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取炉温上限
            Commands.Add(new DeviceCommand()
            {
                Name = "获取炉温上限",
                HelpText = "获取微型反应釜的炉温上限",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("FuranceUpperLimit",-200,600,0,"反应釜炉温上限",true,"℃")
                    }
                },

                Action = WrapAction("获取炉温上限", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        int temperature = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            temperature = new Random().Next(-3, 6) * 100; // 模拟 -200 到 600 度
                            InfoLog($"模拟模式获取微型反应釜炉温上限，结果（{temperature} ℃）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x01, 0x10, 0x00, 0x02 };
                            byte[] frame = BuildReadCmd(0x0110, 2);

                            temperature = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt32Async);

                            InfoLog($"获取微型反应釜炉温上限，结果（{temperature / 100.0} ℃）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("FuranceUpperLimit",-200,600,temperature/100.0,"反应釜炉温上限",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜的炉温上限失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置炉温上限
            Commands.Add(new DeviceCommand()
            {
                Name = "设置炉温上限",
                HelpText = "设置微型反应釜的炉温上限",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetUpperLimit", -200, 600, 0, "炉温上限目标值", true, "℃")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetUpperLimit",-200,600,0,"反应釜炉温上限设定值",true,"℃")
                    }
                },

                Action = WrapAction("设置炉温上限", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        float temperature = parameter.GetValue<float>("TargetUpperLimit");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置微型反应釜炉温上限，结果（成功：{success}，设定值：{temperature} ℃）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x10, 0x01, 0x10, 0x00, 0x02, 0x04, data[2], data[3], data[0], data[1] };
                            byte[] frame = BuildMultWriteCmd(0x0110, (int)(temperature * 100));

                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜炉温上限，结果（成功：{success}，设定值：{temperature} ℃）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetUpperLimit",-200,600,temperature,"反应釜炉温上限设定值",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微型反应釜的炉温上限失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取压力上限
            Commands.Add(new DeviceCommand()
            {
                Name = "获取压力上限",
                HelpText = "获取微型反应釜的压力上限",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("PressureUpperLimit",-1,160,0,"反应釜压力上限",true,"Bar")
                    }
                },

                Action = WrapAction("获取压力上限", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        int temperature = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            temperature = new Random().Next(-1, 160);
                            InfoLog($"模拟模式获取微型反应釜压力上限，结果（{temperature} Bar）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x01, 0x12, 0x00, 0x01 };
                            byte[] frame = BuildReadCmd(0x0112, 1);
                            temperature = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt16Async);

                            InfoLog($"获取微型反应釜压力上限，结果（{temperature / 100.0} Bar）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("PressureUpperLimit",-1,160,temperature/100.0,"反应釜压力上限",true,"Bar")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜的压力上限失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置压力上限
            Commands.Add(new DeviceCommand()
            {
                Name = "设置压力上限",
                HelpText = "设置微型反应釜的压力上限",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetPressure", -1, 160, 0, "压力上限目标值", true, "Bar")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false,"设置成功",true),
                        new NumberParameter("SetTarget", -1, 160, 0, "压力上限设置值", true, "Bar")
                    }
                },

                Action = WrapAction("设置压力上限", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        float temperature = parameter.GetValue<float>("TargetPressure");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置微型反应釜压力上限，结果（成功：{success}，设定值：{temperature} Bar）");
                        }
                        else
                        {
                            // 创建指令
                            //byte[] frame = { slaveId, 0x06, 0x01, 0x12, data[0], data[1] };
                            byte[] frame = BuildWriteCmd(0x0112, (ushort)(temperature * 100));

                            // 写入指令
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜压力上限，结果（成功：{success}，设定值：{temperature} Bar）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success,"设置成功",true),
                                new NumberParameter("SetTarget", -1, 160, temperature, "压力上限设置值", true, "Bar")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微型反应釜的压力上限失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取转换拟合阈值
            Commands.Add(new DeviceCommand()
            {
                Name = "获取转换拟合阈值",
                HelpText = "获取微型反应釜的转换拟合阈值",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TranslateFitThreshold", 0.1, 30, 0,"反应釜转换拟合阈值",true,"℃")
                    }
                },

                Action = WrapAction("获取转换拟合阈值", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        int temperature = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            temperature = new Random().Next(-1, 160);
                            InfoLog($"模拟模式获取微型反应釜转换拟合阈值，结果（{temperature} ℃）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x00, 0x7F, 0x00, 0x01 };
                            byte[] frame = BuildReadCmd(0x007F, 1);

                            temperature = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt16Async);

                            InfoLog($"获取微型反应釜转换拟合阈值，结果（{temperature / 100.0} ℃）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("TranslateFitThreshold", 0.1, 30, temperature/100.0,"反应釜转换拟合阈值",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜的转换拟合阈值失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置转换拟合阈值
            Commands.Add(new DeviceCommand()
            {
                Name = "设置转换拟合阈值",
                HelpText = "设置微型反应釜的转换拟合阈值",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", 0.1, 30, 0, "转换拟合阈值目标值", true, "℃")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false,"设置成功",true),
                        new NumberParameter("SetTarget", 0.1, 30, 0, "转换拟合阈值设置值", true, "℃")
                    }
                },

                Action = WrapAction("设置转换拟合阈值", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        float temperature = parameter.GetValue<float>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置微型反应釜转换拟合阈值，结果（成功：{success}，设定值：{temperature} ℃）");
                        }
                        else
                        {
                            // 创建指令
                            //byte[] frame = { slaveId, 0x06, 0x00, 0x7F, data[0], data[1] };
                            byte[] frame = BuildWriteCmd(0x007F, (ushort)(temperature * 100));

                            // 写入指令
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜转换拟合阈值，结果（成功：{success}，设定值：{temperature} ℃）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success,"设置成功",true),
                                new NumberParameter("SetTarget", 0.1, 30, temperature, "转换拟合阈值设置值", true, "℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微型反应釜的转换拟合阈值失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取升温斜率
            Commands.Add(new DeviceCommand()
            {
                Name = "获取升温斜率",
                HelpText = "获取微型反应釜的升温斜率",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TempSlope", 0.01, 10, 0,"反应釜升温斜率",true,"℃")
                    }
                },

                Action = WrapAction("获取升温斜率", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        int temperature = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            temperature = new Random().Next(-1, 160);
                            InfoLog($"模拟模式获取微型反应釜升温斜率，结果（{temperature} ℃）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x00, 0x81, 0x00, 0x01 };
                            byte[] frame = BuildReadCmd(0x0081, 1);

                            temperature = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt16Async);

                            InfoLog($"获取微型反应釜升温斜率，结果（{temperature / 100.0} ℃）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("TempSlope", 0.01, 10, temperature/100.0,"反应釜升温斜率",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜的升温斜率失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置升温斜率
            Commands.Add(new DeviceCommand()
            {
                Name = "设置升温斜率",
                HelpText = "设置升温斜率",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", 0.01, 10, 0, "升温斜率目标值", true, "℃")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false,"设置成功",true),
                        new NumberParameter("SetTarget", 0.01, 10, 0, "升温斜率设置值", true, "℃")
                    }
                },

                Action = WrapAction("设置升温斜率", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        float temperature = parameter.GetValue<float>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置微型反应釜升温斜率，结果（成功：{success}，设定值：{temperature} ℃）");
                        }
                        else
                        {
                            // 创建指令
                            //byte[] frame = { slaveId, 0x06, 0x00, 0x81, data[0], data[1] };
                            byte[] frame = BuildWriteCmd(0x0081, (ushort)(temperature * 100));

                            // 写入指令
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜转升温斜率，结果（成功：{success}，设定值：{temperature} ℃）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success,"设置成功",true),
                                new NumberParameter("SetTarget", 0.01, 10, temperature, "升温斜率设置值", true, "℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微型反应釜的升温斜率失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取已运行时间
            Commands.Add(new DeviceCommand()
            {
                Name = "获取已运行时间",
                HelpText = "获取微型反应釜的已运行时间",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("RunTime", 0, 9999,0,"反应釜已运行时间",true,"MIN")
                    }
                },

                Action = WrapAction("获取已运行时间", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        int temperature = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            temperature = new Random().Next(-1, 15) * 100;
                            InfoLog($"模拟模式获取微型反应釜已运行时间，结果（{temperature} MIN）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x01, 0x04, 0x00, 0x01 };
                            byte[] frame = BuildReadCmd(0x0104, 1);

                            temperature = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt16Async);

                            InfoLog($"获取微型反应釜已运行时间，结果（{temperature} MIN）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("RunTime", 0, 9999,temperature,"反应釜已运行时间",true,"MIN")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜的已运行时间失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设定运行时间
            Commands.Add(new DeviceCommand()
            {
                Name = "设定运行时间",
                HelpText = "设置微型反应釜的运行时间",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetTime", 0, 9999,0,"设置运行时间目标值",true,"MIN")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTime", 0, 9999,0,"运行时间设置值",true,"MIN")
                    }
                },

                Action = WrapAction("设定运行时间", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        ushort temperature = parameter.GetValue<ushort>("TargetTime");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置微型反应釜运行时间，结果（成功：{success}，设定值：{temperature} MIN）");
                        }
                        else
                        {
                            // 创建指令
                            //byte[] frame = { slaveId, 0x06, 0x01, 0x04, data[0], data[1] };
                            byte[] frame = BuildWriteCmd(0x0104, temperature);

                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜运行时间，结果（成功：{success}，设定值：{temperature} MIN）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetTime", 0, 9999,temperature,"运行时间设置值",true,"MIN")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微型反应釜的运行时间失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取当前运行段
            Commands.Add(new DeviceCommand()
            {
                Name = "获取当前运行段",
                HelpText = "获取微型反应釜的当前运行段",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("RunningSection", 0, 8,0,"反应釜当前运行段",true)
                    }
                },

                Action = WrapAction("获取当前运行段", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        int temperature = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            temperature = new Random().Next(-1, 8);
                            InfoLog($"模拟模式获取微型反应釜当前运行段，结果（{temperature} ）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x01, 0xF5, 0x00, 0x01 };
                            byte[] frame = BuildReadCmd(0x01F5, 1);

                            temperature = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt16Async);

                            InfoLog($"获取微型反应釜当前运行段，结果（{temperature} ）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("RunningSection", 0, 8,temperature,"反应釜当前运行段",true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜的当前运行段失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取当前段时间数据
            Commands.Add(new DeviceCommand()
            {
                Name = "获取当前段时间数据",
                HelpText = "获取微型反应釜当前段运行时间数据",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("SectionRunTime", 0, 3200,0,"当前段运行时间",true,"MIN"),
                        new NumberParameter("SectionRunTotal", 0, 3200,0,"当前段运行总时间",true,"MIN")
                    }
                },

                Action = WrapAction("获取当前段时间数据", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        int duration = 0;
                        int total = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            InfoLog($"模拟模式获取微型反应釜当前段运行时间数据，结果（Run:{duration}，Total:{total} MIN）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x01, 0xF6, 0x00, 0x02 };
                            byte[] frame = BuildReadCmd(0x01F6, 2);

                            short[] result = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt16ArrayAsync);
                            if (result.Length == 2)
                            {
                                duration = result[0];
                                total = result[1];
                            }

                            InfoLog($"获取微型反应釜当前段运行时间数据，结果（Run:{duration / 10.0}，Total:{total / 10.0} MIN）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("SectionRunTime", 0, 3200,duration/10.0,"当前段运行时间",true,"MIN"),
                                new NumberParameter("SectionRunTotal", 0, 3200,total/10.0,"当前段运行总时间",true,"MIN")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜的当前段时间数据失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取实时设定值
            Commands.Add(new DeviceCommand()
            {
                Name = "获取实时设定值",
                HelpText = "获取微型反应釜实时设定值",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("RealTimeSetValue",-200,600,0,"实时设定值",true,"℃")
                    }
                },

                Action = WrapAction("获取实时设定值", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        int temperature = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            temperature = new Random().Next(-3, 6) * 100; // 模拟 -200 到 600 度
                            InfoLog($"模拟模式获取微型反应釜实时设定值，结果（{temperature} ℃）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x01, 0xF8, 0x00, 0x02 };
                            byte[] frame = BuildReadCmd(0x01F8, 2);
                            temperature = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt32Async);

                            InfoLog($"获取微型反应釜实时设定值，结果（{temperature / 100.0} ℃）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("RealTimeSetValue",-200,600,temperature/100.0,"实时设定值",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜的实时设定值失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取SP1、SP2、SP3、SP4、SP5、SP6、SP7、SP8
            Commands.Add(new DeviceCommand()
            {
                Name = "获取SP",
                HelpText = "获取微型反应釜SP",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("SP1",-200,600,0,"",true,"℃"),
                        new NumberParameter("SP2",-200,600,0,"",true,"℃"),
                        new NumberParameter("SP3",-200,600,0,"",true,"℃"),
                        new NumberParameter("SP4",-200,600,0,"",true,"℃"),
                        new NumberParameter("SP5",-200,600,0,"",true,"℃"),
                        new NumberParameter("SP6",-200,600,0,"",true,"℃"),
                        new NumberParameter("SP7",-200,600,0,"",true,"℃"),
                        new NumberParameter("SP8",-200,600,0,"",true,"℃")
                    }
                },

                Action = WrapAction("获取SP", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        float[] sp = new float[8];

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            //sp = new Random().Next(-3, 6) * 100; // 模拟 -200 到 600 度
                            InfoLog($"模拟模式获取微型反应釜SP，结果（{sp} ℃）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x01, 0xFA, 0x00, 0x02 };
                            byte[] frame = BuildReadCmd(0x01FA, 16);

                            var result = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseSingleArrayAsync);
                            if (result.Length == 8)
                            {
                                sp = result;
                            }

                            InfoLog($"获取微型反应釜SP，结果（{string.Join("，", sp)} ℃）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("SP1",-200,600,sp[0]/100.0,"",true,"℃"),
                                new NumberParameter("SP2",-200,600,sp[1]/100.0,"",true,"℃"),
                                new NumberParameter("SP3",-200,600,sp[2]/100.0,"",true,"℃"),
                                new NumberParameter("SP4",-200,600,sp[3]/100.0,"",true,"℃"),
                                new NumberParameter("SP5",-200,600,sp[4]/100.0,"",true,"℃"),
                                new NumberParameter("SP6",-200,600,sp[5]/100.0,"",true,"℃"),
                                new NumberParameter("SP7",-200,600,sp[6]/100.0,"",true,"℃"),
                                new NumberParameter("SP8",-200,600,sp[7]/100.0,"",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜SP失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置SP1
            Commands.Add(new DeviceCommand()
            {
                Name = "设置SP1",
                HelpText = "设置微反应釜SP1",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", -200, 600, 0, "SP1目标值", true, "℃")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "SP1设置成功", true),
                        new NumberParameter("SetValue", -200, 600, 0, "SP1设置值", true, "℃")
                    }
                },

                Action = WrapAction("设置SP1", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        float target = parameter.GetValue<float>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                        }
                        else
                        {
                            // 创建发送的指令
                            //byte[] frame = { slaveId, 0x10, 0x01, 0xFA, 0x00, 0x02, 0x04, data[2], data[3], data[0], data[1] };
                            byte[] frame = BuildMultWriteCmd(0x01FA, (int)(target * 100));

                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜SP1值，结果（成功：{success}，设定值：{target} ℃）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", false, "SP1设置成功", true),
                                new NumberParameter("SetValue", -200, 600, 0, "SP1设置值", true, "℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微反应釜SP1失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置SP2
            Commands.Add(new DeviceCommand()
            {
                Name = "设置SP2",
                HelpText = "设置微反应釜SP2",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", -200, 600, 0, "SP2目标值", true, "℃")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "SP2设置成功", true),
                        new NumberParameter("SetValue", -200, 600, 0, "SP2设置值", true, "℃")
                    }
                },

                Action = WrapAction("设置SP2", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        float target = parameter.GetValue<float>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                        }
                        else
                        {
                            // 创建发送的指令
                            //byte[] frame = { slaveId, 0x10, 0x01, 0xFC, 0x00, 0x02, 0x04, data[2], data[3], data[0], data[1] };
                            byte[] frame = BuildMultWriteCmd(0x01FC, (int)(target * 100));

                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜SP2值，结果（成功：{success}，设定值：{target} ℃）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", false, "SP2设置成功", true),
                                new NumberParameter("SetValue", -200, 600, 0, "SP2设置值", true, "℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微反应釜SP2失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置SP3
            Commands.Add(new DeviceCommand()
            {
                Name = "设置SP3",
                HelpText = "设置微反应釜SP3",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", -200, 600, 0, "SP3目标值", true, "℃")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "SP3设置成功", true),
                        new NumberParameter("SetValue", -200, 600, 0, "SP3设置值", true, "℃")
                    }
                },

                Action = WrapAction("设置SP3", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        float target = parameter.GetValue<float>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                        }
                        else
                        {
                            // 创建发送的指令
                            //byte[] frame = { slaveId, 0x10, 0x01, 0xFE, 0x00, 0x02, 0x04, data[2], data[3], data[0], data[1] };
                            byte[] frame = BuildMultWriteCmd(0x01FE, (int)(target * 100));

                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜SP3值，结果（成功：{success}，设定值：{target} ℃）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", false, "SP3设置成功", true),
                                new NumberParameter("SetValue", -200, 600, 0, "SP3设置值", true, "℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微反应釜SP3失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置SP4
            Commands.Add(new DeviceCommand()
            {
                Name = "设置SP4",
                HelpText = "设置微反应釜SP4",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", -200, 600, 0, "SP4目标值", true, "℃")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "SP4设置成功", true),
                        new NumberParameter("SetValue", -200, 600, 0, "SP4设置值", true, "℃")
                    }
                },

                Action = WrapAction("设置SP4", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        float target = parameter.GetValue<float>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                        }
                        else
                        {
                            // 创建发送的指令
                            //byte[] frame = { slaveId, 0x10, 0x02, 0x00, 0x00, 0x02, 0x04, data[2], data[3], data[0], data[1] };
                            byte[] frame = BuildMultWriteCmd(0x0200, (int)(target * 100));

                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜SP4值，结果（成功：{success}，设定值：{target} ℃）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", false, "SP4设置成功", true),
                                new NumberParameter("SetValue", -200, 600, 0, "SP4设置值", true, "℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微反应釜SP4失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置SP5
            Commands.Add(new DeviceCommand()
            {
                Name = "设置SP5",
                HelpText = "设置微反应釜SP5",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", -200, 600, 0, "SP5目标值", true, "℃")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "SP5设置成功", true),
                        new NumberParameter("SetValue", -200, 600, 0, "SP5设置值", true, "℃")
                    }
                },

                Action = WrapAction("设置SP5", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        float target = parameter.GetValue<float>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                        }
                        else
                        {
                            // 创建发送的指令
                            //byte[] frame = { slaveId, 0x10, 0x02, 0x02, 0x00, 0x02, 0x04, data[2], data[3], data[0], data[1] };
                            byte[] frame = BuildMultWriteCmd(0x0202, (int)(target * 100));

                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜SP5值，结果（成功：{success}，设定值：{target} ℃）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", false, "SP5设置成功", true),
                                new NumberParameter("SetValue", -200, 600, 0, "SP5设置值", true, "℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微反应釜SP5失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置SP6
            Commands.Add(new DeviceCommand()
            {
                Name = "设置SP6",
                HelpText = "设置微反应釜SP6",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", -200, 600, 0, "SP6目标值", true, "℃")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "SP6设置成功", true),
                        new NumberParameter("SetValue", -200, 600, 0, "SP6设置值", true, "℃")
                    }
                },

                Action = WrapAction("设置SP6", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        float target = parameter.GetValue<float>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                        }
                        else
                        {
                            // 创建发送的指令
                            //byte[] frame = { slaveId, 0x10, 0x02, 0x04, 0x00, 0x02, 0x04, data[2], data[3], data[0], data[1] };
                            byte[] frame = BuildMultWriteCmd(0x0204, (int)(target * 100));

                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜SP6值，结果（成功：{success}，设定值：{target} ℃）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", false, "SP6设置成功", true),
                                new NumberParameter("SetValue", -200, 600, 0, "SP6设置值", true, "℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微反应釜SP6失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置SP7
            Commands.Add(new DeviceCommand()
            {
                Name = "设置SP7",
                HelpText = "设置微反应釜SP7",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", -200, 600, 0, "SP7目标值", true, "℃")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "SP7设置成功", true),
                        new NumberParameter("SetValue", -200, 600, 0, "SP7设置值", true, "℃")
                    }
                },

                Action = WrapAction("设置SP7", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        float target = parameter.GetValue<float>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                        }
                        else
                        {
                            // 创建发送的指令
                            //byte[] frame = { slaveId, 0x10, 0x02, 0x06, 0x00, 0x02, 0x04, data[2], data[3], data[0], data[1] };
                            byte[] frame = BuildMultWriteCmd(0x0206, (int)(target * 100));

                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜SP7值，结果（成功：{success}，设定值：{target} ℃）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", false, "SP7设置成功", true),
                                new NumberParameter("SetValue", -200, 600, 0, "SP7设置值", true, "℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微反应釜SP7失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置SP8
            Commands.Add(new DeviceCommand()
            {
                Name = "设置SP8",
                HelpText = "设置微反应釜SP8",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", -200, 600, 0, "SP8目标值", true, "℃")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "SP8设置成功", true),
                        new NumberParameter("SetValue", -200, 600, 0, "SP8设置值", true, "℃")
                    }
                },

                Action = WrapAction("设置SP8", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        float target = parameter.GetValue<float>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                        }
                        else
                        {
                            // 创建发送的指令
                            //byte[] frame = { slaveId, 0x10, 0x02, 0x08, 0x00, 0x02, 0x04, data[2], data[3], data[0], data[1] };
                            byte[] frame = BuildMultWriteCmd(0x0208, (int)(target * 100));

                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜SP8值，结果（成功：{success}，设定值：{target} ℃）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", false, "SP8设置成功", true),
                                new NumberParameter("SetValue", -200, 600, 0, "SP8设置值", true, "℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微反应釜SP8失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取时间设定值
            Commands.Add(new DeviceCommand()
            {
                Name = "获取时间设定值",
                HelpText = "获取微型反应釜时间设定值",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("ST1", -2, 3200,0,"",true,"MIN"),
                        new NumberParameter("ST2", -2, 3200,0,"",true,"MIN"),
                        new NumberParameter("ST3", -2, 3200,0,"",true,"MIN"),
                        new NumberParameter("ST4", -2, 3200,0,"",true,"MIN"),
                        new NumberParameter("ST5", -2, 3200,0,"",true,"MIN"),
                        new NumberParameter("ST6", -2, 3200,0,"",true,"MIN"),
                        new NumberParameter("ST7", -2, 3200,0,"",true,"MIN"),
                        new NumberParameter("ST8", -2, 3200,0,"",true,"MIN")
                    }
                },

                Action = WrapAction("获取时间设定值", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        int st1 = 0;
                        int st2 = 0;
                        int st3 = 0;
                        int st4 = 0;
                        int st5 = 0;
                        int st6 = 0;
                        int st7 = 0;
                        int st8 = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            //InfoLog($"模拟模式获取微型反应釜当前段运行时间数据，结果（ MIN）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x02, 0x4A, 0x00, 0x08 };
                            byte[] frame = BuildReadCmd(0x024A, 8);
                            var result = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt16ArrayAsync);
                            if (result.Length == 8)
                            {
                                st1 = result[0];
                                st2 = result[1];
                                st3 = result[2];
                                st4 = result[3];
                                st5 = result[4];
                                st6 = result[5];
                                st7 = result[6];
                                st8 = result[7];
                            }
                            InfoLog($"获取微型反应釜时间设定值，结果（{st1},{st2},{st3},{st4},{st5},{st6},{st7},{st8} MIN）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("ST1", -2, 3200, st1 / 10.0,"",true,"MIN"),
                                new NumberParameter("ST2", -2, 3200, st2 / 10.0,"",true,"MIN"),
                                new NumberParameter("ST3", -2, 3200, st3 / 10.0,"",true,"MIN"),
                                new NumberParameter("ST4", -2, 3200, st4 / 10.0,"",true,"MIN"),
                                new NumberParameter("ST5", -2, 3200, st5 / 10.0,"",true,"MIN"),
                                new NumberParameter("ST6", -2, 3200, st6 / 10.0,"",true,"MIN"),
                                new NumberParameter("ST7", -2, 3200, st7 / 10.0,"",true,"MIN"),
                                new NumberParameter("ST8", -2, 3200, st8 / 10.0,"",true,"MIN")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜时间设定值失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置ST1
            Commands.Add(new DeviceCommand()
            {
                Name = "设置ST1",
                HelpText = "设置微型反应釜ST1",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", -2, 3200,0,"ST1目标值",true,"MIN")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetValue", 0, 9999,0,"ST1设置值",true,"MIN")
                    }
                },

                Action = WrapAction("设置ST1", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        float st = parameter.GetValue<float>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置微型反应釜ST1，结果（成功：{success}，设定值：{st} MIN）");
                        }
                        else
                        {
                            byte[] data = BitConverter.GetBytes((short)(st * 10));
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }
                            // 创建指令
                            byte[] frame = { slaveId, 0x06, 0x02, 0x4A, data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜ST1，结果（成功：{success}，设定值：{st} MIN）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetValue", 0, 9999,st,"ST1设置值",true,"MIN")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微型反应釜ST1失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置ST2
            Commands.Add(new DeviceCommand()
            {
                Name = "设置ST2",
                HelpText = "设置微型反应釜ST2",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", -2, 3200,0,"ST2目标值",true,"MIN")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetValue", 0, 9999,0,"ST2设置值",true,"MIN")
                    }
                },

                Action = WrapAction("设置ST2", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        float st = parameter.GetValue<float>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置微型反应釜ST2，结果（成功：{success}，设定值：{st} MIN）");
                        }
                        else
                        {
                            byte[] data = BitConverter.GetBytes((short)(st * 10));
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }
                            // 创建指令
                            byte[] frame = { slaveId, 0x06, 0x02, 0x4B, data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜ST2，结果（成功：{success}，设定值：{st} MIN）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetValue", 0, 9999,st,"ST2设置值",true,"MIN")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微型反应釜ST2失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置ST3
            Commands.Add(new DeviceCommand()
            {
                Name = "设置ST3",
                HelpText = "设置微型反应釜ST3",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", -2, 3200,0,"ST3目标值",true,"MIN")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetValue", 0, 9999,0,"ST3设置值",true,"MIN")
                    }
                },

                Action = WrapAction("设置ST3", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        float st = parameter.GetValue<float>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置微型反应釜ST3，结果（成功：{success}，设定值：{st} MIN）");
                        }
                        else
                        {
                            byte[] data = BitConverter.GetBytes((short)(st * 10));
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }
                            // 创建指令
                            byte[] frame = { slaveId, 0x06, 0x02, 0x4C, data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜ST3，结果（成功：{success}，设定值：{st} MIN）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetValue", 0, 9999,st,"ST3设置值",true,"MIN")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微型反应釜ST3失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置ST4
            Commands.Add(new DeviceCommand()
            {
                Name = "设置ST4",
                HelpText = "设置微型反应釜ST4",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", -2, 3200,0,"ST4目标值",true,"MIN")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetValue", 0, 9999,0,"ST4设置值",true,"MIN")
                    }
                },

                Action = WrapAction("设置ST4", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        float st = parameter.GetValue<float>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置微型反应釜ST4，结果（成功：{success}，设定值：{st} MIN）");
                        }
                        else
                        {
                            byte[] data = BitConverter.GetBytes((short)(st * 10));
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }
                            // 创建指令
                            byte[] frame = { slaveId, 0x06, 0x02, 0x4D, data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜ST4，结果（成功：{success}，设定值：{st} MIN）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetValue", 0, 9999,st,"ST4设置值",true,"MIN")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微型反应釜ST4失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置ST5
            Commands.Add(new DeviceCommand()
            {
                Name = "设置ST5",
                HelpText = "设置微型反应釜ST5",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", -2, 3200,0,"ST5目标值",true,"MIN")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetValue", 0, 9999,0,"ST5设置值",true,"MIN")
                    }
                },

                Action = WrapAction("设置ST5", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        float st = parameter.GetValue<float>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置微型反应釜ST5，结果（成功：{success}，设定值：{st} MIN）");
                        }
                        else
                        {
                            byte[] data = BitConverter.GetBytes((short)(st * 10));
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }
                            // 创建指令
                            byte[] frame = { slaveId, 0x06, 0x02, 0x4E, data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜ST5，结果（成功：{success}，设定值：{st} MIN）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetValue", 0, 9999,st,"ST5设置值",true,"MIN")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微型反应釜ST5失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置ST6
            Commands.Add(new DeviceCommand()
            {
                Name = "设置ST6",
                HelpText = "设置微型反应釜ST6",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", -2, 3200,0,"ST6目标值",true,"MIN")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetValue", 0, 9999,0,"ST6设置值",true,"MIN")
                    }
                },

                Action = WrapAction("设置ST6", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        float st = parameter.GetValue<float>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置微型反应釜ST6，结果（成功：{success}，设定值：{st} MIN）");
                        }
                        else
                        {
                            byte[] data = BitConverter.GetBytes((short)(st * 10));
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }
                            // 创建指令
                            byte[] frame = { slaveId, 0x06, 0x02, 0x4F, data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜ST6，结果（成功：{success}，设定值：{st} MIN）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetValue", 0, 9999,st,"ST6设置值",true,"MIN")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微型反应釜ST6失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置ST7
            Commands.Add(new DeviceCommand()
            {
                Name = "设置ST7",
                HelpText = "设置微型反应釜ST7",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", -2, 3200,0,"ST7目标值",true,"MIN")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetValue", 0, 9999,0,"ST7设置值",true,"MIN")
                    }
                },

                Action = WrapAction("设置ST7", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        float st = parameter.GetValue<float>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置微型反应釜ST7，结果（成功：{success}，设定值：{st} MIN）");
                        }
                        else
                        {
                            byte[] data = BitConverter.GetBytes((short)(st * 10));
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }
                            // 创建指令
                            byte[] frame = { slaveId, 0x06, 0x02, 0x50, data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜ST7，结果（成功：{success}，设定值：{st} MIN）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetValue", 0, 9999,st,"ST7设置值",true,"MIN")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微型反应釜ST7失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置ST8
            Commands.Add(new DeviceCommand()
            {
                Name = "设置ST8",
                HelpText = "设置微型反应釜ST8",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", -2, 3200,0,"ST8目标值",true,"MIN")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetValue", 0, 9999,0,"ST8设置值",true,"MIN")
                    }
                },

                Action = WrapAction("设置ST8", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        float st = parameter.GetValue<float>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置微型反应釜ST8，结果（成功：{success}，设定值：{st} MIN）");
                        }
                        else
                        {
                            // 创建指令
                            //byte[] frame = { slaveId, 0x06, 0x02, 0x51, data[0], data[1] };
                            byte[] frame = BuildWriteCmd(0x0251, (ushort)(st * 10));

                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置微型反应釜ST8，结果（成功：{success}，设定值：{st} MIN）");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetValue", 0, 9999,st,"ST8设置值",true,"MIN")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置微型反应釜ST8失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取可视化界面参数
            Commands.Add(new DeviceCommand()
            {
                Name = "获取可视化界面参数",
                HelpText = "获取微型反应釜可视化界面参数数据",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("BathTemperatureSv",-1000, 10000, 0, "反应釜温度 SV", true, "℃"),
                        new NumberParameter("BathTemperaturePv",-1000, 10000, 0, "反应釜温度 PV", true, "℃"),
                        new NumberParameter("FurnacePv",-1000, 10000, 0, "反应釜炉温 PV", true, "℃"),
                        new NumberParameter("SpeedSv",-1000, 10000, 0, "反应釜搅拌速度 SV", true, "RPM"),
                        new NumberParameter("SpeedPv",-1000, 10000, 0, "反应釜搅拌速度 PV", true, "RPM"),
                        new NumberParameter("Pressure",-1000, 10000, 0, "反应釜压力", true, "Mpa")
                    }
                },

                AsyncAction = AsyncWrapAction("获取可视化界面参数", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        float tempSv = 0;
                        float tempPv = 0;
                        float pressure = 0;
                        float speedSv = 0;
                        float speedPv = 0;
                        float furPv = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            tempPv = new Random().Next(0, 100);
                            tempSv= new Random().Next(0, 100);
                            pressure = new Random().Next(0, 100);
                            speedPv = new Random().Next(0, 100);
                            speedSv = new Random().Next(0, 100);
                            furPv = new Random().Next(0, 100);
                        }
                        else
                        {
                            // 釜温 PV
                            byte[] frame = BuildReadCmd(0, 2);
                            tempPv = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt32Async);
                            // 釜温 SV
                            frame = BuildReadCmd(0x0032, 2);
                            tempSv = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt32Async);
                            // 压力
                            frame = BuildReadCmd(0x0106, 1);
                            pressure = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt16Async);
                            // 搅拌速度 PV
                            frame = BuildReadCmd(0x00FB, 1);
                            speedPv = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt16Async);
                            // 搅拌速度 SV
                            frame = BuildReadCmd(0x00FC, 1);
                            speedSv = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt16Async);
                            // 炉温 PV
                            frame = BuildReadCmd(4, 2);
                            furPv = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt32Async);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("BathTemperatureSv",-1000, 10000, tempSv/100.0, "反应釜温度 SV", true, "℃"),
                                new NumberParameter("BathTemperaturePv",-1000, 10000, tempPv/100.0, "反应釜温度 PV", true, "℃"),
                                new NumberParameter("FurnacePv",-1000, 10000, furPv/100.0, "反应釜炉温 PV", true, "℃"),
                                new NumberParameter("SpeedSv",-1000, 10000, speedSv, "反应釜搅拌速度 SV", true, "RPM"),
                                new NumberParameter("SpeedPv",-1000, 10000, speedPv, "反应釜搅拌速度 PV", true, "RPM"),
                                new NumberParameter("Pressure",-1000, 10000, pressure/100.0, "反应釜压力", true, "Mpa")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"微反应釜可视化界面参数获取失败：{e.Message}");
                        throw;
                    }
                })
            });
        }



        /// <summary>
        /// ZLanGateway 模式下的连接实现。发送 Modbus 读寄存器命令,若收到合法应答则视为连通。
        /// </summary>
        protected override async Task<bool> OnConnectViaGatewayAsync()
        {
            try
            {
                InfoLog("通过 ZLAN 网关测试连接");
                byte[] sendCmd = BuildReadCmd(register: 251, count: 1);
                var pressure = await SendAndReceiveWrapperAsync(sendCmd, CancellationToken.None, SendAndParseInt16Async);
                InfoLog($"网关连接成功,当前搅拌速度 PV {pressure}");
                return true;
            }
            catch (TimeoutException)
            {
                ErrorLog("网关连接超时:链路通但设备无应答");
                return false;
            }
            catch (Exception e)
            {
                ErrorLog("网关连接失败", e);
                return false;
            }
        }


        public Task<bool> OnFlowCompletedAsync(FlowCompletionContext context)
        {
            return Task.FromResult(true);
        }

        public Task<bool> OnFlowFailedAsync(FlowFailureContext context)
        {
            return Task.FromResult(true);
        }

        public Task<bool> OnFlowStartedAsync(FlowStartContext context)
        {
            return Task.FromResult(true);
        }


        #region 命令包装(日志 + 异常封装)

        private Func<DeviceParameters, DeviceParameters> WrapAction(string commandName, Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return (DeviceParameters parameters) =>
            {
                _logger.LogInformation($"[ModbusRTU 微型反应釜-开始] Device={Name} Command={commandName}");

                var sw = Stopwatch.StartNew();
                try
                {
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();

                    _logger.LogInformation($"[ModbusRTU 微型反应釜-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[ModbusRTU 微型反应釜-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        private Func<DeviceParameters, Task<DeviceParameters>> AsyncWrapAction(
            string commandName, Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return async (DeviceParameters parameters) =>
            {
                _logger.LogInformation($"[ModbusRTU 微型反应釜-开始] Device={Name} Command={commandName}");
                var sw = Stopwatch.StartNew();
                try
                {
                    var output = await innerAsync(parameters);
                    sw.Stop();
                    _logger.LogInformation($"[ModbusRTU 微型反应釜-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[ModbusRTU 微型反应釜-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        #endregion


        #region 通信层

        /// <summary>
        /// 取当前配置的 Modbus 从机站号 (1~254)。
        /// </summary>
        private byte GetSlaveAddress()
        {
            try
            {
                var addr = (int)Parameters.GetValue<double>("Modbus站号");
                if (addr < 1 || addr > 254) addr = 1;
                return (byte)addr;
            }
            catch
            {
                return 1;
            }
        }

        /// <summary>
        /// 获取本设备实例专属的互斥锁。
        /// 同一 DeviceId 共享同一把锁,确保 IO 串行化,避免轮询/手动/心跳并发撞车。
        /// </summary>
        private SemaphoreSlim GetInstanceLock()
        {
            var key = Parameters?.GetValue<string>("DeviceId") ?? DeviceId ?? "default";
            return _instanceLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        }

        /// <summary>
        /// 构造 Modbus RTU 写单寄存器命令 (功能码 0x06),自动加 CRC。
        /// </summary>
        private byte[] BuildWriteCmd(ushort register, ushort value)
        {
            byte[] frame = new byte[]
            {
                GetSlaveAddress(),
                0x06,
                (byte)(register >> 8), (byte)(register & 0xFF),
                (byte)(value >> 8),    (byte)(value & 0xFF),
            };
            return Crc16Modbus.AppendCrc(frame, 0, frame.Length);
        }

        /// <summary>
        /// 构造 Modbus RTU 写多个寄存器（功能码 0x10），自动加CRC
        /// </summary>
        /// <param name="register"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        private byte[] BuildMultWriteCmd(ushort register, int value)
        {
            byte slaveId = GetSlaveAddress();

            byte[] bytes = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                ReverseArray(bytes);
            }

            byte[] frame =
            {
                slaveId,
                0x10,
                (byte)(register >> 8),(byte)(register & 0xFF),
                0x00, 0x02,
                0x04,
                bytes[0], bytes[1], bytes[2], bytes[3]
            };

            return Crc16Modbus.AppendCrc(frame, 0, frame.Length);
        }

        /// <summary>
        /// 构造 Modbus RTU 读保持寄存器命令 (功能码 0x03),自动加 CRC。
        /// </summary>
        private byte[] BuildReadCmd(ushort register, ushort count)
        {
            byte[] frame = new byte[]
            {
                GetSlaveAddress(),
                0x03,
                (byte)(register >> 8), (byte)(register & 0xFF),
                (byte)(count >> 8),    (byte)(count & 0xFF),
            };
            return Crc16Modbus.AppendCrc(frame, 0, frame.Length);
        }

        /// <summary>
        /// 写命令。自动重试 3 次以应对工业链路偶发抖动。
        /// </summary>
        private async Task<bool> WriteAsync(byte[] buffers, int offset, int count, CancellationToken token)
        {
            Exception lastException = null;

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    return await WriteOnceAsync(buffers, offset, count, token).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    lastException = ex;
                    if (attempt < MaxRetries)
                    {
                        _logger?.LogWarning("Modbus 写入超时 (第 {Attempt}/{Total} 次),{Delay}ms 后重试",
                            attempt, MaxRetries, RetryDelayMs);
                        await Task.Delay(RetryDelayMs, token).ConfigureAwait(false);
                    }
                }
            }

            throw new TimeoutException(
                $"Modbus 写入失败,{MaxRetries} 次尝试后仍超时", lastException);
        }

        /// <summary>
        /// 写命令的单次执行实现。走实例锁排队,通过 IDeviceTransport 发出。
        /// </summary>
        private async Task<bool> WriteOnceAsync(byte[] buffers, int offset, int count, CancellationToken token)
        {
            var sem = GetInstanceLock();
            await sem.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using var transport = await GetTransportAsync().ConfigureAwait(false);
                return await transport.SendAsync(buffers, offset, count, token).ConfigureAwait(false);
            }
            finally
            {
                sem.Release();
            }
        }

        /// <summary>
        /// 读命令的单次执行实现。走实例锁排队,加站号/功能码/长度校验。
        /// </summary>
        private async Task<short> SendAndParseInt16Async(byte[] sendCmd, CancellationToken token)
        {
            var sem = GetInstanceLock();
            await sem.WaitAsync(token).ConfigureAwait(false);
            try
            {
                byte[] recv = new byte[256];
                using var transport = await GetTransportAsync().ConfigureAwait(false);
                int len = await transport.SendAndReceiveAsync(sendCmd, recv, 3000, token).ConfigureAwait(false);

                // 应答最短:站号(1)+功能码(1)+长度(1)+数据(2)+CRC(2) = 7 字节
                if (len < 7)
                {
                    _logger?.LogWarning("应答帧长度不足:{Len} 字节,需要至少 7 字节", len);
                    return 0;
                }

                // 校验站号
                byte expectedAddr = GetSlaveAddress();
                if (recv[0] != expectedAddr)
                {
                    _logger?.LogWarning("站号不匹配:期望 0x{Expected:X2},实际 0x{Actual:X2}",
                        expectedAddr, recv[0]);
                    return 0;
                }

                // 校验功能码(异常码 0x83 单独识别)
                if ((recv[1] & 0x80) != 0)
                {
                    _logger?.LogWarning("Modbus 异常应答,异常码 0x{Code:X2}", recv[2]);
                    return 0;
                }
                if (recv[1] != 0x03)
                {
                    _logger?.LogWarning("功能码不匹配:期望 0x03,实际 0x{Actual:X2}", recv[1]);
                    return 0;
                }

                // 提取数据载荷
                byte[] data = ExtractPayload(recv);
                if (BitConverter.IsLittleEndian)
                {
                    ReverseArray(data);
                }
                return BitConverter.ToInt16(data, 0);
            }
            finally
            {
                sem.Release();
            }
        }

        private async Task<short[]> SendAndParseInt16ArrayAsync(byte[] sendCmd, CancellationToken token)
        {
            var sem = GetInstanceLock();
            await sem.WaitAsync(token).ConfigureAwait(false);
            try
            {
                byte[] recv = new byte[256];
                using var transport = await GetTransportAsync().ConfigureAwait(false);
                int len = await transport.SendAndReceiveAsync(sendCmd, recv, 3000, token).ConfigureAwait(false);

                // 应答最短:站号(1)+功能码(1)+长度(1)+数据(2)+CRC(2) = 7 字节
                if (len < 7)
                {
                    _logger?.LogWarning("应答帧长度不足:{Len} 字节,需要至少 7 字节", len);
                    return Array.Empty<short>();
                }

                // 校验站号
                byte expectedAddr = GetSlaveAddress();
                if (recv[0] != expectedAddr)
                {
                    _logger?.LogWarning("站号不匹配:期望 0x{Expected:X2},实际 0x{Actual:X2}",
                        expectedAddr, recv[0]);
                    return Array.Empty<short>();
                }

                // 校验功能码(异常码 0x83 单独识别)
                if ((recv[1] & 0x80) != 0)
                {
                    _logger?.LogWarning("Modbus 异常应答,异常码 0x{Code:X2}", recv[2]);
                    return Array.Empty<short>();
                }
                if (recv[1] != 0x03)
                {
                    _logger?.LogWarning("功能码不匹配:期望 0x03,实际 0x{Actual:X2}", recv[1]);
                    return Array.Empty<short>();
                }

                // 提取数据载荷
                byte[] data = ExtractPayload(recv);
                short[] result = new short[data.Length / 2];

                for (int i = 0; i < result.Length; i++)
                {
                    // 获取 int16 数据的实际字节（AB）
                    byte[] bytes = { data[i * 2], data[i * 2 + 1] };
                    if (BitConverter.IsLittleEndian)
                    {
                        ReverseArray(bytes);
                    }
                    result[i] = BitConverter.ToInt16(bytes, 0);
                }

                return result;
            }
            finally
            {
                sem.Release();
            }
        }

        /// <summary>
        /// 读取 Modbus 寄存器并解析 float 数据
        /// </summary>
        /// <param name="sendCmd"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        private async Task<int> SendAndParseInt32Async(byte[] sendCmd, CancellationToken token)
        {
            var slim = GetInstanceLock();
            await slim.WaitAsync(token).ConfigureAwait(false);

            try
            {
                byte[] recv = new byte[256];
                using var transport = await GetTransportAsync().ConfigureAwait(false);
                int len = await transport.SendAndReceiveAsync(sendCmd, recv, 3000, token).ConfigureAwait(false);

                // 应答最短:站号(1)+功能码(1)+长度(1)+数据(4)+CRC(2) = 9 字节
                if (len < 9)
                {
                    _logger?.LogWarning("应答帧长度不足:{Len} 字节,需要至少 9 字节", len);
                    return 0;
                }

                // 校验站号
                byte expectedAddr = GetSlaveAddress();
                if (recv[0] != expectedAddr)
                {
                    _logger?.LogWarning("站号不匹配:期望 0x{Expected:X2},实际 0x{Actual:X2}", expectedAddr, recv[0]);
                    return 0;
                }

                // 校验功能码(异常码 0x83 单独识别)
                if ((recv[1] & 0x80) != 0)
                {
                    _logger?.LogWarning("Modbus 异常应答,异常码 0x{Code:X2}", recv[2]);
                    return 0;
                }
                if (recv[1] != 0x03)
                {
                    _logger?.LogWarning("功能码不匹配:期望 0x03,实际 0x{Actual:X2}", recv[1]);
                    return 0;
                }

                // 提取数据载荷 (ABCD)
                byte[] data = ExtractPayload(recv);
                if (BitConverter.IsLittleEndian)
                {
                    ReverseArray(data);
                }
                return BitConverter.ToInt32(data, 0);
            }
            finally
            {
                slim.Release();
            }
        }

        /// <summary>
        /// 解析 Modbus 单精度浮点数组
        /// </summary>
        /// <param name="sendCmd"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        private async Task<float[]> SendAndParseSingleArrayAsync(byte[] sendCmd, CancellationToken token)
        {
            var slim = GetInstanceLock();
            await slim.WaitAsync(token).ConfigureAwait(false);

            try
            {
                byte[] recv = new byte[256];
                using var transport = await GetTransportAsync().ConfigureAwait(false);
                int len = await transport.SendAndReceiveAsync(sendCmd, recv, 3000, token).ConfigureAwait(false);

                // 应答最短:站号(1)+功能码(1)+异常码(1)+CRC(2) = 5 字节
                if (len < 5)
                {
                    _logger?.LogWarning("应答帧长度无效,需要至少 5 字节，实际 {len} 字节", len);
                    return Array.Empty<float>();
                }

                // 校验站号
                byte expectedAddr = GetSlaveAddress();
                if (recv[0] != expectedAddr)
                {
                    _logger?.LogWarning("站号不匹配:期望 0x{Expected:X2},实际 0x{Actual:X2}", expectedAddr, recv[0]);
                    return Array.Empty<float>();
                }

                if (recv[1] != 0x03)
                {
                    _logger?.LogWarning("功能码不匹配:期望 0x03,实际 0x{Actual:X2}", recv[1]);
                    return Array.Empty<float>();
                }

                // 提取数据载荷
                byte[] data = ExtractPayload(recv);

                return ByteArrayToSingleArray(data);
            }
            finally
            {
                slim.Release();
            }
        }

        /// <summary>
        /// 通过基类的 CreateTransportAsync 获取 Transport。
        /// </summary>
        private Task<IDeviceTransport> GetTransportAsync()
        {
            var serialConfig = new SerialPortConfig
            {
                PortName = Parameters.GetValue<string>("串口号") ?? "COM1",
                BaudRate = int.Parse(Parameters.GetValue<string>("波特率") ?? "9600"),
                Parity = Parameters.GetValue<string>("校验位") ?? "None",
                DataBits = int.Parse(Parameters.GetValue<string>("数据位") ?? "8"),
                StopBits = Parameters.GetValue<string>("停止位") ?? "One",
                ReadTimeoutMs = 3000,
                WriteTimeoutMs = 3000,
            };
            return CreateTransportAsync(serialConfig);
        }

        /// <summary>
        /// 将字节数组（字节序：ABCD）转换为浮点数组
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        /// <exception cref="ArgumentException"></exception>
        private float[] ByteArrayToSingleArray(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException("输入字节数为空");
            }

            if (data.Length == 0 || data.Length % 4 != 0)
            {
                throw new ArgumentException($"输入字节数组长度无效，应为4的倍数。数组长度：{data.Length}");
            }

            float[] result = new float[data.Length / 4];
            byte[] temp = new byte[4];
            for (int i = 0; i < result.Length; i++)
            {
                temp[3] = data[4 * i + 3];
                temp[2] = data[4 * i + 2];
                temp[1] = data[4 * i + 1];
                temp[0] = data[4 * i];
                if (BitConverter.IsLittleEndian)
                {
                    ReverseArray(temp);
                }
                result[i] = BitConverter.ToInt32(temp, 0);
            }
            return result;
        }


        /// <summary>
        /// 发送 Modbus 读取寄存器命令的包装方法，自动重试三次
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="sendCmd">指令报文</param>
        /// <param name="token">取消标记</param>
        /// <param name="function">执行的委托</param>
        /// <returns></returns>
        /// <exception cref="TimeoutException"></exception>
        private async Task<T> SendAndReceiveWrapperAsync<T>(byte[] sendCmd, CancellationToken token, Func<byte[], CancellationToken, Task<T>> function)
        {
            Exception lastException = null;

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    return await function(sendCmd, token).ConfigureAwait(false);
                }
                catch (TimeoutException ex)
                {
                    lastException = ex;
                    if (attempt < MaxRetries)
                    {
                        _logger?.LogWarning("Modbus 读取超时 (第 {Attempt}/{Total} 次),{Delay}ms 后重试",
                            attempt, MaxRetries, RetryDelayMs);
                        await Task.Delay(RetryDelayMs, token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    // 非超时异常(协议错、CRC 错、字节流非法)不重试,直接抛
                    _logger?.LogError(ex, "Modbus 读取失败(非超时,不重试)");
                    throw;
                }
            }

            throw new TimeoutException(
                $"Modbus 读取失败,{MaxRetries} 次尝试后仍超时", lastException);
        }

        #endregion

        private byte[] ExtractPayload(byte[] frame)
        {
            if (frame == null || frame.Length < 5)
            {
                throw new ArgumentException("报文长度无效");
            }

            if (frame[1] >= 0x80)
            {
                throw new ArgumentException($"Modbus 异常响应，异常码：{frame[2]:X2}");
            }

            byte dataLen = frame[2];
            int dataStartIndex = 3;
            int crcIndex = dataStartIndex + dataLen;

            if (frame.Length < crcIndex + 2)
                throw new ArgumentException("Modbus RTU 报文长度不匹配");

            byte[] data = new byte[dataLen];
            Array.Copy(frame, dataStartIndex, data, 0, dataLen);
            return data;
        }

        /// <summary>
        /// 反转字节数组
        /// </summary>
        /// <param name="buffers">输入数组</param>
        private void ReverseArray(byte[] buffers)
        {
            if (buffers == null || buffers.Length == 0)
            {
                throw new ArgumentException("输入数据为空", nameof(buffers));
            }

            byte temp = 0;
            int middle = buffers.Length / 2;
            int length = buffers.Length - 1;

            for (int i = 0; i < middle; i++)
            {
                temp = buffers[i];
                buffers[i] = buffers[length - i];
                buffers[length - i] = temp;
            }
        }
    }

    public static class Crc16Modbus
    {
        /// <summary>
        /// 计算 CRC16-Modbus 校验码
        /// </summary>
        public static ushort Compute(byte[] data, int offset, int length)
        {
            ushort crc = 0xFFFF;
            for (int i = offset; i < offset + length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    bool lsb = (crc & 1) != 0;
                    crc >>= 1;
                    if (lsb) crc ^= 0xA001;
                }
            }
            return crc;
        }

        /// <summary>
        /// 给数据包追加 CRC 两个字节（用于发送）
        /// </summary>
        public static byte[] AppendCrc(byte[] data, int offset, int length)
        {
            ushort crc = Compute(data, offset, length);
            byte[] crcBytes = BitConverter.GetBytes(crc);

            byte[] result = new byte[length + 2];
            Buffer.BlockCopy(data, offset, result, 0, length);
            result[length] = crcBytes[0];
            result[length + 1] = crcBytes[1];

            return result;
        }

        /// <summary>
        /// 验证数据包 CRC 是否正确
        /// </summary>
        public static bool Verify(byte[] data)
        {
            if (data.Length < 2) return false;
            int dataLen = data.Length - 2;
            ushort crc = Compute(data, 0, dataLen);
            byte[] crcBytes = BitConverter.GetBytes(crc);
            return data[dataLen] == crcBytes[0] && data[dataLen + 1] == crcBytes[1];
        }
    }
}
