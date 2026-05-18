using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MaxChemical.Core;
using MaxChemical.Logging;

namespace MaxChemical.Infrastructure.Services
{
    /// <summary>
    /// TCP 客户端实现 — 透过 ZLAN 网关的某个通道做透传。
    /// 协议字节流和串口完全一致 (Modbus RTU 等)。
    /// 每次 SendAndReceive 都建/关 TCP 连接,简单可靠。
    /// </summary>
    internal class TcpClientTransport : IDeviceTransport
    {
        private readonly string _ip;
        private readonly int _port;
        private readonly int _connectTimeoutMs;
        private readonly ILogService _logger;
        private readonly SemaphoreSlim _ioLock = new SemaphoreSlim(1, 1);

        public TcpClientTransport(string gatewayIp, int channelPort, ILogService logger,
            int connectTimeoutMs = 3000)
        {
            _ip = gatewayIp ?? throw new ArgumentNullException(nameof(gatewayIp));
            _port = channelPort;
            _connectTimeoutMs = connectTimeoutMs;
            _logger = logger?.ForContext<TcpClientTransport>();
        }

        public async Task<int> SendAndReceiveAsync(
            byte[] sendBuffer, byte[] recvBuffer, int recvTimeoutMs, CancellationToken ct)
        {
            await _ioLock.WaitAsync(ct).ConfigureAwait(false);
            TcpClient client = null;
            try
            {
                client = await OpenAsync(ct).ConfigureAwait(false);
                var stream = client.GetStream();

                // 写 (用 connectTimeout 的一半给写,3000 已经够长)
                await stream.WriteAsync(sendBuffer, 0, sendBuffer.Length, ct).ConfigureAwait(false);

                // 读 (用 CancelAfter 控制超时,真正取消而不是悬挂)
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(recvTimeoutMs);

                try
                {
                    int n = await stream.ReadAsync(recvBuffer, 0, recvBuffer.Length, readCts.Token)
                                        .ConfigureAwait(false);
                    return n;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"网关 TCP 读取超时 ({recvTimeoutMs}ms),设备无响应 {_ip}:{_port}");
                }
            }
            finally
            {
                Close(client);
                _ioLock.Release();
            }
        }

        public async Task<bool> SendAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await _ioLock.WaitAsync(ct).ConfigureAwait(false);
            TcpClient client = null;
            try
            {
                client = await OpenAsync(ct).ConfigureAwait(false);
                var stream = client.GetStream();

                using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                writeCts.CancelAfter(3000);

                try
                {
                    await stream.WriteAsync(buffer, offset, count, writeCts.Token).ConfigureAwait(false);
                    return true;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"网关 TCP 写入超时 {_ip}:{_port}");
                }
            }
            finally
            {
                Close(client);
                _ioLock.Release();
            }
        }

        private async Task<TcpClient> OpenAsync(CancellationToken ct)
        {
            var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_connectTimeoutMs);

            try
            {
                await client.ConnectAsync(_ip, _port, timeoutCts.Token).ConfigureAwait(false);
                client.NoDelay = true;
                return client;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                client.Dispose();
                throw new TimeoutException($"连接网关超时 {_ip}:{_port}");
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        private static void Close(TcpClient client)
        {
            if (client == null) return;
            try { client.Close(); } catch { }
            try { client.Dispose(); } catch { }
        }

        public void Dispose()
        {
            _ioLock.Dispose();
        }
    }
}