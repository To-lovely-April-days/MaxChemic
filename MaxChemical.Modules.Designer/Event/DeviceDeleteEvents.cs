using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Prism.Events;

namespace MaxChemical.Modules.Designer.Event
{
    // 删除选中设备事件
    public class DeleteSelectedDeviceEvent : PubSubEvent<string>
    {
    }

    // 删除选中连接线事件
    public class DeleteSelectedConnectionsEvent : PubSubEvent
    {
    }

    // 设备删除完成事件
    public class DeviceDeletedEvent : PubSubEvent<DeviceDeletedEventArgs>
    {
    }

    public class DeviceDeletedEventArgs
    {
        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public int DeletedConnectionsCount { get; set; }
    }
}
