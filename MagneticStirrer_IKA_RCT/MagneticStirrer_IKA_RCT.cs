using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;

namespace MagneticStirrer_IKA_RCT
{
    /// <summary>
    /// IKA RCT Digital 加热磁力搅拌器驱动(RS-232 ASCII 行协议)。
    ///
    /// ── 协议来源 ────────────────────────────────────────────────────────
    /// 命令表移植自 PyLabware(Cronin Group,MIT License,Copyright (c) 2021),
    /// 文件 PyLabware/devices/ika_rct_digital.py。PyLabware 的原始依据是 IKA 官方
    /// 操作手册英文部分 201811_IKAPlate-Lab_A1_25002139a。
    /// MIT 允许在保留版权声明的前提下把命令序列翻译成其它语言,这里只搬协议不引入任何运行时依赖。
    ///
    /// ── 串口设置 ────────────────────────────────────────────────────────
    /// 9600, 7 数据位, 偶校验, 1 停止位。
    /// 注意不是常见的 8N1 —— IKA 这一代仪器用的是 7E1,配错了表现为收到乱码而不是收不到,
    /// 这一条是自己抓包最容易卡住的地方。
    ///
    /// ── 报文格式 ────────────────────────────────────────────────────────
    /// 发送: 命令名 + " " + 参数 + " \r \n"      ← 结束符里的空格是 IKA 手册原文写法
    /// 接收: 数值 + " " + 通道号 + "\r\n"        ← 所以读数要砍掉尾部两个字符
    /// 例:  发 "IN_PV_1 \r \n"  收 "25.5 1\r\n"  → 外置温度 25.5 ℃
    ///
    /// ── 一个重要限制 ────────────────────────────────────────────────────
    /// 这台设备没有"查询当前是否在加热/搅拌"的命令(PyLabware 原文注释也是这么说的)。
    /// 所以开关状态只能由驱动自己记住上一次下发的值 —— 如果有人在面板上手动按了停止,
    /// 软件这边是看不出来的。判断"实际在不在转"要看转速读数而不是开关状态。
    /// </summary>
    [Export(typeof(IDevice))]
    public class MagneticStirrer_IKA_RCT : Device, IDeviceScreen
    {
        private readonly ILogService _logger;
        private readonly AsciiLineProtocol _proto;

        // ── 命令表(照抄自 PyLabware ika_rct_digital.py)──────────────────
        // 读:IN_PV_x = 实测值(process value),IN_SP_x = 设定值(setpoint)
        private const string CmdGetName = "IN_NAME";     // 设备名,用于连通性检查
        private const string CmdGetTempExt = "IN_PV_1";  // 外置温度探头 ℃
        private const string CmdGetTempInt = "IN_PV_2";  // 加热盘内置温度 ℃
        private const string CmdGetSpeed = "IN_PV_4";    // 当前转速 rpm
        private const string CmdGetVisc = "IN_PV_5";     // 粘度趋势值(无量纲)
        private const string CmdGetTempSet = "IN_SP_1";  // 温度设定值 ℃
        private const string CmdGetSafeTempSet = "IN_SP_3"; // 安全温度设定值 ℃
        private const string CmdGetSpeedSet = "IN_SP_4"; // 转速设定值 rpm
        // 写:OUT_SP_x = 下发设定值
        private const string CmdSetTemp = "OUT_SP_1";
        private const string CmdSetSpeed = "OUT_SP_4";
        private const string CmdStartHeat = "START_1";
        private const string CmdStopHeat = "STOP_1";
        private const string CmdStartStir = "START_4";
        private const string CmdStopStir = "STOP_4";
        // 配置
        private const string CmdSetModeA = "SET_MODE_A"; // 正常操作模式
        private const string CmdReset = "RESET";         // 复位并交还本地控制

        /// <summary>GET_NAME 的期望应答,用来确认接的确实是这台设备。</summary>
        private const string ExpectedName = "RCT digital";

        // 手册给的量程,超出范围设备会拒绝
        private const double TempMin = 20, TempMax = 310;
        private const double SpeedMin = 0, SpeedMax = 1500;

        // ── 最近一次读数/下发值 ──
        // 开关状态只能本地记:这台设备没有查询开关状态的命令
        private double _lastExtTemp;
        private double _lastIntTemp;
        private double _lastTempSv;
        private int _lastSpeed;
        private int _lastSpeedSv;
        private bool _lastHeatOn;
        private bool _lastStirOn;

        // ── 模拟模式状态 ──
        private double _simTempSv = 60;
        private double _simExtTemp = 25;
        private double _simSpeedSv = 300;
        private double _simSpeed;
        private bool _simHeatOn;
        private bool _simStirOn;

        public MagneticStirrer_IKA_RCT()
        {
            _logger = LogManager.GetLogger<MagneticStirrer_IKA_RCT>();

            DeviceId = "IKA0001";
            Name = "磁力搅拌(IKA RCT)";
            Manufacturer = "IKA RCT Digital · RS-232 ASCII";
            // 复用既有的磁力搅拌器画布控件:同一类设备,视觉一致,而且状态名对得上,
            // 不需要在 ProcessDesignerView / DeviceToControlConverter 里新增任何集成点。
            ImageLocation = "CUSTOM_CONTROL:MagneticStirrerControl";
            Category = DeviceCategories.PublicWorks;
            ComId = "IKA0001";
            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();
            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            Parameters.Variables.Add(new StringParameter("DeviceId", "IKA0001", "设备ID")
            {
                Options = new ObservableCollection<string>() { "IKA0001", "IKA0002", "IKA0003", "IKA0004", "IKA0005" },
            });

            // IKA RCT Digital 是 9600/7/Even/1,不是平台默认的 8N1
            DeviceParameterOptions.AddSerialParameters(Parameters,
                baudRate: "9600", parity: "Even", dataBits: "7", stopBits: "One");

            _proto = new AsciiLineProtocol(GetTransportAsync, msg => _logger?.LogDebug(msg))
            {
                // 结束符里的空格照抄 IKA 手册。PyLabware 原注释标了"不确定空格是否必要",
                // 真机上如果不通,先把这里改成 "\r\n" 试。
                CommandTerminator = " \r \n",
                ReplyTerminator = "\r\n",
                ArgsDelimiter = " ",
                TimeoutMs = 3000,
            };

            InitializeCommands();
        }

        /// <summary>
        /// 复用既有的磁力搅拌器设备屏。
        /// 输出参数名与 MagneticStirrer_ModbusRTU 保持一致,所以那块屏不用改就能用。
        /// 代价:屏上的报警灯与定时器这台设备没有,会一直是灭的/不走字。
        /// </summary>
        public string ScreenKey => "MagneticStirrerScreen";

        /// <summary>可监控参数白名单。键名与 MagneticStirrer_ModbusRTU 一致,
        /// 这样两种品牌的搅拌器在监控图表和配方里可以互换。</summary>
        public override IReadOnlyList<CollectableParameter> CollectableParameters { get; } = new[]
        {
            new CollectableParameter("ExternalTemperature", "外置温度", "℃", source: "获取所有数据", isKey: true),
            new CollectableParameter("InternalTemperature", "内置温度", "℃", source: "获取所有数据"),
            new CollectableParameter("SpeedMeasured", "速度测量值", "RPM", source: "获取所有数据"),
            new CollectableParameter("TemperatureSV", "温度设定值", "℃", source: "获取所有数据"),
            new CollectableParameter("SpeedSV", "速度设定值", "RPM", source: "获取所有数据"),
        };

        public override int AutoCollectIntervalMs => 2000;

        /// <summary>ASCII 串口设备也能挂在 ZLAN 串口服务器后面。</summary>
        public override bool SupportsZLanGateway => true;

        private void InitializeCommands()
        {
            // ════════════ 温度控制(设定值 + 启停,与 MagneticStirrer_ModbusRTU 同名同参)════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "温度控制",
                HelpText = "下发温度设定值(OUT_SP_1)并启停加热(START_1/STOP_1)。量程 20~310 ℃。" +
                           "注意本机无法回读加热开关状态,软件显示的开关是上次下发值;" +
                           "有人在面板上手动停机时软件看不出来,请以温度读数为准。",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetTemperature", 0, 310, 0, "温度设定值", true, "℃"),
                        new BooleanParameter("TemperatureSwitch", false, "温度开关(关=停止, 开=启动)", true)
                    }
                },
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTemperature", 0, 310, 0, "设定的温度", true, "℃"),
                        new BooleanParameter("SetSwitch", false, "设定的温度开关", true)
                    }
                },

                AsyncAction = WrapAction("温度控制", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double target = AsciiLineProtocol.Clamp(
                        parameter.GetValue<double>("TargetTemperature"), TempMin, TempMax);
                    bool onOff = parameter.GetValue<bool>("TemperatureSwitch");
                    bool success;

                    OnStateChanged("CommandState_温度控制", CommandExecutionState.Running);

                    if (IsSimulationMode)
                    {
                        await Task.Delay(300);
                        _simTempSv = target;
                        _simHeatOn = onOff;
                        success = true;
                        InfoLog($"模拟模式温度控制:设定 {target}℃,{(onOff ? "启动" : "停止")}");
                    }
                    else
                    {
                        // 设定值是整数命令(手册 type:int),先下发再启停
                        bool ok1 = await _proto.SendAsync(CmdSetTemp, AsciiLineProtocol.Format(target));
                        bool ok2 = await _proto.SendAsync(onOff ? CmdStartHeat : CmdStopHeat);
                        success = ok1 && ok2;
                        InfoLog($"温度控制:设定 {target}℃,{(onOff ? "启动" : "停止")},结果 {success}");
                    }

                    if (success)
                    {
                        _lastTempSv = target;
                        _lastHeatOn = onOff;
                        OnStateChanged("TemperatureSV", target);
                        OnStateChanged("TemperatureSwitch", onOff);
                    }
                    OnStateChanged("CommandState_温度控制",
                        success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new BooleanParameter("Success", success, "设置成功", true),
                            new NumberParameter("SetTemperature", 0, 310, target, "设定的温度", true, "℃"),
                            new BooleanParameter("SetSwitch", onOff, "设定的温度开关", true)
                        }
                    };
                })
            });

            // ════════════ 搅拌控制 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "搅拌控制",
                HelpText = "下发转速设定值(OUT_SP_4)并启停搅拌(START_4/STOP_4)。量程 0~1500 rpm。" +
                           "本机没有定时功能,配方里传的时间参数会被忽略(保留入参是为了与 Modbus 版磁力搅拌器互换)。",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetSpeed", 0, 1500, 0, "速度设定值", true, "RPM"),
                        new BooleanParameter("SpeedSwitch", false, "速度开关(关=停止, 开=启动)", true),
                        new NumberParameter("TimeHour", 0, 99, 0, "时间设定-小时(本机不支持,忽略)", true, "小时"),
                        new NumberParameter("TimeMinute", 0, 59, 0, "时间设定-分钟(本机不支持,忽略)", true, "分钟"),
                        new NumberParameter("TimeSecond", 0, 59, 0, "时间设定-秒(本机不支持,忽略)", true, "秒")
                    }
                },
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetSpeed", 0, 1500, 0, "设定的速度", true, "RPM"),
                        new BooleanParameter("SetSwitch", false, "设定的速度开关", true)
                    }
                },

                AsyncAction = WrapAction("搅拌控制", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double target = AsciiLineProtocol.Clamp(
                        parameter.GetValue<double>("TargetSpeed"), SpeedMin, SpeedMax);
                    bool onOff = parameter.GetValue<bool>("SpeedSwitch");
                    bool success;

                    OnStateChanged("CommandState_搅拌控制", CommandExecutionState.Running);

                    if (IsSimulationMode)
                    {
                        await Task.Delay(300);
                        _simSpeedSv = target;
                        _simStirOn = onOff;
                        success = true;
                        InfoLog($"模拟模式搅拌控制:{target} rpm,{(onOff ? "启动" : "停止")}");
                    }
                    else
                    {
                        bool ok1 = await _proto.SendAsync(CmdSetSpeed, AsciiLineProtocol.Format(target));
                        bool ok2 = await _proto.SendAsync(onOff ? CmdStartStir : CmdStopStir);
                        success = ok1 && ok2;
                        InfoLog($"搅拌控制:{target} rpm,{(onOff ? "启动" : "停止")},结果 {success}");
                    }

                    if (success)
                    {
                        _lastSpeedSv = (int)Math.Round(target);
                        _lastStirOn = onOff;
                        OnStateChanged("SpeedSV", target);
                        OnStateChanged("SpeedSwitch", onOff);
                    }
                    OnStateChanged("CommandState_搅拌控制",
                        success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new BooleanParameter("Success", success, "设置成功", true),
                            new NumberParameter("SetSpeed", 0, 1500, target, "设定的速度", true, "RPM"),
                            new BooleanParameter("SetSwitch", onOff, "设定的速度开关", true)
                        }
                    };
                })
            });

            // ════════════ 获取所有数据 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "获取所有数据",
                HelpText = "依次查询外置温度、内置温度、转速、粘度趋势与三个设定值。" +
                           "本机是一问一答的行协议,没有批量读,所以一轮 7 帧;" +
                           "监控间隔别设太短(默认 2 秒)。",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("ExternalTemperature", -30, 430, 0, "外置温度", true, "℃"),
                        new NumberParameter("InternalTemperature", -30, 630, 0, "内置温度", true, "℃"),
                        new NumberParameter("SpeedMeasured", 0, 1500, 0, "速度测量值", true, "RPM"),
                        new NumberParameter("TemperatureSV", 0, 310, 0, "温度设定值", true, "℃"),
                        new NumberParameter("SpeedSV", 0, 1500, 0, "速度设定值", true, "RPM"),
                        new NumberParameter("SafeTemperature", 0, 400, 0, "安全温度设定值", true, "℃"),
                        new NumberParameter("Viscosity", 0, 100000, 0, "粘度趋势值", true),
                        new BooleanParameter("TemperatureSwitch", false, "温度开关(本地记录)", true),
                        new BooleanParameter("SpeedSwitch", false, "速度开关(本地记录)", true)
                    }
                },

                AsyncAction = WrapAction("获取所有数据", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");

                    double extTemp, intTemp, tempSv, safeTemp, visc;
                    int speed, speedSv;

                    if (IsSimulationMode)
                    {
                        await Task.Delay(200);
                        var rnd = new Random();
                        // 温度:开则向设定值一阶逼近,关则向室温回落
                        if (_simHeatOn) _simExtTemp += (_simTempSv - _simExtTemp) * 0.12 + (rnd.NextDouble() - 0.5) * 0.4;
                        else _simExtTemp += (25 - _simExtTemp) * 0.05 + (rnd.NextDouble() - 0.5) * 0.2;
                        // 转速:开则逼近设定,关则减速停机
                        if (_simStirOn) _simSpeed += (_simSpeedSv - _simSpeed) * 0.35 + (rnd.NextDouble() - 0.5) * 6;
                        else _simSpeed += (0 - _simSpeed) * 0.4;
                        if (_simSpeed < 1) _simSpeed = 0;

                        extTemp = Math.Round(_simExtTemp, 1);
                        intTemp = Math.Round(_simExtTemp + 3.5, 1);
                        speed = (int)Math.Round(_simSpeed);
                        tempSv = _simTempSv;
                        speedSv = (int)Math.Round(_simSpeedSv);
                        safeTemp = 320;
                        visc = _simStirOn ? 40 + rnd.NextDouble() * 5 : 0;
                        _lastHeatOn = _simHeatOn;
                        _lastStirOn = _simStirOn;
                    }
                    else
                    {
                        // 应答形如 "25.5 1" —— 尾部是传感器通道号不是数据,砍掉最后两个字符。
                        // 这就是 PyLabware 命令表里 {"parser": slicer, "args": [-2]} 的含义。
                        extTemp = await ReadValueAsync(CmdGetTempExt, _lastExtTemp);
                        intTemp = await ReadValueAsync(CmdGetTempInt, _lastIntTemp);
                        speed = (int)Math.Round(await ReadValueAsync(CmdGetSpeed, _lastSpeed));
                        tempSv = await ReadValueAsync(CmdGetTempSet, _lastTempSv);
                        speedSv = (int)Math.Round(await ReadValueAsync(CmdGetSpeedSet, _lastSpeedSv));
                        safeTemp = await ReadValueAsync(CmdGetSafeTempSet, 0);
                        visc = await ReadValueAsync(CmdGetVisc, 0);

                        InfoLog($"读数:外置 {extTemp}℃ 内置 {intTemp}℃ 转速 {speed}rpm " +
                                $"温度设定 {tempSv}℃ 转速设定 {speedSv}rpm");
                    }

                    PushReadingsToUI(extTemp, tempSv, speed, speedSv);

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new NumberParameter("ExternalTemperature", -30, 430, extTemp, "外置温度", true, "℃"),
                            new NumberParameter("InternalTemperature", -30, 630, intTemp, "内置温度", true, "℃"),
                            new NumberParameter("SpeedMeasured", 0, 1500, speed, "速度测量值", true, "RPM"),
                            new NumberParameter("TemperatureSV", 0, 310, tempSv, "温度设定值", true, "℃"),
                            new NumberParameter("SpeedSV", 0, 1500, speedSv, "速度设定值", true, "RPM"),
                            new NumberParameter("SafeTemperature", 0, 400, safeTemp, "安全温度设定值", true, "℃"),
                            new NumberParameter("Viscosity", 0, 100000, visc, "粘度趋势值", true),
                            new BooleanParameter("TemperatureSwitch", _lastHeatOn, "温度开关(本地记录)", true),
                            new BooleanParameter("SpeedSwitch", _lastStirOn, "速度开关(本地记录)", true)
                        }
                    };
                })
            });

            // ════════════ 复位 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "复位",
                HelpText = "发 RESET,设备停止所有输出并交还本地面板控制权。" +
                           "远程控制结束时应当调用,否则面板会一直锁在远程模式。",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "复位成功", true)
                    }
                },

                AsyncAction = WrapAction("复位", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    bool success;

                    if (IsSimulationMode)
                    {
                        await Task.Delay(200);
                        _simHeatOn = _simStirOn = false;
                        success = true;
                    }
                    else
                    {
                        success = await _proto.SendAsync(CmdReset);
                    }

                    if (success)
                    {
                        _lastHeatOn = _lastStirOn = false;
                        OnStateChanged("TemperatureSwitch", false);
                        OnStateChanged("SpeedSwitch", false);
                    }

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new BooleanParameter("Success", success, "复位成功", true)
                        }
                    };
                })
            });
        }

        /// <summary>
        /// 读一个 IN_PV_x / IN_SP_x 数值。
        /// 应答是 "数值 通道号",砍掉尾部两个字符再解析;解析不出来时返回上一次的值,
        /// 让轮询链路上偶发的一帧脏数据不至于把曲线拉出一个尖峰。
        /// </summary>
        private async Task<double> ReadValueAsync(string command, double fallback)
        {
            try
            {
                string reply = await _proto.QueryAsync(command);
                string body = AsciiLineProtocol.Slice(reply, null, -2);
                return AsciiLineProtocol.ParseDouble(body, fallback);
            }
            catch (TimeoutException)
            {
                // 单个量读超时不该让整轮采集失败
                _logger?.LogWarning("读取 {Command} 超时,沿用上次值 {Fallback}", command, fallback);
                return fallback;
            }
        }

        /// <summary>把本轮读数推给画布控件。只推变化项,避免每 2 秒刷一遍全量。</summary>
        private void PushReadingsToUI(double extTemp, double tempSv, int speed, int speedSv)
        {
            if (Math.Abs(extTemp - _lastExtTemp) >= 0.05)
            {
                _lastExtTemp = extTemp;
                OnStateChanged("ExternalTemperature", extTemp);
            }
            if (Math.Abs(tempSv - _lastTempSv) >= 0.05)
            {
                _lastTempSv = tempSv;
                OnStateChanged("TemperatureSV", tempSv);
            }
            if (speed != _lastSpeed)
            {
                _lastSpeed = speed;
                OnStateChanged("SpeedMeasured", (double)speed);
            }
            if (speedSv != _lastSpeedSv)
            {
                _lastSpeedSv = speedSv;
                OnStateChanged("SpeedSV", (double)speedSv);
            }
        }

        /// <summary>当前状态快照,供画布控件与设备屏初始化显示。</summary>
        public override DeviceState GetCurrentState()
        {
            return new DeviceState
            {
                States = new Dictionary<string, object>
                {
                    ["ExternalTemperature"] = _lastExtTemp,
                    ["InternalTemperature"] = _lastIntTemp,
                    ["TemperatureSV"] = _lastTempSv,
                    ["SpeedMeasured"] = (double)_lastSpeed,
                    ["SpeedSV"] = (double)_lastSpeedSv,
                    ["TemperatureSwitch"] = _lastHeatOn,
                    ["SpeedSwitch"] = _lastStirOn
                }
            };
        }

        #region 连接

        /// <summary>Direct 模式:串口即用即建,这里发一次 IN_NAME 确认接的是不是这台设备。</summary>
        protected override async Task<bool> OnConnectAsync()
        {
            if (IsSimulationMode) return true;

            try
            {
                string name = await _proto.QueryAsync(CmdGetName);
                bool match = name != null && name.Trim().StartsWith(ExpectedName, StringComparison.OrdinalIgnoreCase);
                if (!match)
                {
                    // 不匹配不直接判失败:IKA 同系列固件回的名字略有差异,
                    // 拦死会让能用的设备连不上。记一条警告,由现场判断。
                    _logger?.LogWarning("设备名不匹配:期望以 \"{Expected}\" 开头,实际 \"{Actual}\"", ExpectedName, name);
                }
                InfoLog($"串口连接成功,设备名 \"{name}\"");
                return true;
            }
            catch (Exception e)
            {
                ErrorLog("串口连接失败", e);
                return false;
            }
        }

        /// <summary>ZLanGateway 模式:同样用 IN_NAME 探活。</summary>
        protected override async Task<bool> OnConnectViaGatewayAsync() => await OnConnectAsync();

        protected override Task<bool> OnDisconnectAsync()
        {
            InfoLog("断开 IKA RCT Digital");
            return Task.FromResult(true);
        }

        /// <summary>
        /// 取 Transport。关键是 Framing = AsciiLine —— 不设的话会走 Modbus 分帧,
        /// 那条路会按"前 3 字节是站号/功能码/长度"去推算帧长,对文本应答完全对不上。
        /// </summary>
        private Task<IDeviceTransport> GetTransportAsync()
        {
            var serialConfig = new SerialPortConfig
            {
                PortName = Parameters.GetValue<string>("串口号") ?? "COM1",
                BaudRate = int.Parse(Parameters.GetValue<string>("波特率") ?? "9600"),
                Parity = Parameters.GetValue<string>("校验位") ?? "Even",
                DataBits = int.Parse(Parameters.GetValue<string>("数据位") ?? "7"),
                StopBits = Parameters.GetValue<string>("停止位") ?? "One",
                ReadTimeoutMs = 3000,
                WriteTimeoutMs = 3000,
                Framing = SerialFraming.AsciiLine,
                ReplyTerminator = "\r\n",
            };
            return CreateTransportAsync(serialConfig);
        }

        #endregion

        #region 命令包装(日志 + 异常封装)

        private Func<DeviceParameters, Task<DeviceParameters>> WrapAction(
            string commandName, Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return async (DeviceParameters parameters) =>
            {
                _logger.LogInformation($"[IKA RCT 命令-开始] Device={Name} Command={commandName}");
                var sw = Stopwatch.StartNew();
                try
                {
                    var output = await innerAsync(parameters);
                    sw.Stop();
                    _logger.LogInformation($"[IKA RCT 命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[IKA RCT 命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        #endregion
    }
}
