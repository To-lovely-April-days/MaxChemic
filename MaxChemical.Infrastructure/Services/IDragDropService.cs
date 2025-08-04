using System;
using System.Windows;
using DevicePlugins.Devices;
using MaxChemical.Infrastructure.Events;
using Prism.Events;

namespace MaxChemical.Modules.Designer.Services
{
    public interface IDragDropService
    {
        // 开始拖拽设备（从设备库）
        void StartDeviceDrag(IDevice device, FrameworkElement sourceElement, Point startPosition);

        // 开始拖拽画布上的设备
        void StartCanvasDeviceDrag(string deviceId, IDevice device, FrameworkElement deviceElement, Point startPosition);

        // 更新拖拽位置
        void UpdateDragPosition(Point currentPosition);

        // 结束拖拽
        void EndDrag(Point endPosition, bool isSuccessful, string targetType = null);

        // 检查是否正在拖拽
        bool IsDragging { get; }

        // 当前拖拽的设备
        IDevice CurrentDraggedDevice { get; }

        // 当前拖拽类型
        string CurrentDragType { get; }
    }
}

