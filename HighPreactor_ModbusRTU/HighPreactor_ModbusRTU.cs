using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO.Ports;

namespace HighPreactor_ModbusRTU
{
    [Export(typeof(IDevice))]
    public class HighPreactor_ModbusRTU : Device, IFlowLifecycleAware
    {
        private readonly ILogService _logger;


        // 限制每次只能打开一个串口
        private readonly SemaphoreSlim _slim = new SemaphoreSlim(1, 1);


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

            // 初始化命令
            InitializeCommands();
        }

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
                        new NumberParameter("Temperatur",-200,600,0,"反应釜釜温",true,"℃")
                    }
                },

                Action = WrapAction("获取釜温", async parameter =>
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
                            InfoLog($"模拟模式获取微型反应釜温度，结果（{temperature} ℃）");
                        }
                        else
                        {
                            byte[] frame = { slaveId, 0x03, 0x00, 0x00, 0x00, 0x02 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);
                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                // 字节序 CDAB
                                byte[] data = ExtractPayload(buffers);
                                // DCBA
                                byte[] dcba = { data[1], data[0], data[3], data[2] };
                                if (!BitConverter.IsLittleEndian)
                                {
                                    ReverseArray(dcba);
                                }
                                temperature = BitConverter.ToInt32(data);
                                InfoLog($"获取微型反应釜温度，结果（{temperature/100.0} ℃）");
                            }
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("Temperatur",-200,600,temperature/100.0,"反应釜釜温",true,"℃")
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
                        new NumberParameter("Temperatur",-200,600,0,"反应釜炉温",true,"℃")
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
                            byte[] frame = { slaveId, 0x03, 0x00, 0x04, 0x00, 0x02 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);
                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                // 字节序 CDAB
                                byte[] data = ExtractPayload(buffers);
                                // DCBA
                                byte[] dcba = { data[1], data[0], data[3], data[2] };
                                if (!BitConverter.IsLittleEndian)
                                {
                                    ReverseArray(dcba);
                                }
                                temperature = BitConverter.ToInt32(data);
                                InfoLog($"获取微型反应釜炉温，结果（{temperature/100.0} ℃）");
                            }
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("Temperatur",-200,600,temperature/100.0,"反应釜炉温",true,"℃")
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
                        new NumberParameter("Temperatur",-200,600,0,"反应釜压力",true,"Bar")
                    }
                },

                Action = WrapAction("获取压力", async parameter =>
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
                            InfoLog($"模拟模式获取微型反应釜压力，结果（{temperature} Bar）");
                        }
                        else
                        {
                            byte[] frame = { slaveId, 0x03, 0x01, 0x06, 0x00, 0x01 };
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
                                temperature = BitConverter.ToInt16(data);
                                InfoLog($"获取微型反应釜压力，结果（{temperature / 100.0} Bar）");
                            }
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("Temperatur",-200,600,temperature/100.0,"反应釜压力",true,"Bar")
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
                        new NumberParameter("Speed", 0, 1500,0,"反应釜搅拌速度",true,"RPM")
                    }
                },

                Action = WrapAction("获取搅拌速度", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        int temperature = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            temperature = new Random().Next(-1, 15)*100;
                            InfoLog($"模拟模式获取微型反应釜搅拌速度，结果（{temperature} RPM）");
                        }
                        else
                        {
                            byte[] frame = { slaveId, 0x03, 0x00, 0xFB, 0x00, 0x01 };
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
                                temperature = BitConverter.ToInt16(data);
                                InfoLog($"获取微型反应釜搅拌速度，结果（{temperature} RPM）");
                            }
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("Speed", 0, 1500,temperature,"反应釜搅拌速度",true,"RPM")
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
                        new NumberParameter("Temperatur",-200,600,0,"反应釜设定温度",true,"℃")
                    }
                },

                Action = WrapAction("获取设定温度", async parameter =>
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
                            InfoLog($"模拟模式获取微型反应釜设定温度，结果（{temperature} ℃）");
                        }
                        else
                        {
                            byte[] frame = { slaveId, 0x03, 0x00, 0x32, 0x00, 0x02 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);
                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                // 字节序 CDAB
                                byte[] data = ExtractPayload(buffers);
                                // DCBA
                                byte[] dcba = { data[1], data[0], data[3], data[2] };
                                if (!BitConverter.IsLittleEndian)
                                {
                                    ReverseArray(dcba);
                                }
                                temperature = BitConverter.ToInt32(data);
                                InfoLog($"获取微型反应釜设定温度，结果（{temperature / 100.0} ℃）");
                            }
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("Temperatur",-200,600,temperature/100.0,"反应釜设定温度",true,"℃")
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
                            byte[] data = BitConverter.GetBytes((int)(temperature * 100));
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data); // ABCD
                            }
                            // 寄存器 CDAB
                            byte[] frame = { slaveId, 0x10, 0x00, 0x32, 0x00, 0x02, 0x04, data[2], data[3], data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                        new NumberParameter("Speed", 0, 1500,0,"反应釜搅拌速度 SV",true,"RPM")
                    }
                },

                Action = WrapAction("获取搅拌速度 SV", async parameter =>
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
                            InfoLog($"模拟模式获取微型反应釜搅拌速度 SV，结果（{temperature} RPM）");
                        }
                        else
                        {
                            byte[] frame = { slaveId, 0x03, 0x00, 0xFC, 0x00, 0x01 };
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
                                temperature = BitConverter.ToInt16(data);
                                InfoLog($"获取微型反应釜搅拌速度 SV，结果（{temperature} RPM）");
                            }
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("Speed", 0, 1500,temperature,"反应釜搅拌速度 SV",true,"RPM")
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
                        short temperature = parameter.GetValue<short>("TargetSpeed");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            InfoLog($"模拟模式设置微型反应釜搅拌速度 SV，结果（成功：{success}，设定值：{temperature} RPM）");
                        }
                        else
                        {
                            byte[] data = BitConverter.GetBytes(temperature);
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }
                            // 创建指令
                            byte[] frame = { slaveId, 0x06, 0x00, 0xFC, data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                        new NumberParameter("TemperatureUpperLimit",-200,600,0,"反应釜釜温上限",true,"℃")
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
                            byte[] frame = { slaveId, 0x03, 0x01, 0x0E, 0x00, 0x02 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                // 字节序 CDAB
                                byte[] data = ExtractPayload(buffers);
                                // DCBA
                                byte[] dcba = { data[1], data[0], data[3], data[2] };
                                if (!BitConverter.IsLittleEndian)
                                {
                                    ReverseArray(dcba);
                                }
                                temperature = BitConverter.ToInt32(data);
                                InfoLog($"获取微型反应釜温度上限，结果（{temperature / 100.0} ℃）");
                            }
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("TemperatureUpperLimit",-200,600,temperature/100.0,"反应釜釜温上限",true,"℃")
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
                            byte[] data = BitConverter.GetBytes((int)(temperature * 100));
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }

                            byte[] frame = { slaveId, 0x10, 0x01, 0x0E, 0x00, 0x02, 0x04, data[2], data[3], data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                        new NumberParameter("TemperatureUpperLimit",-200,600,0,"反应釜炉温上限",true,"℃")
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
                            byte[] frame = { slaveId, 0x03, 0x01, 0x10, 0x00, 0x02 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                // 字节序 CDAB
                                byte[] data = ExtractPayload(buffers);
                                // DCBA
                                byte[] dcba = { data[1], data[0], data[3], data[2] };
                                if (!BitConverter.IsLittleEndian)
                                {
                                    ReverseArray(dcba);
                                }
                                temperature = BitConverter.ToInt32(data);
                                InfoLog($"获取微型反应釜炉温上限，结果（{temperature / 100.0} ℃）");
                            }
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("TemperatureUpperLimit",-200,600,temperature/100.0,"反应釜炉温上限",true,"℃")
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
                            byte[] data = BitConverter.GetBytes((int)(temperature * 100));
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }

                            byte[] frame = { slaveId, 0x10, 0x01, 0x10, 0x00, 0x02, 0x04, data[2], data[3], data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                            byte[] frame = { slaveId, 0x03, 0x01, 0x12, 0x00, 0x01 };
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
                                temperature = BitConverter.ToInt16(data);
                                InfoLog($"获取微型反应釜压力上限，结果（{temperature / 100.0} Bar）");
                            }
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
                            byte[] data = BitConverter.GetBytes((short)(temperature * 100));
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }
                            // 创建指令
                            byte[] frame = { slaveId, 0x06, 0x01, 0x12, data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                            byte[] frame = { slaveId, 0x03, 0x00, 0x7F, 0x00, 0x01 };
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
                                temperature = BitConverter.ToInt16(data);
                                InfoLog($"获取微型反应釜转换拟合阈值，结果（{temperature / 100.0} ℃）");
                            }
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
                            byte[] data = BitConverter.GetBytes((short)(temperature * 100));
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }
                            // 创建指令
                            byte[] frame = { slaveId, 0x06, 0x00, 0x7F, data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                            byte[] frame = { slaveId, 0x03, 0x00, 0x81, 0x00, 0x01 };
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
                                temperature = BitConverter.ToInt16(data);
                                InfoLog($"获取微型反应釜升温斜率，结果（{temperature / 100.0} ℃）");
                            }
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
                            byte[] data = BitConverter.GetBytes((short)(temperature * 100));
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }
                            // 创建指令
                            byte[] frame = { slaveId, 0x06, 0x00, 0x81, data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                            byte[] frame = { slaveId, 0x03, 0x01, 0x04, 0x00, 0x01 };
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
                                temperature = BitConverter.ToInt16(data);
                                InfoLog($"获取微型反应釜已运行时间，结果（{temperature} MIN）");
                            }
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
                        short temperature = parameter.GetValue<short>("TargetTime");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置微型反应釜运行时间，结果（成功：{success}，设定值：{temperature} MIN）");
                        }
                        else
                        {
                            byte[] data = BitConverter.GetBytes(temperature);
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }
                            // 创建指令
                            byte[] frame = { slaveId, 0x06, 0x01, 0x04, data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                            byte[] frame = { slaveId, 0x03, 0x01, 0xF5, 0x00, 0x01 };
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
                                temperature = BitConverter.ToInt16(data);
                                InfoLog($"获取微型反应釜当前运行段，结果（{temperature} ）");
                            }
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
                            byte[] frame = { slaveId, 0x03, 0x01, 0xF6, 0x00, 0x02 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                byte[] data = ExtractPayload(buffers);
                                duration = (short)(data[0] << 8 | data[1]);
                                total = (short)(data[2] << 8 | data[3]);

                                InfoLog($"获取微型反应釜当前段运行时间数据，结果（Run:{duration / 10.0}，Total:{total / 10.0} MIN）");
                            }
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
                            byte[] frame = { slaveId, 0x03, 0x01, 0xF8, 0x00, 0x02 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);
                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                // 字节序 CDAB
                                byte[] data = ExtractPayload(buffers);
                                // DCBA
                                byte[] dcba = { data[1], data[0], data[3], data[2] };
                                if (!BitConverter.IsLittleEndian)
                                {
                                    ReverseArray(dcba);
                                }
                                temperature = BitConverter.ToInt32(data);
                                InfoLog($"获取微型反应釜实时设定值，结果（{temperature / 100.0} ℃）");
                            }
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

            // 获取SP1
            Commands.Add(new DeviceCommand()
            {
                Name = "获取SP1",
                HelpText = "获取微型反应釜SP1",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("SP1",-200,600,0,"",true,"℃")
                    }
                },

                Action = WrapAction("获取SP1", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        int sp = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            sp = new Random().Next(-3, 6) * 100; // 模拟 -200 到 600 度
                            InfoLog($"模拟模式获取微型反应釜SP1，结果（{sp} ℃）");
                        }
                        else
                        {
                            byte[] frame = { slaveId, 0x03, 0x01, 0xFA, 0x00, 0x02 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                // 字节序 CDAB
                                byte[] data = ExtractPayload(buffers);
                                // DCBA
                                byte[] dcba = { data[1], data[0], data[3], data[2] };
                                if (!BitConverter.IsLittleEndian)
                                {
                                    ReverseArray(dcba);
                                }
                                sp = BitConverter.ToInt32(data);
                                InfoLog($"获取微型反应釜SP1，结果（{sp / 100.0} ℃）");
                            }
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("SP1",-200,600,sp/100.0,"",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜SP1失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取SP2
            Commands.Add(new DeviceCommand()
            {
                Name = "获取SP2",
                HelpText = "获取微型反应釜SP2",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("SP2",-200,600,0,"",true,"℃")
                    }
                },

                Action = WrapAction("获取SP2", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        int sp = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            sp = new Random().Next(-3, 6) * 100; // 模拟 -200 到 600 度
                            InfoLog($"模拟模式获取微型反应釜SP2，结果（{sp} ℃）");
                        }
                        else
                        {
                            byte[] frame = { slaveId, 0x03, 0x01, 0xFC, 0x00, 0x02 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                // 字节序 CDAB
                                byte[] data = ExtractPayload(buffers);
                                // DCBA
                                byte[] dcba = { data[1], data[0], data[3], data[2] };
                                if (!BitConverter.IsLittleEndian)
                                {
                                    ReverseArray(dcba);
                                }
                                sp = BitConverter.ToInt32(data);
                                InfoLog($"获取微型反应釜SP2，结果（{sp / 100.0} ℃）");
                            }
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("SP2",-200,600,sp/100.0,"",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜SP2失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取SP3
            Commands.Add(new DeviceCommand()
            {
                Name = "获取SP3",
                HelpText = "获取微型反应釜SP3",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("SP3",-200,600,0,"",true,"℃")
                    }
                },

                Action = WrapAction("获取SP3", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        int sp = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            sp = new Random().Next(-3, 6) * 100; // 模拟 -200 到 600 度
                            InfoLog($"模拟模式获取微型反应釜SP3，结果（{sp} ℃）");
                        }
                        else
                        {
                            byte[] frame = { slaveId, 0x03, 0x01, 0xFE, 0x00, 0x02 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                // 字节序 CDAB
                                byte[] data = ExtractPayload(buffers);
                                // DCBA
                                byte[] dcba = { data[1], data[0], data[3], data[2] };
                                if (!BitConverter.IsLittleEndian)
                                {
                                    ReverseArray(dcba);
                                }
                                sp = BitConverter.ToInt32(data);
                                InfoLog($"获取微型反应釜SP3，结果（{sp / 100.0} ℃）");
                            }
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("SP3",-200,600,sp/100.0,"",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜SP3失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取SP4
            Commands.Add(new DeviceCommand()
            {
                Name = "获取SP4",
                HelpText = "获取微型反应釜SP4",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("SP4",-200,600,0,"",true,"℃")
                    }
                },

                Action = WrapAction("获取SP4", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        int sp = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            sp = new Random().Next(-3, 6) * 100; // 模拟 -200 到 600 度
                            InfoLog($"模拟模式获取微型反应釜SP4，结果（{sp} ℃）");
                        }
                        else
                        {
                            byte[] frame = { slaveId, 0x03, 0x02, 0x00, 0x00, 0x02 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                // 字节序 CDAB
                                byte[] data = ExtractPayload(buffers);
                                // DCBA
                                byte[] dcba = { data[1], data[0], data[3], data[2] };
                                if (!BitConverter.IsLittleEndian)
                                {
                                    ReverseArray(dcba);
                                }
                                sp = BitConverter.ToInt32(data);
                                InfoLog($"获取微型反应釜SP4，结果（{sp / 100.0} ℃）");
                            }
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("SP4",-200,600,sp/100.0,"",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜SP4失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取SP5
            Commands.Add(new DeviceCommand()
            {
                Name = "获取SP5",
                HelpText = "获取微型反应釜SP5",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("SP5",-200,600,0,"",true,"℃")
                    }
                },

                Action = WrapAction("获取SP5", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        int sp = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            sp = new Random().Next(-3, 6) * 100; // 模拟 -200 到 600 度
                            InfoLog($"模拟模式获取微型反应釜SP5，结果（{sp} ℃）");
                        }
                        else
                        {
                            byte[] frame = { slaveId, 0x03, 0x02, 0x02, 0x00, 0x02 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                // 字节序 CDAB
                                byte[] data = ExtractPayload(buffers);
                                // DCBA
                                byte[] dcba = { data[1], data[0], data[3], data[2] };
                                if (!BitConverter.IsLittleEndian)
                                {
                                    ReverseArray(dcba);
                                }
                                sp = BitConverter.ToInt32(data);
                                InfoLog($"获取微型反应釜SP5，结果（{sp / 100.0} ℃）");
                            }
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("SP5",-200,600,sp/100.0,"",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜SP5失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取SP6
            Commands.Add(new DeviceCommand()
            {
                Name = "获取SP6",
                HelpText = "获取微型反应釜SP6",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("SP6",-200,600,0,"",true,"℃")
                    }
                },

                Action = WrapAction("获取SP6", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        int sp = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            sp = new Random().Next(-3, 6) * 100; // 模拟 -200 到 600 度
                            InfoLog($"模拟模式获取微型反应釜SP6，结果（{sp} ℃）");
                        }
                        else
                        {
                            byte[] frame = { slaveId, 0x03, 0x02, 0x04, 0x00, 0x02 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                // 字节序 CDAB
                                byte[] data = ExtractPayload(buffers);
                                // DCBA
                                byte[] dcba = { data[1], data[0], data[3], data[2] };
                                if (!BitConverter.IsLittleEndian)
                                {
                                    ReverseArray(dcba);
                                }
                                sp = BitConverter.ToInt32(data);
                                InfoLog($"获取微型反应釜SP6，结果（{sp / 100.0} ℃）");
                            }
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("SP6",-200,600,sp/100.0,"",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜SP6失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取SP7
            Commands.Add(new DeviceCommand()
            {
                Name = "获取SP7",
                HelpText = "获取微型反应釜SP7",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("SP7",-200,600,0,"",true,"℃")
                    }
                },

                Action = WrapAction("获取SP7", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        int sp = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            sp = new Random().Next(-3, 6) * 100; // 模拟 -200 到 600 度
                            InfoLog($"模拟模式获取微型反应釜SP7，结果（{sp} ℃）");
                        }
                        else
                        {
                            byte[] frame = { slaveId, 0x03, 0x02, 0x06, 0x00, 0x02 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                // 字节序 CDAB
                                byte[] data = ExtractPayload(buffers);
                                // DCBA
                                byte[] dcba = { data[1], data[0], data[3], data[2] };
                                if (!BitConverter.IsLittleEndian)
                                {
                                    ReverseArray(dcba);
                                }
                                sp = BitConverter.ToInt32(data);
                                InfoLog($"获取微型反应釜SP7，结果（{sp / 100.0} ℃）");
                            }
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("SP7",-200,600,sp/100.0,"",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜SP7失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取SP8
            Commands.Add(new DeviceCommand()
            {
                Name = "获取SP8",
                HelpText = "获取微型反应釜SP8",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("SP8",-200,600,0,"",true,"℃")
                    }
                },

                Action = WrapAction("获取SP8", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        int sp = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            sp = new Random().Next(-3, 6) * 100; // 模拟 -200 到 600 度
                            InfoLog($"模拟模式获取微型反应釜SP8，结果（{sp} ℃）");
                        }
                        else
                        {
                            byte[] frame = { slaveId, 0x03, 0x02, 0x08, 0x00, 0x02 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                // 字节序 CDAB
                                byte[] data = ExtractPayload(buffers);
                                // DCBA
                                byte[] dcba = { data[1], data[0], data[3], data[2] };
                                if (!BitConverter.IsLittleEndian)
                                {
                                    ReverseArray(dcba);
                                }
                                sp = BitConverter.ToInt32(data);
                                InfoLog($"获取微型反应釜SP8，结果（{sp / 100.0} ℃）");
                            }
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("SP8",-200,600,sp/100.0,"",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取微型反应釜SP8失败：{e.Message}");
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
                            byte[] data = BitConverter.GetBytes((int)(target * 100));
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }

                            // 创建发送的指令
                            byte[] frame = { slaveId, 0x10, 0x01, 0xFA, 0x00, 0x02, 0x04, data[2], data[3], data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                            byte[] data = BitConverter.GetBytes((int)(target * 100));
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }

                            // 创建发送的指令
                            byte[] frame = { slaveId, 0x10, 0x01, 0xFC, 0x00, 0x02, 0x04, data[2], data[3], data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                            byte[] data = BitConverter.GetBytes((int)(target * 100));
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }

                            // 创建发送的指令
                            byte[] frame = { slaveId, 0x10, 0x01, 0xFE, 0x00, 0x02, 0x04, data[2], data[3], data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                            byte[] data = BitConverter.GetBytes((int)(target * 100));
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }

                            // 创建发送的指令
                            byte[] frame = { slaveId, 0x10, 0x02, 0x00, 0x00, 0x02, 0x04, data[2], data[3], data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                            byte[] data = BitConverter.GetBytes((int)(target * 100));
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }

                            // 创建发送的指令
                            byte[] frame = { slaveId, 0x10, 0x02, 0x02, 0x00, 0x02, 0x04, data[2], data[3], data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                            byte[] data = BitConverter.GetBytes((int)(target * 100));
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }

                            // 创建发送的指令
                            byte[] frame = { slaveId, 0x10, 0x02, 0x04, 0x00, 0x02, 0x04, data[2], data[3], data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                            byte[] data = BitConverter.GetBytes((int)(target * 100));
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }

                            // 创建发送的指令
                            byte[] frame = { slaveId, 0x10, 0x02, 0x06, 0x00, 0x02, 0x04, data[2], data[3], data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                            byte[] data = BitConverter.GetBytes((int)(target * 100));
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }

                            // 创建发送的指令
                            byte[] frame = { slaveId, 0x10, 0x02, 0x08, 0x00, 0x02, 0x04, data[2], data[3], data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                            byte[] frame = { slaveId, 0x03, 0x02, 0x4A, 0x00, 0x08 };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

                            byte[] buffers = new byte[1024];
                            Array.Copy(frame, buffers, frame.Length);
                            int len = await ReadAsync(buffers, 0, frame.Length, CancellationToken.None);
                            if (len > 0)
                            {
                                byte[] data = ExtractPayload(buffers);
                                st1 = (short)(data[0] << 8 | data[1]);
                                st2 = (short)(data[2] << 8 | data[3]);
                                st3 = (short)(data[4] << 8 | data[5]);
                                st4 = (short)(data[6] << 8 | data[7]);
                                st5 = (short)(data[8] << 8 | data[9]);
                                st6 = (short)(data[10] << 8 | data[11]);
                                st7 = (short)(data[12] << 8 | data[13]);
                                st8 = (short)(data[14] << 8 | data[15]);

                                InfoLog($"获取微型反应釜时间设定值，结果（{st1},{st2},{st3},{st4},{st5},{st6},{st7},{st8} MIN）");
                            }
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
                            byte[] data = BitConverter.GetBytes((short)(st * 10));
                            if (BitConverter.IsLittleEndian)
                            {
                                ReverseArray(data);
                            }
                            // 创建指令
                            byte[] frame = { slaveId, 0x06, 0x02, 0x51, data[0], data[1] };
                            frame = Crc16Modbus.AppendCrc(frame, 0, frame.Length);

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
                _logger.LogInformation($"[ModbusRTU微型反应釜-开始] Device={Name} Command={commandName}");

                var sw = Stopwatch.StartNew();
                try
                {
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();

                    _logger.LogInformation($"[ModbusRTU微型反应釜-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[ModbusRTU微型反应釜-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
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
