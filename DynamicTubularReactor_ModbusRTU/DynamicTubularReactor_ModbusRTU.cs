using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO.Ports;

namespace DynamicTubularReactor_ModbusRTU
{
    [Export(typeof(IDevice))]
    public class DynamicTubularReactor_ModbusRTU : Device, IFlowLifecycleAware
    {
        private readonly ILogService _logger;

        private Task _dataTask;
        private CancellationTokenSource _dataCts;

        // 限制每次只能打开一个串口
        private readonly SemaphoreSlim _slim = new SemaphoreSlim(1, 1);

        public DynamicTubularReactor_ModbusRTU()
        {
            _logger = LogManager.GetLogger<DynamicTubularReactor_ModbusRTU>();

            DeviceId = "DTR0001";
            Name = "100ml动态管式反应釜";
            Manufacturer = "ModbusRTU 100ml动态管式反应釜";
            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/PeristalticPumps_CY.png";
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

            InitializeCommands();
        }

        private void InitializeCommands()
        {
            // SV
            Commands.Add(new DeviceCommand()
            {
                Name = "获取搅拌 SV",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("SV", 0 , 1000, 0, "100ml动态管反搅拌 SV", true)
                    }
                },

                Action = WrapAction("获取搅拌 SV", async parameter =>
                {
                    float value = 0;
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            value = new Random().Next(19, 100);
                        }
                        else
                        {
                            // 发送的指令报文
                            byte[] frame = new byte[6];
                            frame[0] = slaveId; // 从站地址
                            frame[1] = 3;       // 功能码
                            frame[2] = 0;       // 起始地址
                            frame[3] = 0;
                            frame[4] = 0;       // 读取数量
                            frame[5] = 2;
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                byte[] data = ExtractPayload(buffers);
                                if (BitConverter.IsLittleEndian)
                                {
                                    ReverseArray(data);
                                }
                                value = BitConverter.ToSingle(data);
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        ErrorLog(e.Message);
                        throw;
                    }

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new NumberParameter("SV", 0 , 1000, value, "100ml动态管反搅拌 SV", true)
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
                        new NumberParameter("PV", 0, 100, 0, "搅拌 PV", true)
                    }
                },

                Action = WrapAction("获取搅拌速度", async parameter =>
                {
                    float speed = 0;
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            speed = new Random().Next(19, 100);
                        }
                        else
                        {
                            // 发送的指令报文
                            byte[] frame = new byte[6] { slaveId, 3, 0, 2, 0, 2 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                byte[] data = ExtractPayload(buffers);
                                if (BitConverter.IsLittleEndian)
                                {
                                    ReverseArray(data);
                                }
                                speed = BitConverter.ToSingle(data);
                            }
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
                            new NumberParameter("PV", 0 , 1000, speed, "搅拌速度", true)
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
                        new NumberParameter("Temperature", 0, 1000, 0, "反应釜温度", true)
                    }
                },

                Action = WrapAction("获取反应釜温度", async parameter =>
                {
                    float temperature = 0;
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            temperature = new Random().Next(19, 100);
                        }
                        else
                        {
                            // 发送的指令报文
                            byte[] frame = new byte[6] { slaveId, 3, 0, 4, 0, 2 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                byte[] data = ExtractPayload(buffers);
                                if (BitConverter.IsLittleEndian)
                                {
                                    ReverseArray(data);
                                }
                                temperature = BitConverter.ToSingle(data);
                            }
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
                            new NumberParameter("Temperature", 0, 1000, temperature, "反应釜温度", true)
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
                        new NumberParameter("Pressure", 0, 1000, 0, "反应釜压力", true)
                    }
                },

                Action = WrapAction("获取反应釜压力", async parameter =>
                {
                    float pressure = 0;
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            pressure = new Random().Next(19, 100);
                        }
                        else
                        {
                            // 发送的指令报文
                            byte[] frame = new byte[6] { slaveId, 3, 0, 6, 0, 2 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                byte[] data = ExtractPayload(buffers);
                                if (BitConverter.IsLittleEndian)
                                {
                                    ReverseArray(data);
                                }
                                pressure = BitConverter.ToSingle(data);
                            }
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
                            new NumberParameter("Temperature", 0, 1000, pressure, "反应釜压力", true)
                        }
                    };
                })
            });

            // 获取搅拌 ON/OFF
            Commands.Add(new DeviceCommand()
            {
                Name = "获取搅拌 ON/OFF",
                HelpText = "获取100ml动态管反 搅拌 ON/OFF",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("搅拌 ON/OFF", 0, 1, 0, "100ml动态管反 搅拌 ON/OFF(1：停止，0：运行)", true)
                    }
                },

                Action = WrapAction("获取搅拌 ON/OFF", async parameter =>
                {
                    float pressure = 0;
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            pressure = new Random().Next(19, 100);
                        }
                        else
                        {
                            // 发送的指令报文
                            byte[] frame = new byte[6] { slaveId, 3, 0, 8, 0, 1 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                byte[] data = ExtractPayload(buffers);
                                if (BitConverter.IsLittleEndian)
                                {
                                    ReverseArray(data);
                                }
                                pressure = BitConverter.ToInt16(data);
                            }
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
                            new NumberParameter("搅拌 ON/OFF", 0, 1, pressure, "100ml动态管反 搅拌 ON/OFF(1：停止，0：运行)", true)
                        }
                    };
                })
            });

            // 获取 报警
            Commands.Add(new DeviceCommand()
            {
                Name = "获取报警数据",

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

                Action = WrapAction("获取报警数据", async parameter =>
                {
                    int alarm1 = 0;
                    int alarm2 = 0;
                    int alarm3 = 0;
                    int alarm4 = 0;
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            alarm1 = new Random().Next(-1, 1);
                        }
                        else
                        {
                            // 发送的指令报文
                            byte[] frame = { slaveId, 3, 0, 9, 0, 4 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                byte[] data = ExtractPayload(buffers);
                                alarm1 = data[0] << 8 | data[1];
                                alarm2 = data[2] << 8 | data[3];
                                alarm3 = data[4] << 8 | data[5];
                                alarm4 = data[6] << 8 | data[7];
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

            // 获取 报警消音
            Commands.Add(new DeviceCommand()
            {
                Name = "获取报警消音状态",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Al silence", false, "100ml动态管反消音状态", true)
                    }
                },

                Action = WrapAction("获取报警消音状态", async parameter =>
                {
                    int pressure = 0;
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            pressure = new Random().Next(-1, 1);
                        }
                        else
                        {
                            // 发送的指令报文
                            byte[] frame = new byte[6] { slaveId, 3, 0, 14, 0, 1 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                byte[] data = ExtractPayload(buffers);
                                if (BitConverter.IsLittleEndian)
                                {
                                    ReverseArray(data);
                                }
                                pressure = BitConverter.ToInt16(data, 0);
                            }
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
                            new BooleanParameter("Al silence", pressure == 0 ? false : true, "100ml动态管反消音状态", true)
                        }
                    };
                })
            });

            // 获取 报警状态
            Commands.Add(new DeviceCommand()
            {
                Name = "获取报警状态",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Alarm status", false, "获取100ml动态管反报警状态(0:无报警，1:有报警)", true)
                    }
                },

                Action = WrapAction("获取报警状态", async parameter =>
                {
                    int pressure = 0;
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            pressure = new Random().Next(-1, 1);
                        }
                        else
                        {
                            // 发送的指令报文
                            byte[] frame = new byte[6] { slaveId, 3, 0, 15, 0, 1 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                byte[] data = ExtractPayload(buffers);
                                if (BitConverter.IsLittleEndian)
                                {
                                    ReverseArray(data);
                                }
                                pressure = BitConverter.ToInt16(data, 0);
                            }
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
                            new BooleanParameter("Alarm status", pressure == 0 ? false : true, "获取100ml动态管反报警状态(0:无报警，1:有报警)", true)
                        }
                    };
                })
            });


            // 设置 SV
            Commands.Add(new DeviceCommand()
            {
                Name = "设置 SV",

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
                        new NumberParameter("Set SV Value",0,1000,0,"SV 设置值",true)
                    }
                },

                Action = WrapAction("设置 SV", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
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
                            byte[] frame = { slaveId, 10, 0, 0, 0, 2, 4, data[0], data[1], data[2], data[3] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                        ErrorLog($"SV 设置失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置 PV
            Commands.Add(new DeviceCommand()
            {
                Name = "设置 PV",

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

                Action = WrapAction("设置 PV", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
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
                            byte[] frame = { slaveId, 10, 0, 2, 0, 2, 4, data[0], data[1], data[2], data[3] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                        ErrorLog($"PV 设置失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置釜温
            Commands.Add(new DeviceCommand()
            {
                Name = "设置釜温",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue",0,1000,0,"目标值",true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success",false,"设置成功",true),
                        new NumberParameter("SetTemperature",0,1000,0,"釜温设置值",true)
                    }
                },

                Action = WrapAction("设置釜温", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
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
                            byte[] frame = { slaveId, 10, 0, 4, 0, 2, 4, data[0], data[1], data[2], data[3] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success",success,"设置成功",true),
                                new NumberParameter("SetTemperature",0,1000,targetValue,"釜温设置值",true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"釜温设置失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置反应釜压力
            Commands.Add(new DeviceCommand()
            {
                Name = "设置压力",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue",0,1000,0,"目标值",true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success",false,"设置成功",true),
                        new NumberParameter("SetPressure",0,1000,0,"压力设置值",true)
                    }
                },

                Action = WrapAction("设置压力", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
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
                            byte[] frame = { slaveId, 10, 0, 6, 0, 2, 4, data[0], data[1], data[2], data[3] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success",success,"设置成功",true),
                                new NumberParameter("SetPressure",0,1000,targetValue,"压力设置值",true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"压力设置失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置 ON/OFF
            Commands.Add(new DeviceCommand()
            {
                Name = "设置 ON/OFF",

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

                Action = WrapAction("设置 ON/OFF", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        short targetValue = parameter.GetValue<short>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                        }
                        else
                        {
                            byte value = (byte)targetValue;

                            // 发送的指令报文
                            byte[] frame = { slaveId, 6, 0, 8, 0, value };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                        ErrorLog($"ON/OFF 设置失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置 釜温上限报警
            Commands.Add(new DeviceCommand()
            {
                Name = "设置釜温上限报警",

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

                Action = WrapAction("设置釜温上限报警", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        short targetValue = parameter.GetValue<short>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                        }
                        else
                        {
                            byte value = (byte)targetValue;

                            // 发送的指令报文
                            byte[] frame = { slaveId, 6, 0, 9, 0, value };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                        ErrorLog($"釜温上限报警设置失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置 压力上限报警
            Commands.Add(new DeviceCommand()
            {
                Name = "设置压力上限报警",

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

                Action = WrapAction("设置压力上限报警", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        short targetValue = parameter.GetValue<short>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                        }
                        else
                        {
                            byte value = (byte)targetValue;

                            // 发送的指令报文
                            byte[] frame = { slaveId, 6, 0, 10, 0, value };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                        ErrorLog($"压力上限报警设置失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置 釜温通信故障
            Commands.Add(new DeviceCommand()
            {
                Name = "设置釜温通信故障",

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

                Action = WrapAction("设置釜温通信故障", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        short targetValue = parameter.GetValue<short>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                        }
                        else
                        {
                            byte value = (byte)targetValue;

                            // 发送的指令报文
                            byte[] frame = { slaveId, 6, 0, 11, 0, value };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                        ErrorLog($"釜温通信故障设置失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置 压力通讯故障
            Commands.Add(new DeviceCommand()
            {
                Name = "设置压力通讯故障",

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

                Action = WrapAction("设置压力通讯故障", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        short targetValue = parameter.GetValue<short>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                        }
                        else
                        {
                            byte value = (byte)targetValue;

                            // 发送的指令报文
                            byte[] frame = { slaveId, 6, 0, 12, 0, value };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                        ErrorLog($"压力通讯故障设置失败：{e.Message}");
                        throw;
                    }
                })
            });

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

                Action = WrapAction("报警复位", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);

                        }
                        else
                        {
                            // 发送的指令报文
                            byte[] frame = { slaveId, 6, 0, 13, 0, 1 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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

                Action = WrapAction("报警消音", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        short targetValue = parameter.GetValue<short>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                        }
                        else
                        {
                            byte value = (byte)targetValue;

                            // 发送的指令报文
                            byte[] frame = { slaveId, 6, 0, 14, 0, value };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                        ErrorLog($"报警消音设置失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置 报警
            Commands.Add(new DeviceCommand()
            {
                Name = "设置报警状态",

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

                Action = WrapAction("设置报警状态", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        short targetValue = parameter.GetValue<short>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                        }
                        else
                        {
                            byte value = (byte)targetValue;

                            // 发送的指令报文
                            byte[] frame = { slaveId, 6, 0, 15, 0, value };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                        ErrorLog($"设置报警状态失败：{e.Message}");
                        throw;
                    }
                })
            });
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


        private Func<DeviceParameters, DeviceParameters> WrapAction(string commandName, Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return (DeviceParameters parameters) =>
            {
                _logger.LogInformation($"[ModbusRTU平流输液泵命令-开始] Device={Name} Command={commandName}");

                var sw = Stopwatch.StartNew();
                try
                {
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
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


        private async Task<int> ReadAsync(byte[] buffers, int offset, int count, CancellationToken token)
        {
            SerialPort serialPort = null;
            try
            {
                string com = Parameters.GetValue<string>("串口号");
                string baudRate = Parameters.GetValue<string>("波特率");
                string parity = Parameters.GetValue<string>("校验位");
                string dataBit = Parameters.GetValue<string>("数据位");
                string stopBit = Parameters.GetValue<string>("停止位");

                // 等待释放信号
                await _slim.WaitAsync(token);

                serialPort = new SerialPort(com);
                serialPort.BaudRate = int.Parse(baudRate);
                serialPort.Parity = (Parity)Enum.Parse(typeof(Parity), parity);
                serialPort.DataBits = int.Parse(dataBit);
                serialPort.StopBits = (StopBits)Enum.Parse(typeof(StopBits), stopBit);
                serialPort.ReadTimeout = 3000;
                serialPort.WriteTimeout = 3000;

                // 打开串口
                serialPort.Open();

                // 发送读取指令
                await serialPort.BaseStream.WriteAsync(buffers, offset, count, token);

                var reader = serialPort.BaseStream.ReadAsync(buffers, 0, buffers.Length, token);
                var complete = await Task.WhenAny(reader, Task.Delay(3000, token));
                if (complete != reader)
                {
                    throw new TimeoutException("串口读取超时，设备无响应");
                }
                // 返回读取结果
                return await reader;
            }
            finally
            {
                try
                {
                    if (serialPort != null)
                    {
                        serialPort.Close();
                        serialPort.Dispose();
                    }
                }
                catch { }

                // 释放信号
                _slim.Release();
            }
        }

        private async Task<bool> WriteAsync(byte[] buffers, int offset, int count, CancellationToken token)
        {
            SerialPort serialPort = null;
            try
            {
                string com = Parameters.GetValue<string>("串口号");
                string baudRate = Parameters.GetValue<string>("波特率");
                string parity = Parameters.GetValue<string>("校验位");
                string dataBit = Parameters.GetValue<string>("数据位");
                string stopBit = Parameters.GetValue<string>("停止位");

                // 等待释放信号
                await _slim.WaitAsync(token);

                serialPort = new SerialPort(com);
                serialPort.BaudRate = int.Parse(baudRate);
                serialPort.Parity = (Parity)Enum.Parse(typeof(Parity), parity);
                serialPort.DataBits = int.Parse(dataBit);
                serialPort.StopBits = (StopBits)Enum.Parse(typeof(StopBits), stopBit);
                serialPort.ReadTimeout = 3000;
                serialPort.WriteTimeout = 3000;

                // 打开串口
                serialPort.Open();

                // 发送指令
                await serialPort.BaseStream.WriteAsync(buffers, offset, count, token);
                // 接收响应报文
                byte[] frame = new byte[128];
                var response = serialPort.BaseStream.ReadAsync(frame, 0, frame.Length, token);

                if (await Task.WhenAny(Task.Delay(3000), response) != response)
                {
                    throw new TimeoutException("串口写入超时，设备无响应");
                }

                int len = await response;
                if (len < 5)
                {
                    throw new Exception("写入失败：响应报文长度无效");
                }

                if (frame[1] >= 0x80)
                {
                    throw new ArgumentException($"写入失败，Modbus 异常响应，异常码：{frame[2]:X2}");
                }

                return true;
            }
            finally
            {
                try
                {
                    if (serialPort != null)
                    {
                        serialPort.Close();
                        serialPort.Dispose();
                    }
                }
                catch { }

                // 释放信号
                _slim.Release();
            }
        }

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
