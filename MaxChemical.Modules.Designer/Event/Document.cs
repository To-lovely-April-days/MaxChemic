using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MaxChemical.Modules.Designer.Models;
using Prism.Events;

namespace MaxChemical.Modules.Designer.Event
{
    public class GetCurrentCanvasDataRequestEvent : PubSubEvent<Action<CanvasDocument>>
    {
    }

    public class NewCanvasCreatedEvent : PubSubEvent<CanvasDocument>
    {
    }

    public class CanvasSavedEvent : PubSubEvent<string>
    {
    }

    public class ClearCanvasRequestEvent : PubSubEvent
    {
    }

    public class CanvasModifiedEvent : PubSubEvent
    {
    }

    public class LoadCanvasRequestEvent : PubSubEvent<CanvasDocument>
    {
    }

    public class CanvasLoadedEvent : PubSubEvent<CanvasDocument>
    {
    }

    public class RestoreDeviceRequestEvent : PubSubEvent<CanvasDeviceData>
    {
    }

    public class RestoreConnectionRequestEvent : PubSubEvent<ConnectionData>
    {
    }

    // 获取当前文件路径请求事件
    public class GetCurrentFilePathRequestEvent : PubSubEvent<Action<string>>
    {
    }

    // 设置当前文件路径事件
    public class SetCurrentFilePathEvent : PubSubEvent<string>
    {
    }

    // 文件保存完成事件
    public class FileSavedEvent : PubSubEvent<string>
    {
    }
}
