using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using MaxChemical.Core;

namespace MaxChemical.Infrastructure.Services
{
    /// <summary>
    /// 物理串口实现。
    /// 每次 SendAndReceive 都打开/关闭串口,简单可靠 (代价是有 ~10ms 开销)。
    /// 
    /// 读取策略 (Modbus RTU 兼容):
    /// 因为 Stream.ReadAsync 的语义是 "读到 ≥1 字节就返回",直接调用会导致只收到第一个字节。
    /// 这里改用循环读取 + 帧长度自适应:
    ///   1. 先攒满 3 字节 (站号+功能码+长度字节)
    ///   2. 根据功能码推算完整帧长:
    ///      - 异常响应 (功能码 & 0x80): 站号+异常功能码+异常码+CRC = 5 字节
    ///      - 0x03/0x04 读响应:         站号+功能码+数据长度N+数据(N)+CRC = 3+N+2 字节
    ///      - 0x05/0x06/0x0F/0x10 写响应: 固定 8 字节 (站号+功能码+地址2+值或数量2+CRC)
    ///      - 其它/未知:                 退回到等 timeout 把缓冲读空
    ///   3. 循环 ReadAsync 直到攒够目标长度或总超时
    /// </summary>
    internal class SerialPortTransport : IDeviceTransport
    {
        private readonly SerialPortConfig _config;
        private readonly SemaphoreSlim _ioLock = new SemaphoreSlim(1, 1);

        public SerialPortTransport(SerialPortConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public async Task<int> SendAndReceiveAsync(
            byte[] sendBuffer, byte[] recvBuffer, int recvTimeoutMs, CancellationToken ct)
        {
            await _ioLock.WaitAsync(ct).ConfigureAwait(false);
            SerialPort port = null;
            try
            {
                port = OpenPort();

                // 发送前清空可能的残留字节,避免脏数据干扰本次读取
                try { port.DiscardInBuffer(); } catch { }

                await port.BaseStream.WriteAsync(sendBuffer, 0, sendBuffer.Length, ct).ConfigureAwait(false);

                return _config.Framing == SerialFraming.AsciiLine
                    ? await ReadUntilTerminatorAsync(port, recvBuffer, _config.ReplyTerminator, recvTimeoutMs, ct).ConfigureAwait(false)
                    : await ReadModbusFrameAsync(port, recvBuffer, recvTimeoutMs, ct).ConfigureAwait(false);
            }
            finally
            {
                ClosePort(port);
                _ioLock.Release();
            }
        }

        /// <summary>
        /// 循环读取直到攒够一个完整的 Modbus RTU 应答帧,或超时。
        /// </summary>
        private static async Task<int> ReadModbusFrameAsync(
            SerialPort port, byte[] recvBuffer, int timeoutMs, CancellationToken ct)
        {
            int offset = 0;
            int targetLen = -1;   // -1 表示还未确定目标长度
            var sw = Stopwatch.StartNew();

            while (true)
            {
                int remainingTime = timeoutMs - (int)sw.ElapsedMilliseconds;
                if (remainingTime <= 0)
                {
                    if (offset == 0)
                        throw new TimeoutException($"串口读取超时 ({timeoutMs}ms),设备无响应");
                    // 已收到部分字节但超时,返回已收到的字节 (上层校验长度后会按短帧处理)
                    return offset;
                }

                // 单次读取的目标:如果已知 targetLen 就读到 targetLen,否则先读到至少能确定帧长 (3字节)
                int wantTotal = targetLen > 0 ? targetLen : Math.Min(3, recvBuffer.Length);
                int toRead = wantTotal - offset;
                if (toRead <= 0)
                {
                    // 已经攒够了
                    return offset;
                }

                // 用 Task.WhenAny 实现单次读取的超时控制
                var readTask = port.BaseStream.ReadAsync(recvBuffer, offset, toRead, ct);
                var timeoutTask = Task.Delay(remainingTime, ct);
                var finished = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);

                if (finished != readTask)
                {
                    // 整体超时
                    if (offset == 0)
                        throw new TimeoutException($"串口读取超时 ({timeoutMs}ms),设备无响应");
                    return offset;
                }

                int n = await readTask.ConfigureAwait(false);
                if (n <= 0)
                {
                    // 流被关闭或对端断开
                    if (offset == 0)
                        throw new TimeoutException("串口读取返回 0,对端可能已断开");
                    return offset;
                }

                offset += n;

                // 一旦攒到 3 字节,尝试推算完整帧长
                if (targetLen < 0 && offset >= 3)
                {
                    targetLen = InferModbusFrameLength(recvBuffer, offset);
                    if (targetLen <= 0)
                    {
                        // 无法推断帧长(未知功能码),保险起见再多收一会儿等空闲
                        // 这里直接返回当前数据,让上层按短帧/CRC 校验处理
                        return offset;
                    }
                    if (targetLen > recvBuffer.Length)
                    {
                        // 推算出来的长度超过缓冲区,异常情况,截断返回
                        return offset;
                    }
                }

                if (targetLen > 0 && offset >= targetLen)
                {
                    return offset;
                }
            }
        }

        /// <summary>
        /// 循环读取直到收到 ASCII 行协议的结束符,或超时。
        ///
        /// 与 Modbus 那条路的区别:没有长度字段可推算,只能一边收一边看尾巴是不是结束符。
        /// 结束符可能是多字节("\r\n"、Tricontinent 的 "\x03\r\n"),所以要按字节序列比,
        /// 不能逐字符判断 —— 这也是 PyLabware 专门写 stripper() 而不用 str.strip() 的原因。
        ///
        /// 超时后若已收到部分字节,按短帧返回交给上层判断,与 Modbus 那条路的处理一致。
        /// </summary>
        private static async Task<int> ReadUntilTerminatorAsync(
            SerialPort port, byte[] recvBuffer, string terminator, int timeoutMs, CancellationToken ct)
        {
            byte[] term = System.Text.Encoding.ASCII.GetBytes(
                string.IsNullOrEmpty(terminator) ? "\r\n" : terminator);

            int offset = 0;
            var sw = Stopwatch.StartNew();

            while (true)
            {
                int remainingTime = timeoutMs - (int)sw.ElapsedMilliseconds;
                if (remainingTime <= 0)
                {
                    if (offset == 0)
                        throw new TimeoutException($"串口读取超时 ({timeoutMs}ms),设备无响应");
                    return offset;
                }

                if (offset >= recvBuffer.Length)
                {
                    // 缓冲区满了还没见到结束符,按已收到的返回,避免死循环
                    return offset;
                }

                var readTask = port.BaseStream.ReadAsync(recvBuffer, offset, recvBuffer.Length - offset, ct);
                var timeoutTask = Task.Delay(remainingTime, ct);
                var finished = await Task.WhenAny(readTask, timeoutTask).ConfigureAwait(false);

                if (finished != readTask)
                {
                    if (offset == 0)
                        throw new TimeoutException($"串口读取超时 ({timeoutMs}ms),设备无响应");
                    return offset;
                }

                int n = await readTask.ConfigureAwait(false);
                if (n <= 0)
                {
                    if (offset == 0)
                        throw new TimeoutException("串口读取返回 0,对端可能已断开");
                    return offset;
                }

                offset += n;

                if (EndsWith(recvBuffer, offset, term))
                {
                    return offset;
                }
            }
        }

        /// <summary>已收到的 count 个字节,尾部是不是 suffix。</summary>
        private static bool EndsWith(byte[] buffer, int count, byte[] suffix)
        {
            if (count < suffix.Length) return false;
            int start = count - suffix.Length;
            for (int i = 0; i < suffix.Length; i++)
            {
                if (buffer[start + i] != suffix[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// 根据已收到的字节推算 Modbus RTU 应答完整帧长。
        /// 返回 <= 0 表示无法推断。
        /// </summary>
        private static int InferModbusFrameLength(byte[] buffer, int received)
        {
            if (received < 2) return -1;

            byte funcCode = buffer[1];

            // 异常响应:功能码最高位置 1,固定 5 字节
            if ((funcCode & 0x80) != 0)
            {
                return 5;   // slave(1) + func|0x80(1) + 错误码(1) + CRC(2)
            }

            switch (funcCode)
            {
                // 读类响应:第 3 字节为数据长度 N,总长 = 3 + N + 2
                case 0x01:   // Read Coils
                case 0x02:   // Read Discrete Inputs
                case 0x03:   // Read Holding Registers
                case 0x04:   // Read Input Registers
                    if (received < 3) return -1;
                    return 3 + buffer[2] + 2;

                // 写类响应:固定 8 字节 (slave+func+addr2+value/qty2+CRC2)
                case 0x05:   // Write Single Coil
                case 0x06:   // Write Single Register
                case 0x0F:   // Write Multiple Coils
                case 0x10:   // Write Multiple Registers
                    return 8;

                default:
                    return -1;   // 未知功能码,让上层处理
            }
        }

        public async Task<bool> SendAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            await _ioLock.WaitAsync(ct).ConfigureAwait(false);
            SerialPort port = null;
            try
            {
                port = OpenPort();
                await port.BaseStream.WriteAsync(buffer, offset, count, ct).ConfigureAwait(false);

                // ASCII 行协议里,不需要应答的命令(IKA 的 START_4/STOP_4、设定值下发等)是真的一个字节都不回。
                // 在这里做 drain 会白等满一个 ReadTimeoutMs(默认 3 秒),每次写都卡 3 秒。
                // 需要应答的 ASCII 命令由驱动改走 SendAndReceiveAsync,不走这条路。
                if (_config.Framing != SerialFraming.AsciiLine)
                {
                    // 写命令也需要等设备的应答回完,否则下一次读会拿到残留字节
                    // 对 Modbus 写应答固定 8 字节,我们尝试把它读掉(不解析,纯丢弃)
                    byte[] dummy = new byte[16];
                    try
                    {
                        // 用较短的超时,避免单播写卡死
                        await ReadModbusFrameAsync(port, dummy, _config.ReadTimeoutMs, ct).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // 写命令吃掉应答失败不影响写本身的语义
                    }
                }

                return true;
            }
            finally
            {
                ClosePort(port);
                _ioLock.Release();
            }
        }

        private SerialPort OpenPort()
        {
            var port = new SerialPort(_config.PortName)
            {
                BaudRate = _config.BaudRate,
                Parity = (Parity)Enum.Parse(typeof(Parity), _config.Parity),
                DataBits = _config.DataBits,
                StopBits = (StopBits)Enum.Parse(typeof(StopBits), _config.StopBits),
                ReadTimeout = _config.ReadTimeoutMs,
                WriteTimeout = _config.WriteTimeoutMs,
            };
            port.Open();
            return port;
        }

        private static void ClosePort(SerialPort port)
        {
            if (port == null) return;
            try { port.Close(); } catch { }
            try { port.Dispose(); } catch { }
        }

        public void Dispose()
        {
            _ioLock.Dispose();
        }
    }
}