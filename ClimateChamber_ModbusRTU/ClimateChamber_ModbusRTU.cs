// ============================================================================
//  ClimateChamber_ModbusRTU.cs
//  人工气候箱驱动 —— Modbus RTU / RS485。
//  依据用户提供的《modbus 协议》文档(4 页,寄存器表)。
//
//  ── 关于设备名 ────────────────────────────────────────────────────
//      协议文档从头到尾没写这是什么设备,只给了寄存器表。从表里的
//      温度 PID / 湿度 PID / CO2 PID 自整定、太阳光输出、LED light1~3 + UV、
//      定时启停、60 段程序 这一组东西看,是一台人工气候箱(光照培养箱)。
//      名字只影响界面显示,不影响通信;如果实际是别的设备,改 Name 一处即可。
//
//  ── 通信参数(文档"项目/内容"表) ──────────────────────────────────
//      Modbus-RTU / RS485 / 设备地址 3(文档注"用户历史约定")
//      9600 / 8 数据位 / **Even 偶校验** / 1 停止位
//      只支持 03H(读保持寄存器)与 06H(写单个保持寄存器)
//      —— 注意没有 0x10,所以写多个寄存器只能一条条 06 发。
//      全部寄存器都是保持寄存器,uint16;int16 负数按补码;大端。
//      访问不存在的地址返回异常码 02。
//
//  ── 文档里两处说不死的地方,做成了参数,不要写死 ──────────────────
//  1) 程序段地址的进制。文档写"定值模式:设定温度2000,设定时间2009,
//     设定转速2010;程序段1:2020/2029/2030;以下依次类推最多60段"。
//     全文其它地址都带 0x 前缀,唯独这几个不带 —— 按字面是十进制,
//     但 步长 20 / 偏移 9、10 这一组数字换成十六进制也一样自洽。
//     猜错的话整张程序段表就全错了,所以把"基址"和"步长"做成设备参数
//     (默认 2000 / 20,即十进制那一种读法),现场对不上时改参数即可,
//     不用重新出驱动。
//
//  2) 测量值那一块的功能码。文档最后写"只读区 input:测量温度 0x10,
//     设定温度 0x11,测量转速 0x16,设定转速 0x17"。"input"像是在说
//     输入寄存器(功能码 04),但前面"功能码支持"一栏只列了 03 和 06。
//     默认按 03 读(以明确列出的那一栏为准),另给一个参数可切到 04。
//
//  3) 温度和转速的小数位。文档没有给任何标度说明。这类控制器温度通常是
//     ×10 传输,转速是整数,所以默认 1 位 / 0 位,同样做成参数 ——
//     现场看一眼仪器面板上的数就知道对不对,改参数比改代码快。
//
//  ── 读的时候为什么要分块 ──────────────────────────────────────────
//      0x1000~0x1015 这一段里,文档只列了 0x1000~0x1005、0x1007、
//      0x100B~0x100D、0x1014、0x1015,中间几个是空的。空的地址到底存不存在
//      文档没说,而访问不存在的地址会被整条拒掉(异常码 02)。
//      所以第一次读时先赌一把整段读,被拒了就退回按"文档列出来的连续小段"
//      逐段读,并把这个结论记下来,之后不再试。一次探测,长期受益。
// ============================================================================

using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;

namespace ClimateChamber_ModbusRTU
{
    /// <summary>寄存器地址表。全部为保持寄存器,读 03,写 06。</summary>
    internal static class Regs
    {
        // ── 只读测量区(文档末尾的"只读区 input") ──────────
        public const ushort TempPv = 0x0010;   // 测量温度
        public const ushort TempSet = 0x0011;   // 设定温度
        public const ushort SpeedPv = 0x0016;   // 测量转速
        public const ushort SpeedSet = 0x0017;   // 设定转速

        // ── 控制与状态区 ────────────────────────────────
        public const ushort RunState = 0x1000;   // 设备启停(需 lic_ok=1)
        public const ushort BuzzerClear = 0x1001;   // 蜂鸣器清除
        public const ushort MotorRun = 0x1002;   // 电机运行(需 lic_ok=1)
        public const ushort TempAutoTune = 0x1003;   // 温度 PID 自整定
        public const ushort HumiAutoTune = 0x1004;   // 湿度 PID 自整定
        public const ushort Sunlight = 0x1005;   // 太阳光输出
        public const ushort Strip = 0x1007;   // 剥离输出
        public const ushort Co2AutoTune = 0x100B;   // CO2 PID 自整定
        public const ushort TimerStartEn = 0x100C;   // 定时启动使能
        public const ushort TimerStopEn = 0x100D;   // 定时停止使能
        public const ushort RemainDebugSw = 0x1014;   // 标记剩余时间调试开关(写 0x5555 切换)
        public const ushort IoDebugEnable = 0x1015;   // IO 调试总使能(写 0x3333 切换)

        // ── 调试输出(写 0x0F 电平翻转) ─────────────────
        public const ushort DebugHeat = 0x1080;
        public const ushort DebugCool = 0x1081;
        public const ushort DebugHumi = 0x1082;
        public const ushort DebugOut1 = 0x1083;   // out1~out10 连续到 0x108C

        // ── LED 调试输出(写值直接赋值) ─────────────────
        public const ushort LedLight1 = 0x10A0;   // 写它会同时清 light2、light3
        public const ushort LedLight2 = 0x10A1;
        public const ushort LedLight3 = 0x10A2;
        public const ushort LedUv = 0x10A4;

        // ── 时间区(年/月/日/星期/时/分/秒,各占一个寄存器) ─
        public const ushort ClockYear = 0x1028;   // 0x1028~0x102E
        public const ushort TimerStartTime = 0x1030;   // 0x1030~0x1036,写后置位保存请求
        public const ushort TimerStopTime = 0x1038;   // 0x1038~0x103E,写后置位保存请求
        public const int TimeFieldCount = 7;

        // ── 状态区一次读全的范围(先赌这一段) ───────────
        public const ushort StatusWideStart = 0x1000;
        public const ushort StatusWideCount = 22;      // 0x1000~0x1015

        // ── 退化方案:文档明确列出的连续小段 ─────────────
        public static readonly (ushort Start, ushort Count)[] StatusChunks =
        {
            (0x1000, 6),   // 0x1000~0x1005
            (0x1007, 1),   // 剥离
            (0x100B, 3),   // 0x100B~0x100D
            (0x1014, 2),   // 0x1014~0x1015
        };

        public const ushort MeasureWideStart = 0x0010;
        public const ushort MeasureWideCount = 8;       // 0x0010~0x0017

        public static readonly (ushort Start, ushort Count)[] MeasureChunks =
        {
            (0x0010, 2),   // 测量温度、设定温度
            (0x0016, 2),   // 测量转速、设定转速
        };
    }

    /// <summary>
    /// 0x1000 / 0x1002 这类启停寄存器的写入值。
    /// 文档给了三组等价编码(0x0000/0x03/0x0303 都是停止),这里取最短的一组。
    /// </summary>
    internal static class RunCmd
    {
        public const ushort Stop = 0x0000;
        public const ushort Start = 0x0001;
        public const ushort Toggle = 0x0002;
    }

    [Export(typeof(IDevice))]
    public class ClimateChamber_ModbusRTU : Device, IDeviceScreen
    {
        /// <summary>源码版本戳。**改动本文件时手动更新**,现场日志第一行会打出来。</summary>
        public const string SourceStamp = "2026-09-01-a";

        private readonly ILogService _logger;

        private const int MaxRetries = 3;
        private const int RetryDelayMs = 100;
        private const int IoTimeoutMs = 3000;

        /// <summary>写 0x1014 / 0x1015 这两个"切换开关"的固定口令,文档规定。</summary>
        private const ushort RemainDebugMagic = 0x5555;
        private const ushort IoDebugMagic = 0x3333;

        /// <summary>写调试输出(0x1080~0x108C)的翻转口令,文档规定。</summary>
        private const ushort DebugToggleMagic = 0x000F;

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _instanceLocks = new();

        /// <summary>
        /// 一次探测的结论:这一块能不能整段读。null = 还没试过,true = 能,false = 得分段。
        /// 见文件抬头"读的时候为什么要分块"。
        ///
        /// 包成一个类而不是直接用 bool? 字段,是因为 ReadAdaptiveAsync 是 async 方法,
        /// C# 不允许 async 方法带 ref 参数,只能传引用类型进去让它就地改。
        /// </summary>
        private sealed class WideProbe { public bool? Ok; }

        private readonly WideProbe _statusProbe = new();
        private readonly WideProbe _measureProbe = new();

        public string ScreenKey => "ClimateChamberScreen";

        public ClimateChamber_ModbusRTU()
        {
            _logger = LogManager.GetLogger<ClimateChamber_ModbusRTU>();

            DeviceId = "CLC0001";
            Name = "全温振荡培养箱";
            Manufacturer = "全温振荡培养箱MB320(Modbus RTU / RS485)";
            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/ClimateChamber.png";
            Category = DeviceCategories.Reactors;
            ComId = "CLC0001";
            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();

            AllowedRegions = new List<RegionType>
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            Parameters.Variables.Add(new StringParameter("DeviceId", "CLC0001", "设备实例编号")
            {
                Options = new ObservableCollection<string> { "CLC0001", "CLC0002", "CLC0003", "CLC0004" }
            });

            Parameters.Variables.Add(new StringParameter("串口号", SerialPortOptions.DefaultPort(), "RS485 转换器对应的串口")
            {
                Options = SerialPortOptions.CreateOptions()
            });

            Parameters.Variables.Add(new StringParameter("波特率", "9600", "文档规定 9600")
            {
                Options = new ObservableCollection<string> { "4800", "9600", "19200", "38400", "57600", "115200" }
            });

            // 默认 Even —— 这台跟仓库里其它 Modbus 设备不一样,别顺手改成 None。
            Parameters.Variables.Add(new StringParameter("校验位", "Even", "文档规定偶校验,与本仓库其它 Modbus 设备不同")
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

            // 文档写"设备地址 3(用户历史约定)",所以默认 3 而不是常见的 1。
            Parameters.Variables.Add(new NumberParameter("Modbus站号", 1, 254, 3, "Modbus 从机站号。文档给的是 3(用户历史约定)"));

            // ── 下面四个是"文档没说死"的项,见文件抬头 ──────────────
            Parameters.Variables.Add(new NumberParameter("程序段基址", 0, 65535, 2000,
                "定值模式的设定温度地址。文档写的是 2000 且没带 0x —— 按十进制理解就是 2000;" +
                "若现场发现是十六进制,填 8192(=0x2000)"));

            Parameters.Variables.Add(new NumberParameter("程序段步长", 1, 4096, 20,
                "相邻两个程序段之间的地址间隔。文档:程序段1 比定值模式高 20"));

            Parameters.Variables.Add(new NumberParameter("温度小数位", 0, 3, 1,
                "温度寄存器的小数位数。文档未给标度,默认 1 位(寄存器值 ÷10)。对不上就改这里"));

            Parameters.Variables.Add(new NumberParameter("转速小数位", 0, 3, 0,
                "转速寄存器的小数位数。文档未给标度,默认 0 位(寄存器值即实际值)"));

            Parameters.Variables.Add(new StringParameter("测量值功能码", "03",
                "读 0x0010/0x0011/0x0016/0x0017 用哪个功能码。文档「功能码支持」只列了 03," +
                "但末尾把这块叫「只读区 input」——「input」通常指功能码 04。默认 03,读不到再切 04")
            {
                Options = new ObservableCollection<string> { "03", "04" }
            });

            InitializeCommands();
        }

        public override bool SupportsZLanGateway => true;

        #region 可监控参数

        public override IReadOnlyList<CollectableParameter> CollectableParameters => new[]
        {
            new CollectableParameter("TEMP_PV",   "箱内温度", "℃",  "获取所有数据", "TEMP_PV", isKey: true),
            new CollectableParameter("TEMP_SET",  "温度设定", "℃",  "获取所有数据", "TEMP_SET"),
            new CollectableParameter("SPEED_PV",  "转速",     "RPM", "获取所有数据", "SPEED_PV"),
            new CollectableParameter("SPEED_SET", "转速设定", "RPM", "获取所有数据", "SPEED_SET"),
            new CollectableParameter("RUNNING",   "运行状态", "",    "获取所有数据", "RUNNING"),
        };

        public override int AutoCollectIntervalMs => 1500;

        #endregion

        #region 连接

        protected override Task<bool> OnConnectAsync() => ProbeAsync("直连");
        protected override Task<bool> OnConnectViaGatewayAsync() => ProbeAsync("ZLAN 网关");

        private async Task<bool> ProbeAsync(string via)
        {
            try
            {
                _logger.LogInformation(
                    $"[人工气候箱 {SourceStamp}] 经{via}连接,站号 {GetSlaveAddress()}," +
                    $"串口 {Parameters.GetValue<string>("串口号")} @ {Parameters.GetValue<string>("波特率")}/" +
                    $"{Parameters.GetValue<string>("数据位")}/{Parameters.GetValue<string>("校验位")}/" +
                    $"{Parameters.GetValue<string>("停止位")}");

                // 这台仪器没有身份寄存器,只能读一个一定存在的地址当连通性判据。
                // 0x1000 是文档里的第一条,设备启停状态,最稳。
                ushort[] r = await ReadHoldingAsync(Regs.RunState, 1, CancellationToken.None);
                if (r.Length < 1) { ErrorLog("连接失败:读 0x1000 没有得到有效应答"); return false; }

                InfoLog($"连接成功,设备启停状态 = {r[0]}({(r[0] != 0 ? "运行" : "停止")})");
                return true;
            }
            catch (TimeoutException)
            {
                ErrorLog("连接超时:串口通了但仪器没应答 —— 先确认站号(文档给的是 3)、" +
                         "偶校验有没有设对、485 的 A/B 有没有反接");
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
            try { return (await ReadHoldingAsync(Regs.RunState, 1, CancellationToken.None)).Length == 1; }
            catch { return false; }
        }

        #endregion

        #region 命令

        private void InitializeCommands()
        {
            Commands.Add(new DeviceCommand
            {
                Name = "获取所有数据",
                HelpText = "读回测量温度/设定温度/测量转速/设定转速,以及启停、电机、" +
                           "三路 PID 自整定、太阳光、剥离、定时使能、两个调试开关的状态。屏幕轮询用。",
                OutputParameters = new DeviceParameters { Variables = BuildAllOutputs() },
                AsyncAction = AsyncWrapAction("获取所有数据", _ => ReadAllAsync())
            });

            // ── 启停 ────────────────────────────────────────────
            //
            // 文档在 0x1000 和 0x1002 两条都标了"需要 license 许可 lic_ok=1"。
            // lic_ok 是仪器内部的授权标志,协议里没有给读它的地址,上位机看不到。
            // 所以这两条命令可能会写成功(仪器回显正常)但设备不动 —— 这不是驱动
            // 的 bug。判断办法:发完之后看 0x1000 读回来变没变。
            AddWriteCommand("启动设备", "写 0x1000 = 1。注意:仪器侧需要 license(lic_ok=1)才会真的动",
                Regs.RunState, RunCmd.Start);
            AddWriteCommand("停止设备", "写 0x1000 = 0", Regs.RunState, RunCmd.Stop);
            AddWriteCommand("电机启动", "写 0x1002 = 1。同样需要 license(lic_ok=1)", Regs.MotorRun, RunCmd.Start);
            AddWriteCommand("电机停止", "写 0x1002 = 0", Regs.MotorRun, RunCmd.Stop);
            AddWriteCommand("蜂鸣器清除", "写 0x1001 = 1", Regs.BuzzerClear, 1);

            // ── 输出 ────────────────────────────────────────────
            AddOnOffCommand("太阳光输出", "0 关闭 / 1 打开 / 2 翻转", Regs.Sunlight);
            AddOnOffCommand("剥离输出", "0 关闭请求 / 1 开启请求 / 2 翻转请求", Regs.Strip);

            // ── PID 自整定 ──────────────────────────────────────
            //
            // 文档三条都注了"1 启动(设备运行时才生效)"。设备没运行时发 1,
            // 仪器会正常回显但不进自整定 —— 和上面 lic_ok 是同一类情况。
            AddOnOffCommand("温度PID自整定", "0 退出 / 1 启动(设备运行时才生效) / 2 翻转", Regs.TempAutoTune);
            AddOnOffCommand("湿度PID自整定", "0 退出 / 1 启动(设备运行时才生效) / 2 翻转", Regs.HumiAutoTune);
            AddOnOffCommand("CO2PID自整定", "0 退出 / 1 启动(设备运行时才生效) / 2 翻转", Regs.Co2AutoTune);

            // ── 定时使能 ────────────────────────────────────────
            AddOnOffCommand("定时启动使能", "0 关闭 / 1 开启 / 2 翻转。写后仪器会自行保存", Regs.TimerStartEn);
            AddOnOffCommand("定时停止使能", "0 关闭 / 1 开启 / 2 翻转。写后仪器会自行保存", Regs.TimerStopEn);

            // ── 时间 ────────────────────────────────────────────
            Commands.Add(new DeviceCommand
            {
                Name = "同步系统时间",
                HelpText = "把上位机当前时间写进 0x1028~0x102E(年/月/日/星期/时/分/秒)。" +
                           "文档只支持 06,所以是连发七条单寄存器写。",
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("同步系统时间", _ => WriteClockAsync(Regs.ClockYear, DateTime.Now))
            });

            AddTimeCommand("设置定时启动时间", "写 0x1030~0x1036。写后仪器置位保存请求", Regs.TimerStartTime);
            AddTimeCommand("设置定时停止时间", "写 0x1038~0x103E。写后仪器置位保存请求", Regs.TimerStopTime);

            Commands.Add(new DeviceCommand
            {
                Name = "读取仪器时间",
                HelpText = "读 0x1028~0x102E",
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new StringParameter("Clock", "", "仪器当前时间"),
                        new BooleanParameter("Success", false, "是否读到"),
                    }
                },
                AsyncAction = AsyncWrapAction("读取仪器时间", _ => ReadClockAsync())
            });

            // ── 程序段 ──────────────────────────────────────────
            Commands.Add(new DeviceCommand
            {
                Name = "写程序段",
                HelpText = "写一个程序段的设定温度 / 设定时间 / 设定转速。" +
                           "段号 0 = 定值模式,1~60 = 程序段 1~60。" +
                           "地址 = 程序段基址 + 段号 × 程序段步长,偏移 +0 温度 / +9 时间 / +10 转速。",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("Segment", 0, 60, 0, "段号,0=定值模式", false),
                        new NumberParameter("Temp", -100, 300, 25, "设定温度", false, "℃") { DecimalPlaces = 1 },
                        new NumberParameter("Time", 0, 65535, 0, "设定时间", false),
                        new NumberParameter("Speed", 0, 65535, 0, "设定转速", false, "RPM"),
                    }
                },
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("写程序段", WriteSegmentAsync)
            });

            Commands.Add(new DeviceCommand
            {
                Name = "读程序段",
                HelpText = "读一个程序段的设定温度 / 设定时间 / 设定转速",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("Segment", 0, 60, 0, "段号,0=定值模式", false),
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("Temp", -100, 300, 0, "设定温度", true, "℃"),
                        new NumberParameter("Time", 0, 65535, 0, "设定时间", true),
                        new NumberParameter("Speed", 0, 65535, 0, "设定转速", true, "RPM"),
                        new NumberParameter("BaseAddress", 0, 65535, 0, "本段基址(排查用)", true),
                        new BooleanParameter("Success", false, "是否读到"),
                    }
                },
                AsyncAction = AsyncWrapAction("读程序段", ReadSegmentAsync)
            });

            // ── LED ─────────────────────────────────────────────
            //
            // 文档:写 light1 会同时清 light2、light3,写 light2 清 light1、light3……
            // 也就是三路是互斥的,同一时刻只有一路有值。界面上按"选一路+给一个值"
            // 来做才对,做成三个独立滑条会让人以为能同时点亮三路。
            Commands.Add(new DeviceCommand
            {
                Name = "设置LED",
                HelpText = "写 LED 调试输出。通道 1/2/3 三路互斥(写其中一路会清掉另两路)," +
                           "通道 4 = UV,独立。值直接赋给寄存器。",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("Channel", 1, 4, 1, "1/2/3 = light1~3(互斥),4 = UV", false),
                        new NumberParameter("Value", 0, 65535, 0, "写入值", false),
                    }
                },
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("设置LED", SetLedAsync)
            });

            // ── 调试(仅命令,不上屏幕) ─────────────────────────
            //
            // 0x1080~0x108C 这些是直接翻转加热/制冷/加湿和十路通用输出的电平,
            // 绕过了仪器自己的控制逻辑。放在命令里是因为协议里有、调试时要用;
            // **没有放到屏幕上**,是因为屏幕是给日常操作用的,一个手滑点到
            // "翻转加热"的后果跟点错一个设定值不是一个量级。
            AddDebugToggleCommand("调试-翻转加热输出", Regs.DebugHeat);
            AddDebugToggleCommand("调试-翻转制冷输出", Regs.DebugCool);
            AddDebugToggleCommand("调试-翻转加湿输出", Regs.DebugHumi);

            Commands.Add(new DeviceCommand
            {
                Name = "调试-翻转通用输出",
                HelpText = "翻转 out1~out10(0x1083~0x108C)之一。需要先打开 IO 调试总使能。",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("Index", 1, 10, 1, "输出序号 out1~out10", false),
                    }
                },
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("调试-翻转通用输出", async p =>
                {
                    if (IsSimulationMode) return Success(true);
                    int idx = (int)(p?.GetNullableValue<double>("Index") ?? 1);
                    idx = Math.Clamp(idx, 1, 10);
                    ushort reg = (ushort)(Regs.DebugOut1 + (idx - 1));
                    return Success(await WriteSingleAsync(reg, DebugToggleMagic, CancellationToken.None));
                })
            });

            Commands.Add(new DeviceCommand
            {
                Name = "调试-切换IO总使能",
                HelpText = "写 0x1015 = 0x3333 切换 IO 调试总使能。这是个切换动作,不是赋值 —— " +
                           "发一次开,再发一次关。0x1080~0x108C 那一组要它打开才有效。",
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("调试-切换IO总使能", async _ =>
                {
                    if (IsSimulationMode) return Success(true);
                    return Success(await WriteSingleAsync(Regs.IoDebugEnable, IoDebugMagic, CancellationToken.None));
                })
            });

            Commands.Add(new DeviceCommand
            {
                Name = "调试-切换剩余时间标记",
                HelpText = "写 0x1014 = 0x5555 切换「标记剩余时间调试开关」。同样是切换,不是赋值。",
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("调试-切换剩余时间标记", async _ =>
                {
                    if (IsSimulationMode) return Success(true);
                    return Success(await WriteSingleAsync(Regs.RemainDebugSw, RemainDebugMagic, CancellationToken.None));
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

        /// <summary>固定值写命令。</summary>
        private void AddWriteCommand(string name, string help, ushort reg, ushort value)
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

        /// <summary>0/1/2(关/开/翻转)三态写命令。</summary>
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
                        new NumberParameter("TargetValue", 0, 2, 0, help, false)
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

        private void AddDebugToggleCommand(string name, ushort reg)
        {
            Commands.Add(new DeviceCommand
            {
                Name = name,
                HelpText = $"写 0x{reg:X4} = 0x000F 翻转电平。需要先打开 IO 调试总使能。" +
                           "这是绕过仪器控制逻辑直接动执行器,只在调试时用。",
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction(name, async _ =>
                {
                    if (IsSimulationMode) return Success(true);
                    return Success(await WriteSingleAsync(reg, DebugToggleMagic, CancellationToken.None));
                })
            });
        }

        private void AddTimeCommand(string name, string help, ushort baseReg)
        {
            Commands.Add(new DeviceCommand
            {
                Name = name,
                HelpText = help + "。星期由年月日自动算出,不用填。",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("Year", 2000, 2099, DateTime.Now.Year, "年", false),
                        new NumberParameter("Month", 1, 12, DateTime.Now.Month, "月", false),
                        new NumberParameter("Day", 1, 31, DateTime.Now.Day, "日", false),
                        new NumberParameter("Hour", 0, 23, 0, "时", false),
                        new NumberParameter("Minute", 0, 59, 0, "分", false),
                        new NumberParameter("Second", 0, 59, 0, "秒", false),
                    }
                },
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction(name, p =>
                {
                    if (IsSimulationMode) return Task.FromResult(Success(true));
                    DateTime t;
                    try
                    {
                        t = new DateTime(
                            (int)(p?.GetNullableValue<double>("Year") ?? DateTime.Now.Year),
                            (int)(p?.GetNullableValue<double>("Month") ?? 1),
                            (int)(p?.GetNullableValue<double>("Day") ?? 1),
                            (int)(p?.GetNullableValue<double>("Hour") ?? 0),
                            (int)(p?.GetNullableValue<double>("Minute") ?? 0),
                            (int)(p?.GetNullableValue<double>("Second") ?? 0));
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        // 2 月 30 日这种。与其把非法日期写进仪器让它自己发疯,不如在这里挡掉。
                        ErrorLog("日期非法(比如 2 月 30 日),没有下发");
                        return Task.FromResult(Success(false));
                    }
                    return WriteClockAsync(baseReg, t);
                })
            });
        }

        #endregion

        #region 读

        private static ObservableCollection<ParameterBase> BuildAllOutputs() => new()
        {
            new NumberParameter("TEMP_PV", -100, 300, 0, "箱内温度测量值", true, "℃"),
            new NumberParameter("TEMP_SET", -100, 300, 0, "温度设定值", true, "℃"),
            new NumberParameter("SPEED_PV", 0, 65535, 0, "转速测量值", true, "RPM"),
            new NumberParameter("SPEED_SET", 0, 65535, 0, "转速设定值", true, "RPM"),

            new BooleanParameter("RUNNING", false, "设备是否运行", true),
            new BooleanParameter("MOTOR_RUN", false, "电机是否运行", true),
            new BooleanParameter("BUZZER_CLEAR", false, "蜂鸣器清除状态", true),
            new BooleanParameter("SUNLIGHT", false, "太阳光输出是否打开", true),
            new BooleanParameter("STRIP", false, "剥离输出是否打开", true),

            new BooleanParameter("TUNE_TEMP", false, "温度 PID 是否正在自整定", true),
            new BooleanParameter("TUNE_HUMI", false, "湿度 PID 是否正在自整定", true),
            new BooleanParameter("TUNE_CO2", false, "CO2 PID 是否正在自整定", true),

            new BooleanParameter("TIMER_START_EN", false, "定时启动使能", true),
            new BooleanParameter("TIMER_STOP_EN", false, "定时停止使能", true),

            new BooleanParameter("IO_DEBUG", false, "IO 调试总使能是否打开", true),
            new BooleanParameter("REMAIN_DEBUG", false, "剩余时间调试开关是否打开", true),

            new BooleanParameter("MEASURE_OK", false, "测量值那一块是否读到", true),
            new BooleanParameter("STATUS_OK", false, "状态那一块是否读到", true),
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

            var token = CancellationToken.None;

            // ── 测量值 ──────────────────────────────────────────
            bool measureOk = false;
            try
            {
                var m = await ReadAdaptiveAsync(
                    Regs.MeasureWideStart, Regs.MeasureWideCount, Regs.MeasureChunks,
                    _measureProbe, "测量值", useMeasureFunction: true, token);

                if (m.TryGetValue(Regs.TempPv, out var tp)) { o.SetValue("TEMP_PV", Scale(tp, TempDecimals)); measureOk = true; }
                if (m.TryGetValue(Regs.TempSet, out var ts)) o.SetValue("TEMP_SET", Scale(ts, TempDecimals));
                if (m.TryGetValue(Regs.SpeedPv, out var sp)) o.SetValue("SPEED_PV", Scale(sp, SpeedDecimals));
                if (m.TryGetValue(Regs.SpeedSet, out var ss)) o.SetValue("SPEED_SET", Scale(ss, SpeedDecimals));
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[人工气候箱] 读测量值失败:{ex.Message}");
            }

            // ── 状态位 ──────────────────────────────────────────
            bool statusOk = false;
            try
            {
                var s = await ReadAdaptiveAsync(
                    Regs.StatusWideStart, Regs.StatusWideCount, Regs.StatusChunks,
                    _statusProbe, "状态", useMeasureFunction: false, token);

                void Flag(string key, ushort reg)
                {
                    if (s.TryGetValue(reg, out var v)) o.SetValue(key, v != 0);
                }

                Flag("RUNNING", Regs.RunState);
                Flag("BUZZER_CLEAR", Regs.BuzzerClear);
                Flag("MOTOR_RUN", Regs.MotorRun);
                Flag("TUNE_TEMP", Regs.TempAutoTune);
                Flag("TUNE_HUMI", Regs.HumiAutoTune);
                Flag("SUNLIGHT", Regs.Sunlight);
                Flag("STRIP", Regs.Strip);
                Flag("TUNE_CO2", Regs.Co2AutoTune);
                Flag("TIMER_START_EN", Regs.TimerStartEn);
                Flag("TIMER_STOP_EN", Regs.TimerStopEn);
                Flag("REMAIN_DEBUG", Regs.RemainDebugSw);
                Flag("IO_DEBUG", Regs.IoDebugEnable);

                statusOk = s.Count > 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[人工气候箱] 读状态失败:{ex.Message}");
            }

            o.SetValue("MEASURE_OK", measureOk);
            o.SetValue("STATUS_OK", statusOk);
            // 两块里有一块读到就算这一轮有效 —— 屏幕上能显示一半总比整屏 "--" 强。
            o.SetValue("Success", measureOk || statusOk);
            return o;
        }

        /// <summary>
        /// "先赌整段、失败就分段"的读取。结论记在 probe 里,只探测一次。
        /// 返回 地址 → 寄存器值 的字典,读不到的地址不进字典(调用方用 TryGetValue)。
        /// </summary>
        private async Task<Dictionary<ushort, ushort>> ReadAdaptiveAsync(
            ushort wideStart, ushort wideCount, (ushort Start, ushort Count)[] chunks,
            WideProbe probe, string what, bool useMeasureFunction, CancellationToken token)
        {
            var map = new Dictionary<ushort, ushort>();

            if (probe.Ok != false)
            {
                try
                {
                    ushort[] all = useMeasureFunction
                        ? await ReadMeasureAsync(wideStart, wideCount, token)
                        : await ReadHoldingAsync(wideStart, wideCount, token);

                    if (all.Length == wideCount)
                    {
                        if (probe.Ok == null)
                        {
                            _logger.LogInformation($"[人工气候箱] {what}区可以一次读完(0x{wideStart:X4}+{wideCount}),之后按这个来");
                            probe.Ok = true;
                        }
                        for (int i = 0; i < wideCount; i++) map[(ushort)(wideStart + i)] = all[i];
                        return map;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[人工气候箱] {what}区整段读失败({ex.Message}),改成分段读");
                }

                if (probe.Ok == null)
                {
                    _logger.LogInformation(
                        $"[人工气候箱] {what}区不能整段读(中间有仪器上不存在的地址),之后一律分段,不再重试整段");
                    probe.Ok = false;
                }
            }

            foreach (var (start, count) in chunks)
            {
                try
                {
                    ushort[] part = useMeasureFunction
                        ? await ReadMeasureAsync(start, count, token)
                        : await ReadHoldingAsync(start, count, token);

                    for (int i = 0; i < part.Length && i < count; i++)
                        map[(ushort)(start + i)] = part[i];
                }
                catch (Exception ex)
                {
                    // 一小段失败不拖垮其它段。哪一段坏了日志里看得到。
                    _logger.LogWarning($"[人工气候箱] {what}区 0x{start:X4}+{count} 读失败:{ex.Message}");
                }
            }
            return map;
        }

        private async Task<DeviceParameters> ReadClockAsync()
        {
            var o = new DeviceParameters
            {
                Variables = new ObservableCollection<ParameterBase>
                {
                    new StringParameter("Clock", "", "仪器当前时间"),
                    new BooleanParameter("Success", false, "是否读到"),
                }
            };

            if (IsSimulationMode)
            {
                o.SetValue("Clock", DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss"));
                o.SetValue("Success", true);
                return o;
            }

            ushort[] r = await ReadHoldingAsync(Regs.ClockYear, Regs.TimeFieldCount, CancellationToken.None);
            if (r.Length < Regs.TimeFieldCount) return o;

            // 顺序:年 月 日 星期 时 分 秒
            o.SetValue("Clock", $"{r[0]:0000}/{r[1]:00}/{r[2]:00} 周{r[3]} {r[4]:00}:{r[5]:00}:{r[6]:00}");
            o.SetValue("Success", true);
            return o;
        }

        private async Task<DeviceParameters> ReadSegmentAsync(DeviceParameters? p)
        {
            var o = new DeviceParameters
            {
                Variables = new ObservableCollection<ParameterBase>
                {
                    new NumberParameter("Temp", -100, 300, 0, "设定温度", true, "℃"),
                    new NumberParameter("Time", 0, 65535, 0, "设定时间", true),
                    new NumberParameter("Speed", 0, 65535, 0, "设定转速", true, "RPM"),
                    new NumberParameter("BaseAddress", 0, 65535, 0, "本段基址", true),
                    new BooleanParameter("Success", false, "是否读到"),
                }
            };

            int seg = (int)(p?.GetNullableValue<double>("Segment") ?? 0);
            ushort a = SegmentBase(seg);
            o.SetValue("BaseAddress", (double)a);

            if (IsSimulationMode) { o.SetValue("Success", true); return o; }

            var token = CancellationToken.None;

            // 三个地址不连续(+0 / +9 / +10),而且中间那段文档没说存不存在,
            // 所以老老实实分三次读,不去赌一次读 11 个。
            var t = await ReadHoldingAsync(a, 1, token);
            var tm = await ReadHoldingAsync((ushort)(a + 9), 1, token);
            var sp = await ReadHoldingAsync((ushort)(a + 10), 1, token);

            if (t.Length == 0 || tm.Length == 0 || sp.Length == 0)
            {
                _logger.LogWarning(
                    $"[人工气候箱] 读程序段 {seg} 失败(基址 {a})。如果每一段都读不到," +
                    "多半是「程序段基址」这个参数的进制猜错了 —— 试试把它从 2000 改成 8192(=0x2000)");
                return o;
            }

            o.SetValue("Temp", Scale(t[0], TempDecimals));
            o.SetValue("Time", (double)tm[0]);
            o.SetValue("Speed", Scale(sp[0], SpeedDecimals));
            o.SetValue("Success", true);
            return o;
        }

        #endregion

        #region 写

        private async Task<DeviceParameters> WriteSegmentAsync(DeviceParameters? p)
        {
            if (IsSimulationMode) return Success(true);

            int seg = (int)(p?.GetNullableValue<double>("Segment") ?? 0);
            double temp = p?.GetNullableValue<double>("Temp") ?? 0;
            ushort time = (ushort)(int)(p?.GetNullableValue<double>("Time") ?? 0);
            double speed = p?.GetNullableValue<double>("Speed") ?? 0;

            ushort a = SegmentBase(seg);
            var token = CancellationToken.None;

            // 文档只支持 06,没有 0x10,所以三条分开发。
            // 三条之间不能插别的写命令 —— 实例锁保证了同一台仪器的 IO 串行,
            // 但锁是每条命令各拿一次的,严格说三条中间仍可能挤进别的写。
            // 这里的取舍:程序段是"人手动编辑"的低频操作,和自动轮询撞车的概率
            // 很低,而把三条包进一把大锁会让轮询卡住,得不偿失。
            bool ok = await WriteSingleAsync(a, Unscale(temp, TempDecimals), token)
                   && await WriteSingleAsync((ushort)(a + 9), time, token)
                   && await WriteSingleAsync((ushort)(a + 10), Unscale(speed, SpeedDecimals), token);

            InfoLog($"写程序段 {seg}(基址 {a}):{temp:F1}℃ / 时间 {time} / 转速 {speed} → {ok}");
            return Success(ok);
        }

        private async Task<DeviceParameters> WriteClockAsync(ushort baseReg, DateTime t)
        {
            if (IsSimulationMode) return Success(true);

            // 顺序:年 月 日 星期 时 分 秒。星期用 DayOfWeek(周日=0),
            // 文档没写星期的编码,这是最常见的一种;仪器若按周一=1,现场会看出来。
            ushort[] vals =
            {
                (ushort)t.Year, (ushort)t.Month, (ushort)t.Day,
                (ushort)(int)t.DayOfWeek,
                (ushort)t.Hour, (ushort)t.Minute, (ushort)t.Second,
            };

            var token = CancellationToken.None;
            for (int i = 0; i < vals.Length; i++)
            {
                if (!await WriteSingleAsync((ushort)(baseReg + i), vals[i], token))
                {
                    ErrorLog($"写时间失败于第 {i + 1} 项(0x{baseReg + i:X4}),前面几项已经写进去了 —— " +
                             "仪器里现在是半套时间,请重发一次");
                    return Success(false);
                }
            }

            InfoLog($"写时间 0x{baseReg:X4}~0x{baseReg + 6:X4} = {t:yyyy/MM/dd HH:mm:ss} 周{(int)t.DayOfWeek}");
            return Success(true);
        }

        private async Task<DeviceParameters> SetLedAsync(DeviceParameters? p)
        {
            if (IsSimulationMode) return Success(true);

            int ch = (int)(p?.GetNullableValue<double>("Channel") ?? 1);
            ushort v = (ushort)(int)(p?.GetNullableValue<double>("Value") ?? 0);

            ushort reg = ch switch
            {
                1 => Regs.LedLight1,
                2 => Regs.LedLight2,
                3 => Regs.LedLight3,
                4 => Regs.LedUv,
                _ => Regs.LedLight1,
            };

            bool ok = await WriteSingleAsync(reg, v, CancellationToken.None);
            InfoLog($"设置 LED 通道 {ch}(0x{reg:X4}) = {v} → {ok}" +
                    (ch is 1 or 2 or 3 ? ";按文档,另外两路 light 会被仪器一并清零" : ""));
            return Success(ok);
        }

        #endregion

        #region 参数与标度

        private int TempDecimals => GetIntParam("温度小数位", 1, 0, 3);
        private int SpeedDecimals => GetIntParam("转速小数位", 0, 0, 3);

        private int GetIntParam(string name, int fallback, int min, int max)
        {
            try
            {
                int v = (int)Parameters.GetValue<double>(name);
                return v < min || v > max ? fallback : v;
            }
            catch { return fallback; }
        }

        /// <summary>段号 → 该段的基址。段 0 = 定值模式。</summary>
        private ushort SegmentBase(int segment)
        {
            int b = GetIntParam("程序段基址", 2000, 0, 65535);
            int step = GetIntParam("程序段步长", 20, 1, 4096);
            int addr = b + Math.Clamp(segment, 0, 60) * step;
            // 越界就夹住,而不是让它绕回去写到某个不相干的寄存器上。
            return (ushort)Math.Clamp(addr, 0, 65535);
        }

        /// <summary>寄存器值 → 实际值。s16 负数按补码。</summary>
        private static double Scale(ushort raw, int decimals)
            => (short)raw / Math.Pow(10, decimals);

        /// <summary>实际值 → 寄存器值。</summary>
        private static ushort Unscale(double value, int decimals)
            => unchecked((ushort)(short)Math.Round(value * Math.Pow(10, decimals), MidpointRounding.AwayFromZero));

        private static void FillSimulated(DeviceParameters o)
        {
            double t = Environment.TickCount64 / 1000.0;
            o.SetValue("TEMP_PV", Math.Round(25.0 + 0.6 * Math.Sin(t / 8.0), 1));
            o.SetValue("TEMP_SET", 25.0);
            o.SetValue("SPEED_PV", Math.Round(120 + 4 * Math.Sin(t / 5.0)));
            o.SetValue("SPEED_SET", 120d);
            o.SetValue("RUNNING", true);
            o.SetValue("MOTOR_RUN", true);
            o.SetValue("SUNLIGHT", true);
            o.SetValue("TIMER_START_EN", true);
            o.SetValue("MEASURE_OK", true);
            o.SetValue("STATUS_OK", true);
            o.SetValue("Success", true);
        }

        #endregion

        #region 通信层

        private byte GetSlaveAddress()
        {
            try
            {
                var addr = (int)Parameters.GetValue<double>("Modbus站号");
                return (byte)(addr is >= 1 and <= 254 ? addr : 3);
            }
            catch { return 3; }
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
                Parity = Parameters.GetValue<string>("校验位") ?? "Even",
                DataBits = int.TryParse(Parameters.GetValue<string>("数据位"), out var d) ? d : 8,
                StopBits = Parameters.GetValue<string>("停止位") ?? "One",
                ReadTimeoutMs = IoTimeoutMs,
                WriteTimeoutMs = IoTimeoutMs,
            };
            return CreateTransportAsync(cfg);
        }

        /// <summary>读保持寄存器(0x03)。</summary>
        private Task<ushort[]> ReadHoldingAsync(ushort start, ushort count, CancellationToken token)
            => ReadAsync(start, count, 0x03, token);

        /// <summary>
        /// 读测量值那一块。功能码由"测量值功能码"参数决定(03 或 04)。
        /// 文档在这一点上自相矛盾,见文件抬头第 2 条。
        /// </summary>
        private Task<ushort[]> ReadMeasureAsync(ushort start, ushort count, CancellationToken token)
        {
            byte fc = (Parameters.GetValue<string>("测量值功能码") ?? "03") == "04" ? (byte)0x04 : (byte)0x03;
            return ReadAsync(start, count, fc, token);
        }

        private async Task<ushort[]> ReadAsync(ushort start, ushort count, byte functionCode, CancellationToken token)
        {
            if (count < 1) return Array.Empty<ushort>();

            byte[] cmd = BuildReadCmd(start, count, functionCode);
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
                    int expectLen = 5 + expectBytes;

                    if (len < 5)
                    {
                        _logger.LogWarning($"[人工气候箱] 读 0x{start:X4} 应答太短:{len} 字节");
                        return Array.Empty<ushort>();
                    }
                    if (recv[0] != GetSlaveAddress())
                    {
                        _logger.LogWarning($"[人工气候箱] 站号不匹配:期望 {GetSlaveAddress()},实际 {recv[0]}");
                        return Array.Empty<ushort>();
                    }
                    if ((recv[1] & 0x80) != 0)
                    {
                        // 异常码 02 是这台仪器最常见的一种,而且是"地址不存在"这个
                        // 具体含义(文档明写),值得单独说清楚 —— 上层的分段退化逻辑
                        // 就是靠它触发的。
                        _logger.LogWarning(
                            $"[人工气候箱] 读 0x{start:X4}+{count} 被拒,异常码 0x{recv[2]:X2}" +
                            (recv[2] == 0x02 ? "(非法地址:这一段里有仪器上不存在的寄存器)" : ""));
                        return Array.Empty<ushort>();
                    }
                    if (recv[1] != functionCode || len < expectLen || recv[2] != expectBytes)
                    {
                        _logger.LogWarning(
                            $"[人工气候箱] 应答格式不对:功能码 0x{recv[1]:X2}(期望 0x{functionCode:X2})," +
                            $"字节数 {recv[2]},收到 {len} 字节,期望 {expectLen}");
                        return Array.Empty<ushort>();
                    }
                    if (!Crc16Modbus.VerifyPrefix(recv, expectLen))
                    {
                        _logger.LogWarning($"[人工气候箱] 读 0x{start:X4} 应答 CRC 校验失败,丢弃");
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

        /// <summary>写单个保持寄存器(0x06)。仪器回显原帧。</summary>
        private async Task<bool> WriteSingleAsync(ushort reg, ushort value, CancellationToken token)
        {
            byte[] cmd = BuildWriteCmd(reg, value);
            return await RetryAsync(async () =>
            {
                var sem = GetInstanceLock();
                await sem.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    byte[] recv = new byte[64];
                    using var transport = await GetTransportAsync().ConfigureAwait(false);
                    int len = await transport.SendAndReceiveAsync(cmd, recv, IoTimeoutMs, token).ConfigureAwait(false);

                    if (len < 8) { _logger.LogWarning($"[人工气候箱] 写 0x{reg:X4} 应答太短:{len}"); return false; }
                    if ((recv[1] & 0x80) != 0)
                    {
                        _logger.LogWarning($"[人工气候箱] 写 0x{reg:X4}={value} 被拒,异常码 0x{recv[2]:X2}");
                        return false;
                    }
                    for (int i = 0; i < 6; i++)
                        if (recv[i] != cmd[i])
                        {
                            _logger.LogWarning($"[人工气候箱] 写 0x{reg:X4} 回显不一致,第 {i} 字节 {recv[i]:X2} != {cmd[i]:X2}");
                            return false;
                        }
                    return true;
                }
                finally { sem.Release(); }
            }, $"写 0x{reg:X4}={value}", token);
        }

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
                        _logger.LogWarning($"[人工气候箱] {what} 超时(第 {attempt}/{MaxRetries} 次),{RetryDelayMs}ms 后重试");
                        await Task.Delay(RetryDelayMs, token).ConfigureAwait(false);
                    }
                }
            }
            throw new TimeoutException($"{what} 失败,{MaxRetries} 次尝试后仍超时", last);
        }

        private byte[] BuildReadCmd(ushort reg, ushort count, byte functionCode)
        {
            byte[] f =
            {
                GetSlaveAddress(), functionCode,
                (byte)(reg >> 8), (byte)(reg & 0xFF),
                (byte)(count >> 8), (byte)(count & 0xFF),
            };
            return Crc16Modbus.AppendCrc(f, 0, f.Length);
        }

        private byte[] BuildWriteCmd(ushort reg, ushort value)
        {
            byte[] f =
            {
                GetSlaveAddress(), 0x06,
                (byte)(reg >> 8), (byte)(reg & 0xFF),
                (byte)(value >> 8), (byte)(value & 0xFF),
            };
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
                        $"[人工气候箱命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex,
                        $"[人工气候箱命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        #endregion
    }

    /// <summary>Modbus CRC16 帮助类。多项式 0xA001,初值 0xFFFF,低字节在前。</summary>
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
            result[length] = (byte)(crc & 0xFF);
            result[length + 1] = (byte)(crc >> 8);
            return result;
        }

        /// <summary>校验 buffer 前 frameLen 个字节构成的一帧,后面的残留不参与。</summary>
        public static bool VerifyPrefix(byte[] buffer, int frameLen)
        {
            if (buffer == null || frameLen < 4 || buffer.Length < frameLen) return false;
            ushort crc = Compute(buffer, 0, frameLen - 2);
            return buffer[frameLen - 2] == (byte)(crc & 0xFF)
                && buffer[frameLen - 1] == (byte)(crc >> 8);
        }
    }
}
