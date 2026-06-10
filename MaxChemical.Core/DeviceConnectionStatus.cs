using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Core
{
    /// <summary>
    /// 设备连接状态
    /// </summary>
    public enum DeviceConnectionStatus
    {
        /// <summary>
        /// 未连接
        /// </summary>
        Disconnected,

        /// <summary>
        /// 连接中
        /// </summary>
        Connecting,

        /// <summary>
        /// 已连接
        /// </summary>
        Connected,

        /// <summary>
        /// 断开连接中
        /// </summary>
        Disconnecting,

        /// <summary>
        /// 连接错误
        /// </summary>
        Error,

        /// <summary>
        /// 超时
        /// </summary>
        Timeout
    }
}
