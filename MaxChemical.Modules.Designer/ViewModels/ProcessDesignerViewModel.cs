// ViewModels/ProcessDesignerViewModel.cs - 扩展后的完整文件
using System;
using System.Collections.Generic;
using System.Windows;
using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Modules.Designer.Services;
using MaxChemical.Modules.Designer.Models;
using Prism.Events;
using Prism.Mvvm;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Linq;

namespace MaxChemical.Modules.Designer.ViewModels
{
    public class ProcessDesignerViewModel : BindableBase
    {
        private readonly IRegionOverlayService _regionOverlayService;
        private readonly IDragDropService _dragDropService;
        private RegionLayout _currentRegionLayout;
        private double _canvasWidth = 1200;
        private double _canvasHeight = 800;
        private bool _isDragOver;

        // 画布上的设备集合
        private ObservableCollection<CanvasDeviceViewModel> _canvasDevices;
        // 连接集合
        private ObservableCollection<ConnectionInfo> _connections;

        public IEventAggregator EventAggregator { get; private set; }

        public RegionLayout CurrentRegionLayout
        {
            get => _currentRegionLayout;
            set => SetProperty(ref _currentRegionLayout, value);
        }

        public double CanvasWidth
        {
            get => _canvasWidth;
            set => SetProperty(ref _canvasWidth, value);
        }

        public double CanvasHeight
        {
            get => _canvasHeight;
            set => SetProperty(ref _canvasHeight, value);
        }

        public bool IsDragOver
        {
            get => _isDragOver;
            set => SetProperty(ref _isDragOver, value);
        }

        public ObservableCollection<CanvasDeviceViewModel> CanvasDevices
        {
            get => _canvasDevices;
            set => SetProperty(ref _canvasDevices, value);
        }

        public ObservableCollection<ConnectionInfo> Connections
        {
            get => _connections;
            set => SetProperty(ref _connections, value);
        }
        private string _currentFilePath;
        private bool _isModified;
        private string _documentTitle;

        // 当前文件路径
        public string CurrentFilePath
        {
            get => _currentFilePath;
            set => SetProperty(ref _currentFilePath, value);
        }

        // 是否已修改
        public bool IsModified
        {
            get => _isModified;
            set
            {
                SetProperty(ref _isModified, value);
                UpdateDocumentTitle();
            }
        }

        // 文档标题（用于显示在标题栏）
        public string DocumentTitle
        {
            get => _documentTitle;
            private set => SetProperty(ref _documentTitle, value);
        }

        // 更新文档标题
        private void UpdateDocumentTitle()
        {
            var fileName = string.IsNullOrEmpty(CurrentFilePath) ? "未命名" : System.IO.Path.GetFileNameWithoutExtension(CurrentFilePath);
            DocumentTitle = IsModified ? $"{fileName}*" : fileName;
        }

        // 设置当前文件（打开文件时调用）
        public void SetCurrentFile(string filePath)
        {
            CurrentFilePath = filePath;
            IsModified = false;
            UpdateDocumentTitle();

            Debug.WriteLine($"设置当前文件: {filePath}");
        }

        // 标记文件已修改
        public void MarkAsModified()
        {
            IsModified = true;
            Debug.WriteLine("文件已标记为修改");
        }

        // 文件保存后调用
        public void OnFileSaved(string filePath = null)
        {
            if (!string.IsNullOrEmpty(filePath))
            {
                CurrentFilePath = filePath;
            }

            IsModified = false;
            UpdateDocumentTitle();

            Debug.WriteLine($"文件已保存: {CurrentFilePath}");
        }
        public ProcessDesignerViewModel(
            IEventAggregator eventAggregator,
            IRegionOverlayService regionOverlayService,
            IDragDropService dragDropService)
        {
            EventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _regionOverlayService = regionOverlayService ?? throw new ArgumentNullException(nameof(regionOverlayService));
            _dragDropService = dragDropService ?? throw new ArgumentNullException(nameof(dragDropService));

            CanvasDevices = new ObservableCollection<CanvasDeviceViewModel>();
            Connections = new ObservableCollection<ConnectionInfo>();

            SubscribeToEvents();
            InitializeServices();

            Debug.WriteLine("ProcessDesignerViewModel 构造函数被调用");
        }

        private void SubscribeToEvents()
        {
            // 订阅画布拖拽事件
            EventAggregator.GetEvent<CanvasDragEnterEvent>().Subscribe(OnCanvasDragEnter);
            EventAggregator.GetEvent<CanvasDragOverEvent>().Subscribe(OnCanvasDragOver);
            EventAggregator.GetEvent<CanvasDragLeaveEvent>().Subscribe(OnCanvasDragLeave);
            EventAggregator.GetEvent<CanvasDropEvent>().Subscribe(OnCanvasDrop);

            // 订阅设备拖拽事件
            EventAggregator.GetEvent<DeviceDragStartedEvent>().Subscribe(OnDeviceDragStarted);
            EventAggregator.GetEvent<DeviceDraggingEvent>().Subscribe(OnDeviceDragging);
            EventAggregator.GetEvent<DeviceDragEndedEvent>().Subscribe(OnDeviceDragEnded);

            // 订阅画布设备位置变化事件
            EventAggregator.GetEvent<CanvasDevicePositionChangedEvent>().Subscribe(OnCanvasDevicePositionChanged);
        }

        private void InitializeServices()
        {
            _regionOverlayService.CanvasSizeChanged += OnCanvasSizeChanged;
            _regionOverlayService.RegionLayoutChanged += OnRegionLayoutChanged;
        }

        #region 拖拽事件处理

        private void OnCanvasDragEnter(CanvasDragEventArgs args)
        {
            IsDragOver = true;
            Debug.WriteLine($"设备 {args.Device?.Name} 拖拽进入画布");
        }

        private void OnCanvasDragOver(CanvasDragEventArgs args)
        {
            Debug.WriteLine($"设备在画布上拖拽，位置: ({args.Position.X:F0}, {args.Position.Y:F0})");
        }

        private void OnCanvasDragLeave(CanvasDragEventArgs args)
        {
            IsDragOver = false;
            Debug.WriteLine($"设备 {args.Device?.Name} 拖拽离开画布");
        }

        private void OnCanvasDrop(CanvasDropEventArgs args)
        {
            IsDragOver = false;

            if (args.Device != null)
            {
                var deviceId = Guid.NewGuid().ToString();
                var canvasDevice = new CanvasDeviceViewModel
                {
                    DeviceId = deviceId,
                    Device = args.Device,
                    Position = args.Position,
                    Width = 80,
                    Height = 60
                };

                CanvasDevices.Add(canvasDevice);

                Debug.WriteLine($"设备 {args.Device.Name} 已放置到画布，位置: ({args.Position.X:F0}, {args.Position.Y:F0})");

                args.IsSuccessful = true;
                args.DeviceId = deviceId;
            }
        }

        private void OnDeviceDragStarted(DeviceDragStartedEventArgs args)
        {
            Debug.WriteLine($"设备拖拽开始: {args.Device?.Name}，来源: {args.SourceType}");

            if (args.SourceType == "Canvas")
            {
                var canvasDevice = FindCanvasDeviceByElement(args.SourceElement);
                if (canvasDevice != null)
                {
                    canvasDevice.IsDragging = true;
                }
            }
        }

        private void OnDeviceDragging(DeviceDraggingEventArgs args)
        {
            if (_dragDropService.CurrentDragType == "Canvas")
            {
                EventAggregator.GetEvent<CanvasDevicePositionChangedEvent>().Publish(new CanvasDevicePositionChangedEventArgs
                {
                    Device = args.Device,
                    NewPosition = args.CurrentPosition,
                    IsCompleted = false
                });
            }
        }

        private void OnDeviceDragEnded(DeviceDragEndedEventArgs args)
        {
            Debug.WriteLine($"设备拖拽结束: {args.Device?.Name}，成功: {args.IsSuccessful}");

            foreach (var device in CanvasDevices)
            {
                device.IsDragging = false;
            }
        }

        private void OnCanvasDevicePositionChanged(CanvasDevicePositionChangedEventArgs args)
        {
            var canvasDevice = CanvasDevices.FirstOrDefault(d => d.DeviceId == args.DeviceId);
            if (canvasDevice != null)
            {
                canvasDevice.Position = args.NewPosition;

                if (args.IsCompleted)
                {
                    Debug.WriteLine($"设备 {args.Device?.Name} 移动完成，新位置: ({args.NewPosition.X:F0}, {args.NewPosition.Y:F0})");
                }
            }
        }

        #endregion

        #region 连接管理

        public void AddConnection(ConnectionInfo connection)
        {
            if (connection != null && !Connections.Contains(connection))
            {
                Connections.Add(connection);
                Debug.WriteLine($"连接已添加到ViewModel: {connection.Id}");
            }
        }

        public void RemoveConnection(string connectionId)
        {
            var connection = Connections.FirstOrDefault(c => c.Id == connectionId);
            if (connection != null)
            {
                Connections.Remove(connection);
                Debug.WriteLine($"连接已从ViewModel移除: {connectionId}");
            }
        }

        public void RemoveConnectionsForDevice(string deviceId)
        {
            var relatedConnections = Connections.Where(c => c.StartDeviceId == deviceId || c.EndDeviceId == deviceId).ToList();
            foreach (var connection in relatedConnections)
            {
                Connections.Remove(connection);
                Debug.WriteLine($"设备相关连接已移除: {connection.Id}");
            }
        }

        #endregion

        #region 辅助方法

        private CanvasDeviceViewModel FindCanvasDeviceByElement(object element)
        {
            // 这里需要根据实际的元素查找逻辑来实现
            // 可能需要通过Tag或其他标识来关联
            return null;
        }

        private void OnCanvasSizeChanged(Size newSize)
        {
            CanvasWidth = newSize.Width;
            CanvasHeight = newSize.Height;
        }

        private void OnRegionLayoutChanged(RegionLayout layout)
        {
            CurrentRegionLayout = layout;
            EventAggregator?.GetEvent<RegionLayoutUpdatedEvent>()?.Publish(layout);
        }

        public void UpdateRegionLayout(RegionLayout newLayout)
        {
            CurrentRegionLayout = newLayout;
        }

        public (double Width, double Height) GetCanvasSize()
        {
            return (CanvasWidth, CanvasHeight);
        }

        #endregion
    }

    // 画布设备视图模型
    public class CanvasDeviceViewModel : BindableBase
    {
        private string _deviceId;
        private IDevice _device;
        private Point _position;
        private double _width;
        private double _height;
        private bool _isDragging;
        private bool _isSelected;

        public string DeviceId
        {
            get => _deviceId;
            set => SetProperty(ref _deviceId, value);
        }

        public IDevice Device
        {
            get => _device;
            set => SetProperty(ref _device, value);
        }

        public Point Position
        {
            get => _position;
            set => SetProperty(ref _position, value);
        }

        public double Width
        {
            get => _width;
            set => SetProperty(ref _width, value);
        }

        public double Height
        {
            get => _height;
            set => SetProperty(ref _height, value);
        }

        public bool IsDragging
        {
            get => _isDragging;
            set => SetProperty(ref _isDragging, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }
}