using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;

namespace MagneticStirrer_ModbusRTU
{
    /// <summary>
    /// 磁力搅拌器驱动（RTC 系列控制器，Modbus RTU，功能码 03 读 / 06 写单个 / 10 写多个）。
    ///
    /// 通信设置：9600, N, 8, 1；RJ45 接口 2 脚=A+, 4 脚=B-；每次读取最多 32 个寄存器。
    ///
    /// 寄存器表（读区 0x0000~0x0011 均可用 FC03 读）：
    ///   0x0002 位状态（.0 外置温度异常 .1 内置温度异常 .2 上偏差 .3 传感器脱落 .4 内置温度保护
    ///                  .5 电机霍尔故障 .6 加热输出 .7 定时结束）
    ///   0x0003 温度最大设定值(×10)   0x0004 温度最小设定值(×10)
    ///   0x0005 外置温度(×10, 可负)   0x0006 内置温度(×10, 可负)
    ///   0x0007/0x0008/0x0009 计时 时/分/秒        0x000A 速度测量值(RPM)
    ///   0x000B 温度设定值(×10)                    0x000C/0x000D/0x000E 时间设定 时/分/秒
    ///   0x000F 速度设定值(RPM)                     0x0010 温度开关(0停/1启)   0x0011 速度开关(0停/1启)
    ///
    /// 写命令按"一件事一条指令"合并（地址不连续处自动拆帧，绝不越写相邻寄存器）：
    ///   温度控制：0x06 写 0x000B(温度×10) + 0x06 写 0x0010(开关)
    ///             —— 两址间隔着时间/速度设定，故不能用 0x10 连写，必须两帧
    ///   搅拌控制：0x10 写 0x000C~0x000F(时,分,秒,速度 共4个) + 0x06 写 0x0011(开关)
    ///             —— 前四个地址连续故一帧写完；速度开关与其间隔着温度开关，单独一帧
    /// </summary>
    [Export(typeof(IDevice))]
    public class MagneticStirrer_ModbusRTU : Device, IDeviceUIInteraction, IDeviceScreen
    {
        public string ScreenKey => "MagneticStirrerScreen";
        /// <summary>可监控参数白名单:声明了的才出现在监控界面并被自动采集,
        /// 其余输出参数(报警位/设定回显等)不进入监控体系;取数复用既有读命令。</summary>
        public override IReadOnlyList<CollectableParameter> CollectableParameters { get; } = new[]
        {
            new CollectableParameter("ExternalTemperature", "外置温度", "℃", source: "获取所有数据", isKey: true),
            new CollectableParameter("InternalTemperature", "内置温度", "℃", source: "获取所有数据"),
            new CollectableParameter("SpeedMeasured", "速度测量值", "RPM", source: "获取所有数据"),
            new CollectableParameter("TemperatureSV", "温度设定值", "℃", source: "获取所有数据"),
            new CollectableParameter("SpeedSV", "速度设定值", "RPM", source: "获取所有数据"),
        };

        /// <summary>采集间隔(毫秒):控制通信与日志量。</summary>
        public override int AutoCollectIntervalMs => 2000;

        private readonly ILogService _logger;

        // ── 寄存器地址 ──
        private const ushort RegStatusBits = 0x0002;   // 位状态(报警/加热输出/定时结束)
        private const ushort RegTempSetting = 0x000B;  // 温度设定值(×10)
        private const ushort RegTimeHour = 0x000C;     // 时间设定-小时(0x000C~0x000E 连续)
        private const ushort RegSpeedSetting = 0x000F; // 速度设定值(RPM)
        private const ushort RegTempSwitch = 0x0010;   // 温度开关 0停/1启
        private const ushort RegSpeedSwitch = 0x0011;  // 速度开关 0停/1启

        /// <summary>一次性读取的寄存器块:0x0002~0x0011 共 16 个(表中上限 32 个,安全)。</summary>
        private const ushort ReadBlockStart = RegStatusBits;
        private const ushort ReadBlockCount = 16;

        /// <summary>温度小数点位=1 → 寄存器值 = 温度 × 10。</summary>
        private const double TempScale = 10.0;

        // 通信层重试参数
        private const int MaxRetries = 3;
        private const int RetryDelayMs = 100;

        // 实例级互斥锁:同一设备实例的所有 IO 串行化,避免与轮询/心跳撞车(按 DeviceId 隔离)
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _instanceLocks
            = new ConcurrentDictionary<string, SemaphoreSlim>();

        // ── 模拟模式状态:无硬件时也能完整演示(设定→测量值一阶逼近) ──
        private double _simTempSv = 60;      // 温度设定 ℃
        private double _simExtTemp = 25;     // 外置温度 ℃
        private double _simSpeedSv = 300;    // 速度设定 RPM
        private double _simSpeed;            // 速度测量 RPM
        private bool _simTempOn;
        private bool _simSpeedOn;
        private int _simHour, _simMinute, _simSecond;

        // ── 最近一次采集到的状态:供画布自定义控件(MagneticStirrerControl)显示 ──
        private double _lastExtTemp;
        private double _lastTempSv;
        private int _lastSpeed;
        private int _lastSpeedSv;
        private bool _lastTempOn;
        private bool _lastSpeedOn;

        public MagneticStirrer_ModbusRTU()
        {
            _logger = LogManager.GetLogger<MagneticStirrer_ModbusRTU>();

            DeviceId = "MST0001";
            Name = "磁力搅拌";
            Manufacturer = "ModbusRTU 磁力搅拌器(RTC 系列控制器)";
            // 画布上用自定义控件绘制,而不是静态图片 —— 这样搅拌开启时画布上的搅拌子
            // 和液面旋涡会跟着转,加热时陶瓷盘发红,示值也跟着实时数据走。
            // 若要退回静态图,换成 "pack://siteoforigin:,,,/Resources/DeviceIcon/magneticstirrer.png"。
            ImageLocation = "CUSTOM_CONTROL:MagneticStirrerControl";
            Category = DeviceCategories.PublicWorks;
            ComId = "MST0001";
            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();
            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            Parameters.Variables.Add(new StringParameter("DeviceId", "MST0001", "ModbusRTU设备ID")
            {
                Options = new ObservableCollection<string>() { "MST0001", "MST0002", "MST0003", "MST0004", "MST0005" },
            });

            // 串口号/波特率/校验位/数据位/停止位/通信方式统一由 DeviceParameterOptions 维护:
            // 串口号取本机实际扫描结果,校验位/停止位/通信方式取自对应枚举,不再各驱动手写。
            // 本机默认 9600,N,8,1 —— 与 RTC 控制器协议规定一致,故不需传参覆盖。
            DeviceParameterOptions.AddSerialParameters(Parameters);

            // 自建云服务器参数（仅"通信方式=RemoteServer"时生效）
            Parameters.Variables.Add(new StringParameter("DTU序列号", "HTB8HYQ2UHFF", "DTU 登录包(序列号)，需与云服务器侧该设备的 DTU 一致"));

            // Modbus 从机站号 (1~254)
            Parameters.Variables.Add(new NumberParameter("Modbus站号", 1, 254, 1, "Modbus 从机站号 (1~254)"));

            InitializeCommands();
        }

        /// <summary>声明本驱动支持通过 ZLAN 网关通信(RJ45 转 485)。</summary>
        public override bool SupportsZLanGateway => true;

        private void InitializeCommands()
        {
            // ════════════ 命令一:温度控制(温度设定值 + 温度开关) ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "温度控制",
                HelpText = "一条指令完成温度设定与启停:写 0x000B(温度设定值×10) 与 0x0010(温度开关 0停/1启)。" +
                           "两地址之间隔着时间与速度设定寄存器,故拆为两帧 0x06 写入,不会误改其他设定。",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TargetTemperature", 0, 600, 0, "温度设定值", true, "℃"),
                        new BooleanParameter("TemperatureSwitch", false, "温度开关(关=停止, 开=启动)", true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTemperature", 0, 600, 0, "设定的温度", true, "℃"),
                        new BooleanParameter("SetSwitch", false, "设定的温度开关", true)
                    }
                },

                AsyncAction = AsyncWrapAction("温度控制", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    double target = parameter.GetValue<double>("TargetTemperature");
                    bool onOff = parameter.GetValue<bool>("TemperatureSwitch");
                    bool success;

                    RaiseControlStateChanged("CommandState_温度控制", CommandExecutionState.Running);

                    if (IsSimulationMode)
                    {
                        await Task.Delay(300);
                        _simTempSv = target;
                        _simTempOn = onOff;
                        success = true;
                        InfoLog($"模拟模式温度控制:设定 {target}℃,开关 {(onOff ? "启动" : "停止")}");
                    }
                    else
                    {
                        // 第一帧:温度设定值(×10)
                        ushort tempReg = ToRegister(target * TempScale);
                        byte[] f1 = BuildWriteCmd(RegTempSetting, tempReg);
                        bool ok1 = await WriteAsync(f1, 0, f1.Length, CancellationToken.None);

                        // 第二帧:温度开关
                        byte[] f2 = BuildWriteCmd(RegTempSwitch, (ushort)(onOff ? 1 : 0));
                        bool ok2 = await WriteAsync(f2, 0, f2.Length, CancellationToken.None);

                        success = ok1 && ok2;
                        InfoLog($"温度控制,结果(Success:{success}, 设定:{target}℃[寄存器 {tempReg}], 开关:{(onOff ? "启动" : "停止")})");
                    }

                    if (success)
                    {
                        _lastTempSv = target;
                        _lastTempOn = onOff;
                        RaiseControlStateChanged("TemperatureSV", target);
                        RaiseControlStateChanged("TemperatureSwitch", onOff);
                    }
                    RaiseControlStateChanged("CommandState_温度控制",
                        success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new BooleanParameter("Success", success, "设置成功", true),
                            new NumberParameter("SetTemperature", 0, 600, target, "设定的温度", true, "℃"),
                            new BooleanParameter("SetSwitch", onOff, "设定的温度开关", true)
                        }
                    };
                })
            });

            // ════════════ 命令二:搅拌控制(时间设定 + 速度设定 + 速度开关) ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "搅拌控制",
                HelpText = "一条指令完成定时、转速设定与启停:以 0x10 连写 0x000C~0x000F(时/分/秒/速度设定)," +
                           "再以 0x06 写 0x0011(速度开关 0停/1启)。时间设为 0/0/0 表示不定时。",

                InputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("TimeHour", 0, 99, 0, "时间设定-小时", true, "小时"),
                        new NumberParameter("TimeMinute", 0, 59, 0, "时间设定-分钟", true, "分钟"),
                        new NumberParameter("TimeSecond", 0, 59, 0, "时间设定-秒", true, "秒"),
                        new NumberParameter("TargetSpeed", 0, 6000, 0, "速度设定值", true, "RPM"),
                        new BooleanParameter("SpeedSwitch", false, "速度开关(关=停止, 开=启动)", true)
                    }
                },

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new BooleanParameter("Success", false, "设置成功", true),
                        new NumberParameter("SetTimeHour", 0, 99, 0, "设定的小时", true, "小时"),
                        new NumberParameter("SetTimeMinute", 0, 59, 0, "设定的分钟", true, "分钟"),
                        new NumberParameter("SetTimeSecond", 0, 59, 0, "设定的秒", true, "秒"),
                        new NumberParameter("SetSpeed", 0, 6000, 0, "设定的速度", true, "RPM"),
                        new BooleanParameter("SetSwitch", false, "设定的速度开关", true)
                    }
                },

                AsyncAction = AsyncWrapAction("搅拌控制", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");
                    int hour = Clamp((int)Math.Round(parameter.GetValue<double>("TimeHour")), 0, 99);
                    int minute = Clamp((int)Math.Round(parameter.GetValue<double>("TimeMinute")), 0, 59);
                    int second = Clamp((int)Math.Round(parameter.GetValue<double>("TimeSecond")), 0, 59);
                    int speed = Clamp((int)Math.Round(parameter.GetValue<double>("TargetSpeed")), 0, 6000);
                    bool onOff = parameter.GetValue<bool>("SpeedSwitch");
                    bool success;

                    RaiseControlStateChanged("CommandState_搅拌控制", CommandExecutionState.Running);

                    if (IsSimulationMode)
                    {
                        await Task.Delay(300);
                        _simHour = hour; _simMinute = minute; _simSecond = second;
                        _simSpeedSv = speed;
                        _simSpeedOn = onOff;
                        success = true;
                        InfoLog($"模拟模式搅拌控制:定时 {hour}时{minute}分{second}秒,转速 {speed} RPM,开关 {(onOff ? "启动" : "停止")}");
                    }
                    else
                    {
                        // 第一帧:0x10 连写 0x000C~0x000F —— 时、分、秒、速度设定(地址连续)
                        byte[] f1 = BuildWriteMultiCmd(RegTimeHour, new ushort[]
                        {
                            (ushort)hour, (ushort)minute, (ushort)second, (ushort)speed
                        });
                        bool ok1 = await WriteAsync(f1, 0, f1.Length, CancellationToken.None);

                        // 第二帧:速度开关(与上段之间隔着温度开关 0x0010,故单独写)
                        byte[] f2 = BuildWriteCmd(RegSpeedSwitch, (ushort)(onOff ? 1 : 0));
                        bool ok2 = await WriteAsync(f2, 0, f2.Length, CancellationToken.None);

                        success = ok1 && ok2;
                        InfoLog($"搅拌控制,结果(Success:{success}, 定时 {hour}:{minute:D2}:{second:D2}, 转速 {speed} RPM, 开关:{(onOff ? "启动" : "停止")})");
                    }

                    if (success)
                    {
                        _lastSpeedSv = speed;
                        _lastSpeedOn = onOff;
                        RaiseControlStateChanged("SpeedSV", (double)speed);
                        RaiseControlStateChanged("SpeedSwitch", onOff);
                    }
                    RaiseControlStateChanged("CommandState_搅拌控制",
                        success ? CommandExecutionState.Succeeded : CommandExecutionState.Failed);

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new BooleanParameter("Success", success, "设置成功", true),
                            new NumberParameter("SetTimeHour", 0, 99, hour, "设定的小时", true, "小时"),
                            new NumberParameter("SetTimeMinute", 0, 59, minute, "设定的分钟", true, "分钟"),
                            new NumberParameter("SetTimeSecond", 0, 59, second, "设定的秒", true, "秒"),
                            new NumberParameter("SetSpeed", 0, 6000, speed, "设定的速度", true, "RPM"),
                            new BooleanParameter("SetSwitch", onOff, "设定的速度开关", true)
                        }
                    };
                })
            });

            // ════════════ 命令三:获取所有数据(监控与自动采集的取数来源) ════════════
            Commands.Add(new DeviceCommand()
            {
                Name = "获取所有数据",
                HelpText = "一次 FC03 读取 0x0002~0x0011 共 16 个寄存器:报警位、温度上下限、内外置温度、" +
                           "计时、速度测量值、温度/时间/速度设定值与两个开关状态。监控图表与自动采集由此取数。",

                OutputParameters = new DeviceParameters()
                {
                    Variables = new ObservableCollection<ParameterBase>()
                    {
                        new NumberParameter("ExternalTemperature", -30, 430, 0, "外置温度", true, "℃"),
                        new NumberParameter("InternalTemperature", -30, 630, 0, "内置温度", true, "℃"),
                        new NumberParameter("SpeedMeasured", 0, 6000, 0, "速度测量值", true, "RPM"),
                        new NumberParameter("TemperatureSV", 0, 600, 0, "温度设定值", true, "℃"),
                        new NumberParameter("SpeedSV", 0, 6000, 0, "速度设定值", true, "RPM"),
                        new NumberParameter("TimeHourSV", 0, 99, 0, "时间设定-小时", true, "小时"),
                        new NumberParameter("TimeMinuteSV", 0, 59, 0, "时间设定-分钟", true, "分钟"),
                        new NumberParameter("TimeSecondSV", 0, 59, 0, "时间设定-秒", true, "秒"),
                        new NumberParameter("TimerHour", 0, 99, 0, "计时-小时", true, "小时"),
                        new NumberParameter("TimerMinute", 0, 59, 0, "计时-分钟", true, "分钟"),
                        new NumberParameter("TimerSecond", 0, 59, 0, "计时-秒", true, "秒"),
                        new NumberParameter("TempMaxSetting", 0, 600, 0, "温度最大设定值", true, "℃"),
                        new NumberParameter("TempMinSetting", 0, 600, 0, "温度最小设定值", true, "℃"),
                        new BooleanParameter("TemperatureSwitch", false, "温度开关状态", true),
                        new BooleanParameter("SpeedSwitch", false, "速度开关状态", true),
                        new BooleanParameter("ExtTempAlarm", false, "外置温度异常", true),
                        new BooleanParameter("IntTempAlarm", false, "内置温度异常", true),
                        new BooleanParameter("UpperDeviationAlarm", false, "上偏差报警", true),
                        new BooleanParameter("SensorFallOffAlarm", false, "传感器脱落报警", true),
                        new BooleanParameter("IntTempProtect", false, "内置温度保护", true),
                        new BooleanParameter("MotorHallFault", false, "电机霍尔故障", true),
                        new BooleanParameter("HeatingOutput", false, "加热输出中", true),
                        new BooleanParameter("TimerFinished", false, "定时结束", true)
                    }
                },

                AsyncAction = AsyncWrapAction("获取所有数据", async parameter =>
                {
                    ComId = Parameters.GetValue<string>("DeviceId");

                    double extTemp, intTemp, tempSv, tempMax, tempMin;
                    int speedMeasured, speedSv, hourSv, minuteSv, secondSv, timerH, timerM, timerS;
                    bool tempOn, speedOn;
                    ushort bits;

                    if (IsSimulationMode)
                    {
                        await Task.Delay(300);
                        var rnd = new Random();

                        // 温度:开则向设定值一阶逼近,关则向室温回落
                        if (_simTempOn) _simExtTemp += (_simTempSv - _simExtTemp) * 0.12 + (rnd.NextDouble() - 0.5) * 0.4;
                        else _simExtTemp += (25 - _simExtTemp) * 0.05 + (rnd.NextDouble() - 0.5) * 0.2;

                        // 转速:开则逼近设定,关则减速停机
                        if (_simSpeedOn) _simSpeed += (_simSpeedSv - _simSpeed) * 0.35 + (rnd.NextDouble() - 0.5) * 6;
                        else _simSpeed += (0 - _simSpeed) * 0.4;
                        if (_simSpeed < 1) _simSpeed = 0;

                        extTemp = Math.Round(_simExtTemp, 1);
                        intTemp = Math.Round(_simExtTemp + 3.5, 1);   // 内置略高于外置
                        tempSv = _simTempSv;
                        speedMeasured = (int)Math.Round(_simSpeed);
                        speedSv = (int)Math.Round(_simSpeedSv);
                        hourSv = _simHour; minuteSv = _simMinute; secondSv = _simSecond;
                        timerH = 0; timerM = 0; timerS = 0;
                        tempMax = 600; tempMin = 0;
                        tempOn = _simTempOn; speedOn = _simSpeedOn;
                        bits = (ushort)(_simTempOn ? 0x0040 : 0x0000);   // .6 加热输出
                        InfoLog($"模拟模式获取所有数据:外置 {extTemp}℃ 内置 {intTemp}℃ 转速 {speedMeasured} RPM");
                    }
                    else
                    {
                        short[] regs = await SendAndParseInt16ArrayAsync(
                            BuildReadCmd(ReadBlockStart, ReadBlockCount), ReadBlockCount, CancellationToken.None);

                        // regs[i] 对应寄存器 (ReadBlockStart + i),即 0x0002 + i
                        bits = (ushort)regs[0x0002 - ReadBlockStart];
                        tempMax = regs[0x0003 - ReadBlockStart] / TempScale;
                        tempMin = regs[0x0004 - ReadBlockStart] / TempScale;
                        extTemp = regs[0x0005 - ReadBlockStart] / TempScale;   // 可为负,short 已带符号
                        intTemp = regs[0x0006 - ReadBlockStart] / TempScale;
                        timerH = regs[0x0007 - ReadBlockStart];
                        timerM = regs[0x0008 - ReadBlockStart];
                        timerS = regs[0x0009 - ReadBlockStart];
                        speedMeasured = regs[0x000A - ReadBlockStart];
                        tempSv = regs[0x000B - ReadBlockStart] / TempScale;
                        hourSv = regs[0x000C - ReadBlockStart];
                        minuteSv = regs[0x000D - ReadBlockStart];
                        secondSv = regs[0x000E - ReadBlockStart];
                        speedSv = regs[0x000F - ReadBlockStart];
                        tempOn = regs[0x0010 - ReadBlockStart] != 0;
                        speedOn = regs[0x0011 - ReadBlockStart] != 0;

                        InfoLog($"获取所有数据:外置 {extTemp}℃ 内置 {intTemp}℃ 转速 {speedMeasured} RPM " +
                                $"温度设定 {tempSv}℃ 速度设定 {speedSv} RPM 温度开关 {tempOn} 速度开关 {speedOn} 状态位 0x{bits:X4}");
                    }

                    // 把本轮读数推给画布上的自定义控件(仅变化时推,避免刷屏)
                    PushReadingsToUI(extTemp, tempSv, speedMeasured, speedSv, tempOn, speedOn);

                    return new DeviceParameters()
                    {
                        Variables = new ObservableCollection<ParameterBase>()
                        {
                            new NumberParameter("ExternalTemperature", -30, 430, extTemp, "外置温度", true, "℃"),
                            new NumberParameter("InternalTemperature", -30, 630, intTemp, "内置温度", true, "℃"),
                            new NumberParameter("SpeedMeasured", 0, 6000, speedMeasured, "速度测量值", true, "RPM"),
                            new NumberParameter("TemperatureSV", 0, 600, tempSv, "温度设定值", true, "℃"),
                            new NumberParameter("SpeedSV", 0, 6000, speedSv, "速度设定值", true, "RPM"),
                            new NumberParameter("TimeHourSV", 0, 99, hourSv, "时间设定-小时", true, "小时"),
                            new NumberParameter("TimeMinuteSV", 0, 59, minuteSv, "时间设定-分钟", true, "分钟"),
                            new NumberParameter("TimeSecondSV", 0, 59, secondSv, "时间设定-秒", true, "秒"),
                            new NumberParameter("TimerHour", 0, 99, timerH, "计时-小时", true, "小时"),
                            new NumberParameter("TimerMinute", 0, 59, timerM, "计时-分钟", true, "分钟"),
                            new NumberParameter("TimerSecond", 0, 59, timerS, "计时-秒", true, "秒"),
                            new NumberParameter("TempMaxSetting", 0, 600, tempMax, "温度最大设定值", true, "℃"),
                            new NumberParameter("TempMinSetting", 0, 600, tempMin, "温度最小设定值", true, "℃"),
                            new BooleanParameter("TemperatureSwitch", tempOn, "温度开关状态", true),
                            new BooleanParameter("SpeedSwitch", speedOn, "速度开关状态", true),
                            new BooleanParameter("ExtTempAlarm", (bits & 0x0001) != 0, "外置温度异常", true),
                            new BooleanParameter("IntTempAlarm", (bits & 0x0002) != 0, "内置温度异常", true),
                            new BooleanParameter("UpperDeviationAlarm", (bits & 0x0004) != 0, "上偏差报警", true),
                            new BooleanParameter("SensorFallOffAlarm", (bits & 0x0008) != 0, "传感器脱落报警", true),
                            new BooleanParameter("IntTempProtect", (bits & 0x0010) != 0, "内置温度保护", true),
                            new BooleanParameter("MotorHallFault", (bits & 0x0020) != 0, "电机霍尔故障", true),
                            new BooleanParameter("HeatingOutput", (bits & 0x0040) != 0, "加热输出中", true),
                            new BooleanParameter("TimerFinished", (bits & 0x0080) != 0, "定时结束", true)
                        }
                    };
                })
            });
        }

        #region UI 双向联动(设备 ↔ 画布自定义控件 MagneticStirrerControl)

        /// <summary>设备状态变化事件(设备 → UI)。</summary>
        public event EventHandler<DeviceControlStateChangedEventArgs> ControlStateChanged;

        private void RaiseControlStateChanged(string stateName, object stateValue)
        {
            try
            {
                ControlStateChanged?.Invoke(this, new DeviceControlStateChangedEventArgs
                {
                    DeviceId = DeviceId,
                    ComId = ComId,
                    StateName = stateName,
                    StateValue = stateValue
                });
            }
            catch (Exception ex)
            {
                // UI 侧异常不能影响设备通信
                _logger?.LogWarning("推送状态到 UI 失败:{State} {Message}", stateName, ex.Message);
            }
        }

        /// <summary>把一轮读数推给 UI。只推变化项,避免每 2 秒刷一遍全量。</summary>
        private void PushReadingsToUI(double extTemp, double tempSv, int speedMeasured,
                                     int speedSv, bool tempOn, bool speedOn)
        {
            if (Math.Abs(extTemp - _lastExtTemp) >= 0.05)
            {
                _lastExtTemp = extTemp;
                RaiseControlStateChanged("ExternalTemperature", extTemp);
            }
            if (Math.Abs(tempSv - _lastTempSv) >= 0.05)
            {
                _lastTempSv = tempSv;
                RaiseControlStateChanged("TemperatureSV", tempSv);
            }
            if (speedMeasured != _lastSpeed)
            {
                _lastSpeed = speedMeasured;
                RaiseControlStateChanged("SpeedMeasured", (double)speedMeasured);
            }
            if (speedSv != _lastSpeedSv)
            {
                _lastSpeedSv = speedSv;
                RaiseControlStateChanged("SpeedSV", (double)speedSv);
            }
            if (tempOn != _lastTempOn)
            {
                _lastTempOn = tempOn;
                RaiseControlStateChanged("TemperatureSwitch", tempOn);
            }
            if (speedOn != _lastSpeedOn)
            {
                _lastSpeedOn = speedOn;
                RaiseControlStateChanged("SpeedSwitch", speedOn);
            }
        }

        /// <summary>
        /// 从 UI 接收命令(UI → 设备)。控件上的三个触控键点下来走这里,
        /// 最终仍然只调用「温度控制」「搅拌控制」这两条写指令。
        /// </summary>
        public async Task<CommandExecutionResult> ExecuteUICommandAsync(
            string commandName, Dictionary<string, object> parameters = null)
        {
            try
            {
                InfoLog($"收到UI命令: {commandName}");

                switch (commandName)
                {
                    case "温度控制":
                        {
                            double sv = GetParam(parameters, "TargetTemperature", _lastTempSv);
                            bool onOff = GetParam(parameters, "TemperatureSwitch", !_lastTempOn);
                            return await InvokeCommandFromUIAsync("温度控制", new DeviceParameters()
                            {
                                Variables = new ObservableCollection<ParameterBase>()
                                {
                                    new NumberParameter("TargetTemperature", 0, 600, sv, "温度设定值", true, "℃"),
                                    new BooleanParameter("TemperatureSwitch", onOff, "温度开关", true)
                                }
                            });
                        }

                    case "搅拌控制":
                        {
                            double sv = GetParam(parameters, "TargetSpeed", _lastSpeedSv);
                            bool onOff = GetParam(parameters, "SpeedSwitch", !_lastSpeedOn);
                            double h = GetParam(parameters, "TimeHour", 0.0);
                            double m = GetParam(parameters, "TimeMinute", 0.0);
                            double s = GetParam(parameters, "TimeSecond", 0.0);
                            return await InvokeCommandFromUIAsync("搅拌控制", new DeviceParameters()
                            {
                                Variables = new ObservableCollection<ParameterBase>()
                                {
                                    new NumberParameter("TimeHour", 0, 99, h, "时间设定-小时", true, "小时"),
                                    new NumberParameter("TimeMinute", 0, 59, m, "时间设定-分钟", true, "分钟"),
                                    new NumberParameter("TimeSecond", 0, 59, s, "时间设定-秒", true, "秒"),
                                    new NumberParameter("TargetSpeed", 0, 6000, sv, "速度设定值", true, "RPM"),
                                    new BooleanParameter("SpeedSwitch", onOff, "速度开关", true)
                                }
                            });
                        }

                    case "获取所有数据":
                        return await InvokeCommandFromUIAsync("获取所有数据", new DeviceParameters());

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

        private static bool GetParam(Dictionary<string, object> parameters, string key, bool fallback)
        {
            if (parameters != null && parameters.TryGetValue(key, out var v) && v != null
                && bool.TryParse(v.ToString(), out var b)) return b;
            return fallback;
        }

        /// <summary>当前状态快照,供控件初始化显示。</summary>
        public override DeviceState GetCurrentState()
        {
            return new DeviceState
            {
                States = new Dictionary<string, object>
                {
                    ["ExternalTemperature"] = _lastExtTemp,
                    ["TemperatureSV"] = _lastTempSv,
                    ["SpeedMeasured"] = (double)_lastSpeed,
                    ["SpeedSV"] = (double)_lastSpeedSv,
                    ["TemperatureSwitch"] = _lastTempOn,
                    ["SpeedSwitch"] = _lastSpeedOn
                }
            };
        }

        #endregion

        #region 命令包装(日志 + 异常封装)

        /// <summary>把命令执行体统一包一层:开始/完成/异常日志 + 异常封装成 DeviceCommandExecutionException。</summary>
        private Func<DeviceParameters, Task<DeviceParameters>> AsyncWrapAction(
            string commandName, Func<DeviceParameters, Task<DeviceParameters>> innerAsync)
        {
            return async (DeviceParameters parameters) =>
            {
                _logger.LogInformation($"[磁力搅拌器命令-开始] Device={Name} Command={commandName}");
                var sw = Stopwatch.StartNew();
                try
                {
                    var output = await innerAsync(parameters);
                    sw.Stop();
                    _logger.LogInformation($"[磁力搅拌器命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex, $"[磁力搅拌器命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        #endregion

        #region Modbus RTU 底层(与平台其他 RTU 驱动一致的收发/CRC)

        /// <summary>把工程量换算成寄存器值并限幅到 0~65535。</summary>
        private static ushort ToRegister(double scaledValue)
        {
            int v = (int)Math.Round(scaledValue);
            if (v < 0) v = 0;
            if (v > 0xFFFF) v = 0xFFFF;
            return (ushort)v;
        }

        private static int Clamp(int value, int min, int max)
            => value < min ? min : (value > max ? max : value);

        /// <summary>取当前配置的 Modbus 从机站号 (1~254)。</summary>
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

        /// <summary>获取本设备实例专属的互斥锁(按 DeviceId 隔离,IO 串行化)。</summary>
        private SemaphoreSlim GetInstanceLock()
        {
            var key = Parameters?.GetValue<string>("DeviceId") ?? DeviceId ?? "default";
            return _instanceLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        }

        /// <summary>构造 Modbus RTU 写单寄存器命令 (功能码 0x06),自动加 CRC。</summary>
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

        /// <summary>构造 Modbus RTU 写多寄存器命令 (功能码 0x10),自动加 CRC。寄存器地址须连续。</summary>
        private byte[] BuildWriteMultiCmd(ushort startRegister, ushort[] values)
        {
            if (values == null || values.Length == 0)
                throw new ArgumentException("写多寄存器的数据不能为空", nameof(values));

            int byteCount = values.Length * 2;
            byte[] frame = new byte[7 + byteCount];
            frame[0] = GetSlaveAddress();
            frame[1] = 0x10;
            frame[2] = (byte)(startRegister >> 8);
            frame[3] = (byte)(startRegister & 0xFF);
            frame[4] = (byte)(values.Length >> 8);
            frame[5] = (byte)(values.Length & 0xFF);
            frame[6] = (byte)byteCount;
            for (int i = 0; i < values.Length; i++)
            {
                frame[7 + i * 2] = (byte)(values[i] >> 8);       // 高字节在前(大端)
                frame[8 + i * 2] = (byte)(values[i] & 0xFF);
            }
            return Crc16Modbus.AppendCrc(frame, 0, frame.Length);
        }

        /// <summary>构造 Modbus RTU 读保持寄存器命令 (功能码 0x03),自动加 CRC。</summary>
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

        /// <summary>写命令。自动重试 3 次以应对工业链路偶发抖动。</summary>
        private async Task<bool> WriteAsync(byte[] buffers, int offset, int count, CancellationToken token)
        {
            Exception? lastException = null;
            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    return await WriteOnceAsync(buffers, offset, count, token).ConfigureAwait(false);
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

        private async Task<bool> WriteOnceAsync(byte[] buffers, int offset, int count, CancellationToken token)
        {
            var sem = GetInstanceLock();
            await sem.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using var transport = await GetTransportAsync().ConfigureAwait(false);
                return await transport.SendAsync(buffers, offset, count, token).ConfigureAwait(false);
            }
            finally
            {
                sem.Release();
            }
        }

        /// <summary>发送 FC03 读命令并解析应答中的 N 个 Int16 寄存器(自动重试 3 次)。</summary>
        private async Task<short[]> SendAndParseInt16ArrayAsync(byte[] sendCmd, int expectedCount, CancellationToken token)
        {
            Exception? lastException = null;
            for (int attempt = 1; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    return await SendAndParseInt16ArrayOnceAsync(sendCmd, expectedCount, token).ConfigureAwait(false);
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
                    _logger?.LogError(ex, "Modbus 读取失败(非超时,不重试)");
                    throw;
                }
            }
            throw new TimeoutException($"Modbus 读取失败,{MaxRetries} 次尝试后仍超时", lastException);
        }

        private async Task<short[]> SendAndParseInt16ArrayOnceAsync(byte[] sendCmd, int expectedCount, CancellationToken token)
        {
            var sem = GetInstanceLock();
            await sem.WaitAsync(token).ConfigureAwait(false);
            try
            {
                byte[] recv = new byte[256];
                using var transport = await GetTransportAsync().ConfigureAwait(false);
                int len = await transport.SendAndReceiveAsync(sendCmd, recv, 3000, token).ConfigureAwait(false);

                // 应答最短:站号(1)+功能码(1)+字节数(1)+数据(2×N)+CRC(2)
                int minLen = 5 + expectedCount * 2;
                if (len < minLen)
                {
                    _logger?.LogWarning("应答帧长度不足:{Len} 字节,需要至少 {Min} 字节", len, minLen);
                    return new short[expectedCount];
                }

                byte expectedAddr = GetSlaveAddress();
                if (recv[0] != expectedAddr)
                {
                    _logger?.LogWarning("站号不匹配:期望 0x{Expected:X2},实际 0x{Actual:X2}", expectedAddr, recv[0]);
                    return new short[expectedCount];
                }
                if ((recv[1] & 0x80) != 0)
                {
                    _logger?.LogWarning("Modbus 异常应答,异常码 0x{Code:X2}", recv[2]);
                    return new short[expectedCount];
                }
                if (recv[1] != 0x03)
                {
                    _logger?.LogWarning("功能码不匹配:期望 0x03,实际 0x{Actual:X2}", recv[1]);
                    return new short[expectedCount];
                }

                byte dataLen = recv[2];
                int available = Math.Min(dataLen / 2, expectedCount);
                short[] result = new short[expectedCount];
                for (int i = 0; i < available; i++)
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

        /// <summary>ZLanGateway 模式下的连接实现:读一个寄存器,收到合法应答即视为连通。</summary>
        protected override async Task<bool> OnConnectViaGatewayAsync()
        {
            try
            {
                InfoLog("通过 ZLAN 网关测试连接");
                byte[] sendCmd = BuildReadCmd(RegStatusBits, 1);
                var regs = await SendAndParseInt16ArrayAsync(sendCmd, 1, CancellationToken.None);
                InfoLog($"网关连接成功,状态位 0x{(ushort)regs[0]:X4}");
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
