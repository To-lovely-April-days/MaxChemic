// ============================================================================
//  Chiller_ModbusRTU.cs
//  自研冷水机驱动 —— Modbus RTU / RS485。
//  依据《主机触模屏通讯协议2023》。
//
//  ── 通信参数(文档"1. 数据定义说明"写死) ───────────────────────────
//      9600 / N / 8 / 1,Modbus RTU + CRC16。
//      地址：大板的设备地址固定为：1。
//      当触摸屏与主板断开后再连接都需要重新传输一次所有需要的参数。
//
//  ── 寄存器全景 ────────────────────────────────────────────────────
//      0x0000~0x001E 读:系统信息(运行状态、输入状态、防冻温度、继电器、
//                          故障位、液体温度、压缩机电流、累计时间、故障明细)
//      0x0100~0x011F 读:版本信息(ASCII 表示版本号)
//      0x0200        写:用户操作命令(0x06 写单个,或 0x05 写线圈)
//      0x0400~0x0408 读/写:常规参数(设定温度、起停方式、压缩机选择、
//                            通讯地址、波特率、校验位)
//      0x0500~0x005F 读/写:厂家参数(一键启动、来电自启、报警输出、
//                            DI 选择、加载/卸载偏差、温度上下限、
//                            低温保护、超温预警/报警/回差、各类延时、
//                            电流检测与额定电流、各类常开常闭开关)
//      开机/关机有两条路:
//        (1) 0x06 写单个寄存器到 0x0200,数据值 0x0072 开机 / 0x0070 关机;
//        (2) 0x05 写线圈,地址 0x0200+相对地址,数据值 0xFF00 命令有效。
//      两条路都按文档保留,现场以固件实际支持为准。
//
//  ── 真机实测结论(2026-10-22 串口抓帧,与文档有出入,以实测为准) ─────
//      ① 0x0000 系统区一次最多干净回 16 个寄存器(0x0000~0x000F);
//         一次读 31 个时,应答前 16 个是真实数据,尾部是脏缓冲(混着上一帧
//         碎片)且 CRC 错误 —— 读 31 个会导致整条命令失败。
//      ② 0x04xx 常规参数区忽略读取数量,任何 count 都只回 1 个寄存器
//         —— 常规参数必须逐寄存器读。
//      ③ 0x0100 版本信息块、0x0500 厂家参数块在本机上不存在
//         (返回 Modbus 异常码 0x02 非法地址),驱动不再读取。
//      ④ 0x03 与 0x04 功能码对 0x0000 区都可用;系统区按 0x04 读。
//
//  ── 小数点 ────────────────────────────────────────────────────────
//      温度单位:0.1℃(寄存器值 = 实际值 ×10);电流单位:0.1A。
//      所有温度/电流进出都要 ×10 / ÷10,别只改一头。
// ============================================================================

using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;

namespace Chiller_ModbusRTU
{
    /// <summary>
    /// 寄存器地址表。全部按《主机触模屏通讯协议2023》文档。
    /// 系统信息/版本信息用 0x03 读;用户命令用 0x06 写或 0x05 写线圈;
    /// 常规参数/厂家参数用 0x03 读、0x06 写单个、0x10 写多个。
    /// </summary>
    internal static class Regs
    {
        // ============================================================
        // 2.2.3 机组信息（系统信息，基地址 0x0000，只读）
        // ============================================================
        public const ushort RunState = 0x0000; // 机组运行状态
        public const ushort InputStatus = 0x0001; // 输入状态
        public const ushort AntiFreezeTemp = 0x0002; // 防冻温度
        public const ushort RelayStatus = 0x0003; // 继电器状态
        public const ushort FaultStatus1 = 0x0004; // 故障状态位 1
        public const ushort FaultStatus2 = 0x0005; // 故障状态位 2
        public const ushort FaultStatus3 = 0x0006; // 故障状态位 3
        public const ushort LiquidTemp = 0x0007; // 液体温度
        public const ushort Comp1Current = 0x0008; // 1#压缩机电流
        public const ushort Comp2Current = 0x0009; // 2#压缩机电流
        public const ushort ChilledPumpCurrent = 0x000A; // 冷冻泵电流
        public const ushort CoolingPumpCurrent = 0x000B; // 冷却泵电流
        public const ushort Comp1RunHours = 0x000C; // 1#压缩机累计运行时间(小时)
        public const ushort Comp1RunMinutes = 0x000D; // 1#压缩机累计运行时间(分钟)
        public const ushort Comp2RunHours = 0x000E; // 2#压缩机累计运行时间(小时)
        public const ushort Comp2RunMinutes = 0x000F; // 2#压缩机累计运行时间(分钟)
        public const ushort DisplayFault1 = 0x0010; // 显示故障信息 1
        public const ushort DisplayFault2 = 0x0011; // 显示故障信息 2
        public const ushort DisplayFault3 = 0x0012; // 显示故障信息 3
        public const ushort DisplayFault4 = 0x0013; // 显示故障信息 4
        public const ushort DisplayFault5 = 0x0014; // 显示故障信息 5
        public const ushort DisplayFault6 = 0x0015; // 显示故障信息 6
        public const ushort DisplayFault7 = 0x0016; // 显示故障信息 7
        public const ushort DisplayFault8 = 0x0017; // 显示故障信息 8
        public const ushort DisplayFault9 = 0x0018; // 显示故障信息 9
        public const ushort DisplayFault10 = 0x0019; // 显示故障信息 10
        public const ushort DisplayFault11 = 0x001A; // 显示故障信息 11
        public const ushort DisplayFault12 = 0x001B; // 显示故障信息 12
        public const ushort DisplayFault13 = 0x001C; // 显示故障信息 13
        public const ushort DisplayFault14 = 0x001D; // 显示故障信息 14
        public const ushort DisplayFault15 = 0x001E; // 显示故障信息 15

        // ============================================================
        // 2.2.2 版本信息（基地址 0x0100，只读，ASCII）
        // ============================================================
        public const ushort VersionBase = 0x0100;

        // ============================================================
        // 2.2.1 用户操作命令（基地址 0x0200，写）
        // ============================================================
        public const ushort UserCmdBase = 0x0200;

        // 0x06 功能码写入时的"数据值"
        public const ushort UserCmd_PowerOn = 0x0072; // 开机 114
        public const ushort UserCmd_PowerOff = 0x0070; // 关机 112
        public const ushort UserCmd_CompressorAllow = 0x006B; // 压缩机允许 107
        public const ushort UserCmd_CompressorForbid = 0x0078; // 压缩机禁止 120
        public const ushort UserCmd_Mute = 0x0076; // 消音
        public const ushort UserCmd_Reset = 0x0075; // 复位
        public const ushort UserCmd_ClearComp1Runtime = 0x0051; // 1#压缩机累计时间清零 81
        public const ushort UserCmd_ClearComp2Runtime = 0x005A; // 2#压缩机累计时间清零 90

        // 0x05 功能码写入时的"相对地址"
        public const ushort UserCoil_PowerOn = 0x0000;
        public const ushort UserCoil_PowerOff = 0x0001;
        public const ushort UserCoil_CompressorAllow = 0x0002;
        public const ushort UserCoil_CompressorForbid = 0x0003;
        public const ushort UserCoil_Mute = 0x0004;
        public const ushort UserCoil_Reset = 0x0005;

        // ============================================================
        // 2.3 常规参数（基地址 0x0400，读/写）
        // ============================================================
        public const ushort ParamBase = 0x0400;
        public const ushort Param0000 = 0x0400; // 用户参数（文档未命名）
        public const ushort SetTemp = 0x0401; // 设定温度 0.1℃
        public const ushort Param0002 = 0x0402; // 备用
        public const ushort StartStopMode = 0x0403; // 起停方式
        public const ushort CompressorSelect = 0x0404; // 压缩机使用选择
        public const ushort Language = 0x0405; // 多语言
        public const ushort CommAddress = 0x0406; // 通讯地址
        public const ushort BaudRate = 0x0407; // 波特率
        public const ushort Parity = 0x0408; // 校验位

        // ============================================================
        // 2.4 厂家参数（基地址 0x0500，读/写）
        // ============================================================
        public const ushort FactoryBase = 0x0500;

        public const ushort F_OneKeyStart = 0x0500; // 一键启动
        public const ushort F_AutoStartOnPower = 0x0501; // 来电自动启动
        public const ushort F_AlarmOutputMode = 0x0502; // 报警输出方式
        public const ushort F_AlarmOutputType = 0x0503; // 报警输出类型
        public const ushort F_DI5Select = 0x0504; // DI5 输入选择
        public const ushort F_DI9Select = 0x0505; // DI9 输入选择
        public const ushort F_WaterLevelLowAction = 0x0506; // 水位低处理
        public const ushort F_WaterFlowLowAction = 0x0507; // 水流不足处理
        public const ushort F_ThreePhaseDetect = 0x0508; // 三相电检测
        public const ushort F_DI1Select = 0x0509; // DI1 输入选择(防冻/冷却过载)
        public const ushort F_CompressorCount = 0x050A; // 压缩机个数
        // 0x000B 备用
        // 0x000C~0x000F 备用
        public const ushort F_LoadDeviation = 0x0510; // 加载偏差 0.1
        public const ushort F_UnloadDeviation = 0x0511; // 卸载偏差 0.1
        public const ushort F_SetTempMax = 0x0512; // 设定温度最大值 0.1
        public const ushort F_SetTempMin = 0x0513; // 设定温度最小值 0.1
        public const ushort F_LiquidTempComp = 0x0514; // 液体探头温度补偿 0.1
        public const ushort F_LowTempProtect = 0x0515; // 低温保护 0.1
        public const ushort F_OverTempWarn = 0x0516; // 超温预警 0.1
        public const ushort F_OverTempAlarm = 0x0517; // 超温报警 0.1
        public const ushort F_OverTempHysteresis = 0x0518; // 超温回差 0.1
        public const ushort F_AntiFreezeTempComp = 0x0519; // 防冻探头温度补偿 0.1
        // 0x001A~0x001F 备用
        public const ushort F_ChilledStartDelay = 0x0520; // 冷冻启动延时
        public const ushort F_CoolingStartDelay = 0x0521; // 冷却启动延时
        public const ushort F_EnergyCycle = 0x0522; // 能量调节周期
        public const ushort F_CompressorProtect = 0x0523; // 压缩机保护
        public const ushort F_GeneralFaultClear = 0x0524; // 一般故障消
        public const ushort F_WaterFlowDelay = 0x0525; // 水流不足延时
        public const ushort F_LowPressureDelay = 0x0526; // 低压检测延时
        public const ushort F_LowPressureClear = 0x0527; // 低压消
        // 0x0028~0x002F 备用
        public const ushort F_CurrentDetect = 0x0530; // 电流检测
        public const ushort F_Comp1RatedCurrent = 0x0531; // 1#压缩机额定电流 0.1A
        public const ushort F_Comp2RatedCurrent = 0x0532; // 2#压缩机额定电流 0.1A
        public const ushort F_ChilledPumpRated = 0x0533; // 冷冻泵额定电流 0.1A
        public const ushort F_CoolingPumpRated = 0x0534; // 冷却泵额定电流 0.1A
        // 0x0035~0x003F 备用
        public const ushort F_ChilledOverload = 0x0540; // 冷冻过载 常开~常闭
        public const ushort F_ChilledFlowSwitch = 0x0541; // 冷冻水流开关 常开~常闭
        public const ushort F_WaterLevelSwitch = 0x0542; // 水位开关 常开~常闭
        public const ushort F_CompressorOverload = 0x0543; // 压缩机过载 常开~常闭
        public const ushort F_CompressorLowPress = 0x0544; // 压缩机低压 常开~常闭
        public const ushort F_CompressorHighPress = 0x0545; // 压缩机高压 常开~常闭
        public const ushort F_ThreePhaseFault = 0x0546; // 三相电源故障 常开~常闭
        public const ushort F_CoolingOverload = 0x0547; // 冷却过载 常开~常闭
        // 0x0048~0x004F 备用
        public const ushort F_Password = 0x055F; // 密码
        public const ushort F_ParamInit = 0x005F; // 参数初始化

        // 文档：参数初始化只初始化 0x0000~0x004F 的参数。
        // 初始化后命令要传到大板，传送数据 0X5A5A。然后重新读所有参数到屏上。
        public const ushort F_ParamInitMagic = 0x5A5A;

        // 读取批量
        public const ushort SystemBlockStart = 0x0000;
        public const ushort SystemBlockCount = 0x1F; // 0x0000~0x001E 共 31 个

        /// <summary>
        /// 实测真机对 0x0000 系统区一次最多干净回 16 个寄存器(0x0000~0x000F);
        /// 一次读 31 个时,应答前 16 个是真实数据,尾部是脏缓冲(混着上一帧碎片)且 CRC 错误。
        /// 屏幕与命令输出用到的全部系统寄存器都在前 16 个之内,按 16 读即可。
        /// </summary>
        public const ushort SystemBlockReadCount = 0x10; // 一次干净读取 16 个
    }

    /// <summary>机组运行状态(0x0000)的取值。</summary>
    internal static class RunStateText
    {
        public static string Describe(int v) => v switch
        {
            0 => "空闲",
            1 => "准备启动",
            2 => "正常运行",
            3 => "延时停机",
            4 => "故障",
            _ => $"未知({v})",
        };
    }

    /// <summary>故障状态 1(0x0004)的位定义。</summary>
    internal static class Fault1Bits
    {
        public static readonly string[] Names =
        {
            "1#压缩机高压", "1#压缩机低压", "1#压缩机过载", "1#压缩机电流过高",
            "1#压缩机电流过低", "排气温度1过高", "2#压缩机高压", "2#压缩机低压",
            "2#压缩机过载", "2#压缩机电流过高", "2#压缩机电流过低", "排气温度2过高",
            "温度过低", "超温预警", "超温停机", "防冻故障",
        };
    }

    /// <summary>故障状态 2(0x0005)的位定义。</summary>
    internal static class Fault2Bits
    {
        public static readonly string[] Names =
        {
            "温度探头断路", "温度探头短路", "冷却风机过载", "冷却泵过载",
            "冷却电流过高", "冷却电流过低", "冷却水流不足", "冷冻流量不足",
            "冷冻泵过载", "送风机过载", "冷冻电流过高", "冷冻电流过低",
            "三相电源故障", "水位故障", "机组需维护", "备用",
        };
    }

    /// <summary>显示故障信息 1~15(0x0010~0x001E)的故障码 → 文字。</summary>
    internal static class FaultCodeText
    {
        public static string Describe(int code) => code switch
        {
            0 => "无故障",
            1 => "1#压缩机高压",
            2 => "1#压缩机低压",
            3 => "1#压缩机过载",
            4 => "1#压缩机电流过高",
            5 => "1#压缩机电流过低",
            6 => "排气温度1过高",
            7 => "2#压缩机高压",
            8 => "2#压缩机低压",
            9 => "2#压缩机过载",
            10 => "2#压缩机电流过高",
            11 => "2#压缩机电流过低",
            12 => "排气温度2过高",
            13 => "温度过低",
            14 => "超温预警",
            15 => "超温停机",
            16 => "防冻故障",
            17 => "温度探头断路",
            18 => "温度探头短路",
            19 => "冷却风机过载",
            20 => "冷却泵过载",
            21 => "冷却电流过高",
            22 => "冷却电流过低",
            23 => "冷却水流不足",
            24 => "冷冻流量不足",
            25 => "冷冻泵过载",
            26 => "送风机过载",
            27 => "冷冻电流过高",
            28 => "冷冻电流过低",
            29 => "三相电源故障",
            30 => "水位故障",
            31 => "机组需维护",
            _ => $"未知故障({code})",
        };
    }

    [Export(typeof(IDevice))]
    public class Chiller_ModbusRTU : Device, IDeviceScreen
    {
        /// <summary>
        /// 源码版本戳。**改动本文件时手动更新**。
        /// 现场日志第一行会打出来 —— 改完代码日志却没变,先看这里对不对。
        /// </summary>
        public const string SourceStamp = "2026-10-22-chiller-b";

        private readonly ILogService _logger;

        private const int MaxRetries = 3;
        private const int RetryDelayMs = 100;
        private const int IoTimeoutMs = 500; // 冷水机响应慢，给 1.5s

        /// <summary>按 DeviceId 隔离的实例锁,保证同一台仪器的 IO 串行。</summary>
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _instanceLocks = new();

        public string ScreenKey => "ChillerScreen";

        public Chiller_ModbusRTU()
        {
            _logger = LogManager.GetLogger<Chiller_ModbusRTU>();

            DeviceId = "CHL0001";
            Name = "自研冷水机";
            Manufacturer = "自研冷水机 Modbus RTU / RS485";
            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/ChilLer.png";
            Category = DeviceCategories.Reactors;
            ComId = "CHL0001";

            AllowedRegions = new List<RegionType>
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            Parameters.Variables.Add(new StringParameter("DeviceId", "CHL0001", "设备实例编号")
            {
                Options = new ObservableCollection<string> { "CHL0001", "CHL0002", "CHL0003", "CHL0004" }
            });

            Parameters.Variables.Add(new StringParameter("串口号", SerialPortOptions.DefaultPort(), "RS485 转换器对应的串口")
            {
                Options = SerialPortOptions.CreateOptions()
            });

            // 文档"1. 数据定义说明"写死 9600,N,8,1。
            // 但常规参数 0x0407/0x0408 可改波特率/校验,所以做成可配。
            Parameters.Variables.Add(new StringParameter("波特率", "9600", "文档规定 9600")
            {
                Options = new ObservableCollection<string> { "4800", "9600", "19200", "38400" }
            });

            Parameters.Variables.Add(new StringParameter("校验位", "None", "文档规定无校验")
            {
                Options = new ObservableCollection<string> { "None", "Odd", "Even" }
            });

            Parameters.Variables.Add(new StringParameter("数据位", "8")
            {
                Options = new ObservableCollection<string> { "7", "8" }
            });

            Parameters.Variables.Add(new StringParameter("停止位", "One")
            {
                Options = new ObservableCollection<string> { "One", "Two" }
            });

            Parameters.Variables.Add(new StringParameter("通信方式", "Direct", "通信方式")
            {
                Options = new ObservableCollection<string> { "Direct", "ModbusTcp", "ZLanGateway", "RemoteServer" }
            });

            Parameters.Variables.Add(new StringParameter("DTU序列号", "HTVA8YXK7XKN",
                "RemoteServer 模式必填：DTU 登录包中的设备序列号"));

            // 文档:"大板的设备地址固定为：1"。做成可配是为了现场排查。
            Parameters.Variables.Add(new NumberParameter("Modbus站号", 1, 254, 1,
                "文档规定大板地址固定为 1，现场按仪器设置填"));

            InitializeCommands();
        }

        public override bool SupportsZLanGateway => true;

        #region 可监控参数


        public override IReadOnlyList<CollectableParameter> CollectableParameters => new[]
        {
            new CollectableParameter("LIQUID_TEMP",  "液体温度",  "℃", "获取所有数据", "LIQUID_TEMP", isKey: true),
            new CollectableParameter("SET_TEMP",     "设定温度",  "℃", "获取所有数据", "SET_TEMP"),
            new CollectableParameter("COMP1_CURRENT","1#压缩机电流", "A", "获取所有数据", "COMP1_CURRENT"),
            new CollectableParameter("COMP2_CURRENT","2#压缩机电流", "A", "获取所有数据", "COMP2_CURRENT"),
            new CollectableParameter("RUN_STATE",    "运行状态",  "",   "获取所有数据", "RUN_STATE_TEXT"),
        };

        public override int AutoCollectIntervalMs => 1000;

        #endregion

        #region 连接

        /// <summary>
        /// 直连模式的连接探测:读 0x0000 机组运行状态,再读 0x0100 版本信息。
        ///
        /// 文档没有给出"产品类型编号"这种身份寄存器,只能靠读系统信息是否成功
        /// 判断"对面是不是这台设备"。
        /// </summary>
        protected override async Task<bool> OnConnectAsync() => await ProbeAsync("直连");

        protected override Task<bool> OnConnectViaGatewayAsync() => ProbeAsync("ZLAN 网关");

        private async Task<bool> ProbeAsync(string via)
        {
            try
            {
                _logger.LogInformation(
                    $"[冷水机 {SourceStamp}] 经{via}连接,站号 {GetSlaveAddress()}," +
                    $"串口 {Parameters.GetValue<string>("串口号")} @ {Parameters.GetValue<string>("波特率")}/8/N/1");

                ushort[] regs = await ReadRegistersAsync(Regs.RunState, 1, CancellationToken.None);
                if (regs.Length < 1) { ErrorLog("连接失败:读运行状态没有得到有效应答"); return false; }

                // 真机没有版本信息块(0x0100 返回 0x02 非法地址),探测只读运行状态
                InfoLog($"连接成功:运行状态 {RunStateText.Describe(regs[0])}");
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
                HelpText = "一次读回系统信息 0x0000~0x000F 与常规参数 0x0400~0x0408。" +
                           "真机实测:0x0000 区单次最多干净回 16 个寄存器;0x04xx 区每次只回 1 个(逐寄存器读);" +
                           "0x0100 版本块与 0x0500 厂家块在本机上不存在(0x02 非法地址),不再读取。屏幕轮询用的就是它。",
                OutputParameters = new DeviceParameters { Variables = BuildAllOutputs() },
                AsyncAction = AsyncWrapAction("获取所有数据", _ => ReadAllAsync())
            });

            Commands.Add(new DeviceCommand
            {
                Name = "获取报警明细",
                HelpText = "把故障状态位 1(0x0004)/2(0x0005)/3(0x0006)拆成中文明细",
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("HasAlarm", false, "是否有报警", true),
                        new NumberParameter("Count", 0, 64, 0, "报警条数", true),
                        new StringParameter("Alarms", "", "报警明细,逗号分隔"),
                        new BooleanParameter("Success", false, "是否读到"),
                    }
                },
                AsyncAction = AsyncWrapAction("获取报警明细", _ => ReadAlarmsAsync())
            });

            // ── 用户操作命令:0x06 写单个寄存器 ──────────────────
            AddUserCmdCommand("开机", "0x0072 开机", Regs.UserCmd_PowerOn);
            AddUserCmdCommand("关机", "0x0070 关机", Regs.UserCmd_PowerOff);
            AddUserCmdCommand("压缩机允许", "0x006B 压缩机允许", Regs.UserCmd_CompressorAllow);
            AddUserCmdCommand("压缩机禁止", "0x0078 压缩机禁止", Regs.UserCmd_CompressorForbid);
            AddUserCmdCommand("消音", "0x0076 消音", Regs.UserCmd_Mute);
            AddUserCmdCommand("复位", "0x0075 复位", Regs.UserCmd_Reset);
            AddUserCmdCommand("1#压缩机累计时间清零", "0x0051 1#压缩机累计时间清零", Regs.UserCmd_ClearComp1Runtime);
            AddUserCmdCommand("2#压缩机累计时间清零", "0x005A 2#压缩机累计时间清零", Regs.UserCmd_ClearComp2Runtime);

            // ── 用户操作命令:0x05 写线圈 ─────────────────────────
            AddUserCoilCommand("开机(线圈)", "0x05 写线圈:相对地址 0x0000 开机", Regs.UserCoil_PowerOn);
            AddUserCoilCommand("关机(线圈)", "0x05 写线圈:相对地址 0x0001 关机", Regs.UserCoil_PowerOff);
            AddUserCoilCommand("压缩机允许(线圈)", "0x05 写线圈:相对地址 0x0002 压缩机允许", Regs.UserCoil_CompressorAllow);
            AddUserCoilCommand("压缩机禁止(线圈)", "0x05 写线圈:相对地址 0x0003 压缩机禁止", Regs.UserCoil_CompressorForbid);
            AddUserCoilCommand("消音(线圈)", "0x05 写线圈:相对地址 0x0004 消音", Regs.UserCoil_Mute);
            AddUserCoilCommand("复位(线圈)", "0x05 写线圈:相对地址 0x0005 复位", Regs.UserCoil_Reset);

            // ── 清故障 ─────────────────────────────────────────
            Commands.Add(new DeviceCommand
            {
                Name = "清除故障",
                HelpText = "文档说明:选中故障信息 1,按清除时将 1 写入 0x0003;" +
                           "选中故障信息 8,按清除时将 8 写入 0x0003;无故障写 0。",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("FaultIndex", 0, 15, 0, "故障信息序号 1~15;0 表示清除全部")
                    }
                },
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("清除故障", async p =>
                {
                    if (IsSimulationMode) return Success(true);
                    ushort idx = RequiredInteger(p, "FaultIndex", 0, 15);
                    return Success(await WriteSingleAsync(Regs.RelayStatus, idx, CancellationToken.None));
                })
            });

            // ── 常规参数:设定温度 ──────────────────────────────
            Commands.Add(new DeviceCommand
            {
                Name = "设置设定温度",
                HelpText = "写常规参数 0x0001 设定温度,单位 0.1℃,文档范围 0.0~350.0℃。",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("Temp", 0, 350, 80, "设定温度", false, "℃") { DecimalPlaces = 1 }
                    }
                },
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("设置设定温度", async p =>
                {
                    if (IsSimulationMode) return Success(true);
                    double t = RequiredNumber(p, "Temp", 0, 350);
                    short raw = checked((short)Math.Round(t * 10.0, MidpointRounding.AwayFromZero));
                    return Success(await WriteSingleAsync(Regs.SetTemp, unchecked((ushort)raw), CancellationToken.None));
                })
            });

            // ── 常规参数:起停方式 ──────────────────────────────
            Commands.Add(new DeviceCommand
            {
                Name = "设置起停方式",
                HelpText = "0=本地 / 1=本地+远程 / 2=远程",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TargetValue", 0, 2, 0, "起停方式", false)
                    }
                },
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("设置起停方式", async p =>
                {
                    if (IsSimulationMode) return Success(true);
                    ushort v = RequiredInteger(p, "TargetValue", 0, 2);
                    return Success(await WriteSingleAsync(Regs.StartStopMode, v, CancellationToken.None));
                })
            });

            // ── 常规参数:压缩机使用选择 ────────────────────────
            Commands.Add(new DeviceCommand
            {
                Name = "设置压缩机使用选择",
                HelpText = "1=1#压缩机,2=2#压缩机(仅在压缩机个数=2 时可选)",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TargetValue", 1, 2, 1, "压缩机选择", false)
                    }
                },
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("设置压缩机使用选择", async p =>
                {
                    if (IsSimulationMode) return Success(true);
                    ushort v = RequiredInteger(p, "TargetValue", 1, 2);
                    return Success(await WriteSingleAsync(Regs.CompressorSelect, v, CancellationToken.None));
                })
            });

            // ── 常规参数:通讯地址 ──────────────────────────────
            Commands.Add(new DeviceCommand
            {
                Name = "设置通讯地址",
                HelpText = "通讯地址 0~99。文档备注:这个地址是指和上位机或者外部设备而言。",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TargetValue", 0, 99, 0, "通讯地址", false)
                    }
                },
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("设置通讯地址", async p =>
                {
                    if (IsSimulationMode) return Success(true);
                    ushort v = RequiredInteger(p, "TargetValue", 0, 99);
                    return Success(await WriteSingleAsync(Regs.CommAddress, v, CancellationToken.None));
                })
            });

            // ── 常规参数:波特率 ────────────────────────────────
            Commands.Add(new DeviceCommand
            {
                Name = "设置波特率",
                HelpText = "19200(编码 0) / 9600(编码 1) / 4800(编码 2)",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TargetValue", 0, 2, 1, "波特率编码", false)
                    }
                },
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("设置波特率", async p =>
                {
                    if (IsSimulationMode) return Success(true);
                    ushort v = RequiredInteger(p, "TargetValue", 0, 2);
                    return Success(await WriteSingleAsync(Regs.BaudRate, v, CancellationToken.None));
                })
            });

            // ── 常规参数:校验位 ────────────────────────────────
            Commands.Add(new DeviceCommand
            {
                Name = "设置校验位",
                HelpText = "无校验(编码 0) / 奇校验(编码 1) / 偶校验(编码 2)",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TargetValue", 0, 2, 0, "校验位编码", false)
                    }
                },
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("设置校验位", async p =>
                {
                    if (IsSimulationMode) return Success(true);
                    ushort v = RequiredInteger(p, "TargetValue", 0, 2);
                    return Success(await WriteSingleAsync(Regs.Parity, v, CancellationToken.None));
                })
            });

            // ── 厂家参数:一键启动 ──────────────────────────────
            AddFactoryOnOffCommand("设置一键启动", "0x0000:0=禁用 / 1=使用", Regs.F_OneKeyStart);
            // ── 厂家参数:来电自动启动 ──────────────────────────
            AddFactoryOnOffCommand("设置来电自动启动", "0x0001:0=禁用 / 1=使用", Regs.F_AutoStartOnPower);
            // ── 厂家参数:报警输出方式 ──────────────────────────
            AddFactoryRangeCommand("设置报警输出方式", "0x0002:0=消音保持输出 / 1=消音停止输出", Regs.F_AlarmOutputMode, 0, 1);
            // ── 厂家参数:报警输出类型 ──────────────────────────
            AddFactoryRangeCommand("设置报警输出类型", "0x0003:0=常开 / 1=常闭", Regs.F_AlarmOutputType, 0, 1);
            // ── 厂家参数:DI5 输入选择 ──────────────────────────
            AddFactoryRangeCommand("设置 DI5 输入选择", "0x0004:0=水位检测 / 1=1#排气温度", Regs.F_DI5Select, 0, 1);
            // ── 厂家参数:DI9 输入选择 ──────────────────────────
            AddFactoryRangeCommand("设置 DI9 输入选择", "0x0005:0=相序检测 / 1=2#排气温度 / 2=冷却水流检测", Regs.F_DI9Select, 0, 2);
            // ── 厂家参数:水位低处理 ────────────────────────────
            AddFactoryRangeCommand("设置水位低处理", "0x0006:0=停水泵 / 1=不停水泵", Regs.F_WaterLevelLowAction, 0, 1);
            // ── 厂家参数:水流不足处理 ──────────────────────────
            AddFactoryRangeCommand("设置水流不足处理", "0x0007:0=停水泵 / 1=不停水泵", Regs.F_WaterFlowLowAction, 0, 1);
            // ── 厂家参数:三相电检测 ────────────────────────────
            AddFactoryRangeCommand("设置三相电检测", "0x0008:0=板载 / 1=开关量输入 / 2=禁用", Regs.F_ThreePhaseDetect, 0, 2);
            // ── 厂家参数:DI1 输入选择 ──────────────────────────
            AddFactoryRangeCommand("设置 DI1 输入选择", "0x0009:0=防冻 / 1=冷却过载", Regs.F_DI1Select, 0, 1);
            // ── 厂家参数:压缩机个数 ────────────────────────────
            AddFactoryRangeCommand("设置压缩机个数", "0x000A:1 或 2", Regs.F_CompressorCount, 1, 2);

            // ── 厂家参数:各类偏差 / 上下限 / 保护值(0.1 单位) ──
            AddFactoryTenthsCommand("设置加载偏差", "0x0010:0.0~10.0", Regs.F_LoadDeviation, 0.0, 10.0);
            AddFactoryTenthsCommand("设置卸载偏差", "0x0011:0.0~10.0", Regs.F_UnloadDeviation, 0.0, 10.0);
            AddFactoryTenthsCommand("设置设定温度最大值", "0x0012:-50.0~99.9", Regs.F_SetTempMax, -50.0, 99.9);
            AddFactoryTenthsCommand("设置设定温度最小值", "0x0013:-50.0~99.9", Regs.F_SetTempMin, -50.0, 99.9);
            AddFactoryTenthsCommand("设置液体探头温度补偿", "0x0014:-9.9~9.9", Regs.F_LiquidTempComp, -9.9, 9.9);
            AddFactoryTenthsCommand("设置低温保护", "0x0015:-55.0~99.9", Regs.F_LowTempProtect, -55.0, 99.9);
            AddFactoryTenthsCommand("设置超温预警", "0x0016:0.0~99.9", Regs.F_OverTempWarn, 0.0, 99.9);
            AddFactoryTenthsCommand("设置超温报警", "0x0017:0.0~99.9", Regs.F_OverTempAlarm, 0.0, 99.9);
            AddFactoryTenthsCommand("设置超温回差", "0x0018:0.0~99.9", Regs.F_OverTempHysteresis, 0.0, 99.9);
            AddFactoryTenthsCommand("设置防冻探头温度补偿", "0x0019:-9.9~9.9", Regs.F_AntiFreezeTempComp, -9.9, 9.9);

            // ── 厂家参数:各类延时 / 周期(整数) ─────────────────
            AddFactoryRangeCommand("设置冷冻启动延时", "0x0020:1~255", Regs.F_ChilledStartDelay, 1, 255);
            AddFactoryRangeCommand("设置冷却启动延时", "0x0021:1~255", Regs.F_CoolingStartDelay, 1, 255);
            AddFactoryRangeCommand("设置能量调节周期", "0x0022:0~255", Regs.F_EnergyCycle, 0, 255);
            AddFactoryRangeCommand("设置压缩机保护", "0x0023:0~255", Regs.F_CompressorProtect, 0, 255);
            AddFactoryRangeCommand("设置一般故障消", "0x0024:0~255", Regs.F_GeneralFaultClear, 0, 255);
            AddFactoryRangeCommand("设置水流不足延时", "0x0025:0~255", Regs.F_WaterFlowDelay, 0, 255);
            AddFactoryRangeCommand("设置低压检测延时", "0x0026:0~255", Regs.F_LowPressureDelay, 0, 255);
            AddFactoryRangeCommand("设置低压消", "0x0027:0~255", Regs.F_LowPressureClear, 0, 255);

            // ── 厂家参数:电流检测 / 额定电流 ────────────────────
            AddFactoryRangeCommand("设置电流检测", "0x0030:0=禁用 / 1=使用", Regs.F_CurrentDetect, 0, 1);
            AddFactoryTenthsCommand("设置 1#压缩机额定电流", "0x0031:0.0~35.0A", Regs.F_Comp1RatedCurrent, 0.0, 35.0);
            AddFactoryTenthsCommand("设置 2#压缩机额定电流", "0x0032:0.0~35.0A", Regs.F_Comp2RatedCurrent, 0.0, 35.0);
            AddFactoryTenthsCommand("设置冷冻泵额定电流", "0x0033:0.0~35.0A", Regs.F_ChilledPumpRated, 0.0, 35.0);
            AddFactoryTenthsCommand("设置冷却泵额定电流", "0x0034:0.0~35.0A", Regs.F_CoolingPumpRated, 0.0, 35.0);

            // ── 厂家参数:各类常开常闭开关 ──────────────────────
            AddFactoryRangeCommand("设置冷冻过载", "0x0040:0=常开 / 1=常闭", Regs.F_ChilledOverload, 0, 1);
            AddFactoryRangeCommand("设置冷冻水流开关", "0x0041:0=常开 / 1=常闭", Regs.F_ChilledFlowSwitch, 0, 1);
            AddFactoryRangeCommand("设置水位开关", "0x0042:0=常开 / 1=常闭", Regs.F_WaterLevelSwitch, 0, 1);
            AddFactoryRangeCommand("设置压缩机过载", "0x0043:0=常开 / 1=常闭", Regs.F_CompressorOverload, 0, 1);
            AddFactoryRangeCommand("设置压缩机低压", "0x0044:0=常开 / 1=常闭", Regs.F_CompressorLowPress, 0, 1);
            AddFactoryRangeCommand("设置压缩机高压", "0x0045:0=常开 / 1=常闭", Regs.F_CompressorHighPress, 0, 1);
            AddFactoryRangeCommand("设置三相电源故障", "0x0046:0=常开 / 1=常闭", Regs.F_ThreePhaseFault, 0, 1);
            AddFactoryRangeCommand("设置冷却过载", "0x0047:0=常开 / 1=常闭", Regs.F_CoolingOverload, 0, 1);

            // ── 厂家参数:密码 / 参数初始化 ──────────────────────
            AddFactoryRangeCommand("设置密码", "0x005F", Regs.F_Password, 0, 65535);

            Commands.Add(new DeviceCommand
            {
                Name = "参数初始化",
                HelpText = "文档说明:参数初始化,只初始化 0x0000~0x004F 的参数。" +
                           "初始化后命令要传到大板,传送数据 0X5A5A。然后重新读所有参数到屏上。",
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("参数初始化", async _ =>
                {
                    if (IsSimulationMode) return Success(true);
                    return Success(await WriteSingleAsync(Regs.F_ParamInit,
                        Regs.F_ParamInitMagic, CancellationToken.None));
                })
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

        /// <summary>0x06 写单个寄存器到 0x0200 的用户命令。</summary>
        private void AddUserCmdCommand(string name, string help, ushort dataValue)
        {
            Commands.Add(new DeviceCommand
            {
                Name = name,
                HelpText = help,
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction(name, async _ =>
                {
                    if (IsSimulationMode) return Success(true);
                    return Success(await WriteSingleAsync(Regs.UserCmdBase, dataValue, CancellationToken.None));
                })
            });
        }

        /// <summary>0x05 写线圈到 0x0200+相对地址的用户命令。</summary>
        private void AddUserCoilCommand(string name, string help, ushort relativeAddr)
        {
            Commands.Add(new DeviceCommand
            {
                Name = name,
                HelpText = help,
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction(name, async _ =>
                {
                    if (IsSimulationMode) return Success(true);
                    return Success(await WriteSingleCoilAsync(
                        (ushort)(Regs.UserCmdBase + relativeAddr), true, CancellationToken.None));
                })
            });
        }

        /// <summary>厂家参数 0/1 开关。</summary>
        private void AddFactoryOnOffCommand(string name, string help, ushort reg)
            => AddFactoryRangeCommand(name, help, reg, 0, 1);

        /// <summary>厂家参数整数范围。</summary>
        private void AddFactoryRangeCommand(string name, string help, ushort reg, int min, int max)
        {
            Commands.Add(new DeviceCommand
            {
                Name = name,
                HelpText = help,
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TargetValue", min, max, min, help, false)
                    }
                },
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction(name, async p =>
                {
                    if (IsSimulationMode) return Success(true);
                    ushort v = RequiredInteger(p, "TargetValue", min, max);
                    return Success(await WriteSingleAsync(reg, v, CancellationToken.None));
                })
            });
        }

        /// <summary>厂家参数 0.1 单位(温度/电流)。</summary>
        private void AddFactoryTenthsCommand(string name, string help, ushort reg, double min, double max)
        {
            Commands.Add(new DeviceCommand
            {
                Name = name,
                HelpText = help + "(单位 0.1)",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TargetValue", min, max, 0, help, false, "") { DecimalPlaces = 1 }
                    }
                },
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction(name, async p =>
                {
                    if (IsSimulationMode) return Success(true);
                    double v = RequiredNumber(p, "TargetValue", min, max);
                    short raw = checked((short)Math.Round(v * 10.0, MidpointRounding.AwayFromZero));
                    return Success(await WriteSingleAsync(reg, unchecked((ushort)raw), CancellationToken.None));
                })
            });
        }

        #endregion

        #region 读

        private static ObservableCollection<ParameterBase> BuildAllOutputs() => new()
        {
            // 系统信息 0x0000~0x001E
            new NumberParameter("RUN_STATE", 0, 4, 0, "机组运行状态 0空闲1准备启动2正常运行3延时停机4故障", true),
            new StringParameter("RUN_STATE_TEXT", "", "运行状态文字"),
            new NumberParameter("INPUT_STATUS", 0, 65535, 0, "输入状态位域", true),
            new NumberParameter("ANTI_FREEZE_TEMP", -55.0, 99.9, 0, "防冻温度", true, "℃") { DecimalPlaces = 1 },
            new NumberParameter("RELAY_STATUS", 0, 65535, 0, "继电器状态位域", true),
            //new NumberParameter("FAULT_STATUS1", 0, 65535, 0, "故障状态位 1", true),
            //new NumberParameter("FAULT_STATUS2", 0, 65535, 0, "故障状态位 2", true),
            //new NumberParameter("FAULT_STATUS3", 0, 65535, 0, "故障状态位 3", true),
            new NumberParameter("LIQUID_TEMP", -100, 200, 0, "液体温度", true, "℃") { DecimalPlaces = 1 },
            //new NumberParameter("COMP1_CURRENT", 0, 100, 0, "1#压缩机电流", true, "A") { DecimalPlaces = 1 },
            //new NumberParameter("COMP2_CURRENT", 0, 100, 0, "2#压缩机电流", true, "A") { DecimalPlaces = 1 },
            //new NumberParameter("CHILLED_PUMP_CURRENT", 0, 100, 0, "冷冻泵电流", true, "A") { DecimalPlaces = 1 },
            //new NumberParameter("COOLING_PUMP_CURRENT", 0, 100, 0, "冷却泵电流", true, "A") { DecimalPlaces = 1 },
            //new NumberParameter("COMP1_RUN_HOURS", 0, 65535, 0, "1#压缩机累计运行时间-小时", true, "h"),
            //new NumberParameter("COMP1_RUN_MINUTES", 0, 59, 0, "1#压缩机累计运行时间-分钟", true, "min"),
            //new NumberParameter("COMP2_RUN_HOURS", 0, 65535, 0, "2#压缩机累计运行时间-小时", true, "h"),
            //new NumberParameter("COMP2_RUN_MINUTES", 0, 59, 0, "2#压缩机累计运行时间-分钟", true, "min"),
            //new StringParameter("FAULT_TEXT", "", "故障明细(位域翻译)"),
            //new NumberParameter("DISPLAY_FAULT1", 0, 65535, 0, "显示故障信息1", true),
            //new NumberParameter("DISPLAY_FAULT2", 0, 65535, 0, "显示故障信息2", true),
            //new NumberParameter("DISPLAY_FAULT3", 0, 65535, 0, "显示故障信息3", true),
            //new NumberParameter("DISPLAY_FAULT4", 0, 65535, 0, "显示故障信息4", true),
            //new NumberParameter("DISPLAY_FAULT5", 0, 65535, 0, "显示故障信息5", true),
            //new NumberParameter("DISPLAY_FAULT6", 0, 65535, 0, "显示故障信息6", true),
            //new NumberParameter("DISPLAY_FAULT7", 0, 65535, 0, "显示故障信息7", true),
            //new NumberParameter("DISPLAY_FAULT8", 0, 65535, 0, "显示故障信息8", true),
            //new NumberParameter("DISPLAY_FAULT9", 0, 65535, 0, "显示故障信息9", true),
            //new NumberParameter("DISPLAY_FAULT10", 0, 65535, 0, "显示故障信息10", true),
            //new NumberParameter("DISPLAY_FAULT11", 0, 65535, 0, "显示故障信息11", true),
            //new NumberParameter("DISPLAY_FAULT12", 0, 65535, 0, "显示故障信息12", true),
            //new NumberParameter("DISPLAY_FAULT13", 0, 65535, 0, "显示故障信息13", true),
            //new NumberParameter("DISPLAY_FAULT14", 0, 65535, 0, "显示故障信息14", true),
            //new NumberParameter("DISPLAY_FAULT15", 0, 65535, 0, "显示故障信息15", true),

            // 版本信息 0x0100
            new StringParameter("VERSION", "", "软件版本信息(ASCII)"),

            // 常规参数 0x0400~0x0408
            new NumberParameter("PARAM_0000", 0, 65535, 0, "常规参数 0x0000", true),
            new NumberParameter("SET_TEMP", 0, 350, 0, "设定温度", true, "℃") { DecimalPlaces = 1 },
            new NumberParameter("PARAM_0002", 0, 65535, 0, "常规参数 0x0002(备用)", true),
            new NumberParameter("START_STOP_MODE", 0, 2, 0, "起停方式 0本地1本地+远程2远程", true),
            new NumberParameter("COMPRESSOR_SELECT", 1, 2, 1, "压缩机使用选择 1=1# / 2=2#", true),
            new NumberParameter("LANGUAGE", 0, 1, 0, "多语言 0中文1英文", true),
            new NumberParameter("COMM_ADDRESS", 0, 99, 0, "通讯地址", true),
            new NumberParameter("BAUD_RATE", 0, 2, 0, "波特率编码 0=19200 / 1=9600 / 2=4800", true),
            new NumberParameter("PARITY", 0, 2, 0, "校验位编码 0无1奇2偶", true),

            // 厂家参数（按文档表 2-4 全部列出）
            // 真机没有厂家参数块(0x0500 返回 0x02 非法地址),以下输出全部不可读,仅保留清单备查
            //new NumberParameter("F_ONE_KEY_START", 0, 1, 0, "一键启动", true),
            //new NumberParameter("F_AUTO_START_ON_POWER", 0, 1, 0, "来电自动启动", true),
            //new NumberParameter("F_ALARM_OUTPUT_MODE", 0, 1, 0, "报警输出方式", true),
            //new NumberParameter("F_ALARM_OUTPUT_TYPE", 0, 1, 0, "报警输出类型", true),
            //new NumberParameter("F_DI5_SELECT", 0, 1, 0, "DI5 输入选择", true),
            //new NumberParameter("F_DI9_SELECT", 0, 2, 0, "DI9 输入选择", true),
            //new NumberParameter("F_WATER_LEVEL_LOW_ACTION", 0, 1, 0, "水位低处理", true),
            //new NumberParameter("F_WATER_FLOW_LOW_ACTION", 0, 1, 0, "水流不足处理", true),
            //new NumberParameter("F_THREE_PHASE_DETECT", 0, 2, 0, "三相电检测", true),
            //new NumberParameter("F_DI1_SELECT", 0, 1, 0, "DI1 输入选择", true),
            //new NumberParameter("F_COMPRESSOR_COUNT", 1, 2, 2, "压缩机个数", true),
            //new NumberParameter("F_LOAD_DEVIATION", 0, 10, 1, "加载偏差", true, "") { DecimalPlaces = 1 },
            //new NumberParameter("F_UNLOAD_DEVIATION", 0, 10, 1, "卸载偏差", true, "") { DecimalPlaces = 1 },
            //new NumberParameter("F_SET_TEMP_MAX", -50, 99.9, 30, "设定温度最大值", true, "℃") { DecimalPlaces = 1 },
            //new NumberParameter("F_SET_TEMP_MIN", -50, 99.9, 5, "设定温度最小值", true, "℃") { DecimalPlaces = 1 },
            //new NumberParameter("F_LIQUID_TEMP_COMP", -9.9, 9.9, 0, "液体探头温度补偿", true, "℃") { DecimalPlaces = 1 },
            //new NumberParameter("F_LOW_TEMP_PROTECT", -55, 99.9, 4, "低温保护", true, "℃") { DecimalPlaces = 1 },
            //new NumberParameter("F_OVER_TEMP_WARN", 0, 99.9, 50, "超温预警", true, "℃") { DecimalPlaces = 1 },
            //new NumberParameter("F_OVER_TEMP_ALARM", 0, 99.9, 60, "超温报警", true, "℃") { DecimalPlaces = 1 },
            //new NumberParameter("F_OVER_TEMP_HYSTERESIS", 0, 99.9, 5, "超温回差", true, "℃") { DecimalPlaces = 1 },
            //new NumberParameter("F_ANTI_FREEZE_TEMP_COMP", -9.9, 9.9, 0, "防冻探头温度补偿", true, "℃") { DecimalPlaces = 1 },
            //new NumberParameter("F_CHILLED_START_DELAY", 1, 255, 10, "冷冻启动延时", true),
            //new NumberParameter("F_COOLING_START_DELAY", 1, 255, 10, "冷却启动延时", true),
            //new NumberParameter("F_ENERGY_CYCLE", 0, 255, 5, "能量调节周期", true),
            //new NumberParameter("F_COMPRESSOR_PROTECT", 0, 255, 60, "压缩机保护", true),
            //new NumberParameter("F_GENERAL_FAULT_CLEAR", 0, 255, 2, "一般故障消", true),
            //new NumberParameter("F_WATER_FLOW_DELAY", 0, 255, 10, "水流不足延时", true),
            //new NumberParameter("F_LOW_PRESSURE_DELAY", 0, 255, 60, "低压检测延时", true),
            //new NumberParameter("F_LOW_PRESSURE_CLEAR", 0, 255, 5, "低压消", true),
            //new NumberParameter("F_CURRENT_DETECT", 0, 1, 0, "电流检测", true),
            //new NumberParameter("F_COMP1_RATED_CURRENT", 0, 35, 0.3, "1#压缩机额定电流", true, "A") { DecimalPlaces = 1 },
            //new NumberParameter("F_COMP2_RATED_CURRENT", 0, 35, 0.3, "2#压缩机额定电流", true, "A") { DecimalPlaces = 1 },
            //new NumberParameter("F_CHILLED_PUMP_RATED", 0, 35, 0.3, "冷冻泵额定电流", true, "A") { DecimalPlaces = 1 },
            //new NumberParameter("F_COOLING_PUMP_RATED", 0, 35, 0.3, "冷却泵额定电流", true, "A") { DecimalPlaces = 1 },
            //new NumberParameter("F_CHILLED_OVERLOAD", 0, 1, 0, "冷冻过载 常开~常闭", true),
            //new NumberParameter("F_CHILLED_FLOW_SWITCH", 0, 1, 0, "冷冻水流开关 常开~常闭", true),
            //new NumberParameter("F_WATER_LEVEL_SWITCH", 0, 1, 0, "水位开关 常开~常闭", true),
            //new NumberParameter("F_COMPRESSOR_OVERLOAD", 0, 1, 0, "压缩机过载 常开~常闭", true),
            //new NumberParameter("F_COMPRESSOR_LOW_PRESS", 0, 1, 0, "压缩机低压 常开~常闭", true),
            //new NumberParameter("F_COMPRESSOR_HIGH_PRESS", 0, 1, 0, "压缩机高压 常开~常闭", true),
            //new NumberParameter("F_THREE_PHASE_FAULT", 0, 1, 0, "三相电源故障 常开~常闭", true),
            //new NumberParameter("F_COOLING_OVERLOAD", 0, 1, 0, "冷却过载 常开~常闭", true),

            new BooleanParameter("Success", false, "是否取到有效数据", true),
        };

        private async Task<DeviceParameters> ReadAllAsync()
        {
            var o = new DeviceParameters { Variables = BuildAllOutputs() };
            if (IsSimulationMode) { FillSimulated(o); return o; }

            // 系统信息:真机实测一次最多干净回 16 个寄存器(0x0000~0x000F)。
            // 一次读 31 个的应答前 16 个是真实数据、尾部是脏缓冲(混着上一帧碎片)且 CRC 错误,
            // 会导致整条命令失败。屏幕与命令输出用到的寄存器全部在前 16 个之内。
            ushort[] sys = await ReadRegistersAsync(Regs.SystemBlockStart, Regs.SystemBlockReadCount, CancellationToken.None);
            if (sys.Length < Regs.SystemBlockReadCount) { o.SetValue("Success", false); return o; }
            ushort S(ushort addr) => sys[addr - Regs.SystemBlockStart];

            o.SetValue("RUN_STATE", (double)S(Regs.RunState));
            o.SetValue("RUN_STATE_TEXT", RunStateText.Describe(S(Regs.RunState)));
            o.SetValue("INPUT_STATUS", (double)S(Regs.InputStatus));
            o.SetValue("ANTI_FREEZE_TEMP", Tenths(S(Regs.AntiFreezeTemp)));
            o.SetValue("RELAY_STATUS", (double)S(Regs.RelayStatus));
            o.SetValue("FAULT_STATUS1", (double)S(Regs.FaultStatus1));
            o.SetValue("FAULT_STATUS2", (double)S(Regs.FaultStatus2));
            o.SetValue("FAULT_STATUS3", (double)S(Regs.FaultStatus3));
            o.SetValue("LIQUID_TEMP", Tenths(S(Regs.LiquidTemp)));
            o.SetValue("COMP1_CURRENT", Tenths(S(Regs.Comp1Current)));
            o.SetValue("COMP2_CURRENT", Tenths(S(Regs.Comp2Current)));
            o.SetValue("CHILLED_PUMP_CURRENT", Tenths(S(Regs.ChilledPumpCurrent)));
            o.SetValue("COOLING_PUMP_CURRENT", Tenths(S(Regs.CoolingPumpCurrent)));
            o.SetValue("COMP1_RUN_HOURS", (double)S(Regs.Comp1RunHours));
            o.SetValue("COMP1_RUN_MINUTES", (double)S(Regs.Comp1RunMinutes));
            o.SetValue("COMP2_RUN_HOURS", (double)S(Regs.Comp2RunHours));
            o.SetValue("COMP2_RUN_MINUTES", (double)S(Regs.Comp2RunMinutes));
            o.SetValue("FAULT_TEXT", DescribeAlarms(S(Regs.FaultStatus1), S(Regs.FaultStatus2)));
            //o.SetValue("DISPLAY_FAULT1", (double)S(Regs.DisplayFault1));
            //o.SetValue("DISPLAY_FAULT2", (double)S(Regs.DisplayFault2));
            //o.SetValue("DISPLAY_FAULT3", (double)S(Regs.DisplayFault3));
            //o.SetValue("DISPLAY_FAULT4", (double)S(Regs.DisplayFault4));
            //o.SetValue("DISPLAY_FAULT5", (double)S(Regs.DisplayFault5));
            //o.SetValue("DISPLAY_FAULT6", (double)S(Regs.DisplayFault6));
            //o.SetValue("DISPLAY_FAULT7", (double)S(Regs.DisplayFault7));
            //o.SetValue("DISPLAY_FAULT8", (double)S(Regs.DisplayFault8));
            //o.SetValue("DISPLAY_FAULT9", (double)S(Regs.DisplayFault9));
            //o.SetValue("DISPLAY_FAULT10", (double)S(Regs.DisplayFault10));
            //o.SetValue("DISPLAY_FAULT11", (double)S(Regs.DisplayFault11));
            //o.SetValue("DISPLAY_FAULT12", (double)S(Regs.DisplayFault12));
            //o.SetValue("DISPLAY_FAULT13", (double)S(Regs.DisplayFault13));
            //o.SetValue("DISPLAY_FAULT14", (double)S(Regs.DisplayFault14));
            //o.SetValue("DISPLAY_FAULT15", (double)S(Regs.DisplayFault15));

            // 版本信息 0x0100:真机不支持(0x02 非法地址),输出留空
            o.SetValue("VERSION", "");

            // 常规参数 0x0400~0x0408:真机对 0x04xx 区忽略读取数量、每次只回 1 个寄存器,
            // 所以逐寄存器读;个别地址真机上不存在(0x02)时跳过,不影响整体成功。
            await ReadParamAsync(o, Regs.Param0000, "PARAM_0000", raw => (double)raw);
            await ReadParamAsync(o, Regs.SetTemp, "SET_TEMP", Tenths);
            await ReadParamAsync(o, Regs.Param0002, "PARAM_0002", raw => (double)raw);
            await ReadParamAsync(o, Regs.StartStopMode, "START_STOP_MODE", raw => (double)raw);
            await ReadParamAsync(o, Regs.CompressorSelect, "COMPRESSOR_SELECT", raw => (double)raw);
            await ReadParamAsync(o, Regs.Language, "LANGUAGE", raw => (double)raw);
            await ReadParamAsync(o, Regs.CommAddress, "COMM_ADDRESS", raw => (double)raw);
            await ReadParamAsync(o, Regs.BaudRate, "BAUD_RATE", raw => (double)raw);
            await ReadParamAsync(o, Regs.Parity, "PARITY", raw => (double)raw);

            // 厂家参数 0x0500~0x054F:真机不支持(0x02 非法地址),整段跳过不再读取

            o.SetValue("Success", true);
            return o;
        }

        /// <summary>
        /// 逐寄存器读常规参数:真机对 0x04xx 区忽略读取数量、只回 1 个寄存器。
        /// 读失败(该地址真机上不存在/超时)只记日志不抛,不影响整条「获取所有数据」的成功。
        /// </summary>
        private async Task ReadParamAsync(DeviceParameters o, ushort addr, string key, Func<ushort, double> convert)
        {
            try
            {
                ushort[] r = await ReadRegistersAsync(addr, 1, CancellationToken.None, quiet: true);
                if (r.Length == 1) o.SetValue(key, convert(r[0]));
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[冷水机] 读参数 0x{addr:X4} 失败:{ex.Message}");
            }
        }

        private async Task<DeviceParameters> ReadAlarmsAsync()
        {
            var o = new DeviceParameters
            {
                Variables = new ObservableCollection<ParameterBase>
                {
                    new BooleanParameter("HasAlarm", false, "是否有报警", true),
                    new NumberParameter("Count", 0, 64, 0, "报警条数", true),
                    new StringParameter("Alarms", "", "报警明细"),
                    new BooleanParameter("Success", false, "是否读到"),
                }
            };

            if (IsSimulationMode) { o.SetValue("Success", true); return o; }

            ushort[] r = await ReadRegistersAsync(Regs.FaultStatus1, 3, CancellationToken.None);
            if (r.Length < 3) return o;

            var list = CollectAlarms(r[0], r[1]);
            o.SetValue("HasAlarm", list.Count > 0);
            o.SetValue("Count", (double)list.Count);
            o.SetValue("Alarms", string.Join("、", list));
            o.SetValue("Success", true);
            return o;
        }

        #endregion

        #region 取值与文字

        /// <summary>寄存器值 → 一位小数的实际值。s16 负数按补码。</summary>
        private static double Tenths(ushort raw) => (short)raw / 10.0;

        private static List<string> CollectAlarms(ushort fault1, ushort fault2)
        {
            var list = new List<string>();
            for (int i = 0; i < Fault1Bits.Names.Length; i++)
                if ((fault1 >> i & 1) != 0) list.Add(Fault1Bits.Names[i]);
            for (int i = 0; i < Fault2Bits.Names.Length; i++)
                if ((fault2 >> i & 1) != 0) list.Add(Fault2Bits.Names[i]);
            return list;
        }

        private static string DescribeAlarms(ushort fault1, ushort fault2)
        {
            var list = CollectAlarms(fault1, fault2);
            return list.Count == 0 ? "" : string.Join("、", list);
        }

        /// <summary>模拟模式下造一份看起来合理的读数,方便无仪器时调流程和屏幕。</summary>
        private static void FillSimulated(DeviceParameters o)
        {
            double t = Environment.TickCount64 / 1000.0;

            //o.SetValue("RUN_STATE", 2d);
            //o.SetValue("RUN_STATE_TEXT", RunStateText.Describe(2));
            //o.SetValue("INPUT_STATUS", 0d);
            o.SetValue("ANTI_FREEZE_TEMP", 4.0);
            //o.SetValue("RELAY_STATUS", 0x0101);
            //o.SetValue("FAULT_STATUS1", 0d);
            //o.SetValue("FAULT_STATUS2", 0d);
            //o.SetValue("FAULT_STATUS3", 0d);
                o.SetValue("LIQUID_TEMP", Math.Round(7.0 + 0.5 * Math.Sin(t / 9.0), 1));
            //o.SetValue("COMP1_CURRENT", Math.Round(12.0 + 0.5 * Math.Sin(t / 5.0), 1));
            //o.SetValue("COMP2_CURRENT", Math.Round(11.5 + 0.5 * Math.Cos(t / 5.0), 1));
            //o.SetValue("CHILLED_PUMP_CURRENT", 3.5);
            //o.SetValue("COOLING_PUMP_CURRENT", 3.2);
            //o.SetValue("COMP1_RUN_HOURS", 1234d);
            //o.SetValue("COMP1_RUN_MINUTES", 25d);
            //o.SetValue("COMP2_RUN_HOURS", 987d);
            //o.SetValue("COMP2_RUN_MINUTES", 12d);
            //o.SetValue("FAULT_TEXT", "");
            for (int i = 1; i <= 15; i++) o.SetValue($"DISPLAY_FAULT{i}", 0d);

            //o.SetValue("VERSION", "A1.59360A.TY.B30M.V00A400");
            //o.SetValue("PARAM_0000", 0d);
                 o.SetValue("SET_TEMP", 8.0);
            //o.SetValue("PARAM_0002", 0d);
            //o.SetValue("START_STOP_MODE", 1d);
            //o.SetValue("COMPRESSOR_SELECT", 2d);
            //o.SetValue("LANGUAGE", 0d);
            //o.SetValue("COMM_ADDRESS", 1d);
            //o.SetValue("BAUD_RATE", 1d);
            //o.SetValue("PARITY", 0d);

            //o.SetValue("F_ONE_KEY_START", 0d);
            //o.SetValue("F_AUTO_START_ON_POWER", 0d);
            //o.SetValue("F_ALARM_OUTPUT_MODE", 0d);
            //o.SetValue("F_ALARM_OUTPUT_TYPE", 0d);
            //o.SetValue("F_DI5_SELECT", 0d);
            //o.SetValue("F_DI9_SELECT", 0d);
            //o.SetValue("F_WATER_LEVEL_LOW_ACTION", 0d);
            //o.SetValue("F_WATER_FLOW_LOW_ACTION", 0d);
            //o.SetValue("F_THREE_PHASE_DETECT", 0d);
            //o.SetValue("F_DI1_SELECT", 0d);
            //o.SetValue("F_COMPRESSOR_COUNT", 2d);
            //o.SetValue("F_LOAD_DEVIATION", 1.0);
            //o.SetValue("F_UNLOAD_DEVIATION", 1.0);
            //o.SetValue("F_SET_TEMP_MAX", 30.0);
            //o.SetValue("F_SET_TEMP_MIN", 5.0);
            //o.SetValue("F_LIQUID_TEMP_COMP", 0.0);
            //o.SetValue("F_LOW_TEMP_PROTECT", 4.0);
            //o.SetValue("F_OVER_TEMP_WARN", 50.0);
            //o.SetValue("F_OVER_TEMP_ALARM", 60.0);
            //o.SetValue("F_OVER_TEMP_HYSTERESIS", 5.0);
                 o.SetValue("F_ANTI_FREEZE_TEMP_COMP", 0.0);
            //o.SetValue("F_CHILLED_START_DELAY", 10d);
            //o.SetValue("F_COOLING_START_DELAY", 10d);
            //o.SetValue("F_ENERGY_CYCLE", 5d);
            //o.SetValue("F_COMPRESSOR_PROTECT", 60d);
            //o.SetValue("F_GENERAL_FAULT_CLEAR", 2d);
            //o.SetValue("F_WATER_FLOW_DELAY", 10d);
            //o.SetValue("F_LOW_PRESSURE_DELAY", 60d);
            //o.SetValue("F_LOW_PRESSURE_CLEAR", 5d);
            //o.SetValue("F_CURRENT_DETECT", 0d);
            //o.SetValue("F_COMP1_RATED_CURRENT", 30.0);
            //o.SetValue("F_COMP2_RATED_CURRENT", 30.0);
            //o.SetValue("F_CHILLED_PUMP_RATED", 3.0);
            //o.SetValue("F_COOLING_PUMP_RATED", 3.0);
            //o.SetValue("F_CHILLED_OVERLOAD", 0d);
            //o.SetValue("F_CHILLED_FLOW_SWITCH", 0d);
            //o.SetValue("F_WATER_LEVEL_SWITCH", 0d);
            //o.SetValue("F_COMPRESSOR_OVERLOAD", 0d);
            //o.SetValue("F_COMPRESSOR_LOW_PRESS", 0d);
            //o.SetValue("F_COMPRESSOR_HIGH_PRESS", 0d);
            //o.SetValue("F_THREE_PHASE_FAULT", 0d);
            //o.SetValue("F_COOLING_OVERLOAD", 0d);

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
        /// 文档没写单次读取上限,这里按 Modbus 惯例 125 个寄存器做硬拦截。
        /// </summary>
        /// <summary>
        /// 读寄存器(0x03/0x04)。quiet=true 时失败只静默返回空数组不写警告日志,
        /// 供「该地址真机上可能不存在」的逐寄存器参数读取使用。
        /// </summary>
        private async Task<ushort[]> ReadRegistersAsync(ushort start, ushort count, CancellationToken token, bool quiet = false)
        {
            if (count is < 1 or > 125)
                throw new ArgumentOutOfRangeException(nameof(count), count, "单次读取寄存器数应为 1~125");

            byte[] cmd = BuildReadCmd(start, count);
            return await RetryAsync(async () =>
            {
                var sem = GetInstanceLock();
                await sem.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    byte expectedFuncCode =(start < 0x0200) ? (byte)0x04 : (byte)0x03;
                    byte[] recv = new byte[512];
                    using var transport = await GetTransportAsync().ConfigureAwait(false);
                    int len = await transport.SendAndReceiveAsync(cmd, recv, IoTimeoutMs, token).ConfigureAwait(false);

                    int expectBytes = count * 2;
                    int expectLen = 5 + expectBytes; // 站号1+功能码1+字节数1+数据+CRC2

                    if (len < 5) { if (!quiet) _logger.LogWarning($"[冷水机] 应答太短:{len} 字节"); return Array.Empty<ushort>(); }
                    if (recv[0] != GetSlaveAddress())
                    {
                        if (!quiet) _logger.LogWarning($"[冷水机] 站号不匹配:期望 {GetSlaveAddress()},实际 {recv[0]}");
                        return Array.Empty<ushort>();
                    }
                    if ((recv[1] & 0x80) != 0)
                    {
                        if (!quiet)
                            _logger.LogWarning(
                                $"[冷水机] 读 0x{start:X4}+{count} 被拒,Modbus 异常码 0x{recv[2]:X2}" +
                                (recv[2] == 0x02 ? "(非法地址 —— 这一段里有仪器上不存在的寄存器)" : ""));
                        return Array.Empty<ushort>();
                    }
                    if (recv[1] != expectedFuncCode || len < expectLen || recv[2] != expectBytes)
                    {
                        if (!quiet)
                            _logger.LogWarning(
                                $"[冷水机] 应答格式不对:功能码 0x{recv[1]:X2},字节数 {recv[2]},收到 {len} 字节,期望 {expectLen}");
                        return Array.Empty<ushort>();
                    }
                    if (!Crc16Modbus.VerifyPrefix(recv, expectLen))
                    {
                        if (!quiet) _logger.LogWarning($"[冷水机] 读 0x{start:X4} 应答 CRC 校验失败,丢弃");
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

                    if (!ValidateWriteResponse(cmd, recv, len, $"写 0x{reg:X4}={value}")) return false;
                    for (int i = 0; i < 6; i++)
                        if (recv[i] != cmd[i])
                        {
                            _logger.LogWarning($"[冷水机] 写 0x{reg:X4} 回显不一致,第 {i} 字节 {recv[i]:X2} != {cmd[i]:X2}");
                            return false;
                        }
                    return true;
                }
                finally { sem.Release(); }
            }, $"写 0x{reg:X4}={value}", token);
        }

        /// <summary>写单个线圈(0x05)。0xFF00 有效 / 0x0000 无效。</summary>
        private async Task<bool> WriteSingleCoilAsync(ushort addr, bool on, CancellationToken token)
        {
            byte[] cmd = BuildWriteCoilCmd(addr, on);
            return await RetryAsync(async () =>
            {
                var sem = GetInstanceLock();
                await sem.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    byte[] recv = new byte[64];
                    using var transport = await GetTransportAsync().ConfigureAwait(false);
                    int len = await transport.SendAndReceiveAsync(cmd, recv, IoTimeoutMs, token).ConfigureAwait(false);

                    if (!ValidateWriteResponse(cmd, recv, len, $"写线圈 0x{addr:X4}={(on ? "ON" : "OFF")}")) return false;
                    for (int i = 0; i < 6; i++)
                        if (recv[i] != cmd[i])
                        {
                            _logger.LogWarning($"[冷水机] 写线圈 0x{addr:X4} 回显不一致,第 {i} 字节 {recv[i]:X2} != {cmd[i]:X2}");
                            return false;
                        }
                    return true;
                }
                finally { sem.Release(); }
            }, $"写线圈 0x{addr:X4}", token);
        }
        private bool ValidateWriteResponse(byte[] request, byte[] response, int length, string what)
        {
            _logger.LogInformation($"[冷水机] {what} TX={Convert.ToHexString(request)} " +
                $"RX={Convert.ToHexString(response, 0, Math.Clamp(length, 0, response.Length))}");
            if (length < 5 || length > response.Length)
            {
                _logger.LogWarning($"[冷水机] {what} 应答长度不对:{length}");
                return false;
            }
            bool exception = response[1] == (byte)(request[1] | 0x80);
            int frameLength = exception ? 5 : 8;
            if (length != frameLength || response[0] != request[0] ||
                !Crc16Modbus.VerifyPrefix(response, frameLength))
            {
                _logger.LogWarning($"[冷水机] {what} 应答站号/长度/CRC 校验失败:" +
                    Convert.ToHexString(response, 0, length));
                return false;
            }
            if (exception)
            {
                _logger.LogWarning($"[冷水机] {what} 被拒,Modbus 异常码 0x{response[2]:X2}");
                return false;
            }
            if (response[1] != request[1])
            {
                _logger.LogWarning($"[冷水机] {what} 功能码不符:0x{response[1]:X2}");
                return false;
            }
            return true;
        }

        /// <summary>
        /// 超时重试。只重试超时 —— 协议错、CRC 错、异常码这些重试没有意义。
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
                        _logger.LogWarning($"[冷水机] {what} 超时(第 {attempt}/{MaxRetries} 次),{RetryDelayMs}ms 后重试");
                        await Task.Delay(RetryDelayMs, token).ConfigureAwait(false);
                    }
                }
            }
            throw new TimeoutException($"{what} 失败,{MaxRetries} 次尝试后仍超时", last);
        }

        private byte[] BuildReadCmd(ushort reg, ushort count)
        {
            byte funcCode =(reg < 0x0200) ? (byte)0x04 : (byte)0x03;
            byte[] f =
            {
                GetSlaveAddress(), funcCode,
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

        private byte[] BuildWriteCoilCmd(ushort addr, bool on)
        {
            byte[] f =
            {
                GetSlaveAddress(), 0x05,
                (byte)(addr >> 8), (byte)(addr & 0xFF),
                on ? (byte)0xFF : (byte)0x00, 0x00,
            };
            return Crc16Modbus.AppendCrc(f, 0, f.Length);
        }
        #endregion

        #region 参数校验

        private static double RequiredNumber(DeviceParameters? p, string name, double min, double max)
        {
            double? v = p?.GetNullableValue<double>(name);
            if (!v.HasValue || !double.IsFinite(v.Value) || v.Value < min || v.Value > max)
                throw new ArgumentException($"参数 {name} 必须提供且位于 {min}~{max} 范围内");
            return v.Value;
        }

        private static ushort RequiredInteger(DeviceParameters? p, string name, int min, int max)
        {
            double v = RequiredNumber(p, name, min, max);
            if (v != Math.Truncate(v)) throw new ArgumentException($"参数 {name} 必须是整数");
            return checked((ushort)v);
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
                    // 将驱动内部的失败变成异常,避免「执行成功」掩盖「设置失败」。
                    if (output.Variables.Any(v => v.Name == "Success") && !output.GetValue<bool>("Success"))
                        throw new InvalidOperationException("设备未确认命令成功,请查看通信日志中的应答和异常码");
                    sw.Stop();
                    _logger.LogInformation(
                        $"[冷水机命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex,
                        $"[冷水机命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
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

        public static bool VerifyPrefix(byte[] buffer, int frameLen)
        {
            if (buffer == null || frameLen < 4 || buffer.Length < frameLen) return false;
            ushort crc = Compute(buffer, 0, frameLen - 2);
            return buffer[frameLen - 2] == (byte)(crc & 0xFF)
                && buffer[frameLen - 1] == (byte)(crc >> 8);
        }
    }
}