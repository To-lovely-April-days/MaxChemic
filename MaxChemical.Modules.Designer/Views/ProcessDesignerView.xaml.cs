using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.Designer.Commands;
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Service;
using MaxChemical.Modules.Designer.Services;
using MaxChemical.Modules.Designer.ViewModels;
using MaxChemical.Modules.Designer.Views.Controls;
using Prism.Events;

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
        private readonly ICanvasDataService _canvasDataService;
        private readonly ICommandHistory _commandHistory;
        private ProcessDesignerViewModel _viewModel;
        private readonly IPipeConnectionService _pipeConnectionService;
        // A. 设备显示名管理
        private readonly Dictionary<string, int> _deviceNameCounters = new();      // 基础名 -> 累计序号
        private readonly Dictionary<string, string> _deviceDisplayNames = new();    // DeviceId -> 显示名（泵#2）

        // B. 执行动画管理
        private readonly Dictionary<string, Storyboard> _runningAnimations = new();

        private readonly IDeviceManager _deviceManager;
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


        private bool _isDraggingPipe = false;           // 是否正在拖拽管道
        private StraightPipeControl _currentDraggingPipe = null; // 当前拖拽的管道
        private PipeAnchorPosition? _draggingAnchor = null;      // 当前拖拽的锚点
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

        private readonly IPipeSnapService _pipeSnapService;
        private bool _isPipeSnapping = false; // 是否正在吸附
        private SnapResult _currentSnapResult = null; // 当前吸附结果
        private PipeSnapSolution _currentSnapSolution = null; // 当前吸附方案


        #region 私有字段 管道管理

        // 管道集合
        private readonly Dictionary<string, FrameworkElement> _pipes = new();

        // 选中的管道
        private string _selectedPipeId = null;
        private FrameworkElement _selectedPipeElement = null;

        // 管道拖拽原始位置（用于撤销）
        private Point _pipeOriginalPosition;

        #endregion

        #region 管道选择和删除

        /// <summary>
        /// 取消管道选择
        /// </summary>
        private void ClearPipeSelection()
        {
            if (_selectedPipeElement != null)
            {
                RemovePipeSelectionVisual(_selectedPipeElement);
                _selectedPipeId = null;
                _selectedPipeElement = null;
            }
        }


        /// <summary>
        /// 移除管道选中视觉反馈
        /// </summary>
        private void RemovePipeSelectionVisual(FrameworkElement pipe)
        {
            if (pipe is StraightPipeControl straightPipe)
            {
                straightPipe.IsSelected = false;
            }
            else if (pipe is ElbowPipeControl elbowPipe)
            {
                elbowPipe.IsSelected = false;
            }
            else if (pipe is ZShapePipeControl zPipe)
            {
                zPipe.IsSelected = false;
            }
        }

        /// <summary>
        /// 删除选中的管道
        /// </summary>
        private void DeleteSelectedPipe()
        {
            if (string.IsNullOrEmpty(_selectedPipeId) || _selectedPipeElement == null)
            {
                Debug.WriteLine("没有选中的管道可删除");
                return;
            }

            try
            {
                Debug.WriteLine($"删除选中的管道: {_selectedPipeId}");

                // 使用命令删除管道
                var deleteCommand = new DeletePipeCommand(
                    DesignCanvas,
                    _selectedPipeElement,
                    _pipeConnectionService);

                _commandHistory.ExecuteCommand(deleteCommand);

                // 从管道集合中移除
                _pipes.Remove(_selectedPipeId);

                // 清除选择
                _selectedPipeId = null;
                _selectedPipeElement = null;

                // 标记画布已修改
                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("管道已删除");

                // ✅ 新增：管道删除完成，标记四叉树需要重建
                if (_pipeSnapService is PipeSnapService snapService)
                {
                    snapService.MarkQuadTreeDirty();
                    Debug.WriteLine("管道删除完成，已标记四叉树需要重建");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"删除管道失败: {ex.Message}");
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"删除管道失败: {ex.Message}");
            }
        }

        #endregion

        #region 私有字段 节流相关

        private DateTime _lastMouseMoveTime = DateTime.MinValue;
        private Point _lastMousePosition = new Point();
        private const double MouseMoveThrottleMs = 16; // ~60FPS
        private const double MouseMoveMinDistance = 2.0; // 最小移动距离（像素）

        #endregion

        #region 私有字段 布局更新优化

        private System.Windows.Threading.DispatcherTimer _layoutUpdateTimer;
        private bool _needsLayoutUpdate = false;

        #endregion

        #region 构造函数和初始化

        public ProcessDesignerView(
            IEventAggregator eventAggregator,
            IRegionOverlayService regionOverlayService,
            IDragDropService dragDropService,
            ICanvasDataService canvasDataService,
            ICommandHistory commandHistory,
            IPipeConnectionService pipeConnectionService,
            IPipeSnapService pipeSnapService,
            IDeviceManager deviceManager)
        {
            // 依赖注入验证
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _regionOverlayService = regionOverlayService ?? throw new ArgumentNullException(nameof(regionOverlayService));
            _dragDropService = dragDropService ?? throw new ArgumentNullException(nameof(dragDropService));
            _canvasDataService = canvasDataService ?? throw new ArgumentNullException(nameof(canvasDataService));
            _commandHistory = commandHistory ?? throw new ArgumentNullException(nameof(commandHistory));
            _pipeConnectionService = pipeConnectionService ?? throw new ArgumentNullException(nameof(pipeConnectionService));
            _pipeSnapService = pipeSnapService ?? throw new ArgumentNullException(nameof(pipeSnapService));
            _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager)); // ✅ 新增

            // 初始化界面组件
            InitializeComponent();
            // ✅ 新增：初始化布局更新定时器
            InitializeLayoutUpdateTimer();
            Debug.WriteLine("ProcessDesignerView 构造函数被调用");

            // 配置拖放功能
            ConfigureDragDropFeature();

            // 配置鼠标和键盘事件
            ConfigureMouseAndKeyboardEvents();

            // 配置管道连接服务
            ConfigurePipeConnectionService();
            // 配置生命周期事件
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;

            // ========== ✅ 硬件加速配置 ==========
            ConfigureHardwareAcceleration();
        }

        #region 硬件加速
        /// <summary>
        /// ✅ 新增：配置硬件加速
        /// </summary>
        private void ConfigureHardwareAcceleration()
        {
            try
            {
                Debug.WriteLine("========== 开始配置画布硬件加速 ==========");

                // 1. 强制启用硬件渲染
                EnableHardwareRendering();

                // 2. 配置画布级别优化
                ConfigureCanvasOptimization();

                // 3. 配置滚动容器优化
                ConfigureScrollViewerOptimization();

                // 4. 启用 GPU 合成
                EnableGPUComposition();

                // 5. 配置像素对齐
                ConfigurePixelAlignment();

                Debug.WriteLine("========== 画布硬件加速配置完成 ==========");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 硬件加速配置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 1. 强制启用硬件渲染
        /// </summary>
        private void EnableHardwareRendering()
        {
            try
            {
                // 获取窗口句柄并强制硬件渲染
                var hwndSource = PresentationSource.FromVisual(this) as HwndSource;
                if (hwndSource != null)
                {
                    hwndSource.CompositionTarget.RenderMode = RenderMode.Default; // 硬件渲染
                    Debug.WriteLine("✅ 强制启用硬件渲染");
                }
                else
                {
                    // 延迟绑定（窗口加载后）
                    this.Loaded += (s, e) =>
                    {
                        var source = PresentationSource.FromVisual(this) as HwndSource;
                        if (source != null)
                        {
                            source.CompositionTarget.RenderMode = RenderMode.Default;
                            Debug.WriteLine("✅ 延迟启用硬件渲染");
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"⚠️ 启用硬件渲染失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 2. 配置画布级别优化
        /// </summary>
        private void ConfigureCanvasOptimization()
        {
            // 高质量渲染
            RenderOptions.SetBitmapScalingMode(DesignCanvas, BitmapScalingMode.HighQuality);
            RenderOptions.SetEdgeMode(DesignCanvas, EdgeMode.Unspecified);

            // 启用缓存提示
            RenderOptions.SetCachingHint(DesignCanvas, CachingHint.Cache);
            RenderOptions.SetCacheInvalidationThresholdMinimum(DesignCanvas, 0.5);
            RenderOptions.SetCacheInvalidationThresholdMaximum(DesignCanvas, 2.0);

            // 像素对齐
            DesignCanvas.SnapsToDevicePixels = true;
            DesignCanvas.UseLayoutRounding = true;

            Debug.WriteLine("✅ 画布级别优化完成");
        }

        /// <summary>
        /// 3. 配置滚动容器优化
        /// </summary>
        private void ConfigureScrollViewerOptimization()
        {
            if (CanvasScrollViewer != null)
            {
                // 高质量渲染
                RenderOptions.SetBitmapScalingMode(CanvasScrollViewer, BitmapScalingMode.HighQuality);
                RenderOptions.SetEdgeMode(CanvasScrollViewer, EdgeMode.Unspecified);

                // 像素对齐
                CanvasScrollViewer.SnapsToDevicePixels = true;
                CanvasScrollViewer.UseLayoutRounding = true;

                Debug.WriteLine("✅ 滚动容器优化完成");
            }
        }

        /// <summary>
        /// 4. 启用 GPU 合成
        /// </summary>
        private void EnableGPUComposition()
        {
            try
            {
                // ✅ 关键：启用 GPU 分层合成
                DesignCanvas.IsVisibleChanged += (s, e) =>
                {
                    if (DesignCanvas.IsVisible)
                    {
                        // 强制 GPU 合成
                        var layer = AdornerLayer.GetAdornerLayer(DesignCanvas);
                        if (layer != null)
                        {
                            RenderOptions.SetBitmapScalingMode(layer, BitmapScalingMode.HighQuality);
                        }
                    }
                };

                Debug.WriteLine("✅ GPU 合成已启用");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"⚠️ GPU 合成启用失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 5. 配置像素对齐
        /// </summary>
        private void ConfigurePixelAlignment()
        {
            this.SnapsToDevicePixels = true;
            this.UseLayoutRounding = true;

            Debug.WriteLine("✅ 像素对齐配置完成");
        }

        #endregion

        /// <summary>
        /// 配置管道连接服务
        /// </summary>
        private void ConfigurePipeConnectionService()
        {
            _pipeConnectionService.GetCanvasFunc = () => DesignCanvas;
            _pipeConnectionService.FindDeviceElementFunc = FindDeviceElementInCanvas;
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
        #region 监控参数状态管理的事件订阅

        /// <summary>
        /// 订阅监控参数状态相关事件
        /// </summary>
        private void SubscribeToMonitoringParameterEvents()
        {
            _eventAggregator?.GetEvent<GetDeviceMonitoringSelectionsRequestEvent>()?.Subscribe(OnGetDeviceMonitoringSelectionsRequest);
            _eventAggregator?.GetEvent<GetMonitoringCardStatesRequestEvent>()?.Subscribe(OnGetMonitoringCardStatesRequest);
            _eventAggregator?.GetEvent<RestoreDeviceMonitoringSelectionsEvent>()?.Subscribe(OnRestoreDeviceMonitoringSelections);
            _eventAggregator?.GetEvent<RestoreMonitoringCardStatesEvent>()?.Subscribe(OnRestoreMonitoringCardStates);
            _eventAggregator?.GetEvent<GetDeviceNameCountersRequestEvent>()?.Subscribe(OnGetDeviceNameCountersRequest);
            _eventAggregator?.GetEvent<RestoreDeviceNameCountersEvent>()?.Subscribe(OnRestoreDeviceNameCounters);

            // *** 新增：订阅设备显示名映射事件 ***
            _eventAggregator?.GetEvent<GetDeviceDisplayNamesRequestEvent>()?.Subscribe(OnGetDeviceDisplayNamesRequest);
            _eventAggregator?.GetEvent<RestoreDeviceDisplayNamesEvent>()?.Subscribe(OnRestoreDeviceDisplayNames);

            // ✅ 新增：订阅监控卡片位置事件
            _eventAggregator?.GetEvent<GetMonitoringCardPositionsRequestEvent>()?.Subscribe(OnGetMonitoringCardPositionsRequest);
            _eventAggregator?.GetEvent<RestoreMonitoringCardPositionsEvent>()?.Subscribe(OnRestoreMonitoringCardPositions);
        }

        /// <summary>
        /// 响应获取设备监控参数选择状态请求
        /// </summary>
        private void OnGetDeviceMonitoringSelectionsRequest(Action<Dictionary<string, HashSet<string>>> callback)
        {
            try
            {
                // *** 关键修复：检查是否为完整键名格式，如果不是则转换 ***
                var processedSelections = new Dictionary<string, HashSet<string>>();

                foreach (var kvp in _deviceMonitoringSelections)
                {
                    var deviceId = kvp.Key;
                    var selections = kvp.Value;

                    // 检查选择是否为完整键名格式
                    if (IsCompleteKeyFormat(selections))
                    {
                        // 已经是完整键名格式，直接使用
                        processedSelections[deviceId] = new HashSet<string>(selections);
                        Debug.WriteLine($"设备 {deviceId} 使用完整键名格式: {string.Join(", ", selections)}");
                    }
                    else
                    {
                        // 转换为完整键名格式
                        var convertedKeys = ConvertToCompleteKeys(deviceId, selections);
                        processedSelections[deviceId] = convertedKeys;
                        Debug.WriteLine($"设备 {deviceId} 转换为完整键名格式: {string.Join(", ", convertedKeys)}");
                    }
                }

                Debug.WriteLine($"返回设备监控参数选择状态: {processedSelections.Count} 个设备");
                callback?.Invoke(processedSelections);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取设备监控参数选择状态失败: {ex.Message}");
                callback?.Invoke(new Dictionary<string, HashSet<string>>());
            }
        }
        /// <summary>
        /// 检查选择是否为完整键名格式
        /// </summary>
        private bool IsCompleteKeyFormat(HashSet<string> selections)
        {
            if (selections.Count == 0) return true;

            // 如果大部分选择包含 # 分隔符，则认为是完整键名格式
            int completeKeyCount = selections.Count(s => s.Contains("#"));
            return completeKeyCount > selections.Count / 2;
        }

        /// <summary>
        /// 将参数名转换为完整键名格式
        /// </summary>
        private HashSet<string> ConvertToCompleteKeys(string deviceId, HashSet<string> parameterNames)
        {
            var completeKeys = new HashSet<string>();

            try
            {
                // 查找设备
                var canvasDevice = _viewModel?.CanvasDevices?.FirstOrDefault(d => d.DeviceId == deviceId);
                if (canvasDevice?.Device?.Commands == null)
                {
                    Debug.WriteLine($"无法找到设备 {deviceId} 的命令信息");
                    return completeKeys;
                }

                // 为每个参数名查找所在的命令
                foreach (var parameterName in parameterNames)
                {
                    bool found = false;
                    foreach (var command in canvasDevice.Device.Commands)
                    {
                        if (command.OutputParameters?.Variables?.Any(p => p.Name == parameterName) == true)
                        {
                            var completeKey = $"{command.Name}#{parameterName}";
                            completeKeys.Add(completeKey);
                            found = true;
                            Debug.WriteLine($"转换参数: {parameterName} -> {completeKey}");
                            break; // 找到第一个匹配的命令即可
                        }
                    }

                    if (!found)
                    {
                        Debug.WriteLine($"⚠️ 警告：参数 {parameterName} 在设备 {deviceId} 中未找到对应命令");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"转换完整键名失败: {ex.Message}");
            }

            return completeKeys;
        }
        /// <summary>
        /// 响应获取监控卡片状态请求
        /// </summary>
        private void OnGetMonitoringCardStatesRequest(Action<Dictionary<string, bool>> callback)
        {
            try
            {
                var cardStates = new Dictionary<string, bool>();

                foreach (var kvp in _monitoringCards)
                {
                    cardStates[kvp.Key] = true; // 存在监控卡片表示为true
                }

                Debug.WriteLine($"返回监控卡片状态: {cardStates.Count} 个设备有监控卡片");
                callback?.Invoke(cardStates);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取监控卡片状态失败: {ex.Message}");
                callback?.Invoke(new Dictionary<string, bool>());
            }
        }

        /// <summary>
        /// 恢复设备监控参数选择状态
        /// </summary>
        private void OnRestoreDeviceMonitoringSelections(Dictionary<string, HashSet<string>> selections)
        {
            try
            {
                if (selections == null || selections.Count == 0)
                {
                    Debug.WriteLine("没有需要恢复的监控参数选择状态");
                    return;
                }

                Debug.WriteLine($"开始恢复 {selections.Count} 个设备的监控参数选择状态");

                // 清空现有状态
                _deviceMonitoringSelections.Clear();

                // 恢复选择状态
                foreach (var kvp in selections)
                {
                    if (kvp.Value != null && kvp.Value.Count > 0)
                    {
                        // *** 关键修复：支持完整键名格式恢复 ***
                        _deviceMonitoringSelections[kvp.Key] = new HashSet<string>(kvp.Value);

                        var formatType = IsCompleteKeyFormat(kvp.Value) ? "完整键名" : "参数名";
                        Debug.WriteLine($"恢复设备 {kvp.Key} 的 {kvp.Value.Count} 个监控参数选择 ({formatType}格式)");
                    }
                }

                Debug.WriteLine("设备监控参数选择状态恢复完成");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"恢复设备监控参数选择状态失败: {ex.Message}");
            }
        }


        /// <summary>
        /// 恢复监控卡片状态
        /// </summary>
        private void OnRestoreMonitoringCardStates(Dictionary<string, bool> cardStates)
        {
            try
            {
                if (cardStates == null || cardStates.Count == 0)
                {
                    Debug.WriteLine("没有需要恢复的监控卡片状态");
                    return;
                }

                Debug.WriteLine($"开始恢复 {cardStates.Count} 个设备的监控卡片状态");

                // *** 关键修复：立即尝试恢复，然后设置延迟重试机制 ***
                RestoreMonitoringCardsInternal(cardStates);

                // 延迟重试恢复，确保设备元素完全加载
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    Debug.WriteLine("延迟重试恢复监控卡片");
                    RestoreMonitoringCardsInternal(cardStates);
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

                // 再次延迟重试，确保所有元素都已就绪
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    Debug.WriteLine("最终重试恢复监控卡片");
                    RestoreMonitoringCardsInternal(cardStates);
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"恢复监控卡片状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 内部恢复监控卡片状态方法
        /// </summary>
        private void RestoreMonitoringCardsInternal(Dictionary<string, bool> cardStates)
        {
            try
            {
                int successCount = 0;
                int failureCount = 0;

                foreach (var kvp in cardStates)
                {
                    if (kvp.Value) // 需要显示监控卡片
                    {
                        var deviceId = kvp.Key;

                        try
                        {
                            if (RestoreSingleMonitoringCard(deviceId))
                            {
                                successCount++;
                            }
                            else
                            {
                                failureCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"恢复设备 {deviceId} 的监控卡片失败: {ex.Message}");
                            failureCount++;
                        }
                    }
                }

                Debug.WriteLine($"监控卡片恢复完成: 成功 {successCount} 个，失败 {failureCount} 个");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"内部恢复监控卡片状态失败: {ex.Message}");
            }
        }
        /// <summary>
        /// 恢复单个监控卡片 - 新方法
        /// </summary>
        private bool RestoreSingleMonitoringCard(string deviceId)
        {
            try
            {
                // 检查是否已经存在监控卡片
                if (_monitoringCards.ContainsKey(deviceId))
                {
                    Debug.WriteLine($"设备 {deviceId} 的监控卡片已存在，跳过恢复");
                    return true;
                }

                // 检查设备是否存在
                var canvasDevice = _viewModel?.CanvasDevices?.FirstOrDefault(d => d.DeviceId == deviceId);
                if (canvasDevice == null)
                {
                    Debug.WriteLine($"设备 {deviceId} 不存在，跳过监控卡片恢复");
                    return false;
                }

                // 检查设备元素是否存在
                var deviceElement = FindDeviceElementInCanvas(deviceId);
                if (deviceElement == null)
                {
                    Debug.WriteLine($"设备 {deviceId} 的视觉元素不存在，跳过监控卡片恢复");
                    return false;
                }

                // 获取监控参数选择状态
                var selectedParameterKeys = GetDeviceMonitoringSelections(deviceId);
                if (selectedParameterKeys.Count == 0)
                {
                    Debug.WriteLine($"设备 {deviceId} 没有选中的监控参数，跳过监控卡片恢复");
                    return false;
                }

                // *** 关键修复：根据选择的键格式查找对应的参数 ***
                var selectedParameters = FindParametersFromKeys(canvasDevice.Device, selectedParameterKeys);
                if (selectedParameters.Count == 0)
                {
                    Debug.WriteLine($"设备 {deviceId} 的选中参数未找到对应的参数对象");
                    return false;
                }

                // 创建监控卡片
                var displayName = GetDeviceDisplayName(deviceId);
                CreateOrUpdateMonitoringCard(deviceId, displayName, selectedParameters);

                Debug.WriteLine($"成功恢复设备 {deviceId} 的监控卡片，包含 {selectedParameters.Count} 个参数");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"恢复单个监控卡片失败: {ex.Message}");
                return false;
            }
        }
        /// <summary>
        /// 根据键格式查找参数对象 - 新方法，支持完整键名和参数名两种格式
        /// </summary>
        private List<ParameterBase> FindParametersFromKeys(IDevice device, HashSet<string> parameterKeys)
        {
            var selectedParameters = new List<ParameterBase>();

            try
            {
                if (device?.Commands?.Any() != true)
                {
                    Debug.WriteLine("设备没有命令，无法查找参数");
                    return selectedParameters;
                }

                // 判断是完整键名格式还是参数名格式
                bool isCompleteKeyFormat = IsCompleteKeyFormat(parameterKeys);
                Debug.WriteLine($"参数键格式: {(isCompleteKeyFormat ? "完整键名" : "参数名")}");

                if (isCompleteKeyFormat)
                {
                    // 完整键名格式：命令名#参数名
                    selectedParameters = FindParametersFromCompleteKeys(device, parameterKeys);
                }
                else
                {
                    // 参数名格式：直接参数名
                    selectedParameters = FindParametersFromParameterNames(device, parameterKeys);
                }

                Debug.WriteLine($"找到 {selectedParameters.Count} 个匹配的参数对象");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"查找参数对象失败: {ex.Message}");
            }

            return selectedParameters;
        }
        /// <summary>
        /// 从完整键名查找参数对象
        /// </summary>
        private List<ParameterBase> FindParametersFromCompleteKeys(IDevice device, HashSet<string> completeKeys)
        {
            var selectedParameters = new List<ParameterBase>();

            foreach (var key in completeKeys)
            {
                try
                {
                    var parts = key.Split('#');
                    if (parts.Length != 2) continue;

                    var commandName = parts[0];
                    var parameterName = parts[1];

                    var command = device.Commands.FirstOrDefault(c => c.Name == commandName);
                    if (command?.OutputParameters?.Variables?.Any() == true)
                    {
                        var parameter = command.OutputParameters.Variables
                            .FirstOrDefault(p => p.Name == parameterName);

                        if (parameter != null)
                        {
                            // *** 关键修复：直接添加，不去重 ***
                            selectedParameters.Add(parameter);
                            Debug.WriteLine($"找到参数: {key}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"处理键名 {key} 失败: {ex.Message}");
                }
            }

            Debug.WriteLine($"总共找到 {selectedParameters.Count} 个参数");
            return selectedParameters;
        }

        /// <summary>
        /// 从参数名查找参数对象
        /// </summary>
        private List<ParameterBase> FindParametersFromParameterNames(IDevice device, HashSet<string> parameterNames)
        {
            var selectedParameters = new List<ParameterBase>();

            // *** 关键修复：遍历所有命令，找到所有匹配的参数 ***
            foreach (var command in device.Commands)
            {
                if (command.OutputParameters?.Variables?.Any() == true)
                {
                    foreach (var param in command.OutputParameters.Variables)
                    {
                        if (parameterNames.Contains(param.Name))
                        {
                            selectedParameters.Add(param);
                            Debug.WriteLine($"找到参数: {param.Name} (命令: {command.Name})");
                        }
                    }
                }
            }

            Debug.WriteLine($"总共找到 {selectedParameters.Count} 个参数");
            return selectedParameters;
        }
        /// <summary>
        /// 根据参数名称查找设备的输出参数
        /// </summary>
        private List<ParameterBase> FindDeviceOutputParameters(IDevice device, HashSet<string> parameterNames)
        {
            var selectedParameters = new List<ParameterBase>();

            try
            {
                if (device?.Commands?.Any() == true)
                {
                    foreach (var command in device.Commands)
                    {
                        if (command.OutputParameters?.Variables?.Any() == true)
                        {
                            foreach (var param in command.OutputParameters.Variables)
                            {
                                if (parameterNames.Contains(param.Name) &&
                                    !selectedParameters.Any(p => p.Name == param.Name))
                                {
                                    selectedParameters.Add(param);
                                }
                            }
                        }
                    }
                }

                Debug.WriteLine($"找到 {selectedParameters.Count} 个匹配的输出参数");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"查找设备输出参数失败: {ex.Message}");
            }

            return selectedParameters;
        }

        /// <summary>
        /// 响应获取设备名称计数器请求
        /// </summary>
        private void OnGetDeviceNameCountersRequest(Action<Dictionary<string, int>> callback)
        {
            try
            {
                // 返回一份拷贝
                var copyCounters = new Dictionary<string, int>(_deviceNameCounters);
                Debug.WriteLine($"返回设备名称计数器: {copyCounters.Count} 个类型");
                callback?.Invoke(copyCounters);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取设备名称计数器失败: {ex.Message}");
                callback?.Invoke(new Dictionary<string, int>());
            }
        }

        /// <summary>
        /// 恢复设备名称计数器
        /// </summary>
        private void OnRestoreDeviceNameCounters(Dictionary<string, int> counters)
        {
            try
            {
                if (counters == null || counters.Count == 0)
                {
                    Debug.WriteLine("没有需要恢复的设备名称计数器");
                    return;
                }

                Debug.WriteLine($"开始恢复 {counters.Count} 个设备名称计数器");

                // 清空现有计数器
                _deviceNameCounters.Clear();

                // 恢复计数器
                foreach (var kvp in counters)
                {
                    _deviceNameCounters[kvp.Key] = kvp.Value;
                    Debug.WriteLine($"恢复计数器: {kvp.Key} = {kvp.Value}");
                }

                Debug.WriteLine("设备名称计数器恢复完成");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"恢复设备名称计数器失败: {ex.Message}");
            }
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


            // *** 新增：订阅监控参数状态事件 ***
            SubscribeToMonitoringParameterEvents();

            // ✅ 新增:订阅管道工具箱事件
            SubscribeToPipeToolboxEvents();
            //初始化吸附服务
            _pipeSnapService.Initialize(DesignCanvas);

            // ✅ 新增: 订阅语音创建设备事件
            SubscribeToVoiceAssistantEvents();

            // ✅ 新增：标记四叉树需要初始化
            (_pipeSnapService as PipeSnapService)?.MarkQuadTreeDirty();
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


        private void OnGetDeviceDisplayNamesRequest(Action<Dictionary<string, string>> callback)
        {
            try
            {
                // 返回一份拷贝，避免外部修改
                callback?.Invoke(new Dictionary<string, string>(_deviceDisplayNames));
                Debug.WriteLine($"返回设备显示名映射: {_deviceDisplayNames.Count} 个设备");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"返回设备显示名映射失败: {ex.Message}");
                callback?.Invoke(new Dictionary<string, string>());
            }
        }
        private void OnRestoreDeviceDisplayNames(Dictionary<string, string> displayNames)
        {
            try
            {
                if (displayNames == null || displayNames.Count == 0)
                {
                    Debug.WriteLine("没有需要恢复的设备显示名映射");
                    return;
                }

                Debug.WriteLine($"开始恢复 {displayNames.Count} 个设备的显示名映射");

                // 清空现有映射
                _deviceDisplayNames.Clear();

                // 恢复映射
                foreach (var kvp in displayNames)
                {
                    _deviceDisplayNames[kvp.Key] = kvp.Value;
                    Debug.WriteLine($"恢复显示名映射: {kvp.Key} = {kvp.Value}");
                }

                Debug.WriteLine("设备显示名映射恢复完成");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"恢复设备显示名映射失败: {ex.Message}");
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
            UnsubscribeFromKeyboardEvents();
            UnsubscribeFromPipeToolboxEvents(); // ✅ 新增
                                                // ✅ 新增: 取消订阅语音助手事件
            _eventAggregator?.GetEvent<VoiceCreateDeviceEvent>()?.Unsubscribe(OnVoiceCreateDevice);
            // ✅ 新增：停止布局更新定时器
            _layoutUpdateTimer?.Stop();
        }
        /// <summary>
        /// 订阅管道工具箱事件
        /// </summary>
        private void SubscribeToPipeToolboxEvents()
        {
            if (PipeToolbox != null)
            {
                PipeToolbox.PipeDragStarted += OnToolboxPipeDragStarted; // ✅ 改名
                Debug.WriteLine("管道工具箱事件已订阅");
            }
        }

        /// <summary>
        /// 取消订阅管道工具箱事件
        /// </summary>
        private void UnsubscribeFromPipeToolboxEvents()
        {
            if (PipeToolbox != null)
            {
                PipeToolbox.PipeDragStarted -= OnToolboxPipeDragStarted; // ✅ 改名
            }
        }

        /// <summary>
        /// 工具箱管道拖拽开始事件（与画布上管道的拖拽事件分开）
        /// </summary>
        private void OnToolboxPipeDragStarted(object sender, PipeDragStartEventArgs e)
        {
            try
            {
                Debug.WriteLine($"工具箱管道拖拽开始: {e.PipeType}");
                // 这里主要是标记，实际拖拽由 OnDragEnter/OnDrop 处理
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"工具箱管道拖拽开始处理失败: {ex.Message}");
            }
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
            _eventAggregator?.GetEvent<RequestCanvasDevicesEvent>()?.Unsubscribe(OnRequestCanvasDevices);
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
            // ✅ 新增：订阅获取选中管道请求事件
            _eventAggregator?.GetEvent<GetSelectedPipeRequestEvent>()?.Subscribe(OnGetSelectedPipeRequest);

            // ✅ 新增：订阅删除选中管道事件
            _eventAggregator?.GetEvent<DeleteSelectedPipeEvent>()?.Subscribe(OnDeleteSelectedPipe);
            // ✅ 新增：订阅管道数据收集和恢复事件
            _eventAggregator?.GetEvent<CollectPipeDataRequestEvent>()?.Subscribe(OnCollectPipeDataRequest);

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

            // *** 新增：取消订阅监控参数状态事件 ***
            _eventAggregator?.GetEvent<GetDeviceMonitoringSelectionsRequestEvent>()?.Unsubscribe(OnGetDeviceMonitoringSelectionsRequest);
            _eventAggregator?.GetEvent<GetMonitoringCardStatesRequestEvent>()?.Unsubscribe(OnGetMonitoringCardStatesRequest);
            _eventAggregator?.GetEvent<RestoreDeviceMonitoringSelectionsEvent>()?.Unsubscribe(OnRestoreDeviceMonitoringSelections);
            _eventAggregator?.GetEvent<RestoreMonitoringCardStatesEvent>()?.Unsubscribe(OnRestoreMonitoringCardStates);
            _eventAggregator?.GetEvent<GetDeviceNameCountersRequestEvent>()?.Unsubscribe(OnGetDeviceNameCountersRequest);
            _eventAggregator?.GetEvent<RestoreDeviceNameCountersEvent>()?.Unsubscribe(OnRestoreDeviceNameCounters);

            // ✅ 新增：取消订阅
            _eventAggregator?.GetEvent<GetSelectedPipeRequestEvent>()?.Unsubscribe(OnGetSelectedPipeRequest);
            _eventAggregator?.GetEvent<DeleteSelectedPipeEvent>()?.Unsubscribe(OnDeleteSelectedPipe);

            // ✅ 新增：取消订阅
            _eventAggregator?.GetEvent<CollectPipeDataRequestEvent>()?.Unsubscribe(OnCollectPipeDataRequest);


            // ✅ 新增：取消订阅监控卡片位置事件
            _eventAggregator?.GetEvent<GetMonitoringCardPositionsRequestEvent>()?.Unsubscribe(OnGetMonitoringCardPositionsRequest);
            _eventAggregator?.GetEvent<RestoreMonitoringCardPositionsEvent>()?.Unsubscribe(OnRestoreMonitoringCardPositions);
        }
        /// <summary>
        /// 响应获取选中管道请求
        /// </summary>
        private void OnGetSelectedPipeRequest(Action<string> callback)
        {
            try
            {
                callback?.Invoke(_selectedPipeId);
                Debug.WriteLine($"返回当前选中管道: {_selectedPipeId ?? "无"}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"返回选中管道失败: {ex.Message}");
                callback?.Invoke(null);
            }
        }

        /// <summary>
        /// 响应删除选中管道事件
        /// </summary>
        private void OnDeleteSelectedPipe(string pipeId)
        {
            try
            {
                Debug.WriteLine($"收到删除管道请求: {pipeId}");

                if (string.IsNullOrEmpty(pipeId))
                {
                    Debug.WriteLine("管道ID为空，取消删除");
                    return;
                }

                // ✅ 调用已有的删除方法
                if (pipeId == _selectedPipeId)
                {
                    DeleteSelectedPipe();
                }
                else
                {
                    Debug.WriteLine("管道ID不匹配当前选中的管道");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"删除管道失败: {ex.Message}");
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"删除管道失败: {ex.Message}");
            }
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
                        _isShiftPressed = true;
                        Debug.WriteLine("Shift键按下");
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
                    _isShiftPressed = false;
                    Debug.WriteLine("Shift键释放");
                    break;

                case Key.LeftCtrl:
                case Key.RightCtrl:
                    HandleCtrlKeyUp();
                    break;
            }
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
            else if (!string.IsNullOrEmpty(_selectedPipeId))
            {
                // ✅ 新增：有选中管道时删除管道
                Debug.WriteLine($"通过Delete键删除选中管道: {_selectedPipeId}");
                DeleteSelectedPipe();
            }
            else
            {
                // 没有选中设备或管道时删除选中的连接线
                Debug.WriteLine("通过Delete键删除选中的连接线");
            }
        }

        /// <summary>
        /// 处理Escape键 取消连接模式
        /// </summary>
        private void HandleEscapeKey()
        {
            Debug.WriteLine("Escape键按下");
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

                // ✅ 新增：调度延迟布局更新
                ScheduleLayoutUpdate();

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
        private void ReleaseMouseCaptures()
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
                var deleteCommand = new DeleteDeviceCommand(_viewModel, deviceToDelete);
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
                // ✅ 新增：设备删除完成，标记四叉树需要重建
                if (_pipeSnapService is PipeSnapService snapService)
                {
                    snapService.MarkQuadTreeDirty();
                    Debug.WriteLine("设备删除完成，已标记四叉树需要重建");
                }
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

        #region 私有字段 拖放节流优化

        // ✅ 拖放区域检测缓存
        private bool? _dragRegionCheckResult = null; // 缓存本次拖放的区域检测结果

        #endregion
        private void OnDragEnter(object sender, DragEventArgs e)
        {
            Debug.WriteLine($"外部拖拽进入: 内部拖拽={_isInternalDragging}, 当前拖拽设备={_currentDraggingDeviceId}");

            // 如果正在进行内部拖拽则忽略外部拖放
            if (_isInternalDragging || !string.IsNullOrEmpty(_currentDraggingDeviceId))
            {
                Debug.WriteLine("忽略外部拖拽，内部拖拽进行中");
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                _dragRegionCheckResult = null; // ✅ 清除缓存
                return;
            }

            // ✅ 处理管道拖拽
            if (e.Data.GetDataPresent("PipeData"))
            {
                var pipeData = e.Data.GetData("PipeData") as PipeDragData;
                Debug.WriteLine($"管道拖拽进入: {pipeData?.PipeType}");
                e.Effects = DragDropEffects.Copy;
                DesignCanvas.Background = new SolidColorBrush(Color.FromArgb(20, 0, 255, 120));
                _dragRegionCheckResult = null; // ✅ 管道不需要区域检测
                e.Handled = true;
                return;
            }

            // 处理设备拖拽
            if (e.Data.GetDataPresent("Device"))
            {
                var device = e.Data.GetData("Device") as IDevice;
                var position = e.GetPosition(DesignCanvas);
                Debug.WriteLine($"外部设备拖拽进入: {device?.Name}");

                // ✅ 只在 DragEnter 时检测一次区域并缓存结果
                _dragRegionCheckResult = IsValidRegionForDevice(device, position);
                Debug.WriteLine($"[DragEnter] 区域检测结果: {(_dragRegionCheckResult == true ? "有效区域" : "无效区域")}"); // ✅ 保留日志

                if (_dragRegionCheckResult == true)
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
                _dragRegionCheckResult = null; // ✅ 清除缓存
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

            // ✅ 处理管道拖拽
            if (e.Data.GetDataPresent("PipeData"))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }

            // ✅ 使用缓存的检测结果，不再重复检测
            if (e.Data.GetDataPresent("Device"))
            {
                var position = e.GetPosition(DesignCanvas); // ✅ 保留位置日志需要

                // ✅ 使用缓存结果
                if (_dragRegionCheckResult == true)
                {
                    e.Effects = DragDropEffects.Copy;
                    DesignCanvas.Background = new SolidColorBrush(Color.FromArgb(20, 0, 120, 212));
                }
                else
                {
                    e.Effects = DragDropEffects.None;
                    DesignCanvas.Background = new SolidColorBrush(Color.FromArgb(20, 255, 0, 0));
                }

                // ✅ 保留原有位置日志（如果需要）
                Debug.WriteLine($"[DragOver] 位置: ({position.X:F0}, {position.Y:F0}), 效果: {e.Effects}");
            }

            e.Handled = true;
        }

        private void OnDragLeave(object sender, DragEventArgs e)
        {
            Debug.WriteLine($"外部拖拽离开: 内部拖拽={_isInternalDragging}");

            // ✅ 清除缓存
            _dragRegionCheckResult = null;

            // 如果正在进行内部拖拽则忽略
            if (_isInternalDragging || !string.IsNullOrEmpty(_currentDraggingDeviceId))
            {
                Debug.WriteLine("忽略外部拖拽离开，内部拖拽进行中");
                e.Handled = true;
                return;
            }

            // 恢复画布背景
            DesignCanvas.Background = Brushes.White;

            if (e.Data.GetDataPresent("PipeData"))
            {
                var pipeData = e.Data.GetData("PipeData") as PipeDragData;
                Debug.WriteLine($"管道拖拽离开: {pipeData?.PipeType}");
            }

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

            // ✅ 清除缓存
            var cachedResult = _dragRegionCheckResult;
            _dragRegionCheckResult = null;

            // 检查是否应该忽略此拖放事件
            if (ShouldIgnoreDropEvent())
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            // ✅ 处理管道拖放
            if (e.Data.GetDataPresent("PipeData"))
            {
                var pipeData = e.Data.GetData("PipeData") as PipeDragData;
                var pipeDropPosition = e.GetPosition(DesignCanvas);

                ProcessPipeDrop(pipeData, pipeDropPosition);

                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }

            // 处理设备拖放
            if (e.Data.GetDataPresent("Device"))
            {
                var device = e.Data.GetData("Device") as IDevice;
                var dropPosition = e.GetPosition(DesignCanvas);

                // ✅ 使用缓存的检测结果
                if (cachedResult == true)
                {
                    ProcessDeviceDrop(device, dropPosition);
                    e.Effects = DragDropEffects.Copy;
                }
                else
                {
                    Debug.WriteLine($"设备 {device?.Name} 不能放置在当前区域（使用缓存结果）");
                    e.Effects = DragDropEffects.None;
                }
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }

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
                var addCommand = new AddDeviceCommand(_viewModel, canvasDevice);
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

                // *** 关键修改：使用反射深拷贝创建完全独立的设备实例 ***
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

        /// <summary>
        /// 处理管道拖放
        /// </summary>
        private void ProcessPipeDrop(PipeDragData pipeData, Point dropPosition)
        {
            _isProcessingDrop = true;
            _lastDropTime = DateTime.Now;

            try
            {
                Debug.WriteLine($"放置管道: {pipeData.PipeType} 在位置 ({dropPosition.X:F0}, {dropPosition.Y:F0})");

                CreatePipeFromToolbox(pipeData, dropPosition);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理管道拖放失败: {ex.Message}");
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
        /// 从工具箱创建管道
        /// </summary>
        private void CreatePipeFromToolbox(PipeDragData pipeData, Point position)
        {
            try
            {
                switch (pipeData.PipeType)
                {
                    case PipeType.Straight:
                        CreateStraightPipe(position);
                        break;

                    case PipeType.Elbow:
                        CreateElbowPipe(position);
                        break;

                    // ✅ 新增：Z 形管道创建
                    case PipeType.ZShape:
                        CreateZShapePipe(position);
                        break;

                    default:
                        Debug.WriteLine($"不支持的管道类型: {pipeData.PipeType}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"创建管道失败: {ex.Message}");
            }
        }

        #endregion

        #region 画布鼠标事件处理 设备拖拽和连接

        private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    var clickPosition = e.GetPosition(DesignCanvas);
                    var hitElement = e.OriginalSource as FrameworkElement;

                    // ✅ 只处理设备和空白区域
                    var deviceControl = FindDeviceControl(hitElement);
                    if (deviceControl?.Tag is string deviceId)
                    {
                        HandleDeviceMouseDown(deviceControl, deviceId, e);
                    }
                    else
                    {
                        // ⚠️ 点击空白区域时，清除所有选择
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
            ClearPipeSelection(); // ✅ 新增：清除管道选择
            ClearAllValveSelections(); // ✅ 新增：清除所有阀门选择
            ClearAllFlowMeterSelections(); // ✅ 新增
            ClearAllMicroreactorSelections(); // ✅ 新增
            CleanupDragState();
            this.Focus();
        }

        private void OnCanvasMouseMove(object sender, MouseEventArgs e)
        {
            var currentPosition = e.GetPosition(DesignCanvas);

            // ✅ 优先处理拖拽状态（无需节流）
            if (_isMouseDown && _mouseDownElement != null && e.LeftButton == MouseButtonState.Pressed)
            {
                ProcessDeviceDragging(e);
                return; // ⚠️ 重要：拖拽处理后直接返回，不受节流影响
            }

            // ✅ 只对非拖拽的鼠标移动进行节流
            var currentTime = DateTime.Now;
            var timeDelta = (currentTime - _lastMouseMoveTime).TotalMilliseconds;
            var distance = Math.Sqrt(Math.Pow(currentPosition.X - _lastMousePosition.X, 2) +
                                     Math.Pow(currentPosition.Y - _lastMousePosition.Y, 2));

            if (timeDelta < MouseMoveThrottleMs && distance < MouseMoveMinDistance)
            {
                return; // 只对普通鼠标移动节流
            }

            _lastMouseMoveTime = currentTime;
            _lastMousePosition = currentPosition;
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
            // ✅ 直接计算距离，不依赖 _lastMousePosition
            double dx = currentPoint.X - _dragStartPoint.X;
            double dy = currentPoint.Y - _dragStartPoint.Y;
            double distance = Math.Sqrt(dx * dx + dy * dy);

            bool shouldStart = distance > DragThreshold;

            if (shouldStart)
            {
                Debug.WriteLine($"拖拽启动: 移动距离 {distance:F1} > 阈值 {DragThreshold}");
            }

            return shouldStart;
        }

        /// <summary>
        /// 开始拖拽
        /// </summary>
        private void StartDragging()
        {
            _hasStartedDrag = true;
            _isInternalDragging = true;

            DesignCanvas.AllowDrop = false;
            DesignCanvas.CaptureMouse();
            AddDragVisualFeedback(_mouseDownElement);

            // ✅ 拖拽时降低渲染质量
            RenderOptions.SetBitmapScalingMode(DesignCanvas, BitmapScalingMode.LowQuality);
            DesignCanvas.CacheMode = null; // 禁用缓存

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

            // ✅ 新增：调度延迟布局更新
            ScheduleLayoutUpdate();

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
            var endPosition = e.GetPosition(DesignCanvas);

            if (_isInternalDragging)
            {
                ProcessDragEnd(endPosition);
            }

            ResetDragState();
            e.Handled = true;
        }

        /// <summary>
        /// 处理拖拽结束 - 添加区域检测
        /// </summary>
        private void ProcessDragEnd(Point endPosition)
        {
            Debug.WriteLine($"结束拖拽: {_currentDraggingDeviceId}");

            RemoveDragVisualFeedback(_mouseDownElement);

            var deviceId = _mouseDownElement.Tag as string;
            var canvasDevice = _viewModel?.CanvasDevices.FirstOrDefault(d => d.DeviceId == deviceId);

            var finalPosition = CalculateFinalPosition();
            var isValidRegion = IsValidRegionForDevice(canvasDevice.Device, finalPosition);
            var isWithinBounds = IsValidDropPosition(finalPosition);
            var isSuccessful = isValidRegion && isWithinBounds;

            if (isSuccessful)
            {
                CompleteDragOperation(canvasDevice, deviceId, finalPosition);
            }
            else
            {
                RestoreDeviceToOriginalPosition(_mouseDownElement);
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    "⚠️ 设备不能放置在当前区域");
            }

            _dragDropService.EndDrag(endPosition, isSuccessful, "Canvas");
            DesignCanvas.AllowDrop = true;

            // ✅ 拖拽结束后恢复高质量
            RenderOptions.SetBitmapScalingMode(DesignCanvas, BitmapScalingMode.HighQuality);
            DesignCanvas.CacheMode = new BitmapCache
            {
                EnableClearType = false,
                RenderAtScale = 1.0,
                SnapsToDevicePixels = true
            };

            Debug.WriteLine("✅ 已恢复高质量渲染");
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
            Debug.WriteLine($"鼠标离开画布，结束拖拽: {_currentDraggingDeviceId}");

            RemoveDragVisualFeedback(_mouseDownElement);
            RestoreDeviceToOriginalPosition(_mouseDownElement);

            if (_mouseDownElement?.Tag is string deviceId)
            {
                _dragDropService.EndDrag(new Point(), false, "Canvas");
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
            }), System.Windows.Threading.DispatcherPriority.Background);
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

            // ✅ 新增：如果设备包含阀门控件，订阅阀门事件
            SubscribeValveEventsInDevice(deviceControl);

            if (canvasDevice.Device?.Name?.Contains("锥形瓶") == true ||
                canvasDevice.Device?.Name?.Contains("Flask") == true)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (deviceControl.Child is Viewbox viewbox &&
                        viewbox.Child is ConicalFlaskControl flask)
                    {
                        Debug.WriteLine("延迟强制刷新锥形瓶液体");
                    }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }

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
        /// 创建设备控件 - 优化版
        /// </summary>
        private Border CreateDeviceControl(CanvasDeviceViewModel canvasDevice)
        {
            var adjustedSize = GetAdjustedDeviceSize(canvasDevice.Device, canvasDevice.Width, canvasDevice.Height);

            string displayName;
            if (_deviceDisplayNames.TryGetValue(canvasDevice.DeviceId, out var existingDisplayName))
            {
                displayName = existingDisplayName;
            }
            else
            {
                displayName = GetDisplayNameForNewDevice(canvasDevice.Device);
                _deviceDisplayNames[canvasDevice.DeviceId] = displayName;
            }

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

            // ✅ 启用设备级别缓存
            RenderOptions.SetCachingHint(deviceControl, CachingHint.Cache);
            deviceControl.CacheMode = new BitmapCache
            {
                EnableClearType = false,
                RenderAtScale = 1.0,
                SnapsToDevicePixels = true
            };

            deviceControl.Child = CreateDeviceContent(canvasDevice, adjustedSize, displayName);

            Debug.WriteLine($"创建设备控件（带缓存）: {canvasDevice.Device.Name}");

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
                var canvasDevice = _viewModel?.CanvasDevices.FirstOrDefault(d => d.DeviceId == deviceId);
                if (canvasDevice == null)
                {
                    Debug.WriteLine($"未找到设备: {deviceId}");
                    MessageBox.Show("未找到设备信息", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Debug.WriteLine($"显示设备属性弹窗: {canvasDevice.Device.Name}");

                // 关闭之前的弹窗
                if (_devicePropertyWindow != null)
                {
                    _devicePropertyWindow.Close();
                    _devicePropertyWindow = null;
                }

                // 创建设备属性对话框
                var devicePropertyDialog = new DevicePropertyDialog();

                // 获取设备显示名称和之前的选择状态
                var displayName = GetDeviceDisplayName(deviceId);
                var previousSelections = GetDeviceMonitoringSelections(deviceId);

                Debug.WriteLine($"设备 {deviceId} 的历史选择状态: {string.Join(", ", previousSelections)}");

                // *** 关键修复：只设置一次数据和状态 ***

                // 1. 先设置设备数据（这会清理所有状态）
                devicePropertyDialog.SetDeviceData(canvasDevice, displayName);

                // 2. 如果有历史选择状态，延迟恢复
                if (previousSelections.Count > 0)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        try
                        {
                            Debug.WriteLine($"开始延迟恢复设备 {deviceId} 的监控参数状态");
                            devicePropertyDialog.RestoreMonitoringSelections(previousSelections);

                            // 可选：延迟调试当前状态
                            Dispatcher.BeginInvoke(new Action(() =>
                            {

                            }), System.Windows.Threading.DispatcherPriority.Background);

                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"延迟恢复监控参数状态失败: {ex.Message}");
                        }
                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
                else
                {
                    Debug.WriteLine($"设备 {deviceId} 没有历史选择状态需要恢复");
                }

                // 订阅事件
                devicePropertyDialog.MonitoringParametersChanged += OnMonitoringParametersChanged;

                // 创建并显示弹窗
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

                devicePropertyDialog.CloseRequested += (s, args) => CloseDevicePropertyWindow();
                devicePropertyDialog.DialogResult += OnDevicePropertyDialogResult;

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

                // *** 关键修复：优先使用完整键名格式 ***
                if (sender is DevicePropertyDialog dialog)
                {
                    var completeSelectionKeys = dialog.GetAllSelectedParameterKeys();
                    SaveDeviceMonitoringSelectionsWithKeys(e.DeviceId, completeSelectionKeys);

                    Debug.WriteLine($"保存完整键名格式: {string.Join(", ", completeSelectionKeys)}");
                }
                else
                {
                    // 向后兼容：从参数名转换为完整键名格式
                    var parameterNames = e.SelectedParameters.Select(p => p.Name).ToHashSet();
                    var convertedKeys = ConvertToCompleteKeys(e.DeviceId, parameterNames);
                    _deviceMonitoringSelections[e.DeviceId] = convertedKeys;

                    Debug.WriteLine($"从参数名转换为完整键名格式: {string.Join(", ", convertedKeys)}");
                }

                if (e.SelectedParameters.Any())
                {
                    CreateOrUpdateMonitoringCard(e.DeviceId, e.DeviceName, e.SelectedParameters);
                }
                else
                {
                    RemoveMonitoringCard(e.DeviceId);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理监控参数变化失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 新增：保存设备监控参数选择状态（使用完整键名）
        /// </summary>
        private void SaveDeviceMonitoringSelectionsWithKeys(string deviceId, HashSet<string> parameterKeys)
        {
            try
            {
                if (string.IsNullOrEmpty(deviceId) || parameterKeys == null)
                    return;

                _deviceMonitoringSelections[deviceId] = new HashSet<string>(parameterKeys);

                Debug.WriteLine($"保存设备 {deviceId} 的完整监控参数选择键: {string.Join(", ", parameterKeys)}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"保存设备监控参数选择键失败: {ex.Message}");
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
        /// 将监控卡片添加到画布（支持位置恢复）
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

                double cardLeft, cardTop;

                // ✅ 优先使用保存的位置
                if (_monitoringCardPositions.TryGetValue(deviceId, out var savedPosition))
                {
                    cardLeft = savedPosition.X;
                    cardTop = savedPosition.Y;
                    Debug.WriteLine($"使用保存的位置: {deviceId} at ({cardLeft:F0}, {cardTop:F0})");
                }
                else
                {
                    // ✅ 使用默认位置（设备右侧）
                    var deviceLeft = Canvas.GetLeft(deviceElement);
                    var deviceTop = Canvas.GetTop(deviceElement);
                    var deviceWidth = deviceElement.ActualWidth;

                    cardLeft = deviceLeft + deviceWidth + 15;
                    cardTop = deviceTop;
                    Debug.WriteLine($"使用默认位置: {deviceId} at ({cardLeft:F0}, {cardTop:F0})");
                }

                // 设置卡片位置
                Canvas.SetLeft(monitoringCard, cardLeft);
                Canvas.SetTop(monitoringCard, cardTop);
                Canvas.SetZIndex(monitoringCard, 500);

                // 设置卡片标识
                monitoringCard.Tag = $"MonitoringCard_{deviceId}";

                // 添加到画布
                DesignCanvas.Children.Add(monitoringCard);

                Debug.WriteLine($"监控卡片已添加到画布: 位置({cardLeft:F0}, {cardTop:F0})");

                // ✅ 启用监控卡片缓存
                RenderOptions.SetCachingHint(monitoringCard, CachingHint.Cache);
                monitoringCard.CacheMode = new BitmapCache
                {
                    EnableClearType = false,
                    RenderAtScale = 1.0,
                    SnapsToDevicePixels = true
                };

                Canvas.SetLeft(monitoringCard, cardLeft);
                Canvas.SetTop(monitoringCard, cardTop);
                Canvas.SetZIndex(monitoringCard, 500);

                monitoringCard.Tag = $"MonitoringCard_{deviceId}";

                DesignCanvas.Children.Add(monitoringCard);

                Debug.WriteLine($"监控卡片已添加（带缓存）: 位置({cardLeft:F0}, {cardTop:F0})");
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
                }
                else
                {
                    RestoreDeviceToOriginalPosition(_mouseDownElement);
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

                // ✅ 新增：调度延迟布局更新
                ScheduleLayoutUpdate();

                // 更新监控卡片位置
                UpdateMonitoringCardPosition(deviceId);
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
                var currentPosition = e.GetPosition(DesignCanvas);

                // ✅ 如果是拖拽状态，立即处理，不节流
                if (_isMouseDown && _currentDraggingDeviceId == deviceId && e.LeftButton == MouseButtonState.Pressed)
                {
                    HandleDeviceMouseMove(deviceElement, e);
                    return; // ⚠️ 重要：直接返回，不受节流影响
                }

                // ✅ 只对非拖拽的鼠标移动进行节流
                var currentTime = DateTime.Now;
                var timeDelta = (currentTime - _lastMouseMoveTime).TotalMilliseconds;
                var distance = Math.Sqrt(Math.Pow(currentPosition.X - _lastMousePosition.X, 2) +
                                         Math.Pow(currentPosition.Y - _lastMousePosition.Y, 2));

                if (timeDelta < MouseMoveThrottleMs && distance < MouseMoveMinDistance)
                {
                    return;
                }

                _lastMouseMoveTime = currentTime;
                _lastMousePosition = currentPosition;
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

            //// 检查区域有效性
            //if (!IsValidDeviceDragPosition(boundedLeft, boundedTop, deviceElement))
            //{
            //    UpdateDragVisualFeedback(deviceElement, false);
            //    return;
            //}

            // 移动设备
            Canvas.SetLeft(deviceElement, boundedLeft);
            Canvas.SetTop(deviceElement, boundedTop);
            deviceElement.Opacity = 0.8;

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
        /// 获取调整后的设备尺寸（支持自定义控件）
        /// </summary>
        private Size GetAdjustedDeviceSize(IDevice device, double defaultWidth, double defaultHeight)
        {
            try
            {
                var imageLocationProperty = device.GetType().GetProperty("ImageLocation");
                string imageLocation = imageLocationProperty?.GetValue(device) as string;

                // ✅ 自定义控件尺寸处理
                if (!string.IsNullOrEmpty(imageLocation) && imageLocation.StartsWith("CUSTOM_CONTROL:"))
                {
                    string controlType = imageLocation.Replace("CUSTOM_CONTROL:", "");

                    switch (controlType)
                    {
                        case "ConicalFlaskControl":
                            // 锥形瓶尺寸
                            double flaskWidth = 80;
                            double flaskHeight = 120;
                            Debug.WriteLine($"锥形瓶尺寸: {flaskWidth:F0}x{flaskHeight:F0}");
                            return new Size(flaskWidth, flaskHeight);

                        // ✅ 新增：柱塞泵尺寸
                        case "PistonPumpControl":
                            // 根据你的 XAML：Width="880" Height="600"
                            // 但画布上需要缩小显示
                            double baseWidth = 880 * 0.25;   // 176
                            double baseHeight = 600 * 0.25;  // 120
                            double pumpWidth = baseWidth;
                            double pumpHeight = baseHeight;
                            Debug.WriteLine($"柱塞泵尺寸: {pumpWidth:F0}x{pumpHeight:F0}");
                            return new Size(pumpWidth, pumpHeight);

                        // ✅ 新增：阀门尺寸
                        case "ValveControl":
                            double valveWidth = 200 * 0.5;   // 100（阀门原尺寸200x200，缩小到一半）
                            double valveHeight = 200 * 0.5;  // 100
                            Debug.WriteLine($"阀门尺寸: {valveWidth:F0}x{valveHeight:F0}");
                            return new Size(valveWidth, valveHeight);
                        //新增电磁阀
                        case "SolenoidValveControl":
                            double solenoidValveWidth = 200 * 0.5;   // 100（电磁阀原尺寸200x200，缩小到一半）
                            double solenoidValveHeight = 200 * 0.5;  // 100
                            Debug.WriteLine($"电磁阀尺寸: {solenoidValveWidth:F0}x{solenoidValveHeight:F0}");
                            return new Size(solenoidValveWidth, solenoidValveHeight);

                        case "MassFlowMeterControl":
                            // 流量计原尺寸 400x500，缩小到画布显示
                            double meterWidth = 400 * 0.35;   // 120
                            double meterHeight = 500 * 0.35;  // 150
                            Debug.WriteLine($"流量计尺寸: {meterWidth:F0}x{meterHeight:F0}");
                            return new Size(meterWidth, meterHeight);

                        // 在 switch (controlType) 中添加
                        case "MicroreactorControl":
                            // 微反应器原尺寸 880x600，缩小到画布显示
                            double reactorWidth = 880 * 0.3;   
                            double reactorHeight = 600 * 0.3; 
                            Debug.WriteLine($"微反应器尺寸: {reactorWidth:F0}x{reactorHeight:F0}");
                            return new Size(reactorWidth, reactorHeight);
                    }
                }
                // 普通图片尺寸处理
                else if (!string.IsNullOrEmpty(imageLocation) && IsValidImageUri(imageLocation))
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
        /// 创建设备图标（支持自定义控件）
        /// </summary>
        private UIElement CreateDeviceIcon(IDevice device, double targetWidth, double targetHeight)
        {
            try
            {
                var imageLocationProperty = device.GetType().GetProperty("ImageLocation");
                string imageLocation = imageLocationProperty?.GetValue(device) as string;

                // ✅ 新增：检查是否为自定义控件
                if (!string.IsNullOrEmpty(imageLocation) && imageLocation.StartsWith("CUSTOM_CONTROL:"))
                {
                    return CreateCustomControlIcon(imageLocation, targetWidth, targetHeight);
                }
                // 普通图片处理
                else if (!string.IsNullOrEmpty(imageLocation) && IsValidImageUri(imageLocation))
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
        /// 创建自定义控件图标（锥形瓶、柱塞泵等）
        /// </summary>
        private UIElement CreateCustomControlIcon(string imageLocation, double targetWidth, double targetHeight)
        {
            try
            {
                string controlType = imageLocation.Replace("CUSTOM_CONTROL:", "");
                Debug.WriteLine($"创建自定义控件: {controlType}, 目标尺寸: {targetWidth}x{targetHeight}");

                switch (controlType)
                {
                    case "ConicalFlaskControl":
                        return CreateConicalFlaskIcon(targetWidth, targetHeight);

                    // ✅ 新增：柱塞泵控件
                    case "PistonPumpControl":
                        return CreatePistonPumpIcon(targetWidth, targetHeight);

                    // ✅ 新增：阀门控件
                    case "ValveControl":
                        return CreateValveIcon(targetWidth, targetHeight);

                    case "SolenoidValveControl":
                        return CreateSolenoidValveIcon(targetWidth, targetHeight);

                    // ✅ 新增：流量计控件
                    case "MassFlowMeterControl":
                        return CreateMassFlowMeterIcon(targetWidth, targetHeight);
                    //微反应器
                    case "MicroreactorControl":
                        return CreateMicroreactorIcon(targetWidth, targetHeight);
                    // 未来可以添加其他自定义控件
                    default:
                        Debug.WriteLine($"不支持的自定义控件类型: {controlType}");
                        return CreateDefaultIcon(targetWidth, targetHeight, "自定义");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"创建自定义控件图标失败: {ex.Message}");
                return CreateDefaultIcon(targetWidth, targetHeight, "自定义");
            }
        }

        /// <summary>
        /// 创建锥形瓶图标（用于画布上的小图标显示）- 优化版
        /// </summary>
        private UIElement CreateConicalFlaskIcon(double targetWidth, double targetHeight)
        {
            try
            {
                var flaskControl = new ConicalFlaskControl
                {
                    FlaskWidth = 80,
                    FlaskHeight = 120,
                    IsHitTestVisible = false,
                    Cursor = System.Windows.Input.Cursors.Hand
                };

                // 隐藏交互元素
                HideFlaskInteractiveElements(flaskControl);

                // ✅ 关键修复：使用 Loaded 事件而不是 SizeChanged
                flaskControl.Loaded += (s, e) =>
                {
                    try
                    {
                        Debug.WriteLine("锥形瓶 Loaded 事件触发");

                        // ✅ 方法1：直接设置液体（推荐）
                        flaskControl.SetLiquidColor(96, 176, 224, 180);  // 增加透明度到180
                        flaskControl.SetLiquidLevel(65, animated: true);
                        //flaskControl.IsWaveAnimationEnabled = true;

                        // ✅ 强制更新布局和几何
                        flaskControl.UpdateLayout();

                        // ✅ 延迟调用，确保控件完全渲染
                        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            flaskControl.InvalidateVisual();
                            flaskControl.DebugLiquidGeometry();
                            Debug.WriteLine("✅ 液体初始化完成");
                        }), System.Windows.Threading.DispatcherPriority.Loaded);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"❌ 锥形瓶液体初始化失败: {ex.Message}");
                    }
                };

                // ✅ 包装在 Viewbox 中
                var viewbox = new System.Windows.Controls.Viewbox
                {
                    Width = targetWidth,
                    Height = targetHeight,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    StretchDirection = System.Windows.Controls.StretchDirection.Both,
                    Child = flaskControl,
                    IsHitTestVisible = false
                };

                Debug.WriteLine($"创建锥形瓶图标容器: {targetWidth}x{targetHeight}");
                return viewbox;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 创建锥形瓶图标失败: {ex.Message}");
                return CreateDefaultIcon(targetWidth, targetHeight, "锥形瓶");
            }
        }

        /// <summary>
        /// 创建柱塞泵图标（用于画布上的小图标显示）
        /// </summary>
        private UIElement CreatePistonPumpIcon(double targetWidth, double targetHeight)
        {
            try
            {
                // ✅ 创建柱塞泵控件实例 - 允许交互
                var pumpControl = new PistonPumpControl
                {
                    IsHitTestVisible = true,  // ✅ 改为 true，允许按钮点击
                    Cursor = System.Windows.Input.Cursors.Hand
                };

                // ✅ 包装在 Viewbox 中 - 也要允许交互
                var viewbox = new System.Windows.Controls.Viewbox
                {
                    Width = targetWidth,
                    Height = targetHeight,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    StretchDirection = System.Windows.Controls.StretchDirection.Both,
                    Child = pumpControl,
                    IsHitTestVisible = true  // ✅ 改为 true
                };

                Debug.WriteLine($"创建柱塞泵图标容器（画布可交互）: {targetWidth}x{targetHeight}");
                return viewbox;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 创建柱塞泵图标失败: {ex.Message}");
                return CreateDefaultIcon(targetWidth, targetHeight, "柱塞泵");
            }
        }


        /// <summary>
        /// 通用的可视化树查找辅助方法
        /// </summary>
        private T FindVisualChild<T>(DependencyObject parent, Func<T, bool> predicate = null)
            where T : DependencyObject
        {
            if (parent == null) return null;

            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is T typedChild && (predicate == null || predicate(typedChild)))
                {
                    return typedChild;
                }

                var result = FindVisualChild(child, predicate);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }
        /// <summary>
        /// 隐藏锥形瓶的交互元素（选择边框、锚点等）
        /// </summary>
        private void HideFlaskInteractiveElements(ConicalFlaskControl flask)
        {
            try
            {
                var selectionBorder = flask.FindName("SelectionBorder") as System.Windows.Shapes.Rectangle;
                var topAnchor = flask.FindName("TopAnchor") as System.Windows.Shapes.Ellipse;
                var bottomAnchor = flask.FindName("BottomAnchor") as System.Windows.Shapes.Ellipse;
                var leftAnchor = flask.FindName("LeftAnchor") as System.Windows.Shapes.Ellipse;
                var rightAnchor = flask.FindName("RightAnchor") as System.Windows.Shapes.Ellipse;

                if (selectionBorder != null) selectionBorder.Visibility = System.Windows.Visibility.Collapsed;
                if (topAnchor != null) topAnchor.Visibility = System.Windows.Visibility.Collapsed;
                if (bottomAnchor != null) bottomAnchor.Visibility = System.Windows.Visibility.Collapsed;
                if (leftAnchor != null) leftAnchor.Visibility = System.Windows.Visibility.Collapsed;
                if (rightAnchor != null) rightAnchor.Visibility = System.Windows.Visibility.Collapsed;

                Debug.WriteLine("锥形瓶交互元素已隐藏");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"隐藏锥形瓶交互元素失败: {ex.Message}");
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
        /// 清空画布请求处理（完整版）
        /// </summary>
        private void OnClearCanvasRequest()
        {
            try
            {
                Debug.WriteLine("========== 开始清空画布 ==========");

                // ✅ 1. 清空监控相关
                ClearAllMonitoringStates();

                // ✅ 2. 清空设备相关状态
                ClearDeviceStates();

                // ✅ 3. 清空管道
                ClearAllPipes();

                // ✅ 4. 强制清空画布视觉元素（先清视图）
                ClearCanvasVisualElements();

                // ✅ 5. 清空数据模型（后清数据）
                ClearViewModelData();

                // ✅ 6. 清空选择状态
                ClearDeviceSelection();
                ClearPipeSelection();

                // ✅ 7. 重置画布状态
                ResetCanvasState();

                Debug.WriteLine("========== 画布清空完成 ==========");
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("画布已清空");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 清空画布失败: {ex.Message}");
            }
        }


        /// <summary>
        /// 重置画布状态
        /// </summary>
        private void ResetCanvasState()
        {
            try
            {
                // 重置缩放
                if (_canvasScaleTransform != null)
                {
                    _canvasScaleTransform.ScaleX = 1.0;
                    _canvasScaleTransform.ScaleY = 1.0;
                }

                // 强制更新布局
                DesignCanvas.UpdateLayout();

                Debug.WriteLine("画布状态已重置");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"重置画布状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清空所有管道
        /// </summary>
        private void ClearAllPipes()
        {
            try
            {
                var pipeElements = DesignCanvas.Children
                    .OfType<FrameworkElement>()
                    .Where(e => e is StraightPipeControl ||
                               e is ElbowPipeControl ||
                               e is ZShapePipeControl)
                    .ToList();

                foreach (var pipe in pipeElements)
                {
                    DesignCanvas.Children.Remove(pipe);
                }

                _pipes.Clear();
                ClearPipeSelection();

                Debug.WriteLine($"清空了 {pipeElements.Count} 个管道");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清空管道失败: {ex.Message}");
            }
        }
        /// <summary>
        /// 清空所有监控相关状态
        /// </summary>
        private void ClearAllMonitoringStates()
        {
            try
            {
                // 清空监控卡片
                ClearAllMonitoringCards();

                // 清空监控参数选择状态
                _deviceMonitoringSelections.Clear();

                Debug.WriteLine("所有监控相关状态已清空");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清空监控相关状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清空设备相关状态
        /// </summary>
        private void ClearDeviceStates()
        {
            try
            {
                // 清空设备名称计数器
                _deviceNameCounters.Clear();

                // 清空设备显示名映射
                _deviceDisplayNames.Clear();

                Debug.WriteLine("设备相关状态已清空");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清空设备相关状态失败: {ex.Message}");
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
        /// 清空ViewModel数据
        /// </summary>
        private void ClearViewModelData()
        {
            if (_viewModel?.CanvasDevices != null)
            {
                Debug.WriteLine($"清空 {_viewModel.CanvasDevices.Count} 个设备");
                _viewModel.CanvasDevices.Clear();
            }
        }

        /// <summary>
        /// 清空画布视觉元素
        /// </summary>
        private void ClearCanvasVisualElements()
        {
            try
            {
                Debug.WriteLine("---------- 清空画布视觉元素 ----------");

                // 收集所有需要删除的元素
                var elementsToRemove = new List<UIElement>();

                foreach (UIElement child in DesignCanvas.Children)
                {
                    // 设备元素
                    if (child is Border border && !string.IsNullOrEmpty(border.Tag as string))
                    {
                        elementsToRemove.Add(child);
                    }
                    // 管道元素
                    else if (child is StraightPipeControl ||
                             child is ElbowPipeControl ||
                             child is ZShapePipeControl)
                    {
                        elementsToRemove.Add(child);
                    }
                    // 监控卡片
                    else if (child is MonitoringCard)
                    {
                        elementsToRemove.Add(child);
                    }
                }

                Debug.WriteLine($"找到 {elementsToRemove.Count} 个元素需要清除");

                // 批量移除
                foreach (var element in elementsToRemove)
                {
                    DesignCanvas.Children.Remove(element);
                }

                // 强制清空（双重保险）
                DesignCanvas.Children.Clear();

                Debug.WriteLine($"清除后画布子元素数量: {DesignCanvas.Children.Count}");
                Debug.WriteLine("---------- 画布视觉元素清空完成 ----------");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 清空画布视觉元素失败: {ex.Message}");
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

                callback?.Invoke(canvasData);
                Debug.WriteLine($"收集画布数据完成: 设备{canvasData.Devices.Count}个");
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
        /// 加载画布请求处理 - 优化版
        /// </summary>
        private void OnLoadCanvasRequest(CanvasDocument canvasDocument)
        {
            try
            {
                Debug.WriteLine($"========== 开始加载画布: {canvasDocument.Name} ==========");
                Debug.WriteLine($"设备数量: {canvasDocument.Devices?.Count ?? 0}");
                Debug.WriteLine($"管道数量: {canvasDocument.Pipes?.Count ?? 0}");

                // 更新画布尺寸
                UpdateCanvasSize(canvasDocument);

                // ✅ 使用异步分批加载
                LoadCanvasContentAsync(canvasDocument);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载画布失败: {ex.Message}");
            }
        }

        /// <summary>
        /// ✅ 新增：异步分批加载画布内容
        /// </summary>
        private async void LoadCanvasContentAsync(CanvasDocument canvasDocument)
        {
            try
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                // 步骤1：分批恢复设备
                await RestoreDevicesBatchAsync(canvasDocument.Devices);
                Debug.WriteLine($"设备恢复耗时: {stopwatch.ElapsedMilliseconds}ms");

                // 步骤2：强制更新布局
                DesignCanvas.UpdateLayout();
                await System.Threading.Tasks.Task.Delay(10); // 给UI线程喘息时间

                // 步骤3：分批恢复管道
                if (canvasDocument.Pipes != null && canvasDocument.Pipes.Count > 0)
                {
                    stopwatch.Restart();
                    await RestorePipesBatchAsync(canvasDocument.Pipes);
                    Debug.WriteLine($"管道恢复耗时: {stopwatch.ElapsedMilliseconds}ms");

                    DesignCanvas.UpdateLayout();
                    await System.Threading.Tasks.Task.Delay(10);
                }

                Debug.WriteLine("========== 画布加载完成 ==========");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"异步加载画布内容失败: {ex.Message}");
            }
        }

        /// <summary>
        /// ✅ 新增：分批恢复设备
        /// </summary>
        private async System.Threading.Tasks.Task RestoreDevicesBatchAsync(List<CanvasDeviceData> deviceDataList)
        {
            if (deviceDataList == null || !deviceDataList.Any())
            {
                Debug.WriteLine("没有设备需要恢复");
                return;
            }

            Debug.WriteLine($"开始分批恢复 {deviceDataList.Count} 个设备");

            const int batchSize = 10; // 每批处理10个设备
            int totalBatches = (int)Math.Ceiling(deviceDataList.Count / (double)batchSize);

            for (int i = 0; i < totalBatches; i++)
            {
                var batch = deviceDataList.Skip(i * batchSize).Take(batchSize).ToList();

                // 在UI线程上恢复这批设备
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var deviceData in batch)
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
                }, System.Windows.Threading.DispatcherPriority.Background);

                // ✅ 更新进度
                var progress = (i + 1) * 100 / totalBatches;
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"正在加载设备... {progress}%");

                // ✅ 每批之间让出UI线程
                if (i < totalBatches - 1)
                {
                    await System.Threading.Tasks.Task.Delay(1);
                }

            }

            Debug.WriteLine($"设备恢复完成，当前设备数量: {_viewModel?.CanvasDevices?.Count ?? 0}");
            // ✅ 新增：批量恢复完成，立即重建四叉树
            if (_pipeSnapService is PipeSnapService snapService)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    snapService.MarkQuadTreeDirty();
                    Debug.WriteLine("批量设备恢复完成，已标记四叉树需要重建");
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        /// <summary>
        /// ✅ 新增：分批恢复管道
        /// </summary>
        private async System.Threading.Tasks.Task RestorePipesBatchAsync(List<CanvasPipeData> pipesData)
        {
            if (pipesData == null || !pipesData.Any())
            {
                Debug.WriteLine("没有管道数据需要恢复");
                return;
            }

            Debug.WriteLine($"开始分批恢复 {pipesData.Count} 个管道");

            // 清空现有管道
            ClearExistingPipesBeforeRestore(pipesData);

            const int batchSize = 20; // 每批处理20个管道（管道比设备简单，可以多处理一些）
            int totalBatches = (int)Math.Ceiling(pipesData.Count / (double)batchSize);
            int successCount = 0;

            for (int i = 0; i < totalBatches; i++)
            {
                var batch = pipesData.Skip(i * batchSize).Take(batchSize).ToList();

                // 在UI线程上恢复这批管道
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var pipeData in batch)
                    {
                        try
                        {
                            if (RestoreSinglePipeSync(pipeData))
                            {
                                successCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"恢复管道 {pipeData.PipeId} 失败: {ex.Message}");
                        }
                    }
                }, System.Windows.Threading.DispatcherPriority.Background);

                // ✅ 更新进度
                var progress = (i + 1) * 100 / totalBatches;
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"正在加载管道... {progress}%");

                // ✅ 每批之间让出UI线程
                if (i < totalBatches - 1)
                {
                    await System.Threading.Tasks.Task.Delay(1);
                }
            }

            Debug.WriteLine($"管道恢复完成: 成功 {successCount}/{pipesData.Count}");

            // ✅ 新增：批量恢复完成，立即重建四叉树
            if (_pipeSnapService is PipeSnapService snapService)
            {
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    snapService.MarkQuadTreeDirty();
                    Debug.WriteLine("批量管道恢复完成，已标记四叉树需要重建");
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }


        /// <summary>
        /// 同步恢复单个管道 - 优化版
        /// </summary>
        private bool RestoreSinglePipeSync(CanvasPipeData pipeData)
        {
            try
            {
                // 检查是否已存在
                if (_pipes.ContainsKey(pipeData.PipeId))
                {
                    return false;
                }

                FrameworkElement pipeControl = null;

                // 根据类型创建管道控件
                switch (pipeData.PipeType)
                {
                    case "Straight":
                        pipeControl = CreateRestoredStraightPipe(pipeData);
                        break;
                    case "Elbow":
                        pipeControl = CreateRestoredElbowPipe(pipeData);
                        break;
                    case "ZShape":
                        pipeControl = CreateRestoredZShapePipe(pipeData);
                        break;
                    default:
                        return false;
                }

                if (pipeControl != null)
                {
                    // 设置位置和层级
                    Canvas.SetLeft(pipeControl, pipeData.X);
                    Canvas.SetTop(pipeControl, pipeData.Y);
                    Panel.SetZIndex(pipeControl, pipeData.ZIndex);

                    // 添加到画布
                    DesignCanvas.Children.Add(pipeControl);

                    // 添加到管道字典
                    _pipes[pipeData.PipeId] = pipeControl;

                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 恢复管道失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// ✅ 新增:清空已存在的相同管道ID,避免重复创建
        /// </summary>
        private void ClearExistingPipesBeforeRestore(List<CanvasPipeData> pipesData)
        {
            var pipeIdsToRestore = pipesData.Select(p => p.PipeId).ToHashSet();

            // 查找画布上已存在的相同ID管道
            var existingPipes = DesignCanvas.Children
                .OfType<FrameworkElement>()
                .Where(e =>
                {
                    if (e is StraightPipeControl sp && pipeIdsToRestore.Contains(sp.PipeId))
                        return true;
                    if (e is ElbowPipeControl ep && pipeIdsToRestore.Contains(ep.PipeId))
                        return true;
                    if (e is ZShapePipeControl zp && pipeIdsToRestore.Contains(zp.PipeId))
                        return true;
                    return false;
                })
                .ToList();

            foreach (var existingPipe in existingPipes)
            {
                DesignCanvas.Children.Remove(existingPipe);

                string pipeId = null;
                if (existingPipe is StraightPipeControl sp) pipeId = sp.PipeId;
                else if (existingPipe is ElbowPipeControl ep) pipeId = ep.PipeId;
                else if (existingPipe is ZShapePipeControl zp) pipeId = zp.PipeId;

                if (!string.IsNullOrEmpty(pipeId) && _pipes.ContainsKey(pipeId))
                {
                    _pipes.Remove(pipeId);
                }

                Debug.WriteLine($"清除已存在的管道: {pipeId}");
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
        /// 恢复单个设备 - 优化版
        /// </summary>
        private void RestoreSingleDevice(CanvasDeviceData deviceData)
        {
            try
            {
                // ✅ 简化日志输出
                // Debug.WriteLine($"恢复设备: {deviceData.DeviceName}");

                // 重建设备实例
                var device = _canvasDataService.RestoreDeviceFromData(deviceData);
                if (device == null)
                {
                    Debug.WriteLine($"⚠️ 无法恢复设备: {deviceData.DeviceName}");
                    return;
                }

                var independentDevice = ReflectionDeviceDeepCopyService.DeepClone(device);

                // 获取显示名
                string displayName;
                if (_deviceDisplayNames.TryGetValue(deviceData.DeviceId, out var existingDisplayName))
                {
                    displayName = existingDisplayName;
                }
                else
                {
                    displayName = deviceData.DeviceName ?? "未知设备";
                    _deviceDisplayNames[deviceData.DeviceId] = displayName;
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

                // ✅ 直接添加到ViewModel，不触发事件
                _viewModel?.CanvasDevices.Add(canvasDeviceViewModel);

                // ✅ 延迟恢复缩放和监控卡片（在所有设备加载完后统一处理）
                if (deviceData.ScaleX != 1.0 || deviceData.ScaleY != 1.0)
                {
                    ScheduleDeviceScaleRestoration(deviceData.DeviceId, deviceData.ScaleX, deviceData.ScaleY);
                }

                ScheduleMonitoringCardRestoration(deviceData.DeviceId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 恢复设备失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 安排监控卡片恢复检查
        /// </summary>
        private void ScheduleMonitoringCardRestoration(string deviceId)
        {
            // 多次延迟检查，确保监控参数状态已经恢复
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                CheckAndRestoreMonitoringCard(deviceId);
            }), System.Windows.Threading.DispatcherPriority.Loaded);

            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                CheckAndRestoreMonitoringCard(deviceId);
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                CheckAndRestoreMonitoringCard(deviceId);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
        /// <summary>
        /// 检查并恢复监控卡片
        /// </summary>
        private void CheckAndRestoreMonitoringCard(string deviceId)
        {
            try
            {
                // 检查是否已经有监控卡片
                if (_monitoringCards.ContainsKey(deviceId))
                {
                    Debug.WriteLine($"设备 {deviceId} 的监控卡片已存在");
                    return;
                }

                // 检查是否有监控参数选择
                var selectedKeys = GetDeviceMonitoringSelections(deviceId);
                if (selectedKeys.Count == 0)
                {
                    Debug.WriteLine($"设备 {deviceId} 没有监控参数选择，跳过卡片创建");
                    return;
                }

                // 尝试恢复监控卡片
                var success = RestoreSingleMonitoringCard(deviceId);
                Debug.WriteLine($"设备 {deviceId} 监控卡片恢复结果: {(success ? "成功" : "失败")}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"检查并恢复监控卡片失败: {ex.Message}");
            }
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

                // ✅ 新增：调度延迟布局更新
                ScheduleLayoutUpdate();

                Debug.WriteLine($"设备 {deviceId} 缩放恢复完成");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"恢复设备缩放失败: {ex.Message}");
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
                Debug.WriteLine($"最终管道数量: {_pipes.Count}"); // ✅ 新增

                // 强制更新布局
                DesignCanvas.UpdateLayout();

                // ✅ 新增：最终确认管道状态
                ScheduleFinalPipeValidation();

                Debug.WriteLine("画布加载处理完成");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"画布加载完成处理失败: {ex.Message}");
            }
        }
        /// <summary>
        /// ✅ 新增：安排最终管道验证
        /// </summary>
        private void ScheduleFinalPipeValidation()
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                ValidatePipesState();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// ✅ 新增：验证管道状态
        /// </summary>
        private void ValidatePipesState()
        {
            try
            {
                Debug.WriteLine("========== 最终验证管道状态 ==========");

                // 检查画布上的管道数量
                var pipesInCanvas = DesignCanvas.Children
                    .OfType<FrameworkElement>()
                    .Count(e => e is StraightPipeControl || e is ElbowPipeControl || e is ZShapePipeControl);

                Debug.WriteLine($"画布上管道数量: {pipesInCanvas}");
                Debug.WriteLine($"管道字典数量: {_pipes.Count}");

                if (pipesInCanvas != _pipes.Count)
                {
                    Debug.WriteLine($"⚠️ 警告：管道数量不匹配");
                }

                Debug.WriteLine("========== 管道状态验证完成 ==========");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"验证管道状态失败: {ex.Message}");
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

                // 传入连接服务实例
                _regionOverlayService.SetCurrentContext(
                    currentDocument,
                    DesignCanvas,
                    variablePanels);

                Debug.WriteLine("RegionOverlayService 上下文设置完成");

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
        /// 区域布局更新事件处理
        /// </summary>
        private void OnRegionLayoutUpdated(RegionLayout newLayout)
        {
            try
            {
                // 更新上下文中的布局信息
                var currentDocument = GetCurrentDocument();
                var variablePanels = GetVariablePanels();

                _regionOverlayService.SetCurrentContext(
                    currentDocument,
                    DesignCanvas,
                    variablePanels);

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
            return _viewModel?.CanvasDevices?.Count > 0;
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

        #region 管道拖拽相关

        /// <summary>
        /// 管道拖动开始
        /// </summary>
        private void OnPipeDragStarted(object sender, PipeDragEventArgs e)
        {
            try
            {
                var pipe = sender as StraightPipeControl;
                if (pipe == null) return;

                // ✅ 记录原始位置用于撤销
                var left = Canvas.GetLeft(pipe);
                var top = Canvas.GetTop(pipe);
                _pipeOriginalPosition = new Point(
                    double.IsNaN(left) ? 0 : left,
                    double.IsNaN(top) ? 0 : top);

                Debug.WriteLine($"管道拖动开始: {e.PipeId}, 原始位置: ({_pipeOriginalPosition.X:F0}, {_pipeOriginalPosition.Y:F0})");

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("正在移动管道...");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"管道拖动开始处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 管道拖动中（已优化节流）
        /// </summary>
        private void OnPipeDragging(object sender, PipeDragEventArgs e)
        {
            // ✅ 节流检查：避免过于频繁的吸附检测
            var now = DateTime.Now;
            if ((now - _lastMouseMoveTime).TotalMilliseconds < 16) // ~60 FPS
            {
                return; // 跳过这次更新
            }
            _lastMouseMoveTime = now;

            try
            {
                var pipe = sender as StraightPipeControl;
                if (pipe == null) return;

                // 获取管道当前中心位置
                var pipeLeft = Canvas.GetLeft(pipe);
                var pipeTop = Canvas.GetTop(pipe);
                if (double.IsNaN(pipeLeft)) pipeLeft = 0;
                if (double.IsNaN(pipeTop)) pipeTop = 0;

                var currentCenter = new Point(
                    pipeLeft + pipe.ActualWidth / 2,
                    pipeTop + pipe.ActualHeight / 2
                );

                // ✅ 使用 QuadTree 优化的吸附检测
                var snapSolution = _pipeSnapService.FindBestSnapSolution(
                    pipe,
                    currentCenter,
                    pipe.PipeId);

                _currentSnapSolution = snapSolution;

                if (snapSolution != null && snapSolution.HasSnap)
                {
                    _pipeSnapService.ShowSnapIndicators(snapSolution.EndPointSnaps);
                    _isPipeSnapping = true;
                }
                else
                {
                    _pipeSnapService.HideSnapIndicator();
                    _isPipeSnapping = false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"管道拖动处理失败: {ex.Message}");
            }
        }


        /// <summary>
        /// 管道拖动完成
        /// </summary>
        private void OnPipeDragCompleted(object sender, PipeDragEventArgs e)
        {
            try
            {
                var pipe = sender as StraightPipeControl;
                if (pipe == null) return;

                // 获取当前位置
                var currentLeft = Canvas.GetLeft(pipe);
                var currentTop = Canvas.GetTop(pipe);
                if (double.IsNaN(currentLeft)) currentLeft = 0;
                if (double.IsNaN(currentTop)) currentTop = 0;

                var currentPosition = new Point(currentLeft, currentTop);

                // ✅ 如果有吸附方案，应用吸附
                if (_currentSnapSolution != null && _currentSnapSolution.HasSnap)
                {
                    var newCenter = _currentSnapSolution.NewCenterPosition;
                    var newLeft = newCenter.X - pipe.ActualWidth / 2;
                    var newTop = newCenter.Y - pipe.ActualHeight / 2;

                    var newPosition = new Point(newLeft, newTop);

                    // ✅ 如果位置发生了变化，创建移动命令
                    if (Math.Abs(currentPosition.X - newPosition.X) > 1 ||
                        Math.Abs(currentPosition.Y - newPosition.Y) > 1)
                    {
                        var moveCommand = new MovePipeCommand(
                            pipe,
                            _pipeOriginalPosition,
                            newPosition);

                        _commandHistory.ExecuteCommand(moveCommand);
                    }

                    var snapTypes = string.Join(", ",
                        _currentSnapSolution.EndPointSnaps.Select(s => s.SnapTarget.Type));

                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $"管道已吸附到: {snapTypes}");
                }
                else
                {
                    // ✅ 没有吸附，但位置可能已经改变，创建移动命令
                    if (Math.Abs(currentPosition.X - _pipeOriginalPosition.X) > 1 ||
                        Math.Abs(currentPosition.Y - _pipeOriginalPosition.Y) > 1)
                    {
                        var moveCommand = new MovePipeCommand(
                            pipe,
                            _pipeOriginalPosition,
                            currentPosition);

                        _commandHistory.ExecuteCommand(moveCommand);
                    }
                }

                // 隐藏吸附指示器
                _pipeSnapService.HideSnapIndicator();
                _isPipeSnapping = false;
                _currentSnapSolution = null;

                // 标记画布已修改
                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("管道移动完成");
                // ✅ 新增：标记四叉树需要重建
                if (_pipeSnapService is PipeSnapService snapService)
                {
                    snapService.MarkQuadTreeDirty();
                }
                Debug.WriteLine($"管道拖动完成: {pipe.PipeId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"管道拖动完成处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 管道调整大小开始
        /// </summary>
        private void OnPipeResizeStarted(object sender, PipeResizeEventArgs e)
        {
            try
            {
                Debug.WriteLine($"管道调整大小开始: {e.PipeId}, 锚点: {e.AnchorPosition}");
                Debug.WriteLine($"原始尺寸: {e.OriginalSize.Width:F0} x {e.OriginalSize.Height:F0}");

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"调整管道大小 ({e.AnchorPosition})...");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"管道调整大小开始处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 管道调整大小中
        /// </summary>
        private void OnPipeResizing(object sender, PipeResizeEventArgs e)
        {
            try
            {
                // 实时更新状态栏
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"管道尺寸: {e.CurrentSize.Width:F0} x {e.CurrentSize.Height:F0}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"管道调整大小处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 管道调整大小完成
        /// </summary>
        private void OnPipeResizeCompleted(object sender, PipeResizeEventArgs e)
        {
            try
            {
                Debug.WriteLine($"管道调整大小完成: {e.PipeId}");
                Debug.WriteLine($"原始尺寸: {e.OriginalSize.Width:F0} x {e.OriginalSize.Height:F0}");
                Debug.WriteLine($"最终尺寸: {e.CurrentSize.Width:F0} x {e.CurrentSize.Height:F0}");

                // 标记画布已修改
                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"管道尺寸已调整为 {e.CurrentSize.Width:F0} x {e.CurrentSize.Height:F0}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"管道调整大小完成处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 直管道旋转开始
        /// </summary>
        private void OnPipeRotateStarted(object sender, PipeRotateEventArgs e)
        {
            try
            {
                Debug.WriteLine($"直管道旋转开始: {e.PipeId}");
                Debug.WriteLine($"原始角度: {e.OriginalAngle:F1}°");

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    "正在旋转直管道... (按住 Shift 键可15度步进)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"直管道旋转开始处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 直管道旋转中
        /// </summary>
        private void OnPipeRotating(object sender, PipeRotateEventArgs e)
        {
            try
            {
                // 实时更新状态栏显示当前角度
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"旋转角度: {e.CurrentAngle:F1}°");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"直管道旋转处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 直管道旋转完成
        /// </summary>
        private void OnPipeRotateCompleted(object sender, PipeRotateEventArgs e)
        {
            try
            {
                Debug.WriteLine($"直管道旋转完成: {e.PipeId}");
                Debug.WriteLine($"原始角度: {e.OriginalAngle:F1}°");
                Debug.WriteLine($"最终角度: {e.CurrentAngle:F1}°");
                Debug.WriteLine($"旋转增量: {(e.CurrentAngle - e.OriginalAngle):F1}°");

                // 标记画布已修改
                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"直管道已旋转至 {e.CurrentAngle:F1}°");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"直管道旋转完成处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 弯管道拖动开始
        /// </summary>
        private void OnElbowPipeDragStarted(object sender, ElbowPipeDragEventArgs e)
        {
            try
            {
                Debug.WriteLine($"弯管道拖动开始: {e.PipeId}");
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("正在移动弯管道...");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"弯管道拖动开始处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 弯管道拖动中（已优化节流）
        /// </summary>
        private void OnElbowPipeDragging(object sender, ElbowPipeDragEventArgs e)
        {
            // ✅ 节流检查
            var now = DateTime.Now;
            if ((now - _lastMouseMoveTime).TotalMilliseconds < 16)
            {
                return;
            }
            _lastMouseMoveTime = now;

            try
            {
                var pipe = sender as ElbowPipeControl;
                if (pipe == null) return;

                var pipeLeft = Canvas.GetLeft(pipe);
                var pipeTop = Canvas.GetTop(pipe);
                if (double.IsNaN(pipeLeft)) pipeLeft = 0;
                if (double.IsNaN(pipeTop)) pipeTop = 0;

                var currentCenter = new Point(
                    pipeLeft + pipe.ActualWidth / 2,
                    pipeTop + pipe.ActualHeight / 2
                );

                var snapSolution = _pipeSnapService.FindBestSnapSolution(
                    pipe,
                    currentCenter,
                    pipe.PipeId);

                _currentSnapSolution = snapSolution;

                if (snapSolution != null && snapSolution.HasSnap)
                {
                    _pipeSnapService.ShowSnapIndicators(snapSolution.EndPointSnaps);
                    _isPipeSnapping = true;
                }
                else
                {
                    _pipeSnapService.HideSnapIndicator();
                    _isPipeSnapping = false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"弯管道拖动处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 弯管道拖动完成
        /// </summary>
        private void OnElbowPipeDragCompleted(object sender, ElbowPipeDragEventArgs e)
        {
            try
            {
                var pipe = sender as ElbowPipeControl;
                if (pipe == null) return;

                if (_currentSnapSolution != null && _currentSnapSolution.HasSnap)
                {
                    var newCenter = _currentSnapSolution.NewCenterPosition;
                    var newLeft = newCenter.X - pipe.ActualWidth / 2;
                    var newTop = newCenter.Y - pipe.ActualHeight / 2;

                    Canvas.SetLeft(pipe, newLeft);
                    Canvas.SetTop(pipe, newTop);

                    Debug.WriteLine($"✅ 弯管道已吸附到位置: 中心({newCenter.X:F0}, {newCenter.Y:F0})");

                    var snapTypes = string.Join(", ",
                        _currentSnapSolution.EndPointSnaps.Select(s => s.SnapTarget.Type));

                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $"弯管道已吸附到: {snapTypes}");
                }

                _pipeSnapService.HideSnapIndicator();
                _isPipeSnapping = false;
                _currentSnapSolution = null;

                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("弯管道移动完成");
                // ✅ 新增：弯管道拖拽完成，标记四叉树需要重建
                if (_pipeSnapService is PipeSnapService snapService)
                {
                    snapService.MarkQuadTreeDirty();
                    Debug.WriteLine("弯管道拖拽完成，已标记四叉树需要重建");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"弯管道拖动完成处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 弯管道调整大小开始
        /// </summary>
        private void OnElbowPipeResizeStarted(object sender, ElbowPipeResizeEventArgs e)
        {
            try
            {
                Debug.WriteLine($"弯管道调整大小开始: {e.PipeId}, 锚点: {e.AnchorPosition}");
                Debug.WriteLine($"原始尺寸: H={e.OriginalSize.HorizontalLength:F0}, " +
                               $"V={e.OriginalSize.VerticalLength:F0}, " +
                               $"W={e.OriginalSize.PipeWidth:F0}");

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"调整弯管道大小 ({e.AnchorPosition})...");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"弯管道调整大小开始处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 弯管道调整大小中
        /// </summary>
        private void OnElbowPipeResizing(object sender, ElbowPipeResizeEventArgs e)
        {
            try
            {
                // 实时更新状态栏
                string sizeInfo = e.AnchorPosition switch
                {
                    ElbowAnchorPosition.Left or ElbowAnchorPosition.Top =>
                        $"臂长: H={e.CurrentSize.HorizontalLength:F0}, V={e.CurrentSize.VerticalLength:F0}",
                    ElbowAnchorPosition.HorizontalWidth or ElbowAnchorPosition.VerticalWidth =>
                        $"管道宽度: {e.CurrentSize.PipeWidth:F0}",
                    _ => $"H={e.CurrentSize.HorizontalLength:F0}, V={e.CurrentSize.VerticalLength:F0}, W={e.CurrentSize.PipeWidth:F0}"
                };

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(sizeInfo);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"弯管道调整大小处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 弯管道调整大小完成
        /// </summary>
        private void OnElbowPipeResizeCompleted(object sender, ElbowPipeResizeEventArgs e)
        {
            try
            {
                Debug.WriteLine($"弯管道调整大小完成: {e.PipeId}");
                Debug.WriteLine($"原始尺寸: H={e.OriginalSize.HorizontalLength:F0}, " +
                               $"V={e.OriginalSize.VerticalLength:F0}, " +
                               $"W={e.OriginalSize.PipeWidth:F0}");
                Debug.WriteLine($"最终尺寸: H={e.CurrentSize.HorizontalLength:F0}, " +
                               $"V={e.CurrentSize.VerticalLength:F0}, " +
                               $"W={e.CurrentSize.PipeWidth:F0}");

                // 标记画布已修改
                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"弯管道已调整: H={e.CurrentSize.HorizontalLength:F0}, " +
                    $"V={e.CurrentSize.VerticalLength:F0}, " +
                    $"W={e.CurrentSize.PipeWidth:F0}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"弯管道调整大小完成处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 弯管道旋转开始
        /// </summary>
        private void OnElbowPipeRotateStarted(object sender, ElbowPipeRotateEventArgs e)
        {
            try
            {
                Debug.WriteLine($"弯管道旋转开始: {e.PipeId}");
                Debug.WriteLine($"原始角度: {e.OriginalAngle:F1}°");

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    "正在旋转弯管道... (按住 Shift 键可15度步进)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"弯管道旋转开始处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 弯管道旋转中
        /// </summary>
        private void OnElbowPipeRotating(object sender, ElbowPipeRotateEventArgs e)
        {
            try
            {
                // 实时更新状态栏显示当前角度
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"旋转角度: {e.CurrentAngle:F1}°");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"弯管道旋转处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 弯管道旋转完成
        /// </summary>
        private void OnElbowPipeRotateCompleted(object sender, ElbowPipeRotateEventArgs e)
        {
            try
            {
                Debug.WriteLine($"弯管道旋转完成: {e.PipeId}");
                Debug.WriteLine($"原始角度: {e.OriginalAngle:F1}°");
                Debug.WriteLine($"最终角度: {e.CurrentAngle:F1}°");

                // 标记画布已修改
                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"弯管道已旋转至 {e.CurrentAngle:F1}°");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"弯管道旋转完成处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// Z形管道拖动开始
        /// </summary>
        private void OnZShapePipeDragStarted(object sender, ZPipeDragEventArgs e)
        {
            try
            {
                Debug.WriteLine($"Z形管道拖动开始: {e.PipeId}");
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("正在移动Z形管道...");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Z形管道拖动开始处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// Z形管道拖动中（已优化节流）
        /// </summary>
        private void OnZShapePipeDragging(object sender, ZPipeDragEventArgs e)
        {
            // ✅ 节流检查
            var now = DateTime.Now;
            if ((now - _lastMouseMoveTime).TotalMilliseconds < 16)
            {
                return;
            }
            _lastMouseMoveTime = now;

            try
            {
                var pipe = sender as ZShapePipeControl;
                if (pipe == null) return;

                var pipeLeft = Canvas.GetLeft(pipe);
                var pipeTop = Canvas.GetTop(pipe);
                if (double.IsNaN(pipeLeft)) pipeLeft = 0;
                if (double.IsNaN(pipeTop)) pipeTop = 0;

                var currentCenter = new Point(
                    pipeLeft + pipe.ActualWidth / 2,
                    pipeTop + pipe.ActualHeight / 2
                );

                var snapSolution = _pipeSnapService.FindBestSnapSolution(
                    pipe,
                    currentCenter,
                    pipe.PipeId);

                _currentSnapSolution = snapSolution;

                if (snapSolution != null && snapSolution.HasSnap)
                {
                    _pipeSnapService.ShowSnapIndicators(snapSolution.EndPointSnaps);
                    _isPipeSnapping = true;
                }
                else
                {
                    _pipeSnapService.HideSnapIndicator();
                    _isPipeSnapping = false;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Z形管道拖动处理失败: {ex.Message}");
            }
        }


        /// <summary>
        /// Z形管道拖动完成
        /// </summary>
        private void OnZShapePipeDragCompleted(object sender, ZPipeDragEventArgs e)
        {
            try
            {
                var pipe = sender as ZShapePipeControl;
                if (pipe == null) return;

                if (_currentSnapSolution != null && _currentSnapSolution.HasSnap)
                {
                    var newCenter = _currentSnapSolution.NewCenterPosition;
                    var newLeft = newCenter.X - pipe.ActualWidth / 2;
                    var newTop = newCenter.Y - pipe.ActualHeight / 2;

                    Canvas.SetLeft(pipe, newLeft);
                    Canvas.SetTop(pipe, newTop);

                    var snapTypes = string.Join(", ",
                        _currentSnapSolution.EndPointSnaps.Select(s => s.SnapTarget.Type));

                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $"Z形管道已吸附到: {snapTypes}");
                }

                _pipeSnapService.HideSnapIndicator();
                _isPipeSnapping = false;
                _currentSnapSolution = null;

                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("Z形管道移动完成");
                // ✅ 新增：Z形管道拖拽完成，标记四叉树需要重建
                if (_pipeSnapService is PipeSnapService snapService)
                {
                    snapService.MarkQuadTreeDirty();
                    Debug.WriteLine("Z形管道拖拽完成，已标记四叉树需要重建");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Z形管道拖动完成处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// Z形管道调整大小开始
        /// </summary>
        private void OnZShapePipeResizeStarted(object sender, ZPipeResizeEventArgs e)
        {
            try
            {
                Debug.WriteLine($"Z形管道调整大小开始: {e.PipeId}, 锚点: {e.AnchorPosition}");
                Debug.WriteLine($"原始尺寸: 上={e.OriginalSize.TopHorizontalLength:F0}, " +
                               $"下={e.OriginalSize.BottomHorizontalLength:F0}, " +
                               $"垂直偏移={e.OriginalSize.VerticalOffset:F0}, " +
                               $"宽度={e.OriginalSize.PipeWidth:F0}");

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"调整Z形管道大小 ({e.AnchorPosition})...");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Z形管道调整大小开始处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// Z形管道调整大小中
        /// </summary>
        private void OnZShapePipeResizing(object sender, ZPipeResizeEventArgs e)
        {
            try
            {
                // 实时更新状态栏
                string sizeInfo = e.AnchorPosition switch
                {
                    ZAnchorPosition.TopLength => $"上方长度: {e.CurrentSize.TopHorizontalLength:F0}",
                    ZAnchorPosition.BottomLength => $"下方长度: {e.CurrentSize.BottomHorizontalLength:F0}",
                    ZAnchorPosition.VerticalOffset => $"垂直偏移: {e.CurrentSize.VerticalOffset:F0}",
                    ZAnchorPosition.PipeWidth => $"管道宽度: {e.CurrentSize.PipeWidth:F0}",
                    _ => $"上={e.CurrentSize.TopHorizontalLength:F0}, 下={e.CurrentSize.BottomHorizontalLength:F0}"
                };

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(sizeInfo);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Z形管道调整大小处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// Z形管道调整大小完成
        /// </summary>
        private void OnZShapePipeResizeCompleted(object sender, ZPipeResizeEventArgs e)
        {
            try
            {
                Debug.WriteLine($"Z形管道调整大小完成: {e.PipeId}");
                Debug.WriteLine($"最终尺寸: 上={e.CurrentSize.TopHorizontalLength:F0}, " +
                               $"下={e.CurrentSize.BottomHorizontalLength:F0}, " +
                               $"垂直偏移={e.CurrentSize.VerticalOffset:F0}, " +
                               $"宽度={e.CurrentSize.PipeWidth:F0}");

                // 标记画布已修改
                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"Z形管道已调整: 上={e.CurrentSize.TopHorizontalLength:F0}, " +
                    $"下={e.CurrentSize.BottomHorizontalLength:F0}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Z形管道调整大小完成处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// Z形管道旋转开始
        /// </summary>
        private void OnZShapePipeRotateStarted(object sender, ZPipeRotateEventArgs e)
        {
            try
            {
                Debug.WriteLine($"Z形管道旋转开始: {e.PipeId}");
                Debug.WriteLine($"原始角度: {e.OriginalAngle:F1}°");

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    "正在旋转Z形管道... (按住 Shift 键可15度步进)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Z形管道旋转开始处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// Z形管道旋转中
        /// </summary>
        private void OnZShapePipeRotating(object sender, ZPipeRotateEventArgs e)
        {
            try
            {
                // 实时更新状态栏显示当前角度
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"旋转角度: {e.CurrentAngle:F1}°");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Z形管道旋转处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// Z形管道旋转完成
        /// </summary>
        private void OnZShapePipeRotateCompleted(object sender, ZPipeRotateEventArgs e)
        {
            try
            {
                Debug.WriteLine($"Z形管道旋转完成: {e.PipeId}");
                Debug.WriteLine($"原始角度: {e.OriginalAngle:F1}°");
                Debug.WriteLine($"最终角度: {e.CurrentAngle:F1}°");

                // 标记画布已修改
                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"Z形管道已旋转至 {e.CurrentAngle:F1}°");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Z形管道旋转完成处理失败: {ex.Message}");
            }
        }

        #endregion

        #region 管道数据保存和恢复

        /// <summary>
        /// 响应收集管道数据请求
        /// </summary>
        private void OnCollectPipeDataRequest(Action<List<CanvasPipeData>> callback)
        {
            var pipeDataList = new List<CanvasPipeData>();

            try
            {
                Debug.WriteLine("========== 开始收集管道数据 ==========");

                // 遍历画布上的所有管道控件
                foreach (var child in DesignCanvas.Children)
                {
                    CanvasPipeData pipeData = null;

                    // 处理直管道
                    if (child is StraightPipeControl straightPipe)
                    {
                        var left = Canvas.GetLeft(straightPipe);
                        var top = Canvas.GetTop(straightPipe);

                        pipeData = new CanvasPipeData
                        {
                            PipeId = straightPipe.PipeId,
                            PipeType = "Straight",
                            X = double.IsNaN(left) ? 0 : left,
                            Y = double.IsNaN(top) ? 0 : top,
                            ZIndex = Panel.GetZIndex(straightPipe),
                            PipeWidth = straightPipe.PipeWidth,
                            RotationAngle = straightPipe.RotationAngle,
                            PipeLength = straightPipe.PipeLength
                        };

                        Debug.WriteLine($"收集直管道: {straightPipe.PipeId} at ({pipeData.X:F0}, {pipeData.Y:F0})");
                    }
                    // 处理弯管道
                    else if (child is ElbowPipeControl elbowPipe)
                    {
                        var left = Canvas.GetLeft(elbowPipe);
                        var top = Canvas.GetTop(elbowPipe);

                        pipeData = new CanvasPipeData
                        {
                            PipeId = elbowPipe.PipeId,
                            PipeType = "Elbow",
                            X = double.IsNaN(left) ? 0 : left,
                            Y = double.IsNaN(top) ? 0 : top,
                            ZIndex = Panel.GetZIndex(elbowPipe),
                            PipeWidth = elbowPipe.PipeWidth,
                            RotationAngle = elbowPipe.RotationAngle,
                            HorizontalLength = elbowPipe.HorizontalLength,
                            VerticalLength = elbowPipe.VerticalLength
                        };

                        Debug.WriteLine($"收集弯管道: {elbowPipe.PipeId} at ({pipeData.X:F0}, {pipeData.Y:F0})");
                    }
                    // 处理Z形管道
                    else if (child is ZShapePipeControl zPipe)
                    {
                        var left = Canvas.GetLeft(zPipe);
                        var top = Canvas.GetTop(zPipe);

                        pipeData = new CanvasPipeData
                        {
                            PipeId = zPipe.PipeId,
                            PipeType = "ZShape",
                            X = double.IsNaN(left) ? 0 : left,
                            Y = double.IsNaN(top) ? 0 : top,
                            ZIndex = Panel.GetZIndex(zPipe),
                            PipeWidth = zPipe.PipeWidth,
                            RotationAngle = zPipe.RotationAngle,
                            TopHorizontalLength = zPipe.TopHorizontalLength,
                            BottomHorizontalLength = zPipe.BottomHorizontalLength,
                            VerticalOffset = zPipe.VerticalOffset
                        };

                        Debug.WriteLine($"收集Z形管道: {zPipe.PipeId} at ({pipeData.X:F0}, {pipeData.Y:F0})");
                    }

                    if (pipeData != null)
                    {
                        pipeDataList.Add(pipeData);
                    }
                }

                Debug.WriteLine($"========== 收集了 {pipeDataList.Count} 个管道 ==========");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 收集管道数据失败: {ex.Message}");
            }

            callback?.Invoke(pipeDataList);
        }






        /// <summary>
        /// 创建恢复的直管道
        /// </summary>
        private StraightPipeControl CreateRestoredStraightPipe(CanvasPipeData pipeData)
        {
            var pipe = new StraightPipeControl
            {
                PipeId = pipeData.PipeId,
                PipeLength = pipeData.PipeLength ?? 80,
                PipeWidth = pipeData.PipeWidth,
                RotationAngle = pipeData.RotationAngle,
                IsSelected = false
            };

            // 订阅事件
            SubscribeStraightPipeEvents(pipe);

            return pipe;
        }

        /// <summary>
        /// 创建恢复的弯管道
        /// </summary>
        private ElbowPipeControl CreateRestoredElbowPipe(CanvasPipeData pipeData)
        {
            var pipe = new ElbowPipeControl
            {
                PipeId = pipeData.PipeId,
                HorizontalLength = pipeData.HorizontalLength ?? 35,
                VerticalLength = pipeData.VerticalLength ?? 42,
                PipeWidth = pipeData.PipeWidth,
                RotationAngle = pipeData.RotationAngle,
                IsSelected = false
            };

            // 订阅事件
            SubscribeElbowPipeEvents(pipe);

            return pipe;
        }

        /// <summary>
        /// 创建恢复的Z形管道
        /// </summary>
        private ZShapePipeControl CreateRestoredZShapePipe(CanvasPipeData pipeData)
        {
            var pipe = new ZShapePipeControl
            {
                PipeId = pipeData.PipeId,
                TopHorizontalLength = pipeData.TopHorizontalLength ?? 20,
                BottomHorizontalLength = pipeData.BottomHorizontalLength ?? 20,
                VerticalOffset = pipeData.VerticalOffset ?? 40,
                PipeWidth = pipeData.PipeWidth,
                RotationAngle = pipeData.RotationAngle,
                IsSelected = false
            };

            // 订阅事件
            SubscribeZShapePipeEvents(pipe);

            return pipe;
        }

        #endregion



        #region 监控卡片位置管理

        /// <summary>
        /// 监控卡片位置字典 - DeviceId -> Position
        /// </summary>
        private readonly Dictionary<string, Point> _monitoringCardPositions = new();

        /// <summary>
        /// 获取监控卡片位置
        /// </summary>
        private void OnGetMonitoringCardPositionsRequest(Action<Dictionary<string, Point>> callback)
        {
            try
            {
                var positions = new Dictionary<string, Point>();

                foreach (var kvp in _monitoringCards)
                {
                    var deviceId = kvp.Key;
                    var card = kvp.Value;

                    var left = Canvas.GetLeft(card);
                    var top = Canvas.GetTop(card);

                    if (!double.IsNaN(left) && !double.IsNaN(top))
                    {
                        positions[deviceId] = new Point(left, top);
                        Debug.WriteLine($"保存监控卡片位置: {deviceId} at ({left:F0}, {top:F0})");
                    }
                }

                Debug.WriteLine($"返回 {positions.Count} 个监控卡片位置");
                callback?.Invoke(positions);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取监控卡片位置失败: {ex.Message}");
                callback?.Invoke(new Dictionary<string, Point>());
            }
        }

        /// <summary>
        /// 恢复监控卡片位置
        /// </summary>
        private void OnRestoreMonitoringCardPositions(Dictionary<string, Point> positions)
        {
            try
            {
                if (positions == null || positions.Count == 0)
                {
                    Debug.WriteLine("没有需要恢复的监控卡片位置");
                    return;
                }

                Debug.WriteLine($"开始恢复 {positions.Count} 个监控卡片位置");

                // 保存到字典，延迟使用
                _monitoringCardPositions.Clear();
                foreach (var kvp in positions)
                {
                    _monitoringCardPositions[kvp.Key] = kvp.Value;
                    Debug.WriteLine($"记录监控卡片位置: {kvp.Key} at ({kvp.Value.X:F0}, {kvp.Value.Y:F0})");
                }

                Debug.WriteLine("监控卡片位置恢复完成");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"恢复监控卡片位置失败: {ex.Message}");
            }
        }

        #endregion

        #region 布局更新优化

        /// <summary>
        /// 初始化布局更新定时器
        /// </summary>
        private void InitializeLayoutUpdateTimer()
        {
            _layoutUpdateTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50) // 每50ms最多更新一次
            };
            _layoutUpdateTimer.Tick += OnLayoutUpdateTimerTick;

            Debug.WriteLine("布局更新定时器已初始化");
        }

        /// <summary>
        /// 调度延迟布局更新（替换所有 UpdateLayout 调用）
        /// </summary>
        private void ScheduleLayoutUpdate()
        {
            _needsLayoutUpdate = true;

            if (!_layoutUpdateTimer.IsEnabled)
            {
                _layoutUpdateTimer.Start();
            }
        }

        /// <summary>
        /// 布局更新定时器触发
        /// </summary>
        private void OnLayoutUpdateTimerTick(object sender, EventArgs e)
        {
            _layoutUpdateTimer.Stop();

            if (_needsLayoutUpdate)
            {
                DesignCanvas.UpdateLayout();
                _needsLayoutUpdate = false;
            }
        }

        #endregion

        #region 管道创建方法

        /// <summary>
        /// 创建直管道 - 优化版
        /// </summary>
        private void CreateStraightPipe(Point position)
        {
            var pipe = new StraightPipeControl
            {
                PipeId = Guid.NewGuid().ToString(),
                PipeLength = 80,
                PipeWidth = 8
            };

            // ✅ 启用管道级别缓存
            RenderOptions.SetCachingHint(pipe, CachingHint.Cache);
            pipe.CacheMode = new BitmapCache
            {
                EnableClearType = false,
                RenderAtScale = 1.0,
                SnapsToDevicePixels = true
            };

            // 订阅事件
            SubscribeStraightPipeEvents(pipe);

            // 使用命令创建管道
            var addCommand = new AddPipeCommand(
                DesignCanvas,
                pipe,
                position,
                "直管道",
                _pipeConnectionService);

            _commandHistory.ExecuteCommand(addCommand);

            // 添加到管道集合
            _pipes[pipe.PipeId] = pipe;

            // 标记四叉树需要重建
            if (_pipeSnapService is PipeSnapService snapService)
            {
                snapService.MarkQuadTreeDirty();
            }

            // 标记画布已修改
            _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

            Debug.WriteLine($"直管道已创建（带缓存）: {pipe.PipeId}");
        }

        /// <summary>
        /// 订阅直管道事件
        /// </summary>
        private void SubscribeStraightPipeEvents(StraightPipeControl pipe)
        {
            pipe.PipeDragStarted += OnPipeDragStarted;
            pipe.PipeDragging += OnPipeDragging;
            pipe.PipeDragCompleted += OnPipeDragCompleted;
            pipe.PipeResizeStarted += OnPipeResizeStarted;
            pipe.PipeResizing += OnPipeResizing;
            pipe.PipeResizeCompleted += OnPipeResizeCompleted;
            pipe.PipeRotateStarted += OnPipeRotateStarted;
            pipe.PipeRotating += OnPipeRotating;
            pipe.PipeRotateCompleted += OnPipeRotateCompleted;
            // ✅ 订阅管道选中事件（新增）
            pipe.PipeSelected += OnPipeSelectedFromControl;
            pipe.PipeDeselected += OnPipeDeselectedFromControl;
        }

        /// <summary>
        /// 管道控件通知画布：我被选中了
        /// </summary>
        private void OnPipeSelectedFromControl(object sender, string pipeId)
        {
            try
            {
                Debug.WriteLine($"========== 画布收到管道选中通知 ==========");
                Debug.WriteLine($"PipeId: {pipeId}");
                Debug.WriteLine($"Sender: {sender?.GetType().Name}");
                Debug.WriteLine($"=========================================");

                var pipeElement = sender as FrameworkElement;
                if (pipeElement == null)
                {
                    Debug.WriteLine("⚠️ 警告：发送者不是 FrameworkElement");
                    return;
                }

                // ✅ 清除其他选择
                ClearDeviceSelection();
                ClearAllPipeSelectionsExcept(pipeId);

                // ✅ 记录选中状态
                _selectedPipeId = pipeId;
                _selectedPipeElement = pipeElement;

                Debug.WriteLine($"✅ 画布已确认管道选中: {pipeId}");

                // 更新状态栏
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"已选中管道: {pipeId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 处理管道选中通知失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 管道控件通知画布：我被取消选中了
        /// </summary>
        private void OnPipeDeselectedFromControl(object sender, string pipeId)
        {
            try
            {
                Debug.WriteLine($"画布收到管道取消选中通知: {pipeId}");

                if (_selectedPipeId == pipeId)
                {
                    _selectedPipeId = null;
                    _selectedPipeElement = null;
                    Debug.WriteLine("画布已清除管道选择");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理管道取消选中通知失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清除除指定管道外的所有管道选择
        /// </summary>
        private void ClearAllPipeSelectionsExcept(string exceptPipeId)
        {
            foreach (var child in DesignCanvas.Children)
            {
                if (child is StraightPipeControl straightPipe && straightPipe.PipeId != exceptPipeId)
                {
                    straightPipe.IsSelected = false;
                }
                else if (child is ElbowPipeControl elbowPipe && elbowPipe.PipeId != exceptPipeId)
                {
                    elbowPipe.IsSelected = false;
                }
                else if (child is ZShapePipeControl zPipe && zPipe.PipeId != exceptPipeId)
                {
                    zPipe.IsSelected = false;
                }
            }
        }

        /// <summary>
        /// 创建弯管道 - 使用命令模式
        /// </summary>
        private void CreateElbowPipe(Point position)
        {
            var pipe = new ElbowPipeControl
            {
                PipeId = Guid.NewGuid().ToString(),
                HorizontalLength = 35,
                VerticalLength = 42,
                PipeWidth = 8
            };

            // 订阅事件
            SubscribeElbowPipeEvents(pipe);

            // ✅ 使用命令创建管道
            var addCommand = new AddPipeCommand(
                DesignCanvas,
                pipe,
                position,
                "弯管道",
                _pipeConnectionService);

            _commandHistory.ExecuteCommand(addCommand);

            // 添加到管道集合
            _pipes[pipe.PipeId] = pipe;

            // 标记画布已修改
            _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

            Debug.WriteLine($"弯管道已创建: {pipe.PipeId}");
        }

        /// <summary>
        /// 订阅弯管道事件
        /// </summary>
        private void SubscribeElbowPipeEvents(ElbowPipeControl pipe)
        {
            pipe.PipeDragStarted += OnElbowPipeDragStarted;
            pipe.PipeDragging += OnElbowPipeDragging;
            pipe.PipeDragCompleted += OnElbowPipeDragCompleted;
            pipe.PipeResizeStarted += OnElbowPipeResizeStarted;
            pipe.PipeResizing += OnElbowPipeResizing;
            pipe.PipeResizeCompleted += OnElbowPipeResizeCompleted;
            pipe.PipeRotateStarted += OnElbowPipeRotateStarted;
            pipe.PipeRotating += OnElbowPipeRotating;
            pipe.PipeRotateCompleted += OnElbowPipeRotateCompleted;
            // ✅ 订阅选中事件
            pipe.PipeSelected += OnPipeSelectedFromControl;
            pipe.PipeDeselected += OnPipeDeselectedFromControl;
        }

        /// <summary>
        /// 创建Z形管道 - 使用命令模式
        /// </summary>
        private void CreateZShapePipe(Point position)
        {
            var pipe = new ZShapePipeControl
            {
                PipeId = Guid.NewGuid().ToString(),
                TopHorizontalLength = 20,
                BottomHorizontalLength = 20,
                VerticalOffset = 40,
                PipeWidth = 8
            };

            // 订阅事件
            SubscribeZShapePipeEvents(pipe);

            // ✅ 使用命令创建管道
            var addCommand = new AddPipeCommand(
                DesignCanvas,
                pipe,
                position,
                "Z形管道",
                _pipeConnectionService);

            _commandHistory.ExecuteCommand(addCommand);

            // 添加到管道集合
            _pipes[pipe.PipeId] = pipe;

            // 标记画布已修改
            _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

            Debug.WriteLine($"Z形管道已创建: {pipe.PipeId}");
        }

        /// <summary>
        /// 订阅Z形管道事件
        /// </summary>
        private void SubscribeZShapePipeEvents(ZShapePipeControl pipe)
        {
            pipe.PipeDragStarted += OnZShapePipeDragStarted;
            pipe.PipeDragging += OnZShapePipeDragging;
            pipe.PipeDragCompleted += OnZShapePipeDragCompleted;
            pipe.PipeResizeStarted += OnZShapePipeResizeStarted;
            pipe.PipeResizing += OnZShapePipeResizing;
            pipe.PipeResizeCompleted += OnZShapePipeResizeCompleted;
            pipe.PipeRotateStarted += OnZShapePipeRotateStarted;
            pipe.PipeRotating += OnZShapePipeRotating;
            pipe.PipeRotateCompleted += OnZShapePipeRotateCompleted;
            // ✅ 订阅选中事件
            pipe.PipeSelected += OnPipeSelectedFromControl;
            pipe.PipeDeselected += OnPipeDeselectedFromControl;
        }

        #endregion


        #region 阀门管理

        /// <summary>
        /// 创建阀门图标（用于画布上的小图标显示）
        /// </summary>
        private UIElement CreateValveIcon(double targetWidth, double targetHeight)
        {
            try
            {
                var valveControl = new ValveControl
                {
                    IsHitTestVisible = true,  // ✅ 阀门需要交互（点击切换状态）
                    Cursor = System.Windows.Input.Cursors.Hand,
                    IsOpen = false  // 初始为关闭状态
                };

                // ✅ 移除：Loaded 事件中的边框隐藏逻辑
                // 因为设备库中的阀门不需要选择功能，画布上的阀门才需要

                // ✅ 包装在 Viewbox 中
                var viewbox = new System.Windows.Controls.Viewbox
                {
                    Width = targetWidth,
                    Height = targetHeight,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    StretchDirection = System.Windows.Controls.StretchDirection.Both,
                    Child = valveControl,
                    IsHitTestVisible = true  // ✅ 允许交互
                };

                Debug.WriteLine($"创建阀门图标容器: {targetWidth}x{targetHeight}");
                return viewbox;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 创建阀门图标失败: {ex.Message}");
                return CreateDefaultIcon(targetWidth, targetHeight, "阀门");
            }
        }
        /// <summary>
        /// 订阅设备中包含的阀门控件事件
        /// </summary>
        private void SubscribeValveEventsInDevice(Border deviceControl)
        {
            try
            {
                // 查找手动阀门
                var valve = FindVisualChild<ValveControl>(deviceControl);
                if (valve != null)
                {
                    Debug.WriteLine($"发现手动阀门控件，准备订阅事件: {valve.ValveId}");
                    SubscribeValveEvents(valve);
                    Debug.WriteLine($"✅ 手动阀门事件订阅完成: {valve.ValveId}");
                }

                // ✅ 新增：查找电磁阀
                var solenoidValve = FindVisualChild<SolenoidValveControl>(deviceControl);
                if (solenoidValve != null)
                {
                    Debug.WriteLine($"发现电磁阀控件，准备订阅事件: {solenoidValve.ValveId}");
                    SubscribeSolenoidValveEvents(solenoidValve);
                    Debug.WriteLine($"✅ 电磁阀事件订阅完成: {solenoidValve.ValveId}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"订阅阀门事件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 创建电磁阀图标（用于画布上的小图标显示）
        /// </summary>
        private UIElement CreateSolenoidValveIcon(double targetWidth, double targetHeight)
        {
            try
            {
                var solenoidValveControl = new SolenoidValveControl
                {
                    IsHitTestVisible = true,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    IsOpen = false  // 初始为关闭状态
                };

                // ✅ 包装在 Viewbox 中
                var viewbox = new System.Windows.Controls.Viewbox
                {
                    Width = targetWidth,
                    Height = targetHeight,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    StretchDirection = System.Windows.Controls.StretchDirection.Both,
                    Child = solenoidValveControl,
                    IsHitTestVisible = true
                };

                Debug.WriteLine($"创建电磁阀图标容器: {targetWidth}x{targetHeight}");
                return viewbox;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 创建电磁阀图标失败: {ex.Message}");
                return CreateDefaultIcon(targetWidth, targetHeight, "电磁阀");
            }
        }
        #endregion
        #region 阀门事件处理

        /// <summary>
        /// 订阅阀门事件
        /// </summary>
        private void SubscribeValveEvents(ValveControl valve)
        {
            // 拖动事件
            valve.ValveDragStarted += OnValveDragStarted;
            valve.ValveDragging += OnValveDragging;
            valve.ValveDragCompleted += OnValveDragCompleted;

            // ✅ 旋转事件
            valve.ValveRotateStarted += OnValveRotateStarted;
            valve.ValveRotating += OnValveRotating;
            valve.ValveRotateCompleted += OnValveRotateCompleted;

            // 选中事件
            valve.ValveSelected += OnValveSelectedFromControl;
            valve.ValveDeselected += OnValveDeselectedFromControl;

            Debug.WriteLine($"阀门事件已订阅: {valve.ValveId}");
        }

        /// <summary>
        /// 阀门拖动开始
        /// </summary>
        private void OnValveDragStarted(object sender, ValveDragEventArgs e)
        {
            try
            {
                Debug.WriteLine($"阀门拖动开始: {e.ValveId}");
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("正在移动阀门...");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"阀门拖动开始处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 阀门拖动中
        /// </summary>
        private void OnValveDragging(object sender, ValveDragEventArgs e)
        {
            // 节流检查
            var now = DateTime.Now;
            if ((now - _lastMouseMoveTime).TotalMilliseconds < 16)
            {
                return;
            }
            _lastMouseMoveTime = now;

            try
            {
                // 可以添加吸附逻辑（与管道类似）
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"阀门拖动处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 阀门拖动完成
        /// </summary>
        private void OnValveDragCompleted(object sender, ValveDragEventArgs e)
        {
            try
            {
                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("阀门移动完成");

                Debug.WriteLine($"阀门拖动完成: {e.ValveId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"阀门拖动完成处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 阀门旋转开始
        /// </summary>
        private void OnValveRotateStarted(object sender, ValveRotateEventArgs e)
        {
            try
            {
                Debug.WriteLine($"阀门旋转开始: {e.ValveId}");
                Debug.WriteLine($"原始角度: {e.OriginalAngle:F1}°");

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    "正在旋转阀门... (按住 Shift 键可15度步进)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"阀门旋转开始处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 阀门旋转中
        /// </summary>
        private void OnValveRotating(object sender, ValveRotateEventArgs e)
        {
            try
            {
                // 实时更新状态栏显示当前角度
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"旋转角度: {e.CurrentAngle:F1}°");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"阀门旋转处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 阀门旋转完成
        /// </summary>
        private void OnValveRotateCompleted(object sender, ValveRotateEventArgs e)
        {
            try
            {
                Debug.WriteLine($"阀门旋转完成: {e.ValveId}");
                Debug.WriteLine($"原始角度: {e.OriginalAngle:F1}°");
                Debug.WriteLine($"最终角度: {e.CurrentAngle:F1}°");

                // 标记画布已修改
                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"阀门已旋转至 {e.CurrentAngle:F1}°");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"阀门旋转完成处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 阀门选中
        /// </summary>
        private void OnValveSelectedFromControl(object sender, string valveId)
        {
            try
            {
                Debug.WriteLine($"阀门被选中: {valveId}");

                // 清除其他选择
                ClearDeviceSelection();
                ClearAllPipeSelectionsExcept(null);

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"已选中阀门: {valveId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理阀门选中失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 阀门取消选中
        /// </summary>
        private void OnValveDeselectedFromControl(object sender, string valveId)
        {
            try
            {
                Debug.WriteLine($"阀门取消选中: {valveId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理阀门取消选中失败: {ex.Message}");
            }
        }
        /// <summary>
        /// ✅ 新增：清除所有阀门选择
        /// </summary>
        private void ClearAllValveSelections()
        {
            try
            {
                // 清除手动阀门
                var valves = FindAllValvesInCanvas();
                foreach (var valve in valves)
                {
                    valve.IsSelected = false;
                    Debug.WriteLine($"清除手动阀门选择: {valve.ValveId}");
                }

                // ✅ 清除电磁阀
                var solenoidValves = FindAllSolenoidValvesInCanvas();
                foreach (var solenoidValve in solenoidValves)
                {
                    solenoidValve.IsSelected = false;
                    Debug.WriteLine($"清除电磁阀选择: {solenoidValve.ValveId}");
                }

                Debug.WriteLine($"清除了 {valves.Count} 个手动阀门和 {solenoidValves.Count} 个电磁阀的选中状态");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清除阀门选择失败: {ex.Message}");
            }
        }

        /// <summary>
        /// ✅ 新增：查找画布中所有阀门控件
        /// </summary>
        private List<ValveControl> FindAllValvesInCanvas()
        {
            var valves = new List<ValveControl>();

            try
            {
                // 遍历画布上的所有设备
                foreach (var child in DesignCanvas.Children)
                {
                    if (child is Border deviceBorder)
                    {
                        // 在设备容器中查找阀门控件
                        var valve = FindVisualChild<ValveControl>(deviceBorder);
                        if (valve != null)
                        {
                            valves.Add(valve);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"查找阀门控件失败: {ex.Message}");
            }

            return valves;
        }
        /// <summary>
        /// ✅ 新增：查找画布中所有电磁阀控件
        /// </summary>
        private List<SolenoidValveControl> FindAllSolenoidValvesInCanvas()
        {
            var solenoidValves = new List<SolenoidValveControl>();

            try
            {
                foreach (var child in DesignCanvas.Children)
                {
                    if (child is Border deviceBorder)
                    {
                        var solenoidValve = FindVisualChild<SolenoidValveControl>(deviceBorder);
                        if (solenoidValve != null)
                        {
                            solenoidValves.Add(solenoidValve);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"查找电磁阀控件失败: {ex.Message}");
            }

            return solenoidValves;
        }
        #endregion

        #region 电磁阀事件处理

        /// <summary>
        /// 订阅电磁阀事件
        /// </summary>
        private void SubscribeSolenoidValveEvents(SolenoidValveControl solenoidValve)
        {
            // 选中事件
            solenoidValve.ValveSelected += OnSolenoidValveSelectedFromControl;
            solenoidValve.ValveDeselected += OnSolenoidValveDeselectedFromControl;

            Debug.WriteLine($"电磁阀事件已订阅: {solenoidValve.ValveId}");
        }

        /// <summary>
        /// 电磁阀选中
        /// </summary>
        private void OnSolenoidValveSelectedFromControl(object sender, string valveId)
        {
            try
            {
                Debug.WriteLine($"电磁阀被选中: {valveId}");

                // 清除其他选择
                ClearDeviceSelection();
                ClearAllPipeSelectionsExcept(null);
                ClearAllValveSelections(); // 清除手动阀门选择

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"已选中电磁阀: {valveId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理电磁阀选中失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 电磁阀取消选中
        /// </summary>
        private void OnSolenoidValveDeselectedFromControl(object sender, string valveId)
        {
            try
            {
                Debug.WriteLine($"电磁阀取消选中: {valveId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理电磁阀取消选中失败: {ex.Message}");
            }
        }

        #endregion

        #region 流量计
        /// <summary>
        /// 创建流量计图标（用于画布上的小图标显示）
        /// </summary>
        private UIElement CreateMassFlowMeterIcon(double targetWidth, double targetHeight)
        {
            try
            {
                var meterControl = new MassFlowMeterControl
                {
                    IsHitTestVisible = true,  // ✅ 允许交互（点击切换流动状态）
                    Cursor = System.Windows.Input.Cursors.Hand,
                    CurrentFlow = 0.0,        // 初始流量为0
                    TargetFlow = 125.0,       // 默认目标流量
                    MaxFlow = 150.0,          // 最大流量
                    IsFlowing = true         // 初始为关闭状态
                };

                // ✅ 订阅流量计事件（如果需要）
                SubscribeMassFlowMeterEvents(meterControl);

                // ✅ 包装在 Viewbox 中
                var viewbox = new System.Windows.Controls.Viewbox
                {
                    Width = targetWidth,
                    Height = targetHeight,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    StretchDirection = System.Windows.Controls.StretchDirection.Both,
                    Child = meterControl,
                    IsHitTestVisible = true  // ✅ 允许交互
                };

                Debug.WriteLine($"创建流量计图标容器: {targetWidth}x{targetHeight}");
                return viewbox;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 创建流量计图标失败: {ex.Message}");
                return CreateDefaultIcon(targetWidth, targetHeight, "流量计");
            }
        }

        #region 流量计事件处理

        /// <summary>
        /// 订阅设备中包含的流量计控件事件
        /// </summary>
        private void SubscribeMassFlowMeterEvents(MassFlowMeterControl meter)
        {
            try
            {
                // 选中事件
                meter.MeterSelected += OnMassFlowMeterSelectedFromControl;
                meter.MeterDeselected += OnMassFlowMeterDeselectedFromControl;

                // 旋转事件
                meter.MeterRotateStarted += OnMassFlowMeterRotateStarted;
                meter.MeterRotating += OnMassFlowMeterRotating;
                meter.MeterRotateCompleted += OnMassFlowMeterRotateCompleted;

                // 流动状态变化事件
                meter.FlowStateChanged += OnMassFlowMeterFlowStateChanged;

                // 目标流量变化事件
                meter.TargetFlowChanged += OnMassFlowMeterTargetFlowChanged;

                Debug.WriteLine($"流量计事件已订阅: {meter.MeterId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"订阅流量计事件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 流量计选中
        /// </summary>
        private void OnMassFlowMeterSelectedFromControl(object sender, string meterId)
        {
            try
            {
                Debug.WriteLine($"流量计被选中: {meterId}");

                // 清除其他选择
                ClearDeviceSelection();
                ClearAllPipeSelectionsExcept(null);
                ClearAllValveSelections();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"已选中流量计: {meterId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理流量计选中失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 流量计取消选中
        /// </summary>
        private void OnMassFlowMeterDeselectedFromControl(object sender, string meterId)
        {
            try
            {
                Debug.WriteLine($"流量计取消选中: {meterId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理流量计取消选中失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 流量计旋转开始
        /// </summary>
        private void OnMassFlowMeterRotateStarted(object sender, MeterRotateEventArgs e)
        {
            try
            {
                Debug.WriteLine($"流量计旋转开始: {e.MeterId}");
                Debug.WriteLine($"原始角度: {e.OriginalAngle:F1}°");

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    "正在旋转流量计... (按住 Shift 键可15度步进)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"流量计旋转开始处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 流量计旋转中
        /// </summary>
        private void OnMassFlowMeterRotating(object sender, MeterRotateEventArgs e)
        {
            try
            {
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"旋转角度: {e.CurrentAngle:F1}°");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"流量计旋转处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 流量计旋转完成
        /// </summary>
        private void OnMassFlowMeterRotateCompleted(object sender, MeterRotateEventArgs e)
        {
            try
            {
                Debug.WriteLine($"流量计旋转完成: {e.MeterId}");
                Debug.WriteLine($"原始角度: {e.OriginalAngle:F1}°");
                Debug.WriteLine($"最终角度: {e.CurrentAngle:F1}°");

                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"流量计已旋转至 {e.CurrentAngle:F1}°");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"流量计旋转完成处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 流量计流动状态变化
        /// </summary>
        private void OnMassFlowMeterFlowStateChanged(object sender, FlowStateChangedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"流量计 {e.MeterId} 流动状态变化: {(e.IsFlowing ? "启动" : "停止")}");

                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    e.IsFlowing ? "流量计已启动" : "流量计已停止");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"流量计流动状态变化处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 流量计目标流量变化
        /// </summary>
        private void OnMassFlowMeterTargetFlowChanged(object sender, FlowValueChangedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"流量计 {e.MeterId} 目标流量变化: {e.OldValue:F1} -> {e.NewValue:F1}");

                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"流量计目标流量: {e.NewValue:F1} kg/h");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"流量计目标流量变化处理失败: {ex.Message}");
            }
        }

        #endregion

        /// <summary>
        /// ✅ 新增：清除所有流量计选择
        /// </summary>
        private void ClearAllFlowMeterSelections()
        {
            try
            {
                var flowMeters = FindAllFlowMetersInCanvas();
                foreach (var meter in flowMeters)
                {
                    meter.HideSelection();
                    Debug.WriteLine($"清除流量计选择: {meter.MeterId}");
                }

                Debug.WriteLine($"清除了 {flowMeters.Count} 个流量计的选中状态");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清除流量计选择失败: {ex.Message}");
            }
        }

        /// <summary>
        /// ✅ 新增：查找画布中所有流量计控件
        /// </summary>
        private List<MassFlowMeterControl> FindAllFlowMetersInCanvas()
        {
            var flowMeters = new List<MassFlowMeterControl>();

            try
            {
                foreach (var child in DesignCanvas.Children)
                {
                    if (child is Border deviceBorder)
                    {
                        var meter = FindVisualChild<MassFlowMeterControl>(deviceBorder);
                        if (meter != null)
                        {
                            flowMeters.Add(meter);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"查找流量计控件失败: {ex.Message}");
            }

            return flowMeters;
        }
        #endregion
        #region 微反应器

        /// <summary>
        /// 创建微反应器图标（用于画布上的小图标显示）
        /// </summary>
        private UIElement CreateMicroreactorIcon(double targetWidth, double targetHeight)
        {
            try
            {
                var reactorControl = new MicroreactorControl
                {
                    IsHitTestVisible = true,  // ✅ 允许交互
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Temperature1 = 85.5,
                    Temperature2 = 92.3,
                    Temperature3 = 98.7,
                    IsReacting = false,  // 初始为关闭状态
                    IsFlow1Enabled = true,
                    IsFlow2Enabled = true,
                    IsFlow3Enabled = true,
                    FlowRate = 100.0         // 默认流速
                };
                // ✅ 订阅微反应器事件
                SubscribeMicroreactorEvents(reactorControl);

                // ✅ 包装在 Viewbox 中
                var viewbox = new System.Windows.Controls.Viewbox
                {
                    Width = targetWidth,
                    Height = targetHeight,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    StretchDirection = System.Windows.Controls.StretchDirection.Both,
                    Child = reactorControl,
                    IsHitTestVisible = true  // ✅ 允许交互
                };

                Debug.WriteLine($"创建微反应器图标容器: {targetWidth}x{targetHeight}");
                return viewbox;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 创建微反应器图标失败: {ex.Message}");
                return CreateDefaultIcon(targetWidth, targetHeight, "微反应器");
            }
        }

        #endregion
        #region 微反应器事件处理

        /// <summary>
        /// 订阅微反应器控件事件
        /// </summary>
        private void SubscribeMicroreactorEvents(MicroreactorControl reactor)
        {
            try
            {
                // 选中事件
                reactor.ReactorSelected += OnMicroreactorSelectedFromControl;
                reactor.ReactorDeselected += OnMicroreactorDeselectedFromControl;

                // 旋转事件（如果实现了旋转功能）
                // reactor.ReactorRotateStarted += OnMicroreactorRotateStarted;
                // reactor.ReactorRotating += OnMicroreactorRotating;
                // reactor.ReactorRotateCompleted += OnMicroreactorRotateCompleted;

                // 状态变化事件
                reactor.ReactorStateChanged += OnMicroreactorStateChanged;

                // 温度变化事件
                reactor.TemperatureChanged += OnMicroreactorTemperatureChanged;

                // 流量变化事件
                reactor.FlowStateChanged += OnMicroreactorFlowStateChanged;

                Debug.WriteLine($"微反应器事件已订阅: {reactor.ReactorId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"订阅微反应器事件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 微反应器选中
        /// </summary>
        private void OnMicroreactorSelectedFromControl(object sender, string reactorId)
        {
            try
            {
                Debug.WriteLine($"微反应器被选中: {reactorId}");

                // 清除其他选择
                ClearDeviceSelection();
                ClearAllPipeSelectionsExcept(null);
                ClearAllValveSelections();
                ClearAllFlowMeterSelections();
                ClearAllMicroreactorSelections(); // ✅ 清除其他微反应器选择（保留当前）

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"已选中微反应器: {reactorId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理微反应器选中失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 微反应器取消选中
        /// </summary>
        private void OnMicroreactorDeselectedFromControl(object sender, string reactorId)
        {
            try
            {
                Debug.WriteLine($"微反应器取消选中: {reactorId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理微反应器取消选中失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 微反应器状态变化
        /// </summary>
        private void OnMicroreactorStateChanged(object sender, ReactorStateChangedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"微反应器 {e.ReactorId} 状态变化: {(e.IsReacting ? "启动" : "停止")}");

                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    e.IsReacting ? "微反应器已启动反应" : "微反应器已停止反应");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"微反应器状态变化处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 微反应器温度变化
        /// </summary>
        private void OnMicroreactorTemperatureChanged(object sender, TemperatureChangedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"微反应器 {e.ReactorId} 温度变化: {e.SensorName} {e.OldValue:F1}°C -> {e.NewValue:F1}°C");

                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"微反应器 {e.SensorName}: {e.NewValue:F1}°C");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"微反应器温度变化处理失败: {ex.Message}");
            }
        }


        /// <summary>
        /// 微反应器流量状态变化（新增方法）
        /// </summary>
        private void OnMicroreactorFlowStateChanged(object sender, MicroreactorFlowStateChangedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"========== 微反应器流量状态变化 ==========");
                Debug.WriteLine($"反应器ID: {e.ReactorId}");
                Debug.WriteLine($"流体路径: {e.FlowPath}");
                Debug.WriteLine($"状态: {(e.IsEnabled ? "✅ 启动" : "❌ 停止")}");
                Debug.WriteLine($"=========================================");

                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"微反应器流体路径{e.FlowPath}: {(e.IsEnabled ? "启动" : "停止")}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"微反应器流量状态变化处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// ✅ 新增：清除所有微反应器选择
        /// </summary>
        private void ClearAllMicroreactorSelections()
        {
            try
            {
                var reactors = FindAllMicroreactorsInCanvas();
                foreach (var reactor in reactors)
                {
                    reactor.IsSelected=false;
                    Debug.WriteLine($"清除微反应器选择: {reactor.ReactorId}");
                }

                Debug.WriteLine($"清除了 {reactors.Count} 个微反应器的选中状态");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清除微反应器选择失败: {ex.Message}");
            }
        }

        /// <summary>
        /// ✅ 新增:查找画布中所有微反应器控件
        /// </summary>
        private List<MicroreactorControl> FindAllMicroreactorsInCanvas()
        {
            var reactors = new List<MicroreactorControl>();

            try
            {
                foreach (var child in DesignCanvas.Children)
                {
                    if (child is Border deviceBorder)
                    {
                        var reactor = FindVisualChild<MicroreactorControl>(deviceBorder);
                        if (reactor != null)
                        {
                            reactors.Add(reactor);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"查找微反应器控件失败: {ex.Message}");
            }

            return reactors;
        }

        #endregion
        #region 语音助手事件处理

        /// <summary>
        /// 订阅语音助手事件
        /// </summary>
        private void SubscribeToVoiceAssistantEvents()
        {
            _eventAggregator?.GetEvent<VoiceCreateDeviceEvent>()?.Subscribe(
                OnVoiceCreateDevice,
                ThreadOption.UIThread);

            Debug.WriteLine("✅ 语音助手事件已订阅");
        }

        /// <summary>
        /// 处理语音创建设备事件
        /// </summary>
        private void OnVoiceCreateDevice(VoiceCreateDeviceEventArgs args)
        {
            try
            {
                Debug.WriteLine($"========== 语音创建设备 ==========");
                Debug.WriteLine($"设备名称: {args.DeviceName}");
                Debug.WriteLine($"语音命令: {args.Command}");
                Debug.WriteLine($"时间戳: {args.Timestamp}");

                // 1. 从设备管理器中查找设备
                var device = FindDeviceByName(args.DeviceName);
                if (device == null)
                {
                    Debug.WriteLine($"❌ 未找到设备: {args.DeviceName}");
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $"未找到 {args.DeviceName}");
                    return;
                }

                // 2. 计算放置位置（画布中心）
                var dropPosition = CalculateCenterPosition();

                // 3. 创建设备
                CreateDeviceFromVoiceCommand(device, dropPosition);

                Debug.WriteLine($"✅ 语音创建设备成功: {args.DeviceName}");
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"已创建 {args.DeviceName}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 语音创建设备失败: {ex.Message}");
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    "创建设备失败");
            }
        }

        /// <summary>
        /// 根据名称查找设备
        /// </summary>
        private IDevice FindDeviceByName(string deviceName)
        {
            try
            {
                // 从设备管理器获取所有设备
                var allDevices = _deviceManager?.GetAllDevices();
                if (allDevices == null || !allDevices.Any())
                {
                    Debug.WriteLine("⚠️ 设备管理器中没有设备");
                    return null;
                }

                // 模糊匹配设备名称
                var device = allDevices.FirstOrDefault(d =>
                    d.Name?.Contains(deviceName) == true ||
                    deviceName.Contains(d.Name));

                if (device != null)
                {
                    Debug.WriteLine($"✅ 找到设备: {device.Name} ({device.GetType().Name})");
                }

                return device;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"查找设备失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 计算画布中心位置
        /// </summary>
        private Point CalculateCenterPosition()
        {
            var canvasWidth = DesignCanvas.ActualWidth > 0 ? DesignCanvas.ActualWidth : 1200;
            var canvasHeight = DesignCanvas.ActualHeight > 0 ? DesignCanvas.ActualHeight : 800;

            // 在中心位置稍微偏移，避免重叠
            var random = new Random();
            var offsetX = random.Next(-50, 50);
            var offsetY = random.Next(-50, 50);

            return new Point(
                canvasWidth / 2 + offsetX,
                canvasHeight / 2 + offsetY
            );
        }

        /// <summary>
        /// 从语音命令创建设备
        /// </summary>
        private void CreateDeviceFromVoiceCommand(IDevice device, Point position)
        {
            try
            {
                Debug.WriteLine($"开始创建设备: {device.Name} 在位置 ({position.X:F1}, {position.Y:F1})");

                // 检查位置是否已有设备
                if (IsPositionOccupied(position))
                {
                    Debug.WriteLine("位置已被占用，调整位置");
                    position = FindNearbyEmptyPosition(position);
                }

                // 创建设备视图模型
                var canvasDevice = CreateCanvasDeviceViewModel(device, position);
                if (canvasDevice == null)
                {
                    Debug.WriteLine("❌ 创建设备视图模型失败");
                    return;
                }

                // 使用命令创建设备（支持撤销重做）
                var addCommand = new AddDeviceCommand(_viewModel, canvasDevice);
                _commandHistory.ExecuteCommand(addCommand);

                Debug.WriteLine($"✅ 设备创建完成: {device.Name}, ID: {canvasDevice.DeviceId}");

                // 标记画布已修改
                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

                // ✅ 选中新创建的设备
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    OnDeviceSelected(canvasDevice.DeviceId);
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"❌ 创建设备失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 查找附近的空位置
        /// </summary>
        private Point FindNearbyEmptyPosition(Point originalPosition)
        {
            const int searchRadius = 100;
            const int step = 20;
            var random = new Random();

            for (int radius = step; radius <= searchRadius; radius += step)
            {
                for (int angle = 0; angle < 360; angle += 45)
                {
                    var radians = angle * Math.PI / 180;
                    var testPosition = new Point(
                        originalPosition.X + radius * Math.Cos(radians),
                        originalPosition.Y + radius * Math.Sin(radians)
                    );

                    if (!IsPositionOccupied(testPosition) && IsValidDropPosition(testPosition))
                    {
                        Debug.WriteLine($"找到空位置: ({testPosition.X:F0}, {testPosition.Y:F0})");
                        return testPosition;
                    }
                }
            }

            // 如果没找到，返回随机偏移的位置
            return new Point(
                originalPosition.X + random.Next(-100, 100),
                originalPosition.Y + random.Next(-100, 100)
            );
        }

        #endregion
    }
}