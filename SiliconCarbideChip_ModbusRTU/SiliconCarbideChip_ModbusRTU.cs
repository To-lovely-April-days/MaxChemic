using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO.Ports;
using System.Windows.Input;

namespace SiliconCarbideChip_ModbusRTU
{
    [Export(typeof(IDevice))]
    public class SiliconCarbideChip_ModbusRTU : Device, IFlowLifecycleAware
    {
        private readonly ILogService _logger;

        // 通信层重试参数
        private const int MaxRetries = 3;       // 总共尝试 3 次
        private const int RetryDelayMs = 100;   // 重试间隔 100ms

        // 实例级 TCP 互斥锁:同一设备实例的所有 IO 串行化,避免与轮询/心跳撞车
        // 用静态字典按 DeviceId 隔离不同设备实例
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _instanceLocks = new ConcurrentDictionary<string, SemaphoreSlim>();


        public SiliconCarbideChip_ModbusRTU()
        {
            _logger = LogManager.GetLogger<SiliconCarbideChip_ModbusRTU>();

            DeviceId = "SCC0001";
            Name = "进料系统集成碳化硅芯片";
            Manufacturer = "ModbusRTU 进料系统集成碳化硅芯片";
            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/SiliconCarbideChip_CY.png";
            Category = DeviceCategories.PublicWorks;
            ComId = "SCC0001"; // 默认设备ID
            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();
            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            Parameters.Variables.Add(new StringParameter("DeviceId", "SCC0001", "ModbusRTU设备ID")
            {
                Options = new ObservableCollection<string>() { "SCC0001", "SCC0002", "SCC0003", "SCC0004", "SCC0005" },
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
                Options = new ObservableCollection<string>() { "Direct", "PLC", "ModbusTcp", "ZLanGateway" }
            });

            // Modbus 从机站号 (1~254)
            Parameters.Variables.Add(new NumberParameter("Modbus站号", 1, 254, 1, "Modbus 从机站号 (1~254)"));

            InitializeCommands();
        }


        /// <summary>
        /// 声明本驱动支持通过 ZLAN 网关通信。
        /// </summary>
        public override bool SupportsZLanGateway => true;


        private void InitializeCommands()
        {
            // 获取温度 T1
            Commands.Add(new DeviceCommand()
            {
                Name = "获取 T1",
                HelpText = "获取进料系统集成碳化硅芯片温度 T1",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("T1",0,1000,0,"温度 T1",true,"℃")
                    }
                },

                AsyncAction = AsyncWrapAction("获取 T1", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        float temperature = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            temperature = new Random().Next(0, 100);
                            InfoLog($"模拟模式获取进料系统集成碳化硅芯片温度 T1，结果（{temperature} ℃）");
                        }
                        else
                        {
                            byte[] buffers = BuildReadCmd(0, 2);
                            temperature = await SendAndParseInt32Async(buffers, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("T1",0,1000,temperature,"温度 T1",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取进料系统集成碳化硅芯片温度 T1 失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取温度T2
            Commands.Add(new DeviceCommand()
            {
                Name = "获取 T2",
                HelpText = "获取进料系统集成碳化硅芯片温度 T2",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("T2",0,1000,0,"温度 T2",true,"℃")
                    }
                },

                Action = WrapAction("获取 T2", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        float temperature = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            temperature = new Random().Next(0, 100);
                            InfoLog($"模拟模式获取进料系统集成碳化硅芯片温度 T2，结果（{temperature} ℃）");
                        }
                        else
                        {
                            byte[] buffers = BuildReadCmd(2, 2);
                            temperature = await SendAndParseInt32Async(buffers, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("T2",0,1000,temperature,"温度 T2",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取进料系统集成碳化硅芯片温度 T2 失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取温度T3
            Commands.Add(new DeviceCommand()
            {
                Name = "获取 T3",
                HelpText = "获取进料系统集成碳化硅芯片温度 T3",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("T3",0,1000,0,"温度 T3",true,"℃")
                    }
                },

                Action = WrapAction("获取 T3", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        float temperature = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            temperature = new Random().Next(0, 100);
                            InfoLog($"模拟模式获取进料系统集成碳化硅芯片温度 T3，结果（{temperature} ℃）");
                        }
                        else
                        {
                            byte[] frame = { slaveId, 0x03, 0x00, 0x04, 0x00, 0x02 };
                            byte[] buffers = BuildReadCmd(4, 2);
                            temperature = await SendAndParseInt32Async(buffers, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("T3",0,1000,temperature,"温度 T3",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取进料系统集成碳化硅芯片温度 T3 失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取温度T4
            Commands.Add(new DeviceCommand()
            {
                Name = "获取 T4",
                HelpText = "获取进料系统集成碳化硅芯片温度 T4",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("T4",0,1000,0,"温度 T4",true,"℃")
                    }
                },

                Action = WrapAction("获取 T4", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        float temperature = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            temperature = new Random().Next(0, 100);
                            InfoLog($"模拟模式获取进料系统集成碳化硅芯片温度 T4，结果（{temperature} ℃）");
                        }
                        else
                        {
                            byte[] frame = { slaveId, 0x03, 0x00, 0x06, 0x00, 0x02 };
                            byte[] buffers = BuildReadCmd(6, 2);
                            temperature = await SendAndParseInt32Async(buffers, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("T4",0,1000,temperature,"温度 T4",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取进料系统集成碳化硅芯片温度 T4 失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取温度T5
            Commands.Add(new DeviceCommand()
            {
                Name = "获取 T5",
                HelpText = "获取进料系统集成碳化硅芯片温度 T5",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("T5",0,1000,0,"温度 T5",true,"℃")
                    }
                },

                Action = WrapAction("获取 T5", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        float temperature = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            temperature = new Random().Next(0, 100);
                            InfoLog($"模拟模式获取进料系统集成碳化硅芯片温度 T5，结果（{temperature} ℃）");
                        }
                        else
                        {
                            byte[] buffers = BuildReadCmd(8, 2);
                            temperature = await SendAndParseInt32Async(buffers, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("T5",0,1000,temperature,"温度 T5",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取进料系统集成碳化硅芯片温度 T5 失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取温度T6
            Commands.Add(new DeviceCommand()
            {
                Name = "获取 T6",
                HelpText = "获取进料系统集成碳化硅芯片温度 T6",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("T6",0,1000,0,"温度 T6",true,"℃")
                    }
                },

                Action = WrapAction("获取 T6", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        float temperature = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            temperature = new Random().Next(0, 100);
                            InfoLog($"模拟模式获取进料系统集成碳化硅芯片温度 T6，结果（{temperature} ℃）");
                        }
                        else
                        {
                            byte[] buffers = BuildReadCmd(0x0A, 2);
                            temperature = await SendAndParseInt32Async(buffers, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("T6",0,1000,temperature,"温度 T6",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取进料系统集成碳化硅芯片温度 T6 失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取背压阀压力P
            Commands.Add(new DeviceCommand()
            {
                Name = "获取背压阀压力P",
                HelpText = "获取进料系统集成碳化硅芯片背压阀压力P",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("Pressure",0,1000,0,"背压阀压力P",true,"Bar")
                    }
                },

                Action = WrapAction("获取背压阀压力P", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        float pressure = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            pressure = new Random().Next(0, 100);
                            InfoLog($"模拟模式获取进料系统集成碳化硅芯片背压阀压力P，结果（{pressure} Bar）");
                        }
                        else
                        {
                            byte[] buffers = BuildReadCmd(0x0C, 2);
                            pressure = await SendAndParseInt32Async(buffers, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("Pressure",0,1000,pressure,"背压阀压力P",true,"Bar")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取进料系统集成碳化硅芯片背压阀压力P失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取背压阀模式
            Commands.Add(new DeviceCommand()
            {
                Name = "获取背压阀模式",
                HelpText = "获取进料系统集成碳化硅芯片背压阀模式",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("Mode",0,1000,0,"背压阀模式",true)
                    }
                },

                Action = WrapAction("获取背压阀模式", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        int mode = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            mode = new Random().Next(0, 100);
                            InfoLog($"模拟模式获取进料系统集成碳化硅芯片背压阀模式，结果（{mode}）");
                        }
                        else
                        {
                            byte[] buffers = BuildReadCmd(0x0E, 1);
                            mode = await SendAndParseInt16Async(buffers, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("Mode",0,1000,mode,"背压阀模式",true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取进料系统集成碳化硅芯片背压阀模式失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取背压阀SV%
            Commands.Add(new DeviceCommand()
            {
                Name = "获取背压阀手动SV",
                HelpText = "获取进料系统集成碳化硅芯片背压阀手动SV",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("Manual SV",0,1000,0,"背压阀手动SV",true)
                    }
                },

                Action = WrapAction("获取背压阀手动SV", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        int mode = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            mode = new Random().Next(0, 100);
                            InfoLog($"模拟模式获取进料系统集成碳化硅芯片背压阀手动SV，结果（{mode}）");
                        }
                        else
                        {
                            byte[] buffers = BuildReadCmd(0x0F, 1);
                            mode = await SendAndParseInt16Async(buffers, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("Manual SV",0,1000,mode,"背压阀手动SV",true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取进料系统集成碳化硅芯片背压阀手动SV失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取背压阀P SV
            Commands.Add(new DeviceCommand()
            {
                Name = "获取背压阀自动SV",
                HelpText = "获取进料系统集成碳化硅芯片背压阀自动SV",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("Auto SV",0,1000,0,"背压阀自动SV",true)
                    }
                },

                Action = WrapAction("获取背压阀自动SV", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        int mode = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            mode = new Random().Next(0, 100);
                            InfoLog($"模拟模式获取进料系统集成碳化硅芯片背压阀自动SV，结果（{mode}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x00, 0x10, 0x00, 0x01 };
                            byte[] buffers = BuildReadCmd(16, 1);
                            mode = await SendAndParseInt16Async(buffers, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("Auto SV",0,1000,mode,"背压阀自动SV",true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取进料系统集成碳化硅芯片背压阀自动SV失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取背压阀 ON OFF
            Commands.Add(new DeviceCommand()
            {
                Name = "获取背压阀ON/OFF",
                HelpText = "获取进料系统集成碳化硅芯片背压阀ON/OFF",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("ON/OFF",0,1000,0,"背压阀ON/OFF",true)
                    }
                },

                Action = WrapAction("获取背压阀ON/OFF", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        int mode = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            mode = new Random().Next(0, 100);
                            InfoLog($"模拟模式获取进料系统集成碳化硅芯片背压阀ON/OFF，结果（{mode}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x00, 0x11, 0x00, 0x01 };
                            byte[] buffers = BuildReadCmd(17, 1);
                            mode = await SendAndParseInt16Async(buffers, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("ON/OFF",0,1000,mode,"背压阀ON/OFF",true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取进料系统集成碳化硅芯片背压阀ON/OFF失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取温度T1上限报警
            Commands.Add(new DeviceCommand()
            {
                Name = "获取温度T1上限报警",
                HelpText = "获取进料系统集成碳化硅芯片温度T1上限报警",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("T1 alarm",false,"温度T1上限报警",true)
                    }
                },

                Action = WrapAction("获取温度T1上限报警", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        int mode = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            mode = new Random().Next(-1, 1);
                            InfoLog($"模拟模式获取进料系统集成碳化硅芯片温度T1上限报警，结果（{mode == 1}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x00, 0x12, 0x00, 0x01 };
                            byte[] buffers = BuildReadCmd(18, 1);
                            mode = await SendAndParseInt16Async(buffers, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("T1 alarm", mode == 1, "温度T1上限报警", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取进料系统集成碳化硅芯片温度T1上限报警失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取温度T2上限报警
            Commands.Add(new DeviceCommand()
            {
                Name = "获取温度T2上限报警",
                HelpText = "获取进料系统集成碳化硅芯片温度T2上限报警",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("T2 alarm",false,"温度T2上限报警",true)
                    }
                },

                Action = WrapAction("获取温度T2上限报警", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        int mode = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            mode = new Random().Next(-1, 1);
                            InfoLog($"模拟模式获取进料系统集成碳化硅芯片温度T2上限报警，结果（{mode == 1}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x00, 0x13, 0x00, 0x01 };
                            byte[] buffers = BuildReadCmd(19, 1);
                            mode = await SendAndParseInt16Async(buffers, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("T2 alarm", mode == 1, "温度T2上限报警", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取进料系统集成碳化硅芯片温度T2上限报警失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取温度T3上限报警
            Commands.Add(new DeviceCommand()
            {
                Name = "获取温度T3上限报警",
                HelpText = "获取进料系统集成碳化硅芯片温度T3上限报警",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("T3 alarm",false,"温度T3上限报警",true)
                    }
                },

                Action = WrapAction("获取温度T3上限报警", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        int mode = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            mode = new Random().Next(-1, 1);
                            InfoLog($"模拟模式获取进料系统集成碳化硅芯片温度T3上限报警，结果（{mode == 1}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x00, 0x14, 0x00, 0x01 };
                            byte[] buffers = BuildReadCmd(20, 1);
                            mode = await SendAndParseInt16Async(buffers, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("T3 alarm", mode == 1, "温度T3上限报警", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取进料系统集成碳化硅芯片温度T3上限报警失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取温度T4上限报警
            Commands.Add(new DeviceCommand()
            {
                Name = "获取温度T4上限报警",
                HelpText = "获取进料系统集成碳化硅芯片温度T4上限报警",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("T4 alarm",false,"温度T4上限报警",true)
                    }
                },

                Action = WrapAction("获取温度T4上限报警", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        int mode = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            mode = new Random().Next(-1, 1);
                            InfoLog($"模拟模式获取进料系统集成碳化硅芯片温度T4上限报警，结果（{mode == 1}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x00, 0x15, 0x00, 0x01 };
                            byte[] buffers = BuildReadCmd(21, 1);
                            mode = await SendAndParseInt16Async(buffers, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("T4 alarm", mode == 1, "温度T4上限报警", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取进料系统集成碳化硅芯片温度T4上限报警失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取温度T5上限报警
            Commands.Add(new DeviceCommand()
            {
                Name = "获取温度T5上限报警",
                HelpText = "获取进料系统集成碳化硅芯片温度T5上限报警",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("T5 alarm",false,"温度T5上限报警",true)
                    }
                },

                Action = WrapAction("获取温度T5上限报警", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        int mode = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            mode = new Random().Next(-1, 1);
                            InfoLog($"模拟模式获取进料系统集成碳化硅芯片温度T5上限报警，结果（{mode == 1}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x00, 0x16, 0x00, 0x01 };
                            byte[] buffers = BuildReadCmd(22, 1);
                            mode = await SendAndParseInt16Async(buffers, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("T5 alarm", mode == 1, "温度T5上限报警", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取进料系统集成碳化硅芯片温度T5上限报警失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取温度T6上限报警
            Commands.Add(new DeviceCommand()
            {
                Name = "获取温度T6上限报警",
                HelpText = "获取进料系统集成碳化硅芯片温度T6上限报警",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("T6 alarm",false,"温度T6上限报警",true)
                    }
                },

                Action = WrapAction("获取温度T6上限报警", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        int mode = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            mode = new Random().Next(-1, 1);
                            InfoLog($"模拟模式获取进料系统集成碳化硅芯片温度T6上限报警，结果（{mode == 1}）");
                        }
                        else
                        {
                            byte[] frame = { slaveId, 0x03, 0x00, 0x17, 0x00, 0x01 };
                            byte[] buffers = BuildReadCmd(23, 1);
                            mode = await SendAndParseInt16Async(buffers, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("T6 alarm", mode == 1, "温度T6上限报警", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取进料系统集成碳化硅芯片温度T6上限报警失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取压力上限报警
            Commands.Add(new DeviceCommand()
            {
                Name = "获取压力上限报警",
                HelpText = "获取进料系统集成碳化硅芯片压力上限报警",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("PressureUpperLimitAlarm",false,"压力上限报警",true)
                    }
                },

                Action = WrapAction("获取压力上限报警", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        int mode = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            mode = new Random().Next(-1, 1);
                            InfoLog($"模拟模式获取进料系统集成碳化硅芯片压力上限报警，结果（{mode == 1}）");
                        }
                        else
                        {
                            byte[] frame = { slaveId, 0x03, 0x00, 0x18, 0x00, 0x01 };
                            byte[] buffers = BuildReadCmd(24, 1);
                            mode = await SendAndParseInt16Async(buffers, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("PressureUpperLimitAlarm", mode == 1, "压力上限报警", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取进料系统集成碳化硅芯片压力上限报警失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取温度检测通讯故障
            Commands.Add(new DeviceCommand()
            {
                Name = "获取温度检测通讯故障",
                HelpText = "获取进料系统集成碳化硅芯片温度检测通讯故障",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("TemperatureCheckAlarm",false,"温度检测通讯故障",true)
                    }
                },

                Action = WrapAction("获取温度检测通讯故障", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        int mode = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            mode = new Random().Next(-1, 1);
                            InfoLog($"模拟模式获取进料系统集成碳化硅芯片温度检测通讯故障，结果（{mode == 1}）");
                        }
                        else
                        {
                            byte[] frame = { slaveId, 0x03, 0x00, 0x19, 0x00, 0x01 };
                            byte[] buffers = BuildReadCmd(25, 1);
                            mode = await SendAndParseInt16Async(buffers, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("TemperatureCheckAlarm", mode == 1, "温度检测通讯故障", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取进料系统集成碳化硅芯片温度检测通讯故障失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取压力检测通讯故障
            Commands.Add(new DeviceCommand()
            {
                Name = "获取压力检测通讯故障",
                HelpText = "获取进料系统集成碳化硅芯片压力检测通讯故障",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("PressureCheckAlarm",false,"压力检测通讯故障",true)
                    }
                },

                Action = WrapAction("获取压力检测通讯故障", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        int mode = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            mode = new Random().Next(-1, 1);
                            InfoLog($"模拟模式获取进料系统集成碳化硅芯片压力检测通讯故障，结果（{mode == 1}）");
                        }
                        else
                        {
                            byte[] frame = { slaveId, 0x03, 0x00, 0x1A, 0x00, 0x01 };
                            byte[] buffers = BuildReadCmd(26, 1);
                            mode = await SendAndParseInt16Async(buffers, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("PressureCheckAlarm", mode == 1, "压力检测通讯故障", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取进料系统集成碳化硅芯片压力检测通讯故障失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取报警复位结果
            Commands.Add(new DeviceCommand()
            {
                Name = "获取报警复位结果",
                HelpText = "获取进料系统集成碳化硅芯片报警复位结果",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("AlarmReset",false,"报警复位结果",true)
                    }
                },

                Action = WrapAction("获取报警复位结果", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        int mode = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            mode = new Random().Next(-1, 1);
                            InfoLog($"模拟模式获取进料系统集成碳化硅芯片报警复位结果，结果（{mode == 1}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x00, 0x1B, 0x00, 0x01 };
                            byte[] buffers = BuildReadCmd(27, 1);
                            mode = await SendAndParseInt16Async(buffers, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("AlarmReset", mode == 1, "报警复位结果", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取进料系统集成碳化硅芯片报警复位结果失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 报警消音
            Commands.Add(new DeviceCommand()
            {
                Name = "获取报警消音结果",
                HelpText = "获取进料系统集成碳化硅芯片报警消音结果",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("AlarmSilence",false,"报警消音结果",true)
                    }
                },

                Action = WrapAction("获取报警消音结果", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        int mode = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            mode = new Random().Next(-1, 1);
                            InfoLog($"模拟模式获取进料系统集成碳化硅芯片报警消音结果，结果（{mode == 1}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x00, 0x1C, 0x00, 0x01 };
                            byte[] buffers = BuildReadCmd(28, 1);
                            mode = await SendAndParseInt16Async(buffers, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("AlarmSilence", mode == 1, "报警消音结果", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取进料系统集成碳化硅芯片报警消音结果失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 报警状态
            Commands.Add(new DeviceCommand()
            {
                Name = "获取报警状态",
                HelpText = "获取进料系统集成碳化硅芯片报警状态",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("AlarmStatus",false,"报警状态",true)
                    }
                },

                Action = WrapAction("获取报警状态", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = parameter.GetValue<byte>("SlaveId");
                        int mode = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            mode = new Random().Next(-1, 1);
                            InfoLog($"模拟模式获取进料系统集成碳化硅芯片报警状态，结果（{mode == 1}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x03, 0x00, 0x1D, 0x00, 0x01 };
                            byte[] buffers = BuildReadCmd(29, 1);
                            mode = await SendAndParseInt16Async(buffers, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("AlarmStatus", mode == 1, "报警状态", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取进料系统集成碳化硅芯片报警状态失败：{e.Message}");
                        throw;
                    }
                })
            });


            // 设置 T1
            Commands.Add(new DeviceCommand()
            {
                Name = "设置 T1",
                HelpText = "设置进料系统集成碳化硅芯片温度T1",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", 0, 1000, 0, "T1 目标值", true, "℃")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue", 0, 1000, 0, "设置的 T1 值", true, "℃")
                    }
                },

                Action = WrapAction("设置 T1", async parameter =>
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
                            InfoLog($"模拟模式设置进料继承系统碳化硅芯片T1值，结果（成功：{success}，目标值：{target}）");
                        }
                        else
                        {
                            byte[] buffers = BuildMultWriteCmd(0, (int)target);
                            //byte[] frame = { slaveId, 0x10, 0x00, 0x00, 0x00, 0x02, 0x04, data[0], data[1], data[2], data[3] };
                            success = await WriteAsync(buffers, 0, buffers.Length, CancellationToken.None);
                            InfoLog($"设置进料系统继承碳化硅芯片T1值成功：{success}，目标值：{target}");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetTargetValue", 0, 1000, target, "设置的 T1 值", true, "℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置进料集成系统碳化硅芯片T1值失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置 T2
            Commands.Add(new DeviceCommand()
            {
                Name = "设置 T2",
                HelpText = "设置进料系统集成碳化硅芯片温度T2",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", 0, 1000, 0, "T2 目标值", true, "℃")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue", 0, 1000, 0, "设置的 T2 值", true, "℃")
                    }
                },

                Action = WrapAction("设置 T2", async parameter =>
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
                            InfoLog($"模拟模式设置进料继承系统碳化硅芯片T2值，结果（成功：{success}，目标值：{target}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x10, 0x00, 0x02, 0x00, 0x02, 0x04, data[0], data[1], data[2], data[3] };
                            byte[] frame = BuildMultWriteCmd(2, (int)target);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置进料系统继承碳化硅芯片T2值成功：{success}，目标值：{target}");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetTargetValue", 0, 1000, target, "设置的 T2 值", true, "℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置进料集成系统碳化硅芯片T2值失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置 T3
            Commands.Add(new DeviceCommand()
            {
                Name = "设置 T3",
                HelpText = "设置进料系统集成碳化硅芯片温度T3",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", 0, 1000, 0, "T3 目标值", true, "℃")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue", 0, 1000, 0, "设置的 T3 值", true, "℃")
                    }
                },

                Action = WrapAction("设置 T3", async parameter =>
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
                            InfoLog($"模拟模式设置进料继承系统碳化硅芯片T3值，结果（成功：{success}，目标值：{target}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x10, 0x00, 0x04, 0x00, 0x02, 0x04, data[0], data[1], data[2], data[3] };
                            byte[] frame = BuildMultWriteCmd(4, (int)target);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置进料系统继承碳化硅芯片T3值成功：{success}，目标值：{target}");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetTargetValue", 0, 1000, target, "设置的 T3 值", true, "℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置进料集成系统碳化硅芯片T3值失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置 T4
            Commands.Add(new DeviceCommand()
            {
                Name = "设置 T4",
                HelpText = "设置进料系统集成碳化硅芯片温度T4",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", 0, 1000, 0, "T4 目标值", true, "℃")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue", 0, 1000, 0, "设置的 T4 值", true, "℃")
                    }
                },

                Action = WrapAction("设置 T4", async parameter =>
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
                            InfoLog($"模拟模式设置进料继承系统碳化硅芯片T4值，结果（成功：{success}，目标值：{target}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x10, 0x00, 0x06, 0x00, 0x02, 0x04, data[0], data[1], data[2], data[3] };
                            byte[] frame = BuildMultWriteCmd(6, (int)target);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置进料系统继承碳化硅芯片T4值成功：{success}，目标值：{target}");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetTargetValue", 0, 1000, target, "设置的 T4 值", true, "℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置进料集成系统碳化硅芯片T4值失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置 T5
            Commands.Add(new DeviceCommand()
            {
                Name = "设置 T5",
                HelpText = "设置进料系统集成碳化硅芯片温度T5",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", 0, 1000, 0, "T5 目标值", true, "℃")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue", 0, 1000, 0, "设置的 T5 值", true, "℃")
                    }
                },

                Action = WrapAction("设置 T5", async parameter =>
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
                            InfoLog($"模拟模式设置进料继承系统碳化硅芯片T5值，结果（成功：{success}，目标值：{target}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x10, 0x00, 0x08, 0x00, 0x02, 0x04, data[0], data[1], data[2], data[3] };
                            byte[] frame = BuildMultWriteCmd(8, (int)target);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置进料系统继承碳化硅芯片T5值成功：{success}，目标值：{target}");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetTargetValue", 0, 1000, target, "设置的 T5 值", true, "℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置进料集成系统碳化硅芯片T5值失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置 T6
            Commands.Add(new DeviceCommand()
            {
                Name = "设置 T6",
                HelpText = "设置进料系统集成碳化硅芯片温度T6",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", 0, 1000, 0, "T6 目标值", true, "℃")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue", 0, 1000, 0, "设置的 T6 值", true, "℃")
                    }
                },

                Action = WrapAction("设置 T6", async parameter =>
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
                            InfoLog($"模拟模式设置进料继承系统碳化硅芯片T6值，结果（成功：{success}，目标值：{target}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x10, 0x00, 0x0A, 0x00, 0x02, 0x04, data[0], data[1], data[2], data[3] };
                            byte[] frame = BuildMultWriteCmd(10, (int)target);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置进料系统继承碳化硅芯片T6值成功：{success}，目标值：{target}");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetTargetValue", 0, 1000, target, "设置的 T6 值", true, "℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置进料集成系统碳化硅芯片T6值失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置背压阀压力P
            Commands.Add(new DeviceCommand()
            {
                Name = "设置背压阀压力P",
                HelpText = "设置进料系统集成碳化硅芯片背压阀压力P",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", 0, 1000, 0, "背压阀压力P目标值", true, "Bar")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue", 0, 1000, 0, "设置的背压阀压力P值", true, "Bar")
                    }
                },

                Action = WrapAction("设置背压阀压力P", async parameter =>
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
                            InfoLog($"模拟模式设置进料继承系统碳化硅芯片背压阀压力P值，结果（成功：{success}，目标值：{target}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x10, 0x00, 0x0C, 0x00, 0x02, 0x04, data[0], data[1], data[2], data[3] };
                            byte[] frame = BuildMultWriteCmd(12, (int)target);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置进料系统继承碳化硅芯片背压阀压力P值成功：{success}，目标值：{target}");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetTargetValue", 0, 1000, target, "设置的背压阀压力P值", true, "Bar")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置进料集成系统碳化硅芯片背压阀压力P值失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置背压阀模式
            Commands.Add(new DeviceCommand()
            {
                Name = "设置背压阀模式",
                HelpText = "设置进料系统集成碳化硅芯片背压阀模式",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", 0, 1000, 0, "背压阀模式目标值", true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue", 0, 1000, 0, "设置的背压阀模式", true)
                    }
                },

                Action = WrapAction("设置背压阀模式", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        short target = parameter.GetValue<short>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置进料继承系统碳化硅芯片背压阀模式，结果（成功：{success}，目标值：{target}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x06, 0x00, 0x0E, data[0], data[1] };
                            byte[] frame = BuildWriteCmd(14, (ushort)target);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置进料系统继承碳化硅芯片背压阀模式成功：{success}，目标值：{target}");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetTargetValue", 0, 1000, target, "设置的背压阀模式", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置进料集成系统碳化硅芯片背压阀模式失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置背压阀SV%
            Commands.Add(new DeviceCommand()
            {
                Name = "设置背压阀手动SV",
                HelpText = "设置进料系统集成碳化硅芯片背压阀手动SV",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", 0, 1000, 0, "背压阀手动SV目标值", true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue", 0, 1000, 0, "设置的背压阀手动SV", true)
                    }
                },

                Action = WrapAction("设置背压阀手动SV", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        short target = parameter.GetValue<short>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置进料继承系统碳化硅芯片背压阀手动SV，结果（成功：{success}，目标值：{target}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x06, 0x00, 0x0F, data[0], data[1] };
                            byte[] frame = BuildWriteCmd(15, (ushort)target);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置进料系统继承碳化硅芯片背压阀手动SV成功：{success}，目标值：{target}");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetTargetValue", 0, 1000, target, "设置的背压阀手动SV", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置进料集成系统碳化硅芯片背压阀手动SV失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置背压阀P SV
            Commands.Add(new DeviceCommand()
            {
                Name = "设置背压阀自动SV",
                HelpText = "设置进料系统集成碳化硅芯片背压阀自动SV",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", 0, 1000, 0, "背压阀自动SV目标值", true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue", 0, 1000, 0, "设置的背压阀自动SV", true)
                    }
                },

                Action = WrapAction("设置背压阀自动SV", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        short target = parameter.GetValue<short>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置进料继承系统碳化硅芯片背压阀自动SV，结果（成功：{success}，目标值：{target}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x06, 0x00, 0x10, data[0], data[1] };
                            byte[] frame = BuildWriteCmd(16, (ushort)target);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置进料系统继承碳化硅芯片背压阀自动SV成功：{success}，目标值：{target}");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetTargetValue", 0, 1000, target, "设置的背压阀自动SV", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置进料集成系统碳化硅芯片背压阀自动SV失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置背压阀 ON/OFF
            Commands.Add(new DeviceCommand()
            {
                Name = "设置背压阀ON/OFF",
                HelpText = "设置进料系统集成碳化硅芯片背压阀ON/OFF",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", 0, 1000, 0, "背压阀ON/OFF目标值", true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue", 0, 1000, 0, "设置的背压阀ON/OFF", true)
                    }
                },

                Action = WrapAction("设置背压阀ON/OFF", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        short target = parameter.GetValue<short>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置进料继承系统碳化硅芯片背压阀ON/OFF，结果（成功：{success}，目标值：{target}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x06, 0x00, 0x11, data[0], data[1] };
                            byte[] frame = BuildWriteCmd(17, (ushort)target);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置进料系统继承碳化硅芯片背压阀ON/OFF成功：{success}，目标值：{target}");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetTargetValue", 0, 1000, target, "设置的背压阀ON/OFF", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置进料集成系统碳化硅芯片背压阀ON/OFF失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置T1上限报警
            Commands.Add(new DeviceCommand()
            {
                Name = "设置T1上限报警",
                HelpText = "设置进料系统集成碳化硅芯片T1上限报警",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", 0, 1000, 0, "T1上限报警目标值", true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue", 0, 1000, 0, "设置T1上限报警", true)
                    }
                },

                Action = WrapAction("设置T1上限报警", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        short target = parameter.GetValue<short>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置进料继承系统碳化硅芯片T1上限报警，结果（成功：{success}，目标值：{target}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x06, 0x00, 0x12, data[0], data[1] };
                            byte[] frame = BuildWriteCmd(18, (ushort)target);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置进料系统继承碳化硅芯片T1上限报警成功：{success}，目标值：{target}");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetTargetValue", 0, 1000, target, "设置T1上限报警", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置进料集成系统碳化硅芯片T1上限报警失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置T2上限报警
            Commands.Add(new DeviceCommand()
            {
                Name = "设置T2上限报警",
                HelpText = "设置进料系统集成碳化硅芯片T2上限报警",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", 0, 1000, 0, "T2上限报警目标值", true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue", 0, 1000, 0, "设置T2上限报警", true)
                    }
                },

                Action = WrapAction("设置T2上限报警", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        short target = parameter.GetValue<short>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置进料继承系统碳化硅芯片T2上限报警，结果（成功：{success}，目标值：{target}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x06, 0x00, 0x13, data[0], data[1] };
                            byte[] frame = BuildWriteCmd(19, (ushort)target);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置进料系统继承碳化硅芯片T2上限报警成功：{success}，目标值：{target}");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetTargetValue", 0, 1000, target, "设置T2上限报警", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置进料集成系统碳化硅芯片T2上限报警失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置T3上限报警
            Commands.Add(new DeviceCommand()
            {
                Name = "设置T3上限报警",
                HelpText = "设置进料系统集成碳化硅芯片T3上限报警",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", 0, 1000, 0, "T3上限报警目标值", true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue", 0, 1000, 0, "设置T3上限报警", true)
                    }
                },

                Action = WrapAction("设置T3上限报警", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        short target = parameter.GetValue<short>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置进料继承系统碳化硅芯片T3上限报警，结果（成功：{success}，目标值：{target}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x06, 0x00, 0x14, data[0], data[1] };
                            byte[] frame = BuildWriteCmd(20, (ushort)target);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置进料系统继承碳化硅芯片T3上限报警成功：{success}，目标值：{target}");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetTargetValue", 0, 1000, target, "设置T3上限报警", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置进料集成系统碳化硅芯片T3上限报警失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置T4上限报警
            Commands.Add(new DeviceCommand()
            {
                Name = "设置T4上限报警",
                HelpText = "设置进料系统集成碳化硅芯片T4上限报警",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", 0, 1000, 0, "T4上限报警目标值", true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue", 0, 1000, 0, "设置T4上限报警", true)
                    }
                },

                Action = WrapAction("设置T4上限报警", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        short target = parameter.GetValue<short>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置进料继承系统碳化硅芯片T4上限报警，结果（成功：{success}，目标值：{target}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x06, 0x00, 0x15, data[0], data[1] };
                            byte[] frame = BuildWriteCmd(21, (ushort)target);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置进料系统继承碳化硅芯片T4上限报警成功：{success}，目标值：{target}");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetTargetValue", 0, 1000, target, "设置T4上限报警", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置进料集成系统碳化硅芯片T4上限报警失败：{e.Message}");
                        throw;
                    }
                })
            });

            //设置T5上限报警
            Commands.Add(new DeviceCommand()
            {
                Name = "设置T5上限报警",
                HelpText = "设置进料系统集成碳化硅芯片T5上限报警",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", 0, 1000, 0, "T5上限报警目标值", true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue", 0, 1000, 0, "设置T5上限报警", true)
                    }
                },

                Action = WrapAction("设置T5上限报警", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        short target = parameter.GetValue<short>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置进料继承系统碳化硅芯片T5上限报警，结果（成功：{success}，目标值：{target}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x06, 0x00, 0x16, data[0], data[1] };
                            byte[] frame = BuildWriteCmd(22, (ushort)target);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置进料系统继承碳化硅芯片T5上限报警成功：{success}，目标值：{target}");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetTargetValue", 0, 1000, target, "设置T5上限报警", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置进料集成系统碳化硅芯片T5上限报警失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置T6上限报警
            Commands.Add(new DeviceCommand()
            {
                Name = "设置T6上限报警",
                HelpText = "设置进料系统集成碳化硅芯片T6上限报警",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", 0, 1000, 0, "T6上限报警目标值", true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue", 0, 1000, 0, "设置T6上限报警", true)
                    }
                },

                Action = WrapAction("设置T6上限报警", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        short target = parameter.GetValue<short>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置进料继承系统碳化硅芯片T6上限报警，结果（成功：{success}，目标值：{target}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x06, 0x00, 0x17, data[0], data[1] };
                            byte[] frame = BuildWriteCmd(23, (ushort)target);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置进料系统继承碳化硅芯片T6上限报警成功：{success}，目标值：{target}");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetTargetValue", 0, 1000, target, "设置T6上限报警", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置进料集成系统碳化硅芯片T6上限报警失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置压力上限报警
            Commands.Add(new DeviceCommand()
            {
                Name = "设置压力上限报警",
                HelpText = "设置进料系统集成碳化硅芯片压力上限报警",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", 0, 1000, 0, "压力上限报警目标值", true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue", 0, 1000, 0, "设置压力上限报警", true)
                    }
                },

                Action = WrapAction("设置压力上限报警", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        short target = parameter.GetValue<short>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置进料继承系统碳化硅芯片压力上限报警，结果（成功：{success}，目标值：{target}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x06, 0x00, 0x18, data[0], data[1] };
                            byte[] frame = BuildWriteCmd(24, (ushort)target);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置进料系统继承碳化硅芯片压力上限报警成功：{success}，目标值：{target}");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetTargetValue", 0, 1000, target, "设置压力上限报警", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置进料集成系统碳化硅芯片压力上限报警失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置温度检测通讯故障
            Commands.Add(new DeviceCommand()
            {
                Name = "设置温度检测通讯故障",
                HelpText = "设置进料系统集成碳化硅芯片温度检测通讯故障",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", 0, 1000, 0, "温度检测通讯故障目标值", true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue", 0, 1000, 0, "设置温度检测通讯故障", true)
                    }
                },

                Action = WrapAction("设置温度检测通讯故障", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        short target = parameter.GetValue<short>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置进料继承系统碳化硅芯片温度检测通讯故障，结果（成功：{success}，目标值：{target}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x06, 0x00, 0x19, data[0], data[1] };
                            byte[] frame = BuildWriteCmd(25, (ushort)target);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置进料系统继承碳化硅芯片温度检测通讯故障成功：{success}，目标值：{target}");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetTargetValue", 0, 1000, target, "设置温度检测通讯故障", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置进料集成系统碳化硅芯片温度检测通讯故障失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置压力检测通讯故障
            Commands.Add(new DeviceCommand()
            {
                Name = "设置压力检测通讯故障",
                HelpText = "设置进料系统集成碳化硅芯片压力检测通讯故障",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", 0, 1000, 0, "压力检测通讯故障目标值", true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue", 0, 1000, 0, "设置压力检测通讯故障", true)
                    }
                },

                Action = WrapAction("设置压力检测通讯故障", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        short target = parameter.GetValue<short>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置进料继承系统碳化硅芯片压力检测通讯故障，结果（成功：{success}，目标值：{target}）");
                        }
                        else
                        {
                            //byte[] frame = { slaveId, 0x06, 0x00, 0x1A, data[0], data[1] };
                            byte[] frame = BuildWriteCmd(26, (ushort)target);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置进料系统继承碳化硅芯片压力检测通讯故障成功：{success}，目标值：{target}");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetTargetValue", 0, 1000, target, "设置压力检测通讯故障", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置进料集成系统碳化硅芯片压力检测通讯故障失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 报警复位
            Commands.Add(new DeviceCommand()
            {
                Name = "报警复位",
                HelpText = "进料系统集成碳化硅芯片报警复位",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "复位成功", true)
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
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式复位进料继承系统碳化硅芯片故障，结果（成功：{success}）");
                        }
                        else
                        {
                            // 发送的指令
                            //byte[] frame = { slaveId, 0x06, 0x00, 0x1B, 0x00, 0x01 };
                            byte[] frame = BuildWriteCmd(27, 1);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"复位进料系统继承碳化硅芯片故障成功：{success}");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "复位成功", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"复位进料集成系统碳化硅芯片故障失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置消音
            Commands.Add(new DeviceCommand()
            {
                Name = "消音",
                HelpText = "进料系统集成碳化硅芯片报警消音",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "消音成功", true)
                    }
                },

                Action = WrapAction("消音", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        byte slaveId = Parameters.GetValue<byte>("SlaveId");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式进料继承系统碳化硅芯片报警消音，结果（成功：{success}）");
                        }
                        else
                        {
                            // 发送的指令
                            //byte[] frame = { slaveId, 0x06, 0x00, 0x1C, 0x00, 0x01 };
                            byte[] frame = BuildWriteCmd(28, 1);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"进料系统继承碳化硅芯片报警消音成功：{success}");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "消音成功", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"进料集成系统碳化硅芯片报警消音失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置报警
            Commands.Add(new DeviceCommand()
            {
                Name = "设置报警",
                HelpText = "设置进料系统集成碳化硅芯片报警",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", 0, 1000, 0, "报警目标值", true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTargetValue", 0, 1000, 0, "设置报警值", true)
                    }
                },

                Action = WrapAction("设置报警", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        short target = parameter.GetValue<short>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = true;
                            InfoLog($"模拟模式设置进料继承系统碳化硅芯片报警，结果（成功：{success}，目标值：{target}）");
                        }
                        else
                        {
                            // 发送的指令
                            //byte[] frame = { slaveId, 0x06, 0x00, 0x1D, data[0], data[1] };
                            byte[] frame = BuildWriteCmd(29, (ushort)target);
                            success = await WriteAsync(frame, 0, frame.Length, CancellationToken.None);
                            InfoLog($"设置进料系统继承碳化硅芯片报警成功：{success}，目标值：{target}");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetTargetValue", 0, 1000, target, "设置报警值", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置进料集成系统碳化硅芯片报警失败：{e.Message}");
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


        /// <summary>
        /// ZLanGateway 模式下的连接实现。发送 Modbus 读寄存器命令,若收到合法应答则视为连通。
        /// </summary>
        protected override async Task<bool> OnConnectViaGatewayAsync()
        {
            try
            {
                InfoLog("通过 ZLAN 网关测试连接");
                byte[] sendCmd = BuildReadCmd(register: 29, count: 1);
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


        private Func<DeviceParameters, DeviceParameters> WrapAction(string commandName, Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return (DeviceParameters parameters) =>
            {
                _logger.LogInformation($"[ModbusRTU进料系统集成碳化硅芯片-开始] Device={Name} Command={commandName}");

                var sw = Stopwatch.StartNew();
                try
                {
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();

                    _logger.LogInformation($"[ModbusRTU进料系统集成碳化硅芯片-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[ModbusRTU进料系统集成碳化硅芯片-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        private Func<DeviceParameters, Task<DeviceParameters>> AsyncWrapAction(
            string commandName,
            Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
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
            byte[] frame =
            {
                slaveId,
                0x10,
                (byte)(register >> 8),(byte)(register & 0xFF),
                0x00, 0x02,
                0x04,
                (byte)(register >> 24),(byte)(register >> 16),
                (byte)(register >> 8),(byte)(register & 0xFF),
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
        /// 发送 Modbus 读寄存器命令并解析应答中的 Int16 数据。
        /// 自动重试 3 次以应对工业链路偶发抖动。
        /// </summary>
        private async Task<short> SendAndParseInt16Async(byte[] sendCmd, CancellationToken token)
        {
            Exception lastException = null;

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    return await SendAndParseInt16OnceAsync(sendCmd, token).ConfigureAwait(false);
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

        /// <summary>
        /// 读命令的单次执行实现。走实例锁排队,加站号/功能码/长度校验。
        /// </summary>
        private async Task<short> SendAndParseInt16OnceAsync(byte[] sendCmd, CancellationToken token)
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
        /// 发送 Modbus 读寄存器命令并解析应答中的 Int32 数据，自动重试3次
        /// </summary>
        /// <param name="sendCmd"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        /// <exception cref="TimeoutException"></exception>
        private async Task<int> SendAndParseInt32Async(byte[] sendCmd, CancellationToken token)
        {
            Exception lastException = null;

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    return await SendAndParseInt32OnceAsync(sendCmd, token).ConfigureAwait(false);
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

        private async Task<int> SendAndParseInt32OnceAsync(byte[] sendCmd, CancellationToken token)
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

                // 提取数据载荷
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
