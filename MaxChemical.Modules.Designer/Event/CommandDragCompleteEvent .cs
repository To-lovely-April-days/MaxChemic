using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DevicePlugins.Devices;
using Prism.Events;
using System.Windows;

namespace MaxChemical.Modules.Designer.Event
{
    // 命令拖拽完成事件
    public class CommandDragCompleteEvent : PubSubEvent<CommandDragResult>
    {
    }

    // 命令拖拽结果
    public class CommandDragResult
    {
        public DeviceCommand Command { get; set; }
        public DragDropEffects DragResult { get; set; }
        public DateTime CompletedAt { get; set; }
    }

    // 命令节点添加到画布事件
    public class CommandNodeAddedToCanvasEvent : PubSubEvent<CommandNodeAddedEventArgs>
    {
    }

    public class CommandNodeAddedEventArgs
    {
        public DeviceCommand Command { get; set; }
        public Point Position { get; set; }
        public DateTime AddedAt { get; set; }
    }
}
