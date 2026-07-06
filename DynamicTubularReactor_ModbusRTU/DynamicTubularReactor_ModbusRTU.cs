using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO.Ports;

namespace DynamicTubularReactor_ModbusRTU
{
    [Export(typeof(IDevice))]
    public class DynamicTubularReactor_ModbusRTU : Device, IFlowLifecycleAware, IDeviceScreen
    {
        private readonly ILogService _logger;

        private Task _dataTask;
        private CancellationTokenSource _dataCts;

        // 实例级 TCP 互斥锁:同一设备实例的所有 IO 串行化,避免与轮询/心跳撞车
        // 用静态字典按 DeviceId 隔离不同设备实例
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _instanceLocks
            = new ConcurrentDictionary<string, SemaphoreSlim>();

        // 通信层重试参数
        private const int MaxRetries = 3;       // 总共尝试 3 次
        private const int RetryDelayMs = 100;   // 重试间隔 100ms

        public string ScreenKey => "DynamicTubularReactor";
        //4A 9B连接线
        public DynamicTubularReactor_ModbusRTU()
        {
            _logger = LogManager.GetLogger<DynamicTubularReactor_ModbusRTU>();

            DeviceId = "DTR0001";
            Name = "100ml动态管式反应釜";
            Manufacturer = "ModbusRTU 100ml动态管式反应釜";
            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/DynamicTubularReactor_CY.png";
            Category = DeviceCategories.Reactors;
            ComId = "DTR0001"; // 默认设备ID
            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();
            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            Parameters.Variables.Add(new StringParameter("DeviceId", "DTR0001", "ModbusRTU设备ID")
            {
                Options = new ObservableCollection<string>() { "DTR0001", "DTR0002", "DTR0003", "DTR0004", "DTR0005" },
            });

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
                Options = new ObservableCollection<string>() { "Direct", "PLC", "ModbusTcp", "ZLanGateway", "RemoteServer" }
            });

            // Modbus 从机站号 (1~254)
            Parameters.Variables.Add(new NumberParameter("Modbus站号", 1, 254, 1, "Modbus 从机站号 (1~254)"));

            // 自建云服务器参数（仅"通信方式=RemoteServer"时生效）
            Parameters.Variables.Add(new StringParameter("DTU序列号", "HT8HTPA666ES", "DTU 登录包(序列号)，需与云服务器侧该设备的 DTU 一致"));
            // 32位浮点字节序 (Modbus 协议不规定，由设备决定，需现场确认)
            Parameters.Variables.Add(new StringParameter("浮点字节序", "ABCD", "32位浮点字节序：ABCD=大端, CDAB=字交换, BADC=字节交换, DCBA=小端")
            {
                Options = new ObservableCollection<string>() { "ABCD", "CDAB", "BADC", "DCBA" }
            });

            InitializeCommands();
        }


        /// <summary>
        /// 声明本驱动支持通过 ZLAN 网关通信。
        /// </summary>
        public override bool SupportsZLanGateway => true;


        private void InitializeCommands()
        {
            // SV
            Commands.Add(new DeviceCommand()
            {
                Name = "获取搅拌 SV",
                HelpText = "获取100ml动态管反的搅拌 SV",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("SV", 0 , 1000, 0, "100ml动态管反的搅拌 SV", true, "RPM")
                    }
                },

                AsyncAction = AsyncWrapAction("获取搅拌 SV", async parameter =>
                {
                    float value = 0;
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            value = new Random().Next(19, 100);
                        }
                        else
                        {
                            // 发送的指令报文
                            //byte[] frame = new byte[6];
                            //frame[0] = slaveId; // 从站地址
                            //frame[1] = 3;       // 功能码
                            //frame[2] = 0;       // 起始地址
                            //frame[3] = 0;
                            //frame[4] = 0;       // 读取数量
                            //frame[5] = 2;
                            byte[] frame = BuildReadCmd(0, 2);
                            value = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseSingleAsync);
                        }
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取100ml动态官反搅拌 SV 失败：{e.Message}");
                        throw;
                    }

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new NumberParameter("SV", 0 , 1000, value, "100ml动态管反的搅拌 SV", true, "RPM")
                        }
                    };
                })
            });

            // PV
            Commands.Add(new DeviceCommand()
            {
                Name = "获取搅拌 PV",
                //ImageLocation = "",
                HelpText = "获取动态管反的搅拌 PV",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("PV", 0, 100, 0, "搅拌 PV", true, "RPM")
                    }
                },

                AsyncAction = AsyncWrapAction("获取搅拌 PV", async parameter =>
                {
                    float speed = 0;
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            speed = new Random().Next(19, 100);
                        }
                        else
                        {
                            // 发送的指令报文
                            //byte[] frame = new byte[6] { slaveId, 3, 0, 2, 0, 2 };
                            byte[] frame = BuildReadCmd(2, 2);
                            speed = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseSingleAsync);
                        }
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取100ml动态管式反应釜搅拌 PV 失败：{e.Message}");
                        throw;
                    }

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new NumberParameter("PV", 0 , 1000, speed, "搅拌 PV", true, "RPM")
                        }
                    };
                })
            });

            // 获取釜温
            Commands.Add(new DeviceCommand()
            {
                Name = "获取反应釜温度",
                HelpText = "获取100ml动态管反的温度",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("Temperature", 0, 1000, 0, "反应釜温度", true, "℃")
                    }
                },

                AsyncAction = AsyncWrapAction("获取反应釜温度", async parameter =>
                {
                    float temperature = 0;
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            temperature = new Random().Next(19, 100);
                        }
                        else
                        {
                            // 发送的指令报文
                            //byte[] frame = new byte[6] { slaveId, 3, 0, 4, 0, 2 };
                            byte[] frame = BuildReadCmd(4, 2);
                            temperature = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseSingleAsync);
                            
                        }
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取100ml动态管式反应釜温度失败:{e.Message}");
                        throw;
                    }

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new NumberParameter("Temperature", 0, 1000, temperature, "反应釜温度", true, "℃")
                        }
                    };
                })
            });

            // 获取反应釜压力
            Commands.Add(new DeviceCommand()
            {
                Name = "获取反应釜压力",
                HelpText = "获取100ml动态管反的压力",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("Pressure", 0, 1000, 0, "反应釜压力", true, "Mpa")
                    }
                },

                AsyncAction = AsyncWrapAction("获取反应釜压力", async parameter =>
                {
                    float pressure = 0;
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            pressure = new Random().Next(19, 100);
                        }
                        else
                        {
                            // 发送的指令报文
                            //byte[] frame = new byte[6] { slaveId, 3, 0, 6, 0, 2 };
                            byte[] frame = BuildReadCmd(6, 2);
                            pressure = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseSingleAsync);
                        }
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取100ml动态管式反应釜压力失败:{e.Message}");
                        throw;
                    }

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new NumberParameter("Pressure", 0, 1000, pressure, "反应釜压力", true, "Mpa")
                        }
                    };
                })
            });

            // 获取搅拌 ON/OFF
            Commands.Add(new DeviceCommand()
            {
                Name = "获取搅拌 ON/OFF",
                HelpText = "获取100ml动态管反的搅拌 ON/OFF",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("ON/OFF", 0, 1, 0, "100ml动态管反 搅拌 ON/OFF(1：停止，0：运行)", true)
                    }
                },

                AsyncAction = AsyncWrapAction("获取搅拌 ON/OFF", async parameter =>
                {
                    int pressure = 0;
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            pressure = new Random().Next(19, 100);
                        }
                        else
                        {
                            // 发送的指令报文
                            //byte[] frame = new byte[6] { slaveId, 3, 0, 8, 0, 1 };
                            byte[] frame = BuildReadCmd(8, 1);
                            pressure = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt16Async);
                        }
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取100ml动态管反搅拌 ON/OFF 失败：{e.Message}");
                        throw;
                    }

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new NumberParameter("ON/OFF", 0, 1, pressure, "100ml动态管反 搅拌 ON/OFF(1：停止，0：运行)", true)
                        }
                    };
                })
            });

            // 获取 报警
            Commands.Add(new DeviceCommand()
            {
                Name = "获取报警数据",
                HelpText = "获取100ml动态管反的报警数据（釜温上限报警、压力上限报警、釜温通信故障、压力通信故障）",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Alarm1", false, "釜温上限报警", true),
                        new BooleanParameter("Alarm2", false, "压力上限报警", true),
                        new BooleanParameter("Alarm3", false, "釜温通信故障", true),
                        new BooleanParameter("Alarm4", false, "压力通讯故障", true)
                    }
                },

                AsyncAction = AsyncWrapAction("获取报警数据", async parameter =>
                {
                    int alarm1 = 0;
                    int alarm2 = 0;
                    int alarm3 = 0;
                    int alarm4 = 0;
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                        }
                        else
                        {
                            // 发送的指令报文
                            //byte[] frame = { slaveId, 3, 0, 9, 0, 4 };
                            byte[] frame = BuildReadCmd(9, 4);
                            ushort[] arr = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseUInt16ArrayAsync);
                            if (arr.Length == 4)
                            {
                                alarm1 = arr[0];
                                alarm2 = arr[1];
                                alarm3 = arr[2];
                                alarm4 = arr[3];
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取100ml动态管式反应釜报警数据失败：{e.Message}");
                        throw;
                    }

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new BooleanParameter("Alarm1", alarm1 == 0 ? false : true, "釜温上限报警", true),
                            new BooleanParameter("Alarm2", alarm2 == 0 ? false : true, "压力上限报警", true),
                            new BooleanParameter("Alarm3", alarm3 == 0 ? false : true, "釜温通信故障", true),
                            new BooleanParameter("Alarm4", alarm4 == 0 ? false : true, "压力通讯故障", true)
                        }
                    };
                })
            });

            /*// 获取 报警消音
            Commands.Add(new DeviceCommand()
            {
                Name = "获取报警消音状态",
                HelpText = "获取100ml动态管反报警消音",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("AlarmSilence", false, "100ml动态管反消音状态", true)
                    }
                },

                AsyncAction = AsyncWrapAction("获取报警消音状态", async parameter =>
                {
                    int pressure = 0;
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            pressure = new Random().Next(-1, 1);
                        }
                        else
                        {
                            // 发送的指令报文
                            //byte[] frame = new byte[6] { slaveId, 3, 0, 14, 0, 1 };
                            byte[] frame = BuildReadCmd(14, 1);
                            pressure = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt16Async);
                        }
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取100ml动态管反消音状态失败：{e.Message}");
                        throw;
                    }

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new BooleanParameter("AlarmSilence", pressure == 0 ? false : true, "100ml动态管反消音状态", true)
                        }
                    };
                })
            });*/

            // 获取 报警状态
            Commands.Add(new DeviceCommand()
            {
                Name = "获取报警状态",
                HelpText = "获取100ml动态管反的报警状态",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("AlarmStatus", false, "获取100ml动态管反报警状态(0:无报警，1:有报警)", true)
                    }
                },

                AsyncAction = AsyncWrapAction("获取报警状态", async parameter =>
                {
                    int pressure = 0;
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            pressure = new Random().Next(-1, 1);
                        }
                        else
                        {
                            // 发送的指令报文
                            //byte[] frame = new byte[6] { slaveId, 3, 0, 15, 0, 1 };
                            byte[] frame = BuildReadCmd(15, 1);
                            pressure = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseInt16Async);
                        }
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取100ml动态管反报警状态失败：{e.Message}");
                        throw;
                    }

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new BooleanParameter("AlarmStatus", pressure == 0 ? false : true, "获取100ml动态管反报警状态(0:无报警，1:有报警)", true)
                        }
                    };
                })
            });


            // 设置 SV
            Commands.Add(new DeviceCommand()
            {
                Name = "设置 SV",
                HelpText = "设置100ml动态管反的搅拌 SV",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue",0,1000,0,"SV 设置值",true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success",false,"设置成功",true),
                        new NumberParameter("Set SV Value",0,1000,0,"SV 设置值",true)
                    }
                },

                AsyncAction = AsyncWrapAction("设置 SV", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        float targetValue = parameter.GetValue<float>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                        }
                        else
                        {
                            byte[] data = BitConverter.GetBytes(targetValue);
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }
                            // 发送的指令报文
                            //byte[] frame = { slaveId, 16, 0, 0, 0, 2, 4, data[0], data[1], data[2], data[3] };

                            byte[] frame = BuildMultWriteCmd(0, targetValue);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success",success,"设置成功",true),
                                new NumberParameter("Set SV Value",0,1000,targetValue,"SV 设置值",true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"100ml动态管反的搅拌 SV 设置失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置 PV
            Commands.Add(new DeviceCommand()
            {
                Name = "设置 PV",
                HelpText = "设置100ml动态管反的搅拌 PV",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue",0,1000,0,"PV 设置值",true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success",false,"设置成功",true),
                        new NumberParameter("Set PV Value",0,1000,0,"PV 设置值",true)
                    }
                },

                AsyncAction = AsyncWrapAction("设置 PV", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        float targetValue = parameter.GetValue<float>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                        }
                        else
                        {
                            // 发送的指令报文
                            //byte[] frame = { slaveId, 16, 0, 2, 0, 2, 4, data[0], data[1], data[2], data[3] };
                            byte[] frame = BuildMultWriteCmd(2, targetValue);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success",success,"设置成功",true),
                                new NumberParameter("Set PV Value",0,1000,targetValue,"PV 设置值",true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"100ml动态管反的搅拌 PV 设置失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置釜温
            Commands.Add(new DeviceCommand()
            {
                Name = "设置釜温",
                HelpText = "设置100ml动态管反的温度",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue",0,1000,0,"目标温度值",true, "℃")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success",false,"设置成功",true),
                        new NumberParameter("SetTemperature",0,1000,0,"釜温设置值",true, "℃")
                    }
                },

                AsyncAction = AsyncWrapAction("设置釜温", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        float targetValue = parameter.GetValue<float>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                        }
                        else
                        {
                            // 发送的指令报文
                            //byte[] frame = { slaveId, 16, 0, 4, 0, 2, 4, data[0], data[1], data[2], data[3] };
                            byte[] frame = BuildMultWriteCmd(4, targetValue);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success",success,"设置成功",true),
                                new NumberParameter("SetTemperature",0,1000,targetValue,"釜温设置值",true, "℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"100ml动态管反的釜温设置失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置反应釜压力
            Commands.Add(new DeviceCommand()
            {
                Name = "设置压力",
                HelpText = "设置100ml动态管反的压力",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue",0,1000,0,"目标压力值",true, "Mpa")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success",false,"设置成功",true),
                        new NumberParameter("SetPressure",0,1000,0,"压力设置值",true, "Mpa")
                    }
                },

                AsyncAction = AsyncWrapAction("设置压力", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        float targetValue = parameter.GetValue<float>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                        }
                        else
                        {
                            // 发送的指令报文
                            //byte[] frame = { slaveId, 16, 0, 6, 0, 2, 4, data[0], data[1], data[2], data[3] };
                            byte[] frame = BuildMultWriteCmd(6, targetValue);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success",success,"设置成功",true),
                                new NumberParameter("SetPressure",0,1000,targetValue,"压力设置值",true, "Mpa")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"100ml动态管反压力设置失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置 ON/OFF
            Commands.Add(new DeviceCommand()
            {
                Name = "设置 ON/OFF",
                HelpText = "设置100ml动态管反 ON/OFF",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue",0,1,0,"目标值",true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success",false,"设置成功",true),
                        new NumberParameter("Set ON/OFF",0,1,0,"ON/OFF 设置值",true)
                    }
                },

                AsyncAction = AsyncWrapAction("设置 ON/OFF", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte targetValue = parameter.GetValue<byte>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                        }
                        else
                        {
                            // 发送的指令报文
                            //byte[] frame = { slaveId, 6, 0, 8, 0, targetValue };
                            byte[] frame = BuildWriteCmd(8, targetValue);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success",success,"设置成功",true),
                                new NumberParameter("Set ON/OFF",0,1,targetValue,"ON/OFF 设置值",true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"100ml动态管反 ON/OFF 设置失败：{e.Message}");
                        throw;
                    }
                })
            });

            /*// 设置 釜温上限报警
            Commands.Add(new DeviceCommand()
            {
                Name = "设置釜温上限报警",
                HelpText = "设置100ml动态管反的釜温上限报警",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue",0,1,0,"目标值",true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success",false,"设置成功",true),
                        new NumberParameter("Set alarm1",0,1,0,"釜温上限报警设置值",true)
                    }
                },

                AsyncAction = AsyncWrapAction("设置釜温上限报警", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        ushort targetValue = parameter.GetValue<ushort>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                        }
                        else
                        {
                            // 发送的指令报文
                            //byte[] frame = { slaveId, 6, 0, 9, 0, value };
                            byte[] frame = BuildWriteCmd(9, targetValue);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success",success,"设置成功",true),
                                new NumberParameter("Set alarm1",0,1,targetValue,"釜温上限报警设置值",true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"100ml动态管反釜温上限报警设置失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置 压力上限报警
            Commands.Add(new DeviceCommand()
            {
                Name = "设置压力上限报警",
                HelpText = "设置100ml动态管反的压力上限报警",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue",0,1,0,"目标值",true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success",false,"设置成功",true),
                        new NumberParameter("Set alarm2",0,1,0,"压力上限报警设置值",true)
                    }
                },

                AsyncAction = AsyncWrapAction("设置压力上限报警", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        ushort targetValue = parameter.GetValue<ushort>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                        }
                        else
                        {
                            // 发送的指令报文
                            //byte[] frame = { slaveId, 6, 0, 10, 0, value };
                            byte[] frame = BuildWriteCmd(10, targetValue);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success",success,"设置成功",true),
                                new NumberParameter("Set alarm2",0,1,targetValue,"压力上限报警设置值",true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"100ml动态管反压力上限报警设置失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置 釜温通信故障
            Commands.Add(new DeviceCommand()
            {
                Name = "设置釜温通信故障",
                HelpText = "设置100ml动态管反釜温通信故障",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue",0,1,0,"目标值",true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success",false,"设置成功",true),
                        new NumberParameter("Set alarm3",0,1,0,"釜温通信故障设置值",true)
                    }
                },

                AsyncAction = AsyncWrapAction("设置釜温通信故障", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        ushort targetValue = parameter.GetValue<ushort>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                        }
                        else
                        {
                            // 发送的指令报文
                            //byte[] frame = { slaveId, 6, 0, 11, 0, value };
                            byte[] frame = BuildWriteCmd(11, targetValue);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success",success,"设置成功",true),
                                new NumberParameter("Set alarm3",0,1,targetValue,"釜温通信故障设置值",true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"100ml动态管反釜温通信故障设置失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置 压力通讯故障
            Commands.Add(new DeviceCommand()
            {
                Name = "设置压力通讯故障",
                HelpText = "设置100ml动态管反压力通信故障",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue",0,1,0,"目标值",true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success",false,"设置成功",true),
                        new NumberParameter("Set alarm4",0,1,0,"压力通讯故障设置值",true)
                    }
                },

                AsyncAction = AsyncWrapAction("设置压力通讯故障", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        ushort targetValue = parameter.GetValue<ushort>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                        }
                        else
                        {
                            // 发送的指令报文
                            //byte[] frame = { slaveId, 6, 0, 12, 0, value };
                            byte[] frame = BuildWriteCmd(12, targetValue);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success",success,"设置成功",true),
                                new NumberParameter("Set alarm4",0,1,targetValue,"压力通讯故障设置值",true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"100ml动态管反压力通讯故障设置失败：{e.Message}");
                        throw;
                    }
                })
            });*/

            // 设置 报警复位
            Commands.Add(new DeviceCommand()
            {
                Name = "报警复位",
                HelpText = "复位100ml动态管反报警",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "复位100ml动态管反报警成功", true)
                    }
                },

                AsyncAction = AsyncWrapAction("报警复位", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);

                        }
                        else
                        {
                            // 发送的指令报文
                            //byte[] frame = { slaveId, 6, 0, 13, 0, 1 };
                            byte[] frame = BuildWriteCmd(13, 1);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "复位100ml动态管反报警成功", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"复位100ml动态管反报警失败:{e.Message}");
                        throw;
                    }
                })
            });

            // 设置 报警消音
            Commands.Add(new DeviceCommand()
            {
                Name = "报警消音",
                HelpText = "100ml动态管反报警消音",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue",0,1,0,"目标值",true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success",false,"设置成功",true),
                        new NumberParameter("Set silence",0,1,0,"报警消音设置值",true)
                    }
                },

                AsyncAction = AsyncWrapAction("报警消音", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        ushort targetValue = parameter.GetValue<ushort>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                        }
                        else
                        {
                            // 发送的指令报文
                            //byte[] frame = { slaveId, 6, 0, 14, 0, value };
                            byte[] frame = BuildWriteCmd(14, targetValue);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success",success,"设置成功",true),
                                new NumberParameter("Set silence",0,1,targetValue,"报警消音设置值",true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"100ml动态管反报警消音设置失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置 报警
            Commands.Add(new DeviceCommand()
            {
                Name = "设置报警状态",
                HelpText = "设置100ml动态管反报警",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue",0,1,0,"目标值",true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success",false,"设置成功",true),
                        new NumberParameter("SetStatus",0,1,0,"报警消音设置值",true)
                    }
                },

                AsyncAction = AsyncWrapAction("设置报警状态", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        ushort targetValue = parameter.GetValue<ushort>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                        }
                        else
                        {
                            // 发送的指令报文
                            //byte[] frame = { slaveId, 6, 0, 15, 0, value };
                            byte[] frame = BuildWriteCmd(15, targetValue);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success",success,"设置成功",true),
                                new NumberParameter("SetStatus",0,1,targetValue,"报警状态设置值",true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"100ml动态管反设置报警状态失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取所有数据（屏幕轮询用，一条读全部）
            Commands.Add(new DeviceCommand()
            {
                Name = "获取所有数据",
                HelpText = "一次读取 搅拌SV/PV、釜温、压力、搅拌ON/OFF（屏幕轮询用，共9个寄存器）",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("SV",          0, 1000, 0, "搅拌 SV",  true, "RPM"),
                        new NumberParameter("PV",          0, 1000, 0, "搅拌 PV",  true, "RPM"),
                        new NumberParameter("Temperature", 0, 1000, 0, "釜温",     true, "℃"),
                        new NumberParameter("Pressure",    0, 1000, 0, "压力",     true, "MPa"),
                        new NumberParameter("ON/OFF",      0, 1,    0, "搅拌 ON/OFF(0:运行,1:停止)", true),
                    }
                },

                AsyncAction = AsyncWrapAction("获取所有数据", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    float sv = 0, pv = 0, temp = 0, press = 0;
                    int onoff = 0;

                    if (IsSimulationMode)
                    {
                        await Task.Delay(200);
                        var rnd = new Random();
                        sv = rnd.Next(0, 300);
                        pv = sv + rnd.Next(-5, 5);
                        temp = rnd.Next(20, 200);
                        press = (float)(rnd.NextDouble() * 5);
                        onoff = rnd.Next(0, 2);
                    }
                    else
                    {
                        // 一次读 9 个寄存器（地址 0~8）
                        byte[] frame = BuildReadCmd(0, 9);
                        ushort[] regs = await SendAndReceiveWrapperAsync(frame, CancellationToken.None, SendAndParseUInt16ArrayAsync);

                        if (regs != null && regs.Length >= 9)
                        {
                            sv = RegsToFloat(regs[0], regs[1]);
                            pv = RegsToFloat(regs[2], regs[3]);
                            temp = RegsToFloat(regs[4], regs[5]);
                            press = RegsToFloat(regs[6], regs[7]);
                            onoff = (short)regs[8];
                        }
                    }

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new NumberParameter("SV",          0, 1000, sv,    "搅拌 SV",  true, "RPM"),
                            new NumberParameter("PV",          0, 1000, pv,    "搅拌 PV",  true, "RPM"),
                            new NumberParameter("Temperature", 0, 1000, temp,  "釜温",     true, "℃"),
                            new NumberParameter("Pressure",    0, 1000, press, "压力",     true, "MPa"),
                            new NumberParameter("ON/OFF",      0, 1,    onoff, "搅拌 ON/OFF(0:运行,1:停止)", true),
                        }
                    };
                })
            });
        }
        /// <summary>
        /// 把两个寄存器(高字在前)按当前配置的浮点字节序拼成 float。
        /// 复用 RestoreFloatByteOrder + ReverseArray，与单点读 SendAndParseSingleAsync 一致。
        /// </summary>
        private float RegsToFloat(ushort high, ushort low)
        {
            byte[] data = new byte[4]
            {
                (byte)(high >> 8), (byte)(high & 0xFF),
                (byte)(low  >> 8), (byte)(low  & 0xFF),
            };
            byte[] abcd = RestoreFloatByteOrder(data);   // 按配置字节序还原成大端 ABCD
            if (BitConverter.IsLittleEndian)
                ReverseArray(abcd);                       // 大端 → 本机字节序
            return BitConverter.ToSingle(abcd, 0);
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


        protected override async Task<bool> OnConnectViaGatewayAsync()
        {
            try
            {
                InfoLog("通过 ZLAN 网关测试连接");
                byte[] sendCmd = BuildReadCmd(register: 15, count: 1);
                var pressure = await SendAndParseInt16Async(sendCmd, CancellationToken.None);
                InfoLog($"网关连接成功,当前报警状态 {pressure}");
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


        #region 命令包装(日志 + 异常封装)

        private Func<DeviceParameters, DeviceParameters> WrapAction(string commandName, Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return (DeviceParameters parameters) =>
            {
                _logger.LogInformation($"[ModbusRTU100ml动态管式反应釜-开始] Device={Name} Command={commandName}");

                var sw = Stopwatch.StartNew();
                try
                {
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();

                    _logger.LogInformation($"[ModbusRTU100ml动态管式反应釜-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[ModbusRTU100ml动态管式反应釜-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        private Func<DeviceParameters, Task<DeviceParameters>> AsyncWrapAction(
            string commandName, Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return async (DeviceParameters parameters) =>
            {
                _logger.LogInformation($"[ModbusRTU平流输液泵命令-开始] Device={Name} Command={commandName}");
                var sw = Stopwatch.StartNew();
                try
                {
                    var output = await innerAsync(parameters);
                    sw.Stop();
                    _logger.LogInformation($"[ModbusRTU平流输液泵命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[ModbusRTU平流输液泵命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
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
        /// <summary>
        /// 构造 Modbus RTU 写多个寄存器命令 (功能码 0x10)，写入一个 32 位 IEEE754 浮点值。
        /// 数据字节按配置的字节序排列，自动加 CRC。
        /// </summary>
        /// <param name="register">起始寄存器地址 (协议地址，0 基)</param>
        /// <param name="value">要写入的浮点值</param>
        private byte[] BuildMultWriteCmd(ushort register, float value)
        {
            byte slaveId = GetSlaveAddress();

            // 1. 取 IEEE754 标准的 4 字节 (BitConverter 在小端机器上得到的是 DCBA 顺序)
            byte[] raw = BitConverter.GetBytes(value);
            if (BitConverter.IsLittleEndian)
            {
                ReverseArray(raw);   // 统一翻成 ABCD (大端) 作为基准
            }

            // 2. 按配置的字节序重排
            byte[] data = ApplyFloatByteOrder(raw);

            // 3. 拼装 0x10 报文：站号 功能码 起始地址(2) 寄存器数量(2) 字节数(1) 数据(4)
            byte[] frame =
            {
        slaveId,
        0x10,
        (byte)(register >> 8), (byte)(register & 0xFF),
        0x00, 0x02,            // 写 2 个寄存器
        0x04,                  // 字节数 = 4
        data[0], data[1], data[2], data[3],
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

        /// <summary>
        /// 读取 Modbus 多个寄存器，并解析 float 数据
        /// </summary>
        /// <param name="sendCmd"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        private async Task<float> SendAndParseSingleAsync(byte[] sendCmd, CancellationToken token)
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

                // 提取数据载荷 (4 字节，设备按配置的字节序发送)
                byte[] data = ExtractPayload(recv);
              
                // 1. 先按配置的字节序还原成大端 ABCD 基准
                byte[] abcd = RestoreFloatByteOrder(data);

                // 2. 大端 → 本机字节序 (小端机器需翻转后再解析 IEEE754)
                if (BitConverter.IsLittleEndian)
                {
                    ReverseArray(abcd);
                }
                return BitConverter.ToSingle(abcd, 0);
            }
            finally
            {
                slim.Release();
            }
        }
        /// <summary>
        /// 把大端 ABCD 基准的 4 字节，按当前配置的字节序重排（用于发送）。
        /// </summary>
        private byte[] ApplyFloatByteOrder(byte[] abcd)
        {
            string order = Parameters.GetValue<string>("浮点字节序") ?? "ABCD";
            // abcd[0]=A abcd[1]=B abcd[2]=C abcd[3]=D
            switch (order)
            {
                case "CDAB": return new byte[] { abcd[2], abcd[3], abcd[0], abcd[1] };
                case "BADC": return new byte[] { abcd[1], abcd[0], abcd[3], abcd[2] };
                case "DCBA": return new byte[] { abcd[3], abcd[2], abcd[1], abcd[0] };
                case "ABCD":
                default: return new byte[] { abcd[0], abcd[1], abcd[2], abcd[3] };
            }
        }

        /// <summary>
        /// 把设备发来的（按配置字节序排列的）4 字节，还原成大端 ABCD 基准（用于接收）。
        /// 注意：这几种排列都是自反的，还原映射与发送映射一致。
        /// </summary>
        private byte[] RestoreFloatByteOrder(byte[] recv)
        {
            string order = Parameters.GetValue<string>("浮点字节序") ?? "ABCD";
            switch (order)
            {
                case "CDAB": return new byte[] { recv[2], recv[3], recv[0], recv[1] };
                case "BADC": return new byte[] { recv[1], recv[0], recv[3], recv[2] };
                case "DCBA": return new byte[] { recv[3], recv[2], recv[1], recv[0] };
                case "ABCD":
                default: return new byte[] { recv[0], recv[1], recv[2], recv[3] };
            }
        }
        /// <summary>
        /// 读取 Modbus 多个寄存器，并解析为 UInt16 数组
        /// </summary>
        /// <param name="sendCmd"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        private async Task<ushort[]> SendAndParseUInt16ArrayAsync(byte[] sendCmd, CancellationToken token)
        {
            var slim = GetInstanceLock();
            await slim.WaitAsync(token).ConfigureAwait(false);

            try
            {
                byte[] recv = new byte[256];
                using var transport = await GetTransportAsync().ConfigureAwait(false);
                int len = await transport.SendAndReceiveAsync(sendCmd, recv, 3000, token).ConfigureAwait(false);

                // 应答最短:站号(1)+功能码(1)+长度(1)+数据(8)+CRC(2) = 13 字节
                if (len < 13)
                {
                    _logger?.LogWarning("应答帧长度不足:{Len} 字节,需要至少 13 字节", len);
                    return Array.Empty<ushort>();
                }

                // 校验站号
                byte expectedAddr = GetSlaveAddress();
                if (recv[0] != expectedAddr)
                {
                    _logger?.LogWarning("站号不匹配:期望 0x{Expected:X2},实际 0x{Actual:X2}", expectedAddr, recv[0]);
                    return Array.Empty<ushort>();
                }

                // 校验功能码(异常码 0x83 单独识别)
                if ((recv[1] & 0x80) != 0)
                {
                    _logger?.LogWarning("Modbus 异常应答,异常码 0x{Code:X2}", recv[2]);
                    return Array.Empty<ushort>();
                }
                if (recv[1] != 0x03)
                {
                    _logger?.LogWarning("功能码不匹配:期望 0x03,实际 0x{Actual:X2}", recv[1]);
                    return Array.Empty<ushort>();
                }

                byte[] data = ExtractPayload(recv);

                return ByteArrayToUshortArray(data);
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

        /// <summary>
        /// 将字节数组转换成ushort数组
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        private ushort[] ByteArrayToUshortArray(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException("输入字节数为空");
            }

            if (data.Length == 0 || data.Length%2 != 0)
            {
                throw new ArgumentException($"输入字节数组长度无效，应为2的倍数。数组长度：{data.Length}");
            }

            ushort[] result = new ushort[data.Length / 2];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = (ushort)(data[i * 2] << 8 | data[i * 2 + 1]);
            }
            return result;
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
