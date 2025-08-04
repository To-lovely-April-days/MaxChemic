using System;
using System.Windows;
using DevicePlugins.Devices;
using Prism.Events;

namespace MaxChemical.Infrastructure.Events
{
    // 设备拖拽完成事件参数
    public class DeviceDragEventArgs
    {
        public IDevice Device { get; set; }
        public DragDropEffects DragResult { get; set; }
        public DateTime EndTime { get; set; }
    }

    // 设备拖拽完成事件
    public class DeviceDragCompleteEvent : PubSubEvent<DeviceDragEventArgs>
    {
    }

    // 设备放置事件参数
    public class DeviceDropEventArgs
    {
        public IDevice Device { get; set; }
        public Point Position { get; set; }
        public string CanvasName { get; set; }
    }

    // 设备放置事件
    public class DeviceDroppedOnCanvasEvent : PubSubEvent<DeviceDropEventArgs>
    {
    }

    // 设备移动事件参数（新增）
    public class DeviceMoveEventArgs
    {
        public IDevice Device { get; set; }
        public Point OldPosition { get; set; }
        public Point NewPosition { get; set; }
        public string CanvasName { get; set; }
    }

    // 设备移动事件（新增）
    public class DeviceMovedOnCanvasEvent : PubSubEvent<DeviceMoveEventArgs>
    {
    }

    // 设备拖拽信息
    public class DeviceDragInfo
    {
        public IDevice Device { get; set; }
        public System.Windows.Controls.Control SourceControl { get; set; }
        public DateTime DragStartTime { get; set; }
    }
}