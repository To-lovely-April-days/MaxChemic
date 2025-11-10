using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Prism.Events;

namespace MaxChemical.Modules.Designer.Event
{
    /// <summary>
    /// 请求获取设备监控参数选择状态
    /// </summary>
    public class GetDeviceMonitoringSelectionsRequestEvent : PubSubEvent<Action<Dictionary<string, HashSet<string>>>> { }

    /// <summary>
    /// 请求获取监控卡片状态
    /// </summary>
    public class GetMonitoringCardStatesRequestEvent : PubSubEvent<Action<Dictionary<string, bool>>> { }

    /// <summary>
    /// 恢复设备监控参数选择状态
    /// </summary>
    public class RestoreDeviceMonitoringSelectionsEvent : PubSubEvent<Dictionary<string, HashSet<string>>> { }

    /// <summary>
    /// 恢复监控卡片状态
    /// </summary>
    public class RestoreMonitoringCardStatesEvent : PubSubEvent<Dictionary<string, bool>> { }

    /// <summary>
    /// 获取监控卡片位置请求事件
    /// </summary>
    public class GetMonitoringCardPositionsRequestEvent : PubSubEvent<Action<Dictionary<string, Point>>>
    {
    }
    /// <summary>
    /// 恢复监控卡片位置事件
    /// </summary>
    public class RestoreMonitoringCardPositionsEvent : PubSubEvent<Dictionary<string, Point>>
    {
    }
}
