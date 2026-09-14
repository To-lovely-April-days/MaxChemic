// ============================================================================
//  ShakingIncubator_ModbusRTU.cs
//  恒温振荡培养箱(摇床)驱动 —— Modbus RTU / RS485。
//  依据《0x140B 恒温振荡培养箱 485 通讯协议 PHRYH_ST70_206Ti 20260714》。
//
//  ── 通信参数(手册"说明"列写死) ────────────────────────────────────
//      9600 / N / 8 / 1,Modbus RTU + CRC,**每次读取最多 32 个地址变量**。
//      手册没有给从站地址,做成可配参数,默认 1。
//
//  ── 寄存器全景 ────────────────────────────────────────────────────
//      0x0000~0x001F  只读:身份、报警位、量程、当前测量/设定值、运行状态
//      0x0020~0x0039  可读可写:预约、程式编辑窗口、系统时钟、运行/照明/灭菌
//      正好被 32 个一批的限制切成两刀:0x0000+32 与 0x0020+26。
//
//  ── 这台仪器最容易踩的坑:改温度不是"写一个温度寄存器" ─────────────
//      0x0008「当前温度设定值」在手册里只有"读数据0x03",是**只读**的。
//      真正改设定值要走 0x0026~0x002F 这个"程式编辑窗口":
//          先写 要修改的模式/段数/时/分/温度/转速/风速,
//          最后写 0x0026 = 2(写标志),仪器才把这批值收下。
//      读某一段的设定值同理:先写 模式/段数,再写 0x0026 = 1(读标志),
//      然后回读 0x0029~0x002F。
//      **顺序不能反** —— 标志寄存器必须最后写,它是这批参数的"提交"动作。
//      后来维护的人如果把"设置温度"改成直接写 0x0008,仪器不会报错,
//      但设定值一点都不会变,而且很难看出来。
//
//  ── 小数点 ────────────────────────────────────────────────────────
//      手册"小数点位"列:温度 1 位(寄存器值 = 实际值 ×10),转速/风速 0 位。
//      所有温度进出都要 ×10 / ÷10,别只改一头。
// ============================================================================

using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;

namespace ShakingIncubator_ModbusRTU
{
    /// <summary>
    /// 寄存器地址表。全部是保持寄存器,读 0x03,写 0x06 / 0x10。
    /// 名字后缀 Pv = 测量值,Set = 设定值。
    /// </summary>
    internal static class Regs
    {
        // ── 只读区 ──────────────────────────────────────────
        public const ushort ProductType = 0x0000;   // 应为 0x140B
        public const ushort SerialNo = 0x0001;
        public const ushort Alarm1 = 0x0002;   // 位域,见 Alarm1Bits
        public const ushort Alarm2 = 0x0003;   // 位域,见 Alarm2Bits
        public const ushort TempLoLimit = 0x0004;   // ÷10
        public const ushort TempHiLimit = 0x0005;   // ÷10
        public const ushort SpeedLoLimit = 0x0006;
        public const ushort SpeedHiLimit = 0x0007;
        public const ushort TempSet = 0x0008;   // ÷10,只读!改设定值走编辑窗口
        public const ushort TempPv = 0x0009;   // ÷10
        public const ushort SpeedSet = 0x000A;
        public const ushort SpeedPv = 0x000B;
        public const ushort ElapsedHour = 0x000C;
        public const ushort ElapsedMinute = 0x000D;
        public const ushort SetHour = 0x000E;
        public const ushort SetMinute = 0x000F;
        public const ushort CurrentMode = 0x0010;   // 0 定值,1~5 程式
        public const ushort RunSegment = 0x0011;
        public const ushort RunCycle = 0x0012;
        public const ushort SetSegment = 0x0013;
        public const ushort SetCycle = 0x0014;
        public const ushort RunState = 0x0015;   // 0停止 1预约等待 2运行 3计时 4自整定
        public const ushort AlarmState = 0x0016;   // 0无报警 1有报警
        public const ushort SpeedFunc = 0x0017;   // 转速功能是否开启
        public const ushort FanFunc = 0x0018;   // 风机可调功能是否开启
        public const ushort FanLoLimit = 0x0019;
        public const ushort FanHiLimit = 0x001A;
        public const ushort FanSet = 0x001B;
        public const ushort FanPv = 0x001C;
        public const ushort LightFunc = 0x001D;   // 照明功能是否开启
        public const ushort SterilizeFunc = 0x001E;   // 灭菌功能是否开启
        public const ushort BookFunc = 0x001F;   // 预约功能是否开启

        // ── 读写区 ──────────────────────────────────────────
        public const ushort BookOnOff = 0x0020;
        public const ushort BookYear = 0x0021;
        public const ushort BookMonth = 0x0022;
        public const ushort BookDay = 0x0023;
        public const ushort BookHour = 0x0024;
        public const ushort BookMinute = 0x0025;

        /// <summary>程式编辑窗口的提交标志:1=读标志,2=写标志。**必须最后写**。</summary>
        public const ushort EditFlag = 0x0026;
        public const ushort EditMode = 0x0027;   // 0 定值,1~5 程式
        public const ushort EditSegment = 0x0028;   // 1~100,仅程式模式有效
        public const ushort EditTotalCycle = 0x0029;   // 0~999,仅程式模式有效
        public const ushort EditTotalSegment = 0x002A;   // 1~100,仅程式模式有效
        public const ushort EditHour = 0x002B;
        public const ushort EditMinute = 0x002C;
        public const ushort EditTemp = 0x002D;   // ×10
        public const ushort EditSpeed = 0x002E;
        public const ushort EditFan = 0x002F;

        public const ushort ClockYear = 0x0030;
        public const ushort ClockMonth = 0x0031;
        public const ushort ClockDay = 0x0032;
        public const ushort ClockHour = 0x0033;
        public const ushort ClockMinute = 0x0034;
        public const ushort ClockSecond = 0x0035;

        public const ushort SetMode = 0x0036;
        public const ushort RunStop = 0x0037;   // 0 停止,1 开启
        public const ushort LightSwitch = 0x0038;
        public const ushort SterilizeSwitch = 0x0039;

        /// <summary>只读块:0x0000 起 32 个(正好是手册允许的单次上限)。</summary>
        public const ushort BlockAStart = 0x0000;
        public const ushort BlockACount = 32;

        /// <summary>读写块:0x0020 起 26 个。</summary>
        public const ushort BlockBStart = 0x0020;
        public const ushort BlockBCount = 26;

        /// <summary>手册标称的产品类型编号。对不上不拦,只告警。</summary>
        public const ushort ExpectedProductType = 0x140B;
    }

    /// <summary>报警状态 1(0x0002)的位定义,低位在前。</summary>
    internal static class Alarm1Bits
    {
        public static readonly string[] Names =
        {
            "主温上偏差报警", "主温下偏差报警", "开门超时报警", "主温故障",
            "门温故障", "蒸发器温度故障", "环温故障", "温控板通信故障",
        };

        // bit8/bit9 不是故障,是照明/灭菌的开关状态,单独取
        public const int LightStateBit = 8;
        public const int SterilizeStateBit = 9;
    }

    /// <summary>报警状态 2(0x0003)的位定义,全部是电机板故障。</summary>
    internal static class Alarm2Bits
    {
        public static readonly string[] Names =
        {
            "IPM故障", "堵转故障", "霍尔故障", "欠压故障", "过压故障",
            "初始位置错误故障", "z相故障", "超速报警故障", "电机板通信故障",
        };
    }

    [Export(typeof(IDevice))]
    public class ShakingIncubator_ModbusRTU : Device, IDeviceScreen
    {
        /// <summary>
        /// 源码版本戳。**改动本文件时手动更新**。
        /// 现场日志第一行会打出来 —— 改完代码日志却没变,先看这里对不对,
        /// 免得对着旧程序集查半天(蓝牙那个驱动吃过这个亏)。
        /// </summary>
        public const string SourceStamp = "2026-09-01-a";

        private readonly ILogService _logger;

        private const int MaxRetries = 3;
        private const int RetryDelayMs = 100;
        private const int IoTimeoutMs = 3000;

        /// <summary>按 DeviceId 隔离的实例锁,保证同一台仪器的 IO 串行。</summary>
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _instanceLocks = new();

        public string ScreenKey => "ShakingIncubatorScreen";

        public ShakingIncubator_ModbusRTU()
        {
            _logger = LogManager.GetLogger<ShakingIncubator_ModbusRTU>();

            DeviceId = "SKI0001";
            Name = "组合式培养箱";
            Manufacturer = "恒温振荡培养箱  MB-820 PHRYH_ST70_206Ti(Modbus RTU / RS485)";
            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/ShakingIncubator.png";
            Category = DeviceCategories.Reactors;
            ComId = "SKI0001";
            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();

            AllowedRegions = new List<RegionType>
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            Parameters.Variables.Add(new StringParameter("DeviceId", "SKI0001", "设备实例编号")
            {
                Options = new ObservableCollection<string> { "SKI0001", "SKI0002", "SKI0003", "SKI0004" }
            });

            Parameters.Variables.Add(new StringParameter("串口号", SerialPortOptions.DefaultPort(), "RS485 转换器对应的串口")
            {
                Options = SerialPortOptions.CreateOptions()
            });

            // 手册"说明"列写死 9600,N,8,1。仍然做成下拉是因为现场常见"仪器被人
            // 改过通信参数"这种事,锁死了反而没法救;默认值按手册给。
            Parameters.Variables.Add(new StringParameter("波特率", "9600", "手册规定 9600")
            {
                Options = new ObservableCollection<string> { "4800", "9600", "19200", "38400", "57600", "115200" }
            });

            Parameters.Variables.Add(new StringParameter("校验位", "None", "手册规定无校验")
            {
                Options = new ObservableCollection<string> { "None", "Odd", "Even", "Mark", "Space" }
            });

            Parameters.Variables.Add(new StringParameter("数据位", "8")
            {
                Options = new ObservableCollection<string> { "5", "6", "7", "8" }
            });

            Parameters.Variables.Add(new StringParameter("停止位", "One")
            {
                Options = new ObservableCollection<string> { "None", "One", "Two", "OnePointFive" }
            });

            Parameters.Variables.Add(new StringParameter("通信方式", "Direct", "通信方式")
            {
                Options = new ObservableCollection<string> { "Direct", "ModbusTcp", "ZLanGateway", "RemoteServer" }
            });

            // 手册没写从站地址。现场大多是出厂默认 1,做成可配。
            Parameters.Variables.Add(new NumberParameter("Modbus站号", 1, 254, 1, "Modbus 从机站号 (1~254),手册未规定,现场按仪器设置填"));

            InitializeCommands();
        }

        public override bool SupportsZLanGateway => true;

        #region 可监控参数

        public override IReadOnlyList<CollectableParameter> CollectableParameters => new[]
        {
            new CollectableParameter("TEMP_PV",  "腔内温度", "℃",  "获取所有数据", "TEMP_PV", isKey: true),
            new CollectableParameter("TEMP_SET", "温度设定", "℃",  "获取所有数据", "TEMP_SET"),
            new CollectableParameter("SPEED_PV", "转速",     "RPM", "获取所有数据", "SPEED_PV"),
            new CollectableParameter("SPEED_SET","转速设定", "RPM", "获取所有数据", "SPEED_SET"),
            new CollectableParameter("FAN_PV",   "风速",     "档",  "获取所有数据", "FAN_PV"),
        };

        public override int AutoCollectIntervalMs => 1000;

        #endregion

        #region 连接

        /// <summary>
        /// 直连模式的连接探测:读 0x0000 产品类型编号。
        ///
        /// 这台仪器难得地给了一个身份寄存器,能把"串口通了"和"对面确实是这台仪器"
        /// 分开 —— 别的 Modbus 驱动只能读一个普通寄存器,读到什么都算连上,
        /// 结果串口号选错时会"连接成功"然后一路读出垃圾。
        ///
        /// 编号对不上只告警不拦:同系列不同型号编号可能不同,为一个型号码
        /// 把人挡在门外,比让人看见一条告警更糟。
        /// </summary>
        protected override async Task<bool> OnConnectAsync() => await ProbeAsync("直连");

        protected override Task<bool> OnConnectViaGatewayAsync() => ProbeAsync("ZLAN 网关");

        private async Task<bool> ProbeAsync(string via)
        {
            try
            {
                _logger.LogInformation(
                    $"[恒温振荡培养箱 {SourceStamp}] 经{via}连接,站号 {GetSlaveAddress()}," +
                    $"串口 {Parameters.GetValue<string>("串口号")} @ {Parameters.GetValue<string>("波特率")}/8/N/1");

                ushort[] regs = await ReadRegistersAsync(Regs.ProductType, 2, CancellationToken.None);
                if (regs.Length < 2) { ErrorLog("连接失败:读产品类型编号没有得到有效应答"); return false; }

                if (regs[0] != Regs.ExpectedProductType)
                    _logger.LogWarning(
                        $"[恒温振荡培养箱] 产品类型编号是 0x{regs[0]:X4},手册标称 0x{Regs.ExpectedProductType:X4}。" +
                        "仍按本协议继续,但如果读数明显不对,先确认站号/串口有没有选到别的仪器。");

                InfoLog($"连接成功:产品类型编号 0x{regs[0]:X4},产品序列号 {regs[1]}");
                return true;
            }
            catch (TimeoutException)
            {
                ErrorLog("连接超时:串口通了但仪器没应答 —— 多半是站号不对,或者 485 的 A/B 反接了");
                return false;
            }
            catch (Exception e)
            {
                ErrorLog($"连接失败:{e.Message}", e);
                return false;
            }
        }

        protected override async Task<bool> OnCheckConnectionAsync()
        {
            try
            {
                var regs = await ReadRegistersAsync(Regs.RunState, 1, CancellationToken.None);
                return regs.Length == 1;
            }
            catch { return false; }
        }

        #endregion

        #region 命令

        private void InitializeCommands()
        {
            // ── 读:一条命令读全 ─────────────────────────────────
            Commands.Add(new DeviceCommand
            {
                Name = "获取所有数据",
                HelpText = "一次读回全部 58 个寄存器(分两批,受手册单次 32 个的限制):" +
                           "温度/转速/风速的测量值与设定值、量程、运行状态、报警位、" +
                           "功能开关、预约与系统时钟。屏幕轮询用的就是它。",
                OutputParameters = new DeviceParameters { Variables = BuildAllOutputs() },
                AsyncAction = AsyncWrapAction("获取所有数据", _ => ReadAllAsync())
            });

            Commands.Add(new DeviceCommand
            {
                Name = "获取报警明细",
                HelpText = "把报警状态 1 / 2 两个位域拆成中文明细",
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("HasAlarm", false, "是否有报警", true),
                        new NumberParameter("Count", 0, 32, 0, "报警条数", true),
                        new StringParameter("Alarms", "", "报警明细,逗号分隔"),
                        new BooleanParameter("Success", false, "是否读到"),
                    }
                },
                AsyncAction = AsyncWrapAction("获取报警明细", _ => ReadAlarmsAsync())
            });

            // ── 运行控制 ────────────────────────────────────────
            AddSwitchCommand("启动", "让仪器按当前模式开始运行", Regs.RunStop, 1);
            AddSwitchCommand("停止", "停止运行", Regs.RunStop, 0);
            AddOnOffCommand("照明开关", "0=关闭,1=开启(仅在照明功能开启时有效)", Regs.LightSwitch);
            AddOnOffCommand("灭菌开关", "0=关闭,1=开启(仅在灭菌功能开启时有效)", Regs.SterilizeSwitch);

            Commands.Add(new DeviceCommand
            {
                Name = "设置当前模式",
                HelpText = "0=定值模式,1~5=程式模式 1~5",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TargetValue", 0, 5, 0, "0 定值 / 1~5 程式", false)
                    }
                },
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("设置当前模式", async p =>
                {
                    ushort v = (ushort)(int)(p?.GetNullableValue<double>("TargetValue") ?? 0);
                    return Success(await WriteSingleAsync(Regs.SetMode, v, CancellationToken.None));
                })
            });

            // ── 定值模式参数 ────────────────────────────────────
            //
            // 这条命令就是日常"把温度调到 37 度"要用的那条。它不是写 0x0008,
            // 而是往编辑窗口写一批值再落一个写标志 —— 原因见文件抬头。
            Commands.Add(new DeviceCommand
            {
                Name = "设置定值参数",
                HelpText = "设置定值模式下的温度 / 转速 / 风速 / 运行时长。" +
                           "内部按手册流程:先写编辑窗口(0x002B~0x002F),最后写 0x0026=2 提交。" +
                           "转速仅在「转速功能」开启时有效,风速仅在「风机可调功能」开启时有效。",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("Temp", -40, 200, 37, "温度设定", false, "℃") { DecimalPlaces = 1 },
                        new NumberParameter("Speed", 0, 2000, 0, "转速设定", false, "RPM"),
                        new NumberParameter("Fan", 0, 10, 0, "风速设定档位", false, "档"),
                        new NumberParameter("Hour", 0, 9999, 0, "运行时长-时", false, "h"),
                        new NumberParameter("Minute", 0, 59, 0, "运行时长-分", false, "min"),
                    }
                },
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("设置定值参数", p => WriteEditWindowAsync(p, programMode: false))
            });

            // ── 程式段编辑 ──────────────────────────────────────
            Commands.Add(new DeviceCommand
            {
                Name = "设置程式段",
                HelpText = "写一段程式。模式 1~5,段号 1~100。" +
                           "总周期/总段数只在程式模式下有意义,会与本段一起写下去。",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("Mode", 1, 5, 1, "程式模式 1~5", false),
                        new NumberParameter("Segment", 1, 100, 1, "要修改的段号", false),
                        new NumberParameter("TotalCycle", 0, 999, 1, "此模式的总周期", false),
                        new NumberParameter("TotalSegment", 1, 100, 1, "此模式的总段数", false),
                        new NumberParameter("Hour", 0, 9999, 0, "本段时长-时", false, "h"),
                        new NumberParameter("Minute", 0, 59, 0, "本段时长-分", false, "min"),
                        new NumberParameter("Temp", -40, 200, 37, "本段温度", false, "℃") { DecimalPlaces = 1 },
                        new NumberParameter("Speed", 0, 2000, 0, "本段转速", false, "RPM"),
                        new NumberParameter("Fan", 0, 10, 0, "本段风速档位", false, "档"),
                    }
                },
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("设置程式段", p => WriteEditWindowAsync(p, programMode: true))
            });

            Commands.Add(new DeviceCommand
            {
                Name = "读取程式段",
                HelpText = "读一段程式的设定值。内部按手册流程:先写模式/段号,再写 0x0026=1 读标志," +
                           "然后回读 0x0029~0x002F。模式填 0 表示读定值模式。",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("Mode", 0, 5, 0, "0 定值 / 1~5 程式", false),
                        new NumberParameter("Segment", 1, 100, 1, "段号(定值模式忽略)", false),
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TotalCycle", 0, 999, 0, "此模式的总周期", true),
                        new NumberParameter("TotalSegment", 0, 100, 0, "此模式的总段数", true),
                        new NumberParameter("Hour", 0, 9999, 0, "本段时长-时", true, "h"),
                        new NumberParameter("Minute", 0, 59, 0, "本段时长-分", true, "min"),
                        new NumberParameter("Temp", -100, 300, 0, "本段温度", true, "℃"),
                        new NumberParameter("Speed", 0, 65535, 0, "本段转速", true, "RPM"),
                        new NumberParameter("Fan", 0, 10, 0, "本段风速档位", true, "档"),
                        new BooleanParameter("Success", false, "是否读到"),
                    }
                },
                AsyncAction = AsyncWrapAction("读取程式段", ReadSegmentAsync)
            });

            // ── 预约 ────────────────────────────────────────────
            Commands.Add(new DeviceCommand
            {
                Name = "设置预约",
                HelpText = "设置预约启动时间并开关预约。仅在「预约功能」开启时有效。" +
                           "时间先写,预约启停最后写。",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("Enable", 0, 1, 0, "0 关闭预约 / 1 开启预约", false),
                        new NumberParameter("Year", 2015, 2037, 2026, "年", false),
                        new NumberParameter("Month", 1, 12, 1, "月", false),
                        new NumberParameter("Day", 1, 31, 1, "日", false),
                        new NumberParameter("Hour", 0, 23, 0, "时", false),
                        new NumberParameter("Minute", 0, 59, 0, "分", false),
                    }
                },
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("设置预约", SetBookingAsync)
            });

            // ── 系统时钟 ────────────────────────────────────────
            Commands.Add(new DeviceCommand
            {
                Name = "同步系统时间",
                HelpText = "把上位机当前时间写进仪器的 0x0030~0x0035。" +
                           "预约和记录都依赖仪器自己的时钟,换季或断电后建议同步一次。",
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("同步系统时间", _ => SyncClockAsync())
            });
        }

        private static DeviceParameters SuccessOnly() => new()
        {
            Variables = new ObservableCollection<ParameterBase>
            {
                new BooleanParameter("Success", false, "是否下发成功", true)
            }
        };

        private static DeviceParameters Success(bool ok)
        {
            var o = SuccessOnly();
            o.SetValue("Success", ok);
            return o;
        }

        /// <summary>固定值的开关命令(启动/停止)。</summary>
        private void AddSwitchCommand(string name, string help, ushort reg, ushort value)
        {
            Commands.Add(new DeviceCommand
            {
                Name = name,
                HelpText = help,
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction(name, async _ =>
                {
                    if (IsSimulationMode) return Success(true);
                    return Success(await WriteSingleAsync(reg, value, CancellationToken.None));
                })
            });
        }

        /// <summary>0/1 参数的开关命令(照明/灭菌)。</summary>
        private void AddOnOffCommand(string name, string help, ushort reg)
        {
            Commands.Add(new DeviceCommand
            {
                Name = name,
                HelpText = help,
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TargetValue", 0, 1, 0, help, false)
                    }
                },
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction(name, async p =>
                {
                    if (IsSimulationMode) return Success(true);
                    ushort v = (ushort)(int)(p?.GetNullableValue<double>("TargetValue") ?? 0);
                    return Success(await WriteSingleAsync(reg, v, CancellationToken.None));
                })
            });
        }

        #endregion

        #region 读

        private static ObservableCollection<ParameterBase> BuildAllOutputs() => new()
        {
            // 身份
            new NumberParameter("PRODUCT_TYPE", 0, 65535, 0, "产品类型编号", true),
            new NumberParameter("SERIAL_NO", 0, 65535, 0, "产品序列号", true),

            // 测量与设定
            new NumberParameter("TEMP_PV", -100, 300, 0, "腔内温度测量值", true, "℃"),
            new NumberParameter("TEMP_SET", -40, 200, 0, "温度设定值", true, "℃"),
            new NumberParameter("SPEED_PV", 0, 65535, 0, "转速测量值", true, "RPM"),
            new NumberParameter("SPEED_SET", 0, 2000, 0, "转速设定值", true, "RPM"),
            new NumberParameter("FAN_PV", 0, 10, 0, "风速测量档位", true, "档"),
            new NumberParameter("FAN_SET", 0, 10, 0, "风速设定档位", true, "档"),

            // 量程(仪器自报,界面拿它约束输入框)
            new NumberParameter("TEMP_LO", -100, 300, 0, "温度设定下限", true, "℃"),
            new NumberParameter("TEMP_HI", -100, 300, 0, "温度设定上限", true, "℃"),
            new NumberParameter("SPEED_LO", 0, 65535, 0, "速度设定下限", true, "RPM"),
            new NumberParameter("SPEED_HI", 0, 65535, 0, "速度设定上限", true, "RPM"),
            new NumberParameter("FAN_LO", 0, 10, 0, "风速设定下限", true, "档"),
            new NumberParameter("FAN_HI", 0, 10, 0, "风速设定上限", true, "档"),

            // 计时
            new NumberParameter("ELAPSED_H", 0, 9999, 0, "已计时-时", true, "h"),
            new NumberParameter("ELAPSED_M", 0, 59, 0, "已计时-分", true, "min"),
            new NumberParameter("SET_H", 0, 9999, 0, "设定时长-时", true, "h"),
            new NumberParameter("SET_M", 0, 59, 0, "设定时长-分", true, "min"),

            // 运行
            new NumberParameter("MODE", 0, 5, 0, "当前模式 0定值/1~5程式", true),
            new StringParameter("MODE_TEXT", "", "当前模式文字"),
            new NumberParameter("RUN_SEGMENT", 0, 100, 0, "当前运行段数", true),
            new NumberParameter("RUN_CYCLE", 0, 999, 0, "当前运行周期", true),
            new NumberParameter("SET_SEGMENT", 0, 100, 0, "当前设定段数", true),
            new NumberParameter("SET_CYCLE", 0, 999, 0, "当前设定周期", true),
            new NumberParameter("RUN_STATE", 0, 4, 0, "运行状态码", true),
            new StringParameter("RUN_STATE_TEXT", "", "运行状态文字"),
            new BooleanParameter("RUNNING", false, "是否在运行", true),

            // 报警
            new BooleanParameter("HAS_ALARM", false, "是否有报警", true),
            new NumberParameter("ALARM1", 0, 65535, 0, "报警状态1 位域原值", true),
            new NumberParameter("ALARM2", 0, 65535, 0, "报警状态2 位域原值", true),
            new StringParameter("ALARM_TEXT", "", "报警明细"),

            // 功能开关(决定界面上哪几块可用)
            new BooleanParameter("FUNC_SPEED", false, "转速功能是否开启", true),
            new BooleanParameter("FUNC_FAN", false, "风机可调功能是否开启", true),
            new BooleanParameter("FUNC_LIGHT", false, "照明功能是否开启", true),
            new BooleanParameter("FUNC_STERILIZE", false, "灭菌功能是否开启", true),
            new BooleanParameter("FUNC_BOOK", false, "预约功能是否开启", true),

            // 开关状态(来自报警状态1 的 bit8/bit9)
            new BooleanParameter("LIGHT_ON", false, "照明当前是否开启", true),
            new BooleanParameter("STERILIZE_ON", false, "灭菌当前是否开启", true),

            // 预约与时钟
            new BooleanParameter("BOOK_ON", false, "预约是否开启", true),
            new StringParameter("BOOK_TIME", "", "预约时间"),
            new StringParameter("CLOCK", "", "仪器系统时间"),

            new BooleanParameter("Success", false, "是否取到有效数据", true),
        };

        private async Task<DeviceParameters> ReadAllAsync()
        {
            var o = new DeviceParameters { Variables = BuildAllOutputs() };

            if (IsSimulationMode)
            {
                FillSimulated(o);
                return o;
            }

            // 手册限制单次最多 32 个地址,所以必须切两刀。
            ushort[] a = await ReadRegistersAsync(Regs.BlockAStart, Regs.BlockACount, CancellationToken.None);
            if (a.Length < Regs.BlockACount)
            {
                o.SetValue("Success", false);
                return o;
            }

            // 第二块失败不整条失败:只读区已经拿到了,界面上先把测量值显示出来,
            // 比整屏 "--" 有用。设定区的项保持默认值,Success 仍标 true。
            ushort[] b;
            try { b = await ReadRegistersAsync(Regs.BlockBStart, Regs.BlockBCount, CancellationToken.None); }
            catch (Exception ex)
            {
                _logger.LogWarning($"[恒温振荡培养箱] 读设定区(0x0020~0x0039)失败,只显示测量值:{ex.Message}");
                b = Array.Empty<ushort>();
            }

            ushort A(ushort addr) => a[addr - Regs.BlockAStart];
            ushort B(ushort addr) => b.Length >= Regs.BlockBCount ? b[addr - Regs.BlockBStart] : (ushort)0;

            o.SetValue("PRODUCT_TYPE", (double)A(Regs.ProductType));
            o.SetValue("SERIAL_NO", (double)A(Regs.SerialNo));

            o.SetValue("TEMP_PV", Tenths(A(Regs.TempPv)));
            o.SetValue("TEMP_SET", Tenths(A(Regs.TempSet)));
            o.SetValue("SPEED_PV", (double)(short)A(Regs.SpeedPv));
            o.SetValue("SPEED_SET", (double)(short)A(Regs.SpeedSet));
            o.SetValue("FAN_PV", (double)(short)A(Regs.FanPv));
            o.SetValue("FAN_SET", (double)(short)A(Regs.FanSet));

            o.SetValue("TEMP_LO", Tenths(A(Regs.TempLoLimit)));
            o.SetValue("TEMP_HI", Tenths(A(Regs.TempHiLimit)));
            o.SetValue("SPEED_LO", (double)(short)A(Regs.SpeedLoLimit));
            o.SetValue("SPEED_HI", (double)(short)A(Regs.SpeedHiLimit));
            o.SetValue("FAN_LO", (double)(short)A(Regs.FanLoLimit));
            o.SetValue("FAN_HI", (double)(short)A(Regs.FanHiLimit));

            o.SetValue("ELAPSED_H", (double)(short)A(Regs.ElapsedHour));
            o.SetValue("ELAPSED_M", (double)(short)A(Regs.ElapsedMinute));
            o.SetValue("SET_H", (double)(short)A(Regs.SetHour));
            o.SetValue("SET_M", (double)(short)A(Regs.SetMinute));

            int mode = (short)A(Regs.CurrentMode);
            o.SetValue("MODE", (double)mode);
            o.SetValue("MODE_TEXT", ModeText(mode));
            o.SetValue("RUN_SEGMENT", (double)(short)A(Regs.RunSegment));
            o.SetValue("RUN_CYCLE", (double)(short)A(Regs.RunCycle));
            o.SetValue("SET_SEGMENT", (double)(short)A(Regs.SetSegment));
            o.SetValue("SET_CYCLE", (double)(short)A(Regs.SetCycle));

            int state = (short)A(Regs.RunState);
            o.SetValue("RUN_STATE", (double)state);
            o.SetValue("RUN_STATE_TEXT", RunStateText(state));
            // 2=正在运行,3=正在计时,4=自整定中 —— 这三种都算"仪器在动"
            o.SetValue("RUNNING", state >= 2);

            ushort al1 = A(Regs.Alarm1), al2 = A(Regs.Alarm2);
            o.SetValue("ALARM1", (double)al1);
            o.SetValue("ALARM2", (double)al2);
            o.SetValue("HAS_ALARM", (short)A(Regs.AlarmState) != 0);
            o.SetValue("ALARM_TEXT", DescribeAlarms(al1, al2));

            o.SetValue("FUNC_SPEED", (short)A(Regs.SpeedFunc) != 0);
            o.SetValue("FUNC_FAN", (short)A(Regs.FanFunc) != 0);
            o.SetValue("FUNC_LIGHT", (short)A(Regs.LightFunc) != 0);
            o.SetValue("FUNC_STERILIZE", (short)A(Regs.SterilizeFunc) != 0);
            o.SetValue("FUNC_BOOK", (short)A(Regs.BookFunc) != 0);

            // 照明/灭菌的**当前状态**在报警状态1 的 bit8/bit9 里,不在 0x0038/0x0039。
            // 那两个是写入口,回读的是上位机上次写的值,不代表仪器实际状态。
            o.SetValue("LIGHT_ON", (al1 >> Alarm1Bits.LightStateBit & 1) != 0);
            o.SetValue("STERILIZE_ON", (al1 >> Alarm1Bits.SterilizeStateBit & 1) != 0);

            if (b.Length >= Regs.BlockBCount)
            {
                o.SetValue("BOOK_ON", (short)B(Regs.BookOnOff) != 0);
                o.SetValue("BOOK_TIME",
                    $"{(short)B(Regs.BookYear):0000}/{(short)B(Regs.BookMonth):00}/{(short)B(Regs.BookDay):00} " +
                    $"{(short)B(Regs.BookHour):00}:{(short)B(Regs.BookMinute):00}");
                o.SetValue("CLOCK",
                    $"{(short)B(Regs.ClockYear):0000}/{(short)B(Regs.ClockMonth):00}/{(short)B(Regs.ClockDay):00} " +
                    $"{(short)B(Regs.ClockHour):00}:{(short)B(Regs.ClockMinute):00}:{(short)B(Regs.ClockSecond):00}");
            }

            o.SetValue("Success", true);
            return o;
        }

        private async Task<DeviceParameters> ReadAlarmsAsync()
        {
            var o = new DeviceParameters
            {
                Variables = new ObservableCollection<ParameterBase>
                {
                    new BooleanParameter("HasAlarm", false, "是否有报警", true),
                    new NumberParameter("Count", 0, 32, 0, "报警条数", true),
                    new StringParameter("Alarms", "", "报警明细"),
                    new BooleanParameter("Success", false, "是否读到"),
                }
            };

            if (IsSimulationMode)
            {
                o.SetValue("Success", true);
                return o;
            }

            ushort[] r = await ReadRegistersAsync(Regs.Alarm1, 2, CancellationToken.None);
            if (r.Length < 2) return o;

            var list = CollectAlarms(r[0], r[1]);
            o.SetValue("HasAlarm", list.Count > 0);
            o.SetValue("Count", (double)list.Count);
            o.SetValue("Alarms", string.Join("、", list));
            o.SetValue("Success", true);
            return o;
        }

        private async Task<DeviceParameters> ReadSegmentAsync(DeviceParameters? p)
        {
            var o = new DeviceParameters
            {
                Variables = new ObservableCollection<ParameterBase>
                {
                    new NumberParameter("TotalCycle", 0, 999, 0, "此模式的总周期", true),
                    new NumberParameter("TotalSegment", 0, 100, 0, "此模式的总段数", true),
                    new NumberParameter("Hour", 0, 9999, 0, "本段时长-时", true, "h"),
                    new NumberParameter("Minute", 0, 59, 0, "本段时长-分", true, "min"),
                    new NumberParameter("Temp", -100, 300, 0, "本段温度", true, "℃"),
                    new NumberParameter("Speed", 0, 65535, 0, "本段转速", true, "RPM"),
                    new NumberParameter("Fan", 0, 10, 0, "本段风速档位", true, "档"),
                    new BooleanParameter("Success", false, "是否读到"),
                }
            };

            if (IsSimulationMode) { o.SetValue("Success", true); return o; }

            ushort mode = (ushort)(int)(p?.GetNullableValue<double>("Mode") ?? 0);
            ushort seg = (ushort)(int)(p?.GetNullableValue<double>("Segment") ?? 1);
            var token = CancellationToken.None;

            // 手册流程:先指定要看哪一段,再落"读标志",仪器才把那一段搬到编辑窗口。
            if (!await WriteMultipleAsync(Regs.EditMode, new[] { mode, seg }, token)) return o;
            if (!await WriteSingleAsync(Regs.EditFlag, 1, token)) return o;

            // 仪器要一点时间把那一段搬进编辑窗口。手册没给时间,给 120ms 余量;
            // 不等就回读,读到的可能还是上一次的内容。
            await Task.Delay(120, token).ConfigureAwait(false);

            ushort[] r = await ReadRegistersAsync(Regs.EditTotalCycle, 7, token);
            if (r.Length < 7) return o;

            o.SetValue("TotalCycle", (double)(short)r[0]);
            o.SetValue("TotalSegment", (double)(short)r[1]);
            o.SetValue("Hour", (double)(short)r[2]);
            o.SetValue("Minute", (double)(short)r[3]);
            o.SetValue("Temp", Tenths(r[4]));
            o.SetValue("Speed", (double)(short)r[5]);
            o.SetValue("Fan", (double)(short)r[6]);
            o.SetValue("Success", true);
            return o;
        }

        #endregion

        #region 写

        /// <summary>
        /// 往编辑窗口写一批参数并提交。定值模式与程式模式共用一段代码,
        /// 差别只在于程式模式多写 段号 / 总周期 / 总段数 三项。
        /// </summary>
        private async Task<DeviceParameters> WriteEditWindowAsync(DeviceParameters? p, bool programMode)
        {
            if (IsSimulationMode) return Success(true);

            var token = CancellationToken.None;

            double temp = p?.GetNullableValue<double>("Temp") ?? 0;
            ushort speed = (ushort)(int)(p?.GetNullableValue<double>("Speed") ?? 0);
            ushort fan = (ushort)(int)(p?.GetNullableValue<double>("Fan") ?? 0);
            ushort hour = (ushort)(int)(p?.GetNullableValue<double>("Hour") ?? 0);
            ushort minute = (ushort)(int)(p?.GetNullableValue<double>("Minute") ?? 0);

            ushort mode = programMode ? (ushort)(int)(p?.GetNullableValue<double>("Mode") ?? 1) : (ushort)0;
            ushort seg = programMode ? (ushort)(int)(p?.GetNullableValue<double>("Segment") ?? 1) : (ushort)1;
            ushort cycles = programMode ? (ushort)(int)(p?.GetNullableValue<double>("TotalCycle") ?? 1) : (ushort)0;
            ushort totalSeg = programMode ? (ushort)(int)(p?.GetNullableValue<double>("TotalSegment") ?? 1) : (ushort)1;

            // 温度 ×10 送下去。负温用补码,手册说 s16 按 Modbus 标准补码传输。
            short tempRaw = (short)Math.Round(temp * 10.0, MidpointRounding.AwayFromZero);

            // 0x0027~0x002F 是连续的九个寄存器,一条 0x10 就写完 —— 手册明确
            // 允许 0x10。分成九条 0x06 也能work,但中间任何一条掉了就会写进
            // 半套参数,而提交标志一落仪器就当真了。一条多写把这个窗口关掉。
            var block = new ushort[]
            {
                mode,                       // 0x0027 要修改的模式
                seg,                        // 0x0028 要修改的段数
                cycles,                     // 0x0029 此模式的总周期
                totalSeg,                   // 0x002A 此模式的总段数
                hour,                       // 0x002B 时
                minute,                     // 0x002C 分
                unchecked((ushort)tempRaw), // 0x002D 温度 ×10
                speed,                      // 0x002E 转速
                fan,                        // 0x002F 风速
            };

            if (!await WriteMultipleAsync(Regs.EditMode, block, token))
                return Success(false);

            // 提交。必须是最后一步。
            bool ok = await WriteSingleAsync(Regs.EditFlag, 2, token);

            InfoLog(programMode
                ? $"写程式段:模式{mode} 第{seg}段 共{totalSeg}段/{cycles}周期 {hour}时{minute}分 {temp:F1}℃ {speed}RPM {fan}档 → {ok}"
                : $"写定值参数:{temp:F1}℃ {speed}RPM {fan}档 {hour}时{minute}分 → {ok}");

            return Success(ok);
        }

        private async Task<DeviceParameters> SetBookingAsync(DeviceParameters? p)
        {
            if (IsSimulationMode) return Success(true);

            var token = CancellationToken.None;
            ushort enable = (ushort)(int)(p?.GetNullableValue<double>("Enable") ?? 0);

            var time = new ushort[]
            {
                (ushort)(int)(p?.GetNullableValue<double>("Year")   ?? 2026),
                (ushort)(int)(p?.GetNullableValue<double>("Month")  ?? 1),
                (ushort)(int)(p?.GetNullableValue<double>("Day")    ?? 1),
                (ushort)(int)(p?.GetNullableValue<double>("Hour")   ?? 0),
                (ushort)(int)(p?.GetNullableValue<double>("Minute") ?? 0),
            };

            // 时间先写完,再开预约。反过来的话仪器可能拿着上一次的旧时间就开始等,
            // 那一下是真的会在错误的时间启动设备的。
            if (!await WriteMultipleAsync(Regs.BookYear, time, token)) return Success(false);

            bool ok = await WriteSingleAsync(Regs.BookOnOff, enable, token);
            InfoLog($"设置预约:{(enable != 0 ? "开启" : "关闭")} {time[0]}/{time[1]:00}/{time[2]:00} {time[3]:00}:{time[4]:00} → {ok}");
            return Success(ok);
        }

        private async Task<DeviceParameters> SyncClockAsync()
        {
            if (IsSimulationMode) return Success(true);

            var now = DateTime.Now;
            var vals = new ushort[]
            {
                (ushort)now.Year, (ushort)now.Month, (ushort)now.Day,
                (ushort)now.Hour, (ushort)now.Minute, (ushort)now.Second,
            };

            // 手册给的年份范围是 2015~2037,超出去仪器多半直接拒。
            if (now.Year < 2015 || now.Year > 2037)
            {
                ErrorLog($"上位机年份 {now.Year} 超出仪器可接受范围 2015~2037,不下发");
                return Success(false);
            }

            bool ok = await WriteMultipleAsync(Regs.ClockYear, vals, CancellationToken.None);
            InfoLog($"同步系统时间到 {now:yyyy/MM/dd HH:mm:ss} → {ok}");
            return Success(ok);
        }

        #endregion

        #region 取值与文字

        /// <summary>寄存器值 → 一位小数的实际值。s16 负数按补码。</summary>
        private static double Tenths(ushort raw) => (short)raw / 10.0;

        private static string ModeText(int mode) => mode switch
        {
            0 => "定值模式",
            >= 1 and <= 5 => $"程式模式 {mode}",
            _ => $"未知({mode})",
        };

        private static string RunStateText(int s) => s switch
        {
            0 => "停止",
            1 => "预约等待",
            2 => "正在运行",
            3 => "正在计时",
            4 => "自整定中",
            _ => $"未知({s})",
        };

        private static List<string> CollectAlarms(ushort alarm1, ushort alarm2)
        {
            var list = new List<string>();
            // bit0~bit7 才是故障位;bit8/bit9 是照明/灭菌状态,不是报警。
            for (int i = 0; i < Alarm1Bits.Names.Length; i++)
                if ((alarm1 >> i & 1) != 0) list.Add(Alarm1Bits.Names[i]);
            for (int i = 0; i < Alarm2Bits.Names.Length; i++)
                if ((alarm2 >> i & 1) != 0) list.Add(Alarm2Bits.Names[i]);
            return list;
        }

        private static string DescribeAlarms(ushort alarm1, ushort alarm2)
        {
            var list = CollectAlarms(alarm1, alarm2);
            return list.Count == 0 ? "" : string.Join("、", list);
        }

        /// <summary>模拟模式下造一份看起来合理的读数,方便无仪器时调流程和屏幕。</summary>
        private static void FillSimulated(DeviceParameters o)
        {
            double t = Environment.TickCount64 / 1000.0;

            o.SetValue("PRODUCT_TYPE", (double)Regs.ExpectedProductType);
            o.SetValue("SERIAL_NO", 12345d);
            o.SetValue("TEMP_PV", Math.Round(37.0 + 0.4 * Math.Sin(t / 9.0), 1));
            o.SetValue("TEMP_SET", 37.0);
            o.SetValue("SPEED_PV", Math.Round(200 + 6 * Math.Sin(t / 5.0)));
            o.SetValue("SPEED_SET", 200d);
            o.SetValue("FAN_PV", 3d);
            o.SetValue("FAN_SET", 3d);
            o.SetValue("TEMP_LO", -10d);
            o.SetValue("TEMP_HI", 60d);
            o.SetValue("SPEED_LO", 0d);
            o.SetValue("SPEED_HI", 300d);
            o.SetValue("FAN_LO", 0d);
            o.SetValue("FAN_HI", 10d);
            o.SetValue("ELAPSED_H", 1d);
            o.SetValue("ELAPSED_M", 23d);
            o.SetValue("SET_H", 8d);
            o.SetValue("SET_M", 0d);
            o.SetValue("MODE", 0d);
            o.SetValue("MODE_TEXT", ModeText(0));
            o.SetValue("RUN_STATE", 3d);
            o.SetValue("RUN_STATE_TEXT", RunStateText(3));
            o.SetValue("RUNNING", true);
            o.SetValue("FUNC_SPEED", true);
            o.SetValue("FUNC_FAN", true);
            o.SetValue("FUNC_LIGHT", true);
            o.SetValue("FUNC_STERILIZE", true);
            o.SetValue("FUNC_BOOK", true);
            o.SetValue("LIGHT_ON", true);
            o.SetValue("CLOCK", DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss"));
            o.SetValue("Success", true);
        }

        #endregion

        #region 通信层

        private byte GetSlaveAddress()
        {
            try
            {
                var addr = (int)Parameters.GetValue<double>("Modbus站号");
                return (byte)(addr is >= 1 and <= 254 ? addr : 1);
            }
            catch { return 1; }
        }

        private SemaphoreSlim GetInstanceLock()
        {
            var key = Parameters?.GetValue<string>("DeviceId") ?? DeviceId ?? "default";
            return _instanceLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        }

        private Task<IDeviceTransport> GetTransportAsync()
        {
            var cfg = new SerialPortConfig
            {
                PortName = Parameters.GetValue<string>("串口号") ?? "COM1",
                BaudRate = int.TryParse(Parameters.GetValue<string>("波特率"), out var b) ? b : 9600,
                Parity = Parameters.GetValue<string>("校验位") ?? "None",
                DataBits = int.TryParse(Parameters.GetValue<string>("数据位"), out var d) ? d : 8,
                StopBits = Parameters.GetValue<string>("停止位") ?? "One",
                ReadTimeoutMs = IoTimeoutMs,
                WriteTimeoutMs = IoTimeoutMs,
            };
            return CreateTransportAsync(cfg);
        }

        /// <summary>
        /// 读保持寄存器(0x03)。返回长度为 count 的数组;失败返回空数组。
        ///
        /// 手册规定单次最多 32 个,这里做硬拦截:超了就直接抛,而不是发出去等
        /// 仪器返回一个语焉不详的异常码 —— 越界是调用方的 bug,应该在这里就炸。
        /// </summary>
        private async Task<ushort[]> ReadRegistersAsync(ushort start, ushort count, CancellationToken token)
        {
            if (count is < 1 or > 32)
                throw new ArgumentOutOfRangeException(nameof(count), count, "手册规定每次最多读 32 个地址变量");

            byte[] cmd = BuildReadCmd(start, count);
            return await RetryAsync(async () =>
            {
                var sem = GetInstanceLock();
                await sem.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    byte[] recv = new byte[512];
                    using var transport = await GetTransportAsync().ConfigureAwait(false);
                    int len = await transport.SendAndReceiveAsync(cmd, recv, IoTimeoutMs, token).ConfigureAwait(false);

                    int expectBytes = count * 2;
                    // 站号(1)+功能码(1)+字节数(1)+数据+CRC(2)
                    int expectLen = 5 + expectBytes;

                    if (len < 5)
                    {
                        _logger.LogWarning($"[恒温振荡培养箱] 应答太短:{len} 字节");
                        return Array.Empty<ushort>();
                    }
                    if (recv[0] != GetSlaveAddress())
                    {
                        _logger.LogWarning($"[恒温振荡培养箱] 站号不匹配:期望 {GetSlaveAddress()},实际 {recv[0]}");
                        return Array.Empty<ushort>();
                    }
                    if ((recv[1] & 0x80) != 0)
                    {
                        _logger.LogWarning(
                            $"[恒温振荡培养箱] 读 0x{start:X4}+{count} 被拒,Modbus 异常码 0x{recv[2]:X2}" +
                            (recv[2] == 0x02 ? "(非法地址 —— 这一段里有仪器上不存在的寄存器)" : ""));
                        return Array.Empty<ushort>();
                    }
                    if (recv[1] != 0x03 || len < expectLen || recv[2] != expectBytes)
                    {
                        _logger.LogWarning(
                            $"[恒温振荡培养箱] 应答格式不对:功能码 0x{recv[1]:X2},字节数 {recv[2]},收到 {len} 字节,期望 {expectLen}");
                        return Array.Empty<ushort>();
                    }
                    if (!Crc16Modbus.VerifyPrefix(recv, expectLen))
                    {
                        // 485 上 CRC 错通常是终端电阻/屏蔽地/波特率的问题。不重试:
                        // 同样的线况重试一百次也是错的,但要让人看见它错了。
                        _logger.LogWarning($"[恒温振荡培养箱] 读 0x{start:X4} 应答 CRC 校验失败,丢弃");
                        return Array.Empty<ushort>();
                    }

                    var result = new ushort[count];
                    for (int i = 0; i < count; i++)
                        result[i] = (ushort)(recv[3 + i * 2] << 8 | recv[4 + i * 2]);
                    return result;
                }
                finally { sem.Release(); }
            }, $"读 0x{start:X4}+{count}", token);
        }

        /// <summary>写单个保持寄存器(0x06)。仪器回显原帧,回显对不上算失败。</summary>
        private async Task<bool> WriteSingleAsync(ushort reg, ushort value, CancellationToken token)
        {
            byte[] cmd = BuildWriteSingleCmd(reg, value);
            return await RetryAsync(async () =>
            {
                var sem = GetInstanceLock();
                await sem.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    byte[] recv = new byte[64];
                    using var transport = await GetTransportAsync().ConfigureAwait(false);
                    int len = await transport.SendAndReceiveAsync(cmd, recv, IoTimeoutMs, token).ConfigureAwait(false);

                    if (len < 8) { _logger.LogWarning($"[恒温振荡培养箱] 写 0x{reg:X4} 应答太短:{len}"); return false; }
                    if ((recv[1] & 0x80) != 0)
                    {
                        _logger.LogWarning($"[恒温振荡培养箱] 写 0x{reg:X4}={value} 被拒,异常码 0x{recv[2]:X2}");
                        return false;
                    }
                    // 0x06 的正常应答是把请求原样回显。逐字节比对是最省事的成功判据。
                    for (int i = 0; i < 6; i++)
                        if (recv[i] != cmd[i])
                        {
                            _logger.LogWarning($"[恒温振荡培养箱] 写 0x{reg:X4} 回显不一致,第 {i} 字节 {recv[i]:X2} != {cmd[i]:X2}");
                            return false;
                        }
                    return true;
                }
                finally { sem.Release(); }
            }, $"写 0x{reg:X4}={value}", token);
        }

        /// <summary>写多个保持寄存器(0x10)。应答是 站号+0x10+起始地址+数量+CRC。</summary>
        private async Task<bool> WriteMultipleAsync(ushort start, ushort[] values, CancellationToken token)
        {
            if (values == null || values.Length == 0) return false;

            byte[] cmd = BuildWriteMultipleCmd(start, values);
            return await RetryAsync(async () =>
            {
                var sem = GetInstanceLock();
                await sem.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    byte[] recv = new byte[64];
                    using var transport = await GetTransportAsync().ConfigureAwait(false);
                    int len = await transport.SendAndReceiveAsync(cmd, recv, IoTimeoutMs, token).ConfigureAwait(false);

                    if (len < 8) { _logger.LogWarning($"[恒温振荡培养箱] 写多个 0x{start:X4} 应答太短:{len}"); return false; }
                    if ((recv[1] & 0x80) != 0)
                    {
                        _logger.LogWarning($"[恒温振荡培养箱] 写多个 0x{start:X4}+{values.Length} 被拒,异常码 0x{recv[2]:X2}");
                        return false;
                    }
                    if (recv[1] != 0x10) { _logger.LogWarning($"[恒温振荡培养箱] 功能码不对:0x{recv[1]:X2}"); return false; }

                    ushort echoStart = (ushort)(recv[2] << 8 | recv[3]);
                    ushort echoCount = (ushort)(recv[4] << 8 | recv[5]);
                    if (echoStart != start || echoCount != values.Length)
                    {
                        _logger.LogWarning(
                            $"[恒温振荡培养箱] 写多个回显不一致:仪器说写了 0x{echoStart:X4}+{echoCount}," +
                            $"我们发的是 0x{start:X4}+{values.Length}");
                        return false;
                    }
                    return true;
                }
                finally { sem.Release(); }
            }, $"写多个 0x{start:X4}+{values.Length}", token);
        }

        /// <summary>
        /// 超时重试。只重试超时 —— 协议错、CRC 错、异常码这些重试没有意义,
        /// 同样的请求还会得到同样的结果,重试只是把故障拖后三秒才暴露。
        /// </summary>
        private async Task<T> RetryAsync<T>(Func<Task<T>> action, string what, CancellationToken token)
        {
            Exception? last = null;
            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try { return await action().ConfigureAwait(false); }
                catch (TimeoutException ex)
                {
                    last = ex;
                    if (attempt < MaxRetries)
                    {
                        _logger.LogWarning($"[恒温振荡培养箱] {what} 超时(第 {attempt}/{MaxRetries} 次),{RetryDelayMs}ms 后重试");
                        await Task.Delay(RetryDelayMs, token).ConfigureAwait(false);
                    }
                }
            }
            throw new TimeoutException($"{what} 失败,{MaxRetries} 次尝试后仍超时", last);
        }

        private byte[] BuildReadCmd(ushort reg, ushort count)
        {
            byte[] f =
            {
                GetSlaveAddress(), 0x03,
                (byte)(reg >> 8), (byte)(reg & 0xFF),
                (byte)(count >> 8), (byte)(count & 0xFF),
            };
            return Crc16Modbus.AppendCrc(f, 0, f.Length);
        }

        private byte[] BuildWriteSingleCmd(ushort reg, ushort value)
        {
            byte[] f =
            {
                GetSlaveAddress(), 0x06,
                (byte)(reg >> 8), (byte)(reg & 0xFF),
                (byte)(value >> 8), (byte)(value & 0xFF),
            };
            return Crc16Modbus.AppendCrc(f, 0, f.Length);
        }

        private byte[] BuildWriteMultipleCmd(ushort start, ushort[] values)
        {
            int n = values.Length;
            byte[] f = new byte[7 + n * 2];
            f[0] = GetSlaveAddress();
            f[1] = 0x10;
            f[2] = (byte)(start >> 8);
            f[3] = (byte)(start & 0xFF);
            f[4] = (byte)(n >> 8);
            f[5] = (byte)(n & 0xFF);
            f[6] = (byte)(n * 2);
            for (int i = 0; i < n; i++)
            {
                f[7 + i * 2] = (byte)(values[i] >> 8);
                f[8 + i * 2] = (byte)(values[i] & 0xFF);
            }
            return Crc16Modbus.AppendCrc(f, 0, f.Length);
        }

        #endregion

        #region 日志包装

        private Func<DeviceParameters, Task<DeviceParameters>> AsyncWrapAction(
            string commandName, Func<DeviceParameters?, Task<DeviceParameters>> inner)
        {
            return async (DeviceParameters parameters) =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var output = await inner(parameters).ConfigureAwait(false);
                    sw.Stop();
                    _logger.LogInformation(
                        $"[恒温振荡培养箱命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex,
                        $"[恒温振荡培养箱命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        #endregion
    }

    /// <summary>Modbus CRC16 帮助类。多项式 0xA001(0x8005 反射),初值 0xFFFF。</summary>
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
            byte[] result = new byte[length + 2];
            Buffer.BlockCopy(data, offset, result, 0, length);
            result[length] = (byte)(crc & 0xFF);        // CRC 低字节在前
            result[length + 1] = (byte)(crc >> 8);
            return result;
        }

        /// <summary>
        /// 校验 buffer 前 frameLen 个字节构成的一帧(末两字节是 CRC)。
        /// 只看前 frameLen 字节,后面的残留数据不参与 —— 接收缓冲区往往比一帧长。
        /// </summary>
        public static bool VerifyPrefix(byte[] buffer, int frameLen)
        {
            if (buffer == null || frameLen < 4 || buffer.Length < frameLen) return false;
            ushort crc = Compute(buffer, 0, frameLen - 2);
            return buffer[frameLen - 2] == (byte)(crc & 0xFF)
                && buffer[frameLen - 1] == (byte)(crc >> 8);
        }
    }
}
