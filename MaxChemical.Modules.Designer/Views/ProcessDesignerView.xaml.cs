using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Modules.Designer.Services;
using MaxChemical.Modules.Designer.ViewModels;
using MaxChemical.Modules.Designer.Models;
using Prism.Events;
using System.Windows.Media.Imaging;
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Modules.Designer.Service;
using MaxChemical.Modules.Designer.Commands;
using System.Windows.Media.Animation;

namespace MaxChemical.Modules.Designer.Views
{
    /// <summary>
    /// 流程设计器主界面视图
    /// 负责处理设备拖拽、连接创建、画布交互等核心功能
    /// </summary>
    public partial class ProcessDesignerView : UserControl
    {
        #region 私有字段 依赖注入服务

        private readonly IEventAggregator _eventAggregator;
        private readonly IDragDropService _dragDropService;
        private readonly IRegionOverlayService _regionOverlayService;
        private readonly IConnectionService _connectionService;
        private readonly ICanvasDataService _canvasDataService;
        private readonly ICommandHistory _commandHistory;
        private ProcessDesignerViewModel _viewModel;
        // A. 设备显示名管理
        private readonly Dictionary<string, int> _deviceNameCounters = new();      // 基础名 -> 累计序号
        private readonly Dictionary<string, string> _deviceDisplayNames = new();    // DeviceId -> 显示名（泵#2）

        // B. 执行动画管理
        private readonly Dictionary<string, Storyboard> _runningAnimations = new();
        #endregion

        #region 私有字段 拖拽状态管理

        // 拖拽基本状态
        private bool _isInternalDragging = false;          // 是否正在进行内部拖拽
        private bool _isMouseDown = false;                 // 鼠标是否按下
        private bool _hasStartedDrag = false;              // 是否已开始拖拽
        private Point _dragStartPoint;                     // 拖拽起始点
        private Point _originalDevicePosition;             // 设备原始位置
        private FrameworkElement _mouseDownElement;        // 鼠标按下的元素
        private string _currentDraggingDeviceId = null;    // 当前被拖拽的设备ID
        private const double DragThreshold = 5.0;          // 拖拽触发阈值

        #endregion

        #region 私有字段 外部拖放防重复处理

        private bool _isProcessingDrop = false;            // 是否正在处理拖放
        private DateTime _lastDropTime = DateTime.MinValue; // 上次拖放时间
        private const int DropCooldownMs = 100;            // 拖放冷却时间毫秒

        #endregion

        #region 私有字段 键盘和界面状态

        private bool _isShiftPressed = false;              // Shift键是否按下
        private bool _isCtrlPressed = false;               // Ctrl键是否按下
        private string _selectedDeviceId;                  // 选中的设备ID
        private FrameworkElement _selectedDeviceElement;   // 选中的设备元素

        #endregion

        #region 私有字段 缩放相关

        private ScaleTransform _canvasScaleTransform;      // 画布缩放变换
        private ScrollViewer _canvasScrollViewer;          // 画布滚动视图

        #endregion

        #region 私有字段 设备属性弹窗相关

        private Window _devicePropertyWindow;
        // 监控卡片字典 - DeviceId -> MonitoringCard
        private readonly Dictionary<string, MonitoringCard> _monitoringCards = new();
        // 保存每个设备的监控参数选择状态 - DeviceId -> 参数名列表
        private readonly Dictionary<string, HashSet<string>> _deviceMonitoringSelections = new();
        #endregion
        #region 构造函数和初始化

        public ProcessDesignerView(
            IEventAggregator eventAggregator,
            IRegionOverlayService regionOverlayService,
            IDragDropService dragDropService,
            IConnectionService connectionService,
            ICanvasDataService canvasDataService,
            ICommandHistory commandHistory)
        {
            // 依赖注入验证
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _regionOverlayService = regionOverlayService ?? throw new ArgumentNullException(nameof(regionOverlayService));
            _dragDropService = dragDropService ?? throw new ArgumentNullException(nameof(dragDropService));
            _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
            _canvasDataService = canvasDataService ?? throw new ArgumentNullException(nameof(canvasDataService));
            _commandHistory = commandHistory ?? throw new ArgumentNullException(nameof(commandHistory));

            // 配置连接服务
            ConfigureConnectionService();

            // 初始化界面组件
            InitializeComponent();
            Debug.WriteLine("ProcessDesignerView 构造函数被调用");

            // 配置拖放功能
            ConfigureDragDropFeature();

            // 配置鼠标和键盘事件
            ConfigureMouseAndKeyboardEvents();

            // 配置生命周期事件
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>
        /// 配置连接服务的委托和事件
        /// </summary>
        private void ConfigureConnectionService()
        {
            _connectionService.GetCanvasFunc = () => DesignCanvas;
            _connectionService.FindDeviceElementFunc = FindDeviceElementInCanvas;
            _connectionService.ShowAllDeviceConnectionPointsAction = ShowAllDeviceConnectionPoints;
            _connectionService.ConnectionCreated += OnConnectionCreated;
            _connectionService.ConnectionDeleted += OnConnectionDeleted;
        }

        /// <summary>
        /// 配置拖放功能
        /// </summary>
        private void ConfigureDragDropFeature()
        {
            AllowDrop = true;
            DesignCanvas.AllowDrop = true;

            // 外部拖放事件 从设备库拖拽到画布
            DesignCanvas.DragOver += OnDragOver;
            DesignCanvas.Drop += OnDrop;
            DesignCanvas.DragEnter += OnDragEnter;
            DesignCanvas.DragLeave += OnDragLeave;
        }

        /// <summary>
        /// 配置鼠标和键盘事件
        /// </summary>
        private void ConfigureMouseAndKeyboardEvents()
        {
            // 画布鼠标事件 用于设备拖动和连接
            DesignCanvas.MouseDown += OnCanvasMouseDown;
            DesignCanvas.MouseMove += OnCanvasMouseMove;
            DesignCanvas.MouseUp += OnCanvasMouseUp;
            DesignCanvas.MouseLeave += OnCanvasMouseLeave;

            // 键盘事件
            this.KeyDown += OnKeyDown;
            this.KeyUp += OnKeyUp;
            this.Focusable = true;
        }

        #endregion

        #region 生命周期事件处理

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Debug.WriteLine("ProcessDesignerView 已加载到界面");

            if (DataContext is ProcessDesignerViewModel viewModel)
            {
                _viewModel = viewModel;
                SubscribeToViewModelEvents();
                SubscribeToZoomEvents();
                InitializeZoomComponents();
                SetRegionOverlayContext();
            }

            // 确保能接收键盘焦点
            this.Focus();

            // 订阅画布操作相关事件
            SubscribeToCanvasEvents();
            SubscribeToExecutionEvents();
            SubscribeToDeviceDisplayNameRequest();
        }
        private void SubscribeToExecutionEvents()
        {
            _eventAggregator?.GetEvent<DeviceExecutionStartedEvent>()?.Subscribe(OnDeviceExecutionStarted, Prism.Events.ThreadOption.UIThread);
            _eventAggregator?.GetEvent<DeviceExecutionEndedEvent>()?.Subscribe(OnDeviceExecutionEnded, Prism.Events.ThreadOption.UIThread);
            // 新增：订阅监控卡片更新事件
            _eventAggregator?.GetEvent<MonitoringCardUpdateEvent>()?.Subscribe(OnMonitoringCardUpdate, Prism.Events.ThreadOption.UIThread);
        }

        private void OnDeviceExecutionStarted(DeviceExecutionEventArgs e)
        {
            var element = FindDeviceElementInCanvas(e.DeviceId) as Border;
            if (element == null) return;

            // 如果已有动画，先停掉
            if (_runningAnimations.TryGetValue(e.DeviceId, out var prev))
            {
                prev.Stop();
                _runningAnimations.Remove(e.DeviceId);
            }

            var sb = CreatePulseStoryboard(element);
            _runningAnimations[e.DeviceId] = sb;
            sb.Begin();
        }

        private void OnDeviceExecutionEnded(DeviceExecutionEventArgs e)
        {
            if (_runningAnimations.TryGetValue(e.DeviceId, out var sb))
            {
                sb.Stop();
                _runningAnimations.Remove(e.DeviceId);
            }

            var element = FindDeviceElementInCanvas(e.DeviceId) as Border;
            if (element == null) return;

            // 成功/失败闪烁一次
            element.Effect = null;
            element.BorderThickness = new Thickness(2);
            element.BorderBrush = e.IsSuccessful ? Brushes.LimeGreen : Brushes.Red;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                element.BorderThickness = new Thickness(0);
                element.BorderBrush = Brushes.DarkBlue;
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
        /// <summary>
        /// 监控卡片更新事件处理
        /// </summary>
        private void OnMonitoringCardUpdate(MonitoringCardUpdateEventArgs e)
        {
            try
            {
                if (_monitoringCards.TryGetValue(e.DeviceId, out var card))
                {
                    // 更新卡片参数值
                    card.UpdateParameterValues(e.OutputParameters);
                    Debug.WriteLine($"更新监控卡片数据: 设备 {e.DeviceId}, 命令 {e.CommandName}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新监控卡片数据失败: {ex.Message}");
            }
        }
        private Storyboard CreatePulseStoryboard(Border element)
        {
            var brush = new SolidColorBrush(Colors.Orange);
            element.BorderBrush = brush;
            element.BorderThickness = new Thickness(2);

            var colorAnim = new ColorAnimation
            {
                From = Colors.Orange,
                To = Colors.DarkOrange,
                Duration = TimeSpan.FromMilliseconds(500),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(colorAnim, brush);
            Storyboard.SetTargetProperty(colorAnim, new PropertyPath(SolidColorBrush.ColorProperty));

            var effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Orange,
                BlurRadius = 10,
                ShadowDepth = 0,
                Opacity = 0.4
            };
            element.Effect = effect;

            var opacityAnim = new DoubleAnimation
            {
                From = 0.4,
                To = 0.9,
                Duration = TimeSpan.FromMilliseconds(500),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            Storyboard.SetTarget(opacityAnim, element);
            Storyboard.SetTargetProperty(opacityAnim, new PropertyPath("(UIElement.Effect).(DropShadowEffect.Opacity)"));

            var sb = new Storyboard();
            sb.Children.Add(colorAnim);
            sb.Children.Add(opacityAnim);
            return sb;
        }
        private void SubscribeToDeviceDisplayNameRequest()
        {
            _eventAggregator?.GetEvent<GetDeviceDisplayNamesRequestEvent>()?.Subscribe(OnGetDeviceDisplayNamesRequest);
        }

        private void OnGetDeviceDisplayNamesRequest(Action<Dictionary<string, string>> callback)
        {
            try
            {
                // 返回一份拷贝，避免外部修改
                callback?.Invoke(new Dictionary<string, string>(_deviceDisplayNames));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"返回设备显示名映射失败: {ex.Message}");
                callback?.Invoke(new Dictionary<string, string>());
            }
        }
        private string GetDisplayNameForNewDevice(IDevice device)
        {
            var baseName = string.IsNullOrWhiteSpace(device?.Name) ? "设备" : device.Name.Trim();
            if (!_deviceNameCounters.TryGetValue(baseName, out var idx)) idx = 0;
            idx++;
            _deviceNameCounters[baseName] = idx;
            return $"{baseName}#{idx}";
        }
        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            // 清理所有事件订阅
            UnsubscribeFromViewModelEvents();
            UnsubscribeFromCanvasEvents();
            UnsubscribeFromConnectionService();
            UnsubscribeFromKeyboardEvents();
        }

        /// <summary>
        /// 订阅ViewModel相关事件
        /// </summary>
        private void SubscribeToViewModelEvents()
        {
            if (_viewModel == null) return;

            _viewModel.CanvasDevices.CollectionChanged += OnCanvasDevicesCollectionChanged;
            _eventAggregator?.GetEvent<RegionLayoutUpdatedEvent>()?.Subscribe(OnRegionLayoutUpdated);
            _eventAggregator?.GetEvent<GetCanvasSizeRequestEvent>()?.Subscribe(OnGetCanvasSizeRequested);
            _eventAggregator?.GetEvent<DeleteSelectedDeviceEvent>()?.Subscribe(OnDeleteSelectedDevice);
            _eventAggregator?.GetEvent<DeleteSelectedConnectionsEvent>()?.Subscribe(OnDeleteSelectedConnections);
            _eventAggregator?.GetEvent<RequestCanvasDevicesEvent>()?.Subscribe(OnRequestCanvasDevices);
        }

        /// <summary>
        /// 取消订阅ViewModel相关事件
        /// </summary>
        private void UnsubscribeFromViewModelEvents()
        {
            if (_viewModel != null)
            {
                _viewModel.CanvasDevices.CollectionChanged -= OnCanvasDevicesCollectionChanged;
            }

            _eventAggregator?.GetEvent<RegionLayoutUpdatedEvent>()?.Unsubscribe(OnRegionLayoutUpdated);
            _eventAggregator?.GetEvent<GetCanvasSizeRequestEvent>()?.Unsubscribe(OnGetCanvasSizeRequested);
            _eventAggregator?.GetEvent<DeleteSelectedDeviceEvent>()?.Unsubscribe(OnDeleteSelectedDevice);
            _eventAggregator?.GetEvent<DeleteSelectedConnectionsEvent>()?.Unsubscribe(OnDeleteSelectedConnections);
        }

        /// <summary>
        /// 订阅画布操作相关事件
        /// </summary>
        private void SubscribeToCanvasEvents()
        {
            _eventAggregator?.GetEvent<ClearCanvasRequestEvent>()?.Subscribe(OnClearCanvasRequest);
            _eventAggregator?.GetEvent<GetCurrentCanvasDataRequestEvent>()?.Subscribe(OnGetCurrentCanvasDataRequest);
            _eventAggregator?.GetEvent<NewCanvasCreatedEvent>()?.Subscribe(OnNewCanvasCreated);
            _eventAggregator?.GetEvent<LoadCanvasRequestEvent>()?.Subscribe(OnLoadCanvasRequest);
            _eventAggregator?.GetEvent<CanvasLoadedEvent>()?.Subscribe(OnCanvasLoaded);


        }

        /// <summary>
        /// 取消订阅画布操作相关事件
        /// </summary>
        private void UnsubscribeFromCanvasEvents()
        {
            _eventAggregator?.GetEvent<ClearCanvasRequestEvent>()?.Unsubscribe(OnClearCanvasRequest);
            _eventAggregator?.GetEvent<GetCurrentCanvasDataRequestEvent>()?.Unsubscribe(OnGetCurrentCanvasDataRequest);
            _eventAggregator?.GetEvent<NewCanvasCreatedEvent>()?.Unsubscribe(OnNewCanvasCreated);
            _eventAggregator?.GetEvent<LoadCanvasRequestEvent>()?.Unsubscribe(OnLoadCanvasRequest);
            _eventAggregator?.GetEvent<CanvasLoadedEvent>()?.Unsubscribe(OnCanvasLoaded);
        }

        /// <summary>
        /// 订阅缩放相关事件
        /// </summary>
        private void SubscribeToZoomEvents()
        {
            _eventAggregator?.GetEvent<ZoomCanvasEvent>()?.Subscribe(OnZoomCanvas);
            _eventAggregator?.GetEvent<ZoomSelectedDeviceEvent>()?.Subscribe(OnZoomSelectedDevice);
            _eventAggregator?.GetEvent<FitCanvasToViewEvent>()?.Subscribe(OnFitCanvasToView);
            _eventAggregator?.GetEvent<GetSelectedDeviceRequestEvent>()?.Subscribe(OnGetSelectedDeviceRequest);
            _eventAggregator?.GetEvent<GetCanvasZoomRequestEvent>()?.Subscribe(OnGetCanvasZoomRequest);
        }

        /// <summary>
        /// 取消订阅连接服务事件
        /// </summary>
        private void UnsubscribeFromConnectionService()
        {
            if (_connectionService != null)
            {
                _connectionService.ConnectionCreated -= OnConnectionCreated;
                _connectionService.ConnectionDeleted -= OnConnectionDeleted;
            }
        }

        /// <summary>
        /// 取消订阅键盘事件
        /// </summary>
        private void UnsubscribeFromKeyboardEvents()
        {
            this.KeyDown -= OnKeyDown;
            this.KeyUp -= OnKeyUp;
        }

        #endregion

        #region 键盘事件处理

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                switch (e.Key)
                {
                    case Key.LeftShift:
                    case Key.RightShift:
                        HandleShiftKeyDown();
                        break;

                    case Key.LeftCtrl:
                    case Key.RightCtrl:
                        HandleCtrlKeyDown();
                        break;

                    case Key.Delete:
                        HandleDeleteKey();
                        e.Handled = true;
                        break;

                    case Key.Escape:
                        HandleEscapeKey();
                        break;

                    case Key.Z when _isCtrlPressed:
                        HandleUndoCommand();
                        e.Handled = true;
                        break;

                    case Key.Y when _isCtrlPressed:
                        HandleRedoCommand();
                        e.Handled = true;
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"键盘事件处理失败: {ex.Message}");
            }
        }

        private void OnKeyUp(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.LeftShift:
                case Key.RightShift:
                    HandleShiftKeyUp();
                    break;

                case Key.LeftCtrl:
                case Key.RightCtrl:
                    HandleCtrlKeyUp();
                    break;
            }
        }

        /// <summary>
        /// 处理Shift键按下 进入连接模式
        /// </summary>
        private void HandleShiftKeyDown()
        {
            _isShiftPressed = true;
            ShowConnectionPointsForSelectedDevices();
            Debug.WriteLine("Shift键按下，进入连接模式");
        }

        /// <summary>
        /// 处理Shift键释放 退出连接模式
        /// </summary>
        private void HandleShiftKeyUp()
        {
            _isShiftPressed = false;
            _connectionService.HideAllConnectionPoints();

            if (_connectionService.IsConnectionMode)
            {
                _connectionService.CancelConnection();
            }
            Debug.WriteLine("Shift键释放，退出连接模式");
        }

        /// <summary>
        /// 处理Ctrl键按下
        /// </summary>
        private void HandleCtrlKeyDown()
        {
            _isCtrlPressed = true;
            Debug.WriteLine("Ctrl键按下");
        }

        /// <summary>
        /// 处理Ctrl键释放
        /// </summary>
        private void HandleCtrlKeyUp()
        {
            _isCtrlPressed = false;
        }

        /// <summary>
        /// 处理Delete键 删除选中的设备或连接
        /// </summary>
        private void HandleDeleteKey()
        {
            Debug.WriteLine("Delete键按下");

            if (!string.IsNullOrEmpty(_selectedDeviceId))
            {
                // 有选中设备时删除设备
                Debug.WriteLine($"通过Delete键删除选中设备: {_selectedDeviceId}");
                OnDeleteSelectedDevice(_selectedDeviceId);
            }
            else
            {
                // 没有选中设备时删除选中的连接
                Debug.WriteLine("通过Delete键删除选中的连接线");
                OnDeleteSelectedConnections();
            }
        }

        /// <summary>
        /// 处理Escape键 取消连接模式
        /// </summary>
        private void HandleEscapeKey()
        {
            if (_connectionService.IsConnectionMode)
            {
                _connectionService.CancelConnection();
                Debug.WriteLine("通过Escape键取消连接模式");
            }
        }

        /// <summary>
        /// 处理撤销命令 Ctrl+Z
        /// </summary>
        private void HandleUndoCommand()
        {
            if (_commandHistory.CanUndo)
            {
                Debug.WriteLine("通过Ctrl+Z执行撤销");
                _commandHistory.Undo();
            }
        }

        /// <summary>
        /// 处理重做命令 Ctrl+Y
        /// </summary>
        private void HandleRedoCommand()
        {
            if (_commandHistory.CanRedo)
            {
                Debug.WriteLine("通过Ctrl+Y执行重做");
                _commandHistory.Redo();
            }
        }

        #endregion

        #region 连接相关事件处理

        /// <summary>
        /// 连接创建事件处理
        /// </summary>
        private void OnConnectionCreated(ConnectionInfo connection)
        {
            // 检查连接是否已存在避免重复添加
            if (_viewModel?.Connections.Any(c => c.Id == connection.Id) == true)
            {
                Debug.WriteLine($"连接 {connection.Id} 已存在于 ViewModel，跳过添加");
                return;
            }

            // 使用命令模式添加连接支持撤销重做
            var createConnectionCommand = new CreateConnectionCommand(_viewModel, _connectionService, connection);
            _commandHistory.ExecuteCommand(createConnectionCommand);

            Debug.WriteLine($"通过命令创建连接: {connection.Id}");
        }

        /// <summary>
        /// 连接删除事件处理
        /// </summary>
        private void OnConnectionDeleted(string connectionId)
        {
            var connection = _viewModel?.Connections.FirstOrDefault(c => c.Id == connectionId);
            if (connection != null)
            {
                _viewModel.Connections.Remove(connection);
                Debug.WriteLine($"连接已从ViewModel移除: {connectionId}");

                // 标记画布已修改
                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

                // 更新区域覆盖上下文
                UpdateRegionOverlayContext();
            }
        }

        /// <summary>
        /// 为选中设备显示连接点
        /// </summary>
        private void ShowConnectionPointsForSelectedDevices()
        {
            if (!_isShiftPressed) return;
            ShowAllDeviceConnectionPoints();
        }

        /// <summary>
        /// 显示所有设备的连接点
        /// </summary>
        private void ShowAllDeviceConnectionPoints()
        {
            if (_viewModel?.CanvasDevices == null) return;

            foreach (var canvasDevice in _viewModel.CanvasDevices)
            {
                _connectionService?.ShowConnectionPoints(canvasDevice.DeviceId);
            }

            Debug.WriteLine($"已显示 {_viewModel.CanvasDevices.Count} 个设备的连接点");
        }

        #endregion

        #region 缩放功能相关

        /// <summary>
        /// 初始化缩放组件
        /// </summary>
        private void InitializeZoomComponents()
        {
            // 获取或创建画布的缩放变换
            if (DesignCanvas.RenderTransform is ScaleTransform existingTransform)
            {
                _canvasScaleTransform = existingTransform;
            }
            else
            {
                _canvasScaleTransform = new ScaleTransform(1.0, 1.0);
                DesignCanvas.RenderTransform = _canvasScaleTransform;
            }

            // 查找父级ScrollViewer
            _canvasScrollViewer = FindParent<ScrollViewer>(this);

            Debug.WriteLine("缩放组件初始化完成");
        }

        /// <summary>
        /// 缩放画布到指定级别
        /// </summary>
        private void OnZoomCanvas(double zoomLevel)
        {
            try
            {
                Debug.WriteLine($"缩放画布到: {zoomLevel}");

                if (_canvasScaleTransform != null)
                {
                    _canvasScaleTransform.ScaleX = zoomLevel;
                    _canvasScaleTransform.ScaleY = zoomLevel;

                    // 如果有ScrollViewer则调整视图中心
                    if (_canvasScrollViewer != null)
                    {
                        var centerX = _canvasScrollViewer.ViewportWidth / 2;
                        var centerY = _canvasScrollViewer.ViewportHeight / 2;

                        _canvasScrollViewer.ScrollToHorizontalOffset(centerX * zoomLevel - centerX);
                        _canvasScrollViewer.ScrollToVerticalOffset(centerY * zoomLevel - centerY);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"缩放画布失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 缩放选中的设备
        /// </summary>
        private void OnZoomSelectedDevice(double scaleFactor)
        {
            try
            {
                if (string.IsNullOrEmpty(_selectedDeviceId))
                {
                    Debug.WriteLine("没有选中的设备可以缩放");
                    return;
                }

                var deviceElement = FindDeviceElementInCanvas(_selectedDeviceId);
                if (deviceElement == null)
                {
                    Debug.WriteLine($"找不到设备元素: {_selectedDeviceId}");
                    return;
                }

                // 获取当前缩放信息
                var (oldScaleX, oldScaleY) = GetDeviceScaleInfo(_selectedDeviceId);

                // 计算新的缩放值并限制范围
                var newScaleX = Math.Max(0.5, Math.Min(oldScaleX * scaleFactor, 2.0));
                var newScaleY = Math.Max(0.5, Math.Min(oldScaleY * scaleFactor, 2.0));

                Debug.WriteLine($"缩放计算: 原始({oldScaleX:F2}x{oldScaleY:F2}) -> 新值({newScaleX:F2}x{newScaleY:F2})");

                // 检查是否需要缩放
                if (Math.Abs(newScaleX - oldScaleX) > 0.01 || Math.Abs(newScaleY - oldScaleY) > 0.01)
                {
                    var deviceName = _viewModel?.CanvasDevices.FirstOrDefault(d => d.DeviceId == _selectedDeviceId)?.Device?.Name ?? "设备";

                    // 使用命令记录缩放操作
                    var scaleCommand = new ScaleDeviceCommand(
                        _selectedDeviceId,
                        oldScaleX, oldScaleY,
                        newScaleX, newScaleY,
                        ApplyDeviceScale,
                        deviceName);

                    _commandHistory.ExecuteCommand(scaleCommand);

                    Debug.WriteLine($"设备 {_selectedDeviceId} 缩放命令已执行");

                    // 标记画布已修改
                    _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();
                }
                else
                {
                    Debug.WriteLine("缩放值变化太小，跳过操作");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"缩放设备失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 应用设备缩放变换
        /// </summary>
        private void ApplyDeviceScale(string deviceId, double scaleX, double scaleY)
        {
            try
            {
                var deviceElement = FindDeviceElementInCanvas(deviceId);
                if (deviceElement == null)
                {
                    Debug.WriteLine($"ApplyDeviceScale: 未找到设备元素 {deviceId}");
                    return;
                }

                Debug.WriteLine($"ApplyDeviceScale: 应用缩放 {scaleX:F2}x{scaleY:F2} 到设备 {deviceId}");

                // 获取或创建变换组
                TransformGroup transformGroup = GetOrCreateTransformGroup(deviceElement);

                // 获取或创建缩放变换
                ScaleTransform deviceScale = GetOrCreateScaleTransform(transformGroup);

                // 应用缩放
                deviceScale.ScaleX = scaleX;
                deviceScale.ScaleY = scaleY;
                deviceElement.RenderTransformOrigin = new Point(0.5, 0.5);

                // 强制更新布局
                deviceElement.UpdateLayout();

                Debug.WriteLine($"ApplyDeviceScale: 设备 {deviceId} 缩放应用完成: {scaleX:F2}x{scaleY:F2}");

                // 延迟更新连接线和连接点
                UpdateDeviceConnectionsAfterTransform(deviceId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ApplyDeviceScale 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取或创建变换组
        /// </summary>
        private TransformGroup GetOrCreateTransformGroup(FrameworkElement element)
        {
            if (element.RenderTransform is TransformGroup existingGroup)
            {
                return existingGroup;
            }

            var transformGroup = new TransformGroup();
            if (element.RenderTransform != null && element.RenderTransform != Transform.Identity)
            {
                transformGroup.Children.Add(element.RenderTransform);
            }
            element.RenderTransform = transformGroup;
            return transformGroup;
        }

        /// <summary>
        /// 获取或创建缩放变换
        /// </summary>
        private ScaleTransform GetOrCreateScaleTransform(TransformGroup transformGroup)
        {
            var scaleTransform = transformGroup.Children.OfType<ScaleTransform>().FirstOrDefault();
            if (scaleTransform == null)
            {
                scaleTransform = new ScaleTransform(1.0, 1.0);
                transformGroup.Children.Add(scaleTransform);
            }
            return scaleTransform;
        }

        /// <summary>
        /// 变换后更新设备连接
        /// </summary>
        private void UpdateDeviceConnectionsAfterTransform(string deviceId)
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    UpdateConnections(deviceId);
                    UpdateDeviceConnectionPoints(deviceId);
                    Debug.WriteLine($"设备 {deviceId} 连接更新完成");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"更新连接失败: {ex.Message}");
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// 画布自适应视图
        /// </summary>
        private void OnFitCanvasToView()
        {
            try
            {
                Debug.WriteLine("执行画布自适应");

                if (_canvasScrollViewer == null)
                {
                    Debug.WriteLine("没有找到 ScrollViewer，使用默认缩放");
                    OnZoomCanvas(1.0);
                    return;
                }

                // 计算合适的缩放比例
                var viewportWidth = _canvasScrollViewer.ViewportWidth;
                var viewportHeight = _canvasScrollViewer.ViewportHeight;
                var canvasWidth = DesignCanvas.ActualWidth > 0 ? DesignCanvas.ActualWidth : 1200;
                var canvasHeight = DesignCanvas.ActualHeight > 0 ? DesignCanvas.ActualHeight : 800;

                var scaleX = viewportWidth / canvasWidth;
                var scaleY = viewportHeight / canvasHeight;
                var scale = Math.Min(scaleX, scaleY) * 0.9; // 留边距

                // 限制缩放范围
                scale = Math.Max(0.1, Math.Min(scale, 2.0));

                Debug.WriteLine($"自适应缩放比例: {scale:F2}");
                OnZoomCanvas(scale);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"画布自适应失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 响应获取选中设备请求
        /// </summary>
        private void OnGetSelectedDeviceRequest(Action<string> callback)
        {
            callback?.Invoke(_selectedDeviceId);
        }

        /// <summary>
        /// 响应获取画布缩放级别请求
        /// </summary>
        private void OnGetCanvasZoomRequest(Action<double> callback)
        {
            var zoom = _canvasScaleTransform?.ScaleX ?? 1.0;
            callback?.Invoke(zoom);
        }

        #endregion

        #region 设备选择管理

        /// <summary>
        /// 设备选择处理
        /// </summary>
        private void OnDeviceSelected(string deviceId)
        {
            // 清除之前的选择
            ClearDeviceSelection();

            _selectedDeviceId = deviceId;
            _selectedDeviceElement = FindDeviceElementInCanvas(deviceId);

            // 添加选中的视觉反馈
            if (_selectedDeviceElement is Border border)
            {
                border.BorderBrush = Brushes.Orange;
                border.BorderThickness = new Thickness(2);

                // 添加发光效果
                border.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Orange,
                    BlurRadius = 8,
                    ShadowDepth = 0,
                    Opacity = 0.6
                };
            }

            Debug.WriteLine($"设备已选中: {deviceId}");
        }

        /// <summary>
        /// 取消设备选择
        /// </summary>
        private void OnDeviceDeselected()
        {
            ClearDeviceSelection();
            Debug.WriteLine("设备选择已取消");
        }

        /// <summary>
        /// 清除设备选择状态
        /// </summary>
        private void ClearDeviceSelection()
        {
            if (_selectedDeviceElement is Border border)
            {
                border.BorderBrush = Brushes.DarkBlue;
                border.BorderThickness = new Thickness(0);
                border.Effect = null;
            }

            _selectedDeviceId = null;
            _selectedDeviceElement = null;
        }

        #endregion

        #region 拖拽状态管理

        /// <summary>
        /// 完整清理拖拽状态
        /// </summary>
        private void CleanupDragState()
        {
            try
            {
                Debug.WriteLine("开始清理拖拽状态");

                // 释放鼠标捕获
                ReleaseMouseCaptures();

                // 移除视觉反馈
                if (_mouseDownElement != null)
                {
                    RemoveDragVisualFeedback(_mouseDownElement);
                }

                // 重置所有状态变量
                ResetDragStateVariables();

                // 恢复画布状态
                DesignCanvas.AllowDrop = true;
                _connectionService?.SetDragMode(false);

                Debug.WriteLine("拖拽状态清理完成");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清理拖拽状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 释放所有鼠标捕获
        /// </summary>
        private void ReleaseMou​seCaptures()
        {
            if (DesignCanvas.IsMouseCaptured)
            {
                DesignCanvas.ReleaseMouseCapture();
                Debug.WriteLine("释放画布鼠标捕获");
            }

            if (_mouseDownElement?.IsMouseCaptured == true)
            {
                _mouseDownElement.ReleaseMouseCapture();
                Debug.WriteLine("释放设备元素鼠标捕获");
            }
        }

        /// <summary>
        /// 重置拖拽状态变量
        /// </summary>
        private void ResetDragStateVariables()
        {
            _isInternalDragging = false;
            _isMouseDown = false;
            _hasStartedDrag = false;
            _mouseDownElement = null;
            _currentDraggingDeviceId = null;
            _originalDevicePosition = new Point();
            _dragStartPoint = new Point();
        }

        /// <summary>
        /// 标准的重置拖拽状态方法
        /// </summary>
        private void ResetDragState()
        {
            CleanupDragState();
        }

        #endregion

        #region 删除操作处理

        /// <summary>
        /// 删除选中设备
        /// </summary>
        private void OnDeleteSelectedDevice(string deviceId)
        {
            try
            {
                Debug.WriteLine($"开始删除设备: {deviceId}");

                if (string.IsNullOrEmpty(deviceId))
                {
                    Debug.WriteLine("设备ID为空，取消删除");
                    return;
                }

                // 如果设备正在被拖拽先清理状态
                if (_currentDraggingDeviceId == deviceId)
                {
                    Debug.WriteLine($"设备 {deviceId} 正被拖拽，清理拖拽状态");
                    CleanupDragState();
                }

                // 查找要删除的设备
                var deviceToDelete = _viewModel?.CanvasDevices.FirstOrDefault(d => d.DeviceId == deviceId);
                if (deviceToDelete == null)
                {
                    Debug.WriteLine($"未找到要删除的设备: {deviceId}");
                    return;
                }

                var deviceName = deviceToDelete.Device?.Name ?? "未知设备";
                Debug.WriteLine($"找到要删除的设备: {deviceName}");

                // 清除监控参数选择状态
                ClearDeviceMonitoringSelections(deviceId);

                // 移除监控卡片
                RemoveMonitoringCard(deviceId);

                // 使用命令删除设备支持撤销重做
                var deleteCommand = new DeleteDeviceCommand(_viewModel, deviceToDelete, _connectionService);
                _commandHistory.ExecuteCommand(deleteCommand);

                // 清除选择状态
                if (deviceId == _selectedDeviceId)
                {
                    OnDeviceDeselected();
                }

                // 移除视觉元素
                RemoveDeviceVisual(deviceId);

                // 确保状态清理
                CleanupDragState();

                // 标记画布已修改
                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

                // 发布删除完成事件
                PublishDeviceDeletedEvent(deviceId, deviceName);

                Debug.WriteLine($"设备删除完成: {deviceName}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"删除设备失败: {ex.Message}");
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"删除设备失败: {ex.Message}");
                CleanupDragState();
            }
        }

        /// <summary>
        /// 发布设备删除事件
        /// </summary>
        private void PublishDeviceDeletedEvent(string deviceId, string deviceName)
        {
            _eventAggregator?.GetEvent<DeviceDeletedEvent>()?.Publish(new DeviceDeletedEventArgs
            {
                DeviceId = deviceId,
                DeviceName = deviceName,
                DeletedConnectionsCount = 0 // 命令中处理
            });
        }

        /// <summary>
        /// 删除选中的连接线
        /// </summary>
        private void OnDeleteSelectedConnections()
        {
            try
            {
                Debug.WriteLine("删除选中的连接线");

                if (_connectionService?.Connections == null)
                {
                    Debug.WriteLine("连接服务无效");
                    return;
                }

                // 查找选中的连接
                var selectedConnections = _connectionService.Connections.Values
                    .Where(c => c.IsSelected)
                    .ToList();

                if (selectedConnections.Count == 0)
                {
                    Debug.WriteLine("没有选中的连接线");
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("没有选中的连接线");
                    return;
                }

                Debug.WriteLine($"找到 {selectedConnections.Count} 个选中的连接线");

                // 为每个连接创建删除命令
                foreach (var connection in selectedConnections)
                {
                    try
                    {
                        var deleteConnectionCommand = new DeleteConnectionCommand(_viewModel, _connectionService, connection);
                        _commandHistory.ExecuteCommand(deleteConnectionCommand);
                        Debug.WriteLine($"通过命令删除连接: {connection.Id}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"删除连接失败: {connection.Id}, 错误: {ex.Message}");
                    }
                }

                // 标记画布已修改
                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"已删除 {selectedConnections.Count} 个连接线");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"删除连接线失败: {ex.Message}");
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"删除连接线失败: {ex.Message}");
            }
        }
        private void OnRequestCanvasDevices(System.Action<List<CanvasDeviceViewModel>> callback)
        {
            try
            {
                var canvasDevicesList = _viewModel?.CanvasDevices?.ToList() ?? new List<CanvasDeviceViewModel>();
                Debug.WriteLine($"响应画布设备请求，返回 {canvasDevicesList.Count} 个设备");
                callback?.Invoke(canvasDevicesList);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"响应画布设备请求失败: {ex.Message}");
                callback?.Invoke(new List<CanvasDeviceViewModel>());
            }
        }

        #endregion

        #region 外部拖放事件处理 从设备库到画布

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            Debug.WriteLine($"外部拖拽进入: 内部拖拽={_isInternalDragging}, 当前拖拽设备={_currentDraggingDeviceId}");

            // 如果正在进行内部拖拽则忽略外部拖放
            if (_isInternalDragging || !string.IsNullOrEmpty(_currentDraggingDeviceId))
            {
                Debug.WriteLine("忽略外部拖拽，内部拖拽进行中");
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            // 处理设备拖拽
            if (e.Data.GetDataPresent("Device"))
            {
                var device = e.Data.GetData("Device") as IDevice;
                var position = e.GetPosition(DesignCanvas);
                Debug.WriteLine($"外部设备拖拽进入: {device?.Name}");

                // 检查设备是否可以放置在当前区域
                if (IsValidRegionForDevice(device, position))
                {
                    e.Effects = DragDropEffects.Copy;
                    DesignCanvas.Background = new SolidColorBrush(Color.FromArgb(20, 0, 120, 212));
                }
                else
                {
                    e.Effects = DragDropEffects.None;
                    DesignCanvas.Background = new SolidColorBrush(Color.FromArgb(20, 255, 0, 0));
                }
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }

            e.Handled = true;
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            // 如果正在进行内部拖拽则拒绝外部拖放
            if (_isInternalDragging || !string.IsNullOrEmpty(_currentDraggingDeviceId))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            // 实时检查拖拽位置是否有效
            if (e.Data.GetDataPresent("Device"))
            {
                var device = e.Data.GetData("Device") as IDevice;
                var position = e.GetPosition(DesignCanvas);

                if (IsValidRegionForDevice(device, position))
                {
                    e.Effects = DragDropEffects.Copy;
                    DesignCanvas.Background = new SolidColorBrush(Color.FromArgb(20, 0, 120, 212));
                }
                else
                {
                    e.Effects = DragDropEffects.None;
                    DesignCanvas.Background = new SolidColorBrush(Color.FromArgb(20, 255, 0, 0));
                }
            }

            e.Handled = true;
        }

        private void OnDragLeave(object sender, DragEventArgs e)
        {
            Debug.WriteLine($"外部拖拽离开: 内部拖拽={_isInternalDragging}");

            // 如果正在进行内部拖拽则忽略
            if (_isInternalDragging || !string.IsNullOrEmpty(_currentDraggingDeviceId))
            {
                Debug.WriteLine("忽略外部拖拽离开，内部拖拽进行中");
                e.Handled = true;
                return;
            }

            // 恢复画布背景
            DesignCanvas.Background = Brushes.White;

            if (e.Data.GetDataPresent("Device"))
            {
                var device = e.Data.GetData("Device") as IDevice;
                Debug.WriteLine($"外部设备拖拽离开: {device?.Name}");
            }

            e.Handled = true;
        }

        private void OnDrop(object sender, DragEventArgs e)
        {
            Debug.WriteLine($"外部拖放: 内部拖拽={_isInternalDragging}, 正在处理={_isProcessingDrop}");

            // 恢复画布背景
            DesignCanvas.Background = Brushes.White;

            // 检查是否应该忽略此拖放事件
            if (ShouldIgnoreDropEvent())
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            // 验证拖放数据
            if (!ValidateDropData(e, out var device, out var dropPosition))
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            // 处理设备拖放
            ProcessDeviceDrop(device, dropPosition);
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        /// <summary>
        /// 检查是否应该忽略拖放事件
        /// </summary>
        private bool ShouldIgnoreDropEvent()
        {
            // 内部拖拽中
            if (_isInternalDragging || !string.IsNullOrEmpty(_currentDraggingDeviceId))
            {
                Debug.WriteLine("忽略外部拖放，内部拖拽进行中");
                return true;
            }

            // 防重复处理
            var currentTime = DateTime.Now;
            if (_isProcessingDrop || (currentTime - _lastDropTime).TotalMilliseconds < DropCooldownMs)
            {
                Debug.WriteLine("忽略重复的拖放事件");
                return true;
            }

            return false;
        }

        /// <summary>
        /// 验证拖放数据
        /// </summary>
        private bool ValidateDropData(DragEventArgs e, out IDevice device, out Point dropPosition)
        {
            device = null;
            dropPosition = new Point();

            if (!e.Data.GetDataPresent("Device"))
            {
                Debug.WriteLine("拖放事件中没有设备数据");
                return false;
            }

            device = e.Data.GetData("Device") as IDevice;
            if (device == null)
            {
                Debug.WriteLine("设备数据为空");
                return false;
            }

            dropPosition = e.GetPosition(DesignCanvas);
            if (!IsValidRegionForDevice(device, dropPosition))
            {
                Debug.WriteLine($"设备 {device.Name} 不能放置在当前区域");
                return false;
            }

            return true;
        }

        /// <summary>
        /// 处理设备拖放
        /// </summary>
        private void ProcessDeviceDrop(IDevice device, Point dropPosition)
        {
            _isProcessingDrop = true;
            _lastDropTime = DateTime.Now;

            try
            {
                Debug.WriteLine($"从设备库放置设备: {device.Name} 在位置 ({dropPosition.X:F0}, {dropPosition.Y:F0})");
                CreateDeviceFromLibraryOnly(device, dropPosition);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理拖放事件失败: {ex.Message}");
            }
            finally
            {
                // 延迟重置处理标志
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _isProcessingDrop = false;
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        /// <summary>
        /// 检查设备是否可以放置在指定区域
        /// </summary>
        private bool IsValidRegionForDevice(IDevice device, Point position)
        {
            if (device == null || _viewModel?.CurrentRegionLayout == null)
                return false;

            var allowedRegions = device.AllowedRegions;
            if (allowedRegions == null || !allowedRegions.Any())
                return true;

            var targetRegion = FindRegionAtPosition(position);
            if (targetRegion == null)
                return false;

            bool isAllowed = allowedRegions.Contains(targetRegion.Type);
            Debug.WriteLine($"设备 {device.Name} 区域检查: 允许={string.Join(", ", allowedRegions)}, 目标={targetRegion.Type}, 结果={isAllowed}");

            return isAllowed;
        }

        /// <summary>
        /// 查找指定位置的区域
        /// </summary>
        private IndependentRegion FindRegionAtPosition(Point position)
        {
            if (_viewModel?.CurrentRegionLayout?.UseIndependentRegions != true)
                return null;

            return _viewModel.CurrentRegionLayout.IndependentRegions
                .FirstOrDefault(region => region.Contains(position));
        }

        /// <summary>
        /// 从设备库创建设备到画布
        /// </summary>
        private void CreateDeviceFromLibraryOnly(IDevice device, Point position)
        {
            try
            {
                if (_viewModel == null || device == null)
                {
                    Debug.WriteLine("CreateDeviceFromLibraryOnly: ViewModel或设备为空");
                    return;
                }

                Debug.WriteLine($"开始创建设备: {device.Name} 在位置 ({position.X:F1}, {position.Y:F1})");

                // 检查位置是否已有设备
                if (IsPositionOccupied(position))
                {
                    Debug.WriteLine("位置已被占用，忽略创建");
                    return;
                }

                // 创建设备视图模型
                var canvasDevice = CreateCanvasDeviceViewModel(device, position);

                // 使用命令创建设备支持撤销重做
                var addCommand = new AddDeviceCommand(_viewModel, canvasDevice, _connectionService);
                _commandHistory.ExecuteCommand(addCommand);

                Debug.WriteLine($"设备创建完成: {device.Name}, ID: {canvasDevice.DeviceId}");

                // 标记画布已修改
                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"创建设备失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查位置是否被占用
        /// </summary>
        private bool IsPositionOccupied(Point position)
        {
            return _viewModel.CanvasDevices.Any(d =>
                Math.Abs(d.Position.X - position.X) < 10 &&
                Math.Abs(d.Position.Y - position.Y) < 10);
        }

        /// <summary>
        /// 创建画布设备视图模型
        /// </summary>
        private CanvasDeviceViewModel CreateCanvasDeviceViewModel(IDevice device, Point position)
        {
            try
            {
                Debug.WriteLine($"创建设备视图模型: {device.Name} 在位置 ({position.X:F1}, {position.Y:F1})");

                // *** 关键修改：使用JSON深拷贝创建完全独立的设备实例 ***
                var independentDevice = ReflectionDeviceDeepCopyService.DeepClone(device);

                Debug.WriteLine($"设备深拷贝完成:");
                Debug.WriteLine($"  原始设备哈希: {device.GetHashCode()}");
                Debug.WriteLine($"  拷贝设备哈希: {independentDevice.GetHashCode()}");
                Debug.WriteLine($"  原始参数哈希: {device.Parameters?.GetHashCode() ?? 0}");
                Debug.WriteLine($"  拷贝参数哈希: {independentDevice.Parameters?.GetHashCode() ?? 0}");

                var canvasDevice = new CanvasDeviceViewModel
                {
                    DeviceId = Guid.NewGuid().ToString(),
                    Device = independentDevice, // ✅ 使用深拷贝的独立设备实例
                    Position = position,
                    Width = 80,
                    Height = 60
                };

                Debug.WriteLine($"设备视图模型创建完成: ID={canvasDevice.DeviceId}");

                return canvasDevice;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"创建设备视图模型失败: {ex.Message}");

                // 记录详细错误信息
                Debug.WriteLine($"设备类型: {device?.GetType()?.Name}");
                Debug.WriteLine($"设备名称: {device?.Name}");
                Debug.WriteLine($"异常详情: {ex}");

                return null;
            }
        }

        #endregion

        #region 画布鼠标事件处理 设备拖拽和连接

        private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                // 确保开始新操作前状态清洁
                if (_isInternalDragging || _isMouseDown)
                {
                    Debug.WriteLine("检测到异常拖拽状态，先清理");
                    CleanupDragState();
                }

                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    var hitElement = e.OriginalSource as FrameworkElement;
                    var deviceControl = FindDeviceControl(hitElement);

                    if (deviceControl?.Tag is string deviceId)
                    {
                        HandleDeviceMouseDown(deviceControl, deviceId, e);
                    }
                    else
                    {
                        HandleCanvasEmptyAreaClick();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"画布鼠标按下处理失败: {ex.Message}");
                CleanupDragState();
            }
        }

        /// <summary>
        /// 处理设备上的鼠标按下
        /// </summary>
        private void HandleDeviceMouseDown(FrameworkElement deviceControl, string deviceId, MouseButtonEventArgs e)
        {
            // 验证设备是否存在
            if (!IsDeviceExistInViewModel(deviceId))
            {
                Debug.WriteLine($"设备 {deviceId} 不存在，忽略鼠标事件");
                e.Handled = true;
                return;
            }

            // 处理连接模式点击
            if (_isShiftPressed)
            {
                HandleConnectionClick(deviceId, e.GetPosition(deviceControl));
                e.Handled = true;
                return;
            }

            // 准备拖拽
            PrepareDragOperation(deviceControl, deviceId, e);
            e.Handled = true;
        }

        /// <summary>
        /// 验证设备是否存在于ViewModel中
        /// </summary>
        private bool IsDeviceExistInViewModel(string deviceId)
        {
            return _viewModel?.CanvasDevices.Any(d => d.DeviceId == deviceId) == true;
        }

        /// <summary>
        /// 准备拖拽操作
        /// </summary>
        private void PrepareDragOperation(FrameworkElement deviceControl, string deviceId, MouseButtonEventArgs e)
        {
            _isMouseDown = true;
            _mouseDownElement = deviceControl;
            _currentDraggingDeviceId = deviceId;
            _dragStartPoint = e.GetPosition(DesignCanvas);
            _hasStartedDrag = false;

            // 记录原始位置
            var left = Canvas.GetLeft(deviceControl);
            var top = Canvas.GetTop(deviceControl);
            _originalDevicePosition = new Point(
                double.IsNaN(left) ? 0 : left,
                double.IsNaN(top) ? 0 : top
            );

            Debug.WriteLine($"准备拖拽设备: {deviceId}, 原始位置: ({_originalDevicePosition.X:F0}, {_originalDevicePosition.Y:F0})");
        }

        /// <summary>
        /// 处理画布空白区域点击
        /// </summary>
        private void HandleCanvasEmptyAreaClick()
        {
            OnDeviceDeselected();
            CleanupDragState();
            this.Focus();
        }

        /// <summary>
        /// 处理连接点击
        /// </summary>
        private void HandleConnectionClick(string deviceId, Point clickPosition)
        {
            var deviceElement = FindDeviceElement(deviceId);
            if (deviceElement == null) return;

            var direction = DetermineConnectionDirection(deviceElement, clickPosition);

            if (_connectionService.IsConnectionMode)
            {
                _connectionService.CompleteConnection(deviceId, direction);
            }
            else
            {
                _connectionService.StartConnection(deviceId, direction);
            }
        }

        /// <summary>
        /// 确定连接方向
        /// </summary>
        private ConnectionDirection DetermineConnectionDirection(FrameworkElement device, Point clickPosition)
        {
            double width = device.ActualWidth;
            double height = device.ActualHeight;

            double relativeX = clickPosition.X / width;
            double relativeY = clickPosition.Y / height;

            if (relativeX < 0.25) return ConnectionDirection.Left;
            if (relativeX > 0.75) return ConnectionDirection.Right;
            if (relativeY < 0.25) return ConnectionDirection.Top;
            return ConnectionDirection.Bottom;
        }

        private void OnCanvasMouseMove(object sender, MouseEventArgs e)
        {
            // 优先处理连接模式的鼠标移动
            if (_connectionService?.IsConnectionMode == true)
            {
                var mousePosition = e.GetPosition(DesignCanvas);
                _connectionService.UpdateMousePosition(mousePosition);
                e.Handled = true;
                return;
            }

            // 处理设备拖拽
            if (_isMouseDown && _mouseDownElement != null && e.LeftButton == MouseButtonState.Pressed)
            {
                ProcessDeviceDragging(e);
            }
        }

        /// <summary>
        /// 处理设备拖拽过程
        /// </summary>
        private void ProcessDeviceDragging(MouseEventArgs e)
        {
            Point currentPoint = e.GetPosition(DesignCanvas);

            // 检查是否达到拖拽阈值
            if (!_hasStartedDrag)
            {
                if (ShouldStartDragging(currentPoint))
                {
                    StartDragging();
                }
                return;
            }

            // 继续拖拽
            ContinueDragging(currentPoint);
            e.Handled = true;
        }

        /// <summary>
        /// 检查是否应该开始拖拽
        /// </summary>
        private bool ShouldStartDragging(Point currentPoint)
        {
            double dx = currentPoint.X - _dragStartPoint.X;
            double dy = currentPoint.Y - _dragStartPoint.Y;
            return Math.Sqrt(dx * dx + dy * dy) > DragThreshold;
        }

        /// <summary>
        /// 开始拖拽
        /// </summary>
        private void StartDragging()
        {
            _hasStartedDrag = true;
            _isInternalDragging = true;

            _connectionService.SetDragMode(true);
            DesignCanvas.AllowDrop = false;
            DesignCanvas.CaptureMouse();
            AddDragVisualFeedback(_mouseDownElement);

            Debug.WriteLine($"开始拖拽设备: {_currentDraggingDeviceId}");
        }

        /// <summary>
        /// 继续拖拽
        /// </summary>
        private void ContinueDragging(Point currentPoint)
        {
            // 计算新位置
            double dx = currentPoint.X - _dragStartPoint.X;
            double dy = currentPoint.Y - _dragStartPoint.Y;

            double origLeft = Canvas.GetLeft(_mouseDownElement);
            double origTop = Canvas.GetTop(_mouseDownElement);

            double newLeft = origLeft + dx;
            double newTop = origTop + dy;

            // 应用边界限制
            var (boundedLeft, boundedTop) = ApplyBoundaryConstraints(newLeft, newTop);

            // 检查区域有效性
            if (!IsValidDragPosition(boundedLeft, boundedTop))
            {
                UpdateDragVisualFeedback(_mouseDownElement, false);
                return;
            }

            // 移动设备
            Canvas.SetLeft(_mouseDownElement, boundedLeft);
            Canvas.SetTop(_mouseDownElement, boundedTop);
            _mouseDownElement.Opacity = 0.8;

            // 更新连接线
            ForceUpdateConnections(_currentDraggingDeviceId);

            UpdateDragVisualFeedback(_mouseDownElement, true);
            _dragStartPoint = currentPoint;
        }

        /// <summary>
        /// 应用边界约束
        /// </summary>
        private (double left, double top) ApplyBoundaryConstraints(double left, double top)
        {
            var deviceWidth = _mouseDownElement.ActualWidth > 0 ? _mouseDownElement.ActualWidth : 80;
            var deviceHeight = _mouseDownElement.ActualHeight > 0 ? _mouseDownElement.ActualHeight : 60;

            left = Math.Max(0, Math.Min(DesignCanvas.ActualWidth - deviceWidth, left));
            top = Math.Max(0, Math.Min(DesignCanvas.ActualHeight - deviceHeight, top));

            return (left, top);
        }

        /// <summary>
        /// 检查拖拽位置是否有效
        /// </summary>
        private bool IsValidDragPosition(double left, double top)
        {
            var deviceId = _mouseDownElement.Tag as string;
            if (string.IsNullOrEmpty(deviceId)) return true;

            var canvasDevice = _viewModel?.CanvasDevices.FirstOrDefault(d => d.DeviceId == deviceId);
            if (canvasDevice == null) return true;

            var deviceWidth = _mouseDownElement.ActualWidth > 0 ? _mouseDownElement.ActualWidth : 80;
            var deviceHeight = _mouseDownElement.ActualHeight > 0 ? _mouseDownElement.ActualHeight : 60;

            var targetPosition = new Point(left + deviceWidth / 2, top + deviceHeight / 2);
            return IsValidRegionForDevice(canvasDevice.Device, targetPosition);
        }

        private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isMouseDown)
            {
                ProcessMouseUpEvent(e);
            }
        }

        /// <summary>
        /// 处理鼠标释放事件
        /// </summary>
        private void ProcessMouseUpEvent(MouseButtonEventArgs e)
        {
            _connectionService.SetDragMode(false);
            var endPosition = e.GetPosition(DesignCanvas);

            if (_isInternalDragging)
            {
                ProcessDragEnd(endPosition);
            }

            ResetDragState();
            e.Handled = true;
        }

        /// <summary>
        /// 处理拖拽结束
        /// </summary>
        private void ProcessDragEnd(Point endPosition)
        {
            Debug.WriteLine($"结束拖拽: {_currentDraggingDeviceId}");

            RemoveDragVisualFeedback(_mouseDownElement);

            var deviceId = _mouseDownElement.Tag as string;
            var canvasDevice = _viewModel?.CanvasDevices.FirstOrDefault(d => d.DeviceId == deviceId);

            if (canvasDevice != null)
            {
                var finalPosition = CalculateFinalPosition();
                var isSuccessful = ValidateFinalPosition(canvasDevice, finalPosition);

                if (isSuccessful)
                {
                    CompleteDragOperation(canvasDevice, deviceId, finalPosition);
                }
                else
                {
                    RestoreDeviceToOriginalPosition(_mouseDownElement);
                    UpdateConnections(deviceId);
                }

                _dragDropService.EndDrag(endPosition, isSuccessful, "Canvas");
            }

            DesignCanvas.AllowDrop = true;
        }

        /// <summary>
        /// 计算最终位置
        /// </summary>
        private Point CalculateFinalPosition()
        {
            var finalLeft = Canvas.GetLeft(_mouseDownElement);
            var finalTop = Canvas.GetTop(_mouseDownElement);
            var centerX = finalLeft + _mouseDownElement.ActualWidth / 2;
            var centerY = finalTop + _mouseDownElement.ActualHeight / 2;
            return new Point(centerX, centerY);
        }

        /// <summary>
        /// 验证最终位置
        /// </summary>
        private bool ValidateFinalPosition(CanvasDeviceViewModel canvasDevice, Point finalPosition)
        {
            var isValidRegion = IsValidRegionForDevice(canvasDevice.Device, finalPosition);
            var isWithinBounds = IsValidDropPosition(finalPosition);
            return isValidRegion && isWithinBounds;
        }

        /// <summary>
        /// 完成拖拽操作
        /// </summary>
        private void CompleteDragOperation(CanvasDeviceViewModel canvasDevice, string deviceId, Point finalPosition)
        {
            canvasDevice.Position = finalPosition;

            // 发布位置变化事件
            _eventAggregator?.GetEvent<CanvasDevicePositionChangedEvent>()?.Publish(
                new CanvasDevicePositionChangedEventArgs
                {
                    DeviceId = deviceId,
                    Device = canvasDevice.Device,
                    OldPosition = _originalDevicePosition,
                    NewPosition = finalPosition,
                    IsCompleted = true
                });

            // 更新连接线
            UpdateConnections(deviceId);
        }

        private void OnCanvasMouseLeave(object sender, MouseEventArgs e)
        {
            if (_isInternalDragging)
            {
                HandleMouseLeaveWhileDragging();
            }
        }

        /// <summary>
        /// 处理拖拽时鼠标离开画布
        /// </summary>
        private void HandleMouseLeaveWhileDragging()
        {
            _connectionService.SetDragMode(false);

            Debug.WriteLine($"鼠标离开画布，结束拖拽: {_currentDraggingDeviceId}");

            RemoveDragVisualFeedback(_mouseDownElement);
            RestoreDeviceToOriginalPosition(_mouseDownElement);

            if (_mouseDownElement?.Tag is string deviceId)
            {
                _dragDropService.EndDrag(new Point(), false, "Canvas");
                UpdateConnections(deviceId);
            }

            DesignCanvas.AllowDrop = true;
            ResetDragState();
        }

        #endregion

        #region 拖拽视觉反馈

        /// <summary>
        /// 添加拖拽视觉反馈
        /// </summary>
        private void AddDragVisualFeedback(FrameworkElement element)
        {
            if (element is Border border)
            {
                border.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Blue,
                    BlurRadius = 10,
                    ShadowDepth = 3,
                    Opacity = 0.7
                };

                Panel.SetZIndex(border, 1000);
                border.Opacity = 0.8;

                Debug.WriteLine("添加拖拽视觉反馈");
            }
        }

        /// <summary>
        /// 更新拖拽视觉反馈
        /// </summary>
        private void UpdateDragVisualFeedback(FrameworkElement element, bool isValidPosition)
        {
            if (element is Border border)
            {
                var color = isValidPosition ? Colors.Green : Colors.Red;
                border.BorderBrush = new SolidColorBrush(color);
                border.BorderThickness = new Thickness(0);
            }
        }

        /// <summary>
        /// 移除拖拽视觉反馈
        /// </summary>
        private void RemoveDragVisualFeedback(FrameworkElement element)
        {
            if (element is Border border)
            {
                border.Effect = null;
                Panel.SetZIndex(border, 0);
                border.Opacity = 1.0;
                border.BorderBrush = Brushes.DarkBlue;
                border.BorderThickness = new Thickness(0);

                Debug.WriteLine("移除拖拽视觉反馈");
            }
        }

        /// <summary>
        /// 恢复设备到原始位置
        /// </summary>
        private void RestoreDeviceToOriginalPosition(FrameworkElement deviceElement)
        {
            if (deviceElement == null) return;

            Canvas.SetLeft(deviceElement, _originalDevicePosition.X);
            Canvas.SetTop(deviceElement, _originalDevicePosition.Y);
            Debug.WriteLine($"恢复设备到原始位置: ({_originalDevicePosition.X:F0}, {_originalDevicePosition.Y:F0})");
        }

        #endregion

        #region 连接更新

        /// <summary>
        /// 强制更新连接 用于拖拽过程中
        /// </summary>
        private void ForceUpdateConnections(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId) || _connectionService?.Connections == null)
            {
                return;
            }

            var relatedConnections = _connectionService.Connections.Values
                .Where(c => c.StartDeviceId == deviceId || c.EndDeviceId == deviceId)
                .ToList();

            foreach (var connection in relatedConnections)
            {
                try
                {
                    // 重新获取设备元素引用
                    var startElement = FindDeviceElementInCanvas(connection.StartDeviceId);
                    var endElement = FindDeviceElementInCanvas(connection.EndDeviceId);

                    if (startElement != null && endElement != null)
                    {
                        connection.StartDevice = startElement;
                        connection.EndDevice = endElement;
                        _connectionService.UpdateConnectionPath(connection);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"强制更新连接失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 标准连接更新
        /// </summary>
        private void UpdateConnections(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId) || _connectionService?.Connections == null)
            {
                return;
            }

            var relatedConnections = _connectionService.Connections.Values
                .Where(c => c.StartDeviceId == deviceId || c.EndDeviceId == deviceId)
                .ToList();

            Debug.WriteLine($"更新设备 {deviceId} 的 {relatedConnections.Count} 个连接");

            foreach (var connection in relatedConnections)
            {
                try
                {
                    var startElement = FindDeviceElementInCanvas(connection.StartDeviceId);
                    var endElement = FindDeviceElementInCanvas(connection.EndDeviceId);

                    if (startElement != null && endElement != null)
                    {
                        connection.StartDevice = startElement;
                        connection.EndDevice = endElement;

                        // 强制更新布局确保变换生效
                        startElement.UpdateLayout();
                        endElement.UpdateLayout();

                        _connectionService.UpdateConnectionPath(connection);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"更新连接失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 更新设备连接点位置
        /// </summary>
        private void UpdateDeviceConnectionPoints(string deviceId)
        {
            try
            {
                if (string.IsNullOrEmpty(deviceId) || _connectionService == null)
                    return;

                Debug.WriteLine($"更新设备 {deviceId} 的连接点");

                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_isShiftPressed || _connectionService.IsConnectionMode)
                    {
                        _connectionService.ShowConnectionPoints(deviceId);
                    }
                    Debug.WriteLine($"设备 {deviceId} 的连接点已更新");
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新设备连接点失败: {ex.Message}");
            }
        }

        #endregion

        #region 设备视觉元素管理

        /// <summary>
        /// 设备集合变化事件处理
        /// </summary>
        private void OnCanvasDevicesCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            // 处理新增设备
            if (e.NewItems != null)
            {
                foreach (CanvasDeviceViewModel canvasDevice in e.NewItems)
                {
                    CreateDeviceVisual(canvasDevice);
                    ScheduleDeviceConnectionUpdate(canvasDevice.DeviceId);
                }
                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();
            }

            // 处理删除设备
            if (e.OldItems != null)
            {
                foreach (CanvasDeviceViewModel canvasDevice in e.OldItems)
                {
                    RemoveDeviceVisual(canvasDevice.DeviceId);
                }
                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();
            }

            // 更新区域覆盖上下文
            UpdateRegionOverlayContext();

            try
            {
                var currentDevices = _viewModel?.CanvasDevices?.ToList() ?? new List<CanvasDeviceViewModel>();
                _eventAggregator?.GetEvent<CanvasDevicesChangedEvent>()?.Publish(currentDevices);
                Debug.WriteLine($"发布画布设备变化事件，当前设备数量: {currentDevices.Count}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"发布画布设备变化事件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 安排设备连接更新
        /// </summary>
        private void ScheduleDeviceConnectionUpdate(string deviceId)
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateDeviceConnections(deviceId);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// 更新特定设备的连接
        /// </summary>
        private void UpdateDeviceConnections(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId) || _connectionService == null) return;

            var relatedConnections = _connectionService.Connections.Values
                .Where(c => c.StartDeviceId == deviceId || c.EndDeviceId == deviceId)
                .ToList();

            foreach (var connection in relatedConnections)
            {
                try
                {
                    UpdateConnectionDeviceReferences(connection, deviceId);
                    _connectionService.UpdateConnectionPath(connection);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"更新设备连接失败: {ex.Message}");
                }
            }

            Debug.WriteLine($"更新了设备 {deviceId} 的 {relatedConnections.Count} 个连接");
        }

        /// <summary>
        /// 更新连接的设备引用
        /// </summary>
        private void UpdateConnectionDeviceReferences(ConnectionInfo connection, string deviceId)
        {
            if (connection.StartDeviceId == deviceId)
            {
                connection.StartDevice = FindDeviceElementInCanvas(deviceId);
            }
            if (connection.EndDeviceId == deviceId)
            {
                connection.EndDevice = FindDeviceElementInCanvas(deviceId);
            }
        }

        /// <summary>
        /// 创建设备视觉元素
        /// </summary>
        private void CreateDeviceVisual(CanvasDeviceViewModel canvasDevice)
        {
            // 检查是否已存在
            if (IsDeviceVisualExists(canvasDevice.DeviceId))
            {
                Debug.WriteLine($"设备 {canvasDevice.DeviceId} 的视觉元素已存在，跳过创建");
                return;
            }

            // 创建设备控件
            var deviceControl = CreateDeviceControl(canvasDevice);

            // 添加到画布
            AddDeviceToCanvas(deviceControl, canvasDevice);

            // 设置事件处理器
            AttachDeviceEventHandlers(deviceControl);

            // 更新连接引用
            UpdateDeviceConnectionReferences(canvasDevice.DeviceId, deviceControl);

            Debug.WriteLine($"设备视觉元素创建完成: {canvasDevice.Device.Name}, ID: {canvasDevice.DeviceId}");
        }

        /// <summary>
        /// 检查设备视觉元素是否已存在
        /// </summary>
        private bool IsDeviceVisualExists(string deviceId)
        {
            return DesignCanvas.Children
                .OfType<FrameworkElement>()
                .Any(e => e.Tag as string == deviceId);
        }

        /// <summary>
        /// 创建设备控件
        /// </summary>
        private Border CreateDeviceControl(CanvasDeviceViewModel canvasDevice)
        {
            var adjustedSize = GetAdjustedDeviceSize(canvasDevice.Device, canvasDevice.Width, canvasDevice.Height);

            // NEW: 生成并缓存显示名（如 泵#2）
            var displayName = GetDisplayNameForNewDevice(canvasDevice.Device);
            _deviceDisplayNames[canvasDevice.DeviceId] = displayName;

            var deviceControl = new Border
            {
                Width = adjustedSize.Width,
                Height = adjustedSize.Height,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.DarkBlue,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(5),
                Cursor = Cursors.Hand,
                Tag = canvasDevice.DeviceId,
                IsHitTestVisible = true,
                AllowDrop = false
            };

            // NEW: 传 displayName，而不是 device.Name
            deviceControl.Child = CreateDeviceContent(canvasDevice, adjustedSize, displayName);

            return deviceControl;
        }

        /// <summary>
        /// 创建设备内容
        /// </summary>
        private DockPanel CreateDeviceContent(CanvasDeviceViewModel canvasDevice, Size adjustedSize, string displayName)
        {
            var mainPanel = new DockPanel { LastChildFill = true };

            // 设备名称（使用画布显示名）
            var nameText = CreateDeviceNameText(displayName);
            DockPanel.SetDock(nameText, Dock.Bottom);
            mainPanel.Children.Add(nameText);

            // 设备图标
            var iconElement = CreateDeviceIcon(canvasDevice.Device, adjustedSize.Width - 4, adjustedSize.Height - 24);
            mainPanel.Children.Add(iconElement);

            return mainPanel;
        }

        /// <summary>
        /// 创建设备名称文本
        /// </summary>
        private TextBlock CreateDeviceNameText(string deviceName)
        {
            return new TextBlock
            {
                Text = deviceName,
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Foreground = Brushes.DarkBlue,
                Background = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                Padding = new Thickness(2, 1, 2, 1),
                IsHitTestVisible = false,
                MaxHeight = 20
            };
        }

        /// <summary>
        /// 将设备添加到画布
        /// </summary>
        private void AddDeviceToCanvas(Border deviceControl, CanvasDeviceViewModel canvasDevice)
        {
            var left = canvasDevice.Position.X - deviceControl.Width / 2;
            var top = canvasDevice.Position.Y - deviceControl.Height / 2;

            Canvas.SetLeft(deviceControl, left);
            Canvas.SetTop(deviceControl, top);

            DesignCanvas.Children.Add(deviceControl);

            // 恢复选择状态
            if (canvasDevice.DeviceId == _selectedDeviceId)
            {
                OnDeviceSelected(canvasDevice.DeviceId);
            }
        }

        /// <summary>
        /// 附加设备事件处理器
        /// </summary>
        private void AttachDeviceEventHandlers(Border deviceControl)
        {
            deviceControl.MouseDown += OnDeviceMouseDown;
            deviceControl.MouseUp += OnDeviceMouseUp;
            deviceControl.MouseEnter += OnDeviceMouseEnter;
            deviceControl.MouseLeave += OnDeviceMouseLeave;
            deviceControl.MouseMove += OnDeviceMouseMove;
            // 添加双击事件处理器
            deviceControl.MouseLeftButtonDown += OnDeviceDoubleClick;
        }
        #region 设备属性弹窗相关

        /// <summary>
        /// 设备双击事件处理
        /// </summary>
        private void OnDeviceDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement deviceElement && deviceElement.Tag is string deviceId)
            {
                // 检查是否为双击
                if (e.ClickCount == 2)
                {
                    Debug.WriteLine($"设备双击，显示属性弹窗: {deviceId}");

                    // 阻止事件冒泡，避免触发拖拽
                    e.Handled = true;

                    // 显示设备属性弹窗
                    ShowDevicePropertyDialog(deviceId);
                }
            }
        }

        /// <summary>
        /// 显示设备属性弹窗
        /// </summary>
        private void ShowDevicePropertyDialog(string deviceId)
        {
            try
            {
                // 查找设备数据
                var canvasDevice = _viewModel?.CanvasDevices.FirstOrDefault(d => d.DeviceId == deviceId);
                if (canvasDevice == null)
                {
                    Debug.WriteLine($"未找到设备: {deviceId}");
                    MessageBox.Show("未找到设备信息", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Debug.WriteLine($"显示设备属性弹窗: {canvasDevice.Device.Name}");

                // 关闭之前的弹窗（如果存在）
                if (_devicePropertyWindow != null)
                {
                    _devicePropertyWindow.Close();
                    _devicePropertyWindow = null;
                }

                // 创建设备属性对话框
                var devicePropertyDialog = new DevicePropertyDialog();

                // 获取设备显示名称
                var displayName = GetDeviceDisplayName(deviceId);

                // 获取之前的监控参数选择状态
                var previousSelections = GetDeviceMonitoringSelections(deviceId);

                // 设置设备数据并恢复选择状态
                devicePropertyDialog.SetDeviceData(canvasDevice, displayName);
                devicePropertyDialog.RestoreMonitoringSelections(previousSelections);

                // 订阅监控参数变化事件
                devicePropertyDialog.MonitoringParametersChanged += OnMonitoringParametersChanged;

                // 创建弹窗窗口
                _devicePropertyWindow = new Window
                {
                    Title = "设备属性",
                    Content = devicePropertyDialog,
                    Width = 1200,
                    Height = 700,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = Window.GetWindow(this),
                    ResizeMode = ResizeMode.NoResize,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false
                };

                // 订阅弹窗事件
                devicePropertyDialog.CloseRequested += (s, args) => CloseDevicePropertyWindow();
                devicePropertyDialog.DialogResult += OnDevicePropertyDialogResult;

                // 显示弹窗
                _devicePropertyWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"显示设备属性弹窗失败: {ex.Message}");
                MessageBox.Show($"无法显示设备属性：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        /// <summary>
        /// 保存设备的监控参数选择状态
        /// </summary>
        private void SaveDeviceMonitoringSelections(string deviceId, List<ParameterBase> selectedParameters)
        {
            try
            {
                if (string.IsNullOrEmpty(deviceId) || selectedParameters == null)
                    return;

                var parameterNames = selectedParameters.Select(p => p.Name).ToHashSet();
                _deviceMonitoringSelections[deviceId] = parameterNames;

                Debug.WriteLine($"保存设备 {deviceId} 的监控参数选择: {string.Join(", ", parameterNames)}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"保存设备监控参数选择失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取设备的监控参数选择状态
        /// </summary>
        private HashSet<string> GetDeviceMonitoringSelections(string deviceId)
        {
            try
            {
                if (string.IsNullOrEmpty(deviceId))
                    return new HashSet<string>();

                if (_deviceMonitoringSelections.TryGetValue(deviceId, out var selections))
                {
                    Debug.WriteLine($"获取设备 {deviceId} 的监控参数选择: {string.Join(", ", selections)}");
                    return selections;
                }

                return new HashSet<string>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取设备监控参数选择失败: {ex.Message}");
                return new HashSet<string>();
            }
        }

        /// <summary>
        /// 清除设备的监控参数选择状态
        /// </summary>
        private void ClearDeviceMonitoringSelections(string deviceId)
        {
            try
            {
                if (_deviceMonitoringSelections.ContainsKey(deviceId))
                {
                    _deviceMonitoringSelections.Remove(deviceId);
                    Debug.WriteLine($"清除设备 {deviceId} 的监控参数选择状态");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清除设备监控参数选择状态失败: {ex.Message}");
            }
        }
        /// <summary>
        /// 监控参数变化事件处理
        /// </summary>
        private void OnMonitoringParametersChanged(object sender, MonitoringParametersChangedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"监控参数变化: 设备 {e.DeviceName}, 参数数量: {e.SelectedParameters.Count}");

                // 保存选择状态
                SaveDeviceMonitoringSelections(e.DeviceId, e.SelectedParameters);

                if (e.SelectedParameters.Any())
                {
                    // 创建或更新监控卡片
                    CreateOrUpdateMonitoringCard(e.DeviceId, e.DeviceName, e.SelectedParameters);
                }
                else
                {
                    // 移除监控卡片
                    RemoveMonitoringCard(e.DeviceId);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理监控参数变化失败: {ex.Message}");
            }
        }
        /// <summary>
        /// 创建或更新监控卡片
        /// </summary>
        private void CreateOrUpdateMonitoringCard(string deviceId, string deviceName, List<ParameterBase> parameters)
        {
            try
            {
                // 检查是否已存在监控卡片
                if (_monitoringCards.TryGetValue(deviceId, out var existingCard))
                {
                    // 更新现有卡片
                    existingCard.SetMonitoringParameters(deviceId, deviceName, parameters);
                    Debug.WriteLine($"更新监控卡片: {deviceName}");
                }
                else
                {
                    // 创建新的监控卡片
                    var monitoringCard = new MonitoringCard();
                    monitoringCard.SetMonitoringParameters(deviceId, deviceName, parameters);

                    // 订阅关闭事件
                    monitoringCard.CloseRequested += (s, e) => RemoveMonitoringCard(deviceId);

                    // 添加到字典
                    _monitoringCards[deviceId] = monitoringCard;

                    // 添加到画布
                    AddMonitoringCardToCanvas(deviceId, monitoringCard);

                    Debug.WriteLine($"创建监控卡片: {deviceName}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"创建或更新监控卡片失败: {ex.Message}");
            }
        }
        /// <summary>
        /// 将监控卡片添加到画布
        /// </summary>
        private void AddMonitoringCardToCanvas(string deviceId, MonitoringCard monitoringCard)
        {
            try
            {
                // 查找设备元素
                var deviceElement = FindDeviceElementInCanvas(deviceId);
                if (deviceElement == null)
                {
                    Debug.WriteLine($"未找到设备元素: {deviceId}");
                    return;
                }

                // 计算卡片位置（设备右侧）
                var deviceLeft = Canvas.GetLeft(deviceElement);
                var deviceTop = Canvas.GetTop(deviceElement);
                var deviceWidth = deviceElement.ActualWidth;

                var cardLeft = deviceLeft + deviceWidth + 15; // 设备右侧15像素间距
                var cardTop = deviceTop;

                // 设置卡片位置
                Canvas.SetLeft(monitoringCard, cardLeft);
                Canvas.SetTop(monitoringCard, cardTop);
                Canvas.SetZIndex(monitoringCard, 500); // 确保在设备之上

                // 设置卡片标识
                monitoringCard.Tag = $"MonitoringCard_{deviceId}";

                // 添加到画布
                DesignCanvas.Children.Add(monitoringCard);

                Debug.WriteLine($"监控卡片已添加到画布: 位置({cardLeft:F0}, {cardTop:F0})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"添加监控卡片到画布失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 移除监控卡片
        /// </summary>
        private void RemoveMonitoringCard(string deviceId)
        {
            try
            {
                if (_monitoringCards.TryGetValue(deviceId, out var card))
                {
                    // 从画布移除
                    DesignCanvas.Children.Remove(card);

                    // 从字典移除
                    _monitoringCards.Remove(deviceId);

                    Debug.WriteLine($"移除监控卡片: {deviceId}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"移除监控卡片失败: {ex.Message}");
            }
        }
        /// <summary>
        /// 更新设备位置时同步更新监控卡片位置
        /// </summary>
        private void UpdateMonitoringCardPosition(string deviceId)
        {
            try
            {
                if (!_monitoringCards.TryGetValue(deviceId, out var card))
                    return;

                var deviceElement = FindDeviceElementInCanvas(deviceId);
                if (deviceElement == null)
                    return;

                // 重新计算位置
                var deviceLeft = Canvas.GetLeft(deviceElement);
                var deviceTop = Canvas.GetTop(deviceElement);
                var deviceWidth = deviceElement.ActualWidth;

                var cardLeft = deviceLeft + deviceWidth + 15;
                var cardTop = deviceTop;

                // 更新卡片位置
                Canvas.SetLeft(card, cardLeft);
                Canvas.SetTop(card, cardTop);

                Debug.WriteLine($"更新监控卡片位置: {deviceId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新监控卡片位置失败: {ex.Message}");
            }
        }
      

        /// <summary>
        /// 获取设备显示名称
        /// </summary>
        private string GetDeviceDisplayName(string deviceId)
        {
            try
            {
                if (_deviceDisplayNames.TryGetValue(deviceId, out var displayName))
                {
                    return displayName;
                }

                // 如果没有找到显示名，返回设备原始名称
                var canvasDevice = _viewModel?.CanvasDevices.FirstOrDefault(d => d.DeviceId == deviceId);
                return canvasDevice?.Device?.Name ?? "未知设备";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取设备显示名失败: {ex.Message}");
                return "未知设备";
            }
        }

        /// <summary>
        /// 关闭设备属性弹窗
        /// </summary>
        private void CloseDevicePropertyWindow()
        {
            try
            {
                if (_devicePropertyWindow != null)
                {
                    _devicePropertyWindow.Close();
                    _devicePropertyWindow = null;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"关闭设备属性弹窗失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 设备属性弹窗结果处理
        /// </summary>
        private void OnDevicePropertyDialogResult(object sender, bool result)
        {
            try
            {
                CloseDevicePropertyWindow();

                if (result)
                {
                    Debug.WriteLine("设备属性已保存");
                    // 标记画布已修改
                    _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();
                }
                else
                {
                    Debug.WriteLine("设备属性对话框已取消");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理设备属性弹窗结果失败: {ex.Message}");
            }
        }

        #endregion
        /// <summary>
        /// 设备专用鼠标按下事件
        /// </summary>
        private void OnDeviceMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement deviceElement && deviceElement.Tag is string deviceId)
            {
                try
                {
                    Debug.WriteLine($"设备鼠标按下: {deviceId}");

                    // 验证设备存在性
                    if (!IsDeviceExistInViewModel(deviceId))
                    {
                        Debug.WriteLine($"设备 {deviceId} 已不存在，忽略事件");
                        e.Handled = true;
                        return;
                    }

                    // 清理之前的状态
                    if (_isInternalDragging || _isMouseDown)
                    {
                        Debug.WriteLine("清理之前的拖拽状态");
                        CleanupDragState();
                    }

                    // 选择设备
                    OnDeviceSelected(deviceId);

                    // 处理左键按下
                    if (e.LeftButton == MouseButtonState.Pressed)
                    {
                        HandleDeviceLeftClick(deviceElement, deviceId, e);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"设备鼠标按下处理失败: {ex.Message}");
                    CleanupDragState();
                }
            }
        }

        /// <summary>
        /// 处理设备左键点击
        /// </summary>
        private void HandleDeviceLeftClick(FrameworkElement deviceElement, string deviceId, MouseButtonEventArgs e)
        {
            if (_isShiftPressed)
            {
                // Shift点击进入连接模式
                HandleConnectionClick(deviceId, e.GetPosition(deviceElement));
                e.Handled = true;
                return;
            }

            // 准备拖拽
            StartDeviceDragPreparation(deviceElement, deviceId, e);
            e.Handled = true;
        }

        /// <summary>
        /// 开始设备拖拽准备
        /// </summary>
        private void StartDeviceDragPreparation(FrameworkElement deviceElement, string deviceId, MouseButtonEventArgs e)
        {
            _isMouseDown = true;
            _mouseDownElement = deviceElement;
            _currentDraggingDeviceId = deviceId;
            _dragStartPoint = e.GetPosition(DesignCanvas);
            _hasStartedDrag = false;

            // 记录原始位置
            var left = Canvas.GetLeft(deviceElement);
            var top = Canvas.GetTop(deviceElement);
            _originalDevicePosition = new Point(
                double.IsNaN(left) ? 0 : left,
                double.IsNaN(top) ? 0 : top
            );

            Debug.WriteLine($"设备拖拽准备: {deviceId}, 原始位置: ({_originalDevicePosition.X:F0}, {_originalDevicePosition.Y:F0})");

            // 捕获鼠标
            deviceElement.CaptureMouse();
        }

        /// <summary>
        /// 设备鼠标释放事件
        /// </summary>
        private void OnDeviceMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement deviceElement && deviceElement.Tag is string deviceId)
            {
                Debug.WriteLine($"设备鼠标释放: {deviceId}");

                // 释放鼠标捕获
                if (deviceElement.IsMouseCaptured)
                {
                    deviceElement.ReleaseMouseCapture();
                }

                // 处理拖拽结束
                if (_isInternalDragging && _currentDraggingDeviceId == deviceId)
                {
                    HandleDragEnd(e.GetPosition(DesignCanvas));
                }

                e.Handled = true;
            }
        }

        /// <summary>
        /// 处理拖拽结束命令模式
        /// </summary>
        private void HandleDragEnd(Point endPosition)
        {
            _connectionService.SetDragMode(false);

            Debug.WriteLine($"结束设备拖拽: {_currentDraggingDeviceId}");

            if (_mouseDownElement != null)
            {
                RemoveDragVisualFeedback(_mouseDownElement);

                var deviceId = _mouseDownElement.Tag as string;
                var canvasDevice = _viewModel?.CanvasDevices.FirstOrDefault(d => d.DeviceId == deviceId);

                var finalPosition = CalculateFinalPosition();
                var isValidPosition = canvasDevice != null && IsValidRegionForDevice(canvasDevice.Device, finalPosition);
                var isWithinBounds = IsValidDropPosition(finalPosition);
                var isSuccessful = isValidPosition && isWithinBounds;

                if (isSuccessful && canvasDevice != null)
                {
                    // 使用命令模式记录移动操作
                    ExecuteMoveDeviceCommand(canvasDevice, finalPosition);
                    UpdateConnections(deviceId);
                }
                else
                {
                    RestoreDeviceToOriginalPosition(_mouseDownElement);
                    if (!string.IsNullOrEmpty(deviceId))
                    {
                        UpdateConnections(deviceId);
                    }
                }

                _dragDropService.EndDrag(endPosition, isSuccessful, "Canvas");
                DesignCanvas.AllowDrop = true;
            }

            ResetDragState();
        }

        /// <summary>
        /// 执行移动设备命令
        /// </summary>
        private void ExecuteMoveDeviceCommand(CanvasDeviceViewModel canvasDevice, Point finalPosition)
        {
            var moveCommand = new MoveDeviceCommand(
                canvasDevice,
                _originalDevicePosition,
                finalPosition,
                UpdateDeviceVisualPosition);

            _commandHistory.ExecuteCommand(moveCommand);

            // 发布位置变化事件
            _eventAggregator?.GetEvent<CanvasDevicePositionChangedEvent>()?.Publish(
                new CanvasDevicePositionChangedEventArgs
                {
                    DeviceId = canvasDevice.DeviceId,
                    Device = canvasDevice.Device,
                    OldPosition = _originalDevicePosition,
                    NewPosition = finalPosition,
                    IsCompleted = true
                });
        }

        /// <summary>
        /// 更新设备视觉位置
        /// </summary>
        private void UpdateDeviceVisualPosition(string deviceId, Point newPosition)
        {
            try
            {
                var deviceElement = FindDeviceElementInCanvas(deviceId);
                if (deviceElement == null)
                {
                    Debug.WriteLine($"UpdateDeviceVisualPosition: 未找到设备元素 {deviceId}");
                    return;
                }

                // 计算左上角位置
                var elementLeft = newPosition.X - deviceElement.ActualWidth / 2;
                var elementTop = newPosition.Y - deviceElement.ActualHeight / 2;

                // 更新位置
                Canvas.SetLeft(deviceElement, elementLeft);
                Canvas.SetTop(deviceElement, elementTop);

                Debug.WriteLine($"设备 {deviceId} 视觉位置已更新到: ({elementLeft:F1}, {elementTop:F1})");

                // 强制更新布局
                deviceElement.UpdateLayout();

                // 更新监控卡片位置
                UpdateMonitoringCardPosition(deviceId);

                // 延迟更新连接线
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateConnections(deviceId);
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新设备视觉位置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 设备鼠标移动事件
        /// </summary>
        private void OnDeviceMouseMove(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement deviceElement && deviceElement.Tag is string deviceId)
            {
                // 只处理当前拖拽设备的鼠标移动
                if (_isMouseDown && _currentDraggingDeviceId == deviceId && e.LeftButton == MouseButtonState.Pressed)
                {
                    HandleDeviceMouseMove(deviceElement, e);
                }
            }
        }

        /// <summary>
        /// 处理设备鼠标移动
        /// </summary>
        private void HandleDeviceMouseMove(FrameworkElement deviceElement, MouseEventArgs e)
        {
            Point currentPoint = e.GetPosition(DesignCanvas);

            // 检查是否超过拖拽阈值
            if (!_hasStartedDrag)
            {
                if (ShouldStartDragging(currentPoint))
                {
                    StartDeviceDrag(deviceElement);
                }
            }

            if (_hasStartedDrag)
            {
                ContinueDeviceDrag(deviceElement, currentPoint);
            }

            e.Handled = true;
        }

        /// <summary>
        /// 开始设备拖拽
        /// </summary>
        private void StartDeviceDrag(FrameworkElement deviceElement)
        {
            _hasStartedDrag = true;
            _isInternalDragging = true;

            _connectionService.SetDragMode(true);
            DesignCanvas.AllowDrop = false;
            AddDragVisualFeedback(deviceElement);

            Debug.WriteLine($"开始拖拽设备: {_currentDraggingDeviceId}");
        }

        /// <summary>
        /// 继续设备拖拽
        /// </summary>
        private void ContinueDeviceDrag(FrameworkElement deviceElement, Point currentPoint)
        {
            // 计算新位置
            double dx = currentPoint.X - _dragStartPoint.X;
            double dy = currentPoint.Y - _dragStartPoint.Y;

            double newLeft = _originalDevicePosition.X + dx;
            double newTop = _originalDevicePosition.Y + dy;

            // 应用边界限制
            var (boundedLeft, boundedTop) = ApplyDeviceBoundaryConstraints(deviceElement, newLeft, newTop);

            // 检查区域有效性
            if (!IsValidDeviceDragPosition(boundedLeft, boundedTop, deviceElement))
            {
                UpdateDragVisualFeedback(deviceElement, false);
                return;
            }

            // 移动设备
            Canvas.SetLeft(deviceElement, boundedLeft);
            Canvas.SetTop(deviceElement, boundedTop);
            deviceElement.Opacity = 0.8;

            // 更新连接
            ForceUpdateConnections(_currentDraggingDeviceId);

            UpdateDragVisualFeedback(deviceElement, true);
        }

        /// <summary>
        /// 应用设备边界约束
        /// </summary>
        private (double left, double top) ApplyDeviceBoundaryConstraints(FrameworkElement deviceElement, double left, double top)
        {
            var deviceWidth = deviceElement.ActualWidth > 0 ? deviceElement.ActualWidth : 80;
            var deviceHeight = deviceElement.ActualHeight > 0 ? deviceElement.ActualHeight : 60;

            left = Math.Max(0, Math.Min(DesignCanvas.ActualWidth - deviceWidth, left));
            top = Math.Max(0, Math.Min(DesignCanvas.ActualHeight - deviceHeight, top));

            return (left, top);
        }

        /// <summary>
        /// 检查设备拖拽位置是否有效
        /// </summary>
        private bool IsValidDeviceDragPosition(double left, double top, FrameworkElement deviceElement)
        {
            var canvasDevice = _viewModel?.CanvasDevices.FirstOrDefault(d => d.DeviceId == _currentDraggingDeviceId);
            if (canvasDevice == null) return true;

            var deviceWidth = deviceElement.ActualWidth > 0 ? deviceElement.ActualWidth : 80;
            var deviceHeight = deviceElement.ActualHeight > 0 ? deviceElement.ActualHeight : 60;

            var targetPosition = new Point(left + deviceWidth / 2, top + deviceHeight / 2);
            return IsValidRegionForDevice(canvasDevice.Device, targetPosition);
        }

        /// <summary>
        /// 设备鼠标进入事件
        /// </summary>
        private void OnDeviceMouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement deviceElement && deviceElement.Tag is string deviceId)
            {
                Debug.WriteLine($"鼠标进入设备: {deviceId}");

                // 添加悬停效果
                AddDeviceHoverEffect(deviceElement, deviceId);

                // 连接模式下显示连接点
                if (_isShiftPressed)
                {
                    _connectionService?.ShowConnectionPoints(deviceId);
                }
            }
        }

        /// <summary>
        /// 添加设备悬停效果
        /// </summary>
        private void AddDeviceHoverEffect(FrameworkElement deviceElement, string deviceId)
        {
            if (deviceElement is Border border && deviceId != _selectedDeviceId)
            {
                border.BorderBrush = Brushes.LightBlue;
                border.BorderThickness = new Thickness(1);
            }
        }

        /// <summary>
        /// 设备鼠标离开事件
        /// </summary>
        private void OnDeviceMouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is FrameworkElement deviceElement && deviceElement.Tag is string deviceId)
            {
                Debug.WriteLine($"鼠标离开设备: {deviceId}");

                // 移除悬停效果但保留选中效果
                RemoveDeviceHoverEffect(deviceElement, deviceId);
            }
        }

        /// <summary>
        /// 移除设备悬停效果
        /// </summary>
        private void RemoveDeviceHoverEffect(FrameworkElement deviceElement, string deviceId)
        {
            if (deviceElement is Border border && deviceId != _selectedDeviceId)
            {
                border.BorderBrush = Brushes.DarkBlue;
                border.BorderThickness = new Thickness(0);
            }
        }

        /// <summary>
        /// 更新设备连接引用
        /// </summary>
        private void UpdateDeviceConnectionReferences(string deviceId, FrameworkElement deviceElement)
        {
            if (string.IsNullOrEmpty(deviceId) || deviceElement == null || _connectionService?.Connections == null)
            {
                return;
            }

            Debug.WriteLine($"更新设备 {deviceId} 的连接引用");

            var relatedConnections = _connectionService.Connections.Values
                .Where(c => c.StartDeviceId == deviceId || c.EndDeviceId == deviceId)
                .ToList();

            foreach (var connection in relatedConnections)
            {
                if (connection.StartDeviceId == deviceId)
                {
                    connection.StartDevice = deviceElement;
                    Debug.WriteLine($"更新连接 {connection.Id} 的起始设备引用");
                }
                if (connection.EndDeviceId == deviceId)
                {
                    connection.EndDevice = deviceElement;
                    Debug.WriteLine($"更新连接 {connection.Id} 的结束设备引用");
                }
            }

            Debug.WriteLine($"设备 {deviceId} 的 {relatedConnections.Count} 个连接引用已更新");
        }

        /// <summary>
        /// 移除设备视觉元素
        /// </summary>
        private void RemoveDeviceVisual(string deviceId)
        {
            try
            {
                var elementToRemove = DesignCanvas.Children
                    .OfType<FrameworkElement>()
                    .FirstOrDefault(e => e.Tag as string == deviceId);

                if (elementToRemove != null)
                {
                    // 清理鼠标捕获
                    if (elementToRemove.IsMouseCaptured)
                    {
                        elementToRemove.ReleaseMouseCapture();
                        Debug.WriteLine($"释放设备 {deviceId} 的鼠标捕获");
                    }

                    // 清理引用
                    if (_mouseDownElement == elementToRemove)
                    {
                        Debug.WriteLine($"清空鼠标按下元素引用: {deviceId}");
                        _mouseDownElement = null;
                    }

                    // 清理事件处理器
                    DetachDeviceEventHandlers(elementToRemove);

                    // 从画布移除
                    DesignCanvas.Children.Remove(elementToRemove);
                    Debug.WriteLine($"设备 {deviceId} 的视觉元素已移除");
                    //从映射移除：
                    _deviceDisplayNames.Remove(deviceId);
                }
                else
                {
                    Debug.WriteLine($"未找到设备 {deviceId} 的视觉元素");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"移除设备视觉元素失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 分离设备事件处理器
        /// </summary>
        private void DetachDeviceEventHandlers(FrameworkElement element)
        {
            element.MouseDown -= OnDeviceMouseDown;
            element.MouseUp -= OnDeviceMouseUp;
            element.MouseEnter -= OnDeviceMouseEnter;
            element.MouseLeave -= OnDeviceMouseLeave;
            element.MouseMove -= OnDeviceMouseMove;
            // 移除双击事件处理器
            element.MouseLeftButtonDown -= OnDeviceDoubleClick;
        }

        #endregion

        #region 设备图标和尺寸相关

        /// <summary>
        /// 获取调整后的设备尺寸
        /// </summary>
        private Size GetAdjustedDeviceSize(IDevice device, double defaultWidth, double defaultHeight)
        {
            try
            {
                var imageLocationProperty = device.GetType().GetProperty("ImageLocation");
                string imageLocation = imageLocationProperty?.GetValue(device) as string;

                if (!string.IsNullOrEmpty(imageLocation) && IsValidImageUri(imageLocation))
                {
                    return CalculateImageBasedSize(imageLocation, defaultWidth, defaultHeight);
                }

                return new Size(defaultWidth, defaultHeight);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"调整设备尺寸时发生错误: {ex.Message}");
                return new Size(defaultWidth, defaultHeight);
            }
        }

        /// <summary>
        /// 基于图片计算尺寸
        /// </summary>
        private Size CalculateImageBasedSize(string imageLocation, double defaultWidth, double defaultHeight)
        {
            var imageSize = GetImageSize(imageLocation);
            if (imageSize.Width > 0 && imageSize.Height > 0)
            {
                double imageAspectRatio = imageSize.Width / imageSize.Height;
                double availableHeight = defaultHeight - 24; // 减去名称区域高度

                double targetWidth, targetHeight;

                if (imageAspectRatio > 1)
                {
                    targetWidth = defaultWidth;
                    targetHeight = targetWidth / imageAspectRatio + 24;
                }
                else
                {
                    targetHeight = defaultHeight;
                    targetWidth = (targetHeight - 24) * imageAspectRatio;
                }

                // 限制尺寸范围
                targetWidth = Math.Max(60, Math.Min(120, targetWidth));
                targetHeight = Math.Max(60, Math.Min(120, targetHeight));

                Debug.WriteLine($"图片尺寸调整: {imageSize.Width}x{imageSize.Height} -> {targetWidth:F0}x{targetHeight:F0}");

                return new Size(targetWidth, targetHeight);
            }

            return new Size(defaultWidth, defaultHeight);
        }

        /// <summary>
        /// 获取图片尺寸
        /// </summary>
        private Size GetImageSize(string imagePath)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;

                if (imagePath.StartsWith("pack://"))
                {
                    bitmap.UriSource = new Uri(imagePath);
                }
                else
                {
                    bitmap.UriSource = new Uri(imagePath, UriKind.RelativeOrAbsolute);
                }

                bitmap.EndInit();
                return new Size(bitmap.PixelWidth, bitmap.PixelHeight);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取图片尺寸失败: {ex.Message}");
                return new Size(0, 0);
            }
        }

        /// <summary>
        /// 创建设备图标
        /// </summary>
        private UIElement CreateDeviceIcon(IDevice device, double targetWidth, double targetHeight)
        {
            try
            {
                var imageLocationProperty = device.GetType().GetProperty("ImageLocation");
                string imageLocation = imageLocationProperty?.GetValue(device) as string;

                if (!string.IsNullOrEmpty(imageLocation) && IsValidImageUri(imageLocation))
                {
                    return CreateImageIcon(imageLocation, targetWidth, targetHeight);
                }
                else
                {
                    return CreateDefaultIcon(targetWidth, targetHeight, device.Name);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"创建设备图标失败: {ex.Message}");
                return CreateDefaultIcon(targetWidth, targetHeight, device.Name);
            }
        }

        /// <summary>
        /// 创建图片图标
        /// </summary>
        private Image CreateImageIcon(string imagePath, double targetWidth, double targetHeight)
        {
            var image = new Image
            {
                Width = targetWidth,
                Height = targetHeight,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            SetImageSource(image, imagePath);
            return image;
        }

        /// <summary>
        /// 设置图片源
        /// </summary>
        private void SetImageSource(Image image, string imagePath)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;

                if (imagePath.StartsWith("pack://"))
                {
                    bitmap.UriSource = new Uri(imagePath);
                }
                else
                {
                    bitmap.UriSource = new Uri(imagePath, UriKind.RelativeOrAbsolute);
                }

                bitmap.EndInit();
                image.Source = bitmap;

                Debug.WriteLine($"成功加载设备图标: {imagePath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"设置图像源失败: {ex.Message}");
                image.Source = null;
            }
        }

        /// <summary>
        /// 创建默认图标
        /// </summary>
        private UIElement CreateDefaultIcon(double width, double height, string deviceName)
        {
            var border = new Border
            {
                Width = width,
                Height = height,
                Background = new SolidColorBrush(Color.FromRgb(200, 220, 240)),
                CornerRadius = new CornerRadius(3)
            };

            var textBlock = new TextBlock
            {
                Text = "设备",
                FontSize = Math.Min(width, height) / 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Foreground = Brushes.DarkBlue,
                IsHitTestVisible = false
            };

            border.Child = textBlock;
            return border;
        }

        /// <summary>
        /// 验证图片URI是否有效
        /// </summary>
        private bool IsValidImageUri(string imageLocation)
        {
            try
            {
                if (string.IsNullOrEmpty(imageLocation))
                    return false;

                // 检查不同类型的URI
                if (imageLocation.StartsWith("pack://siteoforigin:,,,/"))
                {
                    return CheckSiteOfOriginImage(imageLocation);
                }

                if (imageLocation.StartsWith("pack://application:,,,/"))
                {
                    return CheckApplicationResourceImage(imageLocation);
                }

                if (System.IO.Path.IsPathRooted(imageLocation))
                {
                    return System.IO.File.Exists(imageLocation);
                }

                if (Uri.TryCreate(imageLocation, UriKind.Absolute, out Uri uri))
                {
                    return CheckAbsoluteUriImage(uri);
                }

                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"验证图像URI失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 检查站点源图片
        /// </summary>
        private bool CheckSiteOfOriginImage(string imageLocation)
        {
            string relativePath = imageLocation.Substring("pack://siteoforigin:,,,/".Length);
            string appDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            string fullPath = System.IO.Path.Combine(appDir, relativePath);
            return System.IO.File.Exists(fullPath);
        }

        /// <summary>
        /// 检查应用程序资源图片
        /// </summary>
        private bool CheckApplicationResourceImage(string imageLocation)
        {
            try
            {
                var testBitmap = new BitmapImage();
                testBitmap.BeginInit();
                testBitmap.UriSource = new Uri(imageLocation);
                testBitmap.EndInit();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 检查绝对URI图片
        /// </summary>
        private bool CheckAbsoluteUriImage(Uri uri)
        {
            try
            {
                var testBitmap = new BitmapImage();
                testBitmap.BeginInit();
                testBitmap.UriSource = uri;
                testBitmap.EndInit();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取设备缩放信息
        /// </summary>
        private (double ScaleX, double ScaleY) GetDeviceScaleInfo(string deviceId)
        {
            try
            {
                var deviceElement = FindDeviceElementInCanvas(deviceId);
                if (deviceElement == null)
                {
                    Debug.WriteLine($"获取缩放信息: 未找到设备元素 {deviceId}");
                    return (1.0, 1.0);
                }

                // 检查变换组中的缩放变换
                if (deviceElement.RenderTransform is TransformGroup transformGroup)
                {
                    var scaleTransform = transformGroup.Children.OfType<ScaleTransform>().FirstOrDefault();
                    if (scaleTransform != null)
                    {
                        Debug.WriteLine($"设备 {deviceId} 缩放信息: {scaleTransform.ScaleX:F2}x{scaleTransform.ScaleY:F2}");
                        return (scaleTransform.ScaleX, scaleTransform.ScaleY);
                    }
                }
                else if (deviceElement.RenderTransform is ScaleTransform directScale)
                {
                    Debug.WriteLine($"设备 {deviceId} 直接缩放: {directScale.ScaleX:F2}x{directScale.ScaleY:F2}");
                    return (directScale.ScaleX, directScale.ScaleY);
                }

                // 没有缩放变换返回默认值
                Debug.WriteLine($"设备 {deviceId} 无缩放变换，使用默认1.0x1.0");
                return (1.0, 1.0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取设备缩放信息失败: {ex.Message}");
                return (1.0, 1.0);
            }
        }

        #endregion

        #region 画布数据操作相关

        /// <summary>
        /// 清空画布请求处理
        /// </summary>
        private void OnClearCanvasRequest()
        {
            try
            {
                Debug.WriteLine("开始清空画布");

                // 取消连接操作
                CancelActiveConnectionOperations();

                // 清空监控卡片
                ClearAllMonitoringCards();

                // 清空监控参数选择状态
                _deviceMonitoringSelections.Clear();

                // 清空数据模型
                ClearViewModelData();

                // 清空连接服务
                ClearConnectionService();

                // 清空视觉元素
                ClearCanvasVisualElements();

                // 清空区域覆盖层
                _regionOverlayService?.ClearRegionOverlay(DesignCanvas);

                // 隐藏连接点
                _connectionService?.HideAllConnectionPoints();

                // 强制更新布局
                DesignCanvas.UpdateLayout();
                _deviceNameCounters.Clear();
                _deviceDisplayNames.Clear();

                Debug.WriteLine("画布清空完成");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清空画布失败: {ex.Message}");
            }
        }
        /// <summary>
        /// 清空所有监控卡片
        /// </summary>
        private void ClearAllMonitoringCards()
        {
            var cardIds = _monitoringCards.Keys.ToList();
            foreach (var deviceId in cardIds)
            {
                RemoveMonitoringCard(deviceId);
            }
            Debug.WriteLine("所有监控卡片已清空");
        }
        /// <summary>
        /// 取消活动的连接操作
        /// </summary>
        private void CancelActiveConnectionOperations()
        {
            if (_connectionService?.IsConnectionMode == true)
            {
                _connectionService.CancelConnection();
                Debug.WriteLine("取消了进行中的连接操作");
            }
        }

        /// <summary>
        /// 清空ViewModel数据
        /// </summary>
        private void ClearViewModelData()
        {
            if (_viewModel?.CanvasDevices != null)
            {
                Debug.WriteLine($"清空 {_viewModel.CanvasDevices.Count} 个设备");
                _viewModel.CanvasDevices.Clear();
            }

            if (_viewModel?.Connections != null)
            {
                Debug.WriteLine($"清空 ViewModel 中的 {_viewModel.Connections.Count} 个连接");
                _viewModel.Connections.Clear();
            }
        }

        /// <summary>
        /// 清空连接服务
        /// </summary>
        private void ClearConnectionService()
        {
            if (_connectionService?.Connections != null)
            {
                var connectionIds = _connectionService.Connections.Keys.ToList();
                Debug.WriteLine($"清空连接服务中的 {connectionIds.Count} 个连接");

                foreach (var connectionId in connectionIds)
                {
                    _connectionService.RemoveConnection(connectionId);
                }
            }
        }

        /// <summary>
        /// 清空画布视觉元素
        /// </summary>
        private void ClearCanvasVisualElements()
        {
            var elementsToRemove = DesignCanvas.Children
                .OfType<Border>()
                .Where(border => border.Tag != null && !string.IsNullOrEmpty(border.Tag.ToString()))
                .ToList();

            Debug.WriteLine($"从画布直接移除 {elementsToRemove.Count} 个设备元素");

            foreach (var element in elementsToRemove)
            {
                DesignCanvas.Children.Remove(element);
            }
        }

        /// <summary>
        /// 获取当前画布数据请求处理
        /// </summary>
        private void OnGetCurrentCanvasDataRequest(Action<CanvasDocument> callback)
        {
            try
            {
                var canvasData = CreateCanvasDocument();
                CollectDeviceData(canvasData);
                CollectConnectionData(canvasData);

                callback?.Invoke(canvasData);
                Debug.WriteLine($"收集画布数据完成: 设备{canvasData.Devices.Count}个, 连接{canvasData.Connections.Count}个");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"收集画布数据失败: {ex.Message}");
                callback?.Invoke(null);
            }
        }

        /// <summary>
        /// 创建画布文档
        /// </summary>
        private CanvasDocument CreateCanvasDocument()
        {
            return new CanvasDocument
            {
                Name = "当前画布",
                RegionLayout = _viewModel?.CurrentRegionLayout,
                CanvasWidth = DesignCanvas?.ActualWidth ?? 1200,
                CanvasHeight = DesignCanvas?.ActualHeight ?? 800
            };
        }

        /// <summary>
        /// 收集设备数据
        /// </summary>
        private void CollectDeviceData(CanvasDocument canvasData)
        {
            if (_viewModel?.CanvasDevices == null) return;

            foreach (var canvasDevice in _viewModel.CanvasDevices)
            {
                var (scaleX, scaleY) = GetDeviceScaleInfo(canvasDevice.DeviceId);

                var deviceData = new CanvasDeviceData
                {
                    DeviceId = canvasDevice.DeviceId,
                    DeviceName = canvasDevice.Device?.Name,
                    DeviceType = canvasDevice.Device?.GetType().Name,
                    Category = canvasDevice.Device?.Category,
                    X = canvasDevice.Position.X,
                    Y = canvasDevice.Position.Y,
                    Width = canvasDevice.Width,
                    Height = canvasDevice.Height,
                    ScaleX = scaleX,
                    ScaleY = scaleY
                };

                // 收集设备参数
                CollectDeviceParameters(canvasDevice, deviceData);

                canvasData.Devices.Add(deviceData);
                Debug.WriteLine($"保存设备: {deviceData.DeviceName}, 缩放: {scaleX:F2}x{scaleY:F2}");
            }
        }

        /// <summary>
        /// 收集设备参数
        /// </summary>
        private void CollectDeviceParameters(CanvasDeviceViewModel canvasDevice, CanvasDeviceData deviceData)
        {
            if (canvasDevice.Device?.Parameters?.Variables != null)
            {
                foreach (var param in canvasDevice.Device.Parameters.Variables)
                {
                    deviceData.Parameters[param.Name] = param.Value;
                }
            }
        }

        /// <summary>
        /// 收集连接数据
        /// </summary>
        private void CollectConnectionData(CanvasDocument canvasData)
        {
            if (_connectionService?.Connections == null) return;

            foreach (var connection in _connectionService.Connections.Values)
            {
                var connectionData = new ConnectionData
                {
                    ConnectionId = connection.Id,
                    StartDeviceId = connection.StartDeviceId,
                    EndDeviceId = connection.EndDeviceId,
                    StartDirection = connection.StartAnchor?.Direction.ToString(),
                    EndDirection = connection.EndAnchor?.Direction.ToString()
                };

                canvasData.Connections.Add(connectionData);
            }
        }

        /// <summary>
        /// 新画布创建事件处理
        /// </summary>
        private void OnNewCanvasCreated(CanvasDocument newCanvas)
        {
            try
            {
                _viewModel?.UpdateRegionLayout(newCanvas.RegionLayout);
                Debug.WriteLine($"新画布已创建: {newCanvas.Name}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理新画布创建事件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 加载画布请求处理
        /// </summary>
        private void OnLoadCanvasRequest(CanvasDocument canvasDocument)
        {
            try
            {
                Debug.WriteLine($"开始加载画布: {canvasDocument.Name}");
                Debug.WriteLine($"设备数量: {canvasDocument.Devices?.Count ?? 0}");
                Debug.WriteLine($"连接数量: {canvasDocument.Connections?.Count ?? 0}");

                // 更新画布尺寸
                UpdateCanvasSize(canvasDocument);

                // 恢复设备
                RestoreDevices(canvasDocument.Devices);

                // 延迟恢复连接确保设备元素已创建
                ScheduleConnectionRestoration(canvasDocument.Connections);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载画布失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新画布尺寸
        /// </summary>
        private void UpdateCanvasSize(CanvasDocument canvasDocument)
        {
            if (canvasDocument.CanvasWidth > 0 && canvasDocument.CanvasHeight > 0)
            {
                DesignCanvas.Width = canvasDocument.CanvasWidth;
                DesignCanvas.Height = canvasDocument.CanvasHeight;
            }
        }

        /// <summary>
        /// 安排连接恢复
        /// </summary>
        private void ScheduleConnectionRestoration(List<ConnectionData> connectionDataList)
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                DesignCanvas.UpdateLayout();

                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    RestoreConnections(connectionDataList);
                }), System.Windows.Threading.DispatcherPriority.Loaded);

            }), System.Windows.Threading.DispatcherPriority.Render);
        }

        /// <summary>
        /// 恢复设备
        /// </summary>
        private void RestoreDevices(List<CanvasDeviceData> deviceDataList)
        {
            if (deviceDataList == null || !deviceDataList.Any())
            {
                Debug.WriteLine("没有设备需要恢复");
                return;
            }

            Debug.WriteLine($"开始恢复 {deviceDataList.Count} 个设备");

            foreach (var deviceData in deviceDataList)
            {
                try
                {
                    RestoreSingleDevice(deviceData);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"恢复设备 {deviceData.DeviceName} 失败: {ex.Message}");
                }
            }

            Debug.WriteLine($"设备恢复完成，当前设备数量: {_viewModel?.CanvasDevices?.Count ?? 0}");
        }

        /// <summary>
        /// 恢复单个设备
        /// </summary>
        private void RestoreSingleDevice(CanvasDeviceData deviceData)
        {
            Debug.WriteLine($"恢复设备: {deviceData.DeviceName} ID: {deviceData.DeviceId}");

            // 重建设备实例
            var device = _canvasDataService.RestoreDeviceFromData(deviceData);
            var independentDevice = ReflectionDeviceDeepCopyService.DeepClone(device);
            if (device == null)
            {
                Debug.WriteLine($"无法恢复设备: {deviceData.DeviceName}");
                return;
            }

            // 创建CanvasDeviceViewModel
            var canvasDeviceViewModel = new CanvasDeviceViewModel
            {
                DeviceId = deviceData.DeviceId,
                Device = independentDevice,
                Position = new Point(deviceData.X, deviceData.Y),
                Width = deviceData.Width > 0 ? deviceData.Width : 80,
                Height = deviceData.Height > 0 ? deviceData.Height : 60
            };

            // 添加到ViewModel
            _viewModel?.CanvasDevices.Add(canvasDeviceViewModel);

            // 延迟恢复缩放信息
            if (deviceData.ScaleX != 1.0 || deviceData.ScaleY != 1.0)
            {
                ScheduleDeviceScaleRestoration(deviceData.DeviceId, deviceData.ScaleX, deviceData.ScaleY);
            }

            Debug.WriteLine($"设备 {deviceData.DeviceName} 恢复成功");
        }

        /// <summary>
        /// 安排设备缩放恢复
        /// </summary>
        private void ScheduleDeviceScaleRestoration(string deviceId, double scaleX, double scaleY)
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                RestoreDeviceScale(deviceId, scaleX, scaleY);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        /// 恢复设备缩放
        /// </summary>
        private void RestoreDeviceScale(string deviceId, double scaleX, double scaleY)
        {
            try
            {
                var deviceElement = FindDeviceElementInCanvas(deviceId);
                if (deviceElement == null)
                {
                    Debug.WriteLine($"恢复缩放: 未找到设备元素 {deviceId}");
                    return;
                }

                Debug.WriteLine($"恢复设备 {deviceId} 缩放: {scaleX:F2}x{scaleY:F2}");

                // 获取或创建变换
                var transformGroup = GetOrCreateTransformGroup(deviceElement);
                var deviceScale = GetOrCreateScaleTransform(transformGroup);

                // 应用缩放
                deviceScale.ScaleX = scaleX;
                deviceScale.ScaleY = scaleY;
                deviceElement.RenderTransformOrigin = new Point(0.5, 0.5);

                // 更新布局
                deviceElement.UpdateLayout();

                Debug.WriteLine($"设备 {deviceId} 缩放恢复完成");

                // 延迟更新连接线
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateConnections(deviceId);
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"恢复设备缩放失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 恢复连接
        /// </summary>
        private void RestoreConnections(List<ConnectionData> connectionDataList)
        {
            if (connectionDataList == null || !connectionDataList.Any())
            {
                Debug.WriteLine("没有连接需要恢复");
                return;
            }

            Debug.WriteLine($"开始恢复 {connectionDataList.Count} 个连接");

            // 验证设备元素
            ValidateDeviceElementsForConnections(connectionDataList);

            // 恢复每个连接
            foreach (var connectionData in connectionDataList)
            {
                try
                {
                    RestoreSingleConnection(connectionData);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"恢复连接 {connectionData.ConnectionId} 失败: {ex.Message}");
                }
            }

            Debug.WriteLine($"连接恢复完成，连接服务中有 {_connectionService?.Connections?.Count ?? 0} 个连接");
        }

        /// <summary>
        /// 验证连接的设备元素
        /// </summary>
        private void ValidateDeviceElementsForConnections(List<ConnectionData> connectionDataList)
        {
            foreach (var connectionData in connectionDataList)
            {
                var startElement = FindDeviceElementInCanvas(connectionData.StartDeviceId);
                var endElement = FindDeviceElementInCanvas(connectionData.EndDeviceId);

                Debug.WriteLine($"连接 {connectionData.ConnectionId}:");
                Debug.WriteLine($"  起始设备 {connectionData.StartDeviceId}: {(startElement != null ? "已找到" : "未找到")}");
                Debug.WriteLine($"  结束设备 {connectionData.EndDeviceId}: {(endElement != null ? "已找到" : "未找到")}");
            }
        }

        /// <summary>
        /// 恢复单个连接
        /// </summary>
        private void RestoreSingleConnection(ConnectionData connectionData)
        {
            Debug.WriteLine($"正在恢复连接: {connectionData.StartDeviceId} -> {connectionData.EndDeviceId}");

            // 验证设备元素
            var startDeviceElement = FindDeviceElementInCanvas(connectionData.StartDeviceId);
            var endDeviceElement = FindDeviceElementInCanvas(connectionData.EndDeviceId);

            if (startDeviceElement == null || endDeviceElement == null)
            {
                Debug.WriteLine("设备元素未找到，跳过连接恢复");
                return;
            }

            // 解析连接方向
            var startDirection = ParseConnectionDirection(connectionData.StartDirection, ConnectionDirection.Right);
            var endDirection = ParseConnectionDirection(connectionData.EndDirection, ConnectionDirection.Left);

            // 检查连接是否已存在
            var existingConnection = FindExistingConnection(connectionData);
            if (existingConnection != null)
            {
                UpdateExistingConnection(existingConnection, startDeviceElement, endDeviceElement);
                return;
            }

            // 创建新连接
            CreateNewConnection(connectionData, startDirection, endDirection, startDeviceElement, endDeviceElement);
        }

        /// <summary>
        /// 解析连接方向
        /// </summary>
        private ConnectionDirection ParseConnectionDirection(string direction, ConnectionDirection defaultDirection)
        {
            if (Enum.TryParse<ConnectionDirection>(direction, out var parsedDirection))
            {
                return parsedDirection;
            }
            return defaultDirection;
        }

        /// <summary>
        /// 查找已存在的连接
        /// </summary>
        private ConnectionInfo FindExistingConnection(ConnectionData connectionData)
        {
            return _connectionService.Connections.Values
                .FirstOrDefault(c => c.StartDeviceId == connectionData.StartDeviceId &&
                                   c.EndDeviceId == connectionData.EndDeviceId);
        }

        /// <summary>
        /// 更新已存在的连接
        /// </summary>
        private void UpdateExistingConnection(ConnectionInfo existingConnection, FrameworkElement startElement, FrameworkElement endElement)
        {
            Debug.WriteLine($"连接已存在，更新设备引用: {existingConnection.Id}");

            existingConnection.StartDevice = startElement;
            existingConnection.EndDevice = endElement;
            _connectionService.UpdateConnectionPath(existingConnection);
        }

        /// <summary>
        /// 创建新连接
        /// </summary>
        private void CreateNewConnection(ConnectionData connectionData, ConnectionDirection startDirection, ConnectionDirection endDirection, FrameworkElement startElement, FrameworkElement endElement)
        {
            var connection = _connectionService.CreateConnection(
                connectionData.StartDeviceId,
                connectionData.EndDeviceId,
                startDirection,
                endDirection);

            if (connection != null)
            {
                Debug.WriteLine($"连接创建成功: {connection.Id}");

                // 设置设备元素引用
                connection.StartDevice = startElement;
                connection.EndDevice = endElement;

                // 添加到ViewModel
                if (!_viewModel.Connections.Any(c => c.Id == connection.Id))
                {
                    _viewModel?.Connections.Add(connection);
                    Debug.WriteLine("连接已添加到 ViewModel");
                }

                // 更新连接路径
                _connectionService.UpdateConnectionPath(connection);

                Debug.WriteLine($"连接 {connectionData.ConnectionId} 恢复成功");
            }
            else
            {
                Debug.WriteLine($"连接创建失败: {connectionData.ConnectionId}");
            }
        }

        /// <summary>
        /// 画布加载完成事件处理
        /// </summary>
        private void OnCanvasLoaded(CanvasDocument canvasDocument)
        {
            try
            {
                Debug.WriteLine($"画布加载完成: {canvasDocument.Name}");
                Debug.WriteLine($"最终设备数量: {_viewModel?.CanvasDevices?.Count ?? 0}");
                Debug.WriteLine($"最终连接数量: {_connectionService?.Connections?.Count ?? 0}");

                // 确保连接服务状态正确
                EnsureConnectionServiceState();

                // 强制更新布局
                DesignCanvas.UpdateLayout();

                // 最终确定连接引用
                ScheduleFinalConnectionUpdate();

                // 更新区域覆盖服务上下文
                UpdateRegionOverlayContext();

                Debug.WriteLine("画布加载处理完成");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"画布加载完成处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 确保连接服务状态正确
        /// </summary>
        private void EnsureConnectionServiceState()
        {
            if (_connectionService?.IsConnectionMode == true)
            {
                _connectionService.CancelConnection();
            }
            _connectionService?.HideAllConnectionPoints();
        }

        /// <summary>
        /// 安排最终连接更新
        /// </summary>
        private void ScheduleFinalConnectionUpdate()
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                FinalizeConnectionReferences();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// 最终确定连接引用
        /// </summary>
        private void FinalizeConnectionReferences()
        {
            if (_connectionService?.Connections == null)
            {
                Debug.WriteLine("FinalizeConnectionReferences: 连接服务无效");
                return;
            }

            Debug.WriteLine("开始最终确定连接引用");

            foreach (var connection in _connectionService.Connections.Values)
            {
                try
                {
                    FinalizeConnectionReference(connection);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"最终确定连接 {connection.Id} 失败: {ex.Message}");
                }
            }

            Debug.WriteLine("FinalizeConnectionReferences: 完成");
        }

        /// <summary>
        /// 最终确定单个连接引用
        /// </summary>
        private void FinalizeConnectionReference(ConnectionInfo connection)
        {
            var startElement = FindDeviceElementInCanvas(connection.StartDeviceId);
            var endElement = FindDeviceElementInCanvas(connection.EndDeviceId);

            if (startElement != null && endElement != null)
            {
                connection.StartDevice = startElement;
                connection.EndDevice = endElement;

                Debug.WriteLine($"最终确定连接 {connection.Id}:");
                Debug.WriteLine($"  起始设备: {connection.StartDeviceId} -> 元素已找到");
                Debug.WriteLine($"  结束设备: {connection.EndDeviceId} -> 元素已找到");

                // 强制更新布局确保缩放变换生效
                startElement.UpdateLayout();
                endElement.UpdateLayout();

                _connectionService.UpdateConnectionPath(connection);
            }
            else
            {
                Debug.WriteLine($"连接 {connection.Id} 的设备元素未完全找到:");
                Debug.WriteLine($"  起始设备: {(startElement != null ? "已找到" : "未找到")}");
                Debug.WriteLine($"  结束设备: {(endElement != null ? "已找到" : "未找到")}");
            }
        }

        #endregion

        #region 区域覆盖服务相关

        /// <summary>
        /// 设置区域覆盖服务的上下文
        /// </summary>
        private void SetRegionOverlayContext()
        {
            try
            {
                var currentDocument = GetCurrentDocument();
                var variablePanels = GetVariablePanels();
                var connections = _viewModel?.Connections?.Cast<object>().ToList() ?? new List<object>();

                // 传入连接服务实例
                _regionOverlayService.SetCurrentContext(
                    currentDocument,
                    DesignCanvas,
                    variablePanels,
                    connections,
                    _connectionService);

                Debug.WriteLine("RegionOverlayService 上下文设置完成");

                // 调试连接服务信息
                LogConnectionServiceInfo();

                // 更新区域覆盖
                if (_viewModel?.CurrentRegionLayout != null)
                {
                    _regionOverlayService.UpdateRegionOverlay(
                        DesignCanvas,
                        _viewModel.CurrentRegionLayout,
                        1.0);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"设置RegionOverlayService上下文失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 记录连接服务信息
        /// </summary>
        private void LogConnectionServiceInfo()
        {
            if (_connectionService?.Connections != null)
            {
                Debug.WriteLine($"当前连接服务中有 {_connectionService.Connections.Count} 个连接");
                foreach (var connection in _connectionService.Connections.Values)
                {
                    Debug.WriteLine($"  连接: {connection.Id} ({connection.StartDeviceId} -> {connection.EndDeviceId})");
                }
            }
        }

        /// <summary>
        /// 获取当前文档
        /// </summary>
        private object GetCurrentDocument()
        {
            var tempDocument = new
            {
                CanvasRegions = _viewModel?.CurrentRegionLayout ?? CreateDefaultRegionLayout()
            };

            Debug.WriteLine("使用临时文档对象");
            return tempDocument;
        }

        /// <summary>
        /// 获取变量面板
        /// </summary>
        private Dictionary<string, Border> GetVariablePanels()
        {
            var variablePanels = new Dictionary<string, Border>();

            // 从画布中查找变量面板
            foreach (var child in DesignCanvas.Children.OfType<Border>())
            {
                if (IsVariablePanel(child))
                {
                    string panelId = child.Tag?.ToString() ?? Guid.NewGuid().ToString();
                    variablePanels[panelId] = child;
                }
            }

            Debug.WriteLine($"找到 {variablePanels.Count} 个变量面板");
            return variablePanels;
        }

        /// <summary>
        /// 判断是否为变量面板
        /// </summary>
        private bool IsVariablePanel(Border border)
        {
            var tag = border.Tag?.ToString();
            return tag != null && tag.StartsWith("VariablePanel_");
        }

        /// <summary>
        /// 创建默认区域布局
        /// </summary>
        private RegionLayout CreateDefaultRegionLayout()
        {
            var layout = new RegionLayout
            {
                UseIndependentRegions = true
            };

            var canvasWidth = DesignCanvas.Width > 0 ? DesignCanvas.Width : 1200;
            var canvasHeight = DesignCanvas.Height > 0 ? DesignCanvas.Height : 800;

            // 添加默认区域
            layout.IndependentRegions = new List<IndependentRegion>
            {
                new IndependentRegion
                {
                    Type = RegionType.Reaction,
                    Left = 0,
                    Top = 0,
                    Right = canvasWidth,
                    Bottom = canvasHeight
                }
            };

            return layout;
        }

        /// <summary>
        /// 更新区域覆盖上下文
        /// </summary>
        private void UpdateRegionOverlayContext()
        {
            try
            {
                var currentDocument = GetCurrentDocument();
                var variablePanels = GetVariablePanels();
                var connections = _viewModel?.Connections?.Cast<object>().ToList() ?? new List<object>();

                _regionOverlayService.SetCurrentContext(
                    currentDocument,
                    DesignCanvas,
                    variablePanels,
                    connections,
                    _connectionService);

                Debug.WriteLine($"更新上下文后，连接服务中有 {_connectionService?.Connections?.Count ?? 0} 个连接");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新RegionOverlayService上下文失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 区域布局更新事件处理
        /// </summary>
        private void OnRegionLayoutUpdated(RegionLayout newLayout)
        {
            try
            {
                // 更新上下文中的布局信息
                var currentDocument = GetCurrentDocument();
                var variablePanels = GetVariablePanels();
                var connections = _viewModel?.Connections?.Cast<object>().ToList() ?? new List<object>();

                _regionOverlayService.SetCurrentContext(
                    currentDocument,
                    DesignCanvas,
                    variablePanels,
                    connections);

                _regionOverlayService?.UpdateRegionOverlay(DesignCanvas, newLayout);
                _viewModel?.UpdateRegionLayout(newLayout);

                // 只有画布有内容时才标记已修改
                if (HasCanvasContent())
                {
                    _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新区域布局失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 检查画布是否有内容
        /// </summary>
        private bool HasCanvasContent()
        {
            return _viewModel?.CanvasDevices?.Count > 0 || _viewModel?.Connections?.Count > 0;
        }

        /// <summary>
        /// 获取画布尺寸请求处理
        /// </summary>
        private void OnGetCanvasSizeRequested(Action<double, double> callback)
        {
            try
            {
                double width = DesignCanvas?.ActualWidth > 0 ? DesignCanvas.ActualWidth : 1200;
                double height = DesignCanvas?.ActualHeight > 0 ? DesignCanvas.ActualHeight : 800;
                callback?.Invoke(width, height);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取画布尺寸失败: {ex.Message}");
                callback?.Invoke(1200, 800);
            }
        }

        #endregion

        #region 工具辅助方法

        /// <summary>
        /// 查找设备元素在画布中
        /// </summary>
        private FrameworkElement FindDeviceElementInCanvas(string deviceId)
        {
            Debug.WriteLine($"查找设备元素: {deviceId}");

            if (string.IsNullOrEmpty(deviceId))
            {
                Debug.WriteLine("设备ID为空");
                return null;
            }

            // 直接从画布查找
            var element = DesignCanvas.Children
                .OfType<FrameworkElement>()
                .FirstOrDefault(e => e.Tag as string == deviceId);

            if (element != null)
            {
                Debug.WriteLine($"在画布中找到设备元素: {element.GetType().Name}, Tag: {element.Tag}");
                return element;
            }

            Debug.WriteLine($"画布中未找到设备 {deviceId}");
            LogCanvasChildren();

            return null;
        }

        /// <summary>
        /// 记录画布子元素信息
        /// </summary>
        private void LogCanvasChildren()
        {
            Debug.WriteLine($"画布中当前元素数量: {DesignCanvas.Children.Count}");
            foreach (var child in DesignCanvas.Children.OfType<FrameworkElement>())
            {
                Debug.WriteLine($"  - {child.GetType().Name}, Tag: {child.Tag ?? "null"}");
            }
        }

        /// <summary>
        /// 查找设备控件
        /// </summary>
        private FrameworkElement FindDeviceControl(FrameworkElement element)
        {
            while (element != null)
            {
                if (element.Tag is string)
                {
                    return element;
                }
                element = element.Parent as FrameworkElement;
            }
            return null;
        }

        /// <summary>
        /// 查找设备元素
        /// </summary>
        private FrameworkElement FindDeviceElement(string deviceId)
        {
            return DesignCanvas.Children
                .OfType<FrameworkElement>()
                .FirstOrDefault(e => e.Tag as string == deviceId);
        }

        /// <summary>
        /// 检查是否为有效拖放位置
        /// </summary>
        private bool IsValidDropPosition(Point position)
        {
            return position.X >= 0 && position.Y >= 0 &&
                   position.X <= DesignCanvas.ActualWidth &&
                   position.Y <= DesignCanvas.ActualHeight;
        }

        /// <summary>
        /// 查找父级控件
        /// </summary>
        private T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            var parent = VisualTreeHelper.GetParent(child);
            if (parent == null) return null;
            if (parent is T) return parent as T;
            return FindParent<T>(parent);
        }

        #endregion
    }
}