namespace MaxChemical.Modules.GatewayConfig.Models
{
    /// <summary>波特率 — SDK 用下拉框索引值,不是真实 bps。</summary>
    public enum BaudRate
    {
        Bps_1200 = 0,
        Bps_2400 = 1,
        Bps_4800 = 2,
        Bps_7200 = 3,
        Bps_9600 = 4,
        Bps_14400 = 5,
        Bps_19200 = 6,
        Bps_28800 = 7,
        Bps_38400 = 8,
        Bps_57600 = 9,
        Bps_76800 = 10,
        Bps_115200 = 11,
        Bps_240800 = 12,
        Bps_460800 = 13,
    }

    /// <summary>数据位 — 0=8位 1=7位 2=6位 3=5位</summary>
    public enum DataBits
    {
        Bits_8 = 0,
        Bits_7 = 1,
        Bits_6 = 2,
        Bits_5 = 3,
    }

    /// <summary>停止位 — 0=1位 1=2位</summary>
    public enum StopBit
    {
        Stop_1 = 0,
        Stop_2 = 1,
    }

    /// <summary>校验位 — 0=无 1=奇 2=偶 3=Mark 4=Space</summary>
    public enum Parity
    {
        None = 0,
        Odd = 1,
        Even = 2,
        Mark = 3,
        Space = 4,
    }

    /// <summary>流控 — 0=无 1=CTS/RTS 2=DTR/DSR 3=XON/XOFF</summary>
    public enum FlowControl
    {
        None = 0,
        RtsCts = 1,
        DtrDsr = 2,
        XonXoff = 3,
    }

    /// <summary>IP 模式 — 0=静态 1=DHCP</summary>
    public enum IpMode
    {
        Static = 0,
        Dhcp = 1,
    }

    /// <summary>工作模式 — 0=TCP Server 1=TCP Client 2=UDP 3=UDP 组播</summary>
    public enum WorkMode
    {
        TcpServer = 0,
        TcpClient = 1,
        Udp = 2,
        UdpMulti = 3,
    }

    /// <summary>连接状态 — 0=未连接 1=至少一个 TCP 连接 (来自 SDK,与下面 ChannelConnectionState 不同)</summary>
    public enum LinkStatus
    {
        Disconnected = 0,
        Connected = 1,
    }

    /// <summary>
    /// 通道的"绑定 + 探测"状态 — UI 渲染颜色据此区分。
    /// </summary>
    public enum ChannelConnectionState
    {
        /// <summary>未绑定设备 — 黑色描边</summary>
        Unbound = 0,

        /// <summary>已绑定但探测不到设备 (链路有问题) — 警告黄</summary>
        BoundOffline = 1,

        /// <summary>已绑定且连通 — 品牌红</summary>
        BoundOnline = 2,
    }
}