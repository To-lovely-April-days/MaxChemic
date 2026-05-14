using System.Collections.Generic;

namespace MaxChemical.Modules.GatewayConfig.Ipc
{
    /// <summary>
    /// 主进程 (AnyCPU) 和 32 位代理进程 (MaxChemical.GatewayConfig.Proxy.exe) 之间的通信协议。
    /// JSON over Named Pipe,每条消息一行 (LF 分隔)。
    /// </summary>
    public static class IpcProtocol
    {
        public const string PipeName = "MaxChemical.GatewayConfig.Proxy";
        public const int ProtocolVersion = 1;

        public static class Op
        {
            public const string Handshake     = "handshake";
            public const string Shutdown      = "shutdown";
            public const string SearchDevices = "search_devices";
            public const string ReadParams    = "read_params";
            public const string WriteParams   = "write_params";
            public const string SetDefault    = "set_default";
            public const string PollDevice    = "poll_device";
        }

        public static class NotifyOp
        {
            public const string SearchProgress = "notify.search_progress";
            public const string PollProgress   = "notify.poll_progress";
            public const string ProxyError     = "notify.proxy_error";
        }
    }

    public class IpcMessage
    {
        public string MsgType { get; set; } = "";
        public string Op { get; set; } = "";
        public string CorrelationId { get; set; } = "";
    }

    public class IpcRequest : IpcMessage
    {
        public IpcRequest() { MsgType = "request"; }
        public Dictionary<string, object> Payload { get; set; } = new Dictionary<string, object>();
    }

    public class IpcResponse : IpcMessage
    {
        public IpcResponse() { MsgType = "response"; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; } = "";
        public Dictionary<string, object> Payload { get; set; } = new Dictionary<string, object>();
    }

    public class IpcNotification : IpcMessage
    {
        public IpcNotification() { MsgType = "notification"; }
        public Dictionary<string, object> Payload { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// 跨进程传递的设备简要信息。
    /// DeviceId 是 ZLDM 返回的字符串型 ID (12 字符 Hex,即 MAC 的 ASCII 形式)。
    /// </summary>
    public class DeviceSnapshot
    {
        public string DeviceId { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string Mac { get; set; } = "";
        public string Ip { get; set; } = "";
        public ushort LocalPort { get; set; }
        public string Netmask { get; set; } = "";
        public string Gateway { get; set; } = "";
        public string DnsServer { get; set; } = "";
        public ushort WebPort { get; set; }
        public string FirmwareVersion { get; set; } = "";
        public int WorkMode { get; set; }
        public int BaudRate { get; set; }
        public int DataBits { get; set; }
        public int Parity { get; set; }
        public int StopBit { get; set; }
        public int FlowControl { get; set; }
        public ushort DestPort { get; set; }
        public string DestIp { get; set; } = "";
        public int ReconnectTime { get; set; }
        public int KeepAliveTime { get; set; }
        public int LinkStatus { get; set; }
    }
}
