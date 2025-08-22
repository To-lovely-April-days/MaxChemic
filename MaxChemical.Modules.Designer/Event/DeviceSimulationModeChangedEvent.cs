using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Prism.Events;

namespace MaxChemical.Modules.Designer.Event
{
    /// <summary>
    /// 获取模拟模式状态请求事件
    /// </summary>
    public class GetSimulationModeRequestEvent : PubSubEvent<Action<bool>>
    {
    }
    /// <summary>
    /// 设备模拟模式切换事件
    /// </summary>
    public class DeviceSimulationModeChangedEvent : PubSubEvent<DeviceSimulationModeEventArgs>
    {
    }

    /// <summary>
    /// 设备模拟模式事件参数
    /// </summary>
    public class DeviceSimulationModeEventArgs
    {
        /// <summary>
        /// 是否为模拟模式
        /// </summary>
        public bool IsSimulationMode { get; set; }

        /// <summary>
        /// 切换时间
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// 附加消息
        /// </summary>
        public string Message { get; set; }
    }
}
