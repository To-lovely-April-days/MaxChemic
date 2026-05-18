using System;
using System.Threading;
using System.Threading.Tasks;

namespace MaxChemical.Core
{
    /// <summary>
    /// 设备字节流通信抽象。
    /// 隔离"物理串口"和"通过 ZLAN 网关的 TCP"两种通信路径。
    /// 设备驱动通过 IDeviceTransportFactory 获取,不感知底层是哪种实现。
    /// </summary>
    public interface IDeviceTransport : IDisposable
    {
        /// <summary>
        /// 一次完整的"发送 + 接收"事务,由 Transport 内部保证串行化。
        /// </summary>
        /// <param name="sendBuffer">要发送的字节</param>
        /// <param name="recvBuffer">接收缓冲区</param>
        /// <param name="recvTimeoutMs">接收超时</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>实际读取的字节数</returns>
        Task<int> SendAndReceiveAsync(
            byte[] sendBuffer,
            byte[] recvBuffer,
            int recvTimeoutMs,
            CancellationToken ct);

        /// <summary>
        /// 只发送不等待应答 (写命令场景)。
        /// </summary>
        Task<bool> SendAsync(byte[] buffer, int offset, int count, CancellationToken ct);
    }

    public interface IDeviceTransportFactory
    {
        Task<IDeviceTransport> CreateTransportAsync(
            string deviceTypeName,
            string deviceInstanceId,
            SerialPortConfig serialConfig,
            CommunicationMode mode);       

        void InvalidateCache();
    }

    /// <summary>
    /// 串口配置 — 给 SerialPortTransport 用的。
    /// </summary>
    public class SerialPortConfig
    {
        public string PortName { get; set; } = "COM1";
        public int BaudRate { get; set; } = 9600;
        public string Parity { get; set; } = "None";
        public int DataBits { get; set; } = 8;
        public string StopBits { get; set; } = "One";
        public int ReadTimeoutMs { get; set; } = 3000;
        public int WriteTimeoutMs { get; set; } = 3000;
    }
}