// ============================================================================
//  BleProxyLink.cs
//  蓝牙链路的驱动侧:启动 MaxChemical.Ble.Proxy.exe,跟它用 stdio 说话。
//
//  ── 为什么蓝牙不在本进程里做 ──────────────────────────────────────
//  调 Windows 原生 BLE 必须把目标框架写成 net8.0-windows10.0.19041.0,
//  程序集就会依赖 WinRT.Runtime.dll 与 Microsoft.Windows.SDK.NET.dll。
//
//  驱动是被 MEF 从 <程序目录>\Plugins\ 扫出来加载的,那个目录只放驱动 dll 自己,
//  依赖到不了旁边 —— 程序集加载会**静默失败**,表现是设备库里根本没有这台设备,
//  而且不报任何错。这个坑实测踩过,所以本文件所在的工程坚持 net8.0-windows,
//  一个 WinRT 类型都不碰,蓝牙全部交给独立的 exe。
//
//  **不要**为了"少一个进程"把 WinRT 代码搬回来。搬回来的代价就是这台设备
//  在装机后凭空消失,而且没有任何报错线索。
//
//  ── 协议 ──────────────────────────────────────────────────────────
//  一行一条、制表符分隔、UTF-8,详见 MaxChemical.Ble.Proxy/Program.cs 顶部。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PenMeter_BLE
{
    /// <summary>扫描到的一台 BLE 设备。</summary>
    internal sealed class BleDeviceInfo
    {
        public string Name { get; init; } = "";
        public string Address { get; init; } = "";
        public int Rssi { get; init; }

        public override string ToString() => $"{Name}\t{Address}\t{Rssi} dBm";
    }

    internal sealed class BleProxyLink : IPenLink
    {
        /// <summary>代理进程的文件名。build.bat 会把它发布到 app\BleProxy\ 下。</summary>
        private const string ProxyExeName = "MaxChemical.Ble.Proxy.exe";

        private readonly string _name;
        private readonly string _address;
        private readonly string _serviceUuid;
        private readonly string _writeUuid;
        private readonly string _notifyUuid;
        private readonly int _scanTimeoutMs;
        private readonly Action<string> _log;

        private Process? _proc;
        private StreamWriter? _stdin;
        private readonly object _writeLock = new object();
        private volatile bool _open;
        private volatile bool _closing;

        // 一次只有一条命令在等应答(连接、发送各自串行),用一个待决槽就够了
        private readonly object _pendingLock = new object();
        private TaskCompletionSource<bool>? _pendingConnect;
        private TaskCompletionSource<bool>? _pendingTx;
        private TaskCompletionSource<List<BleDeviceInfo>>? _pendingScan;
        private List<BleDeviceInfo>? _scanSink;

        public BleProxyLink(string name, string address,
                            string serviceUuid, string writeUuid, string notifyUuid,
                            int scanTimeoutMs, Action<string> log)
        {
            _name = (name ?? "").Trim();
            _address = (address ?? "").Trim();
            _serviceUuid = (serviceUuid ?? "").Trim();
            _writeUuid = (writeUuid ?? "").Trim();
            _notifyUuid = (notifyUuid ?? "").Trim();
            _scanTimeoutMs = scanTimeoutMs;
            _log = log ?? (_ => { });
        }

        public bool IsOpen => _open;
        public string? LastError { get; private set; }

        public string Description =>
            !string.IsNullOrEmpty(_address) ? $"蓝牙 {_address}"
            : !string.IsNullOrEmpty(_name) ? $"蓝牙 “{_name}”"
            : "蓝牙(未指定设备)";

        public event Action<byte[]>? BytesReceived;
        public event Action? Closed;

        #region 启动代理进程

        /// <summary>
        /// 找代理 exe。按"安装后的样子"优先,再退到开发时可能的几个位置。
        /// 找不到时把找过的路径全列出来 —— 这种错最怕只说一句"找不到"。
        /// </summary>
        internal static string? LocateProxy(out string searched)
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new List<string>
            {
                // build.bat 发布的位置(与网关代理同样的组织方式)
                Path.Combine(baseDir, "BleProxy", ProxyExeName),
                // 万一有人直接把 exe 摊在主目录
                Path.Combine(baseDir, ProxyExeName),
                // 开发时:驱动 dll 通常被拷进 <输出>\Plugins\,代理在上一层
                Path.Combine(baseDir, "..", "BleProxy", ProxyExeName),
            };

            searched = string.Join("\r\n  ", candidates);
            foreach (var c in candidates)
            {
                try { if (File.Exists(c)) return Path.GetFullPath(c); } catch { }
            }
            return null;
        }

        private bool StartProxy()
        {
            var exe = LocateProxy(out var searched);
            if (exe is null)
            {
                LastError = "找不到蓝牙代理进程 " + ProxyExeName + "。找过这些位置:\r\n  " + searched +
                            "\r\n蓝牙功能由这个独立进程提供(驱动本身不能带 WinRT 依赖)," +
                            "请确认安装包里有 BleProxy 目录;开发调试时可手工把它发布过去:" +
                            "dotnet publish MaxChemical.Ble.Proxy -c Release -o <主程序目录>\\BleProxy";
                return false;
            }

            try
            {
                _proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = exe,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = new UTF8Encoding(false),
                        StandardErrorEncoding = new UTF8Encoding(false),
                        // 工作目录设成 exe 所在目录:它的依赖(WinRT 那两个)就在旁边
                        WorkingDirectory = Path.GetDirectoryName(exe) ?? AppDomain.CurrentDomain.BaseDirectory,
                    },
                    EnableRaisingEvents = true,
                };

                _proc.OutputDataReceived += (_, e) => { if (e.Data is not null) OnLine(e.Data); };
                _proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) _log($"代理 stderr: {e.Data}"); };
                _proc.Exited += (_, __) =>
                {
                    if (_closing) return;
                    _open = false;
                    _log("代理进程已退出");
                    FailAllPending("代理进程已退出");
                    Closed?.Invoke();
                };

                _proc.Start();
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();

                _stdin = new StreamWriter(_proc.StandardInput.BaseStream, new UTF8Encoding(false))
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"启动蓝牙代理进程失败:{ex.Message}";
                return false;
            }
        }

        #endregion

        #region 连接

        public async Task<bool> OpenAsync(CancellationToken ct)
        {
            if (!StartProxy()) return false;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingLock) _pendingConnect = tcs;

            Send("CONNECT", _address, _name, _serviceUuid, _writeUuid, _notifyUuid,
                 _scanTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // 代理内部最长也就 扫描超时 + 30s,这里再宽 10s 兜底
            using var timeout = new CancellationTokenSource(_scanTimeoutMs + 40000);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

            using (linked.Token.Register(() => tcs.TrySetResult(false)))
            {
                bool ok = await tcs.Task.ConfigureAwait(false);
                lock (_pendingLock) _pendingConnect = null;

                if (!ok && LastError is null)
                    LastError = "等待蓝牙代理应答超时";

                _open = ok;
                return ok;
            }
        }

        /// <summary>扫描附近的 BLE 设备。**不需要先连接** —— 就是用来确定该连谁的。</summary>
        public static async Task<List<BleDeviceInfo>> ScanAsync(int timeoutMs, Action<string> log,
                                                               CancellationToken ct)
        {
            // 扫描是一次性动作,用一个临时实例跑完就拆掉,不去动正在用的连接
            var link = new BleProxyLink("", "", "", "", "", timeoutMs, log);
            try
            {
                if (!link.StartProxy())
                    throw new InvalidOperationException(link.LastError ?? "无法启动蓝牙代理进程");

                var tcs = new TaskCompletionSource<List<BleDeviceInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (link._pendingLock)
                {
                    link._scanSink = new List<BleDeviceInfo>();
                    link._pendingScan = tcs;
                }

                link.Send("SCAN", timeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture));

                using var timeout = new CancellationTokenSource(timeoutMs + 20000);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
                using (linked.Token.Register(() => tcs.TrySetResult(new List<BleDeviceInfo>())))
                {
                    return await tcs.Task.ConfigureAwait(false);
                }
            }
            finally
            {
                link.Dispose();
            }
        }

        #endregion

        #region 收发

        public async Task<bool> WriteAsync(byte[] frame, CancellationToken ct = default)
        {
            if (!_open) return false;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingLock)
            {
                // 上一条还没回就又发 —— 把旧的判失败,不让它一直挂着
                _pendingTx?.TrySetResult(false);
                _pendingTx = tcs;
            }

            Send("TX", Convert.ToHexString(frame));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            using (linked.Token.Register(() => tcs.TrySetResult(false)))
            {
                bool ok = await tcs.Task.ConfigureAwait(false);
                lock (_pendingLock) { if (ReferenceEquals(_pendingTx, tcs)) _pendingTx = null; }
                return ok;
            }
        }

        /// <summary>处理代理发来的一行。</summary>
        private void OnLine(string line)
        {
            if (line.Length == 0) return;
            var p = line.Split('\t');

            switch (p[0])
            {
                case "HELLO":
                    _log($"蓝牙代理已启动(协议 v{Field(p, 1)})");
                    break;

                case "READY":
                    lock (_pendingLock) _pendingConnect?.TrySetResult(true);
                    break;

                case "ERR":
                    LastError = Field(p, 1);
                    // 连接阶段的 ERR 要唤醒等待方,否则会一直等到超时才报错
                    lock (_pendingLock)
                    {
                        _pendingConnect?.TrySetResult(false);
                        _pendingScan?.TrySetResult(_scanSink ?? new List<BleDeviceInfo>());
                    }
                    _log($"代理报错:{LastError}");
                    break;

                case "RX":
                    try
                    {
                        var bytes = Convert.FromHexString(Field(p, 1));
                        if (bytes.Length > 0) BytesReceived?.Invoke(bytes);
                    }
                    catch { /* 单片解析失败不该拖垮链路,拼帧那边会重同步 */ }
                    break;

                case "TXOK":
                    lock (_pendingLock) _pendingTx?.TrySetResult(true);
                    break;

                case "TXERR":
                    LastError = Field(p, 1);
                    lock (_pendingLock) _pendingTx?.TrySetResult(false);
                    break;

                case "DEV":
                    lock (_pendingLock)
                    {
                        _scanSink?.Add(new BleDeviceInfo
                        {
                            Name = Field(p, 1),
                            Address = Field(p, 2),
                            Rssi = int.TryParse(Field(p, 3), out var r) ? r : 0
                        });
                    }
                    break;

                case "SCANEND":
                    lock (_pendingLock)
                        _pendingScan?.TrySetResult(_scanSink ?? new List<BleDeviceInfo>());
                    break;

                case "CLOSED":
                    _open = false;
                    if (!_closing) Closed?.Invoke();
                    break;

                case "LOG":
                    _log(Field(p, 1));
                    break;
            }
        }

        private void Send(string verb, params string[] fields)
        {
            var sb = new StringBuilder(verb);
            foreach (var f in fields)
            {
                sb.Append('\t');
                sb.Append(Esc(f));
            }

            lock (_writeLock)
            {
                try { _stdin?.WriteLine(sb.ToString()); }
                catch (Exception ex) { LastError = $"写入代理进程失败:{ex.Message}"; }
            }
        }

        private void FailAllPending(string reason)
        {
            LastError = reason;
            lock (_pendingLock)
            {
                _pendingConnect?.TrySetResult(false);
                _pendingTx?.TrySetResult(false);
                _pendingScan?.TrySetResult(_scanSink ?? new List<BleDeviceInfo>());
            }
        }

        #endregion

        public void Dispose()
        {
            _closing = true;
            _open = false;

            try { Send("QUIT"); } catch { }

            try
            {
                // 给它一点时间自己收尾:正常退出会断开 GATT 连接,
                // 直接 Kill 的话适配器上会残留一个连接,下次连同一支笔可能失败。
                if (_proc is not null && !_proc.WaitForExit(2000))
                    _proc.Kill(entireProcessTree: true);
            }
            catch { }

            try { _stdin?.Dispose(); } catch { }
            try { _proc?.Dispose(); } catch { }
            _stdin = null;
            _proc = null;
        }

        #region 转义(与代理侧一一对应)

        private static string Esc(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s!.Replace("\\", "\\\\")
                     .Replace("\r", "\\r")
                     .Replace("\n", "\\n")
                     .Replace("\t", "\\t");
        }

        private static string Field(string[] parts, int index) =>
            index < parts.Length ? Unesc(parts[index]) : "";

        private static string Unesc(string s)
        {
            if (s.IndexOf('\\') < 0) return s;

            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                i++;
                sb.Append(s[i] switch
                {
                    'r' => '\r',
                    'n' => '\n',
                    't' => '\t',
                    '\\' => '\\',
                    _ => s[i]
                });
            }
            return sb.ToString();
        }

        #endregion
    }
}
