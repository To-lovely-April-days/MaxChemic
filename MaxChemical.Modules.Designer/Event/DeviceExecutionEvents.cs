using Prism.Events;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Modules.Designer.Event
{
    // 设备执行动画事件
    public class DeviceExecutionStartedEvent : PubSubEvent<DeviceExecutionEventArgs> { }
    public class DeviceExecutionEndedEvent : PubSubEvent<DeviceExecutionEventArgs> { }

    public class DeviceExecutionEventArgs
    {
        public string DeviceId { get; set; }
        public string CommandName { get; set; }
        public bool IsSuccessful { get; set; }
        public string ErrorMessage { get; set; }

        public bool IsSimulationMode { get; set; }
    }

    // 获取“设备Id -> 显示名(泵#2)”映射
    public class GetDeviceDisplayNamesRequestEvent : PubSubEvent<Action<Dictionary<string, string>>> { }
}
