// ============================================================================
//  PenMeter_BLE.cs
//  笔式酸度-电导率计驱动(BLE 协议,机器类型 0x10)。
//
//  ── 关于 BLE 怎么接进来 ────────────────────────────────────────────
//  协议本身是一条**字节流**:FF | 类型 | 功能码 | 长度 | 数据 | CRC8 | F0。
//  BLE 只是这条字节流的载体,协议内容与载体无关。所以载体做成了可换的
//  (IPenLink),两条路:
//
//    · 「蓝牙直连」(默认)—— 用电脑自带的蓝牙,走 Windows 原生 GATT。
//      不需要任何额外硬件,见 BleProxyLink.cs。
//    · 「透传串口」—— USB 口插一只 BLE 主机透传模块,模块枚举出一个 COM 口,
//      收到什么原样吐什么。给"电脑没有蓝牙 / 蓝牙被 IT 策略禁用"的场合留的退路。
//
//  蓝牙那条路不在本进程里做,而是启动独立的 MaxChemical.Ble.Proxy.exe,
//  跟它用 stdio 说话。原因是本工程必须保持 net8.0-windows:改成 win10 目标框架
//  会引入两个 WinRT 依赖,而驱动 dll 是被 MEF 从 Plugins\ 单独扫出来加载的,
//  依赖到不了旁边,加载会**静默失败** —— 设备库里直接没有这台设备且不报错。
//  详见 BleProxyLink.cs 与 PenMeter_BLE.csproj 顶部。
//
//  ── 与触摸屏酸度计驱动的异同 ──────────────────────────────────────
//  同:都是仪器**主动推**,上位机没有"读一次"的命令。所以同样是
//      "后台线程收帧 → 覆盖快照 → 读命令抄快照"。
//  异:字节序相反(这台是**大端**),且多了 CRC8 校验;
//      另外这台**没有握手包**,连上与否只能看有没有收到合法帧。
//
//  ── 下发设置为什么要先进配置模式 ──────────────────────────────────
//  0x81 一次性下发 21 个字节的完整配置,里面混着"恢复出厂""屏幕自检"这类
//  一碰就动作的项。凭空造这 21 个字节等于把仪器配坏,所以本驱动规定:
//  必须先收到仪器推来的 0x02(仪器进入配置模式时才推),以它为底稿改一项再发。
//  没有底稿就直接拒绝,并在错误信息里告诉操作员该怎么做。
// ============================================================================

using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Text;

namespace PenMeter_BLE
{
    [Export(typeof(IDevice))]
    public class PenMeter_BLE : Device, IDeviceScreen
    {
        private readonly ILogService _logger;

        /// <summary>连上之后等第一帧的上限。仪器在测量模式下推得很勤,3 秒足够。</summary>
        private const int FirstFrameTimeoutMs = 3000;

        /// <summary>超过这个时间没收到任何合法帧就判定掉线(笔可能自动关机了)。</summary>
        private const int StaleThresholdMs = 8000;

        /// <summary>「连接方式」的两个取值。</summary>
        private const string ConnBluetooth = "蓝牙直连";
        private const string ConnSerial = "透传串口";

        private readonly object _linkLock = new object();
        private IPenLink? _link;

        /// <summary>拼帧缓冲区。BLE 的通知回调可能并发进来,所有操作都在 _rxLock 里。</summary>
        private readonly object _rxLock = new object();
        private readonly byte[] _rxBuf = new byte[4096];
        private int _rxLen;

        // ── 各功能码各自一份快照。读命令只抄快照,不碰串口。────────────
        private volatile PenMeasure? _measure;
        private volatile PenSettings? _settings;
        private volatile PenCalibration? _calibration;
        private int _workMode = 1;      // 0关机 1测量 2配置 4校准
        private int _alarm;             // 0x05 报警信号
        private int _errorCode;         // 0x06 错误编号

        private long _lastFrameTicks;
        private volatile TaskCompletionSource<bool>? _firstFrame;

        public string ScreenKey => "PenMeterBle";

        public PenMeter_BLE()
        {
            _logger = LogManager.GetLogger<PenMeter_BLE>();

            DeviceId = "PEN0001";
            Name = "笔式酸度-电导率计";
            Manufacturer = "笔式多参数酸度电导率仪(BLE 透传)";
            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/PenMeter_BLE.png";
            Category = DeviceCategories.InlineDetection;
            ComId = "PEN0001";

            AllowedRegions = new List<RegionType>
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            Parameters.Variables.Add(new StringParameter("DeviceId", "PEN0001", "设备实例编号")
            {
                Options = new ObservableCollection<string> { "PEN0001", "PEN0002", "PEN0003", "PEN0004" }
            });

            // ── 载体选择 ────────────────────────────────────────────
            // 注意参数名是「连接方式」而不是「通信方式」:后者被基类用来解析
            // CommunicationMode 枚举,往里塞 "蓝牙直连" 会解析失败并退回全局默认值
            // (可能是 ModbusTcp),于是连接被路由到根本不适用的分支上。
            Parameters.Variables.Add(new StringParameter("连接方式", ConnBluetooth, "默认用电脑自带蓝牙直连;没有蓝牙时才选透传串口")
            {
                Options = new ObservableCollection<string> { ConnBluetooth, ConnSerial }
            });

            // ── 蓝牙直连用 ──────────────────────────────────────────
            Parameters.Variables.Add(new StringParameter("蓝牙设备名", "",
                "仪器的蓝牙广播名(可只填一部分,不区分大小写)。不确定就先执行「扫描蓝牙设备」命令"));

            Parameters.Variables.Add(new StringParameter("蓝牙地址", "",
                "形如 AA:BB:CC:DD:EE:FF。填了就按地址连,比按名字可靠 —— 现场有多支同型号笔时尤其如此"));

            Parameters.Variables.Add(new NumberParameter("扫描超时", 2, 60, 10,
                "按名字找设备时的广播扫描上限", false, "s"));

            // 手册只规定了帧格式,没写该连哪个 GATT 服务/特征(那是蓝牙模块厂定的)。
            // 驱动会自动识别常见透传模块并按能力兜底,识别不了时才需要手工填这三项,
            // 照着日志里打印出来的服务/特征清单抄即可。短 UUID(如 FFE1)也认。
            Parameters.Variables.Add(new StringParameter("服务UUID", "", "留空=自动识别。手册未规定,由蓝牙模块决定"));
            Parameters.Variables.Add(new StringParameter("写特征UUID", "", "留空=自动识别"));
            Parameters.Variables.Add(new StringParameter("通知特征UUID", "", "留空=自动识别"));

            // ── 透传串口用 ──────────────────────────────────────────
            Parameters.Variables.Add(new StringParameter("串口号", SerialPortOptions.DefaultPort(), "BLE 透传模块枚举出的 COM 口")
            {
                Options = SerialPortOptions.CreateOptions()
            });

            // 波特率是**透传模块串口侧**的速率,由模块自己决定(常见 9600 与 115200),
            // 不是仪器规定的 —— 所以这里给出来让人选,而不是像触摸屏酸度计那样写死。
            Parameters.Variables.Add(new StringParameter("波特率", "115200", "透传模块串口侧速率,按模块说明书填")
            {
                Options = new ObservableCollection<string> { "9600", "19200", "38400", "57600", "115200", "230400" }
            });

            // 只留 Direct,压住基类的全局默认通信方式(否则可能被路由到 ModbusTcp 分支)
            Parameters.Variables.Add(new StringParameter("通信方式", "Direct", "本仪器不经 PLC/网关,固定直连")
            {
                Options = new ObservableCollection<string> { "Direct" }
            });

            InitializeCommands();
        }

        public override bool SupportsZLanGateway => false;

        #region 可监控参数

        public override IReadOnlyList<CollectableParameter> CollectableParameters => new[]
        {
            // 单位随测量模式变(pH / mV / uS/cm ...),所以这里不写死单位,
            // 由 VALUE_UNIT 那个字符串输出告诉界面当前是什么单位。
            new CollectableParameter("VALUE",   "测量值", "", "获取测量数据", "VALUE", isKey: true),
            new CollectableParameter("TEMP",    "温度",   "℃", "获取测量数据", "TEMP"),
            new CollectableParameter("BATTERY", "电量",   "%", "获取测量数据", "BATTERY"),
        };

        public override int AutoCollectIntervalMs => 1000;

        #endregion

        #region 连接管理

        protected override async Task<bool> OnConnectAsync()
        {
            CloseInternal();

            IPenLink link;
            try
            {
                link = CreateLink();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[笔式酸度计] 创建连接失败");
                return false;
            }

            link.BytesReceived += OnBytes;
            link.Closed += OnLinkClosed;
            lock (_linkLock) _link = link;

            // 协议里没有握手包,只能靠"收到一帧合法数据"来确认对面确实是这台仪器。
            // 这一步必须在 OpenAsync 之前挂上,因为蓝牙订阅成功后数据可能立刻就到。
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _firstFrame = tcs;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            bool opened;
            try
            {
                opened = await link.OpenAsync(cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[笔式酸度计] 打开 {link.Description} 异常");
                CloseInternal();
                return false;
            }

            if (!opened)
            {
                _logger.LogError($"[笔式酸度计] 连接失败({link.Description}):{link.LastError}");
                CloseInternal();
                return false;
            }

            // 链路通了不等于对面是这台仪器:
            //   · 蓝牙:订阅成功但连错了设备,就永远收不到 0xFF 开头的帧;
            //   · 串口:透传模块没和笔配对时,COM 口照样开得了,只是没数据。
            // 两种情况都必须报连接失败,不能假装连上了。
            var finished = await Task.WhenAny(tcs.Task, Task.Delay(FirstFrameTimeoutMs)).ConfigureAwait(false);
            _firstFrame = null;

            if (finished == tcs.Task)
            {
                _logger.LogInformation($"[笔式酸度计] 已连接({link.Description})");
                return true;
            }

            _logger.LogError(
                $"[笔式酸度计] {link.Description} 上 {FirstFrameTimeoutMs}ms 内没收到合法数据帧。" +
                (IsBluetooth
                    ? "请确认:笔已开机(不在关机模式)、离电脑不太远、没有被手机 App 占用着;" +
                      "若「蓝牙设备名」填的不是这支笔,请先执行「扫描蓝牙设备」命令看看附近有哪些。"
                    : "请确认:笔已开机、透传模块已与笔配对、串口号与波特率选对。"));
            CloseInternal();
            return false;
        }

        private bool IsBluetooth =>
            !string.Equals(Parameters.GetValue<string>("连接方式"), ConnSerial, StringComparison.Ordinal);

        /// <summary>按「连接方式」造对应的字节管道。</summary>
        private IPenLink CreateLink()
        {
            if (!IsBluetooth)
            {
                var portName = Parameters.GetValue<string>("串口号");
                if (string.IsNullOrWhiteSpace(portName))
                    throw new InvalidOperationException("「连接方式」选了透传串口,但没有配置串口号。");

                if (!int.TryParse(Parameters.GetValue<string>("波特率"), out int baud) || baud <= 0)
                    baud = 115200;

                return new SerialPenLink(portName, baud);
            }

            int scanSec = (int)(Parameters.GetNullableValue<double>("扫描超时") ?? 10);
            return new BleProxyLink(
                Parameters.GetValue<string>("蓝牙设备名") ?? "",
                Parameters.GetValue<string>("蓝牙地址") ?? "",
                Parameters.GetValue<string>("服务UUID") ?? "",
                Parameters.GetValue<string>("写特征UUID") ?? "",
                Parameters.GetValue<string>("通知特征UUID") ?? "",
                Math.Clamp(scanSec, 2, 60) * 1000,
                msg => _logger.LogInformation($"[笔式酸度计/蓝牙] {msg}"));
        }

        /// <summary>底层链路断了(蓝牙断连 / 串口拔了)。</summary>
        private void OnLinkClosed()
        {
            _logger.LogWarning("[笔式酸度计] 链路已断开");
            // 这里只记日志,不改 ConnectionStatus:基类的心跳会调 OnCheckConnectionAsync,
            // 那边发现 _link 不可用或长时间没帧就会把状态打成掉线。
            // 在回调线程里直接改状态容易和正在进行的连接/断开操作打架。
        }

        protected override Task<bool> OnDisconnectAsync()
        {
            CloseInternal();
            return Task.FromResult(true);
        }

        protected override Task<bool> OnCheckConnectionAsync()
        {
            lock (_linkLock)
            {
                if (_link is null || !_link.IsOpen) return Task.FromResult(false);
            }

            var last = new DateTime(Volatile.Read(ref _lastFrameTicks), DateTimeKind.Utc);
            bool alive = (DateTime.UtcNow - last).TotalMilliseconds < StaleThresholdMs;
            if (!alive)
                _logger.LogWarning($"[笔式酸度计] 已超过 {StaleThresholdMs}ms 没收到帧(笔可能已自动关机)");
            return Task.FromResult(alive);
        }

        protected override void OnShutdown()
        {
            CloseInternal();
            base.OnShutdown();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) CloseInternal();
            base.Dispose(disposing);
        }

        private void CloseInternal()
        {
            IPenLink? link;
            lock (_linkLock) { link = _link; _link = null; }

            if (link is not null)
            {
                link.BytesReceived -= OnBytes;
                link.Closed -= OnLinkClosed;
                try { link.Dispose(); } catch { }
            }

            lock (_rxLock) _rxLen = 0;

            _measure = null;
            _settings = null;
            _calibration = null;
            _firstFrame = null;
        }

        #endregion

        #region 拼帧

        /// <summary>
        /// 收到一片字节。载体给什么就拼什么,与是蓝牙还是串口无关。
        ///
        /// 蓝牙尤其需要这一层:BLE 默认 MTU 只有 23,一次通知最多带 20 字节负载,
        /// 而本协议最长的帧是 27 字节 —— **一帧必然被拆成两次通知送到**。
        /// 反过来仪器推得快时也会几帧粘在一片里。所以这里维护一个滚动缓冲区,
        /// 反复取帧、把剩下的挪回开头,半帧、粘包、垃圾字节全靠 TryReadFrame 的
        /// 重同步兜住。
        ///
        /// **可能被并发调用**(BLE 的 ValueChanged 跑在线程池上),所以整段加锁。
        /// </summary>
        private void OnBytes(byte[] chunk)
        {
            if (chunk is null || chunk.Length == 0) return;

            var frames = new List<PenFrame>(4);

            lock (_rxLock)
            {
                // 最长的帧是 6 + 21 = 27 字节,4KB 还装不下说明收的全是垃圾
                if (_rxLen + chunk.Length > _rxBuf.Length)
                {
                    _logger.LogWarning("[笔式酸度计] 接收缓冲溢出,丢弃并重新同步");
                    _rxLen = 0;
                    if (chunk.Length > _rxBuf.Length) return;
                }

                Buffer.BlockCopy(chunk, 0, _rxBuf, _rxLen, chunk.Length);
                _rxLen += chunk.Length;

                int offset = 0;
                while (offset < _rxLen)
                {
                    if (!PenMeterProtocol.TryReadFrame(_rxBuf, offset, _rxLen - offset, out var frame, out int consumed))
                    {
                        if (consumed == 0) break;      // 数据不够,等下一片
                        offset += consumed;            // 重同步
                        continue;
                    }

                    offset += consumed;
                    if (frame is not null) frames.Add(frame);
                }

                if (offset > 0)
                {
                    _rxLen -= offset;
                    if (_rxLen > 0) Buffer.BlockCopy(_rxBuf, offset, _rxBuf, 0, _rxLen);
                }
            }

            // 派发放在锁外:Dispatch 会记日志、触发事件,拿着接收锁做这些事
            // 等于让日志 IO 卡住下一片数据的拼接。
            foreach (var f in frames) Dispatch(f);
        }

        private void Dispatch(PenFrame frame)
        {
            Volatile.Write(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
            _firstFrame?.TrySetResult(true);

            // 手册目前只定义了 0x10 一种机器类型,但备注里写着"(2) ...",
            // 说明以后会有别的型号。不是本机型的帧直接忽略,免得把别人的数据
            // 当成自己的填进快照。
            if (frame.MachineType != PenMeterProtocol.MachineTypePen) return;

            try
            {
                switch (frame.Func)
                {
                    case PenFunc.MeasureData:
                        _measure = PenMeterProtocol.ParseMeasure(frame.Data);
                        break;

                    case PenFunc.ModeData:
                        // 这一帧只带模式/电量/校准信息,测量值沿用上一帧
                        _measure = PenMeterProtocol.ParseModeData(frame.Data, _measure);
                        break;

                    case PenFunc.SettingData:
                        _settings = PenSettings.Parse(frame.Data);
                        break;

                    case PenFunc.CalibrationData:
                        _calibration = PenMeterProtocol.ParseCalibration(frame.Data);
                        break;

                    case PenFunc.WorkModeChanged:
                        if (frame.Data.Length >= 1)
                        {
                            Volatile.Write(ref _workMode, frame.Data[0]);
                            _logger.LogInformation(
                                $"[笔式酸度计] 工作模式切换为 {PenMeterProtocol.WorkModeText(frame.Data[0])}");
                        }
                        break;

                    case PenFunc.AlarmSignal:
                        if (frame.Data.Length >= 1) Volatile.Write(ref _alarm, frame.Data[0]);
                        break;

                    case PenFunc.ErrorCode:
                        if (frame.Data.Length >= 1)
                        {
                            Volatile.Write(ref _errorCode, frame.Data[0]);
                            _logger.LogWarning($"[笔式酸度计] 仪器上报错误编号 {frame.Data[0]}");
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[笔式酸度计] 功能码 0x{frame.Func:X2} 解析失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 发一帧。**必须 await,不要改回同步阻塞** —— 蓝牙那条路径下,
        /// 命令处理链有可能就跑在 UI 线程上,在那儿阻塞等 WinRT 异步操作
        /// 轻则界面冻住、重则死锁。理由写在 IPenLink.WriteAsync 上。
        /// </summary>
        private async Task<bool> WriteRawAsync(byte[] frame)
        {
            IPenLink? link;
            lock (_linkLock) link = _link;

            if (link is null || !link.IsOpen)
            {
                _logger.LogWarning("[笔式酸度计] 链路未打开,命令没有发出");
                return false;
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                if (await link.WriteAsync(frame, cts.Token).ConfigureAwait(false)) return true;
                _logger.LogError($"[笔式酸度计] 发送失败:{link.LastError}");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[笔式酸度计] 发送异常");
                return false;
            }
        }

        #endregion

        #region 命令

        private void InitializeCommands()
        {
            // ══ 读 ══════════════════════════════════════════════════
            Commands.Add(new DeviceCommand
            {
                Name = "获取测量数据",
                HelpText = "读取仪器最近一次推送的测量数据(测量值/单位/温度/状态/模式/电量/校准信息)",
                OutputParameters = new DeviceParameters { Variables = BuildMeasureOutputs() },
                AsyncAction = AsyncWrapAction("获取测量数据", _ => Task.FromResult(ReadMeasure()))
            });

            Commands.Add(new DeviceCommand
            {
                Name = "获取设置数据",
                HelpText = "读取仪器最近一次推送的配置(仅当仪器进入配置模式时才会推送)",
                OutputParameters = new DeviceParameters { Variables = BuildSettingOutputs() },
                AsyncAction = AsyncWrapAction("获取设置数据", _ => Task.FromResult(ReadSettings()))
            });

            Commands.Add(new DeviceCommand
            {
                Name = "获取校准数据",
                HelpText = "读取仪器最近一次推送的校准数据(仅当仪器进入校准模式时才会推送)",
                OutputParameters = new DeviceParameters { Variables = BuildCalibrationOutputs() },
                AsyncAction = AsyncWrapAction("获取校准数据", _ => Task.FromResult(ReadCalibration()))
            });

            Commands.Add(new DeviceCommand
            {
                Name = "获取工作状态",
                HelpText = "读取当前工作模式、报警状态与错误编号",
                OutputParameters = new DeviceParameters { Variables = BuildStatusOutputs() },
                AsyncAction = AsyncWrapAction("获取工作状态", _ => Task.FromResult(ReadStatus()))
            });

            // ══ 配对辅助 ═════════════════════════════════════════════
            // 手册没写笔的蓝牙广播名叫什么,厂家也可能改。与其让人去手机上翻,
            // 不如直接扫一遍列出来 —— 把结果里的名字或地址填回设备参数即可。
            Commands.Add(new DeviceCommand
            {
                Name = "扫描蓝牙设备",
                HelpText = "扫描附近的 BLE 设备并列出名称、地址与信号强度,用于确定「蓝牙设备名」该填什么",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TimeoutSec", 2, 60, 8, "扫描时长", false, "s")
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("Count", 0, 1000, 0, "扫到的设备数", true),
                        new StringParameter("Devices", "", "每行:名称 / 地址 / 信号强度"),
                        new BooleanParameter("Success", false, "是否成功")
                    }
                },
                AsyncAction = AsyncWrapAction("扫描蓝牙设备", ScanBluetoothAsync)
            });

            // ══ 直接下发的控制命令 ═══════════════════════════════════
            AddRawCommand("切换测量模式",
                "0=PH,1=ORP,2=Cond,3=TDS,4=Sal,5=Res", 0, 5,
                v => PenMeterProtocol.BuildSetMeasureMode((byte)v));

            AddRawCommand("切换工作模式",
                "0=关机,1=测量模式,2=配置模式,4=校准模式(改设置前需先切到配置模式)", 0, 4,
                v => PenMeterProtocol.BuildSetWorkMode((byte)v));

            AddRawCommand("数据保持",
                "0=取消保持,1=保持当前读数", 0, 1,
                v => PenMeterProtocol.BuildHold(v >= 0.5));

            AddRawCommand("确认校准",
                "0=不确认,1=确认当前校准值", 0, 1,
                v => PenMeterProtocol.BuildConfirmCalibration(v >= 0.5));

            AddRawCommand("下发报警",
                "0=不报警,1=超上限,2=超下限", 0, 2,
                v => PenMeterProtocol.BuildAlarm((byte)v));

            // ══ 配置项:都走 0x81,以仪器推来的当前配置为底稿改一项 ══
            AddSettingCommand("设置温度单位", "0=℃,1=℉", 0, 1,
                (s, v) => s.TempUnit = (byte)v);

            AddSettingCommand("设置自动锁定", "0=不锁定;5~20=锁定开启并设定秒数", 0, 20,
                (s, v) => s.AutoLock = (byte)v, unit: "s");

            AddSettingCommand("设置自动背光", "0=关闭;1~8=开启并设定分钟数", 0, 8,
                (s, v) => s.AutoBacklight = (byte)v, unit: "min");

            AddSettingCommand("设置自动关机", "0=关闭;10~20=开启并设定分钟数", 0, 20,
                (s, v) => s.AutoPowerOff = (byte)v, unit: "min");

            AddSettingCommand("设置PH缓冲液", "0=CH 国标,1=USA,2=NIST", 0, 2,
                (s, v) => s.PhBuffer = (byte)v);

            AddSettingCommand("设置PH分辨率", "0=1,1=0.1,2=0.01", 0, 2,
                (s, v) => s.PhResolution = (byte)v);

            AddSettingCommand("设置电导率参比温度", "15~30 ℃", 15, 30,
                (s, v) => s.CondRefTemp = (byte)v, unit: "℃");

            AddSettingCommand("设置电导率温补系数", "0.00~9.99 %/℃", 0, 9.99,
                (s, v) =>
                {
                    int raw = (int)Math.Round(v * 100);          // 协议里是 0~999,/100
                    raw = Math.Clamp(raw, 0, 999);
                    s.CondTempCoefHi = (byte)((raw >> 8) & 0xFF); // 大端:高字节在前
                    s.CondTempCoefLo = (byte)(raw & 0xFF);
                }, unit: "%", decimals: 2);

            AddSettingCommand("设置温度补偿值", "电导率/PH 的手动温度补偿值,0~99 ℃", 0, 99,
                (s, v) => s.CondPhTempComp = (byte)v, unit: "℃");

            AddSettingCommand("设置电导率标准液", "0=CH 国标,1=USA", 0, 1,
                (s, v) => s.CondStdSolution = (byte)v);

            AddSettingCommand("设置电导率电极常数", "0.80~1.20", 0.80, 1.20,
                (s, v) => s.CondCellConstant = (byte)Math.Clamp(Math.Round(v * 100), 80, 120),
                decimals: 2);

            AddSettingCommand("设置TDS系数", "0.40~1.00", 0.40, 1.00,
                (s, v) => s.TdsCoefficient = (byte)Math.Clamp(Math.Round(v * 100), 40, 100),
                decimals: 2);

            AddSettingCommand("设置盐度单位", "0=ppt,1=mg/L", 0, 1,
                (s, v) => s.SalinityUnit = (byte)v);

            // 校准提醒是"小时 + 天"两个字节,单独做成双输入命令
            AddCalibrationRemindCommand("设置PH校准提醒",
                (s, h, d) => { s.PhRemindHour = h; s.PhRemindDay = d; });

            AddCalibrationRemindCommand("设置电导率校准提醒",
                (s, h, d) => { s.CondRemindHour = h; s.CondRemindDay = d; });

            // ── 三个"一碰就动作"的项。见 BuildSettingsBase() 的注释:
            //    平时下发任何配置都会把它们强制清零,只有这三条命令会置 1。
            AddSettingTrigger("PH恢复出厂", "PH 参数恢复出厂设置(不可撤销)",
                s => s.PhFactoryReset = 1);

            AddSettingTrigger("电导率恢复出厂", "电导率参数恢复出厂设置(不可撤销)",
                s => s.CondFactoryReset = 1);

            AddSettingTrigger("屏幕自检", "让仪器执行一次屏幕自检",
                s => s.ScreenSelfTest = 1);
        }

        #region 输出参数定义

        private static ObservableCollection<ParameterBase> BuildMeasureOutputs() => new()
        {
            new NumberParameter("VALUE",   -100000, 100000, 0, "测量值",   true),
            new StringParameter("VALUE_UNIT", "pH", "测量值单位"),
            new NumberParameter("TEMP",       -100, 300,    0, "温度",     true, "℃"),
            new StringParameter("TEMP_UNIT",  "℃", "温度单位"),

            new BooleanParameter("STABLE",     false, "读数是否稳定", true),
            new BooleanParameter("HOLD",       false, "是否处于锁定/保持状态", true),
            new BooleanParameter("OVER_HIGH",  false, "是否超上限", true),
            new BooleanParameter("OVER_LOW",   false, "是否超下限", true),

            new NumberParameter("MODE", 0, 5, 0, "测量模式码 0PH/1ORP/2Cond/3TDS/4Sal/5Res", true),
            new StringParameter("MODE_TEXT", "PH", "测量模式"),

            new NumberParameter("BATTERY", 0, 100, 0, "电量", true, "%"),

            new NumberParameter("CAL_FLAGS", 0, 31, 0, "已校准点位图 L1=1 M=2 H1=4 L2=8 H2=16", true),
            new StringParameter("CAL_TEXT", "未校准", "已校准点"),

            new NumberParameter("ZERO_POTENTIAL", -2000, 2000, 0, "PH 校准零电位", true, "mV"),
            new NumberParameter("SLOPE",              0, 200,  0, "PH 校准斜率",   true, "%"),

            new BooleanParameter("Success", false, "是否取到有效数据", true),
        };

        private static ObservableCollection<ParameterBase> BuildSettingOutputs() => new()
        {
            new NumberParameter("TEMP_UNIT",       0, 1,   0, "温度单位 0=℃ 1=℉", true),
            new NumberParameter("AUTO_LOCK",       0, 20,  0, "自动锁定 0=不锁定 5~20=秒", true, "s"),
            new NumberParameter("AUTO_BACKLIGHT",  0, 8,   0, "自动背光 0=关闭 1~8=分钟", true, "min"),
            new NumberParameter("AUTO_POWEROFF",   0, 20,  0, "自动关机 0=关闭 10~20=分钟", true, "min"),
            new NumberParameter("PH_BUFFER",       0, 2,   0, "PH 缓冲液 0=CH 1=USA 2=NIST", true),
            new NumberParameter("PH_RESOLUTION",   0, 2,   0, "PH 分辨率 0=1 1=0.1 2=0.01", true),
            new NumberParameter("PH_REMIND_HOUR",  0, 255, 0, "PH 校准提醒 小时", true, "h"),
            new NumberParameter("PH_REMIND_DAY",   0, 255, 0, "PH 校准提醒 天", true, "d"),
            new NumberParameter("COND_REF_TEMP",   0, 255, 25, "电导率参比温度", true, "℃"),
            new NumberParameter("COND_TEMP_COEF",  0, 9.99, 0, "电导率温度补偿系数", true, "%"),
            new NumberParameter("TEMP_COMP",       0, 99,  0, "电导率/PH 温度补偿值", true, "℃"),
            new NumberParameter("COND_STD",        0, 1,   0, "电导率标准液 0=CH 1=USA", true),
            new NumberParameter("CELL_CONSTANT",   0, 2,   1, "电导率电极常数", true),
            new NumberParameter("COND_REMIND_HOUR",0, 255, 0, "电导率校准提醒 小时", true, "h"),
            new NumberParameter("COND_REMIND_DAY", 0, 255, 0, "电导率校准提醒 天", true, "d"),
            new NumberParameter("TDS_FACTOR",      0, 2,   0, "TDS 系数", true),
            new NumberParameter("SALINITY_UNIT",   0, 1,   0, "盐度单位 0=ppt 1=mg/L", true),
            new BooleanParameter("Success", false, "是否取到有效配置", true),
        };

        private static ObservableCollection<ParameterBase> BuildCalibrationOutputs() => new()
        {
            new NumberParameter("CAL_TYPE", 0, 1, 0, "校准类型 0=PH 1=Cond", true),
            new StringParameter("CAL_TYPE_TEXT", "PH 校准", "校准类型"),
            new NumberParameter("MEASURED", -100000, 100000, 0, "当前测量值", true),
            new StringParameter("MEASURED_UNIT", "pH", "测量值单位"),
            new NumberParameter("STANDARD", -100000, 100000, 0, "标准液标称值", true),
            new StringParameter("STANDARD_UNIT", "pH", "标准值单位"),
            new BooleanParameter("STABLE", false, "读数是否稳定", true),
            new NumberParameter("CAL_FLAGS", 0, 31, 0, "已校准点位图", true),
            new StringParameter("CAL_TEXT", "未校准", "已校准点"),
            new BooleanParameter("CONFIRMED", false, "仪器是否已确认本次校准", true),
            new NumberParameter("ERROR_CODE", 0, 255, 0, "校准错误编号", true),
            new BooleanParameter("Success", false, "是否取到有效校准数据", true),
        };

        private static ObservableCollection<ParameterBase> BuildStatusOutputs() => new()
        {
            new NumberParameter("WORK_MODE", 0, 4, 1, "工作模式码 0关机/1测量/2配置/4校准", true),
            new StringParameter("WORK_MODE_TEXT", "测量模式", "工作模式"),
            new NumberParameter("ALARM", 0, 2, 0, "报警状态 0=无 1=超上限 2=超下限", true),
            new NumberParameter("ERROR_CODE", 0, 255, 0, "仪器错误编号", true),
            new BooleanParameter("Success", true, "是否成功", true),
        };

        #endregion

        #region 读命令实现

        private DeviceParameters ReadMeasure()
        {
            var o = new DeviceParameters { Variables = BuildMeasureOutputs() };

            PenMeasure? m = IsSimulationMode ? MakeSimulatedMeasure() : _measure;
            if (m is null)
            {
                o.SetValue("Success", false);
                return o;
            }

            o.SetValue("VALUE", Math.Round(m.Value, 2));
            o.SetValue("VALUE_UNIT", m.UnitText);
            o.SetValue("TEMP", Math.Round(m.Temperature, 1));
            o.SetValue("TEMP_UNIT", m.TempUnitText);

            o.SetValue("STABLE", m.IsStable);
            o.SetValue("HOLD", m.IsHold);
            o.SetValue("OVER_HIGH", m.OverHigh);
            o.SetValue("OVER_LOW", m.OverLow);

            o.SetValue("MODE", (double)m.Mode);
            o.SetValue("MODE_TEXT", m.ModeText);
            o.SetValue("BATTERY", (double)m.Battery);

            o.SetValue("CAL_FLAGS", (double)m.CalibrationFlags);
            o.SetValue("CAL_TEXT", m.CalibrationText);
            o.SetValue("ZERO_POTENTIAL", Math.Round(m.ZeroPotential, 1));
            o.SetValue("SLOPE", Math.Round(m.Slope, 1));

            o.SetValue("Success", true);
            return o;
        }

        private DeviceParameters ReadSettings()
        {
            var o = new DeviceParameters { Variables = BuildSettingOutputs() };

            PenSettings? s = IsSimulationMode ? MakeSimulatedSettings() : _settings;
            if (s is null)
            {
                o.SetValue("Success", false);
                return o;
            }

            o.SetValue("TEMP_UNIT", (double)s.TempUnit);
            o.SetValue("AUTO_LOCK", (double)s.AutoLock);
            o.SetValue("AUTO_BACKLIGHT", (double)s.AutoBacklight);
            o.SetValue("AUTO_POWEROFF", (double)s.AutoPowerOff);
            o.SetValue("PH_BUFFER", (double)s.PhBuffer);
            o.SetValue("PH_RESOLUTION", (double)s.PhResolution);
            o.SetValue("PH_REMIND_HOUR", (double)s.PhRemindHour);
            o.SetValue("PH_REMIND_DAY", (double)s.PhRemindDay);
            o.SetValue("COND_REF_TEMP", (double)s.CondRefTemp);
            o.SetValue("COND_TEMP_COEF", s.CondTempCoefficient);
            o.SetValue("TEMP_COMP", (double)s.CondPhTempComp);
            o.SetValue("COND_STD", (double)s.CondStdSolution);
            o.SetValue("CELL_CONSTANT", s.CellConstant);
            o.SetValue("COND_REMIND_HOUR", (double)s.CondRemindHour);
            o.SetValue("COND_REMIND_DAY", (double)s.CondRemindDay);
            o.SetValue("TDS_FACTOR", s.TdsFactor);
            o.SetValue("SALINITY_UNIT", (double)s.SalinityUnit);
            o.SetValue("Success", true);
            return o;
        }

        private DeviceParameters ReadCalibration()
        {
            var o = new DeviceParameters { Variables = BuildCalibrationOutputs() };

            PenCalibration? c = _calibration;
            if (c is null)
            {
                o.SetValue("Success", false);
                return o;
            }

            o.SetValue("CAL_TYPE", (double)c.Type);
            o.SetValue("CAL_TYPE_TEXT", c.TypeText);
            o.SetValue("MEASURED", Math.Round(c.Measured, 2));
            o.SetValue("MEASURED_UNIT", c.MeasuredUnitText);
            o.SetValue("STANDARD", Math.Round(c.Standard, 2));
            o.SetValue("STANDARD_UNIT", c.StandardUnitText);
            o.SetValue("STABLE", c.IsStable);
            o.SetValue("CAL_FLAGS", (double)c.CalibrationFlags);
            o.SetValue("CAL_TEXT", c.CalibrationText);
            o.SetValue("CONFIRMED", c.Confirmed);
            o.SetValue("ERROR_CODE", (double)c.ErrorCode);
            o.SetValue("Success", true);
            return o;
        }

        private DeviceParameters ReadStatus()
        {
            var o = new DeviceParameters { Variables = BuildStatusOutputs() };
            int mode = Volatile.Read(ref _workMode);
            o.SetValue("WORK_MODE", (double)mode);
            o.SetValue("WORK_MODE_TEXT", PenMeterProtocol.WorkModeText(mode));
            o.SetValue("ALARM", (double)Volatile.Read(ref _alarm));
            o.SetValue("ERROR_CODE", (double)Volatile.Read(ref _errorCode));
            o.SetValue("Success", true);
            return o;
        }

        /// <summary>
        /// 扫一遍附近的 BLE 设备。这条命令**不需要先连接**——正是用来确定该连谁的。
        /// </summary>
        private async Task<DeviceParameters> ScanBluetoothAsync(DeviceParameters? input)
        {
            var o = new DeviceParameters
            {
                Variables = new ObservableCollection<ParameterBase>
                {
                    new NumberParameter("Count", 0, 1000, 0, "扫到的设备数", true),
                    new StringParameter("Devices", "", "每行:名称 / 地址 / 信号强度"),
                    new BooleanParameter("Success", false, "是否成功")
                }
            };

            int sec = Math.Clamp((int)(input?.GetNullableValue<double>("TimeoutSec") ?? 8), 2, 60);

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(sec + 30));
                var found = await BleProxyLink.ScanAsync(
                    sec * 1000,
                    msg => _logger.LogInformation($"[笔式酸度计/蓝牙] {msg}"),
                    cts.Token).ConfigureAwait(false);

                var sb = new StringBuilder();
                foreach (var d in found) sb.AppendLine(d.ToString());

                o.SetValue("Count", (double)found.Count);
                o.SetValue("Devices", sb.ToString().TrimEnd());
                o.SetValue("Success", true);

                _logger.LogInformation(
                    found.Count == 0
                        ? $"[笔式酸度计] 扫描 {sec}s 未发现任何带名字的 BLE 设备。请确认笔已开机、电脑蓝牙已打开。"
                        : $"[笔式酸度计] 扫到 {found.Count} 台设备:\r\n{sb}");
                return o;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[笔式酸度计] 蓝牙扫描失败");
                o.SetValue("Devices", $"扫描失败:{ex.Message}");
                return o;
            }
        }

        private static PenMeasure MakeSimulatedMeasure()
        {
            double t = Environment.TickCount64 / 1000.0;
            return new PenMeasure
            {
                Value = Math.Round(7.00 + 0.12 * Math.Sin(t / 6.0), 2),
                UnitCode = 0,
                Temperature = Math.Round(25.0 + 0.4 * Math.Sin(t / 10.0), 1),
                TempUnitCode = 0,
                IsStable = true,
                Mode = 0,
                Battery = 86,
                CalibrationFlags = 0x01 | 0x02 | 0x04,   // L1 M H1 三点已校
                ZeroPotential = 1.2,
                Slope = 99.1,
            };
        }

        private static PenSettings MakeSimulatedSettings() => new()
        {
            TempUnit = 0,
            AutoLock = 0,
            AutoBacklight = 2,
            AutoPowerOff = 15,
            PhBuffer = 0,
            PhResolution = 2,
            CondRefTemp = 25,
            CondTempCoefHi = 0,
            CondTempCoefLo = 200,      // 2.00 %/℃
            CondPhTempComp = 25,
            CondStdSolution = 0,
            CondCellConstant = 100,    // 1.00
            TdsCoefficient = 50,       // 0.50
            SalinityUnit = 0,
        };

        #endregion

        #region 写命令的三种形状

        /// <summary>直接组一帧发出去的命令(0x80 / 0x82 / 0x83 / 0x84 / 0x85)。</summary>
        private void AddRawCommand(string name, string help, double min, double max,
                                   Func<double, byte[]> build)
        {
            Commands.Add(new DeviceCommand
            {
                Name = name,
                HelpText = help,
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TargetValue", min, max, min, help)
                    }
                },
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction(name, p =>
                {
                    double v = p?.GetNullableValue<double>("TargetValue") ?? min;
                    return SendAsync(build(v));
                })
            });
        }

        /// <summary>改一项配置:取当前配置为底稿 → 改这一项 → 整包 0x81 发下去。</summary>
        private void AddSettingCommand(string name, string help, double min, double max,
                                       Action<PenSettings, double> apply,
                                       string? unit = null, int decimals = 0)
        {
            var input = new NumberParameter("TargetValue", min, max, min, help, false, unit);
            input.DecimalPlaces = decimals;

            Commands.Add(new DeviceCommand
            {
                Name = name,
                HelpText = help + "(下发前仪器需处于配置模式)",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase> { input }
                },
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction(name, p =>
                {
                    double v = p?.GetNullableValue<double>("TargetValue") ?? min;
                    return PatchSettingsAsync(name, s => apply(s, v));
                })
            });
        }

        /// <summary>校准提醒:小时 + 天两个字节。0-0 表示关闭。</summary>
        private void AddCalibrationRemindCommand(string name, Action<PenSettings, byte, byte> apply)
        {
            const string help = "校准提醒间隔。小时与天都填 0 表示关闭提醒";

            Commands.Add(new DeviceCommand
            {
                Name = name,
                HelpText = help + "(下发前仪器需处于配置模式)",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("Hours", 0, 255, 0, "提醒间隔 小时", false, "h"),
                        new NumberParameter("Days",  0, 255, 0, "提醒间隔 天",   false, "d"),
                    }
                },
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction(name, p =>
                {
                    byte h = (byte)Math.Clamp(p?.GetNullableValue<double>("Hours") ?? 0, 0, 255);
                    byte d = (byte)Math.Clamp(p?.GetNullableValue<double>("Days") ?? 0, 0, 255);
                    return PatchSettingsAsync(name, s => apply(s, h, d));
                })
            });
        }

        /// <summary>无参的动作型配置项(恢复出厂 / 屏幕自检)。</summary>
        private void AddSettingTrigger(string name, string help, Action<PenSettings> apply)
        {
            Commands.Add(new DeviceCommand
            {
                Name = name,
                HelpText = help + "(下发前仪器需处于配置模式)",
                OutputParameters = SuccessOnly(),
                AsyncAction = AsyncWrapAction(name, _ => PatchSettingsAsync(name, apply))
            });
        }

        private static DeviceParameters SuccessOnly() => new()
        {
            Variables = new ObservableCollection<ParameterBase>
            {
                new BooleanParameter("Success", false, "是否下发成功")
            }
        };

        private async Task<DeviceParameters> SendAsync(byte[] frame)
        {
            var o = SuccessOnly();
            if (IsSimulationMode)
            {
                o.SetValue("Success", true);
                return o;
            }
            // 协议没有为下行命令规定应答,"成功"只表示字节确实写出去了。
            // 是否真的生效,要看仪器随后推来的 0x00/0x02/0x04 有没有变。
            o.SetValue("Success", await WriteRawAsync(frame).ConfigureAwait(false));
            return o;
        }

        /// <summary>
        /// 取当前配置的一份可安全下发的底稿。
        ///
        /// 关键:把三个"一碰就动作"的字节强制清零。仪器推上来的 0x02 里这几位
        /// 可能是 1(比如操作员刚在仪器上点过屏幕自检),如果原样带回去,
        /// 那就变成"我只想改一下背光时间,结果顺手又触发了一次恢复出厂"。
        /// 需要触发它们的三条命令会在 apply 里自己置 1。
        /// </summary>
        private PenSettings? BuildSettingsBase()
        {
            var current = _settings;
            if (current is null) return null;

            var draft = current.Clone();
            draft.PhFactoryReset = 0;
            draft.CondFactoryReset = 0;
            draft.ScreenSelfTest = 0;
            return draft;
        }

        private async Task<DeviceParameters> PatchSettingsAsync(string commandName, Action<PenSettings> apply)
        {
            var o = SuccessOnly();

            if (IsSimulationMode)
            {
                o.SetValue("Success", true);
                return o;
            }

            var draft = BuildSettingsBase();
            if (draft is null)
            {
                // 这里宁可失败也不凭空造 21 个字节 —— 见文件头的说明。
                throw new InvalidOperationException(
                    $"还没收到仪器的当前配置,无法下发「{commandName}」。" +
                    "0x81 是整包 21 字节下发,必须以仪器推上来的当前配置为底稿改一项," +
                    "凭空拼包会把参比温度、电极常数等一并清零。" +
                    "请先执行「切换工作模式」并把值设为 2(配置模式)," +
                    "或直接在仪器上进入配置界面,等仪器推来配置后再下发。");
            }

            apply(draft);
            o.SetValue("Success", await WriteRawAsync(PenMeterProtocol.BuildSettings(draft)).ConfigureAwait(false));

            // 本地底稿同步更新:仪器不一定会马上把新配置推回来,
            // 若不更新,连着改两项时第二项会用旧底稿,把第一项改回去。
            _settings = draft;
            return o;
        }

        #endregion

        #endregion

        #region 日志包装

        private Func<DeviceParameters, Task<DeviceParameters>> AsyncWrapAction(
            string commandName,
            Func<DeviceParameters?, Task<DeviceParameters>> innerAsync)
        {
            return async (DeviceParameters parameters) =>
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var output = await innerAsync(parameters).ConfigureAwait(false);
                    sw.Stop();
                    _logger.LogInformation(
                        $"[笔式酸度计命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex,
                        $"[笔式酸度计命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        #endregion
    }
}
