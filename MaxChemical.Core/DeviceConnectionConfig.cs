// DeviceConnectionConfig.cs
using System;
using System.Collections.Generic;

namespace MaxChemical.Core
{
    public static class DeviceConnectionConfig
    {
        /// <summary>
        /// 通信模式
        /// </summary>
        public static CommunicationMode CommunicationMode { get; set; } = CommunicationMode.Direct;

        /// <summary>
        /// PLC连接设置
        /// </summary>
        public static PLCConnectionSettings PLCSettings { get; set; } = new PLCConnectionSettings();

        /// <summary>
        /// ModbusTcp连接设置
        /// </summary>
        public static ModbusTcpConnectionSettings ModbusTcpSettings { get; set; } = new ModbusTcpConnectionSettings();

        /// <summary>
        /// 自建云服务器（DTU 网关）连接设置
        /// </summary>
        public static RemoteServerSettings RemoteServerSettings { get; set; } = new RemoteServerSettings();

        /// <summary>
        /// 是否使用PLC模式（向后兼容）
        /// </summary>
        [Obsolete("请使用 CommunicationMode 属性")]
        public static bool UsePLCMode
        {
            get => CommunicationMode == CommunicationMode.PLC;
            set => CommunicationMode = value ? CommunicationMode.PLC : CommunicationMode.Direct;
        }
    }

    /// <summary>
    /// 通信模式枚举
    /// </summary>
    public enum CommunicationMode
    {
        /// <summary>
        /// 直连模式（每个设备独立连接）
        /// </summary>
        Direct = 0,

        /// <summary>
        /// PLC统一连接模式
        /// </summary>
        PLC = 1,

        /// <summary>
        /// ModbusTcp统一连接模式
        /// </summary>
        ModbusTcp = 2,

        ZLanGateway,    // 透过 ZLAN 网关的 TCP 连接模式

        /// <summary>
        /// 自建云服务器模式：DTU 连到云服务器，本程序经 SignalR 隧道把 Modbus 字节
        /// 转发到云端 DTU 网关，按设备序列号路由。原始 Modbus 字节透传。
        /// </summary>
        RemoteServer,
    }

    /// <summary>
    /// 自建云服务器（DTU 网关）连接设置。
    /// 本程序作为 SignalR 客户端连到云服务器，把设备的 Modbus 字节经云中转到 DTU。
    /// </summary>
    public class RemoteServerSettings
    {
        /// <summary>
        /// 服务器 SignalR Hub 地址，例如 http://your-server:5000/dtuhub
        /// </summary>
        public string ServerUrl { get; set; } = "http://139.224.67.86:9080/dtuhub";//

        /// <summary>
        /// 接入令牌（与服务器配置一致；服务器据此鉴权，防止他人控制设备）。
        /// </summary>
        public string AccessToken { get; set; } = "change-me-please";

        /// <summary>
        /// 单次设备读写（经云）的超时（毫秒）。
        /// </summary>
        public int RequestTimeoutMs { get; set; } = 8000;
    }

    /// <summary>
    /// ModbusTcp连接设置
    /// </summary>
    public class ModbusTcpConnectionSettings
    {
        /// <summary>
        /// Modbus服务器IP地址
        /// </summary>
        public string IpAddress { get; set; } = "192.168.2.191";

        /// <summary>
        /// Modbus服务器端口
        /// </summary>
        public int Port { get; set; } = 502;

        /// <summary>
        /// 连接超时时间（毫秒）
        /// </summary>
        public int ConnectTimeout { get; set; } = 3000;

        /// <summary>
        /// 读写超时时间（毫秒）
        /// </summary>
        public int ReadWriteTimeout { get; set; } = 1000;

        /// <summary>
        /// 是否启用重连
        /// </summary>
        public bool EnableReconnect { get; set; } = true;

        /// <summary>
        /// 重连间隔（毫秒）
        /// </summary>
        public int ReconnectInterval { get; set; } = 5000;

        /// <summary>
        /// 最大重连次数
        /// </summary>
        public int MaxReconnectAttempts { get; set; } = 3;
    }

    public class PLCConnectionSettings
    {
        public string IpAddress { get; set; } = "192.168.1.10";
        public int Rack { get; set; } = 0;
        public int Slot { get; set; } = 1;
        public string ConnectionType { get; set; } = "S7-1500";
    }
}
