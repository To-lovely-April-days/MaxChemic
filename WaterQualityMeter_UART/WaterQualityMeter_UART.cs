// ============================================================================
//  WaterQualityMeter_UART.cs
//  便携式三参数水质测定仪驱动(手持式按键款彩屏,pH / 电导率 / 溶解氧,
//  USB 转串口,私有帧协议)。依据《便携式三参数通信协议文档》V1.0。 
//
//  ── 与触摸屏酸度计那台的区别,后来维护的人先看这一段 ──────────────────
//  两台都是 0x15 … 0x16 的帧,长得像,骨子里完全不同:
//    · 酸度计:握手后仪器**主动推**测量包,驱动只是后台收帧、抄快照;
//    · 这一台:**主从问答**,不问就没有数据。每条读命令都是一次串口往返,
//      "获取测量数据"真的会发 15 01 06 16 出去等回应。
//    · 酸度计是小端 ARM;这一台是 8051,**大端**。float/u16/u32 全部高字节在前。
//  所以这里没有接收线程、没有快照缓存;结构就是 Modbus 驱动那种"发-等-解"。
//
//  ── 会话 ──────────────────────────────────────────────────────────
//  未联机(CMD_CONNECT)之前,仪器对其它命令**静默丢弃**,连错误应答都不给。
//  联机成功仪器蜂鸣一声、屏幕出现联机图标,并且联机期间不会自动关机。
//  于是命令超时的第一嫌疑永远是"联机状态丢了"(仪器被人关机重开、USB 掉过),
//  驱动在整轮重发都无应答后会**自动补一次联机再试一次**,不用人工重连。
//
//  ── 写设置没有"写单项"的命令 ──────────────────────────────────────
//  设置是一个 45 字节的整包,读(0x09)写(0x0A)同一结构。改任何一项都是
//  "读回整包 → 改一项 → 整包写回 → 再读回确认"。协议里三个手动温补字段
//  必须写同一个值,这一点已经压在 WqSetting 的结构上,这里不用再操心。
//
//  ── 串口参数写死 ──────────────────────────────────────────────────
//  文档:460800 / 8 / N / 1 固定不可配。只暴露"串口号"一项。需要装设备厂商的驱动不然无法识别到串口
// ============================================================================

using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO.Ports;
using System.Text;

namespace WaterQualityMeter_UART
{
    [Export(typeof(IDevice))]
    public class WaterQualityMeter_UART : Device, IDeviceScreen
    {
        /// <summary>源码版本戳。**改动本文件时手动更新**,连接时第一行日志打出来,对不上就是构建没生效。</summary>
        public const string SourceStamp = "2026-09-04-a";

        private const string Tag = "[三参数水质仪]";

        private readonly ILogService _logger;

        /// <summary>文档 2:固定 460800 bps,8N1。</summary>
        private const int BaudRate = 460800;

        /// <summary>单条命令等应答的时间。文档建议 500ms~1s。</summary>
        private const int ResponseTimeoutMs = 800;

        /// <summary>同一条命令的发送次数上限。文档:超时可重发 2~3 次。</summary>
        private const int CommandRetries = 3;

        /// <summary>
        /// 串口打开之后、发第一包之前的静置时间。
        /// USB-TTL 芯片开口时翻转 DTR/RTS 可能把仪器复位一次,刚复位的仪器不理人。
        /// </summary>
        private const int SettleAfterOpenMs = 600;

        /// <summary>阻塞读的粒度:越小,等应答的循环检查截止时间越勤,但不至于空转吃 CPU。</summary>
        private const int PortReadTimeoutMs = 50;

        /// <summary>判活:这么久没有任何一次成功应答,就主动探一次。</summary>
        private const int StaleThresholdMs = 5000;

        /// <summary>同一台仪器的串口往返必须串行(一问一答,文档 1)。</summary>
        private readonly SemaphoreSlim _io = new(1, 1);

        private readonly object _portLock = new();
        private SerialPort? _port;

        /// <summary>联机应答里的设备信息(ID + 模式使能位图)。null = 还没联机。</summary>
        private volatile WqDeviceInfo? _info;

        /// <summary>最近一次读回的设置整包,写设置时以它为底。</summary>
        private volatile WqSetting? _setting;

        private long _lastOkTicks;
        private int _lineErrors;

        // 握手失败时留证用:线上到底有没有字节、是什么。见 DiagnoseRaw。
        private readonly byte[] _rawPeek = new byte[128];
        private int _rawPeekLen;
        private long _rawTotal;
        private readonly object _rawLock = new();

        /// <summary>模拟模式下的设置,让屏幕在没仪器时也能把设置改来改去。</summary>
        private WqSetting _simSetting = new()
        {
            ManualTemp = 25f, CondRefTemp = 25f, CondTempCoe = 2.0f, CondTdsCoe = 0.5f, CondK = 1.0f,
            DoSal = 0f, DoPressure = 101.3f, TempUnit = 0, IsBeep = 1, DeviceLock = 0, BgLight = 80,
            Shutdown = 5, PhWaterType = 0, PhStdType = 0, PhDispRes = 2, CondStdType = 0,
        };
        private byte _simMode = WqMode.PH;

        public string ScreenKey => "WaterQualityMeterScreen";

        public WaterQualityMeter_UART()
        {
            _logger = LogManager.GetLogger<WaterQualityMeter_UART>();

            DeviceId = "WQM0001";
            Name = "便携式三参数水质测定仪";
            Manufacturer = "手持式三参数水质测定仪(pH / 电导率 / 溶解氧,USB 串口私有协议)";
            ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/WaterQualityMeter_UART.png";
            Category = DeviceCategories.InlineDetection;
            ComId = "WQM0001";

            AllowedRegions = new List<RegionType>
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            Parameters.Variables.Add(new StringParameter("DeviceId", "WQM0001", "设备实例编号")
            {
                Options = new ObservableCollection<string> { "WQM0001", "WQM0002", "WQM0003", "WQM0004" }
            });

            Parameters.Variables.Add(new StringParameter("串口号", SerialPortOptions.DefaultPort(), "仪器 USB 线对应的串口(460800 8N1,固定)")
            {
                Options = SerialPortOptions.CreateOptions()
            });

            // 只保留 Direct:压住基类的全局默认通信方式,免得被路由到 ModbusTcp 分支。
            Parameters.Variables.Add(new StringParameter("通信方式", "Direct", "本仪器仅支持 USB 串口直连")
            {
                Options = new ObservableCollection<string> { "Direct" }
            });

            InitializeCommands();
        }

        /// <summary>USB 直连,不走 ZLAN 网关。</summary>
        public override bool SupportsZLanGateway => false;

        #region 可监控参数

        public override IReadOnlyList<CollectableParameter> CollectableParameters => new[]
        {
            new CollectableParameter("VALUE",      "当前测量值",  "",  "获取测量数据", "VALUE", isKey: true),
            new CollectableParameter("TEMP",       "温度",        "℃", "获取测量数据", "TEMP"),
            new CollectableParameter("MODE_CODE",  "测量模式码",  "",  "获取测量数据", "MODE_CODE"),
            new CollectableParameter("POLE_LEVEL", "电极状态等级", "", "获取测量数据", "POLE_LEVEL"),
            new CollectableParameter("CAL_LEVEL",  "校准状态等级", "", "获取测量数据", "CAL_LEVEL"),
        };

        /// <summary>文档 7:实时数据轮询间隔建议 ≥ 200ms。</summary>
        public override int AutoCollectIntervalMs => 1000;

        #endregion

        #region 连接管理

        protected override async Task<bool> OnConnectAsync()
        {
            var portName = Parameters.GetValue<string>("串口号");
            if (string.IsNullOrWhiteSpace(portName))
            {
                _logger.LogError($"{Tag} 未配置串口号");
                return false;
            }

            _logger.LogInformation(
                $"{Tag} {SourceStamp} 连接 {portName} @ {BaudRate}/8/N/1(文档规定,不可改)。" +
                $"本机现有串口:{string.Join("、", SerialPortOptions.Scan())}");

            // DTR/RTS 两种接法都试:USB-TTL 芯片把这两根线引出来了,有的板子拿它当复位脚。
            foreach (bool assertDtrRts in new[] { true, false })
            {
                if (await TryConnectAsync(portName, assertDtrRts).ConfigureAwait(false))
                    return true;

                CloseInternal();
            }

            CloseInternal();
            _logger.LogError($"{Tag} {portName} 联机失败。" + DiagnoseRaw());
            return false;
        }

        private async Task<bool> TryConnectAsync(string portName, bool assertDtrRts)
        {
            CloseInternal();

            lock (_rawLock) { _rawPeekLen = 0; _rawTotal = 0; }
            Volatile.Write(ref _lineErrors, 0);

            try
            {
                var port = new SerialPort(portName, BaudRate, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = PortReadTimeoutMs,
                    WriteTimeout = 1000,
                    DtrEnable = assertDtrRts,
                    RtsEnable = assertDtrRts,
                };
                port.ErrorReceived += OnPortErrorReceived;
                port.Open();
                port.DiscardInBuffer();
                port.DiscardOutBuffer();

                lock (_portLock) _port = port;

                _logger.LogInformation($"{Tag} {portName} 已打开(DTR/RTS={assertDtrRts})");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{Tag} 打开串口 {portName} 失败(DTR/RTS={assertDtrRts})");
                return false;
            }

            await Task.Delay(SettleAfterOpenMs).ConfigureAwait(false);

            // 先补一个断开:上一次没正常收尾的话仪器还认为自己连着,清干净再联机。
            // 仪器对断开不回应答;未联机时它会被静默丢弃 —— 两种情况都无害。
            WriteRaw(WqProtocol.BuildDisconnect());
            await Task.Delay(100).ConfigureAwait(false);

            var resp = await Task.Run(() => TransactCore(WqCmd.Connect, null, ResponseTimeoutMs, CommandRetries))
                                 .ConfigureAwait(false);
            if (resp is null)
            {
                _logger.LogWarning($"{Tag} 联机无应答(DTR/RTS={assertDtrRts})。" + DiagnoseRaw());
                return false;
            }
            if (!resp.Ok)
            {
                _logger.LogWarning($"{Tag} 联机被拒:{WqResult.Name(resp.Result)}");
                return false;
            }

            try
            {
                _info = WqProtocol.ParseDeviceInfo(resp.Param);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"{Tag} 联机应答参数解析失败:{WqProtocol.Hex(resp.Param)}");
                return false;
            }

            Volatile.Write(ref _lastOkTicks, DateTime.UtcNow.Ticks);

            var info = _info!;
            _logger.LogInformation(
                $"{Tag} 联机成功({portName},DTR/RTS={assertDtrRts})。设备 ID={info.DeviceId}," +
                $"已启用模式={info.EnabledModesText}(位图 0x{info.ModesSta:X4})");

            if (info.DeviceId != 1)
                _logger.LogWarning($"{Tag} 设备 ID={info.DeviceId},文档写本机型固定为 1,请确认接的是这台仪器");

            // 顺手把设置整包读回来缓存,屏幕打开时不用再等一次
            await FetchSettingAsync().ConfigureAwait(false);
            return true;
        }

        protected override Task<bool> OnDisconnectAsync()
        {
            // 断开命令不回应答,发完直接关串口(文档 6.2)。仪器会清联机图标并恢复自动关机计时。
            try { WriteRaw(WqProtocol.BuildDisconnect()); } catch { }
            CloseInternal();
            return Task.FromResult(true);
        }

        protected override async Task<bool> OnCheckConnectionAsync()
        {
            lock (_portLock)
            {
                if (_port is null || !_port.IsOpen) return false;
            }

            var last = new DateTime(Volatile.Read(ref _lastOkTicks), DateTimeKind.Utc);
            if ((DateTime.UtcNow - last).TotalMilliseconds < StaleThresholdMs) return true;

            // 一直没人读数据(比如屏幕没开、流程没跑),主动探一次,顺带刷新联机状态
            var resp = await TransactAsync(WqCmd.GetData).ConfigureAwait(false);
            if (resp is null)
                _logger.LogWarning($"{Tag} 探测无应答,判为离线");
            return resp is not null;
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
            lock (_portLock)
            {
                if (_port is not null)
                {
                    try { _port.ErrorReceived -= OnPortErrorReceived; } catch { }
                    try { _port.Close(); } catch { }
                    try { _port.Dispose(); } catch { }
                }
                _port = null;
            }
            _info = null;
            _setting = null;
        }

        private void OnPortErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            int n = Interlocked.Increment(ref _lineErrors);
            if (n <= 5)
                _logger.LogWarning($"{Tag} 串口线路错误({e.EventType}),第 {n} 次");
        }

        /// <summary>
        /// 联机失败时把线上收到的原始字节说清楚:一个字节都没有,和收到一堆解不开的字节,
        /// 排查方向完全相反。
        /// </summary>
        private string DiagnoseRaw()
        {
            long total;
            string hex;
            lock (_rawLock)
            {
                total = _rawTotal;
                hex = _rawPeekLen == 0 ? "" : BitConverter.ToString(_rawPeek, 0, _rawPeekLen).Replace('-', ' ');
            }
            int errors = Volatile.Read(ref _lineErrors);

            if (total == 0)
            {
                string s = "已发出联机命令(15 01 01 16),等待窗口内串口上一个字节都没收到。";
                if (errors > 0)
                    return s + $"但串口驱动报了 {errors} 次线路错误 —— 线上有电平在动、解不成字节,指向波特率或接线。" +
                               "注意这台仪器是 460800,普通 USB 转串口线有的跑不到这个速率。";
                return s + "线路错误也是 0,RX 完全安静。可能:串口选错(看设备管理器里哪个 COM 口随仪器 USB 线插拔出现)、" +
                           "仪器没开机、USB 线是只有电源的充电线。";
            }

            return $"串口上收到了 {total} 字节,但没有一个能解成合法应答帧(0x15 | 长度 | 0x00 … | 0x16)。" +
                   $"线是通的、数据对不上 —— 多半是串口号选到了别的设备,或波特率不匹配。开头这些字节:{hex}";
        }

        #endregion

        #region 串口往返

        /// <summary>
        /// 一次"发命令 → 等应答"的核心,同步阻塞,**只能在 Task.Run 里调**。
        /// 每次尝试先清接收缓冲再发,然后在 timeoutMs 内持续收字节、拆帧,
        /// 直到拿到 to_cmd 等于本命令的应答;别的应答(上一轮超时后迟到的)丢弃。
        /// 尝试 attempts 次都没有就返回 null。
        /// </summary>
        private WqResponse? TransactCore(byte cmd, byte[]? param, int timeoutMs, int attempts)
        {
            var frame = WqProtocol.BuildCommand(cmd, param ?? Array.Empty<byte>());
            var buf = new byte[512];
            var chunk = new byte[256];

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                SerialPort? port;
                lock (_portLock) port = _port;
                if (port is null || !port.IsOpen) return null;

                try
                {
                    port.DiscardInBuffer();
                    port.Write(frame, 0, frame.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"{Tag} 发送 {WqCmd.Name(cmd)} 失败");
                    return null;
                }

                int len = 0;
                long deadline = Environment.TickCount64 + timeoutMs;
                while (Environment.TickCount64 < deadline)
                {
                    int read;
                    try
                    {
                        read = port.Read(chunk, 0, chunk.Length);
                    }
                    catch (TimeoutException)
                    {
                        continue;                       // 这 50ms 里没字节,再等
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"{Tag} 串口读取中断:{ex.Message}");
                        return null;
                    }
                    if (read <= 0) continue;

                    NoteRaw(chunk, read);

                    if (len + read > buf.Length)
                    {
                        // 应答最长 62 字节,装不下只能是垃圾,清掉重来
                        len = 0;
                        if (read > buf.Length) continue;
                    }
                    Buffer.BlockCopy(chunk, 0, buf, len, read);
                    len += read;

                    int offset = 0;
                    while (offset < len)
                    {
                        if (!WqProtocol.TryReadFrame(buf, offset, len - offset, out var resp, out int consumed))
                        {
                            if (consumed == 0) break;   // 帧没收全,等下一批字节
                            offset += consumed;         // 重同步
                            continue;
                        }

                        offset += consumed;
                        if (resp!.ToCmd == cmd) return resp;

                        _logger.LogWarning($"{Tag} 收到 {WqCmd.Name(resp.ToCmd)} 的迟到应答,正在等 {WqCmd.Name(cmd)},丢弃");
                    }

                    if (offset > 0)
                    {
                        len -= offset;
                        if (len > 0) Buffer.BlockCopy(buf, offset, buf, 0, len);
                    }
                }

                if (attempt < attempts)
                    _logger.LogWarning($"{Tag} {WqCmd.Name(cmd)} 第 {attempt} 次无应答({timeoutMs}ms),重发");
            }

            return null;
        }

        /// <summary>
        /// 串行化的串口往返。整轮重发都无应答时,按文档"未联机则静默丢弃"的规则,
        /// 补一次联机再试最后一次 —— 仪器被人关机重开、USB 掉过一次都会走到这里,
        /// 不用人工重连。
        /// </summary>
        private async Task<WqResponse?> TransactAsync(byte cmd, byte[]? param = null, bool rehandshake = true)
        {
            await _io.WaitAsync().ConfigureAwait(false);
            try
            {
                var resp = await Task.Run(() => TransactCore(cmd, param, ResponseTimeoutMs, CommandRetries))
                                     .ConfigureAwait(false);

                if (resp is null && rehandshake && cmd != WqCmd.Connect)
                {
                    _logger.LogWarning($"{Tag} {WqCmd.Name(cmd)} 连续 {CommandRetries} 次无应答,怀疑联机状态丢失,补一次联机再试");
                    var hs = await Task.Run(() => TransactCore(WqCmd.Connect, null, ResponseTimeoutMs, 1))
                                       .ConfigureAwait(false);
                    if (hs is { Ok: true })
                    {
                        try { _info = WqProtocol.ParseDeviceInfo(hs.Param); } catch { }
                        _logger.LogInformation($"{Tag} 重新联机成功,已启用模式={_info?.EnabledModesText}");
                        resp = await Task.Run(() => TransactCore(cmd, param, ResponseTimeoutMs, 1))
                                         .ConfigureAwait(false);
                    }
                }

                if (resp is not null) Volatile.Write(ref _lastOkTicks, DateTime.UtcNow.Ticks);
                return resp;
            }
            finally
            {
                _io.Release();
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
                _logger.LogError(ex, $"{Tag} 串口写入失败");
                return false;
            }
        }

        private void NoteRaw(byte[] chunk, int read)
        {
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
        }

        /// <summary>读设置整包并缓存。失败返回 null,不抛。</summary>
        private async Task<WqSetting?> FetchSettingAsync()
        {
            var resp = await TransactAsync(WqCmd.GetSetting).ConfigureAwait(false);
            if (resp is null || !resp.Ok)
            {
                _logger.LogWarning($"{Tag} 读设置失败:{(resp is null ? "无应答" : WqResult.Name(resp.Result))}");
                return null;
            }

            try
            {
                var s = WqProtocol.ParseSetting(resp.Param);
                if (!s.TempFieldsConsistent)
                    _logger.LogWarning($"{Tag} 读回的三个手动温补字段不一致,按 ph_temp 为准(文档说读取时应相同)");
                _setting = s;
                return s;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"{Tag} 设置包解析失败:{ex.Message},原始:{WqProtocol.Hex(resp.Param)}");
                return null;
            }
        }

        #endregion

        #region 命令

        private void InitializeCommands()
        {
            Commands.Add(new DeviceCommand
            {
                Name = "获取测量数据",
                HelpText = "向仪器发一次读实时数据命令(0x06),返回当前模式的测量值、温度、稳定标志与单位",
                OutputParameters = new DeviceParameters { Variables = BuildDataOutputs() },
                AsyncAction = AsyncWrapAction("获取测量数据", ReadDataAsync)
            });

            Commands.Add(new DeviceCommand
            {
                Name = "读取设置",
                HelpText = "读回仪器的 45 字节设置整包(温补、电导率参数、溶解氧补偿、温度单位、提示音、设备锁、背光、自动关机、pH/电导率标液体系)",
                OutputParameters = new DeviceParameters { Variables = BuildSettingOutputs() },
                AsyncAction = AsyncWrapAction("读取设置", ReadSettingAsync)
            });

            // ── 单项设置:每一项都是"读整包 → 改一项 → 写整包 → 回读" ───────
            AddSettingCommand("设置温度单位", "0=℃,1=℉", 0, 1, 0, null,
                (s, v) => s.TempUnit = (byte)v);
            AddSettingCommand("设置提示音", "0=关,1=开", 0, 1, 0, null,
                (s, v) => s.IsBeep = (byte)v);
            AddSettingCommand("设置背光亮度", "背光亮度,有效范围 3~100", 3, 100, 0, "%",
                (s, v) => s.BgLight = (byte)v);
            AddSettingCommand("设置自动关机时间", "无操作自动关机时间(分钟),出厂默认 5;联机期间仪器不会自动关机", 0, 255, 0, "min",
                (s, v) => s.Shutdown = (byte)v);
            AddSettingCommand("设置pH纯水类型", "0=无(普通水样),1=纯水,2=氨水", 0, 2, 0, null,
                (s, v) => s.PhWaterType = (byte)v);
            AddSettingCommand("设置pH标液体系", "0=中国标准(CH),1=NIST,2=美国标准(USA)", 0, 2, 0, null,
                (s, v) => s.PhStdType = (byte)v);
            AddSettingCommand("设置pH显示小数位", "pH 显示小数位,2 或 3", 2, 3, 0, null,
                (s, v) => s.PhDispRes = (byte)v);
            AddSettingCommand("设置电导率标液体系", "0=中国标准(CH),1=美国标准(USA)", 0, 1, 0, null,
                (s, v) => s.CondStdType = (byte)v);
            AddSettingCommand("设置手动温补温度", "手动温度补偿温度(℃)。协议里 pH / 电导率 / 溶解氧三个温补字段共用一个存储,这里同时写三处", -10, 150, 1, "℃",
                (s, v) => s.ManualTemp = (float)v);
            AddSettingCommand("设置电导率参考温度", "电导率参考温度(℃)", 0, 100, 1, "℃",
                (s, v) => s.CondRefTemp = (float)v);
            AddSettingCommand("设置电导率温度系数", "电导率温度系数(%/℃)", 0, 10, 3, null,
                (s, v) => s.CondTempCoe = (float)v);
            AddSettingCommand("设置TDS系数", "TDS 转换系数", 0, 10, 3, null,
                (s, v) => s.CondTdsCoe = (float)v);
            AddSettingCommand("设置电极常数", "电导率电极常数 K", 0, 100, 3, null,
                (s, v) => s.CondK = (float)v);
            AddSettingCommand("设置溶解氧盐度补偿", "溶解氧盐度补偿值", 0, 100, 1, null,
                (s, v) => s.DoSal = (float)v);
            AddSettingCommand("设置溶解氧大气压补偿", "溶解氧大气压补偿值", 0, 2000, 1, null,
                (s, v) => s.DoPressure = (float)v);
            AddSettingCommand("设置设备锁", "0=解锁,1=锁定。锁定后仪器面板按键全部失效,只能由上位机解锁,慎用", 0, 1, 0, null,
                (s, v) => s.DeviceLock = (byte)v);

            Commands.Add(new DeviceCommand
            {
                Name = "切换测量模式",
                HelpText = "0=PH 1=MV 2=ORP 3=COND 4=RES 5=TDS 6=SAL 7=DO 8=SAT。只能切到仪器已启用的模式(见联机应答的模式位图)",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("TargetValue", 0, 8, 0, "目标模式码", false)
                    }
                },
                OutputParameters = new DeviceParameters { Variables = BuildAckOutputs() },
                AsyncAction = AsyncWrapAction("切换测量模式", SetModeAsync)
            });

            Commands.Add(new DeviceCommand
            {
                Name = "恢复出厂设置",
                HelpText = "仪器全部设置回到默认值并蜂鸣提示(不可撤销);如需保留自定义设置,之后重新下发",
                OutputParameters = new DeviceParameters { Variables = BuildAckOutputs() },
                AsyncAction = AsyncWrapAction("恢复出厂设置", ResetAsync)
            });

            Commands.Add(new DeviceCommand
            {
                Name = "读取存储记录",
                HelpText = "从指定索引起逐条读取仪器内的测量记录(最多 3000 条),读到索引越界即结束",
                InputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("StartIndex", 0, WqProtocol.MaxRecords - 1, 0, "起始索引(从 0 开始)", false),
                        new NumberParameter("MaxCount", 1, WqProtocol.MaxRecords, WqProtocol.MaxRecords, "最多读取条数", false),
                    }
                },
                OutputParameters = new DeviceParameters
                {
                    Variables = new ObservableCollection<ParameterBase>
                    {
                        new NumberParameter("Count", 0, WqProtocol.MaxRecords, 0, "本次读到的条数", true),
                        new StringParameter("Records", "", "记录明细,每行:编号/模式/结果/温度/时间(制表符分隔)"),
                        new BooleanParameter("Success", false, "是否读到结束(索引越界)或达到 MaxCount"),
                        new StringParameter("Message", "", "说明"),
                    }
                },
                AsyncAction = AsyncWrapAction("读取存储记录", ReadRecordsAsync)
            });
        }

        private static ObservableCollection<ParameterBase> BuildDataOutputs() => new()
        {
            new NumberParameter("VALUE",      -1e9, 1e9, 0, "当前测量值(含义随模式而定)", true),
            new NumberParameter("TEMP",       -50, 200, 0, "当前温度", true),
            new NumberParameter("MODE_CODE",  0, 15, 0, "测量模式码 0PH 1MV 2ORP 3COND 4RES 5TDS 6SAL 7DO 8SAT", true),
            new StringParameter("MODE", "", "测量模式代号,如 PH / COND / DO"),
            new StringParameter("MODE_NAME", "", "测量模式中文名"),
            new StringParameter("UNIT", "", "测量值单位"),
            new StringParameter("TEMP_UNIT", "℃", "温度单位"),
            new StringParameter("VALUE_TEXT", "", "按仪器小数位格式化的测量值,含单位"),
            new BooleanParameter("STABLE", false, "读数是否稳定", true),
            new NumberParameter("DECIMALS",   0, 6, 2, "仪器要求的显示小数位", true),
            new BooleanParameter("ATC", false, "是否处于自动温度补偿", true),
            new NumberParameter("POLE_LEVEL", 0, 255, 0, "电极状态等级", true),
            new NumberParameter("CAL_LEVEL",  0, 255, 0, "校准状态等级", true),
            new NumberParameter("DEVICE_ID",  0, 255, 0, "设备 ID(本机型为 1)", true),
            new NumberParameter("MODES_STA",  0, 65535, 0, "模式使能位图 bit0=PH … bit8=SAT", true),
            new StringParameter("MODES_ENABLED", "", "已启用模式,如 PH,COND,DO"),
            new BooleanParameter("Success", false, "是否取到有效数据", true),
        };

        private static ObservableCollection<ParameterBase> BuildSettingOutputs() => new()
        {
            new NumberParameter("MANUAL_TEMP",   -10, 150, 25, "手动温补温度", true, "℃"),
            new NumberParameter("COND_REF_TEMP", 0, 100, 25, "电导率参考温度", true, "℃"),
            new NumberParameter("COND_TEMP_COE", 0, 10, 0, "电导率温度系数", true),
            new NumberParameter("COND_TDS_COE",  0, 10, 0, "TDS 转换系数", true),
            new NumberParameter("COND_K",        0, 100, 1, "电导率电极常数", true),
            new NumberParameter("DO_SAL",        0, 100, 0, "溶解氧盐度补偿", true),
            new NumberParameter("DO_PRESSURE",   0, 2000, 0, "溶解氧大气压补偿", true),
            new NumberParameter("TEMP_UNIT_CODE", 0, 1, 0, "温度单位 0=℃ 1=℉", true),
            new BooleanParameter("BEEP", false, "提示音开关", true),
            new BooleanParameter("DEVICE_LOCK", false, "设备锁(锁定后仪器按键失效)", true),
            new NumberParameter("BG_LIGHT",      0, 100, 0, "背光亮度 3~100", true),
            new NumberParameter("SHUTDOWN_MIN",  0, 255, 5, "自动关机时间(分钟)", true),
            new NumberParameter("PH_WATER_TYPE", 0, 2, 0, "pH 纯水类型 0无 1纯水 2氨水", true),
            new NumberParameter("PH_STD_TYPE",   0, 2, 0, "pH 标液体系 0CH 1NIST 2USA", true),
            new NumberParameter("PH_DISP_RES",   0, 3, 2, "pH 显示小数位 2/3", true),
            new NumberParameter("COND_STD_TYPE", 0, 1, 0, "电导率标液体系 0CH 1USA", true),
            new BooleanParameter("Success", false, "是否读到", true),
        };

        private static ObservableCollection<ParameterBase> BuildAckOutputs() => new()
        {
            new BooleanParameter("Success", false, "是否成功"),
            new StringParameter("Message", "", "说明"),
        };

        private async Task<DeviceParameters> ReadDataAsync(DeviceParameters? _)
        {
            var output = new DeviceParameters { Variables = BuildDataOutputs() };

            WqData? d;
            WqDeviceInfo? info;

            if (IsSimulationMode)
            {
                d = MakeSimulatedData();
                info = new WqDeviceInfo { DeviceId = 1, ModesSta = 0x0089 };   // PH + COND + DO
            }
            else
            {
                var resp = await TransactAsync(WqCmd.GetData).ConfigureAwait(false);
                if (resp is null || !resp.Ok)
                {
                    output.SetValue("Success", false);
                    return output;
                }

                try
                {
                    d = WqProtocol.ParseData(resp.Param);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"{Tag} 实时数据解析失败:{ex.Message},原始:{WqProtocol.Hex(resp.Param)}");
                    output.SetValue("Success", false);
                    return output;
                }
                info = _info;
            }

            int dec = d.SafeDecimals;
            output.SetValue("VALUE", Math.Round((double)d.Value, dec));
            output.SetValue("TEMP", Math.Round((double)d.Temp, 1));
            output.SetValue("MODE_CODE", (double)d.Mode);
            output.SetValue("MODE", d.ModeCode);
            output.SetValue("MODE_NAME", WqMode.Label(d.Mode));
            output.SetValue("UNIT", d.UnitText);
            output.SetValue("TEMP_UNIT", d.TempUnitText);
            output.SetValue("VALUE_TEXT", d.Value.ToString("F" + dec, System.Globalization.CultureInfo.InvariantCulture) + " " + d.UnitText);
            output.SetValue("STABLE", d.IsStable);
            output.SetValue("DECIMALS", (double)dec);
            output.SetValue("ATC", d.Atc != 0);
            output.SetValue("POLE_LEVEL", (double)d.PoleLevel);
            output.SetValue("CAL_LEVEL", (double)d.CalLevel);
            output.SetValue("DEVICE_ID", (double)(info?.DeviceId ?? 0));
            output.SetValue("MODES_STA", (double)(info?.ModesSta ?? 0));
            output.SetValue("MODES_ENABLED", info?.EnabledModesText ?? "");
            output.SetValue("Success", true);
            return output;
        }

        private async Task<DeviceParameters> ReadSettingAsync(DeviceParameters? _)
        {
            var output = new DeviceParameters { Variables = BuildSettingOutputs() };

            WqSetting? s = IsSimulationMode ? _simSetting : await FetchSettingAsync().ConfigureAwait(false);
            if (s is null)
            {
                output.SetValue("Success", false);
                return output;
            }

            FillSetting(output, s);
            output.SetValue("Success", true);
            return output;
        }

        private static void FillSetting(DeviceParameters output, WqSetting s)
        {
            output.SetValue("MANUAL_TEMP", Math.Round((double)s.ManualTemp, 1));
            output.SetValue("COND_REF_TEMP", Math.Round((double)s.CondRefTemp, 1));
            output.SetValue("COND_TEMP_COE", Math.Round((double)s.CondTempCoe, 3));
            output.SetValue("COND_TDS_COE", Math.Round((double)s.CondTdsCoe, 3));
            output.SetValue("COND_K", Math.Round((double)s.CondK, 3));
            output.SetValue("DO_SAL", Math.Round((double)s.DoSal, 1));
            output.SetValue("DO_PRESSURE", Math.Round((double)s.DoPressure, 1));
            output.SetValue("TEMP_UNIT_CODE", (double)s.TempUnit);
            output.SetValue("BEEP", s.IsBeep != 0);
            output.SetValue("DEVICE_LOCK", s.DeviceLock != 0);
            output.SetValue("BG_LIGHT", (double)s.BgLight);
            output.SetValue("SHUTDOWN_MIN", (double)s.Shutdown);
            output.SetValue("PH_WATER_TYPE", (double)s.PhWaterType);
            output.SetValue("PH_STD_TYPE", (double)s.PhStdType);
            output.SetValue("PH_DISP_RES", (double)s.PhDispRes);
            output.SetValue("COND_STD_TYPE", (double)s.CondStdType);
        }

        /// <summary>
        /// 单项设置命令。输入 TargetValue,范围 [min,max];decimals 决定输入框的小数位。
        /// 执行流程见 WriteSettingAsync。
        /// </summary>
        private void AddSettingCommand(string name, string help, double min, double max, int decimals, string? unit,
                                       Action<WqSetting, double> apply)
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
                OutputParameters = new DeviceParameters { Variables = BuildAckOutputs() },
                AsyncAction = AsyncWrapAction(name, p => WriteSettingAsync(name, p, min, max, apply))
            });
        }

        private async Task<DeviceParameters> WriteSettingAsync(string name, DeviceParameters? input,
                                                               double min, double max, Action<WqSetting, double> apply)
        {
            var output = new DeviceParameters { Variables = BuildAckOutputs() };

            double v = input?.GetNullableValue<double>("TargetValue") ?? double.NaN;
            if (double.IsNaN(v) || v < min || v > max)
            {
                output.SetValue("Success", false);
                output.SetValue("Message", $"{name}:参数 {v} 超出范围 {min}~{max},未下发");
                return output;
            }

            if (IsSimulationMode)
            {
                apply(_simSetting, v);
                output.SetValue("Success", true);
                output.SetValue("Message", "模拟模式,已记录");
                return output;
            }

            // 读-改-写。不用缓存的旧包做底,总是先读回当前值:仪器面板上也能改设置,
            // 拿旧包整包写回会把别人刚在面板上改的项冲掉。
            var cur = await FetchSettingAsync().ConfigureAwait(false);
            if (cur is null)
            {
                output.SetValue("Success", false);
                output.SetValue("Message", $"{name}:读取当前设置失败,未写入");
                return output;
            }

            var next = cur.Clone();
            apply(next, v);

            var resp = await TransactAsync(WqCmd.SetSetting, next.ToBytes()).ConfigureAwait(false);
            if (resp is null)
            {
                output.SetValue("Success", false);
                output.SetValue("Message", $"{name}:写设置无应答");
                return output;
            }
            if (!resp.Ok)
            {
                output.SetValue("Success", false);
                output.SetValue("Message", $"{name}:仪器返回 {WqResult.Name(resp.Result)}");
                return output;
            }

            _setting = next;

            // 回读确认。仪器写成功后立即存 Flash 并刷新界面,回读是让缓存与仪器一致,
            // 也顺带把"仪器实际收下的值"(比如背光被夹到有效范围)拿回来。
            var back = await FetchSettingAsync().ConfigureAwait(false);
            if (back is not null) _setting = back;

            _logger.LogInformation($"{Tag} {name} = {v} 已写入");
            output.SetValue("Success", true);
            output.SetValue("Message", $"{name} 已写入");
            return output;
        }

        private async Task<DeviceParameters> SetModeAsync(DeviceParameters? input)
        {
            var output = new DeviceParameters { Variables = BuildAckOutputs() };

            int mode = (int)(input?.GetNullableValue<double>("TargetValue") ?? -1);
            if (mode < 0 || mode >= WqMode.Count)
            {
                output.SetValue("Success", false);
                output.SetValue("Message", $"模式码 {mode} 无效(0~8)");
                return output;
            }

            if (IsSimulationMode)
            {
                _simMode = (byte)mode;
                output.SetValue("Success", true);
                output.SetValue("Message", $"模拟模式,已切到 {WqMode.Label(mode)}");
                return output;
            }

            // 文档 5.1:只切仪器已启用的模式。未启用的发下去仪器会怎样文档没写,不去试。
            var info = _info;
            if (info is not null && !info.IsModeEnabled(mode))
            {
                output.SetValue("Success", false);
                output.SetValue("Message", $"仪器未启用 {WqMode.Label(mode)} 模式(已启用:{info.EnabledModesText})");
                return output;
            }

            var resp = await TransactAsync(WqCmd.SetMode, new[] { (byte)mode }).ConfigureAwait(false);
            bool ok = resp is { Ok: true };
            output.SetValue("Success", ok);
            output.SetValue("Message", ok
                ? $"已切到 {WqMode.Label(mode)}"
                : $"切换模式失败:{(resp is null ? "无应答" : WqResult.Name(resp.Result))}");
            return output;
        }

        private async Task<DeviceParameters> ResetAsync(DeviceParameters? _)
        {
            var output = new DeviceParameters { Variables = BuildAckOutputs() };

            if (IsSimulationMode)
            {
                _simSetting = new WqSetting
                {
                    ManualTemp = 25f, CondRefTemp = 25f, CondTempCoe = 2.0f, CondTdsCoe = 0.5f, CondK = 1.0f,
                    DoPressure = 101.3f, IsBeep = 1, BgLight = 80, Shutdown = 5, PhDispRes = 2,
                };
                output.SetValue("Success", true);
                output.SetValue("Message", "模拟模式,设置已重置");
                return output;
            }

            var resp = await TransactAsync(WqCmd.Reset).ConfigureAwait(false);
            bool ok = resp is { Ok: true };
            if (ok)
            {
                _logger.LogInformation($"{Tag} 已恢复出厂设置");
                await FetchSettingAsync().ConfigureAwait(false);
            }
            output.SetValue("Success", ok);
            output.SetValue("Message", ok ? "已恢复出厂设置" : $"恢复出厂失败:{(resp is null ? "无应答" : WqResult.Name(resp.Result))}");
            return output;
        }

        private async Task<DeviceParameters> ReadRecordsAsync(DeviceParameters? input)
        {
            var output = new DeviceParameters
            {
                Variables = new ObservableCollection<ParameterBase>
                {
                    new NumberParameter("Count", 0, WqProtocol.MaxRecords, 0, "本次读到的条数", true),
                    new StringParameter("Records", "", "记录明细"),
                    new BooleanParameter("Success", false, "是否读完"),
                    new StringParameter("Message", "", "说明"),
                }
            };

            int start = (int)(input?.GetNullableValue<double>("StartIndex") ?? 0);
            int max = (int)(input?.GetNullableValue<double>("MaxCount") ?? WqProtocol.MaxRecords);
            start = Math.Clamp(start, 0, WqProtocol.MaxRecords - 1);
            max = Math.Clamp(max, 1, WqProtocol.MaxRecords);

            if (IsSimulationMode)
            {
                output.SetValue("Count", 2d);
                output.SetValue("Records", "0\tPH\t7.02 pH\t25.0℃\t2026-01-01 09:00:00\n1\tCOND\t1413 uS/cm\t25.0℃\t2026-01-01 09:05:00");
                output.SetValue("Success", true);
                output.SetValue("Message", "模拟模式");
                return output;
            }

            var sb = new StringBuilder();
            int count = 0;
            bool complete = false;
            string message = "";
            uint idx = (uint)start;
            var param = new byte[4];

            while (count < max)
            {
                // 参数是 4 字节大端 u32 索引(文档 6.6)
                BinaryPrimitives.WriteUInt32BigEndian(param, idx);

                // 只在第一条允许补联机;读到一半没应答就直接停,免得每条都多等好几秒
                var resp = await TransactAsync(WqCmd.GetRecord, param, rehandshake: count == 0).ConfigureAwait(false);
                if (resp is null)
                {
                    message = $"读第 {idx} 条无应答,中止";
                    break;
                }

                if (resp.Result == WqResult.Fail)
                {
                    // 文档 4.4:没有"记录总数"命令,索引越界返回 FAIL 就是读完了
                    complete = true;
                    break;
                }

                if (!resp.Ok)
                {
                    message = $"读第 {idx} 条仪器返回 {WqResult.Name(resp.Result)},中止";
                    break;
                }

                try
                {
                    sb.AppendLine(WqProtocol.ParseRecord(resp.Param).ToString());
                }
                catch (Exception ex)
                {
                    message = $"第 {idx} 条解析失败:{ex.Message}";
                    break;
                }

                count++;
                idx++;
                if (idx >= WqProtocol.MaxRecords)
                {
                    complete = true;
                    break;
                }
            }

            if (count >= max) complete = true;

            output.SetValue("Count", (double)count);
            output.SetValue("Records", sb.ToString().TrimEnd());
            output.SetValue("Success", complete);
            output.SetValue("Message", complete ? $"共读到 {count} 条" : message);

            if (!complete)
                _logger.LogWarning($"{Tag} 读记录未完成:{message},已读 {count} 条");
            return output;
        }

        /// <summary>模拟模式:按当前模拟模式造一份合理的读数。</summary>
        private WqData MakeSimulatedData()
        {
            double t = Environment.TickCount64 / 1000.0;
            double drift = Math.Sin(t / 7.0);
            (float value, byte unit, byte dec) = _simMode switch
            {
                WqMode.PH => ((float)(7.00 + 0.15 * drift), (byte)0x02, _simSetting.PhDispRes),
                WqMode.MV => ((float)(-2.0 + 8.0 * drift), (byte)0x03, (byte)1),
                WqMode.ORP => ((float)(210 + 6 * drift), (byte)0x03, (byte)0),
                WqMode.COND => ((float)(1413 + 12 * drift), (byte)0x01, (byte)0),
                WqMode.RES => ((float)(0.71 + 0.01 * drift), (byte)0x08, (byte)2),
                WqMode.TDS => ((float)(706 + 6 * drift), (byte)0x04, (byte)0),
                WqMode.SAL => ((float)(0.7 + 0.01 * drift), (byte)0x0B, (byte)2),
                WqMode.DO => ((float)(8.2 + 0.2 * drift), (byte)0x04, (byte)2),
                _ => ((float)(98.5 + 1.5 * drift), (byte)0x0C, (byte)1),
            };

            return new WqData
            {
                Value = value,
                Temp = (float)(25.0 + 0.3 * Math.Sin(t / 11.0)),
                Mode = _simMode,
                IsStable = Math.Abs(drift) < 0.8,
                Decimals = dec,
                UnitCode = unit,
                TempUnitCode = _simSetting.TempUnit,
                Atc = 1,
                PoleLevel = 3,
                CalLevel = 3,
            };
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
                        $"{Tag}[命令-完成] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    return output;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _logger.LogError(ex,
                        $"{Tag}[命令-异常] Device={Name} Command={commandName} DurationMs={sw.ElapsedMilliseconds}");
                    throw new DeviceCommandExecutionException(Name, commandName, ex.Message, ex);
                }
            };
        }

        #endregion
    }
}
