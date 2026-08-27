// ============================================================================
//  BleCore.cs
//  蓝牙代理进程里真正干活的那部分：Windows 原生 BLE（WinRT GATT 客户端）。
//
//  ── 手册没写 GATT 的 UUID，所以这里自己找 ──────────────────────────
//  《笔式酸度-电导率计 BLE 协议》从头到尾讲的是**帧格式**，一个字都没提
//  该连哪个 GATT 服务、哪个特征。这很常见：仪器厂商用的是现成的蓝牙透传模块
//  （Nordic / 汇顶 / 泰凌 / HM-10 之流），UUID 是模块厂定的，仪器厂商自己也不关心。
//
//  所以本类的做法是：连上之后把所有服务和特征枚举一遍，按下面的顺序挑：
//    1) 先按"已知透传模块"的 UUID 对照表精确匹配（命中率最高，见 KnownProfiles）；
//    2) 没命中就按能力匹配 —— 找一个能 Notify 的当接收、一个能 Write 的当发送，
//       且优先选同一个服务里的一对；
//    3) 还不行就报错，并把枚举到的服务/特征全列出来，
//       照着把 UUID 填进设备参数里手工指定即可。
//  调用方传了 UUID 就优先用，不再自动挑。
//
//  ── MTU 与分包 ──────────────────────────────────────────────────
//  BLE 默认 MTU 23，一次只能送 20 字节负载，而本协议最长的帧是
//  6 + 21 = 27 字节 —— 也就是说**一帧一定会被拆开**。两个方向都要处理：
//    · 收：通知按 20 字节切片到达，驱动那边的拼帧缓冲区本来就是按字节流写的，
//          天然吃得下，本进程原样转发即可；
//    · 发：按协商到的 MaxPduSize 切片写。透传模块收到后会自己拼回去。
//  Windows 在 BLE 4.2+ 上一般会协商到 517，但不能指望 —— 老模块就是 23。
//
//  ── 为什么这些代码在独立进程里 ────────────────────────────────────
//  见 MaxChemical.Ble.Proxy.csproj 顶部：WinRT 的依赖进不了 MEF 的 Plugins\ 目录。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace MaxChemical.Ble.Proxy
{
    /// <summary>扫描到的一台 BLE 设备。给"扫描蓝牙设备"命令用。</summary>
    public sealed record BleScanResult(string Name, ulong Address, short Rssi)
    {
        /// <summary>AA:BB:CC:DD:EE:FF 形式的地址,可直接填进设备参数。</summary>
        public string AddressText => BleCore.FormatAddress(Address);
    }

    public sealed class BleCore : IDisposable
    {
        /// <summary>
        /// 已知透传模块的 UUID 对照表:(服务, 写特征, 通知特征)。
        /// 写特征和通知特征相同表示该模块用同一个特征收发(HM-10 就是这样)。
        /// </summary>
        private static readonly (string Service, string Write, string Notify, string Vendor)[] KnownProfiles =
        {
            // Nordic UART Service。用 nRF5x 的模块基本都是它。
            ("6E400001-B5A3-F393-E0A9-E50E24DCCA9E", "6E400002-B5A3-F393-E0A9-E50E24DCCA9E",
             "6E400003-B5A3-F393-E0A9-E50E24DCCA9E", "Nordic UART"),

            // FFE0 的另一种型制:FFE1 只负责写,通知在 FFE4 上。
            // 本项目对接的这支笔就是这一种 —— 放在标准 HM-10 前面,
            // 因为两者服务 UUID 相同,谁在前谁先被选中。
            ("0000FFE0-0000-1000-8000-00805F9B34FB", "0000FFE1-0000-1000-8000-00805F9B34FB",
             "0000FFE4-0000-1000-8000-00805F9B34FB", "FFE0(FFE1 写 / FFE4 通知)"),

            // HM-10 / CC254x 标准型制:一个特征既能写又能通知
            ("0000FFE0-0000-1000-8000-00805F9B34FB", "0000FFE1-0000-1000-8000-00805F9B34FB",
             "0000FFE1-0000-1000-8000-00805F9B34FB", "HM-10 / CC254x"),

            // 国产模块常见的 FFF0:FFF2 写、FFF1 通知
            ("0000FFF0-0000-1000-8000-00805F9B34FB", "0000FFF2-0000-1000-8000-00805F9B34FB",
             "0000FFF1-0000-1000-8000-00805F9B34FB", "FFF0 通用透传"),

            // ISSC / Microchip BM70-71
            ("49535343-FE7D-4AE5-8FA9-9FAFD205E455", "49535343-8841-43F4-A8D4-ECBE34729BB3",
             "49535343-1E4D-4BD9-BA61-23C647249616", "ISSC / BM7x"),

            // 芯海 / 部分血压计秤类模块
            ("0000FEE0-0000-1000-8000-00805F9B34FB", "0000FEE1-0000-1000-8000-00805F9B34FB",
             "0000FEE2-0000-1000-8000-00805F9B34FB", "FEE0 系列"),
        };

        /// <summary>
        /// 源码版本戳。**改动本文件时请手动更新**。
        ///
        /// 加这个是因为吃过亏:改完代码、重新生成,现场日志却和改之前一模一样 ——
        /// 实际跑的还是旧 exe(当时 Shell.csproj 里那个"exe 存在就不重编"的条件
        /// 把代理挡住了)。日志里没有版本号的话,这种事只能靠逐行比对日志文本去猜。
        /// 现在连接时第一行就打出来,对不上立刻就知道是构建没生效,而不是代码没用。
        /// </summary>
        public const string SourceStamp = "2026-08-27-f";

        private readonly string _wantName;
        private readonly ulong _wantAddress;          // 0 = 未指定
        private readonly Guid _forcedService;         // Guid.Empty = 自动
        private readonly Guid _forcedWrite;
        private readonly Guid _forcedNotify;
        private readonly int _scanTimeoutMs;
        private readonly Action<string> _log;

        private BluetoothLEDevice? _device;
        private GattDeviceService? _service;
        private GattCharacteristic? _writeChar;
        private GattCharacteristic? _notifyChar;
        private GattSession? _session;
        private volatile bool _open;
        private volatile bool _closing;

        /// <summary>一次写最多能带多少负载。连上后按协商到的 MTU 更新。</summary>
        private int _writeChunk = 20;

        private GattWriteOption _writeOption = GattWriteOption.WriteWithoutResponse;

        /// <summary>整帧发送的串行闸门。见 WriteAsync 里的说明:两帧交错会都成乱码。</summary>
        private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);

        /// <summary>按名字找到地址后缓存下来,重试时不必再扫一遍(扫描是这里最慢的一步)。</summary>
        private ulong _resolvedAddress;

        /// <summary>这次失败不是链路抖动,重试也没用(比如附近根本没有这台设备)。</summary>
        private bool _noRetry;

        /// <summary>
        /// 本次连接打开过的全部 GATT 服务。每个都持有系统句柄,统一在拆连接时释放。
        /// 不能在挑完候选就把"没选中的"释放掉 —— 候选是在订阅阶段才逐组试的,
        /// 提前释放会让后面的每一步都抛 ObjectDisposedException。
        /// </summary>
        private readonly List<GattDeviceService> _openedServices = new List<GattDeviceService>();

        /// <summary>一组可能的收发特征。</summary>
        private sealed class Candidate
        {
            public GattDeviceService Service = null!;
            public GattCharacteristic Write = null!;
            public GattCharacteristic Notify = null!;
            /// <summary>怎么挑出来的。写进日志,便于判断该信哪一组。</summary>
            public string Source = "";

            public override string ToString() =>
                $"{Source}:服务 {Service.Uuid} / 写 {Write.Uuid} / 通知 {Notify.Uuid}";
        }

        /// <summary>
        /// 所有可能的收发组合,按可信度排序。
        ///
        /// 为什么要留一整份列表而不是挑中一组就走:一台设备完全可能同时暴露
        /// 好几个"长得像透传"的服务(厂商自己的 + 模块自带的)。命中对照表
        /// 第一条就收工的话,万一那条恰好是不通的那个,就再没有第二次机会 ——
        /// 而现象只是"订阅一直失败",完全看不出是选错了对象。
        /// 现在订阅时挨个试,哪组真能订阅上就用哪组。
        /// </summary>
        private readonly List<Candidate> _candidates = new List<Candidate>();

        public BleCore(string wantName, string wantAddressText,
                          string serviceUuid, string writeUuid, string notifyUuid,
                          int scanTimeoutMs, Action<string> log)
        {
            _wantName = (wantName ?? "").Trim();
            _wantAddress = ParseAddress(wantAddressText);
            _forcedService = ParseGuid(serviceUuid);
            _forcedWrite = ParseGuid(writeUuid);
            _forcedNotify = ParseGuid(notifyUuid);
            _scanTimeoutMs = scanTimeoutMs;
            _log = log ?? (_ => { });
        }

        public bool IsOpen => _open;
        public string? LastError { get; private set; }

        public string Description =>
            _wantAddress != 0 ? $"蓝牙 {FormatAddress(_wantAddress)}"
            : !string.IsNullOrEmpty(_wantName) ? $"蓝牙 “{_wantName}”"
            : "蓝牙(自动选择)";

        public event Action<byte[]>? BytesReceived;
        public event Action? Closed;

        #region 连接

        /// <summary>
        /// 连接。内部会重试几次。
        ///
        /// 为什么要重试:Windows 的 BLE 是**按需建链**的 —— 拿到 BluetoothLEDevice
        /// 对象并不代表射频链路已经建立,第一次去读 GATT 服务时链路可能还在建,
        /// 于是返回 Unreachable。实测就出现过"第一次 Unreachable、第二次直接连上"。
        /// 让操作员看见一次抖动就以为设备坏了,不合适。
        /// </summary>
        public async Task<bool> OpenAsync(CancellationToken ct)
        {
            _log($"BleCore {SourceStamp}");

            const int Attempts = 3;

            for (int i = 1; i <= Attempts; i++)
            {
                if (ct.IsCancellationRequested) { LastError = "连接被取消"; return false; }

                if (await OpenOnceAsync(ct).ConfigureAwait(false)) return true;

                // 设备压根不在附近这种失败,重试三次只是让人多等 40 秒,
                // 错误信息还是同一条。只对"可能是链路抖动"的失败重试。
                if (_noRetry) break;
                if (i >= Attempts) break;

                _log($"第 {i} 次连接失败({LastError});{i * 800}ms 后重试");
                CleanupPartial();
                try { await Task.Delay(i * 800, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { LastError = "连接被取消"; return false; }
            }

            return false;
        }

        private async Task<bool> OpenOnceAsync(CancellationToken ct)
        {
            bool notPaired = false;

            try
            {
                ulong address = _wantAddress != 0 ? _wantAddress : _resolvedAddress;

                if (address == 0)
                {
                    address = await ResolveAddressAsync(ct).ConfigureAwait(false);
                    if (address == 0)
                    {
                        // LastError 已在里面写好。扫不到设备不是链路抖动,别重试。
                        _noRetry = true;
                        return false;
                    }
                    _resolvedAddress = address;   // 重试时直接用,省掉一轮扫描
                }

                _device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(ct).ConfigureAwait(false);
                if (_device is null)
                {
                    LastError = $"找不到蓝牙设备 {FormatAddress(address)}。请确认仪器已开机、在有效范围内。";
                    return false;
                }

                _log($"已定位设备 {_device.Name} ({FormatAddress(address)})");
                _device.ConnectionStatusChanged += OnConnectionStatusChanged;

                notPaired = await EnsurePairedAsync(ct).ConfigureAwait(false);

                if (!await DiscoverCoreAsync(ct).ConfigureAwait(false))
                {
                    if (notPaired) LastError += PairingHint;
                    return false;
                }

                // 订阅。收发特征的选定、保持链路、安全等级提升都在里面 ——
                // 因为要按候选逐组试,这些都得跟着候选走。
                if (!await SubscribeAsync(ct, notPaired).ConfigureAwait(false)) return false;

                _open = true;
                return true;
            }
            catch (OperationCanceledException)
            {
                LastError = "连接被取消";
                return false;
            }
            catch (Exception ex)
            {
                // 这里以前只写 ex.Message,而 WinRT 抛出来的异常**经常 Message 是空的**,
                // 结果日志里只有"蓝牙连接失败:"后面什么都没有,完全没法排查。
                // Describe 会把类型名和 HRESULT 一并带上,并把常见 HRESULT 翻成人话。
                LastError = "蓝牙连接失败:" + Describe(ex);
                if (notPaired) LastError += PairingHint;
                return false;
            }
        }

        /// <summary>
        /// 订阅通知特征。没有这一步,仪器推的数据到不了我们这儿。
        ///
        /// ── 为什么要显式提升链路安全等级 ──────────────────────────────
        /// 实测碰到过:设备**已经和本机配对**,订阅 CCCD 依然报
        ///     COMException 0x80650003 (ATT: 鉴权不足)
        /// 配对(bonding)和**链路加密**是两码事。Windows 建立 BLE 链路时默认是
        /// 明文的,只有在明确要求时才会去协商加密。而 HM-10 这类模块的 FFE1
        /// 特征要求加密链路,于是明文链路上的一切操作都被拒。
        ///
        /// 设了 GattCharacteristic.ProtectionLevel,Windows 会在下一次 GATT 操作
        /// 之前先把链路加密起来 —— 这才是 0x80650003 的正解,再配对多少次都没用。
        ///
        /// 分两档试:先 EncryptionRequired,还不行再上
        /// EncryptionAndAuthenticationRequired(要求 MITM 保护的配对)。
        /// 不一上来就用最高档,是因为有些模块的 bond 本身不带 MITM,
        /// 直接要求认证反而会失败。
        /// </summary>
        private async Task<bool> SubscribeAsync(CancellationToken ct, bool notPaired)
        {
            Exception? last = null;
            GattCommunicationStatus lastStatus = GattCommunicationStatus.Unreachable;

            // 逐档提升。Plain 就是"照原样试一次",多数模块到这里就成了。
            var levels = new[]
            {
                GattProtectionLevel.Plain,
                GattProtectionLevel.EncryptionRequired,
                GattProtectionLevel.EncryptionAndAuthenticationRequired,
            };

            for (int ci = 0; ci < _candidates.Count; ci++)
            {
                var cand = _candidates[ci];
                if (ct.IsCancellationRequested) { LastError = "连接被取消"; return false; }

                _log($"尝试第 {ci + 1}/{_candidates.Count} 组 —— {cand}");

                // 先按这一组接上,失败了下一轮会被下一组覆盖掉
                _service = cand.Service;
                _writeChar = cand.Write;
                _notifyChar = cand.Notify;
                PickWriteOption();

                // 保持链路 + 取 MTU。GattSession 是按设备的,任何一个服务拿到的都一样,
                // 所以放在这里、每组都设一次也不会有副作用。
                KeepAliveAndNegotiate();

                // 申请访问权。多数情况直接 Allowed,但设备被别的应用独占、
                // 或系统隐私设置拦了的时候这里会明确告知,好过后面报一个
                // 语焉不详的 GATT 错误。
                await RequestAccessAsync(ct).ConfigureAwait(false);

                _notifyChar.ValueChanged += OnValueChanged;

                var cccd = _notifyChar.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
                    ? GattClientCharacteristicConfigurationDescriptorValue.Notify
                    : GattClientCharacteristicConfigurationDescriptorValue.Indicate;

                foreach (var level in levels)
                {
                    if (ct.IsCancellationRequested) { LastError = "连接被取消"; return false; }

                    if (level != GattProtectionLevel.Plain)
                    {
                        _log($"提升链路安全等级到 {level} 后重试订阅");
                        if (!TryElevate(_notifyChar, level)) continue;
                        // 收发是同一个特征时(HM-10 就是)不必再设一遍
                        if (!ReferenceEquals(_writeChar, _notifyChar) && _writeChar is not null)
                            TryElevate(_writeChar, level);

                        // 踢一脚,逼 Windows 现在就去协商加密。
                        // 设 ProtectionLevel 本身不会立刻加密,要等下一次 GATT 操作;
                        // 而"下一次操作"如果就是我们要做的 CCCD 写,它自己往往先失败一次。
                        await NudgeEncryptionAsync(ct).ConfigureAwait(false);
                    }

                    // 同一档试两次:安全等级提升在 Windows 上是异步生效的,
                    // 第一次往往赶在协商完成之前。第二次之前给它一点时间。
                    for (int attempt = 1; attempt <= 2; attempt++)
                    {
                        if (attempt == 2)
                        {
                            try { await Task.Delay(400, ct).ConfigureAwait(false); }
                            catch (OperationCanceledException) { LastError = "连接被取消"; return false; }
                        }

                        try
                        {
                            var status = await _notifyChar
                                .WriteClientCharacteristicConfigurationDescriptorAsync(cccd)
                                .AsTask(ct).ConfigureAwait(false);

                            if (status == GattCommunicationStatus.Success)
                            {
                                _log($"订阅成功 —— {cand},安全等级 {level}");
                                return true;
                            }

                            lastStatus = status;
                            last = null;
                            _log($"订阅失败({status}),安全等级 {level},第 {attempt} 次");
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            last = ex;
                            _log($"订阅出错(安全等级 {level},第 {attempt} 次):{Describe(ex)}");

                            // 只有"安全等级不够"这一类才值得往上提。别的错
                            // (设备没连上、被别人占着)提升多少档都一样,换下一组候选。
                            if (!IsSecurityError(ex.HResult)) goto nextCandidate;
                        }
                    }
                }

            nextCandidate:
                // 这一组没成:把事件摘掉再换下一组,否则同一个回调会挂好几次,
                // 真连上之后每帧数据会被重复投递。
                try { _notifyChar.ValueChanged -= OnValueChanged; } catch { }
            }

            LastError = last is not null
                ? "订阅通知特征失败:" + Describe(last)
                : $"订阅通知特征失败({lastStatus})。";

            if (_candidates.Count > 1)
                LastError += $"(已把 {_candidates.Count} 组收发特征逐一试过)";

            if (last is not null && IsSecurityError(last.HResult))
                LastError += EncryptionHint;
            else
                LastError += "若仪器已在别的软件(比如手机 App)里连着,请先断开那边再试。";

            if (notPaired) LastError += PairingHint;

            // ── 最后一招:不写 CCCD 直接听 ────────────────────────────
            // 便宜的透传模块里有相当一部分**不管上位机有没有写 CCCD 都照推不误**
            // (不合规范,但很常见)。既然正规订阅已经全试过一遍了,
            // 不如把回调挂在最可信的那一组上,直接听听看。
            //
            // 这样做不会造成"假连上":驱动那边本来就要求 3 秒内收到一帧合法数据
            // 才算连接成功,收不到照样报失败,只是错误信息换成"没收到数据"。
            // 也就是说这一招要么白赚一个能用的连接,要么什么都不改变。
            if (_candidates.Count > 0)
            {
                var fallback = _candidates[0];
                _service = fallback.Service;
                _writeChar = fallback.Write;
                _notifyChar = fallback.Notify;
                PickWriteOption();

                _log("正规订阅全部失败,改为不写 CCCD 直接监听 —— " +
                     $"{fallback}。部分透传模块不写 CCCD 也会推数据,值得一试;" +
                     "若几秒内仍收不到数据,驱动会照常报连接失败。");
                _log("订阅失败的原因:" + LastError);

                _notifyChar.ValueChanged += OnValueChanged;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 触发一次链路加密协商。
        ///
        /// 设了 ProtectionLevel 并不会立刻加密,Windows 要等**下一次 GATT 操作**
        /// 才去协商,而且协商本身是异步的。如果那"下一次操作"就是我们真正要做的
        /// CCCD 写,它十有八九赶在协商完成之前先失败一次。
        /// 所以先拿一个无副作用的读去踢一脚 —— 成不成都无所谓,目的只是把加密
        /// 这件事启动起来。特征不可读时就跳过,后面靠同一档重试两次兜。
        /// </summary>
        private async Task NudgeEncryptionAsync(CancellationToken ct)
        {
            var ch = _notifyChar;
            if (ch is null) return;
            if (!ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read)) return;

            try
            {
                var r = await ch.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(ct).ConfigureAwait(false);
                _log($"触发加密协商的试读:{r.Status}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 预期之中:第一次读多半就是被同一个 0x80650003 拒掉的,
                // 但这一下已经足以让 Windows 开始协商加密。
                _log($"触发加密协商的试读被拒(正常):{Describe(ex)}");
            }
        }

        /// <summary>
        /// 申请服务访问权。失败不当致命错误 —— 有些系统上这个调用本身就返回
        /// Unspecified,但后续 GATT 操作照样能做。只把结果记进日志。
        /// </summary>
        private async Task RequestAccessAsync(CancellationToken ct)
        {
            if (_service is null) return;
            try
            {
                var access = await _service.RequestAccessAsync().AsTask(ct).ConfigureAwait(false);
                if (access != DeviceAccessStatus.Allowed)
                    _log($"服务访问权申请返回 {access}(继续尝试)");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log($"服务访问权申请出错(继续尝试):{Describe(ex)}");
            }
        }

        /// <summary>设安全等级。设不上不算致命,返回 false 让调用方跳过这一档。</summary>
        private bool TryElevate(GattCharacteristic ch, GattProtectionLevel level)
        {
            try
            {
                ch.ProtectionLevel = level;
                return true;
            }
            catch (Exception ex)
            {
                _log($"设置安全等级 {level} 失败:{Describe(ex)}");
                return false;
            }
        }

        /// <summary>这个 HRESULT 是不是"链路安全等级不够"这一类。</summary>
        private static bool IsSecurityError(int hr) => unchecked((uint)hr) switch
        {
            0x80650003 => true,   // 鉴权不足
            0x80650005 => true,   // 授权不足
            0x80650008 => true,   // 加密不足
            0x8065000F => true,   // 加密强度不足
            _ => false
        };

        private const string EncryptionHint =
            "\r\n※ 这一类错误是**链路没有加密**,而不是没配对 —— 两者是两码事。" +
            "程序已经自动逐档提升安全等级重试过了,仍然不行的话依次试:" +
            "① 在 Windows 蓝牙设置里把这台设备删除,再重新配对一次;" +
            "② 确认这支笔没有同时连着手机 App 或别的软件(BLE 从机同一时刻通常只接受一个主机);" +
            "③ 把笔关机再开机,让它重新广播。";

        private const string PairingHint =
            "\r\n※ 这台仪器还没有和本机配对,而它的特征多半要求加密链路 —— 这很可能就是失败的原因。" +
            "请在 Windows「设置 → 蓝牙和其他设备 → 添加设备 → 蓝牙」里手动配对一次" +
            "(这类透传模块的 PIN 通常是 000000 或 123456,以仪器说明书为准)," +
            "配对成功后再回到这里连接。配对是一次性的,以后不用再做。";

        /// <summary>
        /// 检查配对状态,没配对就试一次"无需确认"的自动配对。
        /// 返回 true 表示**仍未配对**,调用方在后续失败时把提示追加到错误信息里。
        ///
        /// 为什么不配对失败就直接返回错误:有些透传模块的特征并不要求加密,
        /// 没配对照样能用。直接拦下来会把本来能工作的设备挡在外面,
        /// 所以这里只记录状态,真失败了再把它作为最可能的原因说出来。
        /// </summary>
        private async Task<bool> EnsurePairedAsync(CancellationToken ct)
        {
            DeviceInformationPairing? pairing;
            try { pairing = _device?.DeviceInformation?.Pairing; }
            catch { return false; }

            if (pairing is null) return false;

            if (pairing.IsPaired)
            {
                // 顺带把配对的保护等级记下来。0x80650003 排查时这一项很有用:
                // 若这里是 None,说明 bond 本身不带加密能力,再怎么提升
                // 特征的 ProtectionLevel 也协商不出加密链路,只能删掉重新配对。
                string prot;
                try { prot = pairing.ProtectionLevel.ToString(); } catch { prot = "未知"; }
                _log($"设备已与本机配对(保护等级 {prot})");
                return false;
            }

            if (!pairing.CanPair)
            {
                _log("设备未配对,且系统报告不支持自动配对");
                return true;
            }

            _log("设备未配对,尝试自动配对(仅限无需输入 PIN 的那种)");
            try
            {
                var custom = pairing.Custom;

                void OnPairingRequested(DeviceInformationCustomPairing s, DevicePairingRequestedEventArgs e)
                {
                    // 只能处理"确认即可"的配对。要输 PIN 的没法在这儿凭空变出来 ——
                    // 那种只能让操作员去 Windows 蓝牙设置里配一次。
                    if (e.PairingKind == DevicePairingKinds.ConfirmOnly) e.Accept();
                }

                custom.PairingRequested += OnPairingRequested;
                try
                {
                    var result = await custom
                        .PairAsync(DevicePairingKinds.ConfirmOnly, DevicePairingProtectionLevel.None)
                        .AsTask(ct).ConfigureAwait(false);

                    _log($"自动配对结果:{result.Status}");
                    bool ok = result.Status == DevicePairingResultStatus.Paired
                           || result.Status == DevicePairingResultStatus.AlreadyPaired;
                    return !ok;
                }
                finally
                {
                    custom.PairingRequested -= OnPairingRequested;
                }
            }
            catch (Exception ex)
            {
                _log($"自动配对出错(不影响继续尝试连接):{Describe(ex)}");
                return true;
            }
        }

        /// <summary>连接失败后、重试之前的清理。不置 _closing,免得把真正的断连事件也吞掉。</summary>
        private void CleanupPartial()
        {
            try { if (_notifyChar is not null) _notifyChar.ValueChanged -= OnValueChanged; } catch { }
            try { if (_device is not null) _device.ConnectionStatusChanged -= OnConnectionStatusChanged; } catch { }
            foreach (var svc in _openedServices)
            {
                try { svc.Dispose(); } catch { }
            }
            _openedServices.Clear();
            _candidates.Clear();

            try { _device?.Dispose(); } catch { }

            _notifyChar = null;
            _writeChar = null;
            _service = null;
            _session = null;
            _device = null;
        }

        /// <summary>
        /// 没给地址时,按名字找。先查已配对列表,再退到广播扫描 ——
        /// 已配对的设备如果正被系统连着可能不再发广播,只扫广播会漏掉。
        /// </summary>
        private async Task<ulong> ResolveAddressAsync(CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(_wantName))
            {
                try
                {
                    var selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
                    var paired = await DeviceInformation.FindAllAsync(selector).AsTask(ct).ConfigureAwait(false);
                    foreach (var info in paired)
                    {
                        if (!NameMatches(info.Name)) continue;
                        using var dev = await BluetoothLEDevice.FromIdAsync(info.Id).AsTask(ct).ConfigureAwait(false);
                        if (dev is null) continue;
                        _log($"在已配对设备里找到 “{info.Name}”");
                        return dev.BluetoothAddress;
                    }
                }
                catch (Exception ex)
                {
                    _log("枚举已配对设备失败(忽略,继续扫描): " + Describe(ex));
                }
            }

            var found = await ScanAsync(_scanTimeoutMs, _wantName, ct).ConfigureAwait(false);
            if (found.Count > 0) return found[0].Address;

            // 带过滤的那轮没扫到,再不带过滤扫一次。
            //
            // 这一轮不只是"为了把附近设备列出来给人看" —— 它还会**再判一次名字**。
            // 实测出现过:带过滤扫 10 秒一无所获,紧接着不带过滤扫 4 秒就扫到了目标。
            // 原因是刚断开连接的 BLE 从机要过一会儿才恢复广播,
            // 而带过滤那轮正好整个落在它的静默期里。
            // 既然这一轮扫到了,就直接用,没有理由报"找不到"。
            var all = await ScanAsync(Math.Min(_scanTimeoutMs, 4000), "", ct).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(_wantName))
            {
                var late = all.FirstOrDefault(d => NameMatches(d.Name));
                if (late is not null)
                {
                    _log($"第二轮扫描找到 “{late.Name}”({late.AddressText}) —— " +
                         "第一轮多半赶上了它断开后的广播静默期");
                    return late.Address;
                }
            }

            var list = all.Count == 0
                ? "(附近没有扫描到任何 BLE 设备)"
                : string.Join("、", all.Take(10).Select(d => $"{d.Name}[{d.AddressText}]"));

            LastError = string.IsNullOrEmpty(_wantName)
                ? $"没有指定「蓝牙设备名」或「蓝牙地址」,无法确定要连哪一台。附近扫到:{list}"
                : $"没有扫描到名称包含 “{_wantName}” 的设备。附近扫到:{list}";
            return 0;
        }

        private bool NameMatches(string? name) =>
            !string.IsNullOrEmpty(name) &&
            name.Contains(_wantName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 广播扫描。<paramref name="nameFilter"/> 为空表示不过滤,收集所有有名字的设备。
        /// </summary>
        public static Task<List<BleScanResult>> ScanAsync(int timeoutMs, string nameFilter, CancellationToken ct)
        {
            var seen = new Dictionary<ulong, BleScanResult>();
            var tcs = new TaskCompletionSource<List<BleScanResult>>(TaskCreationOptions.RunContinuationsAsynchronously);

            var watcher = new BluetoothLEAdvertisementWatcher
            {
                // 主动扫描才能拿到 ScanResponse 里的完整名字;被动扫描经常只有地址
                ScanningMode = BluetoothLEScanningMode.Active
            };

            void Finish()
            {
                try { watcher.Stop(); } catch { }
                lock (seen)
                {
                    tcs.TrySetResult(seen.Values.OrderByDescending(d => d.Rssi).ToList());
                }
            }

            watcher.Received += (_, e) =>
            {
                var name = e.Advertisement?.LocalName ?? "";
                if (string.IsNullOrEmpty(name)) return;          // 没名字的没法给人选

                if (!string.IsNullOrEmpty(nameFilter) &&
                    !name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase)) return;

                lock (seen)
                {
                    seen[e.BluetoothAddress] = new BleScanResult(name, e.BluetoothAddress, e.RawSignalStrengthInDBm);
                    // 指定了名字时,扫到第一台就够了,不必等满超时
                    if (!string.IsNullOrEmpty(nameFilter)) { Finish(); return; }
                }
            };

            watcher.Start();

            var timer = new Timer(_ => Finish(), null, timeoutMs, Timeout.Infinite);
            ct.Register(Finish);
            return tcs.Task.ContinueWith(t => { timer.Dispose(); return t.Result; }, TaskScheduler.Default);
        }

        /// <summary>
        /// 枚举服务与特征,挑出收发那一对。
        ///
        /// 包一层是为了收尾:GetGattServicesAsync 返回的每个 GattDeviceService
        /// 都持有一个系统句柄,不 Dispose 就一直挂着。选中的那个要留着用(由
        /// <see cref="Dispose"/> 负责),**其余的必须当场释放** ——
        /// 否则每连一次就漏一批,连断几十次之后会连不上,而且现象很像"蓝牙坏了"。
        /// </summary>
        private async Task<bool> DiscoverCoreAsync(CancellationToken ct)
        {
            // Uncached:必须真的去设备上读一次。用缓存的话,仪器换过固件
            // 或者上次连的是别的设备时会拿到过时的服务表。
            var svcResult = await _device!.GetGattServicesAsync(BluetoothCacheMode.Uncached)
                                          .AsTask(ct).ConfigureAwait(false);

            if (svcResult.Status != GattCommunicationStatus.Success)
            {
                LastError = $"读取 GATT 服务失败({svcResult.Status})。" +
                            "常见原因:仪器尚未与本机配对、已被手机 App 或另一个程序占用、" +
                            "距离太远,或者已经休眠/自动关机。";
                return false;
            }

            // **所有打开过的服务都留到连接拆除时再释放。**
            // 之前是 discovery 一结束就把"没选中的"释放掉,而选定候选的动作
            // 在订阅阶段才发生 —— 那时 _service 还是 null,于是**全部**被当成
            // 没选中的释放了,后面每一步都报 ObjectDisposedException。
            // 多留几个句柄的代价,远小于这种错的排查成本。
            var services = svcResult.Services.ToList();
            _openedServices.AddRange(services);

            var table = new List<(GattDeviceService Svc, List<GattCharacteristic> Chars)>();
            var inventory = new StringBuilder();

            foreach (var svc in services)
            {
                var chResult = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached)
                                        .AsTask(ct).ConfigureAwait(false);
                var chars = chResult.Status == GattCommunicationStatus.Success
                    ? chResult.Characteristics.ToList()
                    : new List<GattCharacteristic>();
                table.Add((svc, chars));

                inventory.AppendLine($"  服务 {svc.Uuid}");
                foreach (var c in chars)
                    inventory.AppendLine($"    特征 {c.Uuid}  [{c.CharacteristicProperties}]");
            }

            // **无条件把清单打出来。**
            // 以前只在"完全没匹配上"时才打,结果命中对照表的设备反而没法查 ——
            // 订阅一直失败,却始终不知道这台设备到底还有哪些服务可选,只能靠猜。
            // 这几行日志的价值远大于它占的篇幅。
            _log("GATT 清单:\r\n" + inventory.ToString().TrimEnd());

            _candidates.Clear();

            // ① 设备参数里手工指定了 UUID —— 排最前
            if (_forcedService != Guid.Empty || _forcedWrite != Guid.Empty || _forcedNotify != Guid.Empty)
            {
                Collect(table, _forcedService, _forcedWrite, _forcedNotify, "参数指定");
                if (_candidates.Count == 0)
                {
                    LastError = "设备参数里指定的 UUID 在仪器上找不到。实际枚举到的是:\r\n" + inventory;
                    return false;
                }
            }

            // ② 已知透传模块对照表。**全部收下,不是命中一条就停** ——
            //    理由见 _candidates 的注释。
            foreach (var profile in KnownProfiles)
                Collect(table, Guid.Parse(profile.Service), Guid.Parse(profile.Write),
                        Guid.Parse(profile.Notify), profile.Vendor);

            // ③ 按能力兜底:每个服务里凑一对"能通知的"+"能写的"
            foreach (var (svc, chars) in table)
            {
                var notify = chars.FirstOrDefault(CanNotify);
                var write = chars.FirstOrDefault(CanWrite);

                if (notify is null || write is null) continue;
                AddCandidate(svc, write, notify, "按能力");
            }

            if (_candidates.Count == 0)
            {
                LastError = "在仪器上找不到可用的收发特征(需要一个能 Notify、一个能 Write)。枚举到的是:\r\n" + inventory;
                return false;
            }

            _log($"可尝试的收发组合 {_candidates.Count} 组:\r\n  " +
                 string.Join("\r\n  ", _candidates.Select((c, i) => $"[{i + 1}] {c}")));
            return true;
        }

        /// <summary>按给定 UUID 从服务表里挑出匹配的收发对,追加到候选列表。</summary>
        private void Collect(List<(GattDeviceService Svc, List<GattCharacteristic> Chars)> table,
                             Guid service, Guid write, Guid notify, string source)
        {
            foreach (var (svc, chars) in table)
            {
                if (service != Guid.Empty && svc.Uuid != service) continue;

                // **按 UUID 找到之后还要验能力。**
                // 实测碰到过:某台设备的 FFE1 只有 [Write],通知在另一个特征 FFE4 上,
                // 而对照表里 HM-10 那条写的是"FFE1 收发同一个特征"。
                // 光比 UUID 就会把一个根本不能通知的特征当成通知特征收进候选,
                // 白白多试一组,还把真正对的那组挤到后面。
                var w = write == Guid.Empty
                    ? chars.FirstOrDefault(CanWrite)
                    : chars.FirstOrDefault(c => c.Uuid == write && CanWrite(c));

                var n = notify == Guid.Empty
                    ? chars.FirstOrDefault(CanNotify)
                    : chars.FirstOrDefault(c => c.Uuid == notify && CanNotify(c));

                if (w is null || n is null) continue;
                AddCandidate(svc, w, n, source);
            }
        }

        private static bool CanWrite(GattCharacteristic c) =>
            c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Write) ||
            c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);

        private static bool CanNotify(GattCharacteristic c) =>
            c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify) ||
            c.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate);

        /// <summary>加一个候选。同一组收发特征只留一次。</summary>
        private void AddCandidate(GattDeviceService svc, GattCharacteristic write,
                                  GattCharacteristic notify, string source)
        {
            foreach (var c in _candidates)
                if (c.Service.Uuid == svc.Uuid && c.Write.Uuid == write.Uuid && c.Notify.Uuid == notify.Uuid)
                    return;

            _candidates.Add(new Candidate { Service = svc, Write = write, Notify = notify, Source = source });
        }

        /// <summary>
        /// 透传模块基本都支持 WriteWithoutResponse,那样吞吐更好;
        /// 不支持才退到需要应答的 Write。
        /// </summary>
        private void PickWriteOption()
        {
            _writeOption = _writeChar!.CharacteristicProperties
                                      .HasFlag(GattCharacteristicProperties.WriteWithoutResponse)
                ? GattWriteOption.WriteWithoutResponse
                : GattWriteOption.WriteWithResponse;
        }

        /// <summary>
        /// 让 Windows 保持链路,顺便取一下协商到的 MTU。
        ///
        /// MaintainConnection 这一项要紧:BLE 默认是**按需连接**的,两次 GATT 操作
        /// 之间系统会把链路断掉。而这台仪器靠的是**主动推通知** —— 链路一断就
        /// 再也收不到数据,表现是"连上了但一直没读数",极难查。
        /// 必须在订阅通知**之前**打开,否则订阅期间链路就可能已经断了。
        /// </summary>
        private void KeepAliveAndNegotiate()
        {
            try
            {
                _session = _service?.Session;
                if (_session is not null)
                {
                    try { _session.MaintainConnection = true; }
                    catch (Exception ex) { _log($"设置保持链路失败(继续):{Describe(ex)}"); }

                    // MaxPduSize 含 3 字节 ATT 头
                    int payload = _session.MaxPduSize - 3;
                    if (payload >= 20) _writeChunk = payload;
                }
                _log($"单次可写 {_writeChunk} 字节(最长的一帧 27 字节,不够就自动拆包)");
            }
            catch
            {
                _writeChunk = 20;      // 拿不到就按 BLE 默认 MTU 走,一定安全
            }
        }

        private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
        {
            if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected) return;
            if (_closing) return;                 // 我们自己关的,不算掉线

            // 还没连成功就别报"链路已断开"。连接过程中 Windows 会反复建链/断链
            // (枚举服务前后各一次很常见),那不是掉线。
            // 之前没这道闸门,日志里连接阶段就冒出两三条"链路已断开",
            // 看着像设备在抖,其实完全是正常的按需建链。
            if (!_open) return;

            _open = false;
            Closed?.Invoke();
        }

        #endregion

        #region 收发

        private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            try
            {
                var reader = DataReader.FromBuffer(args.CharacteristicValue);
                var bytes = new byte[reader.UnconsumedBufferLength];
                reader.ReadBytes(bytes);
                if (bytes.Length > 0) BytesReceived?.Invoke(bytes);
            }
            catch
            {
                // 单个通知解析失败不该把链路拖掉,丢掉这一片,拼帧那边会重同步
            }
        }

        public async Task<bool> WriteAsync(byte[] frame, CancellationToken ct = default)
        {
            var ch = _writeChar;
            if (!_open || ch is null) return false;

            // 一帧要分几片发时,中间不能插进别的帧 —— 透传模块只会按到达顺序
            // 拼接,两帧交错就都成了乱码。所以整帧的发送要串行化。
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // 一帧最长 27 字节,MTU 没协商上去时必须拆开发。
                for (int offset = 0; offset < frame.Length; offset += _writeChunk)
                {
                    int n = Math.Min(_writeChunk, frame.Length - offset);
                    var slice = new byte[n];
                    // 必须写全限定名:本文件 using 了 Windows.Storage.Streams,
                    // 那里也有一个 Buffer 类,裸写 Buffer 会报"不明确的引用"。
                    // 别"顺手简化"回 Buffer.BlockCopy。
                    System.Buffer.BlockCopy(frame, offset, slice, 0, n);

                    var writer = new DataWriter();
                    writer.WriteBytes(slice);

                    var status = await ch.WriteValueAsync(writer.DetachBuffer(), _writeOption)
                                         .AsTask(ct).ConfigureAwait(false);

                    if (status != GattCommunicationStatus.Success)
                    {
                        LastError = $"蓝牙写入失败({status})";
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                LastError = "蓝牙写入异常:" + Describe(ex);
                return false;
            }
            finally
            {
                _writeGate.Release();
            }
        }

        #endregion

        public void Dispose()
        {
            _closing = true;
            _open = false;

            try
            {
                if (_notifyChar is not null) _notifyChar.ValueChanged -= OnValueChanged;
            }
            catch { }

            try
            {
                if (_device is not null) _device.ConnectionStatusChanged -= OnConnectionStatusChanged;
            }
            catch { }

            // 注意顺序:先服务后设备。反过来会偶发 ObjectDisposedException。
            foreach (var svc in _openedServices)
            {
                try { svc.Dispose(); } catch { }
            }
            _openedServices.Clear();
            _candidates.Clear();

            try { _device?.Dispose(); } catch { }

            _notifyChar = null;
            _writeChar = null;
            _service = null;
            _session = null;
            _device = null;

            try { _writeGate.Dispose(); } catch { }
        }

        #region 异常描述

        /// <summary>
        /// 把异常翻成人看得懂的一句话。
        ///
        /// 为什么不能只用 ex.Message:WinRT 投影抛出来的异常**经常 Message 是空串**,
        /// 真正的信息全在 HRESULT 里。之前日志里出现过一条
        ///     蓝牙连接失败:
        /// 冒号后面什么都没有,完全没法排查 —— 就是这个原因。
        /// 所以这里一定带上类型名和 HRESULT,并把蓝牙相关的常见 HRESULT 翻成中文。
        /// </summary>
        private static string Describe(Exception ex)
        {
            string msg = ex.Message?.Trim() ?? "";
            string hint = HResultText(ex.HResult);

            var parts = new List<string>(3);
            if (msg.Length > 0) parts.Add(msg);
            if (hint.Length > 0) parts.Add(hint);
            parts.Add($"[{ex.GetType().Name} 0x{ex.HResult:X8}]");

            return string.Join(" ", parts);
        }

        /// <summary>
        /// 蓝牙/GATT 常见 HRESULT 的中文解释。查不到就返回空串。
        /// 前四个是 ATT 协议层的错误码,几乎都指向同一件事:没配对。
        /// </summary>
        private static string HResultText(int hr) => unchecked((uint)hr) switch
        {
            0x80650003 => "(ATT: 鉴权不足 —— 该特征要求已配对的加密链路)",
            0x80650005 => "(ATT: 授权不足)",
            0x80650008 => "(ATT: 加密不足 —— 该特征要求加密链路)",
            0x8065000F => "(ATT: 加密强度不足)",
            0x80650000 => "(ATT: 协议错误)",
            0x8007139F => "(设备当前状态不允许该操作,通常是链路已断)",
            0x800710DF => "(设备不可用 —— 检查本机蓝牙是否已打开)",
            0x80070490 => "(找不到该元素)",
            0x8000000E => "(方法调用时机不对)",
            0x80070005 => "(拒绝访问 —— 设备可能正被别的程序占用)",
            _ => ""
        };

        #endregion

        #region 地址与 UUID 的文本互转

        /// <summary>AA:BB:CC:DD:EE:FF</summary>
        public static string FormatAddress(ulong address)
        {
            var b = BitConverter.GetBytes(address);
            return $"{b[5]:X2}:{b[4]:X2}:{b[3]:X2}:{b[2]:X2}:{b[1]:X2}:{b[0]:X2}";
        }

        /// <summary>
        /// 认 AA:BB:CC:DD:EE:FF、AA-BB-...、AABBCCDDEEFF 三种写法。
        /// 解不出来就返回 0,由调用方退到按名字找。
        /// </summary>
        public static ulong ParseAddress(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            var hex = text.Replace(":", "").Replace("-", "").Replace(" ", "").Trim();
            if (hex.Length != 12) return 0;
            return ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        private static Guid ParseGuid(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Guid.Empty;
            var s = text.Trim();

            // 允许只写 16 位短 UUID(比如 FFE1),自动补成蓝牙基准 UUID
            if (s.Length == 4 && ushort.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var shortUuid))
                return Guid.Parse($"0000{shortUuid:X4}-0000-1000-8000-00805F9B34FB");

            return Guid.TryParse(s, out var g) ? g : Guid.Empty;
        }

        #endregion
    }
}
