using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;

namespace JingRuiPump_ModbusRTU
{
    /// <summary>
    /// 精睿系列柱塞泵驱动(杭州精进科技,RS485 标准 Modbus 协议,手册第 7.4 节)。
    ///
    /// 通信设置:9600, 8 数据位, 1 停止位, 无校验(校验可改为奇/偶,改后需与本驱动参数同步);
    ///          出厂默认设备地址 9(面板「信息 → 仪器信息 → 通讯地址」可改,也可写寄存器改)。
    /// 功能码:  0x03 读保持寄存器 / 0x06 写单个保持寄存器。
    ///
    /// ── 寄存器地址的两种起点 ────────────────────────────────────────────
    /// 手册表格里的地址是 1 起点,而手册自己给的报文示例是 0 起点(表格下方注明:
    /// "以上起始地址为 1,如果起始地址为 0,则寄存器地址需要减 1")。
    /// 本驱动内部一律按 0 起点定义,与报文示例逐字节对得上;若现场设备实际是 1 起点,
    /// 把参数「寄存器起点」改成 1 即可,不需要改代码。
    ///
    /// 寄存器表(0 起点):
    ///   0  流量            W  0.01 mL/min  &lt;30000
    ///   1  流量2           W  0.1  mL/min  &lt;30000
    ///   2  最大设置压力     W  0.1  MPa     &lt;200   ← 改高限前务必确认不超过泵本体的最大压力
    ///   3  最小设置压力     W  0.1  MPa     &lt;200   ← 部分泵型有最低压力运行限值
    ///   4  实时压力值       R  0.001 MPa    &lt;20000
    ///   5  启动            W  写 1 启动
    ///   7  停止            W  写 1 停止
    ///   8  压力校零        W  写 1 校零
    ///   9  故障消除        W  写 1 清除故障
    ///  10  定量流量启动     W  0.1 mL       &lt;30000
    ///  14  当前系统状态     R  0 停止 / 1 运行
    ///  15  当前实时流量     R  0.1 mL/min(停止为 0)
    ///  16  故障状态        R  0 正常 / 5 超上限 / 6 超下限(另见 DCS 协议:1 电路 / 7 泵体 / 44 驱动器)
    ///  20  地址修改        W
    ///  21  验证序列号(后四位) W
    ///  22  读序列号最后四位  R/W
    ///  29  读设置流量       R  0.1  mL/min
    ///  30  读设置流量       R  0.01 mL/min
    ///  31  当前实时流量     R  0.01 mL/min
    ///
    /// ── 手册里三条会影响驱动写法的注意事项 ──────────────────────────────
    /// ① 485/232 写入只落 RAM 不落 Flash(频繁写 Flash 伤芯片),屏幕写入才两边都落。
    ///    → 所以本驱动不做周期性重复下发,只在真正需要改值时写一次。
    /// ② 手册建议:每次启动前先下发流量、再读回流量、比对一致后再启动。
    ///    否则泵可能按屏幕存在 Flash 里的旧流量跑。
    ///    → 「设置流量」自带回读比对;「按流量启动」= 设置流量(带比对) + 启动,推荐流程里用它。
    /// ③ 485 接线的 GND 必须接地,否则可能损坏通讯芯片(接线注意,非软件可控,写在帮助文本里提示)。
    ///
    /// 读取策略:写命令寄存器(5/7/8/9/10)在手册里标注"读取无效",跨过它们做整段读会被
    /// 部分固件判为非法地址。因此轮询拆成三帧,每帧只覆盖声明了 0x03 的连续区段:
    ///   帧1 读 4      (1 个) → 实时压力
    ///   帧2 读 14~16  (3 个) → 系统状态 / 实时流量(0.1) / 故障状态
    ///   帧3 读 29~31  (3 个) → 设定流量(0.1) / 设定流量(0.01) / 实时流量(0.01)
    /// 帧3 属于扩展区,老主板可能没有;首次读失败后自动降级,后续不再尝试,改用帧2 的 0.1 精度值。
    /// </summary>
    [Export(typeof(IDevice))]
    public class JingRuiPump_ModbusRTU : Device, IDeviceUIInteraction, IDeviceScreen
    {
        private readonly ILogService _logger;

        // ── 寄存器地址(0 起点)────────────────────────────────
        private const ushort RegFlow = 0;            // 流量        W 0.01 mL/min
        private const ushort RegFlow2 = 1;           // 流量2       W 0.1  mL/min
        private const ushort RegPressureMax = 2;     // 最大设置压力 W 0.1  MPa
        private const ushort RegPressureMin = 3;     // 最小设置压力 W 0.1  MPa
        private const ushort RegPressurePv = 4;      // 实时压力值   R 0.001 MPa
        private const ushort RegStart = 5;           // 启动        W 写 1
        private const ushort RegStop = 7;            // 停止        W 写 1
        private const ushort RegPressureZero = 8;    // 压力校零     W 写 1
        private const ushort RegFaultClear = 9;      // 故障消除     W 写 1
        private const ushort RegDoseStart = 10;      // 定量流量启动  W 0.1 mL
        private const ushort RegSystemState = 14;    // 当前系统状态  R 0 停 / 1 运行
        private const ushort RegSerialRead = 22;     // 读序列号最后四位 R
        private const ushort RegFlowSvExt = 29;      // 读设置流量 0.1 mL/min

        // 轮询帧2:14~16 连续三个只读寄存器
        private const ushort PollCoreStart = RegSystemState;
        private const ushort PollCoreCount = 3;
        // 轮询帧3:29~31 连续三个只读寄存器(扩展区)
        private const ushort PollExtStart = RegFlowSvExt;
        private const ushort PollExtCount = 3;

        // ── 换算刻度 ──────────────────────────────────────────
        private const double FlowScale001 = 100.0;   // 0.01 mL/min → 寄存器值 = 流量 × 100
        private const double FlowScale01 = 10.0;     // 0.1  mL/min → 寄存器值 = 流量 × 10
        private const double PressureSetScale = 10.0;    // 0.1   MPa → 寄存器值 = 压力 × 10
        private const double PressurePvScale = 1000.0;   // 0.001 MPa → 压力 = 寄存器值 / 1000
        private const double DoseScale = 10.0;       // 0.1 mL → 寄存器值 = 体积 × 10

        // 手册限值
        private const double MaxFlowRegister = 29999;    // 流量寄存器 <30000
        private const double MaxPressureSet = 19.9;      // 压力设定寄存器 <200 → 19.9 MPa
        private const double MaxDoseVolume = 2999.9;     // 定量寄存器 <30000 → 2999.9 mL

        // 通信层重试参数
        private const int MaxRetries = 3;
        private const int RetryDelayMs = 100;

        // 实例级互斥锁:同一设备实例的所有 IO 串行化,避免轮询/手动/心跳并发撞车(按 DeviceId 隔离)
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _instanceLocks
            = new ConcurrentDictionary<string, SemaphoreSlim>();

        /// <summary>
        /// 扩展寄存器(29~31)是否还在读。老主板没有这段,读不到就没必要每轮都白发一帧。
        /// 但一次读不到不等于不支持 —— 485 总线偶发抖动很常见,所以连续失败若干次才放弃,
        /// 免得一次瞬时干扰就把精度永久降到 0.1 mL/min。
        /// </summary>
        private bool _extendedRegsSupported = true;
        private int _extendedRegsMissCount;
        private const int ExtendedRegsMissLimit = 3;

        // ── 最近一次的状态快照:供画布控件与屏幕初始化显示 ──
        private double _lastFlowPv;
        private double _lastFlowSv;
        private double _lastPressure;
        private double _lastPressureMax = 4.0;   // 只写寄存器读不回来,只能记住上次写的值
        private double _lastPressureMin = 0.0;
        private bool _lastRunning;
        private int _lastFault;
        private bool _lastDosing;
        private double _lastDoseVolume;

        // ── 模拟模式状态:无硬件时也能完整演示 ──
        private readonly object _simLock = new object();
        private double _simFlowSv;
        private double _simFlowPv;
        private double _simPressure;
        private bool _simRunning;
        private int _simFault;
        private double _simDoseRemain;

        public JingRuiPump_ModbusRTU()
        {
            _logger = LogManager.GetLogger<JingRuiPump_ModbusRTU>();

            DeviceId = "JRP0001";
            Name = "精睿柱塞泵";
            Manufacturer = "杭州精进科技 精睿系列泵 · RS485 Modbus RTU";
            // 画布上用实物照片,实时数据一律在设备屏幕上看(双击设备打开)。
            // 图片放在 Shell 输出目录的 Resources\DeviceIcon 下,不随工程走 git。
            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/JingRuiPump.png";
            Category = DeviceCategories.Pumps;
            ComId = "JRP0001";
            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();
            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            Parameters.Variables.Add(new StringParameter("DeviceId", "JRP0001", "精睿泵设备ID")
            {
                Options = new ObservableCollection<string>() { "JRP0001", "JRP0002", "JRP0003", "JRP0004", "JRP0005" },
            });

            // 串口号/波特率/校验位/数据位/停止位/通信方式统一由 DeviceParameterOptions 维护。
            // 精睿泵默认 9600,N,8,1,与平台默认一致,故不需传参覆盖。
            DeviceParameterOptions.AddSerialParameters(Parameters);

            // Modbus 从机站号:精睿泵出厂默认 9(不是常见的 1)
            Parameters.Variables.Add(new NumberParameter("Modbus站号", 1, 254, 9, "Modbus 从机站号,精睿泵出厂默认 9"));

            // 寄存器起点:手册表格按 1 起点编号,报文示例按 0 起点。本驱动默认 0 起点。
            Parameters.Variables.Add(new NumberParameter("寄存器起点", 0, 1, 0,
                "0=报文示例口径(默认);1=手册表格口径。通讯不上时先试着改这一项"));

            // 泵本体量程:用于界面刻度与输入限幅,不参与通讯
            Parameters.Variables.Add(new NumberParameter("最大流量", 0.01, 299.99, 100,
                "泵本体最大流量,用于界面刻度与输入限幅", false, "mL/min"));
            Parameters.Variables.Add(new NumberParameter("最大压力", 0.1, 19.9, 10,
                "泵本体最大压力,用于界面刻度与输入限幅", false, "MPa"));

            InitializeCommands();
        }

        /// <summary>声明本驱动支持通过 ZLAN 网关通信(网口转 485)。</summary>
        public override bool SupportsZLanGateway => true;

        /// <summary>设备屏幕标识,对应 DeviceScreenRegistry 里注册的那块屏。</summary>
        public string ScreenKey => "JingRuiPumpScreen";

        /// <summary>可监控参数白名单:声明了的才出现在监控界面并被自动采集,
        /// 其余输出参数(设定回显/故障码等)不进入监控体系;取数复用「获取所有数据」。</summary>
        public override IReadOnlyList<CollectableParameter> CollectableParameters { get; } = new[]
        {
            new CollectableParameter("Flow", "实际流量", "mL/min", source: "获取所有数据", isKey: true),
            new CollectableParameter("Pressure", "实际压力", "MPa", source: "获取所有数据"),
            new CollectableParameter("FlowSV", "设定流量", "mL/min", source: "获取所有数据"),
        };

        /// <summary>采集间隔:一轮 2~3 帧 Modbus,1 秒足够,也不至于把 485 总线占满。</summary>
        public override int AutoCollectIntervalMs => 1000;

        #region ── 模拟模式:进料状态(驱动电子秤减重) ──

        /// <summary>把当前进料流量(运行时=实时流量,否则 0)发布到模拟中枢,电子秤据此减重。</summary>
        private void SimPublishPumpFeed()
        {
            double flow;
            lock (_simLock) { flow = _simRunning ? _simFlowPv : 0; }
            SimulationHub.PublishFeedFlow(DeviceId + "_jingrui", flow);
        }

        #endregion

        private void InitializeCommands()
        {
            // ════════════ 设置流量(写 + 回读比对) ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "设置流量",
                HelpText = "写寄存器 0(0.01 mL/min)设定流量,随后回读设定流量做一致性比对。" +
                           "手册明确建议启动前先写流量再读回比对,否则泵可能按屏幕存在 Flash 里的旧流量运行。" +
                           "回读不一致时本命令返回 Verified=false,不会掩盖问题。",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetFlow", 0, 299.99, 0, "流量设定值", true, "mL/min")
                    }
                },
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置流量成功", true),
                        new BooleanParameter("Verified", false, "回读流量与设定值一致", true),
                        new NumberParameter("SetFlow", 0, 299.99, 0, "设定的流量", true, "mL/min"),
                        new NumberParameter("ReadBackFlow", 0, 299.99, 0, "回读到的设定流量", true, "mL/min")
                    }
                },

                AsyncAction = AsyncWrapAction("设置流量", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double target = ClampFlow(parameter.GetValue<double>("TargetFlow"));
                    bool success;
                    bool verified;
                    double readBack;

                    OnStateChanged("CommandState_设置流量", CommandExecutionState.Running);

                    if (IsSimulationMode)
                    {
                        await Task.Delay(200);
                        lock (_simLock) { _simFlowSv = target; }
                        SimPublishPumpFeed();
                        success = true;
                        verified = true;
                        readBack = target;
                        InfoLog($"模拟模式设置流量:{target:F2} mL/min");
                    }
                    else
                    {
                        (success, verified, readBack) = await WriteAndVerifyFlowAsync(target, CancellationToken.None);
                    }

                    if (success)
                    {
                        _lastFlowSv = target;
                        OnStateChanged("FlowSV", target);
                    }
                    OnStateChanged("CommandState_设置流量",
                        success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new BooleanParameter("Success", success, "设置流量成功", true),
                            new BooleanParameter("Verified", verified, "回读流量与设定值一致", true),
                            new NumberParameter("SetFlow", 0, 299.99, target, "设定的流量", true, "mL/min"),
                            new NumberParameter("ReadBackFlow", 0, 299.99, readBack, "回读到的设定流量", true, "mL/min")
                        }
                    };
                })
            });

            // ════════════ 启动泵 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "启动泵",
                HelpText = "写寄存器 5 = 1 启动。只发启动,不改流量 —— 泵会按它当前的设定流量运行。" +
                           "若不确定泵内设定值,请改用「按流量启动」。",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "启动成功", true)
                    }
                },

                AsyncAction = AsyncWrapAction("启动泵", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    bool success;

                    OnStateChanged("CommandState_启动泵", CommandExecutionState.Running);

                    if (IsSimulationMode)
                    {
                        await Task.Delay(400);
                        lock (_simLock) { _simRunning = true; _simFault = 0; }
                        SimPublishPumpFeed();
                        success = true;
                        InfoLog("模拟模式启动精睿泵");
                    }
                    else
                    {
                        success = await WriteRegisterAsync(RegStart, 1, CancellationToken.None);
                    }

                    if (success)
                    {
                        _lastRunning = true;
                        OnStateChanged("IsRunning", true);
                    }
                    OnStateChanged("CommandState_启动泵",
                        success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new BooleanParameter("Success", success, "启动成功", true)
                        }
                    };
                })
            });

            // ════════════ 停止泵 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "停止泵",
                HelpText = "写寄存器 7 = 1 停止。定量运行中同样用这条中止。",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "停止成功", true)
                    }
                },

                AsyncAction = AsyncWrapAction("停止泵", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    bool success;

                    OnStateChanged("CommandState_停止泵", CommandExecutionState.Running);

                    if (IsSimulationMode)
                    {
                        await Task.Delay(400);
                        lock (_simLock) { _simRunning = false; _simDoseRemain = 0; }
                        SimPublishPumpFeed();
                        success = true;
                        InfoLog("模拟模式停止精睿泵");
                    }
                    else
                    {
                        success = await WriteRegisterAsync(RegStop, 1, CancellationToken.None);
                    }

                    if (success)
                    {
                        _lastRunning = false;
                        _lastDosing = false;
                        OnStateChanged("IsRunning", false);
                        OnStateChanged("IsDosing", false);
                    }
                    OnStateChanged("CommandState_停止泵",
                        success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new BooleanParameter("Success", success, "停止成功", true)
                        }
                    };
                })
            });

            // ════════════ 按流量启动(手册推荐的启动流程) ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "按流量启动",
                HelpText = "手册推荐的启动流程,一条命令完成三步:写流量 → 回读比对 → 启动。" +
                           "回读与设定不一致时直接判失败并且不启动,避免泵按 Flash 里的旧流量跑起来。" +
                           "流程编排里的进料动作建议一律用这条,而不是「设置流量」+「启动泵」两条。",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetFlow", 0, 299.99, 0, "流量设定值", true, "mL/min")
                    }
                },
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "按流量启动成功", true),
                        new BooleanParameter("Verified", false, "回读流量与设定值一致", true),
                        new NumberParameter("SetFlow", 0, 299.99, 0, "设定的流量", true, "mL/min"),
                        new NumberParameter("ReadBackFlow", 0, 299.99, 0, "回读到的设定流量", true, "mL/min")
                    }
                },

                AsyncAction = AsyncWrapAction("按流量启动", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double target = ClampFlow(parameter.GetValue<double>("TargetFlow"));
                    bool written, verified, started;
                    double readBack;

                    OnStateChanged("CommandState_按流量启动", CommandExecutionState.Running);

                    if (IsSimulationMode)
                    {
                        await Task.Delay(500);
                        lock (_simLock) { _simFlowSv = target; _simRunning = true; _simFault = 0; }
                        SimPublishPumpFeed();
                        written = verified = started = true;
                        readBack = target;
                        InfoLog($"模拟模式按流量启动:{target:F2} mL/min");
                    }
                    else
                    {
                        (written, verified, readBack) = await WriteAndVerifyFlowAsync(target, CancellationToken.None);

                        if (written && verified)
                        {
                            started = await WriteRegisterAsync(RegStart, 1, CancellationToken.None);
                        }
                        else
                        {
                            // 比对不过就不启动 —— 宁可这一步失败,也不让泵按未知流量进料
                            started = false;
                            ErrorLog($"流量回读比对不通过(设定 {target:F2},回读 {readBack:F2} mL/min),已放弃启动");
                        }
                    }

                    bool success = written && verified && started;

                    if (written)
                    {
                        _lastFlowSv = target;
                        OnStateChanged("FlowSV", target);
                    }
                    if (success)
                    {
                        _lastRunning = true;
                        OnStateChanged("IsRunning", true);
                    }
                    OnStateChanged("CommandState_按流量启动",
                        success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new BooleanParameter("Success", success, "按流量启动成功", true),
                            new BooleanParameter("Verified", verified, "回读流量与设定值一致", true),
                            new NumberParameter("SetFlow", 0, 299.99, target, "设定的流量", true, "mL/min"),
                            new NumberParameter("ReadBackFlow", 0, 299.99, readBack, "回读到的设定流量", true, "mL/min")
                        }
                    };
                })
            });

            // ════════════ 定量输送 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "定量输送",
                HelpText = "写寄存器 10(0.1 mL)启动定量输送,泵按当前设定流量送完指定体积后自行停止。" +
                           "可选在定量前先下发流量(带回读比对)。中途要停用「停止泵」。" +
                           "注:手册报文示例里 20 mL 写的是 20 而不是 200,手册同时注明示例值可能与实际不符;" +
                           "本驱动按寄存器表的 0.1 mL 刻度实现,首次使用请用小体积实测标定一次。",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("Volume", 0.1, 2999.9, 10, "定量体积", true, "mL"),
                        new NumberParameter("TargetFlow", 0, 299.99, 0, "定量流量(0=沿用泵内当前设定)", true, "mL/min")
                    }
                },
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "定量输送启动成功", true),
                        new NumberParameter("SetVolume", 0, 2999.9, 0, "定量体积", true, "mL")
                    }
                },

                AsyncAction = AsyncWrapAction("定量输送", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double volume = ClampDose(parameter.GetValue<double>("Volume"));
                    double target = ClampFlow(parameter.GetValue<double>("TargetFlow"));
                    bool success;

                    OnStateChanged("CommandState_定量输送", CommandExecutionState.Running);

                    if (IsSimulationMode)
                    {
                        await Task.Delay(400);
                        lock (_simLock)
                        {
                            if (target > 0) _simFlowSv = target;
                            _simDoseRemain = volume;
                            _simRunning = true;
                            _simFault = 0;
                        }
                        SimPublishPumpFeed();
                        success = true;
                        InfoLog($"模拟模式定量输送 {volume:F1} mL");
                    }
                    else
                    {
                        bool flowOk = true;
                        if (target > 0)
                        {
                            var (w, v, _) = await WriteAndVerifyFlowAsync(target, CancellationToken.None);
                            flowOk = w && v;
                            if (!flowOk) ErrorLog($"定量前的流量比对不通过(设定 {target:F2} mL/min),已放弃定量启动");
                        }

                        success = flowOk
                               && await WriteRegisterAsync(RegDoseStart, ToRegister(volume * DoseScale), CancellationToken.None);
                    }

                    if (success)
                    {
                        if (target > 0) { _lastFlowSv = target; OnStateChanged("FlowSV", target); }
                        _lastDosing = true;
                        _lastDoseVolume = volume;
                        _lastRunning = true;
                        OnStateChanged("IsDosing", true);
                        OnStateChanged("DoseVolume", volume);
                        OnStateChanged("IsRunning", true);
                    }
                    OnStateChanged("CommandState_定量输送",
                        success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new BooleanParameter("Success", success, "定量输送启动成功", true),
                            new NumberParameter("SetVolume", 0, 2999.9, volume, "定量体积", true, "mL")
                        }
                    };
                })
            });

            // ════════════ 设置压力上下限 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "设置压力上下限",
                HelpText = "写寄存器 2 / 3(0.1 MPa)设定压力报警上下限。" +
                           "手册警示:通过 485 改最大压力时务必确认不超过泵本体的最大压力,否则可能损坏泵;" +
                           "部分泵型另有最低压力运行限值,下限不要随手写 0。这两个寄存器只写不可读,故不做回读比对。",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("PressureMax", 0, 19.9, 4, "压力上限", true, "MPa"),
                        new NumberParameter("PressureMin", 0, 19.9, 0, "压力下限", true, "MPa")
                    }
                },
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetPressureMax", 0, 19.9, 0, "设定的压力上限", true, "MPa"),
                        new NumberParameter("SetPressureMin", 0, 19.9, 0, "设定的压力下限", true, "MPa")
                    }
                },

                AsyncAction = AsyncWrapAction("设置压力上下限", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double pMax = ClampPressure(parameter.GetValue<double>("PressureMax"));
                    double pMin = ClampPressure(parameter.GetValue<double>("PressureMin"));

                    // 下限高于上限是配置错误,直接挡在通讯之前,别让泵拿到自相矛盾的两个限值
                    if (pMin > pMax)
                    {
                        ErrorLog($"压力下限 {pMin:F1} MPa 高于上限 {pMax:F1} MPa,拒绝下发");
                        OnStateChanged("CommandState_设置压力上下限", CommandExecutionState.Failed);
                        return new DeviceParameters()
                        {
                            Variables = new ObservableCollection<ParameterBase>()
                            {
                                new BooleanParameter("Success", false, "设置成功", true),
                                new NumberParameter("SetPressureMax", 0, 19.9, pMax, "设定的压力上限", true, "MPa"),
                                new NumberParameter("SetPressureMin", 0, 19.9, pMin, "设定的压力下限", true, "MPa")
                            }
                        };
                    }

                    bool success;
                    OnStateChanged("CommandState_设置压力上下限", CommandExecutionState.Running);

                    if (IsSimulationMode)
                    {
                        await Task.Delay(300);
                        success = true;
                        InfoLog($"模拟模式设置压力上下限:{pMax:F1} / {pMin:F1} MPa");
                    }
                    else
                    {
                        bool ok1 = await WriteRegisterAsync(RegPressureMax, ToRegister(pMax * PressureSetScale), CancellationToken.None);
                        bool ok2 = await WriteRegisterAsync(RegPressureMin, ToRegister(pMin * PressureSetScale), CancellationToken.None);
                        success = ok1 && ok2;
                    }

                    if (success)
                    {
                        _lastPressureMax = pMax;
                        _lastPressureMin = pMin;
                        OnStateChanged("PressureMax", pMax);
                        OnStateChanged("PressureMin", pMin);
                    }
                    OnStateChanged("CommandState_设置压力上下限",
                        success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new BooleanParameter("Success", success, "设置成功", true),
                            new NumberParameter("SetPressureMax", 0, 19.9, pMax, "设定的压力上限", true, "MPa"),
                            new NumberParameter("SetPressureMin", 0, 19.9, pMin, "设定的压力下限", true, "MPa")
                        }
                    };
                })
            });

            // ════════════ 压力校零 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "压力校零",
                HelpText = "写寄存器 8 = 1 做压力零点校准。必须在泵停止、管路完全泄压的状态下执行," +
                           "带压校零会把当前残压当作零点,之后所有压力读数都会偏低。",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "校零成功", true)
                    }
                },

                AsyncAction = AsyncWrapAction("压力校零", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    bool success;

                    if (IsSimulationMode)
                    {
                        await Task.Delay(500);
                        lock (_simLock) { _simPressure = 0; }
                        success = true;
                        InfoLog("模拟模式压力校零");
                    }
                    else
                    {
                        success = await WriteRegisterAsync(RegPressureZero, 1, CancellationToken.None);
                    }

                    OnStateChanged("CommandState_压力校零",
                        success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new BooleanParameter("Success", success, "校零成功", true)
                        }
                    };
                })
            });

            // ════════════ 故障消除 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "故障消除",
                HelpText = "写寄存器 9 = 1 清除故障标志。只清标志,不解决成因 —— " +
                           "超压/欠压的根因(堵塞、漏液、限值设置不当)不排除的话,重启后会再次报警。",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "故障消除成功", true)
                    }
                },

                AsyncAction = AsyncWrapAction("故障消除", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    bool success;

                    if (IsSimulationMode)
                    {
                        await Task.Delay(300);
                        lock (_simLock) { _simFault = 0; }
                        success = true;
                        InfoLog("模拟模式故障消除");
                    }
                    else
                    {
                        success = await WriteRegisterAsync(RegFaultClear, 1, CancellationToken.None);
                    }

                    if (success)
                    {
                        _lastFault = 0;
                        // 一律推 double —— 画布控件与屏幕都按 double 拆包,推 int 会被静默丢弃
                        OnStateChanged("FaultCode", 0.0);
                    }
                    OnStateChanged("CommandState_故障消除",
                        success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new BooleanParameter("Success", success, "故障消除成功", true)
                        }
                    };
                })
            });

            // ════════════ 读序列号后四位 ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "读序列号后四位",
                HelpText = "读寄存器 22,取序列号最后四位。改 485 地址的前置步骤:" +
                           "读后四位 → 写寄存器 21 验证后四位 → 写寄存器 20 改地址。" +
                           "地址不明时可用广播地址 255 连接后再读。",
                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("SerialTail", 0, 65535, 0, "序列号后四位", true)
                    }
                },

                AsyncAction = AsyncWrapAction("读序列号后四位", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    int tail = 0;

                    if (IsSimulationMode)
                    {
                        await Task.Delay(200);
                        tail = 1202;   // 手册截图里的 JR20119011202
                        InfoLog($"模拟模式读序列号后四位:{tail}");
                    }
                    else
                    {
                        var regs = await ReadRegistersAsync(RegSerialRead, 1, CancellationToken.None);
                        if (regs != null && regs.Length >= 1) tail = (ushort)regs[0];
                    }

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new NumberParameter("SerialTail", 0, 65535, tail, "序列号后四位", true)
                        }
                    };
                })
            });

            // ════════════ 获取所有数据(监控/屏幕/自动采集的取数来源) ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "获取所有数据",
                HelpText = "一轮读取实时压力、系统状态、实时流量、故障状态与设定流量回显。" +
                           "拆成 2~3 帧 FC03,只覆盖手册标注可读的连续区段,不跨过只写寄存器。",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("Flow", 0, 30000, 0, "当前实时流量", true, "mL/min"),
                        new NumberParameter("FlowSV", 0, 30000, 0, "设定流量", true, "mL/min"),
                        new NumberParameter("Pressure", 0, 20, 0, "实时压力", true, "MPa"),
                        new NumberParameter("PressureMax", 0, 19.9, 0, "压力上限(上次下发值)", true, "MPa"),
                        new NumberParameter("PressureMin", 0, 19.9, 0, "压力下限(上次下发值)", true, "MPa"),
                        new NumberParameter("FaultCode", 0, 255, 0, "故障码", true),
                        new BooleanParameter("IsRunning", false, "泵正在运行", true),
                        new BooleanParameter("IsDosing", false, "定量输送中", true),
                        new BooleanParameter("FaultHighPressure", false, "压力超上限", true),
                        new BooleanParameter("FaultLowPressure", false, "压力超下限", true),
                        new BooleanParameter("FaultCircuit", false, "电路故障", true),
                        new BooleanParameter("FaultPumpBody", false, "泵体故障", true),
                        new BooleanParameter("FaultDriver", false, "驱动器故障", true)
                    }
                },

                AsyncAction = AsyncWrapAction("获取所有数据", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");

                    double pressure, flowPv, flowSv;
                    bool running;
                    int fault;

                    if (IsSimulationMode)
                    {
                        await Task.Delay(200);
                        lock (_simLock)
                        {
                            // 流量向设定值一阶逼近,停机时归零
                            if (_simRunning) _simFlowPv += (_simFlowSv - _simFlowPv) * 0.35 + SimulationHub.Noise(0.02);
                            else _simFlowPv += (0 - _simFlowPv) * 0.5;
                            if (_simFlowPv < 0.005) _simFlowPv = 0;

                            // 压力大致随流量走,叠一点噪声
                            double pTarget = _simRunning ? 0.6 + _simFlowPv * 0.05 : 0;
                            _simPressure += (pTarget - _simPressure) * 0.25 + SimulationHub.Noise(0.01);
                            if (_simPressure < 0) _simPressure = 0;

                            // 定量:每轮扣掉这一秒送出的体积,送完自停
                            if (_simRunning && _simDoseRemain > 0)
                            {
                                _simDoseRemain -= _simFlowPv / 60.0 * (AutoCollectIntervalMs / 1000.0);
                                if (_simDoseRemain <= 0) { _simDoseRemain = 0; _simRunning = false; }
                            }

                            // 超过上限就报 5,与真机故障码一致
                            if (_simRunning && _simPressure > _lastPressureMax && _lastPressureMax > 0) _simFault = 5;

                            pressure = Math.Round(_simPressure, 3);
                            flowPv = Math.Round(_simFlowPv, 2);
                            flowSv = _simFlowSv;
                            running = _simRunning;
                            fault = _simFault;
                        }
                        SimPublishPumpFeed();
                    }
                    else
                    {
                        // 帧1:实时压力(0.001 MPa)
                        var pRegs = await ReadRegistersAsync(RegPressurePv, 1, CancellationToken.None);
                        pressure = pRegs != null && pRegs.Length >= 1
                            ? (ushort)pRegs[0] / PressurePvScale
                            : _lastPressure;

                        // 帧2:系统状态 / 实时流量(0.1) / 故障状态
                        var core = await ReadRegistersAsync(PollCoreStart, PollCoreCount, CancellationToken.None);
                        if (core != null && core.Length >= PollCoreCount)
                        {
                            running = core[0] != 0;
                            flowPv = (ushort)core[1] / FlowScale01;
                            fault = (ushort)core[2];
                        }
                        else
                        {
                            running = _lastRunning;
                            flowPv = _lastFlowPv;
                            fault = _lastFault;
                        }

                        // 帧3:扩展区 —— 设定流量与更高精度的实时流量。老主板没有这段,失败一次后就不再发。
                        flowSv = _lastFlowSv;
                        if (_extendedRegsSupported)
                        {
                            short[]? ext;
                            try
                            {
                                ext = await ReadRegistersAsync(PollExtStart, PollExtCount, CancellationToken.None);
                            }
                            catch (TimeoutException)
                            {
                                // 固件没有这段寄存器时,有的返回非法地址异常码(上面已按 null 处理),
                                // 有的干脆不应答。后者若不在这里吞掉,每轮采集都要白等 3 次重试才报错,
                                // 前两帧读到的好数据也会被一起丢掉。
                                ext = null;
                            }

                            if (ext != null && ext.Length >= PollExtCount)
                            {
                                _extendedRegsMissCount = 0;
                                // ext[0]=设定流量 0.1  ext[1]=设定流量 0.01  ext[2]=实时流量 0.01
                                flowSv = (ushort)ext[1] / FlowScale001;
                                if (flowSv <= 0) flowSv = (ushort)ext[0] / FlowScale01;
                                flowPv = (ushort)ext[2] / FlowScale001;
                            }
                            else if (++_extendedRegsMissCount >= ExtendedRegsMissLimit)
                            {
                                _extendedRegsSupported = false;
                                InfoLog($"扩展寄存器 29~31 连续 {ExtendedRegsMissLimit} 次读不到,判定本机不支持;" +
                                        "流量改用 0.1 mL/min 精度,设定流量以本地记录为准");
                            }
                        }
                    }

                    // 定量状态:泵自己停下来就说明这一次定量送完了
                    bool dosing = _lastDosing && running;
                    if (_lastDosing && !running) _lastDosing = false;

                    PushReadingsToUI(flowPv, flowSv, pressure, running, dosing, fault);

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new NumberParameter("Flow", 0, 30000, flowPv, "当前实时流量", true, "mL/min"),
                            new NumberParameter("FlowSV", 0, 30000, flowSv, "设定流量", true, "mL/min"),
                            new NumberParameter("Pressure", 0, 20, pressure, "实时压力", true, "MPa"),
                            new NumberParameter("PressureMax", 0, 19.9, _lastPressureMax, "压力上限(上次下发值)", true, "MPa"),
                            new NumberParameter("PressureMin", 0, 19.9, _lastPressureMin, "压力下限(上次下发值)", true, "MPa"),
                            new NumberParameter("FaultCode", 0, 255, fault, "故障码", true),
                            new BooleanParameter("IsRunning", running, "泵正在运行", true),
                            new BooleanParameter("IsDosing", dosing, "定量输送中", true),
                            new BooleanParameter("FaultHighPressure", fault == 5, "压力超上限", true),
                            new BooleanParameter("FaultLowPressure", fault == 6, "压力超下限", true),
                            new BooleanParameter("FaultCircuit", fault == 1, "电路故障", true),
                            new BooleanParameter("FaultPumpBody", fault == 7, "泵体故障", true),
                            new BooleanParameter("FaultDriver", fault == 44, "驱动器故障", true)
                        }
                    };
                })
            });
        }

        /// <summary>把故障码翻译成人看的描述。0 以外的未知码原样报出来,不吞掉。</summary>
        public static string DescribeFault(int code) => code switch
        {
            0 => "正常",
            1 => "电路故障",
            5 => "压力超上限",
            6 => "压力超下限",
            7 => "泵体故障",
            44 => "驱动器故障",
            _ => $"未知故障({code})"
        };

        #region UI 联动(设备 → 设备屏幕 / 命令面板)

        /// <summary>把一轮读数推给 UI。只推变化项,避免每秒刷一遍全量。</summary>
        private void PushReadingsToUI(double flowPv, double flowSv, double pressure,
                                      bool running, bool dosing, int fault)
        {

            if (Math.Abs(flowPv - _lastFlowPv) >= 0.005)
            {
                _lastFlowPv = flowPv;
                OnStateChanged("Flow", flowPv);
            }
            if (Math.Abs(flowSv - _lastFlowSv) >= 0.005)
            {
                _lastFlowSv = flowSv;
                OnStateChanged("FlowSV", flowSv);
            }
            if (Math.Abs(pressure - _lastPressure) >= 0.0005)
            {
                _lastPressure = pressure;
                OnStateChanged("Pressure", pressure);
            }
            if (running != _lastRunning)
            {
                _lastRunning = running;
                OnStateChanged("IsRunning", running);
            }
            if (dosing != _lastDosing)
            {
                _lastDosing = dosing;
                OnStateChanged("IsDosing", dosing);
            }
            if (fault != _lastFault)
            {
                _lastFault = fault;
                OnStateChanged("FaultCode", (double)fault);
            }
        }

        /// <summary>
        /// 从 UI 接收命令(UI → 设备)。画布控件与设备屏幕上的按钮走这里,
        /// 最终仍然只调用上面注册的那几条命令,不另开一套通讯路径。
        /// </summary>
        public override async Task<CommandExecutionResult> ExecuteUICommandAsync(
            string commandName, Dictionary<string, object> parameters = null)
        {
            try
            {
                InfoLog($"收到UI命令: {commandName}");

                switch (commandName)
                {
                    case "设置流量":
                    case "按流量启动":
                        {
                            double flow = GetParam(parameters, "TargetFlow", _lastFlowSv);
                            return await InvokeCommandFromUIAsync(commandName, new DeviceParameters()
                            {
                                Variables = new ObservableCollection<ParameterBase>()
                                {
                                    new NumberParameter("TargetFlow", 0, 299.99, flow, "流量设定值", true, "mL/min")
                                }
                            });
                        }

                    case "定量输送":
                        {
                            double volume = GetParam(parameters, "Volume", _lastDoseVolume > 0 ? _lastDoseVolume : 10);
                            double flow = GetParam(parameters, "TargetFlow", 0.0);
                            return await InvokeCommandFromUIAsync("定量输送", new DeviceParameters()
                            {
                                Variables = new ObservableCollection<ParameterBase>()
                                {
                                    new NumberParameter("Volume", 0.1, 2999.9, volume, "定量体积", true, "mL"),
                                    new NumberParameter("TargetFlow", 0, 299.99, flow, "定量流量", true, "mL/min")
                                }
                            });
                        }

                    case "设置压力上下限":
                        {
                            double pMax = GetParam(parameters, "PressureMax", _lastPressureMax);
                            double pMin = GetParam(parameters, "PressureMin", _lastPressureMin);
                            return await InvokeCommandFromUIAsync("设置压力上下限", new DeviceParameters()
                            {
                                Variables = new ObservableCollection<ParameterBase>()
                                {
                                    new NumberParameter("PressureMax", 0, 19.9, pMax, "压力上限", true, "MPa"),
                                    new NumberParameter("PressureMin", 0, 19.9, pMin, "压力下限", true, "MPa")
                                }
                            });
                        }

                    case "启动泵":
                    case "停止泵":
                    case "压力校零":
                    case "故障消除":
                    case "读序列号后四位":
                    case "获取所有数据":
                        return await InvokeCommandFromUIAsync(commandName, new DeviceParameters());

                    default:
                        return new CommandExecutionResult { Success = false, Message = $"未识别的命令: {commandName}" };
                }
            }
            catch (Exception ex)
            {
                ErrorLog($"执行UI命令失败: {commandName}", ex);
                return new CommandExecutionResult { Success = false, Message = ex.Message, Exception = ex };
            }
        }

        /// <summary>按名字找到已注册的 DeviceCommand 并执行,把输出打包成 CommandExecutionResult。</summary>
        private async Task<CommandExecutionResult> InvokeCommandFromUIAsync(string commandName, DeviceParameters input)
        {
            var cmd = Commands.FirstOrDefault(c => c.Name == commandName);
            if (cmd?.AsyncAction == null)
                return new CommandExecutionResult { Success = false, Message = $"命令不可用: {commandName}" };

            var output = await cmd.AsyncAction(input);

            var result = new CommandExecutionResult { Success = true, Message = $"{commandName} 完成" };
            if (output?.Variables != null)
            {
                foreach (var v in output.Variables)
                    result.OutputData[v.Name] = v.Value;

                // 写指令带 Success 输出,以它为准
                if (result.OutputData.TryGetValue("Success", out var ok) && ok is bool okFlag)
                {
                    result.Success = okFlag;
                    if (!okFlag) result.Message = $"{commandName} 失败";
                }
            }
            return result;
        }

        private static double GetParam(Dictionary<string, object> parameters, string key, double fallback)
        {
            if (parameters != null && parameters.TryGetValue(key, out var v) && v != null
                && double.TryParse(v.ToString(), out var d)) return d;
            return fallback;
        }

        /// <summary>当前状态快照,供控件与屏幕初始化显示。</summary>
        public override DeviceState GetCurrentState()
        {
            return new DeviceState
            {
                States = new Dictionary<string, object>
                {
                    ["Flow"] = _lastFlowPv,
                    ["FlowSV"] = _lastFlowSv,
                    ["Pressure"] = _lastPressure,
                    ["PressureMax"] = _lastPressureMax,
                    ["PressureMin"] = _lastPressureMin,
                    ["IsRunning"] = _lastRunning,
                    ["IsDosing"] = _lastDosing,
                    ["FaultCode"] = (double)_lastFault
                }
            };
        }

        #endregion

        #region 设备接口实现

        protected override void OnInitialize()
        {
            InfoLog("开始初始化精睿柱塞泵");
            try
            {
                base.OnInitialize();
                InfoLog("精睿柱塞泵初始化完成");
            }
            catch (Exception e)
            {
                ErrorLog("精睿柱塞泵初始化失败", e);
                throw;
            }
        }

        protected override void OnShutdown()
        {
            InfoLog("开始关闭精睿柱塞泵");
            try
            {
                base.OnShutdown();
                InfoLog("精睿柱塞泵关闭完成");
            }
            catch (Exception e)
            {
                ErrorLog("精睿柱塞泵关闭失败", e);
                throw;
            }
        }

        /// <summary>Direct 模式下的连接实现。串口即用即建,这里只做逻辑标记。</summary>
        protected override Task<bool> OnConnectAsync()
        {
            InfoLog("Direct 模式串口连接(每次 IO 即用即建)");
            return Task.FromResult(true);
        }

        /// <summary>ZLanGateway 模式下的连接实现:读实时压力,收到合法应答即视为连通。</summary>
        protected override async Task<bool> OnConnectViaGatewayAsync()
        {
            try
            {
                InfoLog("通过 ZLAN 网关测试连接");
                var regs = await ReadRegistersAsync(RegPressurePv, 1, CancellationToken.None);
                if (regs == null || regs.Length < 1)
                {
                    ErrorLog("网关连接失败:未取到合法应答");
                    return false;
                }
                InfoLog($"网关连接成功,当前压力 {(ushort)regs[0] / PressurePvScale:F3} MPa");
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
            InfoLog("断开精睿柱塞泵");
            return Task.FromResult(true);
        }

        #endregion

        #region 命令包装(日志 + 异常封装)

        private Func<DeviceParameters, Task<DeviceParameters>> AsyncWrapAction(
            string commandName, Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return async (DeviceParameters parameters) =>
            {
                _logger.LogInformation($"[精睿泵命令-开始] Device={Name} Command={commandName}");
                var sw = Stopwatch.StartNew();
                try
                {
                    var output = await innerAsync(parameters);
                    sw.Stop();
                    _logger.LogInformation($"[精睿泵命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[精睿泵命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        #endregion

        #region 业务级通讯动作

        /// <summary>
        /// 写流量并回读比对 —— 手册明确建议的动作序列。
        /// 回读优先用扩展寄存器 30(0.01 mL/min),与写入刻度一致;没有扩展区就退回寄存器 29(0.1)。
        /// 返回 (写成功, 回读一致, 回读值)。回读寄存器整段不可用时视为"无法验证",一致性判 false。
        /// </summary>
        private async Task<(bool written, bool verified, double readBack)> WriteAndVerifyFlowAsync(
            double targetFlow, CancellationToken token)
        {
            bool written = await WriteRegisterAsync(RegFlow, ToRegister(targetFlow * FlowScale001), token);
            if (!written) return (false, false, 0);

            // 屏幕在线时部分版本要等设备内部刷新完才回读得到新值,给一小段沉降时间
            await Task.Delay(120, token).ConfigureAwait(false);

            short[]? ext;
            try
            {
                ext = await ReadRegistersAsync(RegFlowSvExt, 2, token);
            }
            catch (TimeoutException)
            {
                // 回读超时不等于写失败 —— 写已经确认过了。但没读回来就是没验证过,
                // 照实返回 verified=false,由调用方决定要不要继续(「按流量启动」会拒绝启动)。
                ext = null;
            }

            if (ext == null || ext.Length < 2)
            {
                // 只报告"这一次没验证成功",不在这里判定固件支不支持 ——
                // 那个判定归轮询侧的连续失败计数管,写入路径不该顺手把精度降级掉。
                InfoLog("回读寄存器 29/30 无应答,本次流量无法验证");
                return (true, false, 0);
            }

            double back001 = (ushort)ext[1] / FlowScale001;   // 寄存器 30,0.01 mL/min
            double back01 = (ushort)ext[0] / FlowScale01;     // 寄存器 29,0.1  mL/min
            double readBack = back001 > 0 ? back001 : back01;

            // 允许半个最小刻度的误差:0.01 mL/min 精度下就是 0.005
            bool verified = Math.Abs(readBack - targetFlow) <= 0.005 + 1e-9;
            if (!verified)
                InfoLog($"流量回读不一致:设定 {targetFlow:F2},回读 {readBack:F2} mL/min");

            return (true, verified, readBack);
        }

        #endregion

        #region Modbus RTU 底层

        /// <summary>把工程量换算成寄存器值并限幅到 0~65535。</summary>
        private static ushort ToRegister(double scaledValue)
        {
            int v = (int)Math.Round(scaledValue);
            if (v < 0) v = 0;
            if (v > 0xFFFF) v = 0xFFFF;
            return (ushort)v;
        }

        /// <summary>流量限幅:不超过泵本体量程,也不超过寄存器 &lt;30000 的协议上限。</summary>
        private double ClampFlow(double flow)
        {
            double machineMax;
            try { machineMax = Parameters.GetValue<double>("最大流量"); }
            catch { machineMax = 100; }

            double limit = Math.Min(machineMax, MaxFlowRegister / FlowScale001);
            if (flow < 0) return 0;
            return flow > limit ? limit : flow;
        }

        /// <summary>压力限幅:不超过泵本体量程,也不超过寄存器 &lt;200 的协议上限。</summary>
        private double ClampPressure(double pressure)
        {
            double machineMax;
            try { machineMax = Parameters.GetValue<double>("最大压力"); }
            catch { machineMax = MaxPressureSet; }

            double limit = Math.Min(machineMax, MaxPressureSet);
            if (pressure < 0) return 0;
            return pressure > limit ? limit : pressure;
        }

        /// <summary>定量体积限幅:寄存器 &lt;30000,0.1 mL 刻度 → 上限 2999.9 mL。</summary>
        private static double ClampDose(double volume)
        {
            if (volume < 0) return 0;
            return volume > MaxDoseVolume ? MaxDoseVolume : volume;
        }

        /// <summary>取当前配置的 Modbus 从机站号 (1~254)。精睿泵出厂默认 9。</summary>
        private byte GetSlaveAddress()
        {
            try
            {
                var addr = (int)Parameters.GetValue<double>("Modbus站号");
                if (addr < 1 || addr > 254) addr = 9;
                return (byte)addr;
            }
            catch
            {
                return 9;
            }
        }

        /// <summary>
        /// 把驱动内部的 0 起点寄存器号换算成实际报文里的地址。
        /// 参数「寄存器起点」= 1 时整体加 1,对应手册表格那套编号。
        /// </summary>
        private ushort MapRegister(ushort zeroBased)
        {
            try
            {
                var origin = (int)Parameters.GetValue<double>("寄存器起点");
                return origin == 1 ? (ushort)(zeroBased + 1) : zeroBased;
            }
            catch
            {
                return zeroBased;
            }
        }

        /// <summary>获取本设备实例专属的互斥锁(按 DeviceId 隔离,IO 串行化)。</summary>
        private SemaphoreSlim GetInstanceLock()
        {
            var key = Parameters?.GetValue<string>("DeviceId") ?? DeviceId ?? "default";
            return _instanceLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        }

        /// <summary>构造 Modbus RTU 写单寄存器命令 (功能码 0x06),自动加 CRC。</summary>
        private byte[] BuildWriteCmd(ushort register, ushort value)
        {
            ushort addr = MapRegister(register);
            byte[] frame = new byte[]
            {
                GetSlaveAddress(),
                0x06,
                (byte)(addr >> 8), (byte)(addr & 0xFF),
                (byte)(value >> 8), (byte)(value & 0xFF),
            };
            return Crc16Modbus.AppendCrc(frame, 0, frame.Length);
        }

        /// <summary>构造 Modbus RTU 读保持寄存器命令 (功能码 0x03),自动加 CRC。</summary>
        private byte[] BuildReadCmd(ushort register, ushort count)
        {
            ushort addr = MapRegister(register);
            byte[] frame = new byte[]
            {
                GetSlaveAddress(),
                0x03,
                (byte)(addr >> 8), (byte)(addr & 0xFF),
                (byte)(count >> 8), (byte)(count & 0xFF),
            };
            return Crc16Modbus.AppendCrc(frame, 0, frame.Length);
        }

        /// <summary>写一个寄存器。自动重试 3 次以应对工业链路偶发抖动。</summary>
        private async Task<bool> WriteRegisterAsync(ushort register, ushort value, CancellationToken token)
        {
            byte[] frame = BuildWriteCmd(register, value);
            Exception? lastException = null;

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    var sem = GetInstanceLock();
                    await sem.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        using var transport = await GetTransportAsync().ConfigureAwait(false);
                        return await transport.SendAsync(frame, 0, frame.Length, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        sem.Release();
                    }
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

            throw new TimeoutException($"Modbus 写入失败,{MaxRetries} 次尝试后仍超时", lastException);
        }

        /// <summary>
        /// 读 N 个连续寄存器。自动重试 3 次。
        /// 与平台其他驱动不同的是:应答不合法时返回 null 而不是补零数组 ——
        /// 调用方要靠这个 null 区分"读到了 0"和"根本没读到",扩展寄存器的降级判断依赖它。
        /// </summary>
        private async Task<short[]?> ReadRegistersAsync(ushort register, ushort count, CancellationToken token)
        {
            byte[] sendCmd = BuildReadCmd(register, count);
            Exception? lastException = null;

            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    return await ReadRegistersOnceAsync(sendCmd, count, token).ConfigureAwait(false);
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
                    // 非超时异常(协议错、字节流非法)不重试,直接抛
                    _logger?.LogError(ex, "Modbus 读取失败(非超时,不重试)");
                    throw;
                }
            }

            throw new TimeoutException($"Modbus 读取失败,{MaxRetries} 次尝试后仍超时", lastException);
        }

        private async Task<short[]?> ReadRegistersOnceAsync(byte[] sendCmd, ushort count, CancellationToken token)
        {
            var sem = GetInstanceLock();
            await sem.WaitAsync(token).ConfigureAwait(false);
            try
            {
                byte[] recv = new byte[256];
                using var transport = await GetTransportAsync().ConfigureAwait(false);
                int len = await transport.SendAndReceiveAsync(sendCmd, recv, 3000, token).ConfigureAwait(false);

                // 应答最短:站号(1)+功能码(1)+字节数(1)+数据(2×N)+CRC(2)
                int minLen = 5 + count * 2;
                if (len < minLen)
                {
                    _logger?.LogWarning("应答帧长度不足:{Len} 字节,需要至少 {Min} 字节", len, minLen);
                    return null;
                }

                byte expectedAddr = GetSlaveAddress();
                if (recv[0] != expectedAddr)
                {
                    _logger?.LogWarning("站号不匹配:期望 0x{Expected:X2},实际 0x{Actual:X2}", expectedAddr, recv[0]);
                    return null;
                }
                if ((recv[1] & 0x80) != 0)
                {
                    // 0x02 = 非法数据地址,通常意味着这台泵的固件没有这段寄存器
                    _logger?.LogWarning("Modbus 异常应答,异常码 0x{Code:X2}", recv[2]);
                    return null;
                }
                if (recv[1] != 0x03)
                {
                    _logger?.LogWarning("功能码不匹配:期望 0x03,实际 0x{Actual:X2}", recv[1]);
                    return null;
                }

                byte dataLen = recv[2];
                if (dataLen < count * 2)
                {
                    _logger?.LogWarning("数据字节数不足:{Actual},期望 {Expected}", dataLen, count * 2);
                    return null;
                }

                // CRC 校验:帧长 = 3 + dataLen + 2
                int frameLen = 3 + dataLen + 2;
                if (len >= frameLen)
                {
                    byte[] frame = new byte[frameLen];
                    Buffer.BlockCopy(recv, 0, frame, 0, frameLen);
                    if (!Crc16Modbus.Verify(frame))
                    {
                        _logger?.LogWarning("CRC 校验失败,丢弃本帧");
                        return null;
                    }
                }

                short[] result = new short[count];
                for (int i = 0; i < count; i++)
                {
                    // Modbus 大端:高字节在前
                    result[i] = (short)((recv[3 + i * 2] << 8) | recv[4 + i * 2]);
                }
                return result;
            }
            finally
            {
                sem.Release();
            }
        }

        /// <summary>通过基类的 CreateTransportAsync 获取 Transport(串口/网关)。</summary>
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
    }

    /// <summary>CRC16-Modbus 校验工具。多项式 0xA001,初始值 0xFFFF。</summary>
    public static class Crc16Modbus
    {
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
