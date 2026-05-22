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
    /// 4. OpenAsync 内置连接重试 + 退避:卓岚网关 TCP Server 一个通道只允许一个连接,
    ///    即建即关高频/多路场景下,新连接可能撞上"上一条尚未在网关侧释放" -> 被拒,
    ///    用重试吸收这种瞬时失败(典型现象:刚连失败,重连就好)。
    /// </summary>
    internal class TcpClientTransport : IDeviceTransport
    {
        private readonly string _ip;
        private readonly int _port;
        private readonly int _connectTimeoutMs;
        private readonly ILogService _logger;
        private readonly SemaphoreSlim _ioLock = new SemaphoreSlim(1, 1);

        // 连接重试参数
        private const int ConnectMaxAttempts = 3;
        private const int ConnectRetryBaseDelayMs = 150;  // 退避基数:150ms / 300ms

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

                // 写命令前清空残留:网关可能跨连接缓存了上一帧的旧数据,
                // 不清掉会导致本次读到错位字节 -> 功能码非法 -> 短帧/校验失败
                DrainBuffer(stream, ct);

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

                // 写前清残留,避免把旧应答当成本次回显
                DrainBuffer(stream, ct);

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

                // 写应答(如 0x06 回显 8 字节)即使不解析也要完整读掉,避免污染下次连接。
                // 改用完整帧读取,而不是一次性 ReadAsync,确保不留半截尾巴。
                try
                {
                    byte[] echo = new byte[16];
                    using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readCts.CancelAfter(1000);

                    int n = await ReadCompleteModbusFrameAsync(stream, echo, readCts.Token)
                                    .ConfigureAwait(false);
                    if (n > 0)
                        _logger?.LogDebug("写命令应答已读取 {Len} 字节", n);
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
        /// 清空接收缓冲里的残留字节(对应串口的 DiscardInBuffer)。
        /// 只读取当前已到达的数据,不阻塞等待。
        /// </summary>
        private static void DrainBuffer(NetworkStream stream, CancellationToken ct)
        {
            try
            {
                if (!stream.DataAvailable) return;
                byte[] junk = new byte[256];
                while (stream.DataAvailable && !ct.IsCancellationRequested)
                {
                    // DataAvailable 为 true 时 Read 不会阻塞
                    int n = stream.Read(junk, 0, junk.Length);
                    if (n <= 0) break;
                }
            }
            catch
            {
                // 清缓冲失败不影响后续逻辑
            }
        }

        /// <summary>
        /// 根据已收到的字节推算 Modbus RTU 应答完整帧长。返回 &lt;= 0 表示无法推断。
        /// 与 SerialPortTransport 保持完全一致的判断逻辑。
        /// </summary>
        private static int InferModbusFrameLength(byte[] buffer, int received)
        {
            if (received < 2) return -1;

            byte funcCode = buffer[1];

            // 异常响应:功能码最高位置 1,固定 5 字节
            if ((funcCode & 0x80) != 0)
                return 5;   // slave(1) + func|0x80(1) + 错误码(1) + CRC(2)

            switch (funcCode)
            {
                // 读类响应:第 3 字节为数据长度 N,总长 = 3 + N + 2
                case 0x01:   // Read Coils
                case 0x02:   // Read Discrete Inputs
                case 0x03:   // Read Holding Registers
                case 0x04:   // Read Input Registers
                    if (received < 3) return -1;
                    return 3 + buffer[2] + 2;

                // 写类响应:固定 8 字节
                case 0x05:   // Write Single Coil
                case 0x06:   // Write Single Register
                case 0x0F:   // Write Multiple Coils
                case 0x10:   // Write Multiple Registers
                    return 8;

                default:
                    return -1;   // 未知功能码,交上层处理
            }
        }

        /// <summary>
        /// 循环 Read 直到收到完整 Modbus 帧 (避免 TCP 分包问题)。
        /// 语义对齐 SerialPortTransport.ReadModbusFrameAsync:
        ///   - expectedLength 初始 -1,攒够 3 字节后才推算
        ///   - 未知功能码 / 超长:返回已读字节交上层 CRC 校验
        ///   - 一个字节都没读到就被对端关闭(n==0)或取消:抛异常,绝不返回 0
        /// </summary>
        private async Task<int> ReadCompleteModbusFrameAsync(
            NetworkStream stream, byte[] recvBuffer, CancellationToken token)
        {
            int totalRead = 0;
            int expectedLength = -1;   // -1 表示尚未确定目标长度

            while (true)
            {
                // 已知目标长度且已攒够 -> 完成
                if (expectedLength > 0 && totalRead >= expectedLength)
                    return totalRead;

                // 单次读取的目标:已知长度就读到该长度,否则先读到能确定帧长(3字节)
                int wantTotal = expectedLength > 0
                    ? expectedLength
                    : Math.Min(3, recvBuffer.Length);
                int toRead = wantTotal - totalRead;
                if (toRead <= 0)
                    toRead = recvBuffer.Length - totalRead;   // 容错:继续往缓冲填
                if (toRead <= 0)
                    return totalRead;                          // 缓冲满了

                int n = await stream.ReadAsync(recvBuffer, totalRead, toRead, token)
                                    .ConfigureAwait(false);

                if (n == 0)
                {
                    // 对端关闭连接。一个字节都没读到 -> 视为无响应,抛异常(对齐串口语义,不返回 0)
                    if (totalRead == 0)
                        throw new TimeoutException(
                            $"网关 TCP 读取返回 0,设备无响应或网关已断开 {_ip}:{_port}");

                    // 已读到部分字节但被关闭 -> 返回已读,交上层 CRC 校验按短帧处理
                    _logger?.LogWarning("对端关闭连接,已读 {Total} 字节,期望 {Expected}",
                        totalRead, expectedLength);
                    return totalRead;
                }

                totalRead += n;

                // 攒够 3 字节后推算完整帧长(和串口完全一致)
                if (expectedLength < 0 && totalRead >= 3)
                {
                    expectedLength = InferModbusFrameLength(recvBuffer, totalRead);

                    if (expectedLength <= 0)
                    {
                        // 未知功能码,无法推断 -> 返回当前数据交上层处理
                        _logger?.LogWarning("未知 Modbus 功能码 0x{Func:X2},已读 {Total} 字节",
                            recvBuffer[1], totalRead);
                        return totalRead;
                    }
                    if (expectedLength > recvBuffer.Length)
                    {
                        // 推算长度超过缓冲区,异常情况,截断返回
                        _logger?.LogWarning("推算帧长 {Expected} 超过缓冲区 {Buf},截断返回",
                            expectedLength, recvBuffer.Length);
                        return totalRead;
                    }
                }
            }
        }

        /// <summary>
        /// 建立 TCP 连接,内置重试 + 退避。
        /// 网关 TCP Server 一个通道同一时刻只允许一个连接,即建即关高频场景下
        /// 新连接可能撞上上一条尚未释放 -> 被拒(SocketException),这里用重试吸收。
        /// </summary>
        private async Task<TcpClient> OpenAsync(CancellationToken ct)
        {
            Exception last = null;

            for (int attempt = 1; attempt <= ConnectMaxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                var client = new TcpClient();
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_connectTimeoutMs);

                try
                {
                    await client.ConnectAsync(_ip, _port, timeoutCts.Token).ConfigureAwait(false);
                    client.NoDelay = true;
                    if (attempt > 1)
                        _logger?.LogDebug("连接网关成功 {Ip}:{Port} (第 {N} 次尝试)", _ip, _port, attempt);
                    return client;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // 外部主动取消,直接抛,不重试
                    client.Dispose();
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // 连接超时(timeoutCts 触发)
                    client.Dispose();
                    last = new TimeoutException($"连接网关超时 {_ip}:{_port}");
                    _logger?.LogWarning("连接网关超时 {Ip}:{Port} (第 {N}/{Max} 次)",
                        _ip, _port, attempt, ConnectMaxAttempts);
                }
                catch (SocketException ex)
                {
                    // 网关拒绝 / 上一条连接尚未释放 / 网络瞬断
                    client.Dispose();
                    last = ex;
                    _logger?.LogWarning("连接网关被拒 {Ip}:{Port} (第 {N}/{Max} 次): {Code} {Msg}",
                        _ip, _port, attempt, ConnectMaxAttempts, ex.SocketErrorCode, ex.Message);
                }
                catch (Exception ex)
                {
                    client.Dispose();
                    last = ex;
                    _logger?.LogWarning("连接网关异常 {Ip}:{Port} (第 {N}/{Max} 次): {Msg}",
                        _ip, _port, attempt, ConnectMaxAttempts, ex.Message);
                }

                if (attempt < ConnectMaxAttempts)
                    await Task.Delay(ConnectRetryBaseDelayMs * attempt, ct).ConfigureAwait(false);
            }

            // 重试用尽,抛最后一次的异常(超时类抛 TimeoutException,便于上层驱动的重试逻辑识别)
            throw last ?? new TimeoutException($"连接网关失败 {_ip}:{_port}");
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