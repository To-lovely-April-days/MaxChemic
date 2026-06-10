using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DevicePlugins.Devices;
using Prism.Events;

namespace MaxChemical.Modules.Designer.Event
{
    /// <summary>
    /// 监控卡片更新事件
    /// </summary>
    public class MonitoringCardUpdateEvent : PubSubEvent<MonitoringCardUpdateEventArgs>
    {
    }

    /// <summary>
    /// 监控卡片更新事件参数
    /// </summary>
    public class MonitoringCardUpdateEventArgs
    {
        public string DeviceId { get; set; }
        public string CommandName { get; set; }
        public DeviceParameters OutputParameters { get; set; }
        public DateTime UpdateTime { get; set; }
    }
}
