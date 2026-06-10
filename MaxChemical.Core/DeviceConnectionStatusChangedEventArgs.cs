using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Core
{
    /// <summary>
    /// 设备连接状态变更事件参数
    /// </summary>
    public class DeviceConnectionStatusChangedEventArgs : EventArgs
    {
        /// <summary>
        /// 旧状态
        /// </summary>
        public DeviceConnectionStatus OldStatus { get; }

        /// <summary>
        /// 新状态
        /// </summary>
        public DeviceConnectionStatus NewStatus { get; }

        /// <summary>
        /// 变更时间
        /// </summary>
        public DateTime Timestamp { get; }

        public DeviceConnectionStatusChangedEventArgs(DeviceConnectionStatus oldStatus, DeviceConnectionStatus newStatus)
        {
            OldStatus = oldStatus;
            NewStatus = newStatus;
            Timestamp = DateTime.Now;
        }
    }


}
