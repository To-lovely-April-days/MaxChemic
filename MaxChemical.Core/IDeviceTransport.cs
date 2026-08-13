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
    /// 串口应答的分帧方式。
    ///
    /// 加这个枚举的原因:原来 SerialPortTransport 把 Modbus RTU 的分帧规则写死在读取逻辑里
    /// (先读 3 字节定长度,再读到 targetLen)。但实验室里大量仪器走的是 ASCII 行协议
    /// (IKA 磁力搅拌/旋蒸、Julabo/Huber 循环器、Tricontinent 注射泵、IDEX 选择阀、
    ///  Vacuubrand 真空泵、梅特勒天平 MT-SICS ……),应答靠结束符分帧而不是长度字段,
    /// Modbus 那套推算对它们完全不适用。
    ///
    /// 默认值是 ModbusRtu,所以既有驱动不设置这一项时行为一字不变。
    /// </summary>
    public enum SerialFraming
    {
        /// <summary>Modbus RTU:按功能码推算帧长(默认,保持既有行为)。</summary>
        ModbusRtu = 0,

        /// <summary>ASCII 行协议:读到 ReplyTerminator 为止。</summary>
        AsciiLine = 1,
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

        /// <summary>应答分帧方式。不设置时按 Modbus RTU 处理,与改动前一致。</summary>
        public SerialFraming Framing { get; set; } = SerialFraming.ModbusRtu;

        /// <summary>
        /// ASCII 行协议的应答结束符,仅当 Framing = AsciiLine 时有意义。
        /// 各家不一样:IKA/Julabo/Huber/Vacuubrand 是 "\r\n",IDEX 是 "\r",
        /// Tricontinent DT 协议是 "\x03\r\n"。
        /// </summary>
        public string ReplyTerminator { get; set; } = "\r\n";
    }
}