using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MaxChemical.Core;
using MaxChemical.Logging;
using Microsoft.AspNetCore.SignalR.Client;

namespace DevicePlugins.Devices.Remote
{
    /// <summary>
    /// 与自建云服务器(DTU 网关)的 SignalR 隧道客户端（进程级单例）。
    /// 把设备的 Modbus 字节经云中转到 DTU，按设备序列号路由。
    /// 自动重连；连接状态可供上层做断线告警/安全态判断。
    /// </summary>
    public class RemoteGatewayClient
    {
        private static RemoteGatewayClient? _instance;
        private static readonly object _lock = new object();

        public static RemoteGatewayClient Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock) { _instance ??= new RemoteGatewayClient(); }
                }
                return _instance;
            }
        }

        private readonly ILogService _logger;
        private readonly SemaphoreSlim _connLock = new SemaphoreSlim(1, 1);
        // 按 DTU 序列号串行化每台设备的 IO（保证写+回读原子，不同设备并发互不影响）
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _serialLocks =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private HubConnection? _conn;
        private string? _builtForUrl;

        private SemaphoreSlim GetSerialLock(string serial) =>
            _serialLocks.GetOrAdd(serial ?? "", _ => new SemaphoreSlim(1, 1));

        public RemoteGatewayClient()
        {
            _logger = LogManager.GetLogger<RemoteGatewayClient>();
        }

        /// <summary>
        /// 隧道是否已连通（上层可据此做断线告警/暂停流程）。
        /// </summary>
        public bool IsConnected => _conn?.State == HubConnectionState.Connected;

        public HubConnectionState State => _conn?.State ?? HubConnectionState.Disconnected;

        /// <summary>
        /// 隧道连接状态变化通知（重连成功/断开等），上层可订阅做告警。
        /// </summary>
        public event Action<HubConnectionState>? StateChanged;

        /// <summary>
        /// 隧道健康状态变化（true=可用, false=断开/重连中）。
        /// 供流程引擎/界面订阅：断线即可 HOLD 流程并告警，恢复后由人工确认续跑。
        /// </summary>
        public event Action<bool>? LinkHealthyChanged;

        /// <summary>隧道当前是否健康（已连接）。</summary>
        public bool IsLinkHealthy => _conn?.State == HubConnectionState.Connected;

        /// <summary>
        /// 预热/建立隧道连接（设备连接时调用，把首次建连开销挪到"连接"动作里，
        /// 避免首条数据命令很慢）。返回是否已连通；失败不抛异常，返回 false。
        /// </summary>
        public async Task<bool> ConnectAsync(CancellationToken ct = default)
        {
            try
            {
                await EnsureConnectedAsync(ct).ConfigureAwait(false);
                return IsConnected;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("云隧道预热连接失败: {Error}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 查询指定序列号的 DTU 当前是否在线(连接时校验用)。
        /// 服务器端用与转发命令相同的键查找,因此"在线==发得出命令"。
        /// 隧道不通或查询出错时返回 false(失败不抛)。
        /// </summary>
        public async Task<bool> IsSerialOnlineAsync(string serialNo, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(serialNo)) return false;
            try
            {
                await EnsureConnectedAsync(ct).ConfigureAwait(false);
                return await _conn!.InvokeAsync<bool>("IsOnline", serialNo, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("查询 DTU[{Serial}] 在线状态失败: {Error}", serialNo, ex.Message);
                return false;
            }
        }

        private async Task EnsureConnectedAsync(CancellationToken ct)
        {
            var s = DeviceConnectionConfig.RemoteServerSettings;

            if (_conn != null && _conn.State == HubConnectionState.Connected && _builtForUrl == s.ServerUrl)
                return;

            await _connLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_conn != null && _conn.State == HubConnectionState.Connected && _builtForUrl == s.ServerUrl)
                    return;

                // 服务器地址变了，重建连接
                if (_conn != null && _builtForUrl != s.ServerUrl)
                {
                    try { await _conn.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
                    _conn = null;
                }

                if (_conn == null)
                {
                    _conn = new HubConnectionBuilder()
                        .WithUrl(s.ServerUrl, opts =>
                        {
                            // 用自定义头传令牌（.NET 客户端在 negotiate 和 WebSocket 握手都会带上），
                            // 与服务器 DtuHub 读取的 X-Access-Token 一致。
                            if (!string.IsNullOrEmpty(s.AccessToken))
                                opts.Headers["X-Access-Token"] = s.AccessToken;
                        })
                        .WithAutomaticReconnect()
                        .Build();

                    _conn.ServerTimeout = TimeSpan.FromMilliseconds(Math.Max(15000, s.RequestTimeoutMs * 2));
                    _conn.KeepAliveInterval = TimeSpan.FromSeconds(5); // 心跳调快，更快发现断线
                    _builtForUrl = s.ServerUrl;

                    _conn.Reconnecting += err =>
                    {
                        _logger.LogWarning("云服务器隧道断开，正在重连: {Error}", err?.Message);
                        StateChanged?.Invoke(HubConnectionState.Reconnecting);
                        LinkHealthyChanged?.Invoke(false);
                        return Task.CompletedTask;
                    };
                    _conn.Reconnected += id =>
                    {
                        _logger.LogInformation("云服务器隧道已重连");
                        StateChanged?.Invoke(HubConnectionState.Connected);
                        LinkHealthyChanged?.Invoke(true);
                        return Task.CompletedTask;
                    };
                    _conn.Closed += err =>
                    {
                        _logger.LogWarning("云服务器隧道已关闭: {Error}", err?.Message);
                        StateChanged?.Invoke(HubConnectionState.Disconnected);
                        LinkHealthyChanged?.Invoke(false);
                        return Task.CompletedTask;
                    };
                }

                if (_conn.State == HubConnectionState.Disconnected)
                {
                    await _conn.StartAsync(ct).ConfigureAwait(false);
                    _logger.LogInformation("已连接云服务器 {Url}", s.ServerUrl);
                    StateChanged?.Invoke(HubConnectionState.Connected);
                    LinkHealthyChanged?.Invoke(true);
                }
            }
            finally
            {
                _connLock.Release();
            }
        }

        /// <summary>
        /// 一问一答：把 Modbus 请求帧经云发到指定序列号的 DTU，取回应答帧。
        /// 按序列号串行化(同一设备不会并发交错；不同设备并发)。
        /// 服务器在 DTU 超时/离线时返回空数组，调用方据此抛超时让上层重试。
        /// </summary>
        public async Task<byte[]> SendAndReceiveAsync(string serialNo, byte[] send, int timeoutMs, CancellationToken ct)
        {
            var sem = GetSerialLock(serialNo);
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try { return await SendAndReceiveCoreAsync(serialNo, send, timeoutMs, ct).ConfigureAwait(false); }
            finally { sem.Release(); }
        }

        /// <summary>
        /// 只发不收（写命令场景，无回读校验时使用）。按序列号串行化。
        /// </summary>
        public async Task<bool> SendAsync(string serialNo, byte[] data, CancellationToken ct)
        {
            var sem = GetSerialLock(serialNo);
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await EnsureConnectedAsync(ct).ConfigureAwait(false);
                return await _conn!.InvokeAsync<bool>("Send", serialNo, data, ct).ConfigureAwait(false);
            }
            finally { sem.Release(); }
        }

        /// <summary>
        /// 写+回读的原子序列：在同一把序列号锁内，先下发写帧(消费回显)，再读回，返回读应答字节。
        /// 供写后回读校验使用，确保两步之间不被其它命令插入。
        /// </summary>
        public async Task<byte[]> WriteThenReadAsync(string serialNo, byte[] writeFrame, byte[] readFrame, int timeoutMs, CancellationToken ct)
        {
            var sem = GetSerialLock(serialNo);
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // 1) 下发写，消费设备写回显
                await SendAndReceiveCoreAsync(serialNo, writeFrame, timeoutMs, ct).ConfigureAwait(false);
                // 2) 回读
                return await SendAndReceiveCoreAsync(serialNo, readFrame, timeoutMs, ct).ConfigureAwait(false);
            }
            finally { sem.Release(); }
        }

        // 不加序列号锁的底层一问一答(供已持锁的调用方复用，避免重入死锁)
        private async Task<byte[]> SendAndReceiveCoreAsync(string serialNo, byte[] send, int timeoutMs, CancellationToken ct)
        {
            await EnsureConnectedAsync(ct).ConfigureAwait(false);
            return await _conn!.InvokeAsync<byte[]>("SendAndReceive", serialNo, send, timeoutMs, ct).ConfigureAwait(false);
        }
    }
}
