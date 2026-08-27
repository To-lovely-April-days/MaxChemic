// ============================================================================
//  PenLink.cs
//  笔式酸度-电导率计的"字节管道"抽象 + 串口实现。
//
//  为什么要抽这一层:这台仪器的协议本身只是一条字节流
//  (FF | 类型 | 功能码 | 长度 | 数据 | CRC8 | F0),协议内容与载体无关。
//  载体有两种:
//    · 电脑自带蓝牙直连(BlePenLink,原生 GATT)—— 常规接法;
//    · BLE 透传模块转出来的虚拟串口(SerialPenLink)—— 电脑没蓝牙、
//      或者蓝牙被 IT 策略禁用时的退路。
//
//  两种载体的差别全部关在这一层里,驱动侧只认 IPenLink:拿到字节就拼帧,
//  要发就调 Write。分帧、重同步、快照全都不用改。
// ============================================================================

using System;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace PenMeter_BLE
{
    /// <summary>一条通往仪器的双向字节管道。</summary>
    internal interface IPenLink : IDisposable
    {
        /// <summary>管道是否可用。</summary>
        bool IsOpen { get; }

        /// <summary>人类可读的连接目标描述,用于日志与错误信息。</summary>
        string Description { get; }

        /// <summary>建立连接。失败时应把失败原因写进 <paramref name="error"/> 交给调用方展示。</summary>
        Task<bool> OpenAsync(CancellationToken ct);

        /// <summary>打开失败的原因(给操作员看的中文说明)。</summary>
        string? LastError { get; }

        /// <summary>
        /// 发送一帧。
        ///
        /// 之所以是异步的:命令处理链最终会在 UI 线程上同步跑到这里
        /// (DeviceCommand 的 AsyncAction 里 Task.FromResult(...) 的实参是**先算完**再包成 Task 的),
        /// 而 BLE 的 GattCharacteristic.WriteValueAsync 是 WinRT 异步操作。
        /// 在 UI 线程上对它 .GetAwaiter().GetResult() 阻塞等,轻则界面卡住,
        /// 重则完成回调要回到这个已经被占住的线程上而死锁。所以整条写路径必须 await 到底。
        /// </summary>
        Task<bool> WriteAsync(byte[] frame, CancellationToken ct = default);

        /// <summary>
        /// 收到字节。**可能在任意线程上触发**,而且 BLE 那边还可能并发触发,
        /// 所以订阅方必须自己加锁(驱动侧的拼帧缓冲区已经加了)。
        /// </summary>
        event Action<byte[]>? BytesReceived;

        /// <summary>底层链路断开(蓝牙断连、串口拔了)。用于把设备状态打成掉线。</summary>
        event Action? Closed;
    }

    /// <summary>
    /// 走 BLE 透传模块虚拟串口的实现。
    ///
    /// 用独立线程 + 阻塞读,而不是 SerialPort.DataReceived:
    /// DataReceived 跑在串口内部的线程池回调里,重入与顺序都不好把握,
    /// 而这台仪器是持续推流,正好适合一根专线一直读。
    /// </summary>
    internal sealed class SerialPenLink : IPenLink
    {
        private readonly string _portName;
        private readonly int _baud;
        private readonly object _lock = new object();

        private SerialPort? _port;
        private Thread? _rxThread;
        private volatile bool _running;

        public SerialPenLink(string portName, int baud)
        {
            _portName = portName;
            _baud = baud;
        }

        public string Description => $"串口 {_portName} @ {_baud}";
        public string? LastError { get; private set; }

        public bool IsOpen
        {
            get { lock (_lock) return _port is { IsOpen: true }; }
        }

        public event Action<byte[]>? BytesReceived;
        public event Action? Closed;

        public Task<bool> OpenAsync(CancellationToken ct)
        {
            try
            {
                var port = new SerialPort(_portName, _baud, Parity.None, 8, StopBits.One)
                {
                    // 读超时决定接收线程空转的节奏。200ms 既不会把 CPU 吃满,
                    // 也不会让"停止"等太久。
                    ReadTimeout = 200,
                    WriteTimeout = 1000,
                    DtrEnable = true,
                    RtsEnable = true,
                };
                port.Open();
                port.DiscardInBuffer();
                port.DiscardOutBuffer();
                lock (_lock) _port = port;
            }
            catch (Exception ex)
            {
                LastError = $"打开串口 {_portName} 失败:{ex.Message}";
                return Task.FromResult(false);
            }

            _running = true;
            _rxThread = new Thread(ReceiveLoop)
            {
                IsBackground = true,
                Name = $"PenMeter-COM-{_portName}"
            };
            _rxThread.Start();
            return Task.FromResult(true);
        }

        private void ReceiveLoop()
        {
            var chunk = new byte[512];
            while (_running)
            {
                SerialPort? port;
                lock (_lock) port = _port;
                if (port is null || !port.IsOpen) break;

                int read;
                try
                {
                    read = port.Read(chunk, 0, chunk.Length);
                }
                catch (TimeoutException)
                {
                    continue;                    // 正常:这段时间仪器没推东西
                }
                catch
                {
                    break;
                }

                if (read <= 0) continue;

                var buf = new byte[read];
                Buffer.BlockCopy(chunk, 0, buf, 0, read);
                BytesReceived?.Invoke(buf);
            }

            if (_running) Closed?.Invoke();      // 不是我们主动关的
        }

        public Task<bool> WriteAsync(byte[] frame, CancellationToken ct = default)
        {
            // 串口这边本来就是同步 API,27 字节在 115200 上不到 3ms。
            // 直接同步做完包成 Task,不必甩到线程池上 —— 那反而会打乱发送顺序。
            try
            {
                lock (_lock)
                {
                    if (_port is null || !_port.IsOpen) return Task.FromResult(false);
                    _port.Write(frame, 0, frame.Length);
                }
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                LastError = $"串口写入失败:{ex.Message}";
                return Task.FromResult(false);
            }
        }

        public void Dispose()
        {
            _running = false;

            var t = _rxThread;
            _rxThread = null;
            // 接收线程最长阻塞一个 ReadTimeout(200ms),给 1 秒足够它自己退出。
            // join 不成也不强杀 —— 强杀会让串口句柄悬着,下次打开报"拒绝访问"。
            try { t?.Join(1000); } catch { }

            lock (_lock)
            {
                try { _port?.Close(); } catch { }
                try { _port?.Dispose(); } catch { }
                _port = null;
            }
        }
    }
}
