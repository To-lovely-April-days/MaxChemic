using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using MaxChemical.Core;

namespace MaxChemical.Infrastructure.Services
{
    /// <summary>
    /// 物理串口实现。
    /// 每次 SendAndReceive 都打开/关闭串口,简单可靠 (代价是有 ~10ms 开销)。
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
                await port.BaseStream.WriteAsync(sendBuffer, 0, sendBuffer.Length, ct).ConfigureAwait(false);

                var reader = port.BaseStream.ReadAsync(recvBuffer, 0, recvBuffer.Length, ct);
                var finished = await Task.WhenAny(reader, Task.Delay(recvTimeoutMs, ct)).ConfigureAwait(false);
                if (finished != reader)
                    throw new TimeoutException($"串口读取超时 ({recvTimeoutMs}ms),设备无响应");

                return await reader.ConfigureAwait(false);
            }
            finally
            {
                ClosePort(port);
                _ioLock.Release();
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