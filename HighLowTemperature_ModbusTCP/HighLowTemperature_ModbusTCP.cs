using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;

namespace HighLowTemperature_ModbusTCP
{
    [Export(typeof(IDevice))]
    public class HighLowTemperature_ModbusTCP : Device, IDeviceScreen
    {
        //
        private readonly ILogService _logger;


        public HighLowTemperature_ModbusTCP()
        {
            _logger = LogManager.GetLogger<HighLowTemperature_ModbusTCP>();

            Name = "高低温";
            Manufacturer = "ModbusTCP高低温设备";
            Category = DeviceCategories.PublicWorks;
            ComId = "HLT0001";
            // 设备连接管理
            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();
            // 设备允许放置的区域
            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/highlowtemperature.png";

            Parameters.Variables.Add(new StringParameter("DeviceId", "HLT0001")
            {
                Options = new ObservableCollection<string>(new List<string>() { "HLT0001", "HLT0002", "HLT0003", "HLT0004" }),
                HelpText = "ModbusTCP设备ID"
            });

            InitializeCommands();
        }

        /// <summary>
        /// 初始化设备命令
        /// </summary>
        /// <exception cref="InvalidOperationException"></exception>
        private void InitializeCommands()
        {
            // 获取出口温度、物料温度、回口温度
            Commands.Add(new DeviceCommand()
            {
                Name = "获取温度数据",
                HelpText = "获取ModbusTCP高低温设备的出口温度、物料温度、回口温度",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("OutletTemperature",-1000,10000,0,"出口温度",true,"℃"),
                        new NumberParameter("MaterialTemperature",-1000,10000,0,"物料温度",true,"℃"),
                        new NumberParameter("BackTemperature",-1000,10000,0,"回口温度",true,"℃")
                    }
                },

                AsyncAction = AsyncWrapAction("获取温度数据", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        float outlet = 0;
                        float material = 0;
                        float back = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            outlet = new Random().Next(0, 100);
                            material = new Random().Next(0, 100);
                            back = new Random().Next(0, 100);
                            InfoLog($"模拟模式获取高低温设备温度数据，结果（出口温度：{outlet}，物料温度：{material}，回口温度：{back}）");
                        }
                        else if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            outlet = await ConnectionManager.ReadAsync<short>(ComId, "出口温度");
                            material = await ConnectionManager.ReadAsync<short>(ComId, "物料温度");
                            back = await ConnectionManager.ReadAsync<short>(ComId, "回口温度");

                            InfoLog($"获取ModbusTCP高低温设备温度数据，结果（出口温度：{outlet}，物料温度：{material}，回口温度：{back}）");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("OutletTemperature", -1000, 10000, outlet/10.0, "出口温度",true,"℃"),
                                new NumberParameter("MaterialTemperature", -1000, 10000, material/10.0, "物料温度",true,"℃"),
                                new NumberParameter("BackTemperature", -1000, 10000, back/10.0, "回口温度",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取ModbusTCP高低温设备温度数据失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取SV
            Commands.Add(new DeviceCommand()
            {
                Name = "获取温度SV",
                HelpText = "获取ModbusTCP高低温设备温度SV",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("SV",-1000,10000,0,"温度SV",true,"℃")
                    }
                },

                AsyncAction = AsyncWrapAction("获取温度SV", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        float outlet = 0;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            outlet = new Random().Next(0, 100);
                            InfoLog($"模拟模式获取高低温设备温度SV，结果（SV：{outlet}）");
                        }
                        else if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            outlet = await ConnectionManager.ReadAsync<short>(ComId, "温度SV");

                            InfoLog($"获取ModbusTCP高低温设备温度SV，结果（SV：{outlet}）");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("SV", -1000, 10000, outlet/10.0, "温度SV",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取ModbusTCP高低温设备温度SV失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 设置SV
            Commands.Add(new DeviceCommand()
            {
                Name = "设置温度SV",
                HelpText = "设置ModbusTCP高低温设备温度SV",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetValue", -1000, 10000, 0, "温度SV目标值", true, "℃")
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("Set SV", -1000, 10000, 0, "温度SV设定值", true, "℃")
                    }
                },

                AsyncAction = AsyncWrapAction("设置温度SV", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        float outlet = parameter.GetValue<float>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = Convert.ToBoolean(new Random().Next(0, 2));
                            InfoLog($"模拟模式设置高低温设备温度SV，结果（SV：{outlet}）");
                        }
                        else if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            int temp = (int)(outlet * 10);
                            if (temp < 0)
                            {
                                temp = 65535 + temp;
                            }
                            success = await ConnectionManager.WriteAsync(ComId, "温度SV", (ushort)temp);

                            InfoLog($"设置ModbusTCP高低温设备温度SV，结果（Success:{success}, SV：{outlet}）");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new NumberParameter("SV", -1000, 10000, outlet, "温度SV",true,"℃")
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置ModbusTCP高低温设备温度SV失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 定值刷新画面启动
            Commands.Add(new DeviceCommand()
            {
                Name = "定值刷新画面启动",
                HelpText = "ModbusTCP高低温设备定值刷新画面启动",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "启动成功", true)
                    }
                },

                AsyncAction = AsyncWrapAction("定值刷新画面启动", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = Convert.ToBoolean(new Random().Next(0, 2));
                            InfoLog($"模拟模式设置高低温设备定值刷新画面启动，结果（Success：{success}）");
                        }
                        else if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            if (await ConnectionManager.WriteAsync(ComId, "启动/停止", true))
                            {
                                success = await ConnectionManager.WriteAsync(ComId, "启动界面", true);
                            }

                            InfoLog($"ModbusTCP高低温设备定值刷新画面启动，结果（Success:{success}）");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "启动成功", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"ModbusTCP高低温设备定值刷新画面启动失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 定值刷新画面停止
            Commands.Add(new DeviceCommand()
            {
                Name = "定值刷新画面停止",
                HelpText = "ModbusTCP高低温设备定值刷新画面停止",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "停止成功", true)
                    }
                },

                AsyncAction = AsyncWrapAction("定值刷新画面停止", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = Convert.ToBoolean(new Random().Next(0, 2));
                            InfoLog($"模拟模式设置高低温设备定值刷新画面停止，结果（Success：{success}）");
                        }
                        else if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            if (await ConnectionManager.WriteAsync(ComId, "启动/停止", false))
                            {
                                success = await ConnectionManager.WriteAsync(ComId, "停止界面", true);
                            }

                            InfoLog($"ModbusTCP高低温设备定值刷新画面停止，结果（Success:{success}）");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "停止成功", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"ModbusTCP高低温设备定值刷新画面停止失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 程式刷新画面启动
            Commands.Add(new DeviceCommand()
            {
                Name = "程式刷新画面启动",
                HelpText = "ModbusTCP高低温设备程式刷新画面启动",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "启动成功", true)
                    }
                },

                AsyncAction = AsyncWrapAction("程式刷新画面启动", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = Convert.ToBoolean(new Random().Next(0, 2));
                            InfoLog($"模拟模式设置高低温设备程式刷新画面启动，结果（Success：{success}）");
                        }
                        else if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            if (await ConnectionManager.WriteAsync(ComId, "程式启动/停止", true))
                            {
                                success = await ConnectionManager.WriteAsync(ComId, "程式启动界面", true);
                            }

                            InfoLog($"ModbusTCP高低温设备程式刷新画面启动，结果（Success:{success}）");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "启动成功", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"ModbusTCP高低温设备程式刷新画面启动失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 程式刷新画面停止
            Commands.Add(new DeviceCommand()
            {
                Name = "程式刷新画面停止",
                HelpText = "ModbusTCP高低温设备程式刷新画面停止",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "停止成功", true)
                    }
                },

                AsyncAction = AsyncWrapAction("程式刷新画面停止", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = Convert.ToBoolean(new Random().Next(0, 2));
                            InfoLog($"模拟模式设置高低温设备程式刷新画面停止，结果（Success：{success}）");
                        }
                        else if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            if (await ConnectionManager.WriteAsync(ComId, "程式启动/停止", false))
                            {
                                success = await ConnectionManager.WriteAsync(ComId, "程式停止界面", true);
                            }

                            InfoLog($"ModbusTCP高低温设备程式刷新画面停止，结果（Success:{success}）");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "停止成功", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"ModbusTCP高低温设备程式刷新画面停止失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 运行模式（ON：定值，OFF：程式）
            Commands.Add(new DeviceCommand()
            {
                Name = "设置运行模式",
                HelpText = "设置ModbusTCP高低温设备运行模式",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("TargetValue", false, "目标运行模式 (ON：定值，OFF：程式)", true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new BooleanParameter("SetOnOff", false, "设定的运行模式", true)
                    }
                },

                AsyncAction = AsyncWrapAction("设置运行模式", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        bool mode = parameter.GetValue<bool>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = Convert.ToBoolean(new Random().Next(0, 2));
                            InfoLog($"模拟模式设置高低温设备运行模式，结果（On/Off：{mode}）");
                        }
                        else if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            success = await ConnectionManager.WriteAsync(ComId, "运行模式", mode);

                            InfoLog($"设置ModbusTCP高低温设备运行模式，结果（Success:{success}, On/Off：{mode}）");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "设置成功", true),
                                new BooleanParameter("SetOnOff", mode, "设定的运行模式",true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"设置ModbusTCP高低温设备运行模式失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取运行模式
            Commands.Add(new DeviceCommand()
            {
                Name = "获取运行模式",
                HelpText = "获取ModbusTCP高低温设备运行模式",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("On/Off", false,"运行模式",true)
                    }
                },

                AsyncAction = AsyncWrapAction("获取运行模式", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        bool outlet = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            outlet = Convert.ToBoolean(new Random().Next(0, 2));
                            InfoLog($"模拟模式获取高低温设备运行模式，结果（On/Off：{outlet}）");
                        }
                        else if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            outlet = await ConnectionManager.ReadAsync<bool>(ComId, "运行模式");

                            InfoLog($"获取ModbusTCP高低温设备运行模式，结果（On/Off：{outlet}）");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("On/Off", outlet, "运行模式",true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取ModbusTCP高低温设备运行模式失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取画面启动、停止状态
            Commands.Add(new DeviceCommand()
            {
                Name = "获取画面启动、停止状态",
                HelpText = "获取ModbusTCP高低温设备画面启动、停止状态",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("FixedOnOff", false,"定值启动/停止",true),
                        new BooleanParameter("FixedOff", false,"刷新停止",true),
                        new BooleanParameter("FixedOn", false,"刷新启动",true),
                        new BooleanParameter("PatternOnOff", false,"程式启动/停止",true),
                        new BooleanParameter("PatternOff", false,"停止",true),
                        new BooleanParameter("PatternOn", false,"启动",true)
                    }
                },

                AsyncAction = AsyncWrapAction("获取画面启动、停止状态", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        bool[] result = new bool[6];

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            result[0] = Convert.ToBoolean(new Random().Next(0, 2));
                            result[1] = Convert.ToBoolean(new Random().Next(0, 2));
                            result[2] = Convert.ToBoolean(new Random().Next(0, 2));
                            result[3] = Convert.ToBoolean(new Random().Next(0, 2));
                            result[4] = Convert.ToBoolean(new Random().Next(0, 2));
                            result[5] = Convert.ToBoolean(new Random().Next(0, 2));
                            InfoLog($"模拟模式获取高低温设备画面启动、停止状态，结果（{string.Join(", ", result)}）");
                        }
                        else if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            Task<bool>[] tasks = new Task<bool>[6];
                            tasks[0] = ConnectionManager.ReadAsync<bool>(ComId, "启动/停止");//4
                            tasks[1] = ConnectionManager.ReadAsync<bool>(ComId, "启动界面");//3
                            tasks[2] = ConnectionManager.ReadAsync<bool>(ComId, "停止界面");//5
                            tasks[3] = ConnectionManager.ReadAsync<bool>(ComId, "程式启动/停止");//7
                            tasks[4] = ConnectionManager.ReadAsync<bool>(ComId, "程式启动界面");//6
                            tasks[5] = ConnectionManager.ReadAsync<bool>(ComId, "程式停止界面");//8

                            await Task.WhenAll(tasks);

                            for(int i = 0; i < result.Length; i++)
                            {
                                result[i] = await tasks[i];
                            }

                            InfoLog($"获取ModbusTCP高低温设备画面启动、停止状态，结果（{string.Join(", ", result)}）");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("FixedOnOff", result[0],"定值启动/停止",true),
                                new BooleanParameter("FixedOn", result[1],"刷新启动",true),
                                new BooleanParameter("FixedOff", result[2],"刷新停止",true),
                                new BooleanParameter("PatternOnOff", result[3],"程式启动/停止",true),
                                new BooleanParameter("PatternOn", result[4],"启动",true),
                                new BooleanParameter("PatternOff", result[5],"停止",true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取ModbusTCP高低温设备画面启动、停止状态失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 获取可视化界面数据（设备属性界面）
            Commands.Add(new DeviceCommand()
            {
                Name = "获取可视化数据",
                HelpText = "",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("OutletTemperature",-1000,10000,0,"出口温度",true,"℃"),
                        new NumberParameter("MaterialTemperature",-1000,10000,0,"物料温度",true,"℃"),
                        new NumberParameter("BackTemperature",-1000,10000,0,"回口温度",true,"℃"),
                        new NumberParameter("SV", -1000, 10000, 0, "温度设定值SV", true, "℃"),
                        new BooleanParameter("Run", false, "定值启动", true)
                    }
                },

                AsyncAction = AsyncWrapAction("获取可视化数据", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        float outlet = 0;
                        float material = 0;
                        float back = 0;
                        float sv = 0;
                        bool on = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            outlet = new Random().Next(0, 100);
                            material = new Random().Next(0, 100);
                            back = new Random().Next(0, 100);
                            sv = new Random().Next(0, 100);
                            on = Convert.ToBoolean(new Random().Next(0, 2));
                            InfoLog($"模拟模式获取可视化界面数据，结果（出口温度：{outlet}，物料温度：{material}，回口温度：{back}，温度设定值SV：{sv}）");
                        }
                        else if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            Task<short>[] tasks = new Task<short>[4];
                            tasks[0] = ConnectionManager.ReadAsync<short>(ComId, "出口温度");
                            tasks[1] = ConnectionManager.ReadAsync<short>(ComId, "物料温度");
                            tasks[2] = ConnectionManager.ReadAsync<short>(ComId, "回口温度");
                            tasks[3] = ConnectionManager.ReadAsync<short>(ComId, "温度SV");

                            await Task.WhenAll(tasks);
                            on = await ConnectionManager.ReadAsync<bool>(ComId, "启动/停止");

                            outlet = await tasks[0];
                            material = await tasks[1];
                            back = await tasks[2];
                            sv = await tasks[3];

                            InfoLog($"获取ModbusTCP高低温设备可视化界面数据，结果（出口温度：{outlet}，物料温度：{material}，回口温度：{back}，温度设定值SV：{sv}）");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new NumberParameter("OutletTemperature",-1000,10000,outlet/10,"出口温度",true,"℃"),
                                new NumberParameter("MaterialTemperature",-1000,10000,material/10,"物料温度",true,"℃"),
                                new NumberParameter("BackTemperature",-1000,10000,back/10,"回口温度",true,"℃"),
                                new NumberParameter("SV", -1000, 10000, sv/10, "温度设定值SV", true, "℃"),
                                new BooleanParameter("Run", on, "定值启动", true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"获取可视化界面数据失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 定值启动
            Commands.Add(new DeviceCommand()
            {
                Name = "定值启动/停止",
                HelpText = "",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("TargetValue",false,"定值启动停止",true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success",false,"启动成功",true),
                        new BooleanParameter("SetValue",false,"定值启动停止设定值",true)
                    }
                },

                AsyncAction = AsyncWrapAction("定值启动/停止", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        bool mode = parameter.GetValue<bool>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = Convert.ToBoolean(new Random().Next(0, 2));
                            InfoLog($"模拟模式设置高低温设备定值启动/停止，结果（On/Off：{mode}）");
                        }
                        else if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            success = await ConnectionManager.WriteAsync(ComId, "启动/停止", mode);

                            InfoLog($"设置ModbusTCP高低温设备定值启动/停止，结果（Success:{success}, On/Off：{mode}）");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "启动成功", true),
                                new BooleanParameter("SetValue",mode,"定值启动/停止设定值",true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"高低温设备定值启动/停止失败：{e.Message}");
                        throw;
                    }
                })
            });

            // 程式启动
            Commands.Add(new DeviceCommand()
            {
                Name = "程式启动/停止",
                HelpText = "",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("TargetValue",false,"程式启动停止",true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success",false,"启动成功",true),
                        new BooleanParameter("SetValue",false,"程式启动停止设定值",true)
                    }
                },

                AsyncAction = AsyncWrapAction("程式启动/停止", async parameter =>
                {
                    try
                    {
                        ComId = Parameters.GetValue<string>("DeviceId");
                        bool mode = parameter.GetValue<bool>("TargetValue");
                        bool success = false;

                        if (IsSimulationMode)
                        {
                            await Task.Delay(1000);
                            success = Convert.ToBoolean(new Random().Next(0, 2));
                            InfoLog($"模拟模式设置高低温设备程式启动/停止，结果（On/Off：{mode}）");
                        }
                        else if (DeviceConnectionConfig.CommunicationMode == CommunicationMode.ModbusTcp && ConnectionManager != null)
                        {
                            success = await ConnectionManager.WriteAsync(ComId, "程式启动/停止", mode);

                            InfoLog($"设置ModbusTCP高低温设备程式启动/停止，结果（Success:{success}, On/Off：{mode}）");
                        }
                        else
                        {
                            throw new InvalidOperationException("设备未配置为ModbusTCP模式且非模拟模式");
                        }

                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", success, "启动成功", true),
                                new BooleanParameter("SetValue",mode,"程式启动/停止设定值",true)
                            }
                        };
                    }
                    catch (Exception e)
                    {
                        ErrorLog($"高低温设备程式启动/停止失败：{e.Message}");
                        throw;
                    }
                })
            });
        }


        public string ScreenKey => "HighLowTemperatureScreen";


        #region Device 虚方法实现

        protected override Task<bool> OnConnectAsync()
        {
            return base.OnConnectAsync();
        }

        protected override Task<bool> OnDisconnectAsync()
        {
            return base.OnDisconnectAsync();
        }

        #endregion


        #region 命令包装(日志 + 异常封装)

        private Func<DeviceParameters, DeviceParameters> WrapAction(string commandName, Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return (DeviceParameters parameters) =>
            {
                _logger.LogInformation($"[ModbusTCP高低温设备-开始] Device={Name} Command={commandName}");

                var sw = Stopwatch.StartNew();
                try
                {
                    var output = innerAsync(parameters).GetAwaiter().GetResult();
                    sw.Stop();

                    _logger.LogInformation($"[ModbusTCP高低温设备-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[ModbusTCP高低温设备-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        private Func<DeviceParameters, Task<DeviceParameters>> AsyncWrapAction(
            string commandName, Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return async (DeviceParameters parameters) =>
            {
                _logger.LogInformation($"[ModbusTCP高低温设备-开始] Device={Name} Command={commandName}");
                var sw = Stopwatch.StartNew();
                try
                {
                    var output = await innerAsync(parameters);
                    sw.Stop();
                    _logger.LogInformation($"[ModbusTCP高低温设备-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[ModbusTCP高低温设备-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        #endregion
    }
}
