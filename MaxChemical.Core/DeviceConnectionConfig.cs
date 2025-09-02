using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Core
{
    /// <summary>
    /// 设备连接配置
    /// </summary>
    public static class DeviceConnectionConfig
    {
        /// <summary>
        /// 是否使用PLC模式
        /// </summary>
        public static bool UsePLCMode { get; set; } = false;

        /// <summary>
        /// PLC连接配置
        /// </summary>
        public static PLCConnectionSettings PLCSettings { get; set; } = new PLCConnectionSettings();
    }

    /// <summary>
    /// PLC连接设置
    /// </summary>
    public class PLCConnectionSettings
    {
        public string IpAddress { get; set; } = "192.168.1.100";
        public int Rack { get; set; } = 0;
        public int Slot { get; set; } = 1;
        public int ConnectionTimeout { get; set; } = 5000;
        public string ConnectionType { get; set; } = "S7-1500";
    }
}
