// ============================================================================
//  PeristalticPumpLC_ModbusRTU.cs
//  创锐 LC / FC 系列蠕动泵(触摸屏款)驱动 —— Modbus RTU / RS485。
//  依据《LC 蠕动泵通讯规约》20250926-v4.3。
//
//  ── 通信参数 ──────────────────────────────────────────────────────
//      出厂 9600 / N / 8 / 1,从站地址 1(泵菜单里可看)。大端,float 高字在前。
//      只用保持寄存器:0x03 读、0x06 写单个、0x10 写多个。
//
//  ── 先开 485,再写别的 ─────────────────────────────────────────────
//      寄存器 254「485 状态」:写 3 使能、写 0 禁能,**掉电不存**;读回 3 = 使能、1 = 禁能。
//      手册:"一切写指令都是在 485 控制方式使能情况下且非校准状态才有效"。
//      所以驱动在连接时把 254 写成 3,之后每条写命令前再读一次 254,不是 3 就补写。
//      泵断电重启后 485 会回到禁能,这一步省不掉。
//
//  ── 三个"加 0x8000"的寄存器 ──────────────────────────────────────
//      泵头(10)、泵管(11)、运转模式(12)写入时要把最高位置 1,即 0x8000 + 编号;
//      读回来是不带 0x8000 的。手册还写"读取泵头号 / 泵管号时返回值为编号加 1",
//      但同一份手册第 9 页那段 25 个寄存器的整块示例里,寄存器 10 读到 0x0C 却注为
//      LP25(编号 0x0C),对不上"加 1"。这里按手册明文的"加 1"处理,并把原始值
//      一起透出(HEAD_RAW / TUBE_RAW),现场若发现差一位,只改 HeadReadOffset 一处。
//
//  ── 方向的取值,手册自相矛盾 ───────────────────────────────────────
//      第 2 页寄存器表:13 运转方向 "1 顺时针 0 逆时针";
//      第 7 页指令示例:"13 设置泵的运转方向 1 逆时针 0 顺时针"。
//      这里按寄存器表(1 = 顺时针),用 DirectionOneIsClockwise 一处开关;
//      屏幕上把数字和文字一起显示,真机对一眼就知道该不该翻。
//
//  ── 流速只能在传输模式下写 ────────────────────────────────────────
//      手册:"分配模式不能设置流速"。定量 / 定时 / 定时定量三种都属分配模式,
//      驱动在写流速前先看当前模式,不是传输模式就拒绝并说明,不发下去让泵默默吞掉。
// ============================================================================

using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;

namespace PeristalticPumpLC_ModbusRTU
{
    /// <summary>保持寄存器地址表。float 占两个地址,高字在前。</summary>
    internal static class Regs
    {
        public const ushort RunStop = 0;          // 1 启动,0 停止
        public const ushort FillVolume = 1;       // float ml,掉电存储
        public const ushort FillTime = 5;         // float s,掉电存储
        public const ushort Flow = 8;             // float ml/min,掉电不存,仅传输模式可写
        public const ushort Head = 10;            // 写 0x8000+编号;读 编号+1(见文件抬头)
        public const ushort Tube = 11;            // 同上
        public const ushort Mode = 12;            // 写 0x8000+模式;读 0~3
        public const ushort Direction = 13;       // 见文件抬头
        public const ushort FullSpeed = 14;       // 1 全速,0 取消
        public const ushort BackSpeed = 15;       // 回吸速度 10~300 rpm,掉电存储
        public const ushort BackAngle = 16;       // 回吸角度 0~3600°,掉电存储
        public const ushort ExtInput1 = 18;       // 只读:高字节 使能,低字节 启停类型
        public const ushort ExtInput2 = 19;       // 只读:高字节 方向类型,低字节 速度类型
        public const ushort CalVolume = 20;       // float 校准液量 ml,掉电不存
        public const ushort CalActual = 22;       // float 实测液量 ml,写入即触发校准
        public const ushort KValue = 252;         // float 当前流速系数 K,ml/(min·rpm)
        public const ushort Rs485 = 254;          // 写 3 使能 / 0 禁能;读 3 使能 / 1 禁能

        /// <summary>第一块:0~23 一次读全(手册示例一次读了 25 个)。</summary>
        public const ushort BlockAStart = 0;
        public const ushort BlockACount = 24;

        /// <summary>第二块:252~254。</summary>
        public const ushort BlockBStart = 252;
        public const ushort BlockBCount = 3;

        public const ushort WriteFlag = 0x8000;
        public const ushort Rs485On = 3;
        public const ushort Rs485Off = 0;

        /// <summary>读泵头 / 泵管时的偏移,手册明文"返回值为编号加 1"。见文件抬头。</summary>
        public const int HeadReadOffset = 1;

        /// <summary>方向寄存器 1 是否表示顺时针。按寄存器表为 true;示例段写反了。见文件抬头。</summary>
        public const bool DirectionOneIsClockwise = true;

        public static readonly string[] HeadNames =
        {
            "YZ1515x", "YZ15M", "313D", "TX315", "YZ2515x", "YZ25M", "BZ25", "KZ25",
            "DG(6)", "LP15", "BZ15", "YZ25L", "LP25", "YZ35", "KZ50", "DG(10)",
        };

        public static readonly string[] ModeNames = { "传输模式", "定量模式", "定时模式", "定时定量模式" };

        public static string HeadName(int code) => code >= 0 && code < HeadNames.Length ? HeadNames[code] : $"未知泵头({code})";
        public static string ModeName(int code) => code >= 0 && code < ModeNames.Length ? ModeNames[code] : $"未知模式({code})";

        public static string DirectionText(int v)
        {
            if (v != 0 && v != 1) return $"未知({v})";
            bool cw = (v == 1) == DirectionOneIsClockwise;
            return cw ? "顺时针" : "逆时针";
        }

        public static string ExtStartStopText(int v) => v switch
        {
            0 => "无外控启停", 1 => "上升沿", 2 => "下降沿", 3 => "电平", _ => $"未知({v})",
        };
        public static string ExtDirectionText(int v) => v switch
        {
            0 => "无外控方向", 1 => "高电平", 2 => "低电平", _ => $"未知({v})",
        };
        public static string ExtSpeedText(int v) => v switch
        {
            0 => "无外控模拟量", 1 => "0-5V", 2 => "0-10V", 3 => "4-20mA", 4 => "0-10kHz", _ => $"未知({v})",
        };
    }

    [Export(typeof(IDevice))]
    public class PeristalticPumpLC_ModbusRTU : Device, IDeviceScreen
    {
        /// <summary>源码版本戳。**改动本文件时手动更新**,连接时第一行日志打出来。</summary>
        public const string SourceStamp = "2026-09-11-a";

        private const string Tag = "[LC 蠕动泵]";

        private readonly ILogService _logger;

        private const int MaxRetries = 3;
        private const int RetryDelayMs = 100;
        private const int IoTimeoutMs = 2000;

        /// <summary>写 float 后回读的允许偏差(相对):泵内部可能按泵管系数换算过再存。</summary>
        private const double FloatTolerance = 0.01;

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _instanceLocks = new();

        /// <summary>最近一次读到的运转模式,写流速前用它判断是否在传输模式。-1 = 没读过。</summary>
        private int _lastMode = -1;

        // 模拟模式下的状态
        private bool _simRunning, _simFull;
        private int _simMode, _simDirection = 1, _simHead = 12, _simTube = 0;
        private double _simFlow = 6.0, _simFillVolume = 3.0, _simFillTime = 20.0, _simBackSpeed = 60, _simBackAngle = 90;
        private double _simCal = 7.0, _simActual = 7.0, _simK = 1.2;
        private bool _simRs485 = true;

        public string ScreenKey => "PeristalticPumpLCScreen";

        public PeristalticPumpLC_ModbusRTU()
        {
            _logger = LogManager.GetLogger<PeristalticPumpLC_ModbusRTU>();

            DeviceId = "LCP0001";
            Name = "LC 蠕动泵";
            Manufacturer = "创锐 LC/FC 系列蠕动泵(触摸屏款,Modbus RTU / RS485)";
            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/PeristalticPumpLC.png";
            Category = DeviceCategories.Pumps;
            ComId = "LCP0001";
            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();

            AllowedRegions = new List<RegionType>
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            Parameters.Variables.Add(new StringParameter("DeviceId", "LCP0001", "设备实例编号")
            {
                Options = new ObservableCollection<string> { "LCP0001", "LCP0002", "LCP0003", "LCP0004" }
            });

            Parameters.Variables.Add(new StringParameter("串口号", SerialPortOptions.DefaultPort(), "RS485 转换器对应的串口")
            {
                Options = SerialPortOptions.CreateOptions()
            });

            Parameters.Variables.Add(new StringParameter("波特率", "9600", "出厂 9600")
            {
                Options = new ObservableCollection<string> { "2400", "4800", "9600", "19200", "38400", "57600", "115200" }
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

            Parameters.Variables.Add(new NumberParameter("Modbus站号", 1, 254, 1, "泵的 485 地址,出厂 1,可在泵菜单查看"));

            InitializeCommands();
        }

        public override bool SupportsZLanGateway => true;

        #region 可监控参数

        public override IReadOnlyList<CollectableParameter> CollectableParameters => new[]
        {
            new CollectableParameter("FLOW",        "流速",     "mL/min", "获取所有数据", "FLOW", isKey: true),
            new CollectableParameter("FILL_VOLUME", "灌装量",   "mL",     "获取所有数据", "FILL_VOLUME"),
            new CollectableParameter("FILL_TIME",   "灌装时间", "s",      "获取所有数据", "FILL_TIME"),
            new CollectableParameter("K_VALUE",     "流速系数", "",       "获取所有数据", "K_VALUE"),
        };

        public override int AutoCollectIntervalMs => 1000;

        #endregion

        #region 连接

        protected override async Task<bool> OnConnectAsync() => await ProbeAsync("直连");

        protected override Task<bool> OnConnectViaGatewayAsync() => ProbeAsync("ZLAN 网关");

        /// <summary>连接:读 485 状态确认泵在线,然后把 485 使能写上(掉电不存,每次连接都得写)。</summary>
        private async Task<bool> ProbeAsync(string via)
        {
            try
            {
                _logger.LogInformation(
                    $"{Tag} {SourceStamp} 经{via}连接,站号 {GetSlaveAddress()}," +
                    $"串口 {Parameters.GetValue<string>("串口号")} @ {Parameters.GetValue<string>("波特率")}/8/N/1");

                var token = CancellationToken.None;
                ushort[] r = await ReadHoldingAsync(Regs.Rs485, 1, token);
                if (r.Length < 1)
                {
                    ErrorLog("连接失败:读 485 状态没有得到有效应答");
                    return false;
                }

                bool enabled = await EnsureRs485Async(token);
                InfoLog($"连接成功:485 状态原为 {(r[0] == Regs.Rs485On ? "使能" : "禁能")},现在 {(enabled ? "已使能" : "使能失败,写命令会被泵忽略")}");
                return true;
            }
            catch (TimeoutException)
            {
                ErrorLog("连接超时:串口通了但泵没应答 —— 多半是站号不对、波特率不对,或 485 的 A/B 反接");
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
                var r = await ReadHoldingAsync(Regs.RunStop, 1, CancellationToken.None);
                return r.Length == 1;
            }
            catch { return false; }
        }

        /// <summary>485 使能兜底:读 254,不是 3 就写 3 再回读。返回最终是否使能。</summary>
        private async Task<bool> EnsureRs485Async(CancellationToken token)
        {
            var r = await ReadHoldingAsync(Regs.Rs485, 1, token);
            if (r.Length == 1 && r[0] == Regs.Rs485On) return true;

            if (!await WriteSingleAsync(Regs.Rs485, Regs.Rs485On, token)) return false;

            r = await ReadHoldingAsync(Regs.Rs485, 1, token);
            bool ok = r.Length == 1 && r[0] == Regs.Rs485On;
            if (!ok) _logger.LogWarning($"{Tag} 写 485 使能后回读为 {(r.Length == 1 ? r[0].ToString() : "无")},不是 3");
            else _logger.LogInformation($"{Tag} 已写入 485 使能");
            return ok;
        }

        #endregion

        #region 命令

        private void InitializeCommands()
        {
            Commands.Add(new DeviceCommand
            {
                Name = "获取所有数据",
                HelpText = "读寄存器 0~23 与 252~254:启停、灌装量/时间、流速、泵头/泵管、模式、方向、全速、回吸、外控输入、校准量、K 值、485 状态。屏幕轮询用的就是它。",
                OutputParameters = new DeviceParameters { Variables = BuildAllOutputs() },
                AsyncAction = AsyncWrapAction("获取所有数据", _ => ReadAllAsync())
            });

            AddWordCommandFixed("启动", "寄存器 0 写 1。按当前模式运行:传输模式一直转,分配模式按灌装量/时间跑一次", Regs.RunStop, 1, () => _simRunning = true);
            AddWordCommandFixed("停止", "寄存器 0 写 0", Regs.RunStop, 0, () => _simRunning = false);

            Commands.Add(new DeviceCommand
            {
                Name = "设置流速",
                HelpText = "流速 mL/min(寄存器 8~9),掉电不存。只在传输模式下可写,分配模式会被拒绝",
                InputParameters = OneInput(new NumberParameter("TargetValue", 0.001, 9999000, 6, "流速", false, "mL/min") { DecimalPlaces = 3 }),
                OutputParameters = VerifiedOutputs("mL/min"),
                AsyncAction = AsyncWrapAction("设置流速", p => SetFlowAsync(p, startAfter: false))
            });

            Commands.Add(new DeviceCommand
            {
                Name = "按流速启动",
                HelpText = "写流速 → 回读比对 → 启动。任一步不过就不启动。只在传输模式下有效",
                InputParameters = OneInput(new NumberParameter("TargetValue", 0.001, 9999000, 6, "流速", false, "mL/min") { DecimalPlaces = 3 }),
                OutputParameters = VerifiedOutputs("mL/min"),
                AsyncAction = AsyncWrapAction("按流速启动", p => SetFlowAsync(p, startAfter: true))
            });

            AddFloatCommand("设置灌装量", "灌装量 mL,0.001mL~9999L(寄存器 1~2),掉电存储。灌装量 = 流速 × 灌装时间",
                Regs.FillVolume, 0.001, 9999000, "mL", v => _simFillVolume = v);
            AddFloatCommand("设置灌装时间", "灌装时间 s,0.1s~9999min(寄存器 5~6),掉电存储",
                Regs.FillTime, 0.1, 599940, "s", v => _simFillTime = v);

            Commands.Add(new DeviceCommand
            {
                Name = "设置运转模式",
                HelpText = "0=传输 1=定量 2=定时 3=定时定量(寄存器 12,写入时加 0x8000),掉电不存",
                InputParameters = OneInput(new NumberParameter("TargetValue", 0, 3, 0, "模式码", false)),
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("设置运转模式", async p =>
                {
                    int m = (int)(p?.GetNullableValue<double>("TargetValue") ?? 0);
                    if (m < 0 || m > 3) return Success(false);
                    if (IsSimulationMode) { _simMode = m; return Success(true); }
                    bool ok = await WriteFlaggedVerifiedAsync(Regs.Mode, (ushort)m, readOffset: 0, "运转模式", CancellationToken.None);
                    if (ok) _lastMode = m;
                    return Success(ok);
                })
            });

            Commands.Add(new DeviceCommand
            {
                Name = "设置方向",
                HelpText = "寄存器 13。按手册寄存器表 1=顺时针 0=逆时针(示例段写的相反,以真机为准)",
                InputParameters = OneInput(new NumberParameter("TargetValue", 0, 1, 1, "0 / 1", false)),
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("设置方向", async p =>
                {
                    ushort v = (ushort)(int)(p?.GetNullableValue<double>("TargetValue") ?? 1);
                    if (IsSimulationMode) { _simDirection = v; return Success(true); }
                    return Success(await WriteSingleVerifiedAsync(Regs.Direction, v, "方向", CancellationToken.None));
                })
            });

            Commands.Add(new DeviceCommand
            {
                Name = "设置全速",
                HelpText = "0=取消全速,1=全速(寄存器 14)。常用于灌管/排空",
                InputParameters = OneInput(new NumberParameter("TargetValue", 0, 1, 0, "0 取消 / 1 全速", false)),
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("设置全速", async p =>
                {
                    ushort v = (ushort)(int)(p?.GetNullableValue<double>("TargetValue") ?? 0);
                    if (IsSimulationMode) { _simFull = v != 0; return Success(true); }
                    return Success(await WriteSingleVerifiedAsync(Regs.FullSpeed, v, "全速", CancellationToken.None));
                })
            });

            AddWordCommand("设置回吸速度", "回吸速度 10~300 rpm(寄存器 15),掉电存储", Regs.BackSpeed, 10, 300, v => _simBackSpeed = v);
            AddWordCommand("设置回吸角度", "回吸角度 0~3600°(寄存器 16),掉电存储", Regs.BackAngle, 0, 3600, v => _simBackAngle = v);

            Commands.Add(new DeviceCommand
            {
                Name = "设置泵头",
                HelpText = "泵头编号 0~15:0 YZ1515x 1 YZ15M 2 313D 3 TX315 4 YZ2515x 5 YZ25M 6 BZ25 7 KZ25 8 DG(6) 9 LP15 10 BZ15 11 YZ25L 12 LP25 13 YZ35 14 KZ50 15 DG(10)。写入时加 0x8000,掉电存储",
                InputParameters = OneInput(new NumberParameter("TargetValue", 0, 15, 12, "泵头编号", false)),
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("设置泵头", async p =>
                {
                    int v = (int)(p?.GetNullableValue<double>("TargetValue") ?? 0);
                    if (v < 0 || v > 15) return Success(false);
                    if (IsSimulationMode) { _simHead = v; return Success(true); }
                    return Success(await WriteFlaggedVerifiedAsync(Regs.Head, (ushort)v, Regs.HeadReadOffset, "泵头", CancellationToken.None));
                })
            });

            Commands.Add(new DeviceCommand
            {
                Name = "设置泵管",
                HelpText = "泵管编号(对应所选泵头的泵管列表,从 0 起,见手册图一)。写入时加 0x8000,掉电存储",
                InputParameters = OneInput(new NumberParameter("TargetValue", 0, 99, 0, "泵管编号", false)),
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("设置泵管", async p =>
                {
                    int v = (int)(p?.GetNullableValue<double>("TargetValue") ?? 0);
                    if (v < 0 || v > 99) return Success(false);
                    if (IsSimulationMode) { _simTube = v; return Success(true); }
                    return Success(await WriteFlaggedVerifiedAsync(Regs.Tube, (ushort)v, Regs.HeadReadOffset, "泵管", CancellationToken.None));
                })
            });

            AddFloatCommand("设置校准液量", "校准用的目标液量 mL(寄存器 20~21),掉电不存。流程:写校准液量 → 让泵跑一次 → 量出实际液量 → 写实测液量",
                Regs.CalVolume, 0.001, 9999000, "mL", v => _simCal = v);

            Commands.Add(new DeviceCommand
            {
                Name = "设置实测液量",
                HelpText = "校准第二步:写入实际量得的液量 mL(寄存器 22~23),泵据此更新流速系数 K = 实测/校准 × 原 K",
                InputParameters = OneInput(new NumberParameter("TargetValue", 0.001, 9999000, 7, "实测液量", false, "mL") { DecimalPlaces = 3 }),
                OutputParameters = VerifiedOutputs("mL"),
                AsyncAction = AsyncWrapAction("设置实测液量", async p =>
                {
                    var o = VerifiedOutputs("mL");
                    double v = p?.GetNullableValue<double>("TargetValue") ?? double.NaN;
                    if (double.IsNaN(v) || v <= 0) { o.SetValue("Message", "实测液量必须大于 0"); return o; }
                    if (IsSimulationMode)
                    {
                        _simK = _simK * v / Math.Max(_simCal, 1e-6); _simActual = v;
                        o.SetValue("Success", true); o.SetValue("Verified", true); o.SetValue("ReadBack", v);
                        o.SetValue("Message", $"模拟模式:K 更新为 {_simK:F3}");
                        return o;
                    }
                    var token = CancellationToken.None;
                    if (!await EnsureRs485Async(token)) { o.SetValue("Message", "485 未使能,未写入"); return o; }
                    var (ok, verified, back) = await WriteFloatVerifiedAsync(Regs.CalActual, (float)v, "实测液量", token);
                    // 校准会改 K,把新 K 读出来写进日志,方便对账
                    var k = await ReadHoldingAsync(Regs.KValue, 2, token);
                    string kText = k.Length == 2 ? $"{F(k, 0):F3}" : "读不到";
                    o.SetValue("Success", ok && verified); o.SetValue("Verified", verified); o.SetValue("ReadBack", Math.Round(back, 3));
                    o.SetValue("Message", ok && verified ? $"实测液量 {v:F3} mL 已写入,校准后 K = {kText}" : NotTakenMessage("实测液量", v, back, ok));
                    InfoLog($"校准:实测液量 {v:F3} mL → {ok && verified},K = {kText}");
                    return o;
                })
            });

            AddWordCommandFixed("启用485控制", "寄存器 254 写 3。连接时驱动已自动写过;泵断电重启后需要再写", Regs.Rs485, Regs.Rs485On, () => _simRs485 = true);
            AddWordCommandFixed("禁用485控制", "寄存器 254 写 0,之后泵只听面板与外控,本软件的写命令全部无效", Regs.Rs485, Regs.Rs485Off, () => _simRs485 = false);
        }

        private static DeviceParameters OneInput(ParameterBase p) => new()
        {
            Variables = new ObservableCollection<ParameterBase> { p }
        };

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

        private static DeviceParameters VerifiedOutputs(string unit) => new()
        {
            Variables = new ObservableCollection<ParameterBase>
            {
                new BooleanParameter("Success", false, "是否成功", true),
                new BooleanParameter("Verified", false, "回读是否与设定一致", true),
                new NumberParameter("ReadBack", -1e9, 1e9, 0, "回读值", true, unit),
                new StringParameter("Message", "", "说明"),
            }
        };

        /// <summary>固定值的单寄存器命令(启动/停止/485 开关)。写前先保证 485 使能(写 254 自己除外)。</summary>
        private void AddWordCommandFixed(string name, string help, ushort reg, ushort value, Action simApply)
        {
            Commands.Add(new DeviceCommand
            {
                Name = name,
                HelpText = help,
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction(name, async _ =>
                {
                    if (IsSimulationMode) { simApply(); return Success(true); }
                    var token = CancellationToken.None;
                    if (reg != Regs.Rs485 && !await EnsureRs485Async(token)) return Success(false);
                    bool ok = await WriteSingleAsync(reg, value, token);
                    // 485 禁能写完就读不到"3"了,回读没意义;其它都回读确认
                    if (ok && reg != Regs.Rs485)
                    {
                        var r = await ReadHoldingAsync(reg, 1, token);
                        ok = r.Length == 1 && r[0] == value;
                        if (!ok) _logger.LogWarning($"{Tag} {name} 写入后回读不一致,泵可能未使能 485 或处于校准状态");
                    }
                    return Success(ok);
                })
            });
        }

        /// <summary>整数型单寄存器设定命令,带范围与回读。</summary>
        private void AddWordCommand(string name, string help, ushort reg, int min, int max, Action<double> simApply)
        {
            Commands.Add(new DeviceCommand
            {
                Name = name,
                HelpText = help,
                InputParameters = OneInput(new NumberParameter("TargetValue", min, max, min, help, false)),
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction(name, async p =>
                {
                    int v = (int)Math.Round(p?.GetNullableValue<double>("TargetValue") ?? min);
                    if (v < min || v > max) return Success(false);
                    if (IsSimulationMode) { simApply(v); return Success(true); }
                    return Success(await WriteSingleVerifiedAsync(reg, (ushort)v, name, CancellationToken.None));
                })
            });
        }

        /// <summary>float 类设定命令:写两个寄存器并回读比对。</summary>
        private void AddFloatCommand(string name, string help, ushort start, double min, double max, string unit, Action<double> simApply)
        {
            Commands.Add(new DeviceCommand
            {
                Name = name,
                HelpText = help,
                InputParameters = OneInput(new NumberParameter("TargetValue", min, max, min, help, false, unit) { DecimalPlaces = 3 }),
                OutputParameters = VerifiedOutputs(unit),
                AsyncAction = AsyncWrapAction(name, async p =>
                {
                    var o = VerifiedOutputs(unit);
                    double v = p?.GetNullableValue<double>("TargetValue") ?? double.NaN;
                    if (double.IsNaN(v) || v < min || v > max)
                    {
                        o.SetValue("Message", $"{name}:{v} 超出 {min}~{max}");
                        return o;
                    }
                    if (IsSimulationMode)
                    {
                        simApply(v);
                        o.SetValue("Success", true); o.SetValue("Verified", true); o.SetValue("ReadBack", v);
                        return o;
                    }
                    var token = CancellationToken.None;
                    if (!await EnsureRs485Async(token)) { o.SetValue("Message", "485 未使能,未写入"); return o; }
                    var (ok, verified, back) = await WriteFloatVerifiedAsync(start, (float)v, name, token);
                    o.SetValue("Success", ok && verified);
                    o.SetValue("Verified", verified);
                    o.SetValue("ReadBack", Math.Round(back, 3));
                    o.SetValue("Message", ok && verified ? $"{name} = {v:G6} {unit} 已写入" : NotTakenMessage(name, v, back, ok));
                    return o;
                })
            });
        }

        private async Task<DeviceParameters> SetFlowAsync(DeviceParameters? p, bool startAfter)
        {
            var o = VerifiedOutputs("mL/min");
            double v = p?.GetNullableValue<double>("TargetValue") ?? double.NaN;
            if (double.IsNaN(v) || v <= 0)
            {
                o.SetValue("Message", "流速必须大于 0");
                return o;
            }

            if (IsSimulationMode)
            {
                if (_simMode != 0) { o.SetValue("Message", $"当前是{Regs.ModeName(_simMode)},分配模式不能设置流速"); return o; }
                _simFlow = v;
                if (startAfter) _simRunning = true;
                o.SetValue("Success", true); o.SetValue("Verified", true); o.SetValue("ReadBack", v);
                o.SetValue("Message", startAfter ? $"模拟模式:已按 {v:G6} mL/min 启动" : $"模拟模式:流速 = {v:G6}");
                return o;
            }

            var token = CancellationToken.None;

            // 手册:分配模式不能设置流速。先看一眼当前模式,别把值发下去让泵默默吞掉。
            var m = await ReadHoldingAsync(Regs.Mode, 1, token);
            if (m.Length == 1) _lastMode = m[0];
            if (_lastMode != 0)
            {
                o.SetValue("Message", $"当前是{Regs.ModeName(_lastMode)},分配模式不能设置流速;请先把运转模式切到传输模式");
                return o;
            }

            if (!await EnsureRs485Async(token)) { o.SetValue("Message", "485 未使能,未写入"); return o; }

            var (ok, verified, back) = await WriteFloatVerifiedAsync(Regs.Flow, (float)v, "流速", token);
            o.SetValue("Verified", verified);
            o.SetValue("ReadBack", Math.Round(back, 3));

            if (!(ok && verified))
            {
                o.SetValue("Message", NotTakenMessage("流速", v, back, ok) + (startAfter ? " 已放弃启动。" : ""));
                return o;
            }

            if (!startAfter)
            {
                o.SetValue("Success", true);
                o.SetValue("Message", $"流速 = {v:G6} mL/min 已写入(回读 {back:G6})");
                return o;
            }

            bool started = await WriteSingleAsync(Regs.RunStop, 1, token);
            o.SetValue("Success", started);
            o.SetValue("Message", started ? $"已按 {v:G6} mL/min 启动(回读 {back:G6},比对通过)" : "流速已写入,但启动写入失败");
            InfoLog($"按流速启动 {v:G6} mL/min → {started}");
            return o;
        }

        private static string NotTakenMessage(string what, double set, double back, bool echoOk)
        {
            if (!echoOk) return $"{what} 写入无应答或被拒";
            return $"{what} 回读比对不通过:设定 {set:G6},回读 {back:G6}。泵可能未使能 485、正在校准,或值超出泵允许范围";
        }

        #endregion

        #region 读

        private static ObservableCollection<ParameterBase> BuildAllOutputs() => new()
        {
            new BooleanParameter("RUNNING", false, "是否运行(寄存器 0)", true),
            new NumberParameter("FILL_VOLUME", 0, 1e7, 0, "灌装量", true, "mL"),
            new NumberParameter("FILL_TIME",   0, 1e7, 0, "灌装时间", true, "s"),
            new NumberParameter("FLOW",        0, 1e7, 0, "流速", true, "mL/min"),
            new NumberParameter("HEAD_CODE",   -1, 255, 12, "泵头编号(按手册'读值-1'换算)", true),
            new NumberParameter("HEAD_RAW",    0, 65535, 0, "泵头寄存器原始值", true),
            new StringParameter("HEAD_NAME", "", "泵头型号"),
            new NumberParameter("TUBE_CODE",   -1, 255, 0, "泵管编号(按手册'读值-1'换算)", true),
            new NumberParameter("TUBE_RAW",    0, 65535, 0, "泵管寄存器原始值", true),
            new NumberParameter("MODE",        0, 3, 0, "运转模式 0传输 1定量 2定时 3定时定量", true),
            new StringParameter("MODE_TEXT", "", "运转模式文字"),
            new NumberParameter("DIRECTION",   0, 1, 1, "方向寄存器值", true),
            new StringParameter("DIRECTION_TEXT", "", "方向文字(按寄存器表 1=顺时针)"),
            new BooleanParameter("FULL_SPEED", false, "是否全速", true),
            new NumberParameter("BACK_SPEED",  0, 300, 0, "回吸速度", true, "rpm"),
            new NumberParameter("BACK_ANGLE",  0, 3600, 0, "回吸角度", true, "°"),
            new BooleanParameter("EXT_ENABLE", false, "外控是否使能", true),
            new StringParameter("EXT_STARTSTOP", "", "外控启停类型"),
            new StringParameter("EXT_DIRECTION", "", "外控方向类型"),
            new StringParameter("EXT_SPEED", "", "外控模拟量类型"),
            new NumberParameter("CAL_VOLUME",  0, 1e7, 0, "校准液量", true, "mL"),
            new NumberParameter("CAL_ACTUAL",  0, 1e7, 0, "实测液量", true, "mL"),
            new NumberParameter("K_VALUE",     0, 1e6, 0, "流速系数 K", true, "mL/(min·rpm)"),
            new BooleanParameter("RS485_ENABLED", false, "485 控制是否使能", true),
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

            ushort[] a = await ReadHoldingAsync(Regs.BlockAStart, Regs.BlockACount, token);
            if (a.Length < Regs.BlockACount)
            {
                o.SetValue("Success", false);
                return o;
            }

            ushort[] b;
            try { b = await ReadHoldingAsync(Regs.BlockBStart, Regs.BlockBCount, token); }
            catch (Exception ex)
            {
                _logger.LogWarning($"{Tag} 读 252~254 失败,K 值与 485 状态本轮不更新:{ex.Message}");
                b = Array.Empty<ushort>();
            }

            ushort A(ushort addr) => a[addr - Regs.BlockAStart];
            float AF(ushort addr) => F(a, addr - Regs.BlockAStart);

            o.SetValue("RUNNING", A(Regs.RunStop) != 0);
            o.SetValue("FILL_VOLUME", Math.Round(AF(Regs.FillVolume), 3));
            o.SetValue("FILL_TIME", Math.Round(AF(Regs.FillTime), 3));
            o.SetValue("FLOW", Math.Round(AF(Regs.Flow), 3));

            int headRaw = A(Regs.Head), tubeRaw = A(Regs.Tube);
            int headCode = (headRaw & 0x7FFF) - Regs.HeadReadOffset;
            int tubeCode = (tubeRaw & 0x7FFF) - Regs.HeadReadOffset;
            o.SetValue("HEAD_RAW", (double)headRaw);
            o.SetValue("HEAD_CODE", (double)headCode);
            o.SetValue("HEAD_NAME", Regs.HeadName(headCode));
            o.SetValue("TUBE_RAW", (double)tubeRaw);
            o.SetValue("TUBE_CODE", (double)tubeCode);

            int mode = A(Regs.Mode) & 0x7FFF;
            _lastMode = mode;
            o.SetValue("MODE", (double)mode);
            o.SetValue("MODE_TEXT", Regs.ModeName(mode));

            int dir = A(Regs.Direction);
            o.SetValue("DIRECTION", (double)dir);
            o.SetValue("DIRECTION_TEXT", Regs.DirectionText(dir));
            o.SetValue("FULL_SPEED", A(Regs.FullSpeed) != 0);
            o.SetValue("BACK_SPEED", (double)A(Regs.BackSpeed));
            o.SetValue("BACK_ANGLE", (double)A(Regs.BackAngle));

            ushort ext1 = A(Regs.ExtInput1), ext2 = A(Regs.ExtInput2);
            o.SetValue("EXT_ENABLE", (ext1 >> 8 & 0xFF) != 0);
            o.SetValue("EXT_STARTSTOP", Regs.ExtStartStopText(ext1 & 0xFF));
            o.SetValue("EXT_DIRECTION", Regs.ExtDirectionText(ext2 >> 8 & 0xFF));
            o.SetValue("EXT_SPEED", Regs.ExtSpeedText(ext2 & 0xFF));

            o.SetValue("CAL_VOLUME", Math.Round(AF(Regs.CalVolume), 3));
            o.SetValue("CAL_ACTUAL", Math.Round(AF(Regs.CalActual), 3));

            if (b.Length >= Regs.BlockBCount)
            {
                o.SetValue("K_VALUE", Math.Round(F(b, 0), 4));
                o.SetValue("RS485_ENABLED", b[2] == Regs.Rs485On);
            }

            o.SetValue("Success", true);
            return o;
        }

        private void FillSimulated(DeviceParameters o)
        {
            o.SetValue("RUNNING", _simRunning);
            o.SetValue("FILL_VOLUME", _simFillVolume);
            o.SetValue("FILL_TIME", _simFillTime);
            o.SetValue("FLOW", _simFlow);
            o.SetValue("HEAD_RAW", (double)(_simHead + Regs.HeadReadOffset));
            o.SetValue("HEAD_CODE", (double)_simHead);
            o.SetValue("HEAD_NAME", Regs.HeadName(_simHead));
            o.SetValue("TUBE_RAW", (double)(_simTube + Regs.HeadReadOffset));
            o.SetValue("TUBE_CODE", (double)_simTube);
            o.SetValue("MODE", (double)_simMode);
            o.SetValue("MODE_TEXT", Regs.ModeName(_simMode));
            o.SetValue("DIRECTION", (double)_simDirection);
            o.SetValue("DIRECTION_TEXT", Regs.DirectionText(_simDirection));
            o.SetValue("FULL_SPEED", _simFull);
            o.SetValue("BACK_SPEED", _simBackSpeed);
            o.SetValue("BACK_ANGLE", _simBackAngle);
            o.SetValue("EXT_ENABLE", false);
            o.SetValue("EXT_STARTSTOP", Regs.ExtStartStopText(0));
            o.SetValue("EXT_DIRECTION", Regs.ExtDirectionText(0));
            o.SetValue("EXT_SPEED", Regs.ExtSpeedText(0));
            o.SetValue("CAL_VOLUME", _simCal);
            o.SetValue("CAL_ACTUAL", _simActual);
            o.SetValue("K_VALUE", Math.Round(_simK, 4));
            o.SetValue("RS485_ENABLED", _simRs485);
            o.SetValue("Success", true);
        }

        #endregion

        #region 写(带回读)

        /// <summary>写单个寄存器并回读比对。写前保证 485 使能。</summary>
        private async Task<bool> WriteSingleVerifiedAsync(ushort reg, ushort value, string what, CancellationToken token)
        {
            if (!await EnsureRs485Async(token)) return false;
            if (!await WriteSingleAsync(reg, value, token)) return false;
            var r = await ReadHoldingAsync(reg, 1, token);
            if (r.Length == 1 && r[0] == value) return true;
            _logger.LogWarning($"{Tag} {what} 写 {value} 后回读为 {(r.Length == 1 ? r[0].ToString() : "无")},不一致。泵可能正在校准,或值超出允许范围");
            return false;
        }

        /// <summary>
        /// 写"加 0x8000"类寄存器(泵头 / 泵管 / 模式):发 0x8000+code,回读期望 code+readOffset。
        /// </summary>
        private async Task<bool> WriteFlaggedVerifiedAsync(ushort reg, ushort code, int readOffset, string what, CancellationToken token)
        {
            if (!await EnsureRs485Async(token)) return false;
            if (!await WriteSingleAsync(reg, (ushort)(Regs.WriteFlag | code), token)) return false;

            var r = await ReadHoldingAsync(reg, 1, token);
            if (r.Length != 1) return false;

            int got = r[0] & 0x7FFF;
            if (got == code + readOffset) return true;

            // 手册里"读值 = 编号 + 1"这条本身就存疑(见文件抬头),回读差 1 时只告警不判失败,
            // 差得更多才是真的没写进去。
            if (readOffset != 0 && got == code)
            {
                _logger.LogWarning($"{Tag} {what} 写 {code} 后回读 {got}:与手册'读值加 1'的说法差 1,按已写入处理;若现场确认无偏移,请把 HeadReadOffset 改为 0");
                return true;
            }

            _logger.LogWarning($"{Tag} {what} 写 {code} 后回读 {got},不一致");
            return false;
        }

        /// <summary>写 float(两个寄存器,0x10)并回读。返回 (回显是否成功, 回读是否一致, 回读值)。</summary>
        private async Task<(bool ok, bool verified, double back)> WriteFloatVerifiedAsync(ushort start, float value, string what, CancellationToken token)
        {
            if (!await WriteMultipleAsync(start, Split(value), token)) return (false, false, double.NaN);

            var r = await ReadHoldingAsync(start, 2, token);
            if (r.Length < 2) return (true, false, double.NaN);

            double back = F(r, 0);
            double tol = Math.Max(1e-4, Math.Abs(value) * FloatTolerance);
            bool verified = Math.Abs(back - value) <= tol;
            if (!verified)
                _logger.LogWarning($"{Tag} {what} 写 {value:G6} 后回读 {back:G6},不一致");
            return (true, verified, back);
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

        /// <summary>一次请求-应答,校验站号 / 异常位 / 帧长 / CRC,返回整帧;格式错不重试,只有超时才重试。</summary>
        private async Task<byte[]?> ExchangeAsync(byte[] cmd, string what, CancellationToken token)
        {
            return await RetryAsync(async () =>
            {
                var sem = GetInstanceLock();
                await sem.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    byte[] recv = new byte[512];
                    using var transport = await GetTransportAsync().ConfigureAwait(false);
                    int len = await transport.SendAndReceiveAsync(cmd, recv, IoTimeoutMs, token).ConfigureAwait(false);

                    if (len < 5) { _logger.LogWarning($"{Tag} {what} 应答太短:{len} 字节"); return null; }
                    if (recv[0] != GetSlaveAddress())
                    {
                        _logger.LogWarning($"{Tag} {what} 站号不匹配:期望 {GetSlaveAddress()},实际 {recv[0]}");
                        return null;
                    }
                    if ((recv[1] & 0x80) != 0)
                    {
                        _logger.LogWarning($"{Tag} {what} 被拒,Modbus 异常码 0x{recv[2]:X2}" +
                                           (recv[2] == 0x02 ? "(非法地址)" : recv[2] == 0x03 ? "(非法数据值)" : ""));
                        return null;
                    }

                    int frameLen = recv[1] switch
                    {
                        0x03 => 5 + recv[2],
                        0x06 or 0x10 => 8,
                        _ => -1,
                    };
                    if (frameLen < 0 || len < frameLen)
                    {
                        _logger.LogWarning($"{Tag} {what} 应答格式不对:功能码 0x{recv[1]:X2},收到 {len} 字节,期望 {frameLen}");
                        return null;
                    }
                    if (!Crc16Modbus.VerifyPrefix(recv, frameLen))
                    {
                        _logger.LogWarning($"{Tag} {what} 应答 CRC 校验失败,丢弃");
                        return null;
                    }

                    var frame = new byte[frameLen];
                    Buffer.BlockCopy(recv, 0, frame, 0, frameLen);
                    return frame;
                }
                finally { sem.Release(); }
            }, what, token);
        }

        /// <summary>读保持寄存器(0x03)。失败返回空数组。</summary>
        private async Task<ushort[]> ReadHoldingAsync(ushort start, ushort count, CancellationToken token)
        {
            var frame = await ExchangeAsync(BuildFrame(0x03, start, count), $"读 {start}+{count}", token);
            if (frame is null || frame[1] != 0x03 || frame[2] != count * 2) return Array.Empty<ushort>();
            var r = new ushort[count];
            for (int i = 0; i < count; i++)
                r[i] = (ushort)(frame[3 + i * 2] << 8 | frame[4 + i * 2]);
            return r;
        }

        /// <summary>写单个保持寄存器(0x06)。泵回显原帧。</summary>
        private async Task<bool> WriteSingleAsync(ushort reg, ushort value, CancellationToken token)
        {
            byte[] cmd = BuildFrame(0x06, reg, value);
            var frame = await ExchangeAsync(cmd, $"写 {reg}=0x{value:X4}", token);
            if (frame is null) return false;
            for (int i = 0; i < 6; i++)
                if (frame[i] != cmd[i])
                {
                    _logger.LogWarning($"{Tag} 写 {reg} 回显不一致,第 {i} 字节 {frame[i]:X2} != {cmd[i]:X2}");
                    return false;
                }
            return true;
        }

        /// <summary>写多个保持寄存器(0x10)。</summary>
        private async Task<bool> WriteMultipleAsync(ushort start, ushort[] values, CancellationToken token)
        {
            int n = values.Length;
            byte[] f = new byte[7 + n * 2];
            f[0] = GetSlaveAddress();
            f[1] = 0x10;
            f[2] = (byte)(start >> 8); f[3] = (byte)(start & 0xFF);
            f[4] = (byte)(n >> 8); f[5] = (byte)(n & 0xFF);
            f[6] = (byte)(n * 2);
            for (int i = 0; i < n; i++)
            {
                f[7 + i * 2] = (byte)(values[i] >> 8);
                f[8 + i * 2] = (byte)(values[i] & 0xFF);
            }
            byte[] cmd = Crc16Modbus.AppendCrc(f, 0, f.Length);

            var frame = await ExchangeAsync(cmd, $"写多个 {start}+{n}", token);
            if (frame is null || frame[1] != 0x10) return false;
            ushort echoStart = (ushort)(frame[2] << 8 | frame[3]);
            ushort echoCount = (ushort)(frame[4] << 8 | frame[5]);
            if (echoStart != start || echoCount != n)
            {
                _logger.LogWarning($"{Tag} 写多个回显不一致:泵说写了 {echoStart}+{echoCount},我们发的是 {start}+{n}");
                return false;
            }
            return true;
        }

        private byte[] BuildFrame(byte fc, ushort a, ushort b)
        {
            byte[] f =
            {
                GetSlaveAddress(), fc,
                (byte)(a >> 8), (byte)(a & 0xFF),
                (byte)(b >> 8), (byte)(b & 0xFF),
            };
            return Crc16Modbus.AppendCrc(f, 0, f.Length);
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
                        _logger.LogWarning($"{Tag} {what} 超时(第 {attempt}/{MaxRetries} 次),{RetryDelayMs}ms 后重试");
                        await Task.Delay(RetryDelayMs, token).ConfigureAwait(false);
                    }
                }
            }
            throw new TimeoutException($"{what} 失败,{MaxRetries} 次尝试后仍超时", last);
        }

        /// <summary>两个寄存器 → float,高字在前(手册示例 6 mL/min = 40 C0 00 00)。</summary>
        private static float F(ushort[] r, int i) => BitConverter.Int32BitsToSingle(r[i] << 16 | r[i + 1]);

        private static ushort[] Split(float v)
        {
            int bits = BitConverter.SingleToInt32Bits(v);
            return new[] { (ushort)(bits >> 16 & 0xFFFF), (ushort)(bits & 0xFFFF) };
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
                    _logger.LogInformation($"{Tag}[命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"{Tag}[命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        #endregion
    }

    /// <summary>Modbus CRC16。多项式 0xA001,初值 0xFFFF,低字节在前。</summary>
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

        public static bool VerifyPrefix(byte[] buffer, int frameLen)
        {
            if (buffer == null || frameLen < 4 || buffer.Length < frameLen) return false;
            ushort crc = Compute(buffer, 0, frameLen - 2);
            return buffer[frameLen - 2] == (byte)(crc & 0xFF)
                && buffer[frameLen - 1] == (byte)(crc >> 8);
        }
    }
}
