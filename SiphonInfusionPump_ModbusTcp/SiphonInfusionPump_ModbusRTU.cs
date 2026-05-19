using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;

namespace SiphonInfusionPump_ModbusTcp
{
    [Export(typeof(IDevice))]
    public class SiphonInfusionPump_ModbusRTU : Device, IFlowLifecycleAware
    {
        private readonly ILogService _logger;

        // 实时压力轮询任务相关
        private Task _dataTask;
        private CancellationTokenSource _dataCts;

        public SiphonInfusionPump_ModbusRTU()
        {
            _logger = LogManager.GetLogger<SiphonInfusionPump_ModbusRTU>();

            DeviceId = "SIP0001";
            Name = "平流输液泵";
            Manufacturer = "ModbusRTU平流输液泵";
            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/PeristalticPumps_CY.png";
            Category = DeviceCategories.Pumps;
            ComId = "SIP0001";
            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();
            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            Parameters.Variables.Add(new StringParameter("DeviceId", "SIP0001", "ModbusRTU设备ID")
            {
                Options = new ObservableCollection<string>() { "SIP0001", "SIP0002", "SIP0003", "SIP0004", "SIP0005" },
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

            // 通信方式:由用户在画布参数面板逐实例选择
            Parameters.Variables.Add(new StringParameter("通信方式", "Direct", "通信方式")
            {
                Options = new ObservableCollection<string>() { "Direct", "PLC", "ModbusTcp", "ZLanGateway" }
            });

            // Modbus 从机站号 (1~254),由用户根据设备面板设定填写
            Parameters.Variables.Add(new NumberParameter("Modbus站号", 1, 254, 1, "Modbus 从机站号 (1~254)"));

            InitializeCommands();
        }

        /// <summary>
        /// 声明本驱动支持通过 ZLAN 网关通信,网关配置界面才会把它列入候选设备。
        /// </summary>
        public override bool SupportsZLanGateway => true;

        private void InitializeCommands()
        {
            // 停止
            Commands.Add(new DeviceCommand()
            {
                Name = "停止泵",
                ImageLocation = "",
                HelpText = "停止平流输液泵",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", true, "平流输液泵停止成功", true)
                    }
                },

                AsyncAction = AsyncWrapAction("停止泵", async parameter =>
                {
                    OnStateChanged("CommandState_停止水平输液泵", CommandExecutionState.Running);
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            InfoLog("模拟模式停止水平输液泵");
                        }
                        else
                        {
                            byte[] buffers = BuildWriteCmd(register: 0x0007, value: 0x0001);
                            success = await WriteAsync(buffers, 0, buffers.Length, CancellationToken.None);
                        }

                        OnStateChanged("CommandState_停止水平输液泵",
                            success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "平流输液泵停止成功", true)
                            }
                        };
                    }
                    catch (Exception ex)
                    {
                        OnStateChanged("CommandState_停止水平输液泵", CommandExecutionState.Error);
                        ErrorLog($"平流输液泵停止失败:{ex.Message}");
                        throw;
                    }
                })
            });

            // 启动
            Commands.Add(new DeviceCommand()
            {
                Name = "启动泵",
                ImageLocation = "",
                HelpText = "启动平流输液泵",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "平流输液泵启动成功", true)
                    }
                },

                AsyncAction = AsyncWrapAction("启动泵", async parameters =>
                {
                    try
                    {
                        ComId = parameters.GetValue<string>("DeviceId");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                            InfoLog("模拟模式启动水平输液泵");
                        }
                        else
                        {
                            byte[] buffers = BuildWriteCmd(register: 0x0005, value: 0x0001);
                            success = await WriteAsync(buffers, 0, buffers.Length, CancellationToken.None);
                        }

                        OnStateChanged("CommandState_启动水平输液泵",
                            success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "平流输液泵启动成功", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        OnStateChanged("CommandState_启动水平输液泵", CommandExecutionState.Error);
                        ErrorLog($"平流输液泵启动失败:{e.Message}");
                        throw;
                    }
                })
            });

            // 冲洗
            Commands.Add(new DeviceCommand()
            {
                Name = "冲洗",
                ImageLocation = "",
                HelpText = "冲洗平流输液泵",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "冲洗成功", true)
                    }
                },

                AsyncAction = AsyncWrapAction("冲洗", async parameters =>
                {
                    try
                    {
                        ComId = parameters.GetValue<string>("DeviceId");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                            InfoLog("模拟模式冲洗平流输液泵");
                        }
                        else
                        {
                            byte[] buffers = BuildWriteCmd(register: 0x0006, value: 0x0001);
                            success = await WriteAsync(buffers, 0, buffers.Length, CancellationToken.None);
                        }

                        OnStateChanged("CommandState_冲洗水平输液泵",
                            success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "冲洗成功", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        OnStateChanged("CommandState_冲洗输液泵", CommandExecutionState.Error);
                        ErrorLog($"平流输液泵冲洗失败:{e.Message}");
                        throw;
                    }
                })
            });

            // 重置压力
            Commands.Add(new DeviceCommand()
            {
                Name = "重置压力",
                ImageLocation = "",
                HelpText = "平流输液泵重置压力",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "重置压力成功", true)
                    }
                },

                AsyncAction = AsyncWrapAction("重置压力", async parameters =>
                {
                    try
                    {
                        ComId = parameters.GetValue<string>("DeviceId");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                            InfoLog("模拟模式平流输液泵重置压力");
                        }
                        else
                        {
                            byte[] buffers = BuildWriteCmd(register: 0x0008, value: 0x0001);
                            success = await WriteAsync(buffers, 0, buffers.Length, CancellationToken.None);
                        }

                        OnStateChanged("CommandState_重置压力",
                            success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "重置压力成功", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        OnStateChanged("CommandState_重置压力", CommandExecutionState.Error);
                        ErrorLog($"平流输液泵重置压力失败:{e.Message}");
                        throw;
                    }
                })
            });

            // 设置压力上限
            Commands.Add(new DeviceCommand()
            {
                Name = "设置压力上限",
                ImageLocation = "",
                HelpText = "设置平流输液泵压力上限",
                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("PressureUpperLimit", 0, 25, 20, "设置的上限值", false)
                    }
                },
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", true, "设置成功", true),
                        new NumberParameter("SetUpperLimit", 0, 100, 0, "设置的上限值", true)
                    }
                },

                AsyncAction = AsyncWrapAction("设置压力上限", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        bool success = false;
                        short upperLimit = parameter.GetValue<short>("PressureUpperLimit");

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                            InfoLog($"模拟模式设置平流输液泵压力上限:{upperLimit} Mpa");
                        }
                        else
                        {
                            byte[] buffers = BuildWriteCmd(register: 0x0002, value: (ushort)(upperLimit*10));
                            success = await WriteAsync(buffers, 0, buffers.Length, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetUpperLimit", 0, 25, upperLimit, "设置的上限值", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置平流输液泵压力上限失败:{e.Message}");
                        throw;
                    }
                })
            });

            // 设置压力下限
            Commands.Add(new DeviceCommand()
            {
                Name = "设置压力下限",
                ImageLocation = "",
                HelpText = "设置平流输液泵的最小压力",
                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("PressureLowerLimit", 0, 25, 0, "设置的下限值", false)
                    }
                },
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", true, "设置成功", true),
                        new NumberParameter("SetLowerLimit", 0, 100, 0, "设置的下限值", true)
                    }
                },

                AsyncAction = AsyncWrapAction("设置压力下限", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        bool success = false;
                        short lowerLimit = parameter.GetValue<short>("PressureLowerLimit");

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                            InfoLog($"模拟模式设置平流输液泵压力下限:{lowerLimit} Mpa");
                        }
                        else
                        {
                            byte[] buffers = BuildWriteCmd(register: 0x0003, value: (ushort)(lowerLimit * 10));
                            success = await WriteAsync(buffers, 0, buffers.Length, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SetLowerLimit", 0, 100, lowerLimit, "设置的下限值", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置平流输液泵压力下限失败:{e.Message}");
                        throw;
                    }
                })
            });

            // 设置流量
            Commands.Add(new DeviceCommand()
            {
                Name = "设置流量",
                ImageLocation = "",
                HelpText = "设置平流输液泵流量",
                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetFlow", 0, 9999, 0, "设置的目标值", true, "ml/min")
                    }
                },
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置平流输液泵流量成功", true),
                        new NumberParameter("SetFlow", 0, 9999, 0, "设置的目标值", true, "ml/min")
                    }
                },

                AsyncAction = AsyncWrapAction("设置流量", async parameters =>
                {
                    try
                    {
                        ComId = parameters.GetValue<string>("DeviceId");
                        bool success = false;
                        short target = parameters.GetValue<short>("TargetFlow");

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                            InfoLog("模拟模式设置平流输液泵流量");
                        }
                        else
                        {
                            byte[] buffers = BuildWriteCmd(register: 0x0000, value: (ushort)(target*100));
                            success = await WriteAsync(buffers, 0, buffers.Length, CancellationToken.None);
                        }

                        OnStateChanged("CommandState_设置平流输液泵流量",
                            success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置平流输液泵流量成功", true),
                                new NumberParameter("SetFlow", 0, 9999, target, "设置的目标值", true, "ml/min")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        OnStateChanged("CommandState_设置平流输液泵流量", CommandExecutionState.Error);
                        ErrorLog($"设置平流输液泵流量失败:{e.Message}");
                        throw;
                    }
                })
            });

            // 设置流量2
            Commands.Add(new DeviceCommand()
            {
                Name = "设置流量2",
                ImageLocation = "",
                HelpText = "设置平流输液泵流量2",
                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetFlow2", 0, 9999, 0, "设置的目标值", true, "ml/min")
                    }
                },
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置平流输液泵流量2成功", true)
                    }
                },

                AsyncAction = AsyncWrapAction("设置流量2", async parameters =>
                {
                    try
                    {
                        ComId = parameters.GetValue<string>("DeviceId");
                        bool success = false;
                        short target = parameters.GetValue<short>("TargetFlow2");

                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            success = true;
                            InfoLog("模拟模式设置平流输液泵流量2");
                        }
                        else
                        {
                            byte[] buffers = BuildWriteCmd(register: 0x0001, value: (ushort)(target*10));
                            success = await WriteAsync(buffers, 0, buffers.Length, CancellationToken.None);
                        }

                        OnStateChanged("CommandState_设置平流输液泵流量2",
                            success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置平流输液泵流量2成功", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        OnStateChanged("CommandState_设置平流输液泵流量2", CommandExecutionState.Error);
                        ErrorLog($"设置平流输液泵流量2失败:{e.Message}");
                        throw;
                    }
                })
            });

            // 获取实时压力
            Commands.Add(new DeviceCommand()
            {
                Name = "获取实时压力",
                ImageLocation = "",
                HelpText = "获取平流输液泵的实时压力",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("Pressure", 0, 5000, 0, "实时压力", true, "Mpa")
                    }
                },
                AsyncAction = AsyncWrapAction("获取实时压力", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        double pressure = 0;
                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            pressure = new Random().Next(20, 100);
                            InfoLog($"模拟模式获取平流输液泵实时压力,结果({pressure})");
                        }
                        else
                        {
                            byte[] sendCmd = BuildReadCmd(register: 0x0004, count: 0x0001);
                            pressure = await SendAndParseInt16Async(sendCmd, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("Pressure", 0, 5000, pressure / 10.0, "实时压力", true, "Mpa")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取平流输液泵实施压力失败:{e.Message}");
                        throw;
                    }
                })
            });

            // 获取压力上限
            Commands.Add(new DeviceCommand()
            {
                Name = "获取压力上限",
                ImageLocation = "",
                HelpText = "获取平流输液泵的压力上限",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("UpperLimit", 0, 5000, 0, "压力上限", true, "Mpa")
                    }
                },
                AsyncAction = AsyncWrapAction("获取压力上限", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        double pressure = 0;
                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            pressure = new Random().Next(20, 100);
                            InfoLog($"模拟模式获取平流输液泵压力上限,结果({pressure})");
                        }
                        else
                        {
                            byte[] sendCmd = BuildReadCmd(register: 0x0002, count: 0x0001);
                            pressure = await SendAndParseInt16Async(sendCmd, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("UpperLimit", 0, 25, pressure / 10.0, "压力上限", true, "Mpa")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取平流输液泵压力上限失败:{e.Message}");
                        throw;
                    }
                })
            });

            // 获取压力下限
            Commands.Add(new DeviceCommand()
            {
                Name = "获取压力下限",
                ImageLocation = "",
                HelpText = "获取平流输液泵的压力下限",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("LowerLimit", 0, 420, 0, "压力下限", true, "Mpa")
                    }
                },
                AsyncAction = AsyncWrapAction("获取压力下限", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        double pressure = 0;
                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            pressure = new Random().Next(20, 100);
                            InfoLog($"模拟模式获取平流输液泵压力下限,结果({pressure})");
                        }
                        else
                        {
                            byte[] sendCmd = BuildReadCmd(register: 0x0003, count: 0x0001);
                            pressure = await SendAndParseInt16Async(sendCmd, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("LowerLimit", 0, 25, pressure / 10.0, "压力下限", true, "Mpa")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取平流输液泵压力下限失败:{e.Message}");
                        throw;
                    }
                })
            });

            // 获取流量
            Commands.Add(new DeviceCommand()
            {
                Name = "获取流量",
                ImageLocation = "",
                HelpText = "获取平流输液泵的流量",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("Flow", 0, 9999, 0, "流量", true, "ml/min")
                    }
                },
                AsyncAction = AsyncWrapAction("获取流量", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        double pressure = 0;
                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            pressure = new Random().Next(20, 100);
                            InfoLog($"模拟模式获取平流输液泵流量,结果({pressure})");
                        }
                        else
                        {
                            byte[] sendCmd = BuildReadCmd(register: 0x0000, count: 0x0001);
                            pressure = await SendAndParseInt16Async(sendCmd, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("Flow", 0, 9999, pressure / 100.0, "流量", true, "ml/min")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取平流输液泵流量:{e.Message}");
                        throw;
                    }
                })
            });

            // 获取流量2
            Commands.Add(new DeviceCommand()
            {
                Name = "获取流量2",
                ImageLocation = "",
                HelpText = "获取平流输液泵的流量2",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("Flow2", 0, 9999, 0, "流量", true, "ml/min")
                    }
                },
                AsyncAction = AsyncWrapAction("获取流量2", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        double pressure = 0;
                        if (IsSimulationMode)
                        {
                            await Task.Delay(2000);
                            pressure = new Random().Next(20, 100);
                            InfoLog($"模拟模式获取平流输液泵流量2,结果({pressure})");
                        }
                        else
                        {
                            byte[] sendCmd = BuildReadCmd(register: 0x0001, count: 0x0001);
                            pressure = await SendAndParseInt16Async(sendCmd, CancellationToken.None);
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("Flow2", 0, 9999, pressure / 10.0, "流量2", true, "ml/min")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取平流输液泵流量2失败:{e.Message}");
                        throw;
                    }
                })
            });
        }

        #region 设备接口实现

        protected override void OnInitialize()
        {
            InfoLog("开始初始化平流输液泵");
            try
            {
                base.OnInitialize();
                InfoLog("平流输液泵初始化完成");
            }
            catch (Exception e)
            {
                ErrorLog($"平流输液泵初始化失败", e);
                throw;
            }
        }

        protected override void OnShutdown()
        {
            InfoLog("开始关闭平流输液泵");
            try
            {
                base.OnShutdown();
                InfoLog("平流输液泵关闭完成");
            }
            catch (Exception e)
            {
                ErrorLog($"平流输液泵关闭失败", e);
                throw;
            }
        }

        /// <summary>
        /// Direct 模式下的连接实现。串口采用即用即建策略,这里只做逻辑标记。
        /// </summary>
        protected override Task<bool> OnConnectAsync()
        {
            InfoLog("Direct 模式串口连接(每次 IO 即用即建)");
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
                byte[] sendCmd = BuildReadCmd(register: 0x0004, count: 0x0001);
                var pressure = await SendAndParseInt16Async(sendCmd, CancellationToken.None);
                InfoLog($"网关连接成功,当前压力 {pressure}");
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

        protected override Task<bool> OnDisconnectAsync()
        {
            InfoLog("断开平流输液泵");
            return Task.FromResult(true);
        }

        #endregion

        #region 命令包装(日志 + 异常封装)

        private Func<DeviceParameters, DeviceParameters> WrapAction(
      string commandName, Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return (DeviceParameters parameters) =>
            {
                _logger.LogInformation($"[ModbusRTU平流输液泵命令-开始] Device={Name} Command={commandName}");
                var sw = Stopwatch.StartNew();
                try
                {
                    // 用 Task.Run 把 inner 切到线程池,避免 UI 线程死锁
                    var output = Task.Run(() => innerAsync(parameters)).GetAwaiter().GetResult();
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

        #region 流程生命周期接口实现

        public async Task<bool> OnFlowStartedAsync(FlowStartContext context)
        {
            try
            {
                InfoLog($"平流输液泵流程开始:{context.FlowName}");
                await StartPressurePollingAsync();
                InfoLog("平流输液泵已准备好执行流程");
                return true;
            }
            catch (Exception e)
            {
                ErrorLog("平流输液泵流程开始处理失败", e);
                return false;
            }
        }

        public async Task<bool> OnFlowCompletedAsync(FlowCompletionContext context)
        {
            try
            {
                InfoLog($"平流输液泵流程完成:{context.FlowName},成功:{context.IsSuccessful}");
                await StopPressurePollingAsync();
                InfoLog("平流输液泵流程完成处理结束");
                return true;
            }
            catch (Exception e)
            {
                ErrorLog("平流输液泵流程完成处理失败", e);
                return false;
            }
        }

        public async Task<bool> OnFlowFailedAsync(FlowFailureContext context)
        {
            try
            {
                InfoLog($"平流输液泵流程失败:{context.FlowName},错误消息:{context.ErrorMessage}");
                await StopPressurePollingAsync();
                InfoLog("平流输液泵流程失败处理结束");
                return true;
            }
            catch (Exception e)
            {
                ErrorLog("平流输液泵流程失败处理异常", e);
                return false;
            }
        }

        #endregion

        #region 实时压力轮询

        private async Task StartPressurePollingAsync()
        {
            try
            {
                InfoLog("实时压力轮询启动");
                if (_dataTask != null && !_dataTask.IsCompleted)
                {
                    InfoLog("已存在实时压力轮询任务,先停止");
                    await StopPressurePollingAsync();
                }

                _dataCts = new CancellationTokenSource();
                _dataTask = Task.Run(async () =>
                {
                    await PressurePollingAsync(_dataCts.Token);
                }, _dataCts.Token);
                InfoLog("实时压力轮询已启动");
            }
            catch (Exception e)
            {
                ErrorLog("实时压力轮询启动失败", e);
            }
        }

        private async Task StopPressurePollingAsync()
        {
            try
            {
                if (_dataCts != null)
                {
                    InfoLog("停止实时压力轮询");
                    _dataCts.Cancel();
                    try
                    {
                        if (_dataTask != null)
                        {
                            await _dataTask.WaitAsync(TimeSpan.FromSeconds(2));
                        }
                    }
                    catch { }
                    _dataCts.Dispose();
                    _dataCts = null;
                    _dataTask = null;
                    InfoLog("实时压力轮询已停止");
                }
            }
            catch (Exception e)
            {
                ErrorLog("实时压力轮询停止异常", e);
            }
        }

        private async Task PressurePollingAsync(CancellationToken token)
        {
            InfoLog("实时压力轮询任务已启动");
            while (!token.IsCancellationRequested)
            {
                try
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double pressure = 0;

                    if (IsSimulationMode)
                    {
                        await Task.Delay(2000, token);
                        pressure = new Random().Next(20, 100);
                    }
                    else
                    {
                        byte[] sendCmd = BuildReadCmd(register: 0x0004, count: 0x0001);
                        pressure = await SendAndParseInt16Async(sendCmd, token);
                    }

                    await Task.Delay(1000, token);
                }
                catch (OperationCanceledException)
                {
                    InfoLog("实时压力轮询任务已取消");
                    break;
                }
                catch (Exception e)
                {
                    ErrorLog("实时压力轮询任务执行失败", e);
                    try { await Task.Delay(1000, token); }
                    catch { break; }
                }
            }
        }

        #endregion

        #region 通信层

        /// <summary>
        /// 取当前配置的 Modbus 从机站号 (用户在参数面板可改,1~254)。
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
        /// 构造 Modbus RTU 写单寄存器命令 (功能码 0x06),自动加 CRC。
        /// 格式: [站号] 06 [reg_hi] [reg_lo] [val_hi] [val_lo] [crc_lo] [crc_hi]
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
        /// 构造 Modbus RTU 读保持寄存器命令 (功能码 0x03),自动加 CRC。
        /// 格式: [站号] 03 [reg_hi] [reg_lo] [cnt_hi] [cnt_lo] [crc_lo] [crc_hi]
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
        /// 写命令。底层走 IDeviceTransport,由框架根据"通信方式"参数路由到串口或网关 TCP。
        /// </summary>
        private async Task<bool> WriteAsync(byte[] buffers, int offset, int count, CancellationToken token)
        {
            using var transport = await GetTransportAsync();
            return await transport.SendAsync(buffers, offset, count, token);
        }

        /// <summary>
        /// 发送 Modbus 读寄存器命令并解析应答中的 Int16 数据。
        /// 用于读取压力、流量等单寄存器场景。
        /// </summary>
        private async Task<short> SendAndParseInt16Async(byte[] sendCmd, CancellationToken token)
        {
            byte[] recv = new byte[256];
            using var transport = await GetTransportAsync();
            int len = await transport.SendAndReceiveAsync(sendCmd, recv, 3000, token);

            if (len <= 0) return 0;

            byte[] data = ExtractPayload(recv);
            if (BitConverter.IsLittleEndian)
            {
                ReverseArray(data);
            }
            return BitConverter.ToInt16(data, 0);
        }

        /// <summary>
        /// 通过基类的 CreateTransportAsync 获取 Transport。
        /// 框架根据本设备实例当前选择的"通信方式"参数,决定返回串口或 TCP 实现。
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

        #region Modbus 工具

        /// <summary>
        /// 从 Modbus RTU 应答帧中提取数据载荷(去掉地址、功能码、长度、CRC)。
        /// </summary>
        private byte[] ExtractPayload(byte[] frame)
        {
            if (frame == null || frame.Length < 5)
            {
                throw new ArgumentException("报文长度无效");
            }

            if (frame[1] >= 0x80)
            {
                throw new ArgumentException($"Modbus 异常响应,异常码:{frame[2]:X2}");
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
        /// 原地反转字节数组(用于大端/小端转换)。
        /// </summary>
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

        #endregion
    }

    /// <summary>
    /// CRC16-Modbus 校验工具。多项式 0xA001,初始值 0xFFFF。
    /// </summary>
    public static class Crc16Modbus
    {
        /// <summary>计算 CRC16-Modbus 校验码</summary>
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

        /// <summary>在数据包尾部追加 CRC 两个字节并返回新数组</summary>
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

        /// <summary>校验数据包尾部 CRC 是否正确</summary>
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