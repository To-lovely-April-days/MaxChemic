using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Prism.Events;

namespace MaxChemical.Modules.Designer.Event
{
    // 缩放画布事件
    public class ZoomCanvasEvent : PubSubEvent<double>
    {
    }

    // 缩放选中设备事件
    public class ZoomSelectedDeviceEvent : PubSubEvent<double>
    {
    }

    // 画布自适应大小事件
    public class FitCanvasToViewEvent : PubSubEvent
    {
    }

    // 获取当前选中设备请求事件
    public class GetSelectedDeviceRequestEvent : PubSubEvent<Action<string>>
    {
    }

    // 获取画布缩放级别请求事件
    public class GetCanvasZoomRequestEvent : PubSubEvent<Action<double>>
    {
    }
}
