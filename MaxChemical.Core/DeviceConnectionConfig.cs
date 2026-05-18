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