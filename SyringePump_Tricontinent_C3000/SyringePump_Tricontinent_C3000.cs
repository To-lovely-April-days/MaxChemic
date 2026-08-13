using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;

namespace SyringePump_Tricontinent_C3000
{
    /// <summary>
    /// Tricontinent C3000 注射泵(带集成分配阀)驱动,DT 协议,RS-232/RS-485 ASCII。
    ///
    /// ── 协议来源 ────────────────────────────────────────────────────────
    /// 命令表移植自 PyLabware(Cronin Group,MIT License,Copyright (c) 2021),
    /// 文件 PyLabware/devices/tricontinent_c3000.py,原始依据是 Tricontinent 操作手册 8694-12 E。
    /// 只搬协议,不引入任何运行时依赖。
    ///
    /// ── 串口设置 ────────────────────────────────────────────────────────
    /// 9600, 8 数据位, 无校验, 1 停止位。
    ///
    /// ── 报文格式(DT 协议)──────────────────────────────────────────────
    /// 发送: "/" + 站号字符 + 命令串 + ["R"] + "\r\n"
    ///        站号字符不是数字,是泵体上拨码开关位置的映射:拨 0 → '1',拨 9 → ':',拨 A → ';' ……
    ///        结尾的 "R" 是"立即执行",不带 R 的命令只是排进缓冲区等后续 R 一起跑。
    ///        查询类命令(? 开头)不加 R —— 它们本来就是立即应答的。
    /// 接收: "/0" + 状态字节 + 数据 + "\u0003\r\n"
    ///        状态字节是一个 ASCII 字符,按位取含义:
    ///          bit5 = 1 空闲 / 0 忙
    ///          低 4 位 = 错误码(0 表示无错)
    ///        所以每一次应答都自带一次健康检查,这一点比 Modbus 设备友好得多。
    ///
    /// ── 体积与步数的换算 ────────────────────────────────────────────────
    /// 泵本身只认"步数"(increments),不认体积。满行程步数由分辨率模式决定:
    ///     N0 普通模式      = 3000 步(上电默认)
    ///     N1/N2 微步模式   = 24000 步
    /// 于是 步数 = 体积 / 注射器规格 × 满行程步数。
    /// 注射器规格必须与实际装的针筒一致,配错了泵不会报错,只会默默打错量 ——
    /// 这是这类泵现场最容易出的事故,所以规格做成了必填参数而不是写死。
    ///
    /// ── 阀位表示法 ──────────────────────────────────────────────────────
    /// Y 型阀 / 90°阀 / T 型阀用 IOBE 表示法:I=进样口, O=出样口, B=旁通, E=额外口。
    /// 6 通分配阀用 I1..I6 / O1..O6:I 是顺时针转过去,O 是逆时针转过去(选近的那个方向省时间)。
    /// 3 通分配阀两种都可能,取决于出厂 Uxx 配置。
    /// </summary>
    [Export(typeof(IDevice))]
    public class SyringePump_Tricontinent_C3000 : Device
    {
        private readonly ILogService _logger;
        private readonly AsciiLineProtocol _proto;

        /// <summary>
        /// 拨码开关位置 → 串口上的站号字符。照抄手册 p.23,注意 F 位不可用。
        /// 这层映射是 DT 协议特有的坑:面板上拨 "0",报文里要写 '1'。
        /// </summary>
        private static readonly Dictionary<string, string> SwitchAddresses = new()
        {
            ["0"] = "1",
            ["1"] = "2",
            ["2"] = "3",
            ["3"] = "4",
            ["4"] = "5",
            ["5"] = "6",
            ["6"] = "7",
            ["7"] = "8",
            ["8"] = "9",
            ["9"] = ":",
            ["A"] = ";",
            ["B"] = "<",
            ["C"] = "=",
            ["D"] = ">",
            ["E"] = "?",
            ["广播"] = "-",
        };

        /// <summary>
        /// 状态字节低 4 位的错误码,照抄手册 p.90 与 PyLabware 的 ERROR_CODES。
        /// </summary>
        private static readonly Dictionary<int, string> ErrorCodes = new()
        {
            [0b0000] = "无错误",
            [0b0001] = "初始化失败",
            [0b0010] = "无效命令",
            [0b0011] = "无效操作数",
            [0b0100] = "校验和错误",
            [0b0110] = "EEPROM 故障",
            [0b0111] = "设备未初始化",
            [0b1000] = "CAN 总线故障",
            [0b1001] = "柱塞过载",
            [0b1010] = "阀过载",
            [0b1011] = "当前阀位不允许柱塞移动(先切阀位)",
            [0b1111] = "命令溢出",
        };

        /// <summary>分辨率模式 → 满行程步数。</summary>
        private static int StrokeIncrements(string mode) => mode switch
        {
            "N1" => 24000,
            "N2" => 24000,
            _ => 3000,      // N0,上电默认
        };

        // ── 命令表(照抄自 PyLabware tricontinent_c3000.py)────────────────
        private const string CmdInitAllCw = "Z";     // 初始化柱塞与阀,阀位从注射器侧顺时针编号
        private const string CmdInitAllCcw = "Y";    // 同上,逆时针编号
        private const string CmdMoveAbs = "A";       // 柱塞移到绝对步数
        private const string CmdPickupRel = "P";     // 相对吸液(步数)
        private const string CmdDispenseRel = "D";   // 相对排液(步数)
        private const string CmdValveIn = "I";       // 阀转到进样口 / 顺时针第 n 位
        private const string CmdValveOut = "O";      // 阀转到出样口 / 逆时针第 n 位
        private const string CmdValveBypass = "B";   // 阀转到旁通位
        private const string CmdValveExtra = "E";    // 阀转到额外位
        private const string CmdTerminate = "T";     // 立即终止当前动作
        private const string CmdSetSpeedCode = "S";  // 速度档位 0~40
        private const string CmdSetResolution = "N"; // 分辨率模式
        private const string CmdGetStatus = "?Q";    // 查状态(只看状态字节)
        private const string CmdGetPlungerPos = "?"; // 查柱塞绝对步数
        private const string CmdGetValvePos = "?6";  // 查阀位
        private const string CmdGetInitStatus = "?19"; // 查是否已初始化
        private const string CmdGetFwVersion = "?23";  // 查固件版本

        /// <summary>忙等待的上限。柱塞满行程最慢档大约二十几秒,留足余量。</summary>
        private const int BusyWaitTimeoutMs = 60000;
        private const int BusyPollIntervalMs = 200;

        // ── 最近一次读数 ──
        private int _lastPlungerSteps;
        private double _lastVolume;
        private string _lastValvePos = "";
        private bool _lastIdle = true;
        private bool _lastInitialized;
        private string _lastError = "无错误";

        // ── 模拟模式状态 ──
        private int _simPlungerSteps;
        private string _simValvePos = "i";
        private bool _simInitialized;

        public SyringePump_Tricontinent_C3000()
        {
            _logger = LogManager.GetLogger<SyringePump_Tricontinent_C3000>();

            DeviceId = "C3000-01";
            Name = "注射泵(Tricontinent C3000)";
            Manufacturer = "Tricontinent C3000 · DT Protocol";
            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/SyringePumpC3000.png";
            Category = DeviceCategories.Pumps;
            ComId = "C3000-01";
            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();
            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            Parameters.Variables.Add(new StringParameter("DeviceId", "C3000-01", "设备ID")
            {
                Options = new ObservableCollection<string>() { "C3000-01", "C3000-02", "C3000-03", "C3000-04" },
            });

            // 拨码地址:泵体侧面那个旋钮的刻度,不是站号字符本身
            Parameters.Variables.Add(new StringParameter("拨码地址", "0",
                "泵体上旋转拨码开关的刻度(0~9/A~E)。F 位不可用。多台泵串在同一条 485 上时必须各不相同。")
            {
                Options = new ObservableCollection<string>()
                    { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "A", "B", "C", "D", "E", "广播" },
            });

            // 注射器规格:必须与实际装的针筒一致,否则打量全错且泵不会报错
            Parameters.Variables.Add(new StringParameter("注射器规格", "5",
                "实际安装的针筒容量(mL)。这个值只用于体积↔步数换算,配错泵不会报错,只会默默打错量。")
            {
                Options = new ObservableCollection<string>()
                    { "0.05", "0.1", "0.25", "0.5", "1", "2.5", "5", "10", "25", "50" },
            });

            Parameters.Variables.Add(new StringParameter("分辨率模式", "N0",
                "N0=普通模式满行程 3000 步(上电默认);N1/N2=微步模式 24000 步。改这里只影响换算,真要切模式请用「设置分辨率」命令。")
            {
                Options = new ObservableCollection<string>() { "N0", "N1", "N2" },
            });

            // C3000 是 9600/8/None/1
            DeviceParameterOptions.AddSerialParameters(Parameters,
                baudRate: "9600", parity: "None", dataBits: "8", stopBits: "One");

            _proto = new AsciiLineProtocol(GetTransportAsync, msg => _logger?.LogDebug(msg))
            {
                // 前缀在每条命令下发前按拨码地址刷新(见 SyncAddress)
                CommandPrefix = "/1",
                // 结束符里不含 "R":要不要立即执行由每条命令自己决定,查询类命令加了 R 反而多一次空执行。
                CommandTerminator = "\r\n",
                // 前缀留空,自己在 ParseReply 里校验 —— 交给 Strip 剥的话,
                // 收到一帧不以 "/0" 开头的脏数据时会把 '/' 当状态字节,解出一个莫名其妙的"命令溢出"。
                ReplyPrefix = "",
                ReplyTerminator = "\u0003\r\n",
                ArgsDelimiter = "",
                TimeoutMs = 3000,
            };

            InitializeCommands();
        }

        public override IReadOnlyList<CollectableParameter> CollectableParameters { get; } = new[]
        {
            new CollectableParameter("CurrentVolume", "当前液量", "mL", source: "获取所有数据", isKey: true),
            new CollectableParameter("PlungerSteps", "柱塞步数", "步", source: "获取所有数据"),
            new CollectableParameter("ValvePosition", "阀位", "", source: "获取所有数据"),
        };

        public override int AutoCollectIntervalMs => 2000;

        public override bool SupportsZLanGateway => true;

        private void InitializeCommands()
        {
            // ════════════ 初始化 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "初始化",
                HelpText = "上电后必须先做一次初始化(Z/Y),否则所有移动命令都会报「设备未初始化」。" +
                           "方向决定阀位编号是从注射器侧顺时针还是逆时针数;非分配阀则决定初始化后停在左位还是右位。" +
                           "初始化会把柱塞排空并回零,执行前请确认管路里的液体可以被排掉。",
                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Clockwise", true, "true=顺时针编号(Z), false=逆时针编号(Y)", true),
                        new NumberParameter("InputPort", 0, 6, 0, "初始化用的进液口(0=默认)", true),
                        new NumberParameter("OutputPort", 0, 6, 0, "初始化用的出液口(0=默认)", true)
                    }
                },
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "初始化成功", true),
                        new StringParameter("Error", "", "错误描述", true)
                    }
                },
                AsyncAction = WrapAction("初始化", async parameter =>
                {
                    SyncAddress();
                    bool cw = parameter.GetValue<bool>("Clockwise");
                    int inPort = (int)parameter.GetValue<double>("InputPort");
                    int outPort = (int)parameter.GetValue<double>("OutputPort");

                    OnStateChanged("CommandState_初始化", CommandExecutionState.Running);

                    if (IsSimulationMode)
                    {
                        await Task.Delay(1500);
                        _simInitialized = true;
                        _simPlungerSteps = 0;
                        OnStateChanged("CommandState_初始化", CommandExecutionState.Succeeded);
                        return InitResult(true, "");
                    }

                    // 参数是逗号分隔的三元组:柱塞初始化力度(留空用默认)、进液口、出液口。
                    // 非分配阀会忽略后两个。
                    string args = "," + inPort + "," + outPort;
                    var r = await SendActionAsync((cw ? CmdInitAllCw : CmdInitAllCcw) + args);

                    if (r.Ok)
                    {
                        // 初始化本身要跑十几秒,等它做完再返回,免得配方里下一步立刻打量却报「柱塞移动不允许」
                        await WaitUntilIdleAsync();
                        _lastInitialized = true;
                        _lastPlungerSteps = 0;
                    }
                    InfoLog($"初始化({(cw ? "CW" : "CCW")}):{(r.Ok ? "成功" : r.Error)}");

                    OnStateChanged("CommandState_初始化",
                        r.Ok ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);
                    return InitResult(r.Ok, r.Error);
                })
            });

            // ════════════ 阀门控制 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "阀门控制",
                HelpText = "切换集成分配阀的位置。写法有两套,取决于阀的型号:\n" +
                           "  IOBE 表示法(Y 型/90°/T 型阀):I=进样口, O=出样口, B=旁通, E=额外口\n" +
                           "  编号表示法(6 通分配阀):I1~I6 顺时针转过去,O1~O6 逆时针转过去\n" +
                           "选哪个方向不影响最终位置,只影响转过去要多久 —— 挑近的那个方向。",
                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new StringParameter("ValvePosition", "I", "阀位(I/O/B/E 或 I1~I6/O1~O6)", true)
                        {
                            Options = new ObservableCollection<string>()
                            {
                                "I", "O", "B", "E",
                                "I1", "I2", "I3", "I4", "I5", "I6",
                                "O1", "O2", "O3", "O4", "O5", "O6"
                            }
                        }
                    }
                },
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "切换成功", true),
                        new StringParameter("Error", "", "错误描述", true)
                    }
                },
                AsyncAction = WrapAction("阀门控制", async parameter =>
                {
                    SyncAddress();
                    string pos = (parameter.GetValue<string>("ValvePosition") ?? "I").Trim().ToUpperInvariant();

                    OnStateChanged("CommandState_阀门控制", CommandExecutionState.Running);

                    if (IsSimulationMode)
                    {
                        await Task.Delay(500);
                        _simValvePos = pos.ToLowerInvariant();
                        OnStateChanged("ValvePosition", pos);
                        OnStateChanged("CommandState_阀门控制", CommandExecutionState.Succeeded);
                        return InitResult(true, "");
                    }

                    string head = pos.Substring(0, 1);
                    string tail = pos.Length > 1 ? pos.Substring(1) : "";
                    string cmd = head switch
                    {
                        "I" => CmdValveIn + tail,
                        "O" => CmdValveOut + tail,
                        "B" => CmdValveBypass,
                        "E" => CmdValveExtra,
                        _ => null
                    };

                    if (cmd == null)
                    {
                        OnStateChanged("CommandState_阀门控制", CommandExecutionState.Failed);
                        return InitResult(false, $"未知阀位 <{pos}>");
                    }

                    // 阀正在转的时候再发命令会被拒,先等空闲
                    await WaitUntilIdleAsync();
                    var r = await SendActionAsync(cmd);
                    if (r.Ok)
                    {
                        await WaitUntilIdleAsync();
                        _lastValvePos = pos;
                        OnStateChanged("ValvePosition", pos);
                    }
                    InfoLog($"阀门切到 {pos}:{(r.Ok ? "成功" : r.Error)}");

                    OnStateChanged("CommandState_阀门控制",
                        r.Ok ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);
                    return InitResult(r.Ok, r.Error);
                })
            });

            // ════════════ 吸液 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "吸液",
                HelpText = "按体积相对吸液(P 命令)。体积会按「注射器规格」和「分辨率模式」换成步数。" +
                           "吸之前请确认阀位已经切到进液口,否则泵会回「当前阀位不允许柱塞移动」。" +
                           "超出剩余行程时泵会拒绝执行而不是吸一半。",
                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("Volume", 0, 50, 1, "吸液体积", true, "mL"),
                        new BooleanParameter("WaitForCompletion", true, "等动作完成后再返回", true)
                    }
                },
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "执行成功", true),
                        new NumberParameter("Steps", 0, 24000, 0, "换算出的步数", true, "步"),
                        new StringParameter("Error", "", "错误描述", true)
                    }
                },
                AsyncAction = WrapAction("吸液", MoveActionFactory(CmdPickupRel, "吸液", +1))
            });

            // ════════════ 排液 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "排液",
                HelpText = "按体积相对排液(D 命令)。规则与吸液相同,只是方向相反。" +
                           "排之前请确认阀位已经切到出液口。",
                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("Volume", 0, 50, 1, "排液体积", true, "mL"),
                        new BooleanParameter("WaitForCompletion", true, "等动作完成后再返回", true)
                    }
                },
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "执行成功", true),
                        new NumberParameter("Steps", 0, 24000, 0, "换算出的步数", true, "步"),
                        new StringParameter("Error", "", "错误描述", true)
                    }
                },
                AsyncAction = WrapAction("排液", MoveActionFactory(CmdDispenseRel, "排液", -1))
            });

            // ════════════ 移动到绝对位置 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "移动到绝对位置",
                HelpText = "把柱塞移到指定的绝对液量(A 命令)。0 表示排空回零。" +
                           "比「吸液/排液」更适合做定量:相对移动的误差会累积,绝对定位不会。",
                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("Volume", 0, 50, 0, "目标液量(0=排空回零)", true, "mL"),
                        new BooleanParameter("WaitForCompletion", true, "等动作完成后再返回", true)
                    }
                },
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "执行成功", true),
                        new NumberParameter("Steps", 0, 24000, 0, "换算出的步数", true, "步"),
                        new StringParameter("Error", "", "错误描述", true)
                    }
                },
                AsyncAction = WrapAction("移动到绝对位置", MoveActionFactory(CmdMoveAbs, "移动到绝对位置", 0))
            });

            // ════════════ 设置速度 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "设置速度",
                HelpText = "设置柱塞最高速度档位(S 命令,0~40)。数字越小越快:" +
                           "0 档在 N0 模式下是 6000 步/秒(约 2 秒走完满行程),40 档是 10 步/秒。上电默认 11 档。" +
                           "粘稠液体或大针筒要往高档位调,否则会失步。",
                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("SpeedCode", 0, 40, 11, "速度档位(0 最快,40 最慢)", true)
                    }
                },
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new StringParameter("Error", "", "错误描述", true)
                    }
                },
                AsyncAction = WrapAction("设置速度", async parameter =>
                {
                    SyncAddress();
                    int code = (int)AsciiLineProtocol.Clamp(parameter.GetValue<double>("SpeedCode"), 0, 40);

                    if (IsSimulationMode)
                    {
                        await Task.Delay(150);
                        return InitResult(true, "");
                    }

                    var r = await SendActionAsync(CmdSetSpeedCode + code);
                    InfoLog($"设置速度档位 {code}:{(r.Ok ? "成功" : r.Error)}");
                    return InitResult(r.Ok, r.Error);
                })
            });

            // ════════════ 设置分辨率 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "设置分辨率",
                HelpText = "切换柱塞步进模式(N 命令)。N0=3000 步满行程(上电默认),N1/N2=24000 步微步模式。" +
                           "切完记得把设备参数里的「分辨率模式」也改成一样的,否则体积换算会差 8 倍。",
                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new StringParameter("ResolutionMode", "N0", "分辨率模式", true)
                        {
                            Options = new ObservableCollection<string>() { "N0", "N1", "N2" }
                        }
                    }
                },
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new StringParameter("Error", "", "错误描述", true)
                    }
                },
                AsyncAction = WrapAction("设置分辨率", async parameter =>
                {
                    SyncAddress();
                    string mode = (parameter.GetValue<string>("ResolutionMode") ?? "N0").Trim().ToUpperInvariant();
                    if (mode != "N0" && mode != "N1" && mode != "N2")
                        return InitResult(false, $"未知分辨率模式 <{mode}>");

                    if (IsSimulationMode)
                    {
                        await Task.Delay(150);
                        return InitResult(true, "");
                    }

                    // 命令是 N + 模式号,拼出来正好就是 "N0"/"N1"/"N2"
                    var r = await SendActionAsync(CmdSetResolution + mode.Substring(1));
                    if (r.Ok)
                    {
                        Parameters.SetValue("分辨率模式", mode);
                        InfoLog($"分辨率切到 {mode},满行程 {StrokeIncrements(mode)} 步");
                    }
                    return InitResult(r.Ok, r.Error);
                })
            });

            // ════════════ 停止 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "停止",
                HelpText = "立即终止当前动作(T 命令),已排进缓冲区的后续命令也一并作废。" +
                           "急停用。停完柱塞停在半路,后续要么继续绝对定位,要么重新初始化。",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "已停止", true),
                        new StringParameter("Error", "", "错误描述", true)
                    }
                },
                AsyncAction = WrapAction("停止", async parameter =>
                {
                    SyncAddress();

                    if (IsSimulationMode)
                    {
                        await Task.Delay(100);
                        return InitResult(true, "");
                    }

                    var r = await SendActionAsync(CmdTerminate);
                    InfoLog($"停止:{(r.Ok ? "成功" : r.Error)}");
                    return InitResult(r.Ok, r.Error);
                })
            });

            // ════════════ 获取所有数据 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "获取所有数据",
                HelpText = "依次查柱塞位置(?)、阀位(?6)、初始化状态(?19)与状态字节(?Q)。" +
                           "状态字节每一帧应答都带,所以「是否忙」「有没有报错」是顺带拿到的,不额外占报文。",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("CurrentVolume", 0, 50, 0, "当前液量", true, "mL"),
                        new NumberParameter("PlungerSteps", 0, 24000, 0, "柱塞步数", true, "步"),
                        new StringParameter("ValvePosition", "", "阀位", true),
                        new BooleanParameter("IsIdle", true, "空闲(状态字节 bit5)", true),
                        new BooleanParameter("IsInitialized", false, "已初始化", true),
                        new StringParameter("ErrorText", "", "错误描述", true),
                        new BooleanParameter("HasError", false, "存在错误", true)
                    }
                },
                AsyncAction = WrapAction("获取所有数据", async parameter =>
                {
                    SyncAddress();
                    double stroke = StrokeIncrements(GetResolutionMode());
                    double syringeMl = GetSyringeVolumeMl();

                    if (IsSimulationMode)
                    {
                        await Task.Delay(150);
                        _lastPlungerSteps = _simPlungerSteps;
                        _lastValvePos = _simValvePos.ToUpperInvariant();
                        _lastIdle = true;
                        _lastInitialized = _simInitialized;
                        _lastError = "无错误";
                    }
                    else
                    {
                        // 这几处只看数据段不看 Ok:泵带着错误标志时照样会回真实的位置数据,
                        // 拿 Ok 当门槛的话,一旦出现"未初始化"这类持续性错误,界面上的位置就永远停在旧值了。
                        var pos = await QuerySafeAsync(CmdGetPlungerPos);
                        if (!string.IsNullOrWhiteSpace(pos.Data))
                            _lastPlungerSteps = AsciiLineProtocol.ParseInt(pos.Data, _lastPlungerSteps);

                        var valve = await QuerySafeAsync(CmdGetValvePos);
                        if (!string.IsNullOrWhiteSpace(valve.Data))
                            _lastValvePos = valve.Data.Trim().ToUpperInvariant();

                        var init = await QuerySafeAsync(CmdGetInitStatus);
                        if (!string.IsNullOrWhiteSpace(init.Data))
                            _lastInitialized = init.Data.Trim() == "1";

                        // ?Q 只是为了刷新状态字节:忙/闲与错误码都在应答头里
                        var st = await QuerySafeAsync(CmdGetStatus);
                        _lastIdle = st.Idle;
                        _lastError = st.Error;

                        InfoLog($"读数:柱塞 {_lastPlungerSteps} 步 阀位 {_lastValvePos} " +
                                $"{(_lastIdle ? "空闲" : "忙")} {_lastError}");
                    }

                    double volume = Math.Round(_lastPlungerSteps / stroke * syringeMl, 4);
                    if (Math.Abs(volume - _lastVolume) > 1e-6)
                    {
                        _lastVolume = volume;
                        OnStateChanged("CurrentVolume", volume);
                    }

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new NumberParameter("CurrentVolume", 0, 50, volume, "当前液量", true, "mL"),
                            new NumberParameter("PlungerSteps", 0, 24000, _lastPlungerSteps, "柱塞步数", true, "步"),
                            new StringParameter("ValvePosition", _lastValvePos, "阀位", true),
                            new BooleanParameter("IsIdle", _lastIdle, "空闲(状态字节 bit5)", true),
                            new BooleanParameter("IsInitialized", _lastInitialized, "已初始化", true),
                            new StringParameter("ErrorText", _lastError, "错误描述", true),
                            new BooleanParameter("HasError", _lastError != "无错误", "存在错误", true)
                        }
                    };
                })
            });
        }

        /// <summary>
        /// 吸液/排液/绝对移动三条命令的实现只差一个命令字母和方向,抽出来共用。
        /// </summary>
        /// <param name="command">P / D / A</param>
        /// <param name="label">日志用的中文名</param>
        /// <param name="direction">模拟模式下柱塞步数的变化方向:+1 吸, -1 排, 0 绝对定位</param>
        private Func<DeviceParameters, Task<DeviceParameters>> MoveActionFactory(
            string command, string label, int direction)
        {
            return async parameter =>
            {
                SyncAddress();
                double stroke = StrokeIncrements(GetResolutionMode());
                double syringeMl = GetSyringeVolumeMl();
                double volume = Math.Max(0, parameter.GetValue<double>("Volume"));
                bool wait = parameter.GetValue<bool>("WaitForCompletion");

                // 体积 → 步数。四舍五入到整步:泵不接受小数步。
                int steps = (int)Math.Round(volume / syringeMl * stroke);
                steps = (int)AsciiLineProtocol.Clamp(steps, 0, stroke);

                OnStateChanged($"CommandState_{label}", CommandExecutionState.Running);

                if (IsSimulationMode)
                {
                    await Task.Delay(600);
                    _simPlungerSteps = direction switch
                    {
                        +1 => Math.Min((int)stroke, _simPlungerSteps + steps),
                        -1 => Math.Max(0, _simPlungerSteps - steps),
                        _ => steps
                    };
                    OnStateChanged($"CommandState_{label}", CommandExecutionState.Succeeded);
                    return MoveResult(true, steps, "");
                }

                // 上一动作没做完就发下一条,泵会回「命令溢出」,先等空闲
                await WaitUntilIdleAsync();
                var r = await SendActionAsync(command + steps);

                if (r.Ok && wait)
                {
                    await WaitUntilIdleAsync();
                }
                InfoLog($"{label} {volume} mL({steps} 步):{(r.Ok ? "成功" : r.Error)}");

                OnStateChanged($"CommandState_{label}",
                    r.Ok ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);
                return MoveResult(r.Ok, steps, r.Error);
            };
        }

        private static DeviceParameters InitResult(bool ok, string error) => new DeviceParameters()
        {
            Variables = new ObservableCollection<ParameterBase>()
            {
                new BooleanParameter("Success", ok, "执行成功", true),
                new StringParameter("Error", error ?? "", "错误描述", true)
            }
        };

        private static DeviceParameters MoveResult(bool ok, int steps, string error) => new DeviceParameters()
        {
            Variables = new ObservableCollection<ParameterBase>()
            {
                new BooleanParameter("Success", ok, "执行成功", true),
                new NumberParameter("Steps", 0, 24000, steps, "换算出的步数", true, "步"),
                new StringParameter("Error", error ?? "", "错误描述", true)
            }
        };

        #region DT 协议收发

        /// <summary>一次 DT 交互的结果。</summary>
        private readonly struct DtReply
        {
            public bool Ok { get; init; }        // 通讯成功且状态字节里没有错误码
            public bool Idle { get; init; }      // 状态字节 bit5
            public string Data { get; init; }    // 状态字节之后的数据段
            public string Error { get; init; }   // 错误描述,无错时为 "无错误"
        }

        /// <summary>
        /// 发一条动作命令。动作命令要加 "R" 立即执行 —— 不加的话只会排进缓冲区,
        /// 泵照样回一个成功应答,但什么都不会动,这是 DT 协议最容易踩的坑。
        /// </summary>
        private Task<DtReply> SendActionAsync(string body) => ExchangeAsync(body + "R");

        /// <summary>发一条查询命令(? 开头)。查询是立即应答的,不加 R。</summary>
        private Task<DtReply> QuerySafeAsync(string body) => ExchangeAsync(body);

        private async Task<DtReply> ExchangeAsync(string body)
        {
            try
            {
                string raw = await _proto.QueryAsync(body);
                return ParseDtReply(raw);
            }
            catch (TimeoutException)
            {
                _logger?.LogWarning("命令 {Body} 无应答", body);
                return new DtReply { Ok = false, Idle = false, Data = "", Error = "无应答(检查拨码地址与接线)" };
            }
        }

        /// <summary>
        /// 拆一帧 DT 应答。到这里时结束符已经被剥掉,剩下 "/0" + 状态字节 + 数据。
        /// 前缀留给这里自己校验,是为了能把"收到脏数据"和"设备报错"区分开 ——
        /// 交给通用 Strip 剥的话,一帧不以 "/0" 开头的垃圾会被当成状态字节 '/',
        /// 解出 0x2F & 0b1111 = 0b1111 = "命令溢出",一个完全不存在的故障。
        /// </summary>
        private static DtReply ParseDtReply(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return new DtReply { Ok = false, Data = "", Error = "空应答" };

            if (!raw.StartsWith("/0", StringComparison.Ordinal))
                return new DtReply { Ok = false, Data = "", Error = $"应答格式非法 <{raw}>" };

            string body = raw.Substring(2);
            if (body.Length == 0)
                return new DtReply { Ok = false, Data = "", Error = "应答缺少状态字节" };

            int status = body[0];
            bool idle = (status & (1 << 5)) != 0;
            int code = status & 0b1111;
            string error = ErrorCodes.TryGetValue(code, out var msg) ? msg : $"未知错误码 0x{code:X}";

            return new DtReply
            {
                Ok = code == 0,
                Idle = idle,
                Data = body.Substring(1),
                Error = error
            };
        }

        /// <summary>
        /// 轮询 ?Q 直到泵空闲。对应 PyLabware 的 execute_when_ready ——
        /// 泵在忙的时候收到新命令会直接拒绝(命令溢出),所以移动类命令前后都要等。
        /// 超时后不抛异常,只记警告并放行:卡在这里会让整条配方停摆,
        /// 真出问题的话下一条命令自己会报错,信息更准确。
        /// </summary>
        private async Task WaitUntilIdleAsync()
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < BusyWaitTimeoutMs)
            {
                var r = await QuerySafeAsync(CmdGetStatus);
                _lastIdle = r.Idle;
                if (r.Idle) return;
                await Task.Delay(BusyPollIntervalMs);
            }
            _logger?.LogWarning("等待泵空闲超过 {Timeout}ms 仍未就绪,继续下发后续命令", BusyWaitTimeoutMs);
        }

        /// <summary>
        /// 按「拨码地址」参数刷新命令前缀。
        /// 每条命令执行前调一次而不是构造时定死 —— 用户可能在设备属性里改地址,
        /// 改完不该要求重启软件。命令是串行执行的,这里不加锁。
        /// </summary>
        private void SyncAddress()
        {
            ComId = Parameters.GetValue<string>("DeviceId");
            string dial = (Parameters.GetValue<string>("拨码地址") ?? "0").Trim().ToUpperInvariant();
            if (SwitchAddresses.TryGetValue(dial, out var addr))
            {
                _proto.CommandPrefix = "/" + addr;
            }
            else
            {
                _logger?.LogWarning("无效的拨码地址 <{Dial}>,按 0 处理", dial);
                _proto.CommandPrefix = "/1";
            }
        }

        private string GetResolutionMode()
            => (Parameters.GetValue<string>("分辨率模式") ?? "N0").Trim().ToUpperInvariant();

        private double GetSyringeVolumeMl()
        {
            double v = AsciiLineProtocol.ParseDouble(Parameters.GetValue<string>("注射器规格") ?? "5", 5);
            return v <= 0 ? 5 : v;   // 防止有人填 0 导致除零
        }

        #endregion

        public override DeviceState GetCurrentState()
        {
            return new DeviceState
            {
                States = new Dictionary<string, object>
                {
                    ["CurrentVolume"] = _lastVolume,
                    ["PlungerSteps"] = (double)_lastPlungerSteps,
                    ["ValvePosition"] = _lastValvePos,
                    ["IsIdle"] = _lastIdle,
                    ["IsInitialized"] = _lastInitialized
                }
            };
        }

        #region 连接

        protected override async Task<bool> OnConnectAsync()
        {
            if (IsSimulationMode) return true;

            try
            {
                SyncAddress();
                var r = await QuerySafeAsync(CmdGetFwVersion);
                if (!r.Ok && string.IsNullOrEmpty(r.Data))
                {
                    ErrorLog($"连接失败:{r.Error}", null);
                    return false;
                }
                InfoLog($"串口连接成功,固件版本 \"{r.Data}\"");

                // 上电后没初始化的话,后续所有移动命令都会被拒。这里只提示不自动初始化 ——
                // 初始化会把柱塞排空,在不知道管路里装着什么的情况下自动执行太危险。
                var init = await QuerySafeAsync(CmdGetInitStatus);
                _lastInitialized = init.Data?.Trim() == "1";
                if (!_lastInitialized)
                {
                    _logger?.LogWarning("泵尚未初始化,移动命令会被拒绝。请先执行「初始化」命令。");
                }
                return true;
            }
            catch (Exception e)
            {
                ErrorLog("串口连接失败", e);
                return false;
            }
        }

        protected override async Task<bool> OnConnectViaGatewayAsync() => await OnConnectAsync();

        protected override Task<bool> OnDisconnectAsync()
        {
            InfoLog("断开 Tricontinent C3000");
            return Task.FromResult(true);
        }

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
                Framing = SerialFraming.AsciiLine,
                // 分帧靠这个三字节结束符。设错了会一直读到超时,
                // 表现为"命令有效但每条都慢 3 秒",很难查。
                ReplyTerminator = "\u0003\r\n",
            };
            return CreateTransportAsync(serialConfig);
        }

        #endregion

        #region 命令包装

        private Func<DeviceParameters, Task<DeviceParameters>> WrapAction(
            string commandName, Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return async (DeviceParameters parameters) =>
            {
                _logger.LogInformation($"[C3000 命令-开始] Device={Name} Command={commandName}");
                var sw = Stopwatch.StartNew();
                try
                {
                    var output = await innerAsync(parameters);
                    sw.Stop();
                    _logger.LogInformation($"[C3000 命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[C3000 命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        #endregion
    }
}
