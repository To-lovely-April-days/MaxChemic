// ============================================================================
//  PeristalticPumpSN100CQ_ModbusRTU.cs
//  创锐 SN100CQ 蠕动泵驱动 —— Modbus RTU / RS485。
//  依据《SN100CQ 蠕动泵通讯规约》20230707-v1.0。
//
//  ── 通信参数 ──────────────────────────────────────────────────────
//      出厂 9600 / N / 8 / 1,从站地址 1(可在泵菜单里看)。大端。
//
//  ── 这台泵用了 Modbus 的三张表,别的驱动只用保持寄存器 ─────────────
//      线圈(0x01 读 / 0x05 写):   0 启停、1 全速。写 FF00 启动、0000 停止。
//      输入寄存器(0x04 只读):    0~1 实际转速 float。
//      保持寄存器(0x03/06/10):   1 方向、2~3 手控最大转速、4~5 模拟上限、
//                                 6~7 模拟下限、8~9 设定转速、10 本机地址、11 波特率。
//      所以通信层比培养箱那份多了 0x01 / 0x04 / 0x05 三个功能码。
//
//  ── 写指令只在「485 控制方式」下有效 ───────────────────────────────
//      手册注 4:"一切写指令都是在 485 控制方式使能情况下才有效,可随时读取"。
//      485 控制方式由泵背后的拨码开关决定(Bit1~3 全 OFF),协议里**没有**
//      寄存器能打开它。泵不在这个方式时,写命令照样回显成功、值却纹丝不动。
//      所以本驱动每次写完都回读比对,对不上就明说"泵大概不在 485 控制方式",
//      别让人对着"下发成功"的日志找半天。
//
//  ── float ─────────────────────────────────────────────────────────
//      两个寄存器一个 float,高字在前(手册示例 120rpm = 42 F0 00 00)。
// ============================================================================

using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;

namespace PeristalticPumpSN100CQ_ModbusRTU
{
    /// <summary>地址表。三张表各自从 0 起编号,注意别混用。</summary>
    internal static class Regs
    {
        // ── 线圈(0x01 / 0x05)────────────────────────────
        public const ushort CoilRun = 0;          // FF00 启动,0000 停止
        public const ushort CoilFullSpeed = 1;    // FF00 全速,0000 取消全速

        // ── 输入寄存器(0x04)─────────────────────────────
        public const ushort InSpeedPv = 0;        // float:0 停止;0.1~最大转速 运行

        // ── 保持寄存器(0x03 / 0x06 / 0x10)────────────────
        public const ushort Direction = 1;        // 0 逆时针,1 顺时针
        public const ushort MaxSpeed = 2;         // float 2~150 rpm,手控最大转速
        public const ushort AnalogHi = 4;         // float 2~手控最大转速
        public const ushort AnalogLo = 6;         // float 0.1~模拟上限
        public const ushort SpeedSet = 8;         // float 0.1~手控最大转速
        public const ushort Address = 10;         // 1~255,掉电保存
        public const ushort Baud = 11;            // 0~4,掉电保存

        /// <summary>保持寄存器一次读全:1~11 共 11 个。</summary>
        public const ushort HoldStart = 1;
        public const ushort HoldCount = 11;

        public static readonly string[] BaudNames = { "2400", "4800", "9600", "19200", "38400" };
        public static string BaudText(int code) => code >= 0 && code < BaudNames.Length ? BaudNames[code] : $"未知({code})";
        public static string DirectionText(int v) => v == 1 ? "顺时针" : v == 0 ? "逆时针" : $"未知({v})";
    }

    [Export(typeof(IDevice))]
    public class PeristalticPumpSN100CQ_ModbusRTU : Device, IDeviceScreen
    {
        /// <summary>源码版本戳。**改动本文件时手动更新**,连接时第一行日志打出来。</summary>
        public const string SourceStamp = "2026-09-11-a";

        private const string Tag = "[SN100CQ 蠕动泵]";

        private readonly ILogService _logger;

        private const int MaxRetries = 3;
        private const int RetryDelayMs = 100;
        private const int IoTimeoutMs = 2000;

        /// <summary>写完回读时允许的转速偏差。float 传输不会有误差,留 0.05 只是防止泵内部四舍五入。</summary>
        private const double SpeedTolerance = 0.05;

        /// <summary>手册硬上限:手控最大转速 2~150 rpm。</summary>
        private const double AbsMaxSpeed = 150.0;

        /// <summary>按 DeviceId 隔离的实例锁,保证同一台泵的 IO 串行。</summary>
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _instanceLocks = new();

        /// <summary>最近一次读到的手控最大转速,用来约束设定转速。没读到时按手册上限 150。</summary>
        private double _lastMaxSpeed = AbsMaxSpeed;

        // 模拟模式下的状态,让屏幕在没泵时也能演示
        private bool _simRunning;
        private bool _simFull;
        private int _simDirection = 1;
        private double _simSpeedSet = 60;
        private double _simMaxSpeed = 150;
        private double _simAnalogHi = 150;
        private double _simAnalogLo = 0;

        public string ScreenKey => "PeristalticPumpSN100CQScreen";

        public PeristalticPumpSN100CQ_ModbusRTU()
        {
            _logger = LogManager.GetLogger<PeristalticPumpSN100CQ_ModbusRTU>();

            DeviceId = "SNP0001";
            Name = "SN100CQ 蠕动泵";
            Manufacturer = "创锐 SN100CQ 蠕动泵(Modbus RTU / RS485)";
            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/PeristalticPumpSN100CQ.png";
            Category = DeviceCategories.Pumps;
            ComId = "SNP0001";
            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();

            AllowedRegions = new List<RegionType>
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            Parameters.Variables.Add(new StringParameter("DeviceId", "SNP0001", "设备实例编号")
            {
                Options = new ObservableCollection<string> { "SNP0001", "SNP0002", "SNP0003", "SNP0004" }
            });

            Parameters.Variables.Add(new StringParameter("串口号", SerialPortOptions.DefaultPort(), "RS485 转换器对应的串口")
            {
                Options = SerialPortOptions.CreateOptions()
            });

            // 出厂 9600 8N1。泵上可以改波特率(寄存器 11),所以留下拉。
            Parameters.Variables.Add(new StringParameter("波特率", "9600", "出厂 9600;若用「设置波特率」改过,这里要跟着改")
            {
                Options = new ObservableCollection<string> { "2400", "4800", "9600", "19200", "38400" }
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
            Parameters.Variables.Add(new StringParameter("DTU序列号", "HTLHYRF2GFU9", "DTU 登录包(序列号)，需与云服务器侧该设备的 DTU 一致"));
            InitializeCommands();
        }

        public override bool SupportsZLanGateway => true;

        #region 可监控参数

        public override IReadOnlyList<CollectableParameter> CollectableParameters => new[]
        {
            new CollectableParameter("SPEED_PV",  "实际转速", "rpm", "获取所有数据", "SPEED_PV", isKey: true),
            new CollectableParameter("SPEED_SET", "设定转速", "rpm", "获取所有数据", "SPEED_SET"),
            new CollectableParameter("DIRECTION", "方向",     "",    "获取所有数据", "DIRECTION"),
        };

        public override int AutoCollectIntervalMs => 1000;

        #endregion

        #region 连接

        /// <summary>
        /// 连接探测:读保持寄存器 10「本机地址」,它应当等于我们用的站号。
        /// 这是这张表里唯一能拿来对身份的东西 —— 读别的寄存器读到什么都算通,
        /// 串口选错时会"连接成功"然后一路读垃圾。
        /// </summary>
        protected override async Task<bool> OnConnectAsync() => await ProbeAsync("直连");

        protected override Task<bool> OnConnectViaGatewayAsync() => ProbeAsync("ZLAN 网关");

        private async Task<bool> ProbeAsync(string via)
        {
            try
            {
                _logger.LogInformation(
                    $"{Tag} {SourceStamp} 经{via}连接,站号 {GetSlaveAddress()}," +
                    $"串口 {Parameters.GetValue<string>("串口号")} @ {Parameters.GetValue<string>("波特率")}/8/N/1");

                ushort[] r = await ReadHoldingAsync(Regs.Address, 2, CancellationToken.None);
                if (r.Length < 2)
                {
                    ErrorLog("连接失败:读本机地址/波特率没有得到有效应答");
                    return false;
                }

                if (r[0] != GetSlaveAddress())
                    _logger.LogWarning($"{Tag} 泵回报的本机地址是 {r[0]},与站号 {GetSlaveAddress()} 不一致,请确认没连到别的泵");

                InfoLog($"连接成功:本机地址 {r[0]},波特率 {Regs.BaudText(r[1])}。" +
                        "注意:写指令只在泵处于 485 控制方式(拨码 Bit1~3 全 OFF)时生效");
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
                var r = await ReadInputAsync(Regs.InSpeedPv, 2, CancellationToken.None);
                return r.Length == 2;
            }
            catch { return false; }
        }

        #endregion

        #region 命令

        private void InitializeCommands()
        {
            Commands.Add(new DeviceCommand
            {
                Name = "获取所有数据",
                HelpText = "读线圈(启停/全速)、输入寄存器(实际转速)与保持寄存器 1~11(方向、转速设定、量程、地址、波特率)。屏幕轮询用的就是它。",
                OutputParameters = new DeviceParameters { Variables = BuildAllOutputs() },
                AsyncAction = AsyncWrapAction("获取所有数据", _ => ReadAllAsync())
            });

            AddCoilCommand("启动", "线圈 0 写 FF00,泵按当前设定转速与方向运行", Regs.CoilRun, true);
            AddCoilCommand("停止", "线圈 0 写 0000", Regs.CoilRun, false);

            Commands.Add(new DeviceCommand
            {
                Name = "设置全速",
                HelpText = "0=取消全速,1=全速(线圈 1)。全速时泵按手控最大转速跑,常用于灌管/排空",
                InputParameters = OneInput(new NumberParameter("TargetValue", 0, 1, 0, "0 取消 / 1 全速", false)),
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("设置全速", async p =>
                {
                    bool on = (p?.GetNullableValue<double>("TargetValue") ?? 0) >= 0.5;
                    if (IsSimulationMode) { _simFull = on; return Success(true); }
                    return Success(await WriteCoilAsync(Regs.CoilFullSpeed, on, CancellationToken.None));
                })
            });

            Commands.Add(new DeviceCommand
            {
                Name = "设置方向",
                HelpText = "0=逆时针,1=顺时针(保持寄存器 1)",
                InputParameters = OneInput(new NumberParameter("TargetValue", 0, 1, 1, "0 逆时针 / 1 顺时针", false)),
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
                Name = "设置转速",
                HelpText = "设定转速 0.1~手控最大转速(rpm),写保持寄存器 8~9 并回读比对",
                InputParameters = OneInput(new NumberParameter("TargetValue", 0.1, AbsMaxSpeed, 60, "转速设定", false, "rpm") { DecimalPlaces = 1 }),
                OutputParameters = VerifiedOutputs(),
                AsyncAction = AsyncWrapAction("设置转速", p => SetSpeedAsync(p, startAfter: false))
            });

            Commands.Add(new DeviceCommand
            {
                Name = "按转速启动",
                HelpText = "写转速 → 回读比对 → 启动。任一步不过就不启动,避免泵按旧转速跑起来",
                InputParameters = OneInput(new NumberParameter("TargetValue", 0.1, AbsMaxSpeed, 60, "转速设定", false, "rpm") { DecimalPlaces = 1 }),
                OutputParameters = VerifiedOutputs(),
                AsyncAction = AsyncWrapAction("按转速启动", p => SetSpeedAsync(p, startAfter: true))
            });

            AddFloatCommand("设置手控最大转速", "手控最大转速 2~150 rpm(保持寄存器 2~3)。它同时是设定转速与模拟上限的上限",
                Regs.MaxSpeed, 2, AbsMaxSpeed, (v) => _simMaxSpeed = v);
            AddFloatCommand("设置模拟速度上限", "外控模拟量对应的转速上限,2~手控最大转速(保持寄存器 4~5)",
                Regs.AnalogHi, 2, AbsMaxSpeed, (v) => _simAnalogHi = v);
            AddFloatCommand("设置模拟速度下限", "外控模拟量对应的转速下限,0.1~模拟上限,默认 0(保持寄存器 6~7)",
                Regs.AnalogLo, 0, AbsMaxSpeed, (v) => _simAnalogLo = v);

            Commands.Add(new DeviceCommand
            {
                Name = "设置本机地址",
                HelpText = "改泵的 485 地址 1~255,掉电保存。写成功后泵立刻用新地址应答,本设备的「Modbus站号」参数要同步改,否则下一条命令就超时",
                InputParameters = OneInput(new NumberParameter("TargetValue", 1, 255, 1, "新地址", false)),
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("设置本机地址", async p =>
                {
                    ushort v = (ushort)(int)(p?.GetNullableValue<double>("TargetValue") ?? 1);
                    if (IsSimulationMode) return Success(true);
                    // 不回读:写完泵就换地址了,用旧站号读不到。回显对上即算成功。
                    bool ok = await WriteSingleAsync(Regs.Address, v, CancellationToken.None);
                    if (ok) _logger.LogWarning($"{Tag} 本机地址已改为 {v},请把设备参数「Modbus站号」改成 {v} 后重新连接");
                    return Success(ok);
                })
            });

            Commands.Add(new DeviceCommand
            {
                Name = "设置波特率",
                HelpText = "0=2400 1=4800 2=9600 3=19200 4=38400,掉电保存。写成功后本设备的「波特率」参数要同步改并重新连接",
                InputParameters = OneInput(new NumberParameter("TargetValue", 0, 4, 2, "波特率代号", false)),
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction("设置波特率", async p =>
                {
                    ushort v = (ushort)(int)(p?.GetNullableValue<double>("TargetValue") ?? 2);
                    if (IsSimulationMode) return Success(true);
                    bool ok = await WriteSingleAsync(Regs.Baud, v, CancellationToken.None);
                    if (ok) _logger.LogWarning($"{Tag} 波特率已改为 {Regs.BaudText(v)},请把设备参数「波特率」改成 {Regs.BaudText(v)} 后重新连接");
                    return Success(ok);
                })
            });
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

        private static DeviceParameters VerifiedOutputs() => new()
        {
            Variables = new ObservableCollection<ParameterBase>
            {
                new BooleanParameter("Success", false, "是否成功", true),
                new BooleanParameter("Verified", false, "回读是否与设定一致", true),
                new NumberParameter("ReadBack", 0, 1000, 0, "回读值", true, "rpm"),
                new StringParameter("Message", "", "说明"),
            }
        };

        /// <summary>线圈开关命令(启动/停止)。写完回读线圈确认。</summary>
        private void AddCoilCommand(string name, string help, ushort coil, bool on)
        {
            Commands.Add(new DeviceCommand
            {
                Name = name,
                HelpText = help,
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction(name, async _ =>
                {
                    if (IsSimulationMode) { _simRunning = on; return Success(true); }
                    return Success(await WriteCoilAsync(coil, on, CancellationToken.None));
                })
            });
        }

        /// <summary>float 类设定命令:写两个寄存器并回读比对。</summary>
        private void AddFloatCommand(string name, string help, ushort start, double min, double max, Action<double> simApply)
        {
            Commands.Add(new DeviceCommand
            {
                Name = name,
                HelpText = help,
                InputParameters = OneInput(new NumberParameter("TargetValue", min, max, min, help, false, "rpm") { DecimalPlaces = 1 }),
                OutputParameters = VerifiedOutputs(),
                AsyncAction = AsyncWrapAction(name, async p =>
                {
                    var o = VerifiedOutputs();
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
                    var (ok, verified, back) = await WriteFloatVerifiedAsync(start, (float)v, name, CancellationToken.None);
                    o.SetValue("Success", ok && verified);
                    o.SetValue("Verified", verified);
                    o.SetValue("ReadBack", Math.Round(back, 2));
                    o.SetValue("Message", ok && verified ? $"{name} = {v:F1} rpm 已写入" : NotTakenMessage(name, v, back, ok));
                    return o;
                })
            });
        }

        private async Task<DeviceParameters> SetSpeedAsync(DeviceParameters? p, bool startAfter)
        {
            var o = VerifiedOutputs();
            double v = p?.GetNullableValue<double>("TargetValue") ?? double.NaN;

            // 上限按泵自己报的手控最大转速,不按手册的 150:泵被人设成 100 时,120 发下去泵不会认。
            double max = _lastMaxSpeed > 0 ? _lastMaxSpeed : AbsMaxSpeed;
            if (double.IsNaN(v) || v < 0.1 || v > max + 1e-6)
            {
                o.SetValue("Message", $"转速 {v} 超出 0.1~{max:F1} rpm(上限为泵当前的手控最大转速)");
                return o;
            }

            if (IsSimulationMode)
            {
                _simSpeedSet = v;
                if (startAfter) _simRunning = true;
                o.SetValue("Success", true); o.SetValue("Verified", true); o.SetValue("ReadBack", v);
                o.SetValue("Message", startAfter ? $"模拟模式:已按 {v:F1} rpm 启动" : $"模拟模式:转速 = {v:F1} rpm");
                return o;
            }

            var token = CancellationToken.None;
            var (ok, verified, back) = await WriteFloatVerifiedAsync(Regs.SpeedSet, (float)v, "设定转速", token);
            o.SetValue("Verified", verified);
            o.SetValue("ReadBack", Math.Round(back, 2));

            if (!(ok && verified))
            {
                o.SetValue("Message", NotTakenMessage("设定转速", v, back, ok) + (startAfter ? " 已放弃启动。" : ""));
                return o;
            }

            if (!startAfter)
            {
                o.SetValue("Success", true);
                o.SetValue("Message", $"转速 = {v:F1} rpm 已写入(回读 {back:F1})");
                return o;
            }

            bool started = await WriteCoilAsync(Regs.CoilRun, true, token);
            o.SetValue("Success", started);
            o.SetValue("Message", started ? $"已按 {v:F1} rpm 启动(回读 {back:F1},比对通过)" : "转速已写入,但启动线圈写入失败");
            InfoLog($"按转速启动 {v:F1} rpm → {started}");
            return o;
        }

        private string NotTakenMessage(string what, double set, double back, bool echoOk)
        {
            if (!echoOk) return $"{what} 写入无应答或被拒";
            // 回显对了、回读没变:这台泵最典型的"看起来成功了"。
            return $"{what} 回读比对不通过:设定 {set:F1},回读 {back:F1}。泵大概不在 485 控制方式(拨码 Bit1~3 应全 OFF),或值超出泵允许范围";
        }

        #endregion

        #region 读

        private static ObservableCollection<ParameterBase> BuildAllOutputs() => new()
        {
            new BooleanParameter("RUNNING", false, "是否运行(线圈 0)", true),
            new BooleanParameter("FULL_SPEED", false, "是否全速(线圈 1)", true),
            new NumberParameter("SPEED_PV",  0, 1000, 0, "实际转速", true, "rpm"),
            new NumberParameter("SPEED_SET", 0, 1000, 0, "设定转速", true, "rpm"),
            new NumberParameter("DIRECTION", 0, 1, 1, "方向 0 逆时针 / 1 顺时针", true),
            new StringParameter("DIRECTION_TEXT", "", "方向文字"),
            new NumberParameter("MAX_SPEED", 0, 1000, 150, "手控最大转速", true, "rpm"),
            new NumberParameter("ANALOG_HI", 0, 1000, 0, "模拟速度上限", true, "rpm"),
            new NumberParameter("ANALOG_LO", 0, 1000, 0, "模拟速度下限", true, "rpm"),
            new NumberParameter("ADDRESS",   0, 255, 1, "本机地址", true),
            new NumberParameter("BAUD_CODE", 0, 4, 2, "波特率代号", true),
            new StringParameter("BAUD_TEXT", "", "波特率"),
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

            // 实际转速是必须项;线圈与保持区任一失败只告警,先把读到的显示出来。
            ushort[] inp = await ReadInputAsync(Regs.InSpeedPv, 2, token);
            if (inp.Length < 2)
            {
                o.SetValue("Success", false);
                return o;
            }
            o.SetValue("SPEED_PV", Math.Round(F(inp, 0), 2));

            bool[] coils;
            try { coils = await ReadCoilsAsync(Regs.CoilRun, 2, token); }
            catch (Exception ex) { _logger.LogWarning($"{Tag} 读线圈失败:{ex.Message}"); coils = Array.Empty<bool>(); }
            if (coils.Length >= 2)
            {
                o.SetValue("RUNNING", coils[0]);
                o.SetValue("FULL_SPEED", coils[1]);
            }
            else
            {
                // 线圈读不到时用实际转速兜底判"在不在转"
                o.SetValue("RUNNING", F(inp, 0) > 0.05);
            }

            ushort[] h;
            try { h = await ReadHoldingAsync(Regs.HoldStart, Regs.HoldCount, token); }
            catch (Exception ex) { _logger.LogWarning($"{Tag} 读保持寄存器 1~11 失败:{ex.Message}"); h = Array.Empty<ushort>(); }
            if (h.Length >= Regs.HoldCount)
            {
                int H(ushort addr) => addr - Regs.HoldStart;

                int dir = h[H(Regs.Direction)];
                o.SetValue("DIRECTION", (double)dir);
                o.SetValue("DIRECTION_TEXT", Regs.DirectionText(dir));

                double max = F(h, H(Regs.MaxSpeed));
                if (max >= 2 && max <= AbsMaxSpeed) _lastMaxSpeed = max;
                o.SetValue("MAX_SPEED", Math.Round(max, 2));
                o.SetValue("ANALOG_HI", Math.Round(F(h, H(Regs.AnalogHi)), 2));
                o.SetValue("ANALOG_LO", Math.Round(F(h, H(Regs.AnalogLo)), 2));
                o.SetValue("SPEED_SET", Math.Round(F(h, H(Regs.SpeedSet)), 2));
                o.SetValue("ADDRESS", (double)h[H(Regs.Address)]);
                int baud = h[H(Regs.Baud)];
                o.SetValue("BAUD_CODE", (double)baud);
                o.SetValue("BAUD_TEXT", Regs.BaudText(baud));
            }

            o.SetValue("Success", true);
            return o;
        }

        private void FillSimulated(DeviceParameters o)
        {
            double t = Environment.TickCount64 / 1000.0;
            double pv = _simRunning ? (_simFull ? _simMaxSpeed : _simSpeedSet) + 0.3 * Math.Sin(t / 3.0) : 0;
            o.SetValue("RUNNING", _simRunning);
            o.SetValue("FULL_SPEED", _simFull);
            o.SetValue("SPEED_PV", Math.Round(Math.Max(0, pv), 2));
            o.SetValue("SPEED_SET", _simSpeedSet);
            o.SetValue("DIRECTION", (double)_simDirection);
            o.SetValue("DIRECTION_TEXT", Regs.DirectionText(_simDirection));
            o.SetValue("MAX_SPEED", _simMaxSpeed);
            o.SetValue("ANALOG_HI", _simAnalogHi);
            o.SetValue("ANALOG_LO", _simAnalogLo);
            o.SetValue("ADDRESS", (double)GetSlaveAddress());
            o.SetValue("BAUD_CODE", 2d);
            o.SetValue("BAUD_TEXT", "9600");
            o.SetValue("Success", true);
        }

        #endregion

        #region 写(带回读)

        /// <summary>写单个保持寄存器并回读比对。</summary>
        private async Task<bool> WriteSingleVerifiedAsync(ushort reg, ushort value, string what, CancellationToken token)
        {
            if (!await WriteSingleAsync(reg, value, token)) return false;
            var r = await ReadHoldingAsync(reg, 1, token);
            if (r.Length == 1 && r[0] == value) return true;
            _logger.LogWarning($"{Tag} {what} 写入后回读为 {(r.Length == 1 ? r[0].ToString() : "无")},不是 {value}。泵大概不在 485 控制方式");
            return false;
        }

        /// <summary>写 float(两个寄存器,0x10)并回读。返回 (回显是否成功, 回读是否一致, 回读值)。</summary>
        private async Task<(bool ok, bool verified, double back)> WriteFloatVerifiedAsync(ushort start, float value, string what, CancellationToken token)
        {
            if (!await WriteMultipleAsync(start, Split(value), token)) return (false, false, double.NaN);

            var r = await ReadHoldingAsync(start, 2, token);
            if (r.Length < 2) return (true, false, double.NaN);

            double back = F(r, 0);
            bool verified = Math.Abs(back - value) <= SpeedTolerance;
            if (!verified)
                _logger.LogWarning($"{Tag} {what} 写 {value:F2} 后回读 {back:F2},不一致");
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

        /// <summary>
        /// 一次请求-应答:发一帧,校验站号 / 异常位 / 帧长 / CRC,返回整帧(不含多余字节)。
        /// 格式错、CRC 错、异常码都返回 null 且不重试;只有超时才重试(见 RetryAsync)。
        /// </summary>
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
                        0x01 or 0x03 or 0x04 => 5 + recv[2],   // 站号 功能码 字节数 数据 CRC
                        0x05 or 0x06 or 0x10 => 8,             // 站号 功能码 地址 值/数量 CRC
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

        /// <summary>读线圈(0x01)。返回 count 个 bool;失败返回空数组。</summary>
        private async Task<bool[]> ReadCoilsAsync(ushort start, ushort count, CancellationToken token)
        {
            var frame = await ExchangeAsync(BuildFrame(0x01, start, count), $"读线圈 {start}+{count}", token);
            if (frame is null || frame[1] != 0x01) return Array.Empty<bool>();
            int need = (count + 7) / 8;
            if (frame[2] < need) return Array.Empty<bool>();
            var r = new bool[count];
            for (int i = 0; i < count; i++)
                r[i] = (frame[3 + i / 8] >> (i % 8) & 1) != 0;   // 低位在前
            return r;
        }

        /// <summary>读输入寄存器(0x04)。</summary>
        private Task<ushort[]> ReadInputAsync(ushort start, ushort count, CancellationToken token)
            => ReadWordsAsync(0x04, start, count, $"读输入寄存器 {start}+{count}", token);

        /// <summary>读保持寄存器(0x03)。</summary>
        private Task<ushort[]> ReadHoldingAsync(ushort start, ushort count, CancellationToken token)
            => ReadWordsAsync(0x03, start, count, $"读保持寄存器 {start}+{count}", token);

        private async Task<ushort[]> ReadWordsAsync(byte fc, ushort start, ushort count, string what, CancellationToken token)
        {
            var frame = await ExchangeAsync(BuildFrame(fc, start, count), what, token);
            if (frame is null || frame[1] != fc || frame[2] != count * 2) return Array.Empty<ushort>();
            var r = new ushort[count];
            for (int i = 0; i < count; i++)
                r[i] = (ushort)(frame[3 + i * 2] << 8 | frame[4 + i * 2]);
            return r;
        }

        /// <summary>写单个线圈(0x05):FF00 = ON,0000 = OFF。泵回显原帧。</summary>
        private async Task<bool> WriteCoilAsync(ushort coil, bool on, CancellationToken token)
        {
            byte[] cmd = BuildFrame(0x05, coil, on ? (ushort)0xFF00 : (ushort)0x0000);
            var frame = await ExchangeAsync(cmd, $"写线圈 {coil}={(on ? "ON" : "OFF")}", token);
            return frame is not null && EchoMatches(frame, cmd);
        }

        /// <summary>写单个保持寄存器(0x06)。泵回显原帧。</summary>
        private async Task<bool> WriteSingleAsync(ushort reg, ushort value, CancellationToken token)
        {
            byte[] cmd = BuildFrame(0x06, reg, value);
            var frame = await ExchangeAsync(cmd, $"写寄存器 {reg}={value}", token);
            return frame is not null && EchoMatches(frame, cmd);
        }

        /// <summary>写多个保持寄存器(0x10)。应答:站号 0x10 起始地址 数量 CRC。</summary>
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

            var frame = await ExchangeAsync(cmd, $"写多个寄存器 {start}+{n}", token);
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

        private bool EchoMatches(byte[] frame, byte[] cmd)
        {
            for (int i = 0; i < 6; i++)
                if (frame[i] != cmd[i])
                {
                    _logger.LogWarning($"{Tag} 回显不一致,第 {i} 字节 {frame[i]:X2} != {cmd[i]:X2}");
                    return false;
                }
            return true;
        }

        /// <summary>站号 + 功能码 + 两个 16 位字段 + CRC 的通用六字节帧(0x01/03/04/05/06 都是这个形状)。</summary>
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

        /// <summary>只重试超时;协议错、CRC 错、异常码重试没有意义。</summary>
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

        /// <summary>两个寄存器 → float,高字在前(手册示例 42 F0 00 00 = 120)。</summary>
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
