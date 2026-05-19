using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MaxChemical.Core;
using MaxChemical.Logging;

namespace MaxChemical.Infrastructure.Services
{
    /// <summary>
    /// TCP 客户端实现,透过 ZLAN 网关的某个通道做透传。
    /// 协议字节流和串口完全一致 (Modbus RTU 等)。
    /// 每次 SendAndReceive 都建/关 TCP 连接,简单可靠。
    ///
    /// 关键设计:
    /// 1. 读取时循环 Read 直到收到完整 Modbus 帧 (避免 TCP 分包导致只读到一半)
    /// 2. 根据 Modbus 功能码动态计算期望长度
    /// 3. CancelAfter 控制超时,真正取消而不是悬挂任务
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

                // 先写命令
                await stream.WriteAsync(sendBuffer, 0, sendBuffer.Length, ct).ConfigureAwait(false);

                // 读取应答 - 循环读直到收到完整 Modbus 帧
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(recvTimeoutMs);

                try
                {
                    return await ReadCompleteModbusFrameAsync(stream, recvBuffer, readCts.Token)
                                .ConfigureAwait(false);
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
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"网关 TCP 写入超时 {_ip}:{_port}");
                }

                // 对于写命令(功能码 0x06),设备会返回 8 字节回显应答
                // 即使调用方不解析,也要把它读掉,避免下次连接拿到旧数据
                try
                {
                    byte[] echo = new byte[16];
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(1000);

                    int n = await stream.ReadAsync(echo, 0, echo.Length, readCts.Token)
                                        .ConfigureAwait(false);
                    if (n > 0)
                    {
                        _logger?.LogDebug("写命令应答已读取 {Len} 字节", n);
                    }
                }
                catch
                {
                    // 读应答失败不影响写成功
                }

                return true;
            }
            finally
            {
                Close(client);
                _ioLock.Release();
            }
        }

        /// <summary>
        /// 循环 Read 直到收到完整 Modbus 帧 (避免 TCP 分包问题)。
        /// 根据功能码动态判断期望长度。
        /// </summary>
        private async Task<int> ReadCompleteModbusFrameAsync(
            NetworkStream stream, byte[] recvBuffer, CancellationToken token)
        {
            int totalRead = 0;
            int expectedLength = 5;  // 至少要读够 3 字节才能算出最终长度

            while (totalRead < expectedLength)
            {
                int n = await stream.ReadAsync(
                    recvBuffer, totalRead, recvBuffer.Length - totalRead, token)
                    .ConfigureAwait(false);

                if (n == 0)
                {
                    // 对端关闭连接
                    _logger?.LogWarning("对端关闭连接,已读 {Total} 字节,期望 {Expected}",
                        totalRead, expectedLength);
                    break;
                }

                totalRead += n;

                // 读到第 3 字节后,根据功能码精确计算应答总长
                if (totalRead >= 3 && expectedLength == 5)
                {
                    byte funcCode = recvBuffer[1];

                    if ((funcCode & 0x80) != 0)
                    {
                        // 异常应答: [addr][func|0x80][exception_code][crc_lo][crc_hi] = 5 字节
                        expectedLength = 5;
                    }
                    else if (funcCode == 0x03 || funcCode == 0x04)
                    {
                        // 读保持/输入寄存器: [addr][func][byte_count][data...][crc_lo][crc_hi]
                        byte byteCount = recvBuffer[2];
                        expectedLength = 3 + byteCount + 2;
                    }
                    else if (funcCode == 0x05 || funcCode == 0x06)
                    {
                        // 写单线圈/单寄存器: [addr][func][reg_hi][reg_lo][val_hi][val_lo][crc_lo][crc_hi] = 8 字节
                        expectedLength = 8;
                    }
                    else if (funcCode == 0x0F || funcCode == 0x10)
                    {
                        // 写多线圈/多寄存器: [addr][func][reg_hi][reg_lo][cnt_hi][cnt_lo][crc_lo][crc_hi] = 8 字节
                        expectedLength = 8;
                    }
                    else if (funcCode == 0x01 || funcCode == 0x02)
                    {
                        // 读线圈/离散输入: [addr][func][byte_count][data...][crc_lo][crc_hi]
                        byte byteCount = recvBuffer[2];
                        expectedLength = 3 + byteCount + 2;
                    }
                    else
                    {
                        // 未知功能码,按已读字节返回
                        _logger?.LogWarning("未知 Modbus 功能码 0x{Func:X2}", funcCode);
                        break;
                    }
                }
            }

            return totalRead;
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