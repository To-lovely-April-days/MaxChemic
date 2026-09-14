// ============================================================================
//  PhMeter_TouchScreen.cs
//  触摸屏酸度计驱动(TTL 串口私有协议)。
//
//  ── 与本仓库其它驱动最大的不同 ──────────────────────────────────────
//  已有的二十几个驱动都是 Modbus/S7 的**请求-应答**模型:要数据就发一帧,
//  等一帧回来。这台仪器反过来:握手成功之后仪器每 ~800ms **主动推**一个
//  70 字节的测量参数包,协议里压根没有"读一次"的命令。
//
//  因此这里的结构是:
//      后台接收线程  → 连续收帧 → 解析 → 覆盖 _latest 快照
//      读命令        → 直接抄 _latest,不碰串口
//      写命令        → 单向发一个设置包,不等应答(协议没有规定应答)
//
//  后来维护的人请留意两件事:
//    · 给"获取测量数据"加一次串口往返是错的 —— 没有对应的请求命令;
//    · 快照有可能是 null(刚连上还没收到第一包),读命令要能容忍,
//      不能抛异常,否则屏幕一打开就是一片红。
//
//  ── 串口参数为什么写死 ────────────────────────────────────────────
//  手册原文:"串口通信接口参数是固定的" 9600/8/1/无校验。做成下拉框只会
//  让人误以为可调,调错了还查不出原因,所以只暴露"串口号"一项。
// ============================================================================

using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO.Ports;
using System.Text;

namespace PhMeter_TouchScreen
{
    [Export(typeof(IDevice))]
    public class PhMeter_TouchScreen : Device, IDeviceScreen
    {
        /// <summary>
        /// 源码版本戳。**改动本文件时请手动更新**。
        /// 蓝牙那个驱动吃过亏:改完代码重新生成,现场日志和改之前一模一样,
        /// 实际跑的还是旧程序集,只能靠逐行比对日志文本去猜。
        /// 连接时第一行就打出来,对不上立刻知道是构建没生效。
        /// </summary>
        public const string SourceStamp = "2026-08-28-a";

        private readonly ILogService _logger;

        /// <summary>
        /// 握手等待上限。手册说仪器"立即"回显,但真机上仪器可能正忙着刷屏,
        /// 而且它每 ~800ms 才推一次包 —— 2.5s 覆盖两个推包周期,留足余量。
        /// </summary>
        private const int HandshakeTimeoutMs = 2500;

        /// <summary>每种接线方式下的握手次数。</summary>
        private const int HandshakeRetries = 2;

        /// <summary>
        /// 串口打开之后、发第一包之前的静置时间。
        ///
        /// 不少 USB-TTL 芯片(CH340/CP2102 之流)在串口被打开时会翻转 DTR/RTS,
        /// 而这两根线在很多板子上是接到 MCU 复位脚的 —— 也就是说**开串口这个动作
        /// 本身就可能把仪器重启一次**。刚复位的仪器不会理你的请求连接包。
        /// 静置一下再开口说话。
        /// </summary>
        private const int SettleAfterOpenMs = 600;

        /// <summary>
        /// 判定"还活着"的推包间隔上限。仪器标称 ~800ms 一包,
        /// 给到 5 秒是留足余量:偶尔丢一两包不该被当成掉线。
        /// </summary>
        private const int StaleThresholdMs = 5000;

        private readonly object _portLock = new object();
        private SerialPort? _port;
        private Thread? _rxThread;
        private volatile bool _rxRunning;

        /// <summary>最新一包测量数据。读命令只读它,不碰串口。volatile + 整体替换,无需加锁。</summary>
        private volatile TouchPhSnapshot? _latest;

        /// <summary>最近一次收到**任何**合法帧的时刻,用于判活。</summary>
        private long _lastFrameTicks;

        /// <summary>握手用:接收线程看到任意一个合法帧时置位。</summary>
        private volatile TaskCompletionSource<bool>? _handshake;

        /// <summary>
        /// 串口上收到的**原始字节**,只留开头这些。
        ///
        /// 握手失败时最要命的问题是"不知道线上到底有没有东西":
        /// 收到一堆解不开的字节(波特率/接线不对),和一个字节都没收到
        /// (串口选错、线没插、仪器没开),处理方式完全相反,
        /// 而原来的日志"握手无回应"对这两种情况一模一样。
        /// 失败时把这段十六进制打出来,一眼就能分清。
        /// </summary>
        private readonly byte[] _rawPeek = new byte[256];
        private int _rawPeekLen;
        private long _rawTotal;
        private readonly object _rawLock = new object();

        /// <summary>记录上传用:发出查询命令后由接收线程往里塞记录,收到结束包时完成。</summary>
        private readonly object _recordLock = new object();
        private List<TouchPhRecord>? _recordSink;
        private TaskCompletionSource<bool>? _recordDone;

        public string ScreenKey => "PhMeterTouchScreen";

        public PhMeter_TouchScreen()
        {
            _logger = LogManager.GetLogger<PhMeter_TouchScreen>();

            DeviceId = "PHM0001";
            Name = "触摸屏酸度计";
            Manufacturer = "触摸屏酸度计(TTL 串口私有协议)";
            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/PhMeter_TouchScreen.png";
            Category = DeviceCategories.InlineDetection;
            ComId = "PHM0001";

            // 在线分析仪表,流程里哪一段都可能挂一支电极
            AllowedRegions = new List<RegionType>
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            Parameters.Variables.Add(new StringParameter("DeviceId", "PHM0001", "设备实例编号")
            {
                Options = new ObservableCollection<string> { "PHM0001", "PHM0002", "PHM0003", "PHM0004" }
            });

            Parameters.Variables.Add(new StringParameter("串口号", SerialPortOptions.DefaultPort(), "仪器 USB 线对应的串口")
            {
                Options = SerialPortOptions.CreateOptions()
            });

            // 只保留 Direct 一项:本机 USB 直连是这台仪器唯一的接法。
            // 留着这个参数是为了压住基类里的全局默认通信方式 —— 若不写,
            // GetDeviceCommunicationMode() 会退到 DeviceConnectionConfig 的全局值,
            // 那可能是 ModbusTcp,于是连接会被路由到根本不适用的分支上。
            Parameters.Variables.Add(new StringParameter("通信方式", "Direct", "本仪器仅支持 USB/TTL 串口直连")
            {
                Options = new ObservableCollection<string> { "Direct" }
            });

            InitializeCommands();
        }

        /// <summary>TTL 串口直连,不走 ZLAN 网关,不出现在网关绑定下拉框里。</summary>
        public override bool SupportsZLanGateway => false;

        #region 可监控参数

        public override IReadOnlyList<CollectableParameter> CollectableParameters => new[]
        {
            new CollectableParameter("PH",      "PH 值",    "pH", "获取测量数据", "PH",      isKey: true),
            new CollectableParameter("MV",      "电位",     "mV", "获取测量数据", "MV"),
            new CollectableParameter("PH_TEMP", "PH 温度",  "℃",  "获取测量数据", "PH_TEMP"),
            new CollectableParameter("COND",    "电导率",   "",   "获取测量数据", "COND"),
            new CollectableParameter("DO",      "溶解氧",   "",   "获取测量数据", "DO"),
        };

        public override int AutoCollectIntervalMs => 1000;

        #endregion

        #region 连接管理

        protected override async Task<bool> OnConnectAsync()
        {
            var portName = Parameters.GetValue<string>("串口号");
            if (string.IsNullOrWhiteSpace(portName))
            {
                _logger.LogError("[触摸屏酸度计] 未配置串口号");
                return false;
            }

            _logger.LogInformation(
                $"[触摸屏酸度计 {SourceStamp}] 连接 {portName} @ 9600/8/N/1(手册规定,不可改)。" +
                $"本机现有串口:{string.Join("、", SerialPortOptions.Scan())}");

            // DTR/RTS 两种接法都试一遍。
            //
            // 这两根线在纯 TTL 接法里通常没有意义,但 USB-TTL 芯片会把它们引出来,
            // 而有的板子拿它当复位脚、有的仪器拿它当"上位机在线"的判据。
            // 到底该置位还是不置位,只有真机知道 —— 与其让人猜,不如两种都试。
            foreach (bool assertDtrRts in new[] { true, false })
            {
                if (await TryHandshakeAsync(portName, assertDtrRts).ConfigureAwait(false))
                    return true;

                CloseInternal();
            }

            _logger.LogError(
                $"[触摸屏酸度计] {portName} 握手失败。" + DiagnoseRaw() +
                "\r\n可依次排查:① 串口号是否选对(设备管理器里看仪器插拔时哪个口出现/消失);" +
                "② USB 线是否插牢、仪器是否已开机并停在测量界面;" +
                "③ 该口是否被别的软件占着(厂家自带的上位机、串口调试助手)。");
            CloseInternal();
            return false;
        }

        /// <summary>按一种 DTR/RTS 接法开口并握手。</summary>
        private async Task<bool> TryHandshakeAsync(string portName, bool assertDtrRts)
        {
            CloseInternal();

            lock (_rawLock) { _rawPeekLen = 0; _rawTotal = 0; }

            try
            {
                var port = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One)
                {
                    // 读超时决定接收线程空转的节奏。200ms 既不会把 CPU 吃满,
                    // 也不会让"停止"指令等太久。
                    ReadTimeout = 200,
                    WriteTimeout = 1000,
                    DtrEnable = assertDtrRts,
                    RtsEnable = assertDtrRts,
                };
                port.Open();
                port.DiscardInBuffer();
                port.DiscardOutBuffer();

                lock (_portLock) _port = port;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[触摸屏酸度计] 打开串口 {portName} 失败(DTR/RTS={assertDtrRts})");
                return false;
            }

            StartReceiver();

            // 开串口可能刚把仪器复位过,先静置一下再说话。见 SettleAfterOpenMs。
            await Task.Delay(SettleAfterOpenMs).ConfigureAwait(false);

            for (int attempt = 1; attempt <= HandshakeRetries; attempt++)
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _handshake = tcs;

                // 先补一个"请求断开包"。上一次连接没正常收尾的话,仪器可能还认为
                // 自己连着上位机;先让它断干净,再请求连接。仪器对断开包不回应,
                // 发完稍等一下即可。手册说这个包不是必需的,这里是拿它清场。
                if (attempt > 1)
                {
                    WriteRaw(TouchPhProtocol.BuildDisconnect());
                    await Task.Delay(200).ConfigureAwait(false);
                }

                if (!WriteRaw(TouchPhProtocol.BuildConnect()))
                {
                    _handshake = null;
                    return false;
                }

                var finished = await Task.WhenAny(tcs.Task, Task.Delay(HandshakeTimeoutMs)).ConfigureAwait(false);
                _handshake = null;

                if (finished == tcs.Task)
                {
                    _logger.LogInformation(
                        $"[触摸屏酸度计] 握手成功({portName},DTR/RTS={assertDtrRts},第 {attempt} 次)");
                    Volatile.Write(ref _lastFrameTicks, DateTime.UtcNow.Ticks);
                    return true;
                }

                _logger.LogWarning(
                    $"[触摸屏酸度计] 第 {attempt} 次握手无回应(DTR/RTS={assertDtrRts})。" + DiagnoseRaw());
            }

            return false;
        }

        /// <summary>
        /// 把线上收到的原始字节说清楚。
        ///
        /// "握手无回应"这一句对两种截然不同的情况长得一模一样:
        ///   · 一个字节都没收到 —— 串口选错、线没插、仪器没开、口被别人占着;
        ///   · 收到一堆解不开的字节 —— 线通着,但波特率或接线不对。
        /// 两者的排查方向完全相反,所以必须分清。
        /// </summary>
        private string DiagnoseRaw()
        {
            long total;
            string hex;
            lock (_rawLock)
            {
                total = _rawTotal;
                hex = _rawPeekLen == 0
                    ? ""
                    : BitConverter.ToString(_rawPeek, 0, _rawPeekLen).Replace('-', ' ');
            }

            if (total == 0)
                return "串口上**一个字节都没收到** —— 说明线路根本不通:" +
                       "串口号选错、USB 线没插好、仪器没开机,或者这个口正被别的软件占着。";

            return $"串口上收到了 {total} 字节,但没有一个能解成合法帧(帧头 0x15 / 包尾 0x16)。" +
                   $"说明线是通的、但数据对不上 —— 多半是串口号选到了别的设备。开头这些字节:{hex}";
        }

        protected override Task<bool> OnDisconnectAsync()
        {
            // 请求断开包不是必需的(手册原话),但发了之后仪器屏幕右上角的电脑
            // 图标会消失,对操作员更友好。仪器不回应答,发完直接关。
            try { WriteRaw(TouchPhProtocol.BuildDisconnect()); } catch { }

            CloseInternal();
            return Task.FromResult(true);
        }

        protected override Task<bool> OnCheckConnectionAsync()
        {
            // 没有"心跳命令"可发 —— 判活只能看仪器还在不在推包。
            lock (_portLock)
            {
                if (_port is null || !_port.IsOpen) return Task.FromResult(false);
            }

            var last = new DateTime(Volatile.Read(ref _lastFrameTicks), DateTimeKind.Utc);
            bool alive = (DateTime.UtcNow - last).TotalMilliseconds < StaleThresholdMs;
            if (!alive)
                _logger.LogWarning($"[触摸屏酸度计] 已超过 {StaleThresholdMs}ms 没收到推包");
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
            _rxRunning = false;

            var thread = _rxThread;
            _rxThread = null;
            // 接收线程最长阻塞一个 ReadTimeout(200ms),给 1 秒足够它自己退出。
            // join 不成也不强杀 —— 强杀会让串口句柄悬着,下次打开报"拒绝访问"。
            try { thread?.Join(1000); } catch { }

            lock (_portLock)
            {
                try { _port?.Close(); } catch { }
                try { _port?.Dispose(); } catch { }
                _port = null;
            }

            _latest = null;
            _handshake = null;

            lock (_recordLock)
            {
                _recordSink = null;
                _recordDone?.TrySetResult(false);
                _recordDone = null;
            }
        }

        #endregion

        #region 接收线程

        private void StartReceiver()
        {
            _rxRunning = true;
            _rxThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = $"TouchPhMeter-RX-{DeviceId}"
            };
            _rxThread.Start();
        }

        /// <summary>
        /// 收帧主循环。
        ///
        /// 用独立线程 + 阻塞读,而不是 SerialPort.DataReceived:
        /// DataReceived 在 .NET 上跑在串口内部的线程池回调里,重入与顺序都不好把握,
        /// 而这台仪器是持续推流,正好适合一根专线一直读。
        ///
        /// 缓冲区策略:收到的字节追加到 _buf 尾部,从头反复取帧,取完把剩余字节
        /// 挪到开头。半帧、粘包、垃圾字节都由 TryReadFrame 的重同步兜住。
        /// </summary>
        private void ReceiveLoop()
        {
            var buf = new byte[4096];
            int len = 0;
            var chunk = new byte[512];

            while (_rxRunning)
            {
                SerialPort? port;
                lock (_portLock) port = _port;
                if (port is null || !port.IsOpen) break;

                int read;
                try
                {
                    read = port.Read(chunk, 0, chunk.Length);
                }
                catch (TimeoutException)
                {
                    continue;                       // 正常:这段时间仪器没推东西
                }
                catch (Exception ex)
                {
                    if (_rxRunning)
                        _logger.LogWarning($"[触摸屏酸度计] 串口读取中断: {ex.Message}");
                    break;
                }

                if (read <= 0) continue;

                // 先把原始字节留个证,再谈解析。见 _rawPeek 的说明。
                lock (_rawLock)
                {
                    _rawTotal += read;
                    int room = _rawPeek.Length - _rawPeekLen;
                    if (room > 0)
                    {
                        int n = Math.Min(room, read);
                        Buffer.BlockCopy(chunk, 0, _rawPeek, _rawPeekLen, n);
                        _rawPeekLen += n;
                    }
                }

                // 缓冲区放不下就说明一直在收垃圾(正常帧最长 73 字节),清空重来,
                // 免得越积越多、每轮都从头扫一遍 4KB。
                if (len + read > buf.Length)
                {
                    _logger.LogWarning("[触摸屏酸度计] 接收缓冲溢出,丢弃并重新同步");
                    len = 0;
                    if (read > buf.Length) continue;
                }

                Buffer.BlockCopy(chunk, 0, buf, len, read);
                len += read;

                int offset = 0;
                while (offset < len)
                {
                    if (!TouchPhProtocol.TryReadFrame(buf, offset, len - offset, out var frame, out int consumed))
                    {
                        if (consumed == 0) break;   // 数据不够,等下一轮
                        offset += consumed;         // 重同步
                        continue;
                    }

                    offset += consumed;
                    if (frame is not null) Dispatch(frame);
                }

                // 把没消费完的挪到开头
                if (offset > 0)
                {
                    len -= offset;
                    if (len > 0) Buffer.BlockCopy(buf, offset, buf, 0, len);
                }
            }
        }

        private void Dispatch(TouchPhFrame frame)
        {
            Volatile.Write(ref _lastFrameTicks, DateTime.UtcNow.Ticks);

            // **收到任意一个合法帧就算连上了**,不只认 ConnectAck。
            // 手册写的是"仪器把请求连接包原封不动返回",但固件不见得都这么老实 ——
            // 有的直接开始推测量包。既然帧头、长度、包尾三项都对上了,
            // 对面就是这台仪器,没有理由因为它没回显就判失败。
            _handshake?.TrySetResult(true);

            switch (frame.Kind)
            {
                case TouchPhFrameKind.ConnectAck:
                    break;

                case TouchPhFrameKind.Params:
                    try { _latest = TouchPhProtocol.ParseParams(frame.Data); }
                    catch (Exception ex) { _logger.LogWarning($"[触摸屏酸度计] 测量包解析失败: {ex.Message}"); }
                    break;

                case TouchPhFrameKind.Record:
                    lock (_recordLock)
                    {
                        if (_recordSink is null) break;    // 没人在等记录,丢掉
                        try { _recordSink.Add(TouchPhProtocol.ParseRecord(frame.Data)); }
                        catch (Exception ex) { _logger.LogWarning($"[触摸屏酸度计] 记录包解析失败: {ex.Message}"); }
                    }
                    break;

                case TouchPhFrameKind.RecordEnd:
                    lock (_recordLock) _recordDone?.TrySetResult(true);
                    break;
            }
        }

        private bool WriteRaw(byte[] frame)
        {
            try
            {
                lock (_portLock)
                {
                    if (_port is null || !_port.IsOpen) return false;
                    _port.Write(frame, 0, frame.Length);
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[触摸屏酸度计] 串口写入失败");
                return false;
            }
        }

        #endregion

        #region 命令

        private void InitializeCommands()
        {
            // ── 读:直接抄后台缓存的最新推包 ──────────────────────────
            Commands.Add(new DeviceCommand
            {
                Name = "获取测量数据",
                HelpText = "读取仪器最近一次推送的测量参数(PH / mV / 电导率 / 溶解氧及各自温度与状态)",
                OutputParameters = new DeviceParameters { Variables = BuildMeasureOutputs() },
                AsyncAction = AsyncWrapAction("获取测量数据", _ => Task.FromResult(ReadMeasure()))
            });

            // ── 写:系统参数设置包 ────────────────────────────────────
            AddSettingCommand("设置水类型", "0=普通水,1=纯水,2=加氨纯水",
                TouchPhProtocol.SettingH2oType, 0, 2);

            AddSettingCommand("设置PH温度源", "0=自动温度补偿(ATC),1=手动温度补偿(MTC)",
                TouchPhProtocol.SettingPhTempSource, 0, 1);

            AddSettingCommand("设置PH标液系列", "0=国标系列,1=NIST 系列,2=USA 欧美系列",
                TouchPhProtocol.SettingPhStdSeries, 0, 2);

            AddSettingCommand("设置PH分辨率",
                "低精度酸度计:1=0.1 pH,2=0.01 pH;高精度酸度计:2=0.01 pH,3=0.001 pH。请按仪器型号选择",
                TouchPhProtocol.SettingPhResolution, 1, 3);

            AddSettingCommand("设置PH手动温度", "手动温度补偿值,范围 0.0~99.9 ℃",
                TouchPhProtocol.SettingPhMtcTemp, 0, 99.9, unit: "℃", decimals: 1);

            AddTriggerCommand("清除PH记录", "清除仪器内保存的全部 PH 记录(不可撤销)",
                TouchPhProtocol.SettingClearRecords);

            AddTriggerCommand("保存当前PH数据", "把当前测量的 PH 数据存为一条记录",
                TouchPhProtocol.SettingSavePhData);

            AddTriggerCommand("PH恢复出厂", "PH 参数恢复出厂设置",
                TouchPhProtocol.SettingPhFactoryReset);

            // ── 记录上传:发一条命令,收 N 个记录包,直到结束包 ────────
            Commands.Add(new DeviceCommand
            {
                Name = "查询PH记录",
                HelpText = "让仪器上传全部 PH 记录。仪器会逐条推送记录包,收到结束包后一次性返回",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TimeoutSec", 1, 120, 15, "等待上传完成的超时(秒)", false, "s")
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("Count", 0, 100000, 0, "记录条数", true),
                        new StringParameter("Records", "", "记录明细,每行:编号/PH/mV/温度/时间"),
                        new BooleanParameter("Success", false, "是否成功")
                    }
                },
                AsyncAction = AsyncWrapAction("查询PH记录", QueryRecordsAsync)
            });
        }

        private static ObservableCollection<ParameterBase> BuildMeasureOutputs() => new()
        {
            new NumberParameter("PH",          -2, 20,     0, "PH 值",              true, "pH"),
            new NumberParameter("MV",       -2000, 2000,   0, "电位",               true, "mV"),
            new NumberParameter("PH_TEMP",   -10, 150,     0, "PH 通道温度",        true, "℃"),
            new NumberParameter("COND",        0, 1000000, 0, "电导率",             true),
            new NumberParameter("COND_TEMP", -10, 150,     0, "电导率通道温度",     true, "℃"),
            new NumberParameter("DO",          0, 100,     0, "溶解氧",             true),
            new NumberParameter("DO_SAT",      0, 500,     0, "溶解氧饱和度",       true, "%"),
            new NumberParameter("DO_TEMP",   -10, 150,     0, "溶解氧通道温度",     true, "℃"),
            new NumberParameter("DO_CURRENT",  0, 100000,  0, "溶解氧电极电流",     true),

            new BooleanParameter("PH_STABLE",   false, "PH 读数是否稳定",     true),
            new BooleanParameter("COND_STABLE", false, "电导率读数是否稳定",  true),
            new BooleanParameter("DO_STABLE",   false, "溶解氧读数是否稳定",  true),
            new BooleanParameter("PH_ATC",      false, "PH 当前是否处于自动温补", true),

            // ph_tmp_src 与 is_ph_atc 是协议里两个不同的字段:前者是"温度源设定",
            // 后者是"当前实际是否在自动温补"。正常两者一致,但要回读设置项就该用
            // 前者 —— 万一仪器设了 ATC 却因为没插温度探头而实际走 MTC,
            // 拿 is_ph_atc 回填下拉框会把用户的设定显示反。
            new NumberParameter("PH_TEMP_SRC", 0, 1, 0, "PH 温度源设定 0=ATC 1=MTC", true),

            new NumberParameter("PH_RESOLUTION", 0, 3, 2, "PH 分辨率编码(=小数位数)", true),
            new NumberParameter("PH_STD_TYPE",   0, 3, 0, "PH 标液系列 0国标/1NIST/2USA", true),
            new NumberParameter("PH_H2O_TYPE",   0, 3, 0, "水类型 0普通水/1纯水/2加氨纯水", true),
            new NumberParameter("PH_MTC_TEMP",   0, 100, 0, "PH 手动温度设定值", true, "℃"),
            new NumberParameter("COND_REF_TEMP", 0, 31, 25, "电导率基准温度", true, "℃"),
            new NumberParameter("COND_K",        0, 100, 0, "电导电极常数", true),
            new NumberParameter("MODEL",         0, 15, 0, "仪器型号码", true),

            new StringParameter("TEMP_UNIT", "℃", "温度单位"),
            new BooleanParameter("Success", false, "是否取到有效数据", true),
        };

        /// <summary>
        /// 读命令的实现:抄快照。
        /// 注意 Success=false 时其余数值保持为 0 而不是抛异常 ——
        /// 刚连上还没收到第一包是很正常的状态,屏幕那边会显示 "--"。
        /// </summary>
        private DeviceParameters ReadMeasure()
        {
            var output = new DeviceParameters { Variables = BuildMeasureOutputs() };

            TouchPhSnapshot? s = _latest;

            if (IsSimulationMode)
                s = MakeSimulatedSnapshot();

            if (s is null)
            {
                output.SetValue("Success", false);
                return output;
            }

            output.SetValue("PH", Math.Round((double)s.Ph, s.PhDecimals));
            output.SetValue("MV", Math.Round((double)s.Mv, 1));
            output.SetValue("PH_TEMP", Math.Round((double)s.PhTemp, 1));
            output.SetValue("COND", Math.Round((double)s.Cond, 2));
            output.SetValue("COND_TEMP", Math.Round((double)s.CondTemp, 1));
            output.SetValue("DO", Math.Round((double)s.Do, s.DoDecimals));
            output.SetValue("DO_SAT", Math.Round((double)s.DoSaturation, 1));
            output.SetValue("DO_TEMP", Math.Round((double)s.DoTemp, 1));
            output.SetValue("DO_CURRENT", Math.Round((double)s.DoCurrent, 3));

            output.SetValue("PH_STABLE", s.IsPhStable);
            output.SetValue("COND_STABLE", s.IsCondStable);
            output.SetValue("DO_STABLE", s.IsDoStable);
            output.SetValue("PH_ATC", s.IsPhAtc);
            output.SetValue("PH_TEMP_SRC", (double)s.PhTempSource);

            output.SetValue("PH_RESOLUTION", (double)s.PhResolution);
            output.SetValue("PH_STD_TYPE", (double)s.PhStdType);
            output.SetValue("PH_H2O_TYPE", (double)s.PhH2oType);
            output.SetValue("PH_MTC_TEMP", Math.Round((double)s.PhMtcTemp, 1));
            output.SetValue("COND_REF_TEMP", (double)s.CondRefTemp);
            output.SetValue("COND_K", Math.Round((double)s.CondCellConstant, 3));
            output.SetValue("MODEL", (double)s.Model);

            output.SetValue("TEMP_UNIT", s.TempUnitText);
            output.SetValue("Success", true);
            return output;
        }

        /// <summary>模拟模式下造一份看起来合理的读数,方便无仪器时调流程和屏幕。</summary>
        private TouchPhSnapshot MakeSimulatedSnapshot()
        {
            // 用时间做一个缓慢漂移,比纯随机更像真实电极
            double t = Environment.TickCount64 / 1000.0;
            float ph = (float)(7.00 + 0.15 * Math.Sin(t / 7.0));
            return new TouchPhSnapshot
            {
                Model = 1,
                Ph = ph,
                Mv = (float)((7.00 - ph) * 59.16),
                PhTemp = (float)(25.0 + 0.3 * Math.Sin(t / 11.0)),
                Cond = (float)(1408 + 20 * Math.Sin(t / 13.0)),
                CondTemp = 25.0f,
                Do = (float)(8.2 + 0.2 * Math.Sin(t / 9.0)),
                DoSaturation = 98.5f,
                DoTemp = 25.0f,
                PhResolution = 2,
                DoResolution = 2,
                IsPhStable = true,
                IsCondStable = true,
                IsDoStable = true,
                IsPhAtc = true,
                CondRefTemp = 25,
                CondCellConstant = 1.0f,
                PhMtcTemp = 25.0f,
            };
        }

        /// <summary>数值型设置项:一个 TargetValue 输入,组包发出去。</summary>
        private void AddSettingCommand(string name, string help, byte settingNo,
                                       double min, double max, string? unit = null, int decimals = 0)
        {
            var input = new NumberParameter("TargetValue", min, max, min, help, false, unit);
            input.DecimalPlaces = decimals;

            Commands.Add(new DeviceCommand
            {
                Name = name,
                HelpText = help,
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase> { input }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("Success", false, "是否下发成功")
                    }
                },
                AsyncAction = AsyncWrapAction(name, p =>
                {
                    double v = p?.GetNullableValue<double>("TargetValue") ?? min;
                    return Task.FromResult(SendSetting(settingNo, (float)v));
                })
            });
        }

        /// <summary>无参动作型设置项(清记录、存数据、恢复出厂)。值按手册惯例写 0。</summary>
        private void AddTriggerCommand(string name, string help, byte settingNo)
        {
            Commands.Add(new DeviceCommand
            {
                Name = name,
                HelpText = help,
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new BooleanParameter("Success", false, "是否下发成功")
                    }
                },
                AsyncAction = AsyncWrapAction(name, _ => Task.FromResult(SendSetting(settingNo, 0f)))
            });
        }

        private DeviceParameters SendSetting(byte settingNo, float value)
        {
            var output = new DeviceParameters
            {
                Variables = new ObservableCollection<ParameterBase>
                {
                    new BooleanParameter("Success", false, "是否下发成功")
                }
            };

            if (IsSimulationMode)
            {
                output.SetValue("Success", true);
                return output;
            }

            // 手册没有为设置包规定应答,所以"成功"只能表示字节确实写出去了。
            // 实际是否生效要看随后推来的测量包里对应字段有没有变 —— 这一点
            // 在屏幕上通过"下发后回读"体现,驱动层不做假保证。
            bool ok = WriteRaw(TouchPhProtocol.BuildSetting(settingNo, value));
            output.SetValue("Success", ok);
            return output;
        }

        private async Task<DeviceParameters> QueryRecordsAsync(DeviceParameters? input)
        {
            var output = new DeviceParameters
            {
                Variables = new ObservableCollection<ParameterBase>
                {
                    new NumberParameter("Count", 0, 100000, 0, "记录条数", true),
                    new StringParameter("Records", "", "记录明细"),
                    new BooleanParameter("Success", false, "是否成功")
                }
            };

            if (IsSimulationMode)
            {
                output.SetValue("Count", 2d);
                output.SetValue("Records", "0001\t6.86 pH\t14 mV\t25.0℃\t2026/1/1 09:00\n0002\t4.01 pH\t176 mV\t25.0℃\t2026/1/1 09:05");
                output.SetValue("Success", true);
                return output;
            }

            int timeoutSec = (int)(input?.GetNullableValue<double>("TimeoutSec") ?? 15);
            timeoutSec = Math.Clamp(timeoutSec, 1, 120);

            var sink = new List<TouchPhRecord>();
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_recordLock)
            {
                // 上一次查询没收完就又发一次的话,把旧的结掉,避免两批记录混在一起
                _recordDone?.TrySetResult(false);
                _recordSink = sink;
                _recordDone = done;
            }

            try
            {
                if (!WriteRaw(TouchPhProtocol.BuildSetting(TouchPhProtocol.SettingQueryPhRecords, 0f)))
                    return output;

                var finished = await Task.WhenAny(done.Task, Task.Delay(timeoutSec * 1000)).ConfigureAwait(false);

                // 超时也把已经收到的交出去 —— 收了一半总比一条不给强,
                // 但 Success 如实标 false,让调用方知道这批可能不全。
                bool complete = finished == done.Task && done.Task.Result;

                List<TouchPhRecord> snapshot;
                lock (_recordLock) snapshot = new List<TouchPhRecord>(sink);

                var sb = new StringBuilder();
                foreach (var r in snapshot) sb.AppendLine(r.ToString());

                output.SetValue("Count", (double)snapshot.Count);
                output.SetValue("Records", sb.ToString().TrimEnd());
                output.SetValue("Success", complete);

                if (!complete)
                    _logger.LogWarning($"[触摸屏酸度计] 记录上传未收到结束包,已收 {snapshot.Count} 条");

                return output;
            }
            finally
            {
                lock (_recordLock)
                {
                    if (ReferenceEquals(_recordSink, sink)) _recordSink = null;
                    if (ReferenceEquals(_recordDone, done)) _recordDone = null;
                }
            }
        }

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
                        $"[触摸屏酸度计命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex,
                        $"[触摸屏酸度计命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        #endregion
    }
}
