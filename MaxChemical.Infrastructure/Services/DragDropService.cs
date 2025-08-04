using System;
using System.Windows;
using DevicePlugins.Devices;
using MaxChemical.Infrastructure.Events;
using Prism.Events;

namespace MaxChemical.Modules.Designer.Services
{
    public class DragDropService : IDragDropService
    {
        private readonly IEventAggregator _eventAggregator;

        private bool _isDragging;
        private IDevice _currentDraggedDevice;
        private string _currentDragType;
        private Point _dragStartPosition;
        private FrameworkElement _sourceElement;
        private string _deviceId;

        public bool IsDragging => _isDragging;
        public IDevice CurrentDraggedDevice => _currentDraggedDevice;
        public string CurrentDragType => _currentDragType;

        public DragDropService(IEventAggregator eventAggregator)
        {
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
        }

        public void StartDeviceDrag(IDevice device, FrameworkElement sourceElement, Point startPosition)
        {
            if (_isDragging) return;

            _isDragging = true;
            _currentDraggedDevice = device;
            _currentDragType = "Library";
            _dragStartPosition = startPosition;
            _sourceElement = sourceElement;

            // 发布拖拽开始事件
            _eventAggregator.GetEvent<DeviceDragStartedEvent>().Publish(new DeviceDragStartedEventArgs
            {
                Device = device,
                StartPosition = startPosition,
                SourceType = "Library",
                SourceElement = sourceElement
            });
        }

        public void StartCanvasDeviceDrag(string deviceId, IDevice device, FrameworkElement deviceElement, Point startPosition)
        {
            if (_isDragging) return;

            _isDragging = true;
            _currentDraggedDevice = device;
            _currentDragType = "Canvas";
            _dragStartPosition = startPosition;
            _sourceElement = deviceElement;
            _deviceId = deviceId;

            // 发布拖拽开始事件
            _eventAggregator.GetEvent<DeviceDragStartedEvent>().Publish(new DeviceDragStartedEventArgs
            {
                Device = device,
                StartPosition = startPosition,
                SourceType = "Canvas",
                SourceElement = deviceElement
            });
        }

        public void UpdateDragPosition(Point currentPosition)
        {
            if (!_isDragging) return;

            var deltaPosition = new Point(
                currentPosition.X - _dragStartPosition.X,
                currentPosition.Y - _dragStartPosition.Y
            );

            // 发布拖拽进行中事件
            _eventAggregator.GetEvent<DeviceDraggingEvent>().Publish(new DeviceDraggingEventArgs
            {
                Device = _currentDraggedDevice,
                CurrentPosition = currentPosition,
                DeltaPosition = deltaPosition,
                IsOverValidDropTarget = true // 这里可以添加验证逻辑
            });
        }

        public void EndDrag(Point endPosition, bool isSuccessful, string targetType = null)
        {
            if (!_isDragging) return;

            // 发布拖拽结束事件
            _eventAggregator.GetEvent<DeviceDragEndedEvent>().Publish(new DeviceDragEndedEventArgs
            {
                Device = _currentDraggedDevice,
                EndPosition = endPosition,
                IsSuccessful = isSuccessful,
                TargetType = targetType ?? "Unknown"
            });

            // 重置状态
            _isDragging = false;
            _currentDraggedDevice = null;
            _currentDragType = null;
            _sourceElement = null;
            _deviceId = null;
        }
    }
}