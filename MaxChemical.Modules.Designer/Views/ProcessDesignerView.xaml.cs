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
using MaxChemical.Modules.Designer.Views.Controls.Service;
using Prism.Events;
using System;
using System.ComponentModel;
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
using System.Windows.Shapes;
using static MaxChemical.Modules.Designer.Views.Controls.SolenoidValveControl;

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
        // ★ 上云: 当前项目名(随 ProjectNameUpdatedEvent 更新),Ctrl+U 上传时带上,云端按名归档
        private string _currentProjectName = "";
        // ★ 上云: 地址/密钥(与云端一致)+ 实时值推送(节流)
        private const string CloudBaseUrl = "http://139.224.67.86:9080";
        private const string CloudUploadKey = "";   // 云端若设了 Process:UploadKey,填成同值
        private readonly System.Net.Http.HttpClient _cloudHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
        private System.Windows.Threading.DispatcherTimer _cloudTelemetryTimer;
        private volatile bool _cloudTelemetryDirty;
        // ★ 上云: 实时曲线批次——每次监控更新攒一帧样本(时间+值),1s 批量发,不丢数据。
        //   与 OnMonitoringCardUpdate/定时器同在 UI 线程,无需加锁。
        private readonly System.Collections.Generic.List<object> _seriesBatch = new();
        private readonly ICanvasDataService _canvasDataService;
        private readonly ICommandHistory _commandHistory;
        private ProcessDesignerViewModel _viewModel;
        private readonly IPipeConnectionService _pipeConnectionService;
        // A. 设备显示名管理
        private readonly Dictionary<string, int> _deviceNameCounters = new();      // 基础名 -> 累计序号
        private readonly Dictionary<string, string> _deviceDisplayNames = new();    // DeviceId -> 显示名（泵#2）

        // B. 执行动画管理
        //private readonly Dictionary<string, Storyboard> _runningAnimations = new();

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
                                                           // ★ 新增:统一选中视觉
        private SelectionOverlay _selectionOverlay;
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

        private string _editingDeviceId; 
        #region 私有字段 管道管理

        // 管道集合
        private readonly Dictionary<string, FrameworkElement> _pipes = new();

        // 选中的管道
        private string _selectedPipeId = null;
        private FrameworkElement _selectedPipeElement = null;

        // 管道拖拽原始位置（用于撤销）
        private Point _pipeOriginalPosition;

        #endregion
        // ═══ 1. 添加字段 ═══

        private Window _pipePropertyWindow;
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
            else if (pipe is TShapePipeControl tPipe)
            {
                tPipe.IsSelected = false;
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

                //  新增：管道删除完成，标记四叉树需要重建
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

        // ========== 新增：设备状态订阅管理 ==========
        private readonly Dictionary<string, IDeviceUIInteraction> _deviceUIInteractions = new();

        #region 私有字段 自适应相关

        private Window _parentWindow;                                        // 父窗口引用
        private System.Windows.Threading.DispatcherTimer _fitDebounceTimer; // 防抖定时器

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
            // ★ 上云: 订阅项目名,供上传与云端归档
            _eventAggregator.GetEvent<ProjectNameUpdatedEvent>().Subscribe(
                n => { if (!string.IsNullOrWhiteSpace(n)) _currentProjectName = n.Trim(); });
            // ★ 上云: 订阅标题栏「上云」按钮的请求(UI 线程,才能弹 loading 遮罩)
            _eventAggregator.GetEvent<CloudUploadRequestedEvent>().Subscribe(
                TriggerCloudUpload, Prism.Events.ThreadOption.UIThread);
            _regionOverlayService = regionOverlayService ?? throw new ArgumentNullException(nameof(regionOverlayService));
            _dragDropService = dragDropService ?? throw new ArgumentNullException(nameof(dragDropService));
            _canvasDataService = canvasDataService ?? throw new ArgumentNullException(nameof(canvasDataService));
            _commandHistory = commandHistory ?? throw new ArgumentNullException(nameof(commandHistory));
            _pipeConnectionService = pipeConnectionService ?? throw new ArgumentNullException(nameof(pipeConnectionService));
            _pipeSnapService = pipeSnapService ?? throw new ArgumentNullException(nameof(pipeSnapService));
            _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager)); //  新增

            // 初始化界面组件
            InitializeComponent();

            // ==========【临时】导出工艺画布为 SVG:Ctrl+Shift+E ==========
            // 用于把当前画布(设备+管路,渲染结果)导出成矢量 SVG,发给云端还原。
            // 验证/对接完成后可删除这段。
            this.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.E && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                {
                    try
                    {
                        DesignCanvas.UpdateLayout();
                        string svg = CanvasSvgExporter.ExportToSvg(
                            DesignCanvas, DesignCanvas.ActualWidth, DesignCanvas.ActualHeight,
                            CanvasSvgExporter.TryResolveDeviceId);
                        string outPath = System.IO.Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "process.svg");
                        System.IO.File.WriteAllText(outPath, svg);
                        MessageBox.Show("已导出:" + outPath);
                    }
                    catch (Exception ex) { MessageBox.Show("导出失败:" + ex.Message); }
                }
            };

            // ==========【上云】导出并直接上传到云端:Ctrl+U(与工具栏「上云」按钮同一逻辑)==========
            // 无需手动导出文件:导出当前画布 → POST 到云端 → 网页 process.html 立即展示。
            this.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.U && Keyboard.Modifiers == ModifierKeys.Control)
                    TriggerCloudUpload();
            };

            //  新增：初始化布局更新定时器
            InitializeLayoutUpdateTimer();

            // ★ 上云: 实时值推送定时器(1s 节流,监控值有更新才推全量快照)
            _cloudTelemetryTimer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromSeconds(1) };
            _cloudTelemetryTimer.Tick += (s, e) =>
            {
                if (_cloudTelemetryDirty) { _cloudTelemetryDirty = false; _ = PushTelemetryAsync(); }
                FlushSeriesBatch();   // ★ 曲线:把这一秒攒的样本批量发出去
            };
            _cloudTelemetryTimer.Start();

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

            // ==========  硬件加速配置 ==========
            ConfigureHardwareAcceleration();
        }

        #region 硬件加速
        /// <summary>
        ///  新增：配置硬件加速
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
                Debug.WriteLine($" 硬件加速配置失败: {ex.Message}");
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
                    Debug.WriteLine(" 强制启用硬件渲染");
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
                            Debug.WriteLine(" 延迟启用硬件渲染");
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"启用硬件渲染失败: {ex.Message}");
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

            Debug.WriteLine(" 画布级别优化完成");
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

                Debug.WriteLine(" 滚动容器优化完成");
            }
        }

        /// <summary>
        /// 4. 启用 GPU 合成
        /// </summary>
        private void EnableGPUComposition()
        {
            try
            {
                //  关键：启用 GPU 分层合成
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

                Debug.WriteLine(" GPU 合成已启用");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GPU 合成启用失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 5. 配置像素对齐
        /// </summary>
        private void ConfigurePixelAlignment()
        {
            this.SnapsToDevicePixels = true;
            this.UseLayoutRounding = true;

            Debug.WriteLine(" 像素对齐配置完成");
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

            //  新增：订阅监控卡片位置事件
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
                        Debug.WriteLine($"警告：参数 {parameterName} 在设备 {deviceId} 中未找到对应命令");
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

            this.Focus();

            SubscribeToCanvasEvents();

            _eventAggregator?.GetEvent<MonitoringCardUpdateEvent>()?.Subscribe(
                OnMonitoringCardUpdate,
                Prism.Events.ThreadOption.UIThread);

            SubscribeToMonitoringParameterEvents();
            SubscribeToPipeToolboxEvents();
            _pipeSnapService.Initialize(DesignCanvas);
            SubscribeToVoiceAssistantEvents();
            (_pipeSnapService as PipeSnapService)?.MarkQuadTreeDirty();

            // 新增：订阅布局相关事件（面板切换、窗口变化）
            SubscribeToLayoutFitEvents();
            // ★ 新增:创建统一选中视觉(整个画布只一个实例)
            _selectionOverlay = new SelectionOverlay(DesignCanvas);
        }
        /*
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
        }*/
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
                    _cloudTelemetryDirty = true;   // ★ 上云: 标记有更新,定时器下一拍推全量快照
                    AppendSeriesSample(card);      // ★ 曲线: 攒一帧样本(此刻该卡片各指标值)
                    Debug.WriteLine($"更新监控卡片数据: 设备 {e.DeviceId}, 命令 {e.CommandName}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新监控卡片数据失败: {ex.Message}");
            }
        }

        // ══════════════ 上云(标题栏「上云」按钮 / Ctrl+U 共用)══════════════

        private bool _cloudUploading;

        /// <summary>触发一次上云:显示 loading 遮罩 → 导出并上传 → 收起。标题栏按钮与 Ctrl+U 都走这里。</summary>
        private async void TriggerCloudUpload()
        {
            if (_cloudUploading) return;   // 防重入(loading 期间再次点击/按键)
            _cloudUploading = true;
            if (UploadingOverlay != null) UploadingOverlay.Visibility = Visibility.Visible;
            try
            {
                await UploadToCloudAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show("上云失败:" + ex.Message);
            }
            finally
            {
                if (UploadingOverlay != null) UploadingOverlay.Visibility = Visibility.Collapsed;
                _cloudUploading = false;
            }
        }

        /// <summary>导出当前画布 SVG 上传 + 顺带即时推一版实时值(点一次就刷新一次云端)。</summary>
        private async Task UploadToCloudAsync()
        {
            DesignCanvas.UpdateLayout();
            string svg = CanvasSvgExporter.ExportToSvg(
                DesignCanvas, DesignCanvas.ActualWidth, DesignCanvas.ActualHeight,
                CanvasSvgExporter.TryResolveDeviceId);
            await PostCloudAsync("/ingest/process/svg", new { name = _currentProjectName ?? "", svg });
            await PushTelemetryAsync();   // 即时把当前监控值也推上去
        }

        /// <summary>把所有监控卡片的当前值拼成 {设备名|指标:值} 全量快照,推给云端实时展示。</summary>
        private async Task PushTelemetryAsync()
        {
            try
            {
                var values = new Dictionary<string, object>();
                foreach (var kv in _monitoringCards)
                {
                    var card = kv.Value;
                    var name = card?.DeviceName;
                    if (card?.Parameters == null || string.IsNullOrEmpty(name)) continue;
                    foreach (var p in card.Parameters)
                    {
                        if (p == null || string.IsNullOrEmpty(p.Name)) continue;
                        var key = name + "|" + p.Name;
                        if (double.TryParse(p.Value, out var d)) values[key] = d;
                        else if (!string.IsNullOrEmpty(p.Value)) values[key] = p.Value;
                    }
                }
                if (values.Count == 0) return;
                await PostCloudAsync("/ingest/process/telemetry", new { name = _currentProjectName ?? "", values });
            }
            catch (Exception ex) { Debug.WriteLine("推送实时值失败: " + ex.Message); }
        }

        /// <summary>攒一帧曲线样本:此刻某卡片各指标的数值(带时间戳)。不丢中间值。</summary>
        private void AppendSeriesSample(MonitoringCard card)
        {
            try
            {
                var name = card?.DeviceName;
                if (card?.Parameters == null || string.IsNullOrEmpty(name)) return;
                var values = new Dictionary<string, double>();
                foreach (var p in card.Parameters)
                {
                    if (p == null || string.IsNullOrEmpty(p.Name)) continue;
                    if (double.TryParse(p.Value, out var d)) values[name + "|" + p.Name] = d;   // 曲线只收数值
                }
                if (values.Count == 0) return;
                _seriesBatch.Add(new { t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), values });
                if (_seriesBatch.Count > 3000) _seriesBatch.RemoveRange(0, _seriesBatch.Count - 3000); // 背压保护
            }
            catch (Exception ex) { Debug.WriteLine("攒曲线样本失败: " + ex.Message); }
        }

        /// <summary>把这一秒攒的曲线样本批量发到云端(每个样本都发,不丢数据)。</summary>
        private void FlushSeriesBatch()
        {
            if (_seriesBatch.Count == 0) return;
            var frames = _seriesBatch.ToArray();
            _seriesBatch.Clear();
            _ = SafePostAsync("/ingest/process/series", new { name = _currentProjectName ?? "", frames });
        }

        /// <summary>后台 POST:静默吞异常(云端未启动/未登录时不打扰桌面)。</summary>
        private async Task SafePostAsync(string path, object payload)
        {
            try { await PostCloudAsync(path, payload); } catch (Exception ex) { Debug.WriteLine($"上云后台 POST {path} 失败: {ex.Message}"); }
        }

        /// <summary>统一的云端 POST(带可选 X-Upload-Key)。</summary>
        private async Task PostCloudAsync(string path, object payload)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            using var req = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Post, CloudBaseUrl + path);
            req.Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(CloudUploadKey)) req.Headers.Add("X-Upload-Key", CloudUploadKey);
            using var resp = await _cloudHttp.SendAsync(req);
            resp.EnsureSuccessStatusCode();
        }
        /*
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
        */

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
            UnsubscribeFromViewModelEvents();
            UnsubscribeFromCanvasEvents();
            UnsubscribeFromKeyboardEvents();
            UnsubscribeFromPipeToolboxEvents();

            _eventAggregator?.GetEvent<VoiceCreateDeviceEvent>()?.Unsubscribe(OnVoiceCreateDevice);
            _eventAggregator?.GetEvent<VoiceSetDeviceParameterEvent>()?.Unsubscribe(OnVoiceSetDeviceParameter);
            _eventAggregator?.GetEvent<VoiceControlDeviceStateEvent>()?.Unsubscribe(OnVoiceControlDeviceState);
            _eventAggregator?.GetEvent<MonitoringCardUpdateEvent>()?.Unsubscribe(OnMonitoringCardUpdate);

            _layoutUpdateTimer?.Stop();

            //新增：清理布局自适应订阅
            UnsubscribeFromLayoutFitEvents();
        }
        /// <summary>
        /// 订阅管道工具箱事件
        /// </summary>
        private void SubscribeToPipeToolboxEvents()
        {
            if (PipeToolbox != null)
            {
                PipeToolbox.PipeDragStarted += OnToolboxPipeDragStarted; //  改名
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
                PipeToolbox.PipeDragStarted -= OnToolboxPipeDragStarted; //  改名
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
            //  新增：订阅获取选中管道请求事件
            _eventAggregator?.GetEvent<GetSelectedPipeRequestEvent>()?.Subscribe(OnGetSelectedPipeRequest);

            //  新增：订阅删除选中管道事件
            _eventAggregator?.GetEvent<DeleteSelectedPipeEvent>()?.Subscribe(OnDeleteSelectedPipe);
            //  新增：订阅管道数据收集和恢复事件
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

            //  新增：取消订阅
            _eventAggregator?.GetEvent<GetSelectedPipeRequestEvent>()?.Unsubscribe(OnGetSelectedPipeRequest);
            _eventAggregator?.GetEvent<DeleteSelectedPipeEvent>()?.Unsubscribe(OnDeleteSelectedPipe);

            //  新增：取消订阅
            _eventAggregator?.GetEvent<CollectPipeDataRequestEvent>()?.Unsubscribe(OnCollectPipeDataRequest);


            //  新增：取消订阅监控卡片位置事件
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

                //  调用已有的删除方法
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
                //  新增：有选中管道时删除管道
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

            // 优先使用 XAML 中命名的 CanvasScrollViewer，找不到再向上查找
            _canvasScrollViewer = CanvasScrollViewer
                                  ?? FindParent<ScrollViewer>(DesignCanvas)
                                  ?? FindParent<ScrollViewer>(this);

            Debug.WriteLine($"缩放组件初始化: ScrollViewer={(_canvasScrollViewer != null ? $"已找到 {_canvasScrollViewer.ActualWidth:F0}x{_canvasScrollViewer.ActualHeight:F0}" : "未找到")}");
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

                //  新增：调度延迟布局更新
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
        /// 画布自适应视图（事件入口）
        /// FitCanvasToViewEvent 触发此方法，使用 Dispatcher 延迟确保布局已更新
        /// </summary>
        private void OnFitCanvasToView()
        {
            Debug.WriteLine("[FitToView] 收到自适应请求");
            // Background 优先级：等所有布局刷新完毕后执行
            Dispatcher.BeginInvoke(
                new Action(FitCanvasToViewInternal),
                System.Windows.Threading.DispatcherPriority.Background);
        }
        /// <summary>
        /// 画布自适应核心逻辑（两段式）
        /// ① 画布 < 视口 → 扩展画布尺寸至视口，scale = 1.0（与新建项目完全一致）
        /// ② 画布 > 视口 → 缩放使全部内容可见
        /// </summary>
        private void FitCanvasToViewInternal()
        {
            try
            {
                Debug.WriteLine("========== FitCanvasToView 核心逻辑开始 ==========");

                // ① 确保 ScrollViewer 引用有效
                if (_canvasScrollViewer == null)
                {
                    _canvasScrollViewer = CanvasScrollViewer
                                          ?? FindParent<ScrollViewer>(DesignCanvas)
                                          ?? FindParent<ScrollViewer>(this);
                }
                DesignCanvas.LayoutTransform = null;
                // ② 强制刷新布局，获取最新视口尺寸
                _canvasScrollViewer?.UpdateLayout();
                UpdateLayout();

                double viewportW = (_canvasScrollViewer?.ViewportWidth > 0)
                    ? _canvasScrollViewer.ViewportWidth : ActualWidth;
                double viewportH = (_canvasScrollViewer?.ViewportHeight > 0)
                    ? _canvasScrollViewer.ViewportHeight : ActualHeight;

                if (viewportW <= 0 || viewportH <= 0)
                {
                    Debug.WriteLine("视口尺寸无效，跳过自适应");
                    return;
                }

                // ③ 画布当前逻辑尺寸（Width 属性比 ActualWidth 更准确）
                double canvasW = DesignCanvas.Width > 0 ? DesignCanvas.Width :
                                 DesignCanvas.ActualWidth > 0 ? DesignCanvas.ActualWidth : 1200;
                double canvasH = DesignCanvas.Height > 0 ? DesignCanvas.Height :
                                 DesignCanvas.ActualHeight > 0 ? DesignCanvas.ActualHeight : 800;

                Debug.WriteLine($"视口: {viewportW:F0} x {viewportH:F0}");
                Debug.WriteLine($"画布: {canvasW:F0} x {canvasH:F0}");

                // ④ 确保缩放变换已初始化
                if (_canvasScaleTransform == null)
                    InitializeZoomComponents();

                // ============================================================
                // 关键判断：画布是否大于视口
                // ============================================================
                bool canvasExceedsViewport = canvasW > viewportW + 1 || canvasH > viewportH + 1;

                if (canvasExceedsViewport)
                {
                    //// ---------- 情况 A：画布 > 视口 → 缩放以显示全部内容 ----------
                    //Debug.WriteLine("情况A：画布大于视口，缩放适配");

                    //// 不加 padding，避免不必要的缩小
                    //var scaleX = viewportW / canvasW;
                    //var scaleY = viewportH / canvasH;
                    //var scale = Math.Max(0.05, Math.Min(Math.Min(scaleX, scaleY), 3.0));

                    //if (_canvasScaleTransform != null)
                    //{
                    //    _canvasScaleTransform.ScaleX = scale;
                    //    _canvasScaleTransform.ScaleY = scale;
                    //}

                    //// 居中滚动
                    //if (_canvasScrollViewer != null)
                    //{
                    //    var scrollX = Math.Max(0, (canvasW * scale - viewportW) / 2.0);
                    //    var scrollY = Math.Max(0, (canvasH * scale - viewportH) / 2.0);
                    //    _canvasScrollViewer.ScrollToHorizontalOffset(scrollX);
                    //    _canvasScrollViewer.ScrollToVerticalOffset(scrollY);
                    //    Debug.WriteLine($"缩放: {scale:F3}，滚动偏移: ({scrollX:F1}, {scrollY:F1})");
                    //}
                }
                else
                {
                    double newW = Math.Max(canvasW, viewportW);
                    double newH = Math.Max(canvasH, viewportH);

                    // 先算比例，再扩展
                    double ratioX = (canvasW > 0) ? newW / canvasW : 1.0;
                    double ratioY = (canvasH > 0) ? newH / canvasH : 1.0;

                    Debug.WriteLine($"情况B - 扩展比例: ratioX={ratioX:F4}, ratioY={ratioY:F4}");

                    // ① 缩放区域数据
                    _viewModel?.CurrentRegionLayout?.ScaleLayout(ratioX, ratioY);

                    // ② 扩展画布
                    DesignCanvas.Width = newW;
                    DesignCanvas.Height = newH;

                    // ③ 刷新覆盖层（此时 Region 坐标已更新）
                    RefreshRegionOverlay(newW, newH);

                    Debug.WriteLine($"画布扩展至: {newW:F0} x {newH:F0}，scale = 1.0");

                }

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?
                    .Publish("画布已自适应");

                Debug.WriteLine("========== FitCanvasToView 完成 ==========");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FitCanvasToViewInternal 异常: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// 画布尺寸变化后刷新区域覆盖层
        /// </summary>
        private void RefreshRegionOverlay(double newWidth, double newHeight)
        {
            try
            {
                if (_viewModel?.CurrentRegionLayout == null) return;

                _regionOverlayService?.UpdateRegionOverlay(
                    DesignCanvas,
                    _viewModel.CurrentRegionLayout,
                    1.0);

                Debug.WriteLine($"区域覆盖层已刷新: {newWidth:F0} x {newHeight:F0}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"刷新区域覆盖层失败: {ex.Message}");
            }
        }
        #region 布局自适应事件管理

        /// <summary>
        /// 订阅所有会影响画布可用空间的事件，触发自适应
        /// </summary>
        private void SubscribeToLayoutFitEvents()
        {
            try
            {
                // 左侧面板显隐（菜单按钮点击）
                _eventAggregator?.GetEvent<PanelVisibilityEvent>()?
                    .Subscribe(OnPanelVisibilityChangedForFit, Prism.Events.ThreadOption.UIThread);

                _eventAggregator?.GetEvent<LeftPanelVisibilityChangedEvent>()?
                    .Subscribe(OnLeftPanelVisibilityChangedForFit, Prism.Events.ThreadOption.UIThread);

                // 订阅父窗口尺寸和位置变化（外接大屏等场景）
                _parentWindow = Window.GetWindow(this);
                if (_parentWindow != null)
                {
                    _parentWindow.SizeChanged += OnWindowSizeChangedForFit;
                    _parentWindow.LocationChanged += OnWindowLocationChangedForFit;
                    Debug.WriteLine("已订阅父窗口 SizeChanged / LocationChanged");
                }

                // 初始化防抖定时器（300ms）
                _fitDebounceTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(300)
                };
                _fitDebounceTimer.Tick += (s, e) =>
                {
                    _fitDebounceTimer.Stop();
                    FitCanvasToViewInternal();
                };

                Debug.WriteLine("布局自适应事件订阅完成");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"订阅布局自适应事件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 取消所有布局自适应事件订阅
        /// </summary>
        private void UnsubscribeFromLayoutFitEvents()
        {
            try
            {
                _eventAggregator?.GetEvent<PanelVisibilityEvent>()?
                    .Unsubscribe(OnPanelVisibilityChangedForFit);

                _eventAggregator?.GetEvent<LeftPanelVisibilityChangedEvent>()?
                    .Unsubscribe(OnLeftPanelVisibilityChangedForFit);

                if (_parentWindow != null)
                {
                    _parentWindow.SizeChanged -= OnWindowSizeChangedForFit;
                    _parentWindow.LocationChanged -= OnWindowLocationChangedForFit;
                    _parentWindow = null;
                }

                _fitDebounceTimer?.Stop();
                _fitDebounceTimer = null;

                Debug.WriteLine("布局自适应事件已取消订阅");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"取消布局自适应事件失败: {ex.Message}");
            }
        }

        // ---- 事件处理器 ----

        private void OnPanelVisibilityChangedForFit(bool isVisible)
        {
            Debug.WriteLine($"[FitToView] 面板可见性变化: {isVisible}，触发防抖自适应");
            ScheduleDelayedFitToView();
        }

        private void OnLeftPanelVisibilityChangedForFit(bool isVisible)
        {
            Debug.WriteLine($"[FitToView] 左侧面板可见性变化: {isVisible}，触发防抖自适应");
            ScheduleDelayedFitToView();
        }

        private void OnWindowSizeChangedForFit(object sender, SizeChangedEventArgs e)
        {
            Debug.WriteLine($"[FitToView] 窗口尺寸变化: {e.NewSize.Width:F0}x{e.NewSize.Height:F0}");
            ScheduleDelayedFitToView();
        }

        private void OnWindowLocationChangedForFit(object sender, EventArgs e)
        {
            // 移动到不同分辨率/DPI 屏幕时触发
            Debug.WriteLine("[FitToView] 窗口位置变化（可能切换到外接屏幕）");
            ScheduleDelayedFitToView(delayMs: 500); // 切屏延迟稍长
        }

        /// <summary>
        /// 防抖触发自适应：重置定时器，避免连续触发时重复计算
        /// </summary>
        private void ScheduleDelayedFitToView(int delayMs = 300)
        {
            if (_fitDebounceTimer == null) return;

            _fitDebounceTimer.Stop();
            _fitDebounceTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
            _fitDebounceTimer.Start();
        }

        #endregion
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

            // ★ 新方案:统一选中标记,不再改 BorderBrush/Effect
            if (_selectedDeviceElement != null)
            {
                _selectionOverlay?.Attach(_selectedDeviceElement);
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
            // ★ 隐藏统一选中标记
            _selectionOverlay?.Detach();

            // 清掉之前可能残留的旧视觉(给老代码兜底)
            if (_selectedDeviceElement is Border border)
            {
                border.BorderBrush = Brushes.Transparent;
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
            _isInternalDragging = false;
            _isMouseDown = false;
            _hasStartedDrag = false;
            _mouseDownElement = null;
            _currentDraggingDeviceId = null;
            _originalDevicePosition = new Point();
            _dragStartPoint = new Point();

            // ★ 恢复选中标记跟随
            _selectionOverlay?.ResumeUpdates();
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
                //  新增：设备删除完成，标记四叉树需要重建
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

        //  拖放区域检测缓存
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
                _dragRegionCheckResult = null; //  清除缓存
                return;
            }

            //  处理管道拖拽
            if (e.Data.GetDataPresent("PipeData"))
            {
                var pipeData = e.Data.GetData("PipeData") as PipeDragData;
                Debug.WriteLine($"管道拖拽进入: {pipeData?.PipeType}");
                e.Effects = DragDropEffects.Copy;
                DesignCanvas.Background = new SolidColorBrush(Color.FromArgb(20, 0, 255, 120));
                _dragRegionCheckResult = null; //  管道不需要区域检测
                e.Handled = true;
                return;
            }

            // 处理设备拖拽
            if (e.Data.GetDataPresent("Device"))
            {
                var device = e.Data.GetData("Device") as IDevice;
                var position = e.GetPosition(DesignCanvas);
                Debug.WriteLine($"外部设备拖拽进入: {device?.Name}");

                //  只在 DragEnter 时检测一次区域并缓存结果
                _dragRegionCheckResult = IsValidRegionForDevice(device, position);
                Debug.WriteLine($"[DragEnter] 区域检测结果: {(_dragRegionCheckResult == true ? "有效区域" : "无效区域")}"); //  保留日志

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
                _dragRegionCheckResult = null; //  清除缓存
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

            //  处理管道拖拽
            if (e.Data.GetDataPresent("PipeData"))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }

            //  使用缓存的检测结果，不再重复检测
            if (e.Data.GetDataPresent("Device"))
            {
                var position = e.GetPosition(DesignCanvas); //  保留位置日志需要

                //  使用缓存结果
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

                //  保留原有位置日志（如果需要）
                Debug.WriteLine($"[DragOver] 位置: ({position.X:F0}, {position.Y:F0}), 效果: {e.Effects}");
            }

            e.Handled = true;
        }

        private void OnDragLeave(object sender, DragEventArgs e)
        {
            Debug.WriteLine($"外部拖拽离开: 内部拖拽={_isInternalDragging}");

            //  清除缓存
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

            //  清除缓存
            var cachedResult = _dragRegionCheckResult;
            _dragRegionCheckResult = null;

            // 检查是否应该忽略此拖放事件
            if (ShouldIgnoreDropEvent())
            {
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            //  处理管道拖放
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

                //  使用缓存的检测结果
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
                var canvasDeviceId = Guid.NewGuid().ToString();
                var canvasDevice = new CanvasDeviceViewModel
                {
                    DeviceId = canvasDeviceId,
                    Device = independentDevice, //  使用深拷贝的独立设备实例
                    Position = position,
                    // 改为
                    Width = 80,
                    Height = 80   // φ:1 中型,适合大多数普通设备
                };
                //  关键修复：将画布ID传递给设备实例
                if (independentDevice is IDeviceUIInteraction uiInteraction)
                {
                    // 如果设备支持UI交互，设置其DeviceId属性
                    var deviceIdProperty = independentDevice.GetType().GetProperty("DeviceId");
                    if (deviceIdProperty != null && deviceIdProperty.CanWrite)
                    {
                        deviceIdProperty.SetValue(independentDevice, canvasDeviceId);
                    }
                }
                // 画布设备实例允许驱动级自动采集(插件目录/探测用实例不允许)
                if (independentDevice is DevicePlugins.Devices.Device canvasBaseDevice)
                {
                    canvasBaseDevice.AutoCollectAllowed = true;
                }
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

                    //  新增：Z 形管道创建
                    case PipeType.ZShape:
                        CreateZShapePipe(position);
                        break;
                    //新增：T 型管道创建
                    case PipeType.Tee:
                        CreateTShapePipe(position);
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

                    //  只处理设备和空白区域
                    var deviceControl = FindDeviceControl(hitElement);
                    if (deviceControl?.Tag is string deviceId)
                    {
                        HandleDeviceMouseDown(deviceControl, deviceId, e);
                    }
                    else
                    {
                        // 点击空白区域时，清除所有选择
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
            ClearPipeSelection(); //  新增：清除管道选择
            ClearAllValveSelections(); //  新增：清除所有阀门选择
            ClearAllFlowMeterSelections(); //  新增
            ClearAllMicroreactorSelections(); //  新增
            ClearAllHeatExchangerSelections(); //  新增：清除换热器选择
            ClearAllDigitalScaleSelections(); // 新增
            ClearAllTemperatureCirculatorSelections(); // 新增：清除高低温循环器选择
            ClearAllTubularReactorSelections();
            CleanupDragState();

            this.Focus();
        }

        private void OnCanvasMouseMove(object sender, MouseEventArgs e)
        {
            var currentPosition = e.GetPosition(DesignCanvas);

            //  优先处理拖拽状态（无需节流）
            if (_isMouseDown && _mouseDownElement != null && e.LeftButton == MouseButtonState.Pressed)
            {
                ProcessDeviceDragging(e);
                return; // 重要：拖拽处理后直接返回，不受节流影响
            }

            //  只对非拖拽的鼠标移动进行节流
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
            //  直接计算距离，不依赖 _lastMousePosition
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

            // ★ 挂起选中标记自动跟随,避免拖拽卡顿
            _selectionOverlay?.SuspendUpdates();
        }

    
        private bool _continueDraggingScheduled;
        /// <summary>
        /// 继续拖拽
        /// </summary>
        private void ContinueDragging(Point currentPoint)
        {
            // ★ 节流:同一帧内多次触发只处理一次
            if (_continueDraggingScheduled) return;
            _continueDraggingScheduled = true;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                _continueDraggingScheduled = false;

                if (_mouseDownElement == null) return;

                double dx = currentPoint.X - _dragStartPoint.X;
                double dy = currentPoint.Y - _dragStartPoint.Y;

                double origLeft = Canvas.GetLeft(_mouseDownElement);
                double origTop = Canvas.GetTop(_mouseDownElement);

                double newLeft = origLeft + dx;
                double newTop = origTop + dy;

                var (boundedLeft, boundedTop) = ApplyBoundaryConstraints(newLeft, newTop);

                if (!IsValidDragPosition(boundedLeft, boundedTop))
                {
                    UpdateDragVisualFeedback(_mouseDownElement, false);
                    return;
                }

                Canvas.SetLeft(_mouseDownElement, boundedLeft);
                Canvas.SetTop(_mouseDownElement, boundedTop);
                _mouseDownElement.Opacity = 0.8;

                UpdateDragVisualFeedback(_mouseDownElement, true);

                // ★ Overlay 跟随
                _selectionOverlay?.Refresh();

                _dragStartPoint = currentPoint;
            }), System.Windows.Threading.DispatcherPriority.Render);
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
                    "设备不能放置在当前区域");
            }

            _dragDropService.EndDrag(endPosition, isSuccessful, "Canvas");
            DesignCanvas.AllowDrop = true;

            //  拖拽结束后恢复高质量
            RenderOptions.SetBitmapScalingMode(DesignCanvas, BitmapScalingMode.HighQuality);
            DesignCanvas.CacheMode = new BitmapCache
            {
                EnableClearType = false,
                RenderAtScale = 1.0,
                SnapsToDevicePixels = true
            };

            Debug.WriteLine(" 已恢复高质量渲染");
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
        private void AddDragVisualFeedback(FrameworkElement element, bool isValid = true)
        {
            if (element is Border border)
            {
                var color = isValid ? Colors.Green : Colors.Red;
                border.BorderBrush = new SolidColorBrush(color);
                border.BorderThickness = new Thickness(0);

                // ★ 拖拽时临时置顶,让用户清楚"我抓的就是这个"
                Panel.SetZIndex(border, 9999);
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
                border.Opacity = 1.0;
                border.BorderBrush = Brushes.Transparent;
                border.BorderThickness = new Thickness(0);

                // ★ 改:按设备类型恢复正确层级,而不是无脑设 0
                if (border.Tag is string deviceId)
                {
                    var canvasDevice = _viewModel?.CanvasDevices?.FirstOrDefault(d => d.DeviceId == deviceId);
                    if (canvasDevice?.Device != null)
                    {
                        int zIndex = GetDeviceZIndex(canvasDevice.Device);
                        Panel.SetZIndex(border, zIndex);
                    }
                    else
                    {
                        Panel.SetZIndex(border, 100);  // 兜底
                    }
                }
                else
                {
                    Panel.SetZIndex(border, 100);
                }

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

            //  新增：如果设备包含阀门控件，订阅阀门事件
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
            //  订阅设备状态变化
            if (canvasDevice.Device is IDeviceUIInteraction uiInteraction)
            {
                _deviceUIInteractions[canvasDevice.DeviceId] = uiInteraction;
                uiInteraction.ControlStateChanged += OnDeviceStateChanged;

                Debug.WriteLine($" 已订阅设备UI交互: {canvasDevice.DeviceId}");
            }

            //  订阅控件交互事件
            SubscribeDeviceControlEvents(deviceControl, canvasDevice);
        }
        /// <summary>
        /// 订阅设备控件的交互事件（统一入口）
        /// </summary>
        private void SubscribeDeviceControlEvents(Border deviceControl, CanvasDeviceViewModel canvasDevice)
        {
            try
            {
                // 新增：查找电磁阀控件
                var solenoidValve = FindVisualChild<SolenoidValveControl>(deviceControl);
                if (solenoidValve != null)
                {
                    //  订阅状态切换请求事件
                    solenoidValve.StateChangeRequested += async (s, e) =>
                    {
                        await HandleValveStateChangeRequestAsync(canvasDevice.Device, e);
                    };
                    Debug.WriteLine($" 已订阅电磁阀状态切换事件: {canvasDevice.DeviceId}");
                }

                //  未来可扩展其他设备类型...
            }
            catch (Exception ex)
            {
                Debug.WriteLine($" 订阅设备控件事件失败: {ex.Message}");
            }
        }


        /// <summary>
        /// 处理电磁阀门状态切换请求
        /// </summary>
        private async Task HandleValveStateChangeRequestAsync(IDevice device, object eventArgs)
        {
            SolenoidValveControl solenoidValve = null;

            try
            {
                // 1️⃣ 解析事件参数
                if (eventArgs is not SolenoidValveControl.ValveStateChangeRequestEventArgs solenoidArgs)
                {
                    Debug.WriteLine("未知的事件参数类型");
                    return;
                }

                string valveId = solenoidArgs.ValveId;
                bool currentState = solenoidArgs.CurrentState;
                bool requestedState = solenoidArgs.RequestedState;

                // 查找电磁阀控件
                solenoidValve = FindDeviceControl<SolenoidValveControl>(valveId);
                if (solenoidValve == null)
                {
                    Debug.WriteLine($"未找到电磁阀控件: {valveId}");
                    return;
                }

                Debug.WriteLine($"========== 处理电磁阀状态切换请求 ==========");
                Debug.WriteLine($"ValveId: {valveId}");
                Debug.WriteLine($"当前状态: {currentState}");
                Debug.WriteLine($"请求状态: {requestedState}");

                // 2️⃣ 检查设备是否支持UI交互
                if (device is not IDeviceUIInteraction uiInteraction)
                {
                    Debug.WriteLine($"设备 {device.Name} 不支持UI交互");
                    solenoidValve?.HideLoading();
                    return;
                }

                // 3️⃣ 确定命令名称
                string commandName = requestedState ? "打开电磁阀" : "关闭电磁阀";
                Debug.WriteLine($"执行命令: {commandName}");

                // 4️⃣ 调用设备命令（异步执行）
                var result = await uiInteraction.ExecuteUICommandAsync(commandName);

                // 5️⃣ 隐藏加载动画
                solenoidValve?.HideLoading();

                // 6️⃣ 处理执行结果
                if (result.Success)
                {
                    Debug.WriteLine($" 命令执行成功: {result.Message}");
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $" {commandName}成功");
                }
                else
                {
                    Debug.WriteLine($" 命令执行失败: {result.Message}");
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $" {commandName}失败: {result.Message}");

                    //  显示失败动画
                    solenoidValve?.ShowOperationFailure();
                }

                Debug.WriteLine($"==========================================");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($" 处理阀门状态切换请求失败: {ex.Message}");
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $" 操作失败: {ex.Message}");

                //  隐藏加载 + 显示错误
                solenoidValve?.HideLoading();
                solenoidValve?.ShowOperationFailure();
            }
        }
        /// <summary>
        ///  辅助方法：查找设备控件
        /// </summary>
        private T FindDeviceControl<T>(string valveId)  where T : UserControl
        {
            foreach (var item in DesignCanvas.Children)
            {
                if (item is Border border)
                {
                    var control = FindVisualChild<T>(border);
                    if (control != null)
                    {
                        // 通过反射获取 ValveId 属性
                        var idProperty = typeof(T).GetProperty("ValveId");
                        if (idProperty != null)
                        {
                            var controlValveId = idProperty.GetValue(control) as string;
                            if (controlValveId == valveId)
                            {
                                return control;
                            }
                        }
                    }
                }
            }
            return null;
        }
        /// <summary>
        /// 设备状态变化处理（设备 → UI）
        /// </summary>
        private void OnDeviceStateChanged(object sender, DeviceControlStateChangedEventArgs e)
        {  // ★ 加这一行
            Debug.WriteLine($"[诊断] OnDeviceStateChanged 收到: e.DeviceId='{e.DeviceId}' | e.ComId='{e.ComId}' | sender.HashCode={sender?.GetHashCode()} | {e.StateName}");
            try
            {
                Debug.WriteLine($" 设备状态变化: {e.DeviceId} - {e.StateName} = {e.StateValue}");

                Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    var deviceElement = FindDeviceElementInCanvas(e.DeviceId);
                    if (deviceElement == null)
                    {
                        Debug.WriteLine($"未找到设备元素: {e.DeviceId}");
                        return;
                    }

                    //  统一处理电磁阀状态
                    if (e.StateName == "ValveState")
                    {
                        HandleSolenoidValveStateChange(deviceElement, e);
                    }
                  
                    // 查找柱塞泵控件
                    var pumpControl = FindVisualChild<PistonPumpControl>(deviceElement);
                    if (pumpControl!=null)
                    {
                        //  处理不同类型的状态变化
                        switch (e.StateName)
                        {
                            case "PumpSpeed":
                                // 更新泵速
                                if (e.StateValue is int speed)
                                {
                                    pumpControl.PumpSpeed = speed;
                                    Debug.WriteLine($" 泵速已更新: {speed}");
                                }
                                break;

                            case "IsRunning":
                                // 更新运行状态
                                if (e.StateValue is bool isRunning)
                                {
                                    pumpControl.IsRunning = isRunning;
                                    Debug.WriteLine($" 运行状态已更新: {isRunning}");
                                }
                                break;

                            case "CommandState_启动泵":
                            case "CommandState_设置泵速":
                            case "CommandState_停止泵":
                                // 处理命令执行状态
                                HandleCommandState(pumpControl, e);
                                break;
                        }
                    }
                    //  新增：处理流量计状态
                    var meterControl = FindVisualChild<MassFlowMeterControl>(deviceElement);
                    if (meterControl != null)
                    {
                        HandleFlowMeterControlState(meterControl, e);
                    }
                    //  新增：处理电子秤状态
                    var scaleControl = FindVisualChild<DigitalScaleControl>(deviceElement);
                    if (scaleControl != null)
                    {
                        HandleDigitalScaleControlState(scaleControl, e);
                    }
                    // 新增：处理加热磁力搅拌器状态
                    var stirrerControl = FindVisualChild<MagneticStirrerControl>(deviceElement);
                    if (stirrerControl != null)
                    {
                        HandleMagneticStirrerControlState(stirrerControl, e);
                    }
                    // 新增：处理精睿柱塞泵状态
                    var jingRuiPumpControl = FindVisualChild<JingRuiPumpControl>(deviceElement);
                    if (jingRuiPumpControl != null)
                    {
                        HandleJingRuiPumpControlState(jingRuiPumpControl, e);
                    }
                    // 新增：处理高低温循环器状态
                    var circulatorControl = FindVisualChild<TemperatureControlSystemControl>(deviceElement);
                    if (circulatorControl != null)
                    {
                        HandleTemperatureCirculatorControlState(circulatorControl, e);
                    }
                    // 新增：微反应器温度更新
                    var reactorControl = FindVisualChild<MicroreactorControl>(deviceElement);
                    if (reactorControl != null)
                    {
                        HandleMicroreactorTemperatureState(reactorControl, e);
                    }
                    // 新增：换热器温度，压力更新
                    var heatControl = FindVisualChild<HeatExchangerControl>(deviceElement);
                    if (heatControl != null)
                    {
                        switch (e.StateName)
                        {
                            case "TemperatureValue":
                                if (e.StateValue is double t)
                                {
                                    heatControl.Temperature = Math.Round(t, 1);
                                    Debug.WriteLine($" 温度已更新: {t}℃");
                                }
                                break;
                            case "PressureValue":
                                if (e.StateValue is double p)
                                {
                                    heatControl.Pressure = Math.Round(p, 1);
                                    Debug.WriteLine($" 压力已更新: {p}Mpa");
                                }
                                break;
                            default:
                                break;
                        }
                    }
                    //// 新增：处理管式反应器状态
                    //var tubularReactorControl = FindVisualChild<TubularReactorControl>(deviceElement);
                    //if (tubularReactorControl != null)
                    //{
                    //    HandleTubularReactorControlState(tubularReactorControl, e);
                    //}

                }));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($" 处理设备状态变化失败: {ex.Message}");
            }
        }
        /// <summary>
        ///  处理电磁阀状态变化（统一入口）
        /// </summary>
        private void HandleSolenoidValveStateChange(
      FrameworkElement deviceElement,
      DeviceControlStateChangedEventArgs e)
        {
            if (e.StateValue is not SolenoidValveState valveState)
            {
                Debug.WriteLine($"状态值类型错误: {e.StateValue?.GetType().Name}");
                return;
            }

            // 查找电磁阀控件
            var solenoidValve = FindVisualChild<SolenoidValveControl>(deviceElement);
            if (solenoidValve == null)
            {
                Debug.WriteLine($"未找到电磁阀控件: {e.DeviceId}");
                return;
            }

            Debug.WriteLine($" 电磁阀状态: {valveState}");

            //  根据状态更新UI
            switch (valveState)
            {
                case SolenoidValveState.Opening:
                    solenoidValve.ShowLoading();
                    UpdateStatusBar(" 正在打开电磁阀...");
                    break;

                case SolenoidValveState.Open:
                    solenoidValve.HideLoading();
                    solenoidValve.IsOpen = true;
                    UpdateStatusBar(" 电磁阀已打开");
                    break;

                case SolenoidValveState.Closing:
                    solenoidValve.ShowLoading();
                    UpdateStatusBar(" 正在关闭电磁阀...");
                    break;

                case SolenoidValveState.Closed:
                    solenoidValve.HideLoading();
                    solenoidValve.IsOpen = false;
                    UpdateStatusBar(" 电磁阀已关闭");
                    break;

                case SolenoidValveState.Failed:
                    solenoidValve.HideLoading();
                    solenoidValve.ShowOperationFailure();
                    UpdateStatusBar(" 电磁阀操作失败");
                    break;

                case SolenoidValveState.Error:
                    solenoidValve.HideLoading();
                    solenoidValve.ShowOperationFailure();
                    var errorMsg = e.AdditionalData?.ContainsKey("ErrorMessage") == true
                        ? e.AdditionalData["ErrorMessage"]?.ToString()
                        : "未知错误";
                    UpdateStatusBar($"电磁阀错误: {errorMsg}");
                    break;
            }
        }


        //  新增：处理柱塞泵命令执行状态
        private void HandleCommandState(PistonPumpControl pumpControl, DeviceControlStateChangedEventArgs e)
        {
            if (e.StateValue is not CommandExecutionState state)
            {
                Debug.WriteLine($"状态值类型错误: {e.StateValue?.GetType().Name}");
                return;
            }

            Debug.WriteLine($" 命令状态: {e.StateName} -> {state}");

            switch (state)
            {
                case CommandExecutionState.Running:
                    // 显示加载动画
                    pumpControl.ShowLoading();
                    UpdateStatusBar(" 正在执行命令...");
                    break;

                case CommandExecutionState.Succeeded:
                    // 隐藏加载，命令成功
                    pumpControl.HideLoading();
                    UpdateStatusBar(" 命令执行成功");
                    break;

                case CommandExecutionState.Failed:
                    // 隐藏加载，显示失败动画
                    pumpControl.HideLoading();
                    pumpControl.ShowOperationFailure();
                    UpdateStatusBar(" 命令执行失败");
                    break;

                case CommandExecutionState.Error:
                    // 隐藏加载，显示错误动画
                    pumpControl.HideLoading();
                    pumpControl.ShowOperationFailure();
                    var errorMsg = e.AdditionalData?.ContainsKey("ErrorMessage") == true
                        ? e.AdditionalData["ErrorMessage"]?.ToString()
                        : "未知错误";
                    UpdateStatusBar($"命令异常: {errorMsg}");
                    break;
            }
        }

        /// <summary>
        ///  新增：处理流量计控件状态
        /// </summary>
        private void HandleFlowMeterControlState(MassFlowMeterControl meterControl, DeviceControlStateChangedEventArgs e)
        {
            switch (e.StateName)
            {
                case "CurrentFlow":
                    if (e.StateValue is double currentFlow)
                    {
                        meterControl.CurrentFlow = currentFlow;
                        Debug.WriteLine($" 当前流量已更新: {currentFlow:F2}");
                    }
                    break;

                case "TargetFlow":
                    if (e.StateValue is double targetFlow)
                    {
                        meterControl.TargetFlow = targetFlow;
                        Debug.WriteLine($" 目标流量已更新: {targetFlow:F2}");
                    }
                    break;

                case "IsFlowing":
                    if (e.StateValue is bool isFlowing)
                    {
                        meterControl.IsFlowing = isFlowing;
                        Debug.WriteLine($" 流动状态已更新: {isFlowing}");
                    }
                    break;

                case "Density":
                    // 可选：如果UI需要显示密度
                    if (e.StateValue is double density)
                    {
                        Debug.WriteLine($" 密度已更新: {density:F2}");
                    }
                    break;

                case "CommandState_设置基准流量":
                case "CommandState_停止流量控制":
                case "CommandState_设置液体密度":
                case "CommandState_获取基准流量":
                    HandleFlowMeterCommandState(meterControl, e);
                    break;
            }
        }

        /// <summary>
        ///  新增：处理流量计命令执行状态
        /// </summary>
        private void HandleFlowMeterCommandState(MassFlowMeterControl meterControl, DeviceControlStateChangedEventArgs e)
        {
            if (e.StateValue is not CommandExecutionState state)
            {
                Debug.WriteLine($"状态值类型错误: {e.StateValue?.GetType().Name}");
                return;
            }

            Debug.WriteLine($" 流量计命令状态: {e.StateName} -> {state}");

            switch (state)
            {
                case CommandExecutionState.Running:
                    meterControl.ShowLoading();
                    UpdateStatusBar(" 正在执行命令...");
                    break;

                case CommandExecutionState.Succeeded:
                    meterControl.HideLoading();
                    UpdateStatusBar(" 命令执行成功");
                    break;

                case CommandExecutionState.Failed:
                    meterControl.HideLoading();
                    meterControl.ShowOperationFailure();
                    UpdateStatusBar(" 命令执行失败");
                    break;

                case CommandExecutionState.Error:
                    meterControl.HideLoading();
                    meterControl.ShowOperationFailure();
                    var errorMsg = e.AdditionalData?.ContainsKey("ErrorMessage") == true
                        ? e.AdditionalData["ErrorMessage"]?.ToString()
                        : "未知错误";
                    UpdateStatusBar($"命令异常: {errorMsg}");
                    break;
            }
        }

        /// <summary>
        /// 处理电子秤控件状态（设备 → UI）
        /// </summary>
        private void HandleDigitalScaleControlState(DigitalScaleControl scaleControl, DeviceControlStateChangedEventArgs e)
        {
            switch (e.StateName)
            {
                case "WeightValue":
                    if (e.StateValue is double weight)
                    {
                        scaleControl.WeightValue = weight;
                        Debug.WriteLine($" 电子秤重量已更新: {weight:F2}");
                    }
                    break;

                case "IsPoweredOn":
                    if (e.StateValue is bool isPoweredOn)
                    {
                        scaleControl.IsPoweredOn = isPoweredOn;
                        Debug.WriteLine($" 电子秤电源状态已更新: {isPoweredOn}");
                    }
                    break;

                case "IsTared":
                    if (e.StateValue is bool isTared)
                    {
                        scaleControl.IsTared = isTared;
                        Debug.WriteLine($" 电子秤去皮状态已更新: {isTared}");
                    }
                    break;

                case "CurrentUnit":
                    if (e.StateValue is string unit)
                    {
                        scaleControl.CurrentUnit = unit;
                        Debug.WriteLine($" 电子秤单位已更新: {unit}");
                    }
                    break;

                case "CommandState_开机":
                case "CommandState_关机":
                case "CommandState_去皮":
                case "CommandState_切换单位":
                    HandleDigitalScaleCommandState(scaleControl, e);
                    break;
            }
        }

        /// <summary>
        /// 处理电子秤命令执行状态
        /// </summary>
        private void HandleDigitalScaleCommandState(DigitalScaleControl scaleControl, DeviceControlStateChangedEventArgs e)
        {
            if (e.StateValue is not CommandExecutionState state)
            {
                Debug.WriteLine($"状态值类型错误: {e.StateValue?.GetType().Name}");
                return;
            }

            var commandName = e.StateName.Replace("CommandState_", "");
            Debug.WriteLine($" 电子秤命令状态: {commandName} -> {state}");

            switch (state)
            {
                case CommandExecutionState.Running:
                    scaleControl.ShowLoading();
                    UpdateStatusBar($" 正在执行 {commandName}...");
                    break;

                case CommandExecutionState.Succeeded:
                    scaleControl.HideLoading();
                    UpdateStatusBar($" {commandName} 成功");
                    break;

                case CommandExecutionState.Failed:
                    scaleControl.HideLoading();
                    scaleControl.ShowOperationFailure();
                    UpdateStatusBar($"{commandName} 失败");
                    break;

                case CommandExecutionState.Error:
                    scaleControl.HideLoading();
                    scaleControl.ShowOperationFailure();
                    var errorMsg = e.AdditionalData?.ContainsKey("ErrorMessage") == true
                        ? e.AdditionalData["ErrorMessage"]?.ToString()
                        : "未知错误";
                    UpdateStatusBar($"{commandName} 异常: {errorMsg}");
                    break;
            }
        }

        /// <summary>
        /// 处理加热磁力搅拌器控件状态（设备 → UI）
        /// </summary>
        private void HandleMagneticStirrerControlState(
            MagneticStirrerControl stirrerControl,
            DeviceControlStateChangedEventArgs e)
        {
            switch (e.StateName)
            {
                case "ExternalTemperature":
                    if (e.StateValue is double extTemp)
                        stirrerControl.Temperature = extTemp;
                    break;

                case "TemperatureSV":
                    if (e.StateValue is double tempSv)
                        stirrerControl.TemperatureSetpoint = tempSv;
                    break;

                case "SpeedMeasured":
                    if (e.StateValue is double speed)
                        stirrerControl.Speed = speed;
                    break;

                case "SpeedSV":
                    if (e.StateValue is double speedSv)
                        stirrerControl.SpeedSetpoint = speedSv;
                    break;

                case "TemperatureSwitch":
                    if (e.StateValue is bool heating)
                        stirrerControl.IsHeating = heating;
                    break;

                case "SpeedSwitch":
                    if (e.StateValue is bool stirring)
                        stirrerControl.IsStirring = stirring;
                    break;

                case "CommandState_温度控制":
                case "CommandState_搅拌控制":
                    HandleMagneticStirrerCommandState(stirrerControl, e);
                    break;
            }
        }

        /// <summary>
        /// 处理加热磁力搅拌器命令执行状态
        /// </summary>
        private void HandleMagneticStirrerCommandState(
            MagneticStirrerControl stirrerControl,
            DeviceControlStateChangedEventArgs e)
        {
            if (e.StateValue is not CommandExecutionState state)
                return;

            var commandName = e.StateName.Replace("CommandState_", "");

            switch (state)
            {
                case CommandExecutionState.Running:
                    stirrerControl.ShowLoading();
                    UpdateStatusBar($" 正在执行 {commandName}...");
                    break;

                case CommandExecutionState.Succeeded:
                    stirrerControl.HideLoading();
                    UpdateStatusBar($" {commandName} 成功");
                    break;

                case CommandExecutionState.Failed:
                case CommandExecutionState.Error:
                    stirrerControl.HideLoading();
                    stirrerControl.ShowOperationFailure();
                    UpdateStatusBar($"{commandName} 失败");
                    break;
            }
        }

        /// <summary>
        /// 处理精睿柱塞泵控件状态（设备 → UI）
        /// </summary>
        private void HandleJingRuiPumpControlState(
            JingRuiPumpControl pumpControl,
            DeviceControlStateChangedEventArgs e)
        {
            switch (e.StateName)
            {
                case "Flow":
                    if (e.StateValue is double flow)
                        pumpControl.Flow = flow;
                    break;

                case "FlowSV":
                    if (e.StateValue is double flowSv)
                        pumpControl.FlowSetpoint = flowSv;
                    break;

                case "Pressure":
                    if (e.StateValue is double pressure)
                        pumpControl.Pressure = pressure;
                    break;

                case "PressureMax":
                    if (e.StateValue is double pressureMax)
                        pumpControl.PressureLimit = pressureMax;
                    break;

                // 量程由驱动在首轮采集时推一次,让压力条与液流动画按泵本体的实际规格标定
                case "FlowRange":
                    if (e.StateValue is double flowRange && flowRange > 0)
                        pumpControl.FlowRange = flowRange;
                    break;

                case "PressureRange":
                    if (e.StateValue is double pressureRange && pressureRange > 0)
                        pumpControl.PressureRange = pressureRange;
                    break;

                case "IsRunning":
                    if (e.StateValue is bool running)
                        pumpControl.IsRunning = running;
                    break;

                case "IsDosing":
                    if (e.StateValue is bool dosing)
                        pumpControl.IsDosing = dosing;
                    break;

                case "FaultCode":
                    if (e.StateValue is double faultCode)
                        pumpControl.FaultCode = faultCode;
                    break;

                case "CommandState_设置流量":
                case "CommandState_启动泵":
                case "CommandState_停止泵":
                case "CommandState_按流量启动":
                case "CommandState_定量输送":
                case "CommandState_设置压力上下限":
                case "CommandState_压力校零":
                case "CommandState_故障消除":
                    HandleJingRuiPumpCommandState(pumpControl, e);
                    break;
            }
        }

        /// <summary>
        /// 处理精睿柱塞泵命令执行状态
        /// </summary>
        private void HandleJingRuiPumpCommandState(
            JingRuiPumpControl pumpControl,
            DeviceControlStateChangedEventArgs e)
        {
            if (e.StateValue is not CommandExecutionState state)
                return;

            var commandName = e.StateName.Replace("CommandState_", "");

            switch (state)
            {
                case CommandExecutionState.Running:
                    pumpControl.ShowLoading();
                    UpdateStatusBar($" 正在执行 {commandName}...");
                    break;

                case CommandExecutionState.Succeeded:
                    pumpControl.HideLoading();
                    UpdateStatusBar($" {commandName} 成功");
                    break;

                case CommandExecutionState.Failed:
                case CommandExecutionState.Error:
                    pumpControl.HideLoading();
                    pumpControl.ShowOperationFailure();
                    UpdateStatusBar($"{commandName} 失败");
                    break;
            }
        }

        /// <summary>
        /// 处理高低温循环器控件状态（设备 → UI）
        /// </summary>
        private void HandleTemperatureCirculatorControlState(
            TemperatureControlSystemControl circulatorControl,
            DeviceControlStateChangedEventArgs e)
        {
            switch (e.StateName)
            {
                case "SetTemperature":
                    if (e.StateValue is double setTemp)
                    {
                        circulatorControl.SetTargetTemperature(setTemp);
                        Debug.WriteLine($"设定温度已更新: {setTemp:F1}℃");
                    }
                    break;

                case "ActualTemperature":
                    if (e.StateValue is double actualTemp)
                    {
                        circulatorControl.UpdateCurrentTemperature(actualTemp);
                        Debug.WriteLine($"当前温度已更新: {actualTemp:F1}℃");
                    }
                    break;

                case "IsCycleRunning":
                    if (e.StateValue is bool isCycleRunning)
                    {
                        UpdateCirculatorButtonState(circulatorControl, "CirculationButton", isCycleRunning);
                        Debug.WriteLine($"循环状态已更新: {isCycleRunning}");
                    }
                    break;

                case "IsHeatingRunning":
                    if (e.StateValue is bool isHeatingRunning)
                    {
                        UpdateCirculatorButtonState(circulatorControl, "HeatingButton", isHeatingRunning);
                        Debug.WriteLine($"加热状态已更新: {isHeatingRunning}");
                    }
                    break;

                case "IsCoolingRunning":
                    if (e.StateValue is bool isCoolingRunning)
                    {
                        UpdateCirculatorButtonState(circulatorControl, "CoolingButton", isCoolingRunning);
                        Debug.WriteLine($"制冷状态已更新: {isCoolingRunning}");
                    }
                    break;

                case "CommandState_设置温度":
                case "CommandState_启动循环":
                case "CommandState_停止循环":
                case "CommandState_启动加热":
                case "CommandState_停止加热":
                case "CommandState_启动制冷":
                case "CommandState_停止制冷":
                    HandleCirculatorCommandState(circulatorControl, e);
                    break;
            }
        }

        /// <summary>
        /// 更新高低温循环器按钮状态
        /// </summary>
        private void UpdateCirculatorButtonState(
            TemperatureControlSystemControl circulator,
            string buttonName,
            bool isActive)
        {
            try
            {
                var button = circulator.FindName(buttonName) as Button;
                if (button != null)
                {
                    button.Tag = isActive ? "Active" : null;
                    Debug.WriteLine($"按钮 {buttonName} 状态已更新: {(isActive ? "激活" : "未激活")}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新按钮状态失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理高低温循环器命令执行状态
        /// </summary>
        private void HandleCirculatorCommandState(
            TemperatureControlSystemControl circulator,
            DeviceControlStateChangedEventArgs e)
        {
            if (e.StateValue is not CommandExecutionState state)
            {
                Debug.WriteLine($"状态值类型错误: {e.StateValue?.GetType().Name}");
                return;
            }

            var commandName = e.StateName.Replace("CommandState_", "");
            Debug.WriteLine($"高低温循环器命令状态: {commandName} -> {state}");

            switch (state)
            {
                case CommandExecutionState.Running:
                    circulator.ShowLoading();
                    UpdateStatusBar($"正在执行 {commandName}...");
                    break;

                case CommandExecutionState.Succeeded:
                    circulator.HideLoading();
                    UpdateStatusBar($"{commandName} 成功");
                    break;

                case CommandExecutionState.Failed:
                    circulator.HideLoading();
                    circulator.ShowOperationFailure();
                    UpdateStatusBar($"{commandName} 失败");
                    break;

                case CommandExecutionState.Error:
                    circulator.HideLoading();
                    circulator.ShowOperationFailure();
                    var errorMsg = e.AdditionalData?.ContainsKey("ErrorMessage") == true
                        ? e.AdditionalData["ErrorMessage"]?.ToString()
                        : "未知错误";
                    UpdateStatusBar($"{commandName} 异常: {errorMsg}");
                    break;
            }
        }

        /// <summary>
        ///  新增：将温度状态写入 MicroreactorControl 的三个依赖属性
        /// </summary>
        private void HandleMicroreactorTemperatureState(
            MicroreactorControl reactor,
            DeviceControlStateChangedEventArgs e)
        {
            try
            {
                switch (e.StateName)
                {
                    case "TT1001_ActualTemp":
                        if (e.StateValue is double t1)
                        {
                            reactor.Temperature1 = Math.Round(t1, 2);
                            Debug.WriteLine($"微反应器 TT1001 温度已更新: {t1:F2}℃");
                        }
                        break;

                    case "TT1002_ActualTemp":
                        if (e.StateValue is double t2)
                        {
                            reactor.Temperature2 = Math.Round(t2, 2);
                            Debug.WriteLine($"微反应器 TT1002 温度已更新: {t2:F2}℃");
                        }
                        break;

                    case "TT1003_ActualTemp":
                        if (e.StateValue is double t3)
                        {
                            reactor.Temperature3 = Math.Round(t3, 2);
                            Debug.WriteLine($"微反应器 TT1003 温度已更新: {t3:F2}℃");
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理微反应器温度状态失败: {ex.Message}");
            }
        }

        ///// <summary>
        ///// 处理管式反应器控件状态（设备驱动 → UI）
        ///// </summary>
        //private void HandleTubularReactorControlState(
        //    TubularReactorControl reactorControl,
        //    DeviceControlStateChangedEventArgs e)
        //{
        //    try
        //    {
        //        switch (e.StateName)
        //        {
        //            // ---- 温度更新 ----
        //            case "Temperature":
        //            case "OutletTemperature":
        //                if (e.StateValue is double temperature)
        //                {
        //                    reactorControl.Temperature = Math.Round(temperature, 1);
        //                    Debug.WriteLine($"管式反应器温度已更新: {temperature:F1}°C");
        //                }
        //                break;

        //            // ---- 压力更新 ----
        //            case "Pressure":
        //            case "OutletPressure":
        //                if (e.StateValue is double pressure)
        //                {
        //                    reactorControl.Pressure = Math.Round(pressure, 2);
        //                    Debug.WriteLine($"管式反应器压力已更新: {pressure:F2} MPa");
        //                }
        //                break;

        //            // ---- 反应运行状态 ----
        //            case "IsReacting":
        //                if (e.StateValue is bool isReacting)
        //                {
        //                    reactorControl.IsReacting = isReacting;
        //                    Debug.WriteLine($"管式反应器反应状态已更新: {isReacting}");
        //                }
        //                break;

        //            // ---- 流动启停状态 ----
        //            case "IsFlowEnabled":
        //                if (e.StateValue is bool isFlowEnabled)
        //                {
        //                    reactorControl.IsFlowEnabled = isFlowEnabled;
        //                    Debug.WriteLine($"管式反应器流动状态已更新: {isFlowEnabled}");
        //                }
        //                break;

        //            // ---- 流量更新 ----
        //            case "FlowRate":
        //                if (e.StateValue is double flowRate)
        //                {
        //                    reactorControl.SetFlowRate(flowRate);
        //                    Debug.WriteLine($"管式反应器流量已更新: {flowRate:F1} kg/h");
        //                }
        //                break;

        //            // ---- 命令执行状态 ----
        //            case "CommandState_启动反应":
        //            case "CommandState_停止反应":
        //            case "CommandState_启动流动":
        //            case "CommandState_停止流动":
        //            case "CommandState_设置流量":
        //                HandleTubularReactorCommandState(reactorControl, e);
        //                break;

        //            default:
        //                Debug.WriteLine($"管式反应器未处理的状态: {e.StateName}");
        //                break;
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine($"处理管式反应器控件状态失败: {ex.Message}");
        //    }
        //}

        /// <summary>
        /// 处理管式反应器命令执行状态
        /// </summary>
        private void HandleTubularReactorCommandState(
            TubularReactorControl reactorControl,
            DeviceControlStateChangedEventArgs e)
        {
            if (e.StateValue is not CommandExecutionState state)
            {
                Debug.WriteLine($"状态值类型错误: {e.StateValue?.GetType().Name}");
                return;
            }

            var commandName = e.StateName.Replace("CommandState_", "");
            Debug.WriteLine($"管式反应器命令状态: {commandName} -> {state}");

            // TubularReactorControl 暂无 ShowLoading/HideLoading，
            // 仅通过状态栏向用户反馈
            switch (state)
            {
                case CommandExecutionState.Running:
                    UpdateStatusBar($"正在执行 {commandName}...");
                    break;

                case CommandExecutionState.Succeeded:
                    UpdateStatusBar($"{commandName} 成功");
                    break;

                case CommandExecutionState.Failed:
                    UpdateStatusBar($"{commandName} 失败");
                    break;

                case CommandExecutionState.Error:
                    var errorMsg = e.AdditionalData?.ContainsKey("ErrorMessage") == true
                        ? e.AdditionalData["ErrorMessage"]?.ToString()
                        : "未知错误";
                    UpdateStatusBar($"{commandName} 异常: {errorMsg}");
                    break;
            }
        }
        /// <summary>
        ///  辅助方法：更新状态栏
        /// </summary>
        private void UpdateStatusBar(string message)
        {
            _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(message);
            Debug.WriteLine($" 状态栏: {message}");
        }
        /// <summary>
        ///  新增：处理命令执行状态变化
        /// </summary>
        private void HandleCommandStateChange(DeviceControlStateChangedEventArgs e)
        {
            try
            {
                if (e.StateValue is not CommandExecutionState executionState)
                {
                    Debug.WriteLine($"命令状态值类型错误: {e.StateValue?.GetType().Name}");
                    return;
                }

                // 提取命令名称
                var commandName = e.StateName.Replace("CommandState_", "");

                Debug.WriteLine($" 命令状态变化: {commandName} -> {executionState}");

                // 根据执行状态更新UI
                switch (executionState)
                {
                    case CommandExecutionState.Running:
                        _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                            $" 正在执行 {commandName}...");
                        break;

                    case CommandExecutionState.Succeeded:
                        _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                            $" {commandName} 成功");
                        break;

                    case CommandExecutionState.Failed:
                        _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                            $" {commandName} 失败");
                        break;

                    case CommandExecutionState.Error:
                        var errorMessage = e.AdditionalData?.ContainsKey("ErrorMessage") == true
                            ? e.AdditionalData["ErrorMessage"]?.ToString()
                            : "未知错误";
                        _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                            $"{commandName} 异常: {errorMessage}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($" 处理命令状态变化失败: {ex.Message}");
            }
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

            //  启用设备级别缓存
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

            // 设备图标。
            // 这里减的是"名称区预留高度",不是名称的实际高度 —— 两者故意不相等:
            // 各设备在 GetAdjustedDeviceSize 里的固定尺寸都是按"减 24"调出来的,
            // 改成按实测高度减,名字短的设备图标会突然变大,把已经调好的比例全打乱。
            // 名字折成两行时会比预留多占几个像素,DockPanel 让图标少几像素,
            // 图片是 Uniform 居中缩放的,肉眼看不出来。
            var iconElement = CreateDeviceIcon(canvasDevice.Device, adjustedSize.Width - 4,
                                               adjustedSize.Height - DeviceNameReservedHeight);
            mainPanel.Children.Add(iconElement);

            return mainPanel;
        }


        /// <summary>
        /// 画布设备底部名称区的**预留**高度。各设备的固定尺寸都是按这个数调出来的,
        /// 改它会让所有设备的图标比例一起变,不要为了单台设备动这里。
        /// </summary>
        private const double DeviceNameReservedHeight = 24;

        /// <summary>
        /// 名称文本自身的高度上限。FontSize 9 时一行约 14px、两行约 26px,
        /// 给到 30 让两行完整显示,再长就靠 TextTrimming 打省略号。
        /// </summary>
        private const double DeviceNameMaxHeight = 30;

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
                // Margin = new Thickness(0, 0, 0, 0), //  新增：底部边距 15 像素
                IsHitTestVisible = false,
                // 名字长了会折行。原来这里是 20 —— FontSize 9 时一行约 14px、两行约 26px,
                // 所以但凡折了行,第二行就被从字的中间切掉,像"(Tricontinent"那样只露出半截。
                // 放到 30 让两行能完整显示。
                MaxHeight = DeviceNameMaxHeight,
                // 超过两行的名字用省略号收尾。没有这一条的话超出部分是硬切,
                // 看上去像渲染坏了;有省略号至少能看出"名字还没说完"。
                TextTrimming = TextTrimming.CharacterEllipsis
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

            // ★ 改:按设备类型分级,瓶子(130) > 秤(110) > 普通(100) > 管道(10)
            int zIndex = GetDeviceZIndex(canvasDevice.Device);
            Panel.SetZIndex(deviceControl, zIndex);

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
                if (e.ClickCount == 2)
                {
                    Debug.WriteLine($"设备双击，显示属性弹窗: {deviceId}");
                    e.Handled = true;

                    //  关键修复：打开对话框前彻底清理拖拽状态
                    // 防止对话框关闭后 _isMouseDown 残留，导致误触发设备拖拽
                    if (deviceElement.IsMouseCaptured)
                        deviceElement.ReleaseMouseCapture();
                    if (DesignCanvas.IsMouseCaptured)
                        DesignCanvas.ReleaseMouseCapture();
                    CleanupDragState();

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
                _editingDeviceId = deviceId;
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
                    Width = 960,
                    Height = 620,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = Window.GetWindow(this),
                    ResizeMode = ResizeMode.NoResize,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    AllowsTransparency = true,   // ← 新增：支持圆角
                    Background = Brushes.Transparent  // ← 新增：透明背景让圆角可见
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
                if (string.IsNullOrEmpty(deviceId))
                    return;

                //  关键修复：允许 parameterKeys 为空集合（表示用户取消了所有选择）
                // 原先 parameterKeys == null 时直接 return，导致旧的选中状态残留
                _deviceMonitoringSelections[deviceId] = new HashSet<string>(parameterKeys ?? new HashSet<string>());

                Debug.WriteLine($"保存设备 {deviceId} 的完整监控参数选择键: " +
                                $"{string.Join(", ", _deviceMonitoringSelections[deviceId])}");
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
                var deviceElement = FindDeviceElementInCanvas(deviceId);
                if (deviceElement == null)
                {
                    Debug.WriteLine($"未找到设备元素: {deviceId}");
                    return;
                }

                double cardLeft, cardTop;

                if (_monitoringCardPositions.TryGetValue(deviceId, out var savedPosition))
                {
                    cardLeft = savedPosition.X;
                    cardTop = savedPosition.Y;
                }
                else
                {
                    var deviceLeft = Canvas.GetLeft(deviceElement);
                    var deviceTop = Canvas.GetTop(deviceElement);
                    var deviceWidth = deviceElement.ActualWidth;

                    cardLeft = deviceLeft + deviceWidth + 15;
                    cardTop = deviceTop;
                }

                Canvas.SetLeft(monitoringCard, cardLeft);
                Canvas.SetTop(monitoringCard, cardTop);
                Canvas.SetZIndex(monitoringCard, 500);
                monitoringCard.Tag = $"MonitoringCard_{deviceId}";

                RenderOptions.SetCachingHint(monitoringCard, CachingHint.Cache);
                monitoringCard.CacheMode = new BitmapCache
                {
                    EnableClearType = false,
                    RenderAtScale = 1.0,
                    SnapsToDevicePixels = true
                };

                DesignCanvas.Children.Add(monitoringCard);

                // ── 监听卡片位置变化，实时更新折线 ────────────────────────────
                var localDeviceId = deviceId; // 捕获闭包变量

                EventHandler leftChangedHandler = (s, e) =>
                {
                    // 同步保存最新位置，确保保存/加载时坐标正确
                    if (_monitoringCards.TryGetValue(localDeviceId, out var card))
                    {
                        var l = Canvas.GetLeft(card);
                        var t = Canvas.GetTop(card);
                        if (!double.IsNaN(l) && !double.IsNaN(t))
                            _monitoringCardPositions[localDeviceId] = new Point(l, t);
                    }
                    UpdateMonitoringConnectionLine(localDeviceId);
                };

                EventHandler topChangedHandler = (s, e) =>
                {
                    if (_monitoringCards.TryGetValue(localDeviceId, out var card))
                    {
                        var l = Canvas.GetLeft(card);
                        var t = Canvas.GetTop(card);
                        if (!double.IsNaN(l) && !double.IsNaN(t))
                            _monitoringCardPositions[localDeviceId] = new Point(l, t);
                    }
                    UpdateMonitoringConnectionLine(localDeviceId);
                };

                DependencyPropertyDescriptor
                    .FromProperty(Canvas.LeftProperty, typeof(UIElement))
                    .AddValueChanged(monitoringCard, leftChangedHandler);

                DependencyPropertyDescriptor
                    .FromProperty(Canvas.TopProperty, typeof(UIElement))
                    .AddValueChanged(monitoringCard, topChangedHandler);

                // 保存处理器引用，供移除时使用
                _monitoringCardLeftHandlers[deviceId] = leftChangedHandler;
                _monitoringCardTopHandlers[deviceId] = topChangedHandler;

                // ── 监控卡片渲染完成后，创建虚线连接 ────────────────────────────
                monitoringCard.SizeChanged += (s, e) =>
                {
                    if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
                        UpdateMonitoringConnectionLine(localDeviceId);
                };

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateMonitoringConnectionLine(deviceId);
                }), System.Windows.Threading.DispatcherPriority.Loaded);

                Debug.WriteLine($"监控卡片已添加（带缓存和虚线）: 位置({cardLeft:F0}, {cardTop:F0})");
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
                    // ── 取消位置监听 ──────────────────────────────────────────
                    if (_monitoringCardLeftHandlers.TryGetValue(deviceId, out var leftHandler))
                    {
                        DependencyPropertyDescriptor
                            .FromProperty(Canvas.LeftProperty, typeof(UIElement))
                            .RemoveValueChanged(card, leftHandler);
                        _monitoringCardLeftHandlers.Remove(deviceId);
                    }

                    if (_monitoringCardTopHandlers.TryGetValue(deviceId, out var topHandler))
                    {
                        DependencyPropertyDescriptor
                            .FromProperty(Canvas.TopProperty, typeof(UIElement))
                            .RemoveValueChanged(card, topHandler);
                        _monitoringCardTopHandlers.Remove(deviceId);
                    }

                    DesignCanvas.Children.Remove(card);
                    _monitoringCards.Remove(deviceId);
                }

                // ── 同步移除虚线 ──────────────────────────────────────────────
                RemoveMonitoringConnectionLine(deviceId);

                Debug.WriteLine($"移除监控卡片及连接线: {deviceId}");
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
                if (!_monitoringCards.TryGetValue(deviceId, out var card)) return;

                var deviceElement = FindDeviceElementInCanvas(deviceId);
                if (deviceElement == null) return;

                var deviceLeft = Canvas.GetLeft(deviceElement);
                var deviceTop = Canvas.GetTop(deviceElement);
                var deviceWidth = deviceElement.ActualWidth;

                Canvas.SetLeft(card, deviceLeft + deviceWidth + 15);
                Canvas.SetTop(card, deviceTop);

                // ── 同步更新虚线 ───────────────────────────────────────
                UpdateMonitoringConnectionLine(deviceId);

                Debug.WriteLine($"更新监控卡片及连接线位置: {deviceId}");
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

                   

                    _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();
                }
                if (!string.IsNullOrEmpty(_editingDeviceId))
                {
                    RefreshConicalFlaskLiquidName(_editingDeviceId);
                }
                _editingDeviceId = null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理设备属性弹窗结果失败: {ex.Message}");
            }
        }
        /// <summary>
        /// 刷新锥形瓶的液体名称显示
        /// </summary>
        private void RefreshConicalFlaskLiquidName(string deviceId)
        {
            try
            {
                var canvasDevice = _viewModel?.CanvasDevices?.FirstOrDefault(d => d.DeviceId == deviceId);
                if (canvasDevice?.Device == null) return;

                var imageLocationProp = canvasDevice.Device.GetType().GetProperty("ImageLocation");
                var imageLocation = imageLocationProp?.GetValue(canvasDevice.Device) as string;
                if (imageLocation != "CUSTOM_CONTROL:ConicalFlaskControl") return;

                var liquidNameParam = canvasDevice.Device.Parameters?.Variables?
                    .FirstOrDefault(p => p.Name == "LiquidName");
                string liquidName = liquidNameParam?.Value as string ?? "";

                var deviceElement = FindDeviceElementInCanvas(deviceId);
                if (deviceElement == null) return;

                var flaskControl = FindVisualChild<ConicalFlaskControl>(deviceElement);
                if (flaskControl != null)
                {
                    flaskControl.LiquidName = liquidName;
                    Debug.WriteLine($"锥形瓶 {deviceId} 液体名称已更新: {liquidName}");
                }
                var colorParam = canvasDevice.Device.Parameters?.Variables?
    .FirstOrDefault(p => p.Name == "LiquidColor");
                if (colorParam?.Value is string colorName && !string.IsNullOrEmpty(colorName))
                {
                    if (colorName == "无色")
                        flaskControl.SetLiquidColor(200, 220, 235, 80);
                    else
                        flaskControl.SetLiquidColor(colorName);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"刷新锥形瓶液体名称失败: {ex.Message}");
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
                if (deviceElement == null) return;

                Canvas.SetLeft(deviceElement, newPosition.X - deviceElement.ActualWidth / 2);
                Canvas.SetTop(deviceElement, newPosition.Y - deviceElement.ActualHeight / 2);

                ScheduleLayoutUpdate();

                // 更新监控卡片位置
                UpdateMonitoringCardPosition(deviceId);

                // ── 再次同步虚线（设备自身也移动了）────────────────────
                UpdateMonitoringConnectionLine(deviceId);
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

                //  如果是拖拽状态，立即处理，不节流
                if (_isMouseDown && _currentDraggingDeviceId == deviceId && e.LeftButton == MouseButtonState.Pressed)
                {
                    HandleDeviceMouseMove(deviceElement, e);
                    return; // 重要：直接返回，不受节流影响
                }

                //  只对非拖拽的鼠标移动进行节流
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
            // ★ 让选中标记跟着设备走
            _selectionOverlay?.Refresh();
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
            // 不再修改 BorderBrush,鼠标悬停只改光标即可
            if (deviceElement != null && deviceId != _selectedDeviceId)
            {
                deviceElement.Cursor = System.Windows.Input.Cursors.Hand;
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
                border.BorderBrush = Brushes.Transparent;
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

                //  取消订阅
                if (_deviceUIInteractions.TryGetValue(deviceId, out var uiInteraction))
                {
                    uiInteraction.ControlStateChanged -= OnDeviceStateChanged;
                    _deviceUIInteractions.Remove(deviceId);
                    Debug.WriteLine($" 已取消设备UI交互订阅: {deviceId}");
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
                //  特定设备直接返回固定尺寸,绕开 60~120 的图片尺寸限制
                switch (device?.GetType().Name)
                {
                    case "TwoLiquidOneGasFeedSystem_ModbusRTU":
                        return new Size(200*0.7, 280*0.7); 
                    case "SiliconCarbideChip_ModbusRTU":
                        return new Size(400, 340);
                    case "SiphonInfusionPump_ModbusRTU":
                        return new Size(200, 170);
                    case "DynamicTubularReactor_ModbusRTU":
                        return new Size(240, 216);
                    case "HighPreactor_ModbusRTU":
                        return new Size(200, 300);
                    case "HighTempFurnace_ModbusRTU":
                        return new Size(198, 120);
                    case "HighLowTemperature_ModbusTCP":
                        return new Size(288, 400);
                    case "JingRuiPump_ModbusRTU":
                        return new Size(297*0.8, 300 * 0.8);      

                        // 以后还想给别的设备单独定尺寸,继续在这里加 case
                }

                var imageLocationProperty = device.GetType().GetProperty("ImageLocation");
                string imageLocation = imageLocationProperty?.GetValue(device) as string;

                // 自定义控件:统一用绝对像素值,避免"原尺寸 × 系数"的混乱
                if (!string.IsNullOrEmpty(imageLocation) && imageLocation.StartsWith("CUSTOM_CONTROL:"))
                {
                    string controlType = imageLocation.Replace("CUSTOM_CONTROL:", "");

                    switch (controlType)
                    {
                        case "ConicalFlaskControl":
                            return new Size(66, 105);         // 瓶子瘦高

                        case "DigitalScaleControl":
                            return new Size(140, 105);        // 秤,能放下瓶子

                        case "PistonPumpControl":
                            return new Size(105, 105);        // 矮方块

                        case "ValveControl":
                        case "SolenoidValveControl":
                            return new Size(66, 66);          // 小方块

                        case "MassFlowMeterControl":
                            return new Size(66, 105);         // 瘦高

                        case "MicroreactorControl":
                            return new Size(340, 210);        // 横长台式

                        case "HeatExchangerControl":
                            return new Size(66, 105);         // 瘦高

                        case "TemperatureControlSystemControl":
                            return new Size(105, 170);        // 高瘦塔式

                        case "TubularReactorControl":
                            return new Size(170, 105);        // 横长管式

                        case "MagneticStirrerControl":
                            // 控件画布已裁到 306×501,这里保持同比例,只调 scale 一个数即可缩放。
                            // 比例不一致的话 Viewbox 是 Uniform,图会缩在中间、四周留白。
                            return new Size(306 * 0.62, 501 * 0.62);   // ≈190×311

                        case "JingRuiPumpControl":
                            // 控件画布 320×250(含机身外的进出液管),同比例缩放
                            return new Size(320 * 0.55, 250 * 0.55);   // ≈176×138

                        default:
                            Debug.WriteLine($"未识别的自定义控件: {controlType}");
                            return new Size(defaultWidth, defaultHeight);
                    }
                }

                // 普通图片
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
        /// 获取设备的渲染层级 ZIndex。
        /// 层级越高画在越上面。瓶子永远在秤上面,符合"放置"的物理直觉。
        /// </summary>
        private int GetDeviceZIndex(IDevice device)
        {
            try
            {
                var imageLocationProperty = device.GetType().GetProperty("ImageLocation");
                string imageLocation = imageLocationProperty?.GetValue(device) as string;

                if (!string.IsNullOrEmpty(imageLocation) && imageLocation.StartsWith("CUSTOM_CONTROL:"))
                {
                    string controlType = imageLocation.Replace("CUSTOM_CONTROL:", "");
                    switch (controlType)
                    {
                        // 顶层 — 放在别的设备上面的容器/样品
                        case "ConicalFlaskControl":
                            return 130;

                        // 承载层 — 平台型,被别的东西放在上面
                        case "DigitalScaleControl":
                        case "MagneticStirrerControl":
                            return 110;

                        // 默认层 — 大多数设备
                        default:
                            return 100;
                    }
                }

                return 100;  // 普通图片设备走默认
            }
            catch
            {
                return 100;
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

                //  新增：检查是否为自定义控件
                if (!string.IsNullOrEmpty(imageLocation) && imageLocation.StartsWith("CUSTOM_CONTROL:"))
                {
                    return CreateCustomControlIcon(imageLocation, targetWidth, targetHeight, device);
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
        private UIElement CreateCustomControlIcon(string imageLocation, double targetWidth, double targetHeight, IDevice device)
        {
            try
            {
                string controlType = imageLocation.Replace("CUSTOM_CONTROL:", "");
                Debug.WriteLine($"创建自定义控件: {controlType}, 目标尺寸: {targetWidth}x{targetHeight}");

                switch (controlType)
                {
                    case "ConicalFlaskControl":
                        return CreateConicalFlaskIcon(targetWidth, targetHeight, device);

                    //  新增：柱塞泵控件
                    case "PistonPumpControl":
                        return CreatePistonPumpIcon(targetWidth, targetHeight);

                    //  新增：阀门控件
                    case "ValveControl":
                        return CreateValveIcon(targetWidth, targetHeight);

                    case "SolenoidValveControl":
                        return CreateSolenoidValveIcon(targetWidth, targetHeight);

                    //  新增：流量计控件
                    case "MassFlowMeterControl":
                        return CreateMassFlowMeterIcon(targetWidth, targetHeight);
                    //微反应器
                    case "MicroreactorControl":
                        return CreateMicroreactorIcon(targetWidth, targetHeight);
                    //管式反应器
                    case "TubularReactorControl":
                        return CreateTubularReactorIcon(targetWidth, targetHeight);
                    // 未来可以添加其他自定义控件
                    case "HeatExchangerControl":
                        return CreateHeatExchangerIcon(targetWidth, targetHeight);
                        //添加电子秤
                    case "DigitalScaleControl":
                        return CreateDigitalScaleIcon(targetWidth, targetHeight);
                        //加热磁力搅拌器
                    case "MagneticStirrerControl":
                        return CreateMagneticStirrerIcon(targetWidth, targetHeight);
                        //精睿柱塞泵
                    case "JingRuiPumpControl":
                        return CreateJingRuiPumpIcon(targetWidth, targetHeight);
                    case "TemperatureControlSystemControl":
                        return CreateTemperatureCirculatorIcon(targetWidth, targetHeight);
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
        /// 创建锥形瓶图标（手动缩放，标注可溢出）
        /// </summary>
        private UIElement CreateConicalFlaskIcon(double targetWidth, double targetHeight, IDevice device)
        {
            try
            {
                var flaskControl = new ConicalFlaskControl
                {
                    FlaskWidth = 80,
                    FlaskHeight = 120,
                    IsHitTestVisible = false,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ClipToBounds = false
                };

                HideFlaskInteractiveElements(flaskControl);

                // 手动缩放：计算比例（等同于 Viewbox 的 Stretch.Uniform）
                double scaleX = targetWidth / 80.0;
                double scaleY = targetHeight / 120.0;
                double scale = Math.Min(scaleX, scaleY);

                flaskControl.RenderTransform = new ScaleTransform(scale, scale);
                flaskControl.RenderTransformOrigin = new Point(0, 0);

                flaskControl.Loaded += (s, e) =>
                {
                    // 读取液体颜色
                    var colorParam = device?.Parameters?.Variables?
                        .FirstOrDefault(p => p.Name == "LiquidColor");
                    if (colorParam?.Value is string colorName && !string.IsNullOrEmpty(colorName))
                    {
                        if (colorName == "无色")
                        {
                            flaskControl.SetLiquidColor(200, 220, 235, 80); // 极淡透明
                        }
                        else
                        {
                            flaskControl.SetLiquidColor(colorName);
                        }
                    }
                    else
                    {
                        flaskControl.SetLiquidColor(96, 176, 224, 180); // 默认蓝
                    }

                    // 液位:读取设备 "Volume" 参数(当作 0–100 充满度),并在其被修改时实时刷新
                    var volParam = device?.Parameters?.Variables?
                        .FirstOrDefault(p => p.Name == "Volume");
                    double initLevel = volParam?.Value != null ? System.Convert.ToDouble(volParam.Value) : 0;
                    flaskControl.SetLiquidLevel(initLevel, animated: true);
                    if (volParam != null)
                    {
                        EventHandler onVolChanged = (vs, ve) =>
                            flaskControl.Dispatcher.Invoke(() =>
                                flaskControl.SetLiquidLevel(
                                    volParam.Value != null ? System.Convert.ToDouble(volParam.Value) : 0,
                                    animated: true));
                        volParam.ValueChanged += onVolChanged;
                        flaskControl.Unloaded += (_, __) => volParam.ValueChanged -= onVolChanged;
                    }

                    // 读取液体名称
                    var nameParam = device?.Parameters?.Variables?
                        .FirstOrDefault(p => p.Name == "LiquidName");
                    if (nameParam?.Value is string name && !string.IsNullOrEmpty(name))
                    {
                        flaskControl.LiquidName = name;
                    }
                };

                // Canvas 容器代替 Viewbox，允许标注溢出
                var container = new Canvas
                {
                    Width = targetWidth,
                    Height = targetHeight,
                    ClipToBounds = false,
                    IsHitTestVisible = false
                };

                Canvas.SetLeft(flaskControl, 0);
                Canvas.SetTop(flaskControl, 0);
                container.Children.Add(flaskControl);

                Debug.WriteLine($"创建锥形瓶图标容器（手动缩放 {scale:F2}x）: {targetWidth}x{targetHeight}");
                return container;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"创建锥形瓶图标失败: {ex.Message}");
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
                //  创建柱塞泵控件实例 - 允许交互
                var pumpControl = new PistonPumpControl
                {
                    IsHitTestVisible = true,  //  改为 true，允许按钮点击
                    Cursor = System.Windows.Input.Cursors.Hand,
                    IsRunning = false,
                    PumpSpeed = 20
                };
                SubscribePistonPumpEvents(pumpControl);
                //  包装在 Viewbox 中 - 也要允许交互
                var viewbox = new System.Windows.Controls.Viewbox
                {
                    Width = targetWidth,
                    Height = targetHeight,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    StretchDirection = System.Windows.Controls.StretchDirection.Both,
                    Child = pumpControl,
                    IsHitTestVisible = true  //  改为 true
                };

                Debug.WriteLine($"创建柱塞泵图标容器（画布可交互）: {targetWidth}x{targetHeight}");
                return viewbox;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($" 创建柱塞泵图标失败: {ex.Message}");
                return CreateDefaultIcon(targetWidth, targetHeight, "柱塞泵");
            }
        }

        #region 柱塞泵事件处理

        /// <summary>
        /// 订阅柱塞泵控件事件
        /// </summary>
        private void SubscribePistonPumpEvents(PistonPumpControl pump)
        {
            try
            {
                //  订阅状态切换请求事件
                pump.StateChangeRequested += async (s, e) =>
                {
                    await HandlePumpStateChangeRequestAsync(pump, e);
                };

                //  订阅速度变化请求事件
                pump.SpeedChangeRequested += async (s, e) =>
                {
                    await HandlePumpSpeedChangeRequestAsync(pump, e);
                };

                Debug.WriteLine($" 柱塞泵事件已订阅: {pump.PumpId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($" 订阅柱塞泵事件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理泵状态切换请求 (UI → 设备驱动)
        /// </summary>
        private async Task HandlePumpStateChangeRequestAsync(
            PistonPumpControl pump,
            PumpStateChangeRequestEventArgs e)
        {
            try
            {
                Debug.WriteLine($"========== 处理柱塞泵状态切换请求 ==========");
                Debug.WriteLine($"PumpId: {e.PumpId}");
                Debug.WriteLine($"当前状态: {e.CurrentState}");
                Debug.WriteLine($"请求状态: {e.RequestedState}");

                // 1. 查找对应的设备
                var canvasDevice = _viewModel?.CanvasDevices?.FirstOrDefault(d =>
                {
                    var deviceElement = FindDeviceElementInCanvas(d.DeviceId);
                    if (deviceElement == null) return false;

                    var pumpInDevice = FindVisualChild<PistonPumpControl>(deviceElement);
                    return pumpInDevice?.PumpId == e.PumpId;
                });

                if (canvasDevice?.Device == null)
                {
                    Debug.WriteLine($"未找到泵对应的设备");
                    pump.HideLoading();
                    return;
                }

                // 2. 检查设备是否支持UI交互
                if (canvasDevice.Device is not IDeviceUIInteraction uiInteraction)
                {
                    Debug.WriteLine($"设备 {canvasDevice.Device.Name} 不支持UI交互");
                    pump.HideLoading();
                    return;
                }

                // 3. 确定命令名称
                string commandName = e.RequestedState ? "启动泵" : "停止泵";
                Debug.WriteLine($"执行命令: {commandName}");

                // 4. 调用设备命令
                var result = await uiInteraction.ExecuteUICommandAsync(commandName);

                // 5. 隐藏加载动画
                pump.HideLoading();

                // 6. 处理执行结果
                if (result.Success)
                {
                    Debug.WriteLine($" 命令执行成功: {result.Message}");

                    //  更新UI状态 (设备驱动 → UI)
                    pump.IsRunning = e.RequestedState;

                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $" {commandName}成功");
                }
                else
                {
                    Debug.WriteLine($" 命令执行失败: {result.Message}");

                    pump.ShowOperationFailure();

                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $" {commandName}失败: {result.Message}");
                }

                Debug.WriteLine($"==========================================");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($" 处理泵状态切换请求失败: {ex.Message}");
                pump.HideLoading();
                pump.ShowOperationFailure();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $" 操作失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理泵速度变化请求 (UI → 设备驱动)
        /// </summary>
        private async Task HandlePumpSpeedChangeRequestAsync(
            PistonPumpControl pump,
            PumpSpeedChangeRequestEventArgs e)
        {
            try
            {
                Debug.WriteLine($"========== 处理柱塞泵速度变化请求 ==========");
                Debug.WriteLine($"PumpId: {e.PumpId}");
                Debug.WriteLine($"当前速度: {e.CurrentSpeed}");
                Debug.WriteLine($"请求速度: {e.RequestedSpeed}");
                Debug.WriteLine($"变化类型: {e.ChangeType}");

                // 1. 查找对应的设备
                var canvasDevice = _viewModel?.CanvasDevices?.FirstOrDefault(d =>
                {
                    var deviceElement = FindDeviceElementInCanvas(d.DeviceId);
                    if (deviceElement == null) return false;

                    var pumpInDevice = FindVisualChild<PistonPumpControl>(deviceElement);
                    return pumpInDevice?.PumpId == e.PumpId;
                });

                if (canvasDevice?.Device == null)
                {
                    Debug.WriteLine($"未找到泵对应的设备");
                    e.OnCompleted?.Invoke(false);
                    return;
                }

                // 2. 检查设备是否支持UI交互
                if (canvasDevice.Device is not IDeviceUIInteraction uiInteraction)
                {
                    Debug.WriteLine($"设备 {canvasDevice.Device.Name} 不支持UI交互");
                    e.OnCompleted?.Invoke(false);
                    return;
                }

                //  关键：确保参数类型一致
                var parameters = new Dictionary<string, object>
                {
                    ["TargetSpeed"] = (double)e.RequestedSpeed //  强制转换为 double
                };

                Debug.WriteLine($" 向设备发送参数: TargetSpeed={parameters["TargetSpeed"]}");

                // 4. 调用设备命令
                var result = await uiInteraction.ExecuteUICommandAsync("设置泵速", parameters);

                // 5. 处理执行结果
                if (result.Success)
                {
                    Debug.WriteLine($" 速度设置成功: {e.RequestedSpeed}");

                    //  更新UI状态 (设备驱动 → UI)
                    pump.PumpSpeed = e.RequestedSpeed;

                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $" 泵速度设置为 {e.RequestedSpeed}");
                }
                else
                {
                    Debug.WriteLine($" 速度设置失败: {result.Message}");
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $" 设置速度失败: {result.Message}");
                }

                //  调用回调
                e.OnCompleted?.Invoke(result.Success);

                Debug.WriteLine($"==========================================");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($" 处理泵速度变化请求失败: {ex.Message}");
                e.OnCompleted?.Invoke(false);

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $" 操作失败: {ex.Message}");
            }
        }


        #endregion
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

                //  1. 清空监控相关
                ClearAllMonitoringStates();

                //  2. 清空设备相关状态
                ClearDeviceStates();

                //  3. 清空管道
                ClearAllPipes();

                //  4. 强制清空画布视觉元素（先清视图）
                ClearCanvasVisualElements();

                //  5. 清空数据模型（后清数据）
                ClearViewModelData();

                //  6. 清空选择状态
                ClearDeviceSelection();
                ClearPipeSelection();

                //  7. 重置画布状态
                ResetCanvasState();

                Debug.WriteLine("========== 画布清空完成 ==========");
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("画布已清空");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($" 清空画布失败: {ex.Message}");
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
                               e is ZShapePipeControl||
                               e is TShapePipeControl)
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

                var elementsToRemove = new List<UIElement>();

                foreach (UIElement child in DesignCanvas.Children)
                {
                    if (child is Border border && !string.IsNullOrEmpty(border.Tag as string))
                    {
                        elementsToRemove.Add(child);
                    }
                    // ★ 确保所有四种管道类型都能被清理
                    else if (child is StraightPipeControl ||
                             child is ElbowPipeControl ||
                             child is ZShapePipeControl ||
                             child is TShapePipeControl)
                    {
                        elementsToRemove.Add(child);
                    }
                    else if (child is MonitoringCard)
                    {
                        elementsToRemove.Add(child);
                    }
                }

                Debug.WriteLine($"找到 {elementsToRemove.Count} 个元素需要清除");

                foreach (var element in elementsToRemove)
                {
                    DesignCanvas.Children.Remove(element);
                }

                DesignCanvas.Children.Clear();

                Debug.WriteLine($"清除后画布子元素数量: {DesignCanvas.Children.Count}");
                Debug.WriteLine("---------- 画布视觉元素清空完成 ----------");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($" 清空画布视觉元素失败: {ex.Message}");
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

                //  获取控件旋转角度
                var rotationAngle = GetDeviceRotationAngle(canvasDevice.DeviceId);

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
                    ScaleY = scaleY,
                    RotationAngle = rotationAngle //  保存旋转角度
                };

                // 收集设备参数
                CollectDeviceParameters(canvasDevice, deviceData);

                canvasData.Devices.Add(deviceData);
                Debug.WriteLine($"保存设备: {deviceData.DeviceName}, 缩放: {scaleX:F2}x{scaleY:F2}, 旋转: {rotationAngle:F1}°");
            }
        }

        /// <summary>
        ///  新增：获取设备内控件的旋转角度
        /// </summary>
        private double GetDeviceRotationAngle(string deviceId)
        {
            try
            {
                var deviceElement = FindDeviceElementInCanvas(deviceId);
                if (deviceElement == null)
                {
                    Debug.WriteLine($"未找到设备元素: {deviceId}");
                    return 0.0;
                }

                // 查找设备内的各种自定义控件
                var valve = FindVisualChild<ValveControl>(deviceElement);
                if (valve != null)
                {
                    Debug.WriteLine($"阀门旋转角度: {valve.RotationAngle:F1}°");
                    return valve.RotationAngle;
                }

                var solenoidValve = FindVisualChild<SolenoidValveControl>(deviceElement);
                if (solenoidValve != null)
                {
                    Debug.WriteLine($"电磁阀旋转角度: {solenoidValve.RotationAngle:F1}°");
                    return solenoidValve.RotationAngle;
                }

                var flowMeter = FindVisualChild<MassFlowMeterControl>(deviceElement);
                if (flowMeter != null)
                {
                    Debug.WriteLine($"流量计旋转角度: {flowMeter.RotationAngle:F1}°");
                    return flowMeter.RotationAngle;
                }

                var heatExchanger = FindVisualChild<HeatExchangerControl>(deviceElement);
                if (heatExchanger != null)
                {
                    Debug.WriteLine($"换热器旋转角度: {heatExchanger.RotationAngle:F1}°");
                    return heatExchanger.RotationAngle;
                }
                var tubularReactor = FindVisualChild<TubularReactorControl>(deviceElement);
                if (tubularReactor != null)
                {
                    Debug.WriteLine($"管式反应器旋转角度: {tubularReactor.RotationAngle:F1}°");
                    return tubularReactor.RotationAngle;
                }

                // 其他控件...

                return 0.0;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取设备旋转角度失败: {ex.Message}");
                return 0.0;
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

                //  使用异步分批加载
                LoadCanvasContentAsync(canvasDocument);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载画布失败: {ex.Message}");
            }
        }

        /// <summary>
        ///  新增：异步分批加载画布内容
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
        ///  新增：分批恢复设备
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

                //  更新进度
                var progress = (i + 1) * 100 / totalBatches;
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"正在加载设备... {progress}%");

                //  每批之间让出UI线程
                if (i < totalBatches - 1)
                {
                    await System.Threading.Tasks.Task.Delay(1);
                }

            }

            Debug.WriteLine($"设备恢复完成，当前设备数量: {_viewModel?.CanvasDevices?.Count ?? 0}");
            //  新增：批量恢复完成，立即重建四叉树
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
        ///  新增：分批恢复管道
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

                //  更新进度
                var progress = (i + 1) * 100 / totalBatches;
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"正在加载管道... {progress}%");

                //  每批之间让出UI线程
                if (i < totalBatches - 1)
                {
                    await System.Threading.Tasks.Task.Delay(1);
                }
            }

            Debug.WriteLine($"管道恢复完成: 成功 {successCount}/{pipesData.Count}");

            //  新增：批量恢复完成，立即重建四叉树
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
                    case "TShape":
                        pipeControl = CreateRestoredTShapePipe(pipeData);
                        break;
                    default:
                        return false;
                }

                if (pipeControl != null)
                {
                    // 设置位置和层级
                    Canvas.SetLeft(pipeControl, pipeData.X);
                    Canvas.SetTop(pipeControl, pipeData.Y);
                    //Panel.SetZIndex(pipeControl, pipeData.ZIndex);
                    // 【修改】强制设置 ZIndex 为 10，忽略保存文件中的值(pipeData.ZIndex)
                    // 这样可以保证即便旧文件数据混乱，加载后层级也是正确的
                    Panel.SetZIndex(pipeControl, 10);

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
                Debug.WriteLine($" 恢复管道失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        ///  新增:清空已存在的相同管道ID,避免重复创建
        /// </summary>
        private void ClearExistingPipesBeforeRestore(List<CanvasPipeData> pipesData)
        {
            var pipeIdsToRestore = pipesData.Select(p => p.PipeId).ToHashSet();

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
                    // ★ 新增
                    if (e is TShapePipeControl tp && pipeIdsToRestore.Contains(tp.PipeId))
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
                else if (existingPipe is TShapePipeControl tp) pipeId = tp.PipeId;  // ★ 新增

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
                // 重建设备实例
                var device = _canvasDataService.RestoreDeviceFromData(deviceData);
                if (device == null)
                {
                    Debug.WriteLine($"无法恢复设备: {deviceData.DeviceName}");
                    return;
                }

                var independentDevice = ReflectionDeviceDeepCopyService.DeepClone(device);
                try
                {
                    // 尝试通过反射设置 DeviceId 属性
                    var deviceIdProperty = independentDevice.GetType().GetProperty("DeviceId");
                    if (deviceIdProperty != null && deviceIdProperty.CanWrite)
                    {
                        deviceIdProperty.SetValue(independentDevice, deviceData.DeviceId);
                        Debug.WriteLine($" 已恢复内部设备ID: {deviceData.DeviceId}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($" 注入 DeviceId 失败: {ex.Message}");
                }
                // 画布设备实例允许驱动级自动采集(插件目录/探测用实例不允许)
                if (independentDevice is DevicePlugins.Devices.Device restoredBaseDevice)
                {
                    restoredBaseDevice.AutoCollectAllowed = true;
                }
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

                //  直接添加到ViewModel
                _viewModel?.CanvasDevices.Add(canvasDeviceViewModel);

                //  延迟恢复缩放
                if (deviceData.ScaleX != 1.0 || deviceData.ScaleY != 1.0)
                {
                    ScheduleDeviceScaleRestoration(deviceData.DeviceId, deviceData.ScaleX, deviceData.ScaleY);
                }

                //  新增：延迟恢复旋转角度
                if (Math.Abs(deviceData.RotationAngle) > 0.01)
                {
                    ScheduleDeviceRotationRestoration(deviceData.DeviceId, deviceData.RotationAngle);
                }

                ScheduleMonitoringCardRestoration(deviceData.DeviceId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($" 恢复设备失败: {ex.Message}");
            }
        }
        /// <summary>
        ///  新增：安排设备旋转角度恢复
        /// </summary>
        private void ScheduleDeviceRotationRestoration(string deviceId, double rotationAngle)
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                RestoreDeviceRotation(deviceId, rotationAngle);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>
        ///  新增：恢复设备旋转角度
        /// </summary>
        private void RestoreDeviceRotation(string deviceId, double rotationAngle)
        {
            try
            {
                var deviceElement = FindDeviceElementInCanvas(deviceId);
                if (deviceElement == null)
                {
                    Debug.WriteLine($"恢复旋转: 未找到设备元素 {deviceId}");
                    return;
                }

                Debug.WriteLine($"恢复设备 {deviceId} 旋转角度: {rotationAngle:F1}°");

                // 查找并设置各种控件的旋转角度
                bool restored = false;

                var valve = FindVisualChild<ValveControl>(deviceElement);
                if (valve != null)
                {
                    valve.RotationAngle = rotationAngle;
                    restored = true;
                    Debug.WriteLine($" 阀门旋转角度已恢复: {rotationAngle:F1}°");
                }

                var solenoidValve = FindVisualChild<SolenoidValveControl>(deviceElement);
                if (solenoidValve != null)
                {
                    solenoidValve.RotationAngle = rotationAngle;
                    restored = true;
                    Debug.WriteLine($" 电磁阀旋转角度已恢复: {rotationAngle:F1}°");
                }

                var flowMeter = FindVisualChild<MassFlowMeterControl>(deviceElement);
                if (flowMeter != null)
                {
                    flowMeter.RotationAngle = rotationAngle;
                    restored = true;
                    Debug.WriteLine($" 流量计旋转角度已恢复: {rotationAngle:F1}°");
                }

                var heatExchanger = FindVisualChild<HeatExchangerControl>(deviceElement);
                if (heatExchanger != null)
                {
                    heatExchanger.RotationAngle = rotationAngle;
                    restored = true;
                    Debug.WriteLine($" 换热器旋转角度已恢复: {rotationAngle:F1}°");
                }
                var tubularReactor = FindVisualChild<TubularReactorControl>(deviceElement);
                if (tubularReactor != null)
                {
                    tubularReactor.RotationAngle = rotationAngle;
                    restored = true;
                    Debug.WriteLine($"管式反应器旋转角度已恢复: {rotationAngle:F1}°");
                }
                // 其他控件...

                if (!restored)
                {
                    Debug.WriteLine($"设备 {deviceId} 中未找到可旋转的控件");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"恢复设备旋转角度失败: {ex.Message}");
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

                //  新增：调度延迟布局更新
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
                Debug.WriteLine($"最终管道数量: {_pipes.Count}"); //  新增

                // 强制更新布局
                DesignCanvas.UpdateLayout();

                //  新增：最终确认管道状态
                ScheduleFinalPipeValidation();

                Debug.WriteLine("画布加载处理完成");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"画布加载完成处理失败: {ex.Message}");
            }
        }
        /// <summary>
        ///  新增：安排最终管道验证
        /// </summary>
        private void ScheduleFinalPipeValidation()
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                ValidatePipesState();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        ///  新增：验证管道状态
        /// </summary>
        private void ValidatePipesState()
        {
            try
            {
                Debug.WriteLine("========== 最终验证管道状态 ==========");

                // ★ 确保所有四种管道类型都参与统计
                var pipesInCanvas = DesignCanvas.Children
                    .OfType<FrameworkElement>()
                    .Count(e => e is StraightPipeControl ||
                                e is ElbowPipeControl ||
                                e is ZShapePipeControl ||
                                e is TShapePipeControl);

                Debug.WriteLine($"画布上管道数量: {pipesInCanvas}");
                Debug.WriteLine($"管道字典数量: {_pipes.Count}");

                if (pipesInCanvas != _pipes.Count)
                {
                    Debug.WriteLine($"警告：管道数量不匹配");
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

                //  记录原始位置用于撤销
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
            //  节流检查：避免过于频繁的吸附检测
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

                //  使用 QuadTree 优化的吸附检测
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

                //  如果有吸附方案，应用吸附
                if (_currentSnapSolution != null && _currentSnapSolution.HasSnap)
                {
                    var newCenter = _currentSnapSolution.NewCenterPosition;
                    var newLeft = newCenter.X - pipe.ActualWidth / 2;
                    var newTop = newCenter.Y - pipe.ActualHeight / 2;

                    var newPosition = new Point(newLeft, newTop);

                    //  如果位置发生了变化，创建移动命令
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
                    //  没有吸附，但位置可能已经改变，创建移动命令
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
                //  新增：标记四叉树需要重建
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
            //  节流检查
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

                    Debug.WriteLine($" 弯管道已吸附到位置: 中心({newCenter.X:F0}, {newCenter.Y:F0})");

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
                //  新增：弯管道拖拽完成，标记四叉树需要重建
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
            //  节流检查
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
                //  新增：Z形管道拖拽完成，标记四叉树需要重建
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
        #region T型管道事件处理

        /// <summary>
        /// T型管道拖动开始
        /// </summary>
        private void OnTShapePipeDragStarted(object sender, TPipeDragEventArgs e)
        {
            try
            {
                Debug.WriteLine($"T型管道拖动开始: {e.PipeId}");
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("正在移动T型管道...");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"T型管道拖动开始处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// T型管道拖动中（已优化节流）
        /// </summary>
        private void OnTShapePipeDragging(object sender, TPipeDragEventArgs e)
        {
            //  节流检查
            var now = DateTime.Now;
            if ((now - _lastMouseMoveTime).TotalMilliseconds < 16)
            {
                return;
            }
            _lastMouseMoveTime = now;

            try
            {
                var pipe = sender as TShapePipeControl;
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
                Debug.WriteLine($"T型管道拖动处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// T型管道拖动完成
        /// </summary>
        private void OnTShapePipeDragCompleted(object sender, TPipeDragEventArgs e)
        {
            try
            {
                var pipe = sender as TShapePipeControl;
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
                        $"T型管道已吸附到: {snapTypes}");
                }

                _pipeSnapService.HideSnapIndicator();
                _isPipeSnapping = false;
                _currentSnapSolution = null;

                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("T型管道移动完成");

                //  标记四叉树需要重建
                if (_pipeSnapService is PipeSnapService snapService)
                {
                    snapService.MarkQuadTreeDirty();
                    Debug.WriteLine("T型管道拖拽完成，已标记四叉树需要重建");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"T型管道拖动完成处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// T型管道调整大小开始
        /// </summary>
        private void OnTShapePipeResizeStarted(object sender, TPipeResizeEventArgs e)
        {
            try
            {
                Debug.WriteLine($"T型管道调整大小开始: {e.PipeId}, 锚点: {e.AnchorPosition}");
                Debug.WriteLine($"原始尺寸: 主管={e.OriginalSize.MainPipeLength:F0}, " +
                               $"支管={e.OriginalSize.BranchPipeLength:F0}, " +
                               $"支管位置={e.OriginalSize.BranchPosition:F0}, " +
                               $"宽度={e.OriginalSize.PipeWidth:F0}");

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"调整T型管道大小 ({e.AnchorPosition})...");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"T型管道调整大小开始处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// T型管道调整大小中
        /// </summary>
        private void OnTShapePipeResizing(object sender, TPipeResizeEventArgs e)
        {
            try
            {
                // 实时更新状态栏
                string sizeInfo = e.AnchorPosition switch
                {
                    TAnchorPosition.MainLength => $"主管长度: {e.CurrentSize.MainPipeLength:F0}",
                    TAnchorPosition.BranchLength => $"支管长度: {e.CurrentSize.BranchPipeLength:F0}",
                    TAnchorPosition.BranchPosition => $"支管位置: {e.CurrentSize.BranchPosition:F0}",
                    TAnchorPosition.PipeWidth => $"管道宽度: {e.CurrentSize.PipeWidth:F0}",
                    _ => $"主管={e.CurrentSize.MainPipeLength:F0}, 支管={e.CurrentSize.BranchPipeLength:F0}"
                };

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(sizeInfo);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"T型管道调整大小处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// T型管道调整大小完成
        /// </summary>
        private void OnTShapePipeResizeCompleted(object sender, TPipeResizeEventArgs e)
        {
            try
            {
                Debug.WriteLine($"T型管道调整大小完成: {e.PipeId}");
                Debug.WriteLine($"最终尺寸: 主管={e.CurrentSize.MainPipeLength:F0}, " +
                               $"支管={e.CurrentSize.BranchPipeLength:F0}, " +
                               $"支管位置={e.CurrentSize.BranchPosition:F0}, " +
                               $"宽度={e.CurrentSize.PipeWidth:F0}");

                // 标记画布已修改
                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"T型管道已调整: 主管={e.CurrentSize.MainPipeLength:F0}, " +
                    $"支管={e.CurrentSize.BranchPipeLength:F0}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"T型管道调整大小完成处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// T型管道旋转开始
        /// </summary>
        private void OnTShapePipeRotateStarted(object sender, TPipeRotateEventArgs e)
        {
            try
            {
                Debug.WriteLine($"T型管道旋转开始: {e.PipeId}");
                Debug.WriteLine($"原始角度: {e.OriginalAngle:F1}°");

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    "正在旋转T型管道... (按住 Shift 键可15度步进)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"T型管道旋转开始处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// T型管道旋转中
        /// </summary>
        private void OnTShapePipeRotating(object sender, TPipeRotateEventArgs e)
        {
            try
            {
                // 实时更新状态栏显示当前角度
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"旋转角度: {e.CurrentAngle:F1}°");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"T型管道旋转处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// T型管道旋转完成
        /// </summary>
        private void OnTShapePipeRotateCompleted(object sender, TPipeRotateEventArgs e)
        {
            try
            {
                Debug.WriteLine($"T型管道旋转完成: {e.PipeId}");
                Debug.WriteLine($"原始角度: {e.OriginalAngle:F1}°");
                Debug.WriteLine($"最终角度: {e.CurrentAngle:F1}°");

                // 标记画布已修改
                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"T型管道已旋转至 {e.CurrentAngle:F1}°");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"T型管道旋转完成处理失败: {ex.Message}");
            }
        }

        #endregion
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
                            PipeLength = straightPipe.PipeLength,

                            // ★ 新增：保存管道外观属性
                            PipeColorHex = ColorToHex(straightPipe.PipeColor),
                            FlangeColorHex = ColorToHex(straightPipe.FlangeColor),
                            FluidColorHex = ColorToHex(straightPipe.FluidColor),
                            FluidOpacity = straightPipe.FluidOpacity,
                            IsFluidVisible = straightPipe.IsFluidVisible,
                            IsFlowing = straightPipe.IsFlowing,
                            FlowDirectionStr = straightPipe.PipeFlowDir.ToString(),
                            FlowSpeed = straightPipe.FlowSpeed
                        };

                        Debug.WriteLine($"收集直管道: {straightPipe.PipeId}, 流体可见={straightPipe.IsFluidVisible}");
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
                            VerticalLength = elbowPipe.VerticalLength,

                            // ★ 新增：保存管道外观属性
                            PipeColorHex = ColorToHex(elbowPipe.PipeColor),
                            FlangeColorHex = ColorToHex(elbowPipe.FlangeColor),
                            FluidColorHex = ColorToHex(elbowPipe.FluidColor),
                            FluidOpacity = elbowPipe.FluidOpacity,
                            IsFluidVisible = elbowPipe.IsFluidVisible,
                            IsFlowing = elbowPipe.IsFlowing,
                            FlowDirectionStr = elbowPipe.PipeFlowDir.ToString(),
                            FlowSpeed = elbowPipe.FlowSpeed
                        };

                        Debug.WriteLine($"收集弯管道: {elbowPipe.PipeId}, 流体可见={elbowPipe.IsFluidVisible}");
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
                            VerticalOffset = zPipe.VerticalOffset,

                            // ★ 新增：保存管道外观属性
                            PipeColorHex = ColorToHex(zPipe.PipeColor),
                            FlangeColorHex = ColorToHex(zPipe.FlangeColor),
                            FluidColorHex = ColorToHex(zPipe.FluidColor),
                            FluidOpacity = zPipe.FluidOpacity,
                            IsFluidVisible = zPipe.IsFluidVisible,
                            IsFlowing = zPipe.IsFlowing,
                            FlowDirectionStr = zPipe.PipeFlowDir.ToString(),
                            FlowSpeed = zPipe.FlowSpeed
                        };

                        Debug.WriteLine($"收集Z形管道: {zPipe.PipeId}, 流体可见={zPipe.IsFluidVisible}");
                    }

                    // 处理T形管道
                    else if (child is TShapePipeControl tPipe)
                    {
                        var left = Canvas.GetLeft(tPipe);
                        var top = Canvas.GetTop(tPipe);

                        pipeData = new CanvasPipeData
                        {
                            PipeId = tPipe.PipeId,
                            PipeType = "TShape",
                            X = double.IsNaN(left) ? 0 : left,
                            Y = double.IsNaN(top) ? 0 : top,
                            ZIndex = Panel.GetZIndex(tPipe),
                            PipeWidth = tPipe.PipeWidth,
                            RotationAngle = tPipe.RotationAngle,
                            MainPipeLength = tPipe.MainPipeLength,
                            BranchPipeLength = tPipe.BranchPipeLength,
                            BranchPosition = tPipe.BranchPosition,

                            // ★ 新增：保存管道外观属性
                            PipeColorHex = ColorToHex(tPipe.PipeColor),
                            FlangeColorHex = ColorToHex(tPipe.FlangeColor),
                            FluidColorHex = ColorToHex(tPipe.FluidColor),
                            FluidOpacity = tPipe.FluidOpacity,
                            IsFluidVisible = tPipe.IsFluidVisible,
                            IsFlowing = tPipe.IsFlowing,
                            FlowDirectionStr = tPipe.PipeFlowDir.ToString(),
                            FlowSpeed = tPipe.FlowSpeed
                        };

                        Debug.WriteLine($"收集T形管道: {tPipe.PipeId}, 流体可见={tPipe.IsFluidVisible}");
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
                Debug.WriteLine($" 收集管道数据失败: {ex.Message}");
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

            // ★ 新增：恢复管道外观属性
            if (!string.IsNullOrEmpty(pipeData.PipeColorHex))
                pipe.PipeColor = HexToColor(pipeData.PipeColorHex);

            if (!string.IsNullOrEmpty(pipeData.FlangeColorHex))
                pipe.FlangeColor = HexToColor(pipeData.FlangeColorHex);

            if (!string.IsNullOrEmpty(pipeData.FluidColorHex))
                pipe.FluidColor = HexToColor(pipeData.FluidColorHex);

            pipe.FluidOpacity = pipeData.FluidOpacity > 0 ? pipeData.FluidOpacity : 0.5;
            pipe.IsFluidVisible = pipeData.IsFluidVisible;
            pipe.IsFlowing = pipeData.IsFlowing;
            pipe.FlowSpeed = pipeData.FlowSpeed > 0 ? pipeData.FlowSpeed : 1.0;

            if (!string.IsNullOrEmpty(pipeData.FlowDirectionStr) &&
                Enum.TryParse<PipeFlowDirection>(pipeData.FlowDirectionStr, out var dir))
            {
                pipe.PipeFlowDir = dir;
            }

            // 订阅事件
            SubscribeStraightPipeEvents(pipe);

            return pipe;
        }
        // ═══ 6. 添加颜色转换辅助方法 ═══
        private static string ColorToHex(Color c)
        {
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }

        private static Color HexToColor(string hex)
        {
            try
            {
                hex = hex.TrimStart('#');
                if (hex.Length == 6)
                {
                    return Color.FromRgb(
                        Convert.ToByte(hex.Substring(0, 2), 16),
                        Convert.ToByte(hex.Substring(2, 2), 16),
                        Convert.ToByte(hex.Substring(4, 2), 16));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"颜色解析失败: {hex}, {ex.Message}");
            }
            return Color.FromRgb(0x72, 0x71, 0x71);
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

            // ★ 新增：恢复管道外观属性
            if (!string.IsNullOrEmpty(pipeData.PipeColorHex))
                pipe.PipeColor = HexToColor(pipeData.PipeColorHex);

            if (!string.IsNullOrEmpty(pipeData.FlangeColorHex))
                pipe.FlangeColor = HexToColor(pipeData.FlangeColorHex);

            if (!string.IsNullOrEmpty(pipeData.FluidColorHex))
                pipe.FluidColor = HexToColor(pipeData.FluidColorHex);

            pipe.FluidOpacity = pipeData.FluidOpacity > 0 ? pipeData.FluidOpacity : 0.5;
            pipe.IsFluidVisible = pipeData.IsFluidVisible;
            pipe.IsFlowing = pipeData.IsFlowing;
            pipe.FlowSpeed = pipeData.FlowSpeed > 0 ? pipeData.FlowSpeed : 1.0;

            if (!string.IsNullOrEmpty(pipeData.FlowDirectionStr) &&
                Enum.TryParse<PipeFlowDirection>(pipeData.FlowDirectionStr, out var dir))
            {
                pipe.PipeFlowDir = dir;
            }

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

            // ★ 新增：恢复管道外观属性
            if (!string.IsNullOrEmpty(pipeData.PipeColorHex))
                pipe.PipeColor = HexToColor(pipeData.PipeColorHex);

            if (!string.IsNullOrEmpty(pipeData.FlangeColorHex))
                pipe.FlangeColor = HexToColor(pipeData.FlangeColorHex);

            if (!string.IsNullOrEmpty(pipeData.FluidColorHex))
                pipe.FluidColor = HexToColor(pipeData.FluidColorHex);

            pipe.FluidOpacity = pipeData.FluidOpacity > 0 ? pipeData.FluidOpacity : 0.5;
            pipe.IsFluidVisible = pipeData.IsFluidVisible;
            pipe.IsFlowing = pipeData.IsFlowing;
            pipe.FlowSpeed = pipeData.FlowSpeed > 0 ? pipeData.FlowSpeed : 1.0;

            if (!string.IsNullOrEmpty(pipeData.FlowDirectionStr) &&
                Enum.TryParse<PipeFlowDirection>(pipeData.FlowDirectionStr, out var dir))
            {
                pipe.PipeFlowDir = dir;
            }

            SubscribeZShapePipeEvents(pipe);
            return pipe;
        }


        /// <summary>
        /// 创建恢复的T形管道
        /// </summary>
        private TShapePipeControl CreateRestoredTShapePipe(CanvasPipeData pipeData)
        {
            var pipe = new TShapePipeControl
            {
                PipeId = pipeData.PipeId,
                MainPipeLength = pipeData.MainPipeLength ?? 120,
                BranchPipeLength = pipeData.BranchPipeLength ?? 54,
                BranchPosition = pipeData.BranchPosition ?? 60,
                PipeWidth = pipeData.PipeWidth,
                RotationAngle = pipeData.RotationAngle,
                IsSelected = false
            };

            // ★ 新增：恢复管道外观属性
            if (!string.IsNullOrEmpty(pipeData.PipeColorHex))
                pipe.PipeColor = HexToColor(pipeData.PipeColorHex);

            if (!string.IsNullOrEmpty(pipeData.FlangeColorHex))
                pipe.FlangeColor = HexToColor(pipeData.FlangeColorHex);

            if (!string.IsNullOrEmpty(pipeData.FluidColorHex))
                pipe.FluidColor = HexToColor(pipeData.FluidColorHex);

            pipe.FluidOpacity = pipeData.FluidOpacity > 0 ? pipeData.FluidOpacity : 0.5;
            pipe.IsFluidVisible = pipeData.IsFluidVisible;
            pipe.IsFlowing = pipeData.IsFlowing;
            pipe.FlowSpeed = pipeData.FlowSpeed > 0 ? pipeData.FlowSpeed : 1.0;

            if (!string.IsNullOrEmpty(pipeData.FlowDirectionStr) &&
                Enum.TryParse<PipeFlowDirection>(pipeData.FlowDirectionStr, out var dir))
            {
                pipe.PipeFlowDir = dir;
            }

            SubscribeTShapePipeEvents(pipe);
            return pipe;
        }
        #endregion



        #region 监控卡片位置管理

        /// <summary>
        /// 监控卡片位置字典 - DeviceId -> Position
        /// </summary>
        private readonly Dictionary<string, Point> _monitoringCardPositions = new();
        // 监控卡片连接虚线字典 - DeviceId -> Line
        private readonly Dictionary<string, Polyline> _monitoringCardLines = new();
        // ★ 新增:箭头字典
        private readonly Dictionary<string, Polygon> _monitoringCardArrows = new();
        // 监控卡片位置变更处理器（用于取消订阅，避免内存泄漏）
        private readonly Dictionary<string, EventHandler> _monitoringCardLeftHandlers = new();
        private readonly Dictionary<string, EventHandler> _monitoringCardTopHandlers = new();
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

        /// <summary>
        /// 更新设备与监控卡片之间的直角折线连接（自动创建或更新）
        /// </summary>
        private void UpdateMonitoringConnectionLine(string deviceId)
        {
            try
            {
                var deviceElement = FindDeviceElementInCanvas(deviceId);
                if (deviceElement == null) return;

                if (!_monitoringCards.TryGetValue(deviceId, out var card)) return;

                // ── 设备包围盒 ──────────────────────────────────────────
                var deviceLeft = Canvas.GetLeft(deviceElement);
                var deviceTop = Canvas.GetTop(deviceElement);
                if (double.IsNaN(deviceLeft)) deviceLeft = 0;
                if (double.IsNaN(deviceTop)) deviceTop = 0;

                var dw = deviceElement.ActualWidth > 0 ? deviceElement.ActualWidth : 80;
                var dh = deviceElement.ActualHeight > 0 ? deviceElement.ActualHeight : 60;

                // ── 卡片包围盒 ──────────────────────────────────────────
                var cardLeft = Canvas.GetLeft(card);
                var cardTop = Canvas.GetTop(card);
                if (double.IsNaN(cardLeft)) cardLeft = 0;
                if (double.IsNaN(cardTop)) cardTop = 0;

                var cw = card.ActualWidth > 0 ? card.ActualWidth : 200;
                var ch = card.ActualHeight > 0 ? card.ActualHeight : 60;

                // ── 决定走线模式 + 端点 + 箭头方向 ─────────────────────
                PointCollection points;
                Point arrowTip;        // 箭头尖端坐标
                double arrowAngle;     // 箭头朝向(度)0=右,90=下,180=左,270=上

                bool cardIsRight = cardLeft >= deviceLeft + dw - 5;
                bool cardIsLeft = cardLeft + cw <= deviceLeft + 5;
                bool cardIsBottom = !cardIsRight && !cardIsLeft && cardTop >= deviceTop + dh - 5;

                if (cardIsRight)
                {
                    // 设备 → 右 → 卡片
                    double x1 = deviceLeft + dw;
                    double y1 = deviceTop + dh / 2;
                    double x2 = cardLeft;
                    double y2 = cardTop + ch / 2;
                    double midX = x1 + (x2 - x1) / 2.0;

                    points = new PointCollection
            {
                new Point(x1, y1),
                new Point(midX, y1),
                new Point(midX, y2),
                new Point(x2, y2)
            };
                    arrowTip = new Point(x2, y2);
                    arrowAngle = 0;       // 朝右
                }
                else if (cardIsLeft)
                {
                    // 设备 → 左 → 卡片
                    double x1 = deviceLeft;
                    double y1 = deviceTop + dh / 2;
                    double x2 = cardLeft + cw;
                    double y2 = cardTop + ch / 2;
                    double midX = x2 + (x1 - x2) / 2.0;

                    points = new PointCollection
            {
                new Point(x1, y1),
                new Point(midX, y1),
                new Point(midX, y2),
                new Point(x2, y2)
            };
                    arrowTip = new Point(x2, y2);
                    arrowAngle = 180;     // 朝左
                }
                else if (cardIsBottom)
                {
                    // 设备 → 下 → 卡片
                    double x1 = deviceLeft + dw / 2;
                    double y1 = deviceTop + dh;
                    double x2 = cardLeft + cw / 2;
                    double y2 = cardTop;
                    double midY = y1 + (y2 - y1) / 2.0;

                    points = new PointCollection
            {
                new Point(x1, y1),
                new Point(x1, midY),
                new Point(x2, midY),
                new Point(x2, y2)
            };
                    arrowTip = new Point(x2, y2);
                    arrowAngle = 90;      // 朝下
                }
                else
                {
                    // 设备 → 上 → 卡片
                    double x1 = deviceLeft + dw / 2;
                    double y1 = deviceTop;
                    double x2 = cardLeft + cw / 2;
                    double y2 = cardTop + ch;
                    double midY = y2 + (y1 - y2) / 2.0;

                    points = new PointCollection
            {
                new Point(x1, y1),
                new Point(x1, midY),
                new Point(x2, midY),
                new Point(x2, y2)
            };
                    arrowTip = new Point(x2, y2);
                    arrowAngle = 270;     // 朝上
                }

                // ★ 主题深红 #A41626
                var themeColor = Color.FromRgb(0xA4, 0x16, 0x26);

                // ── 创建或更新连接线 ─────────────────────────────────
                if (_monitoringCardLines.TryGetValue(deviceId, out var existingPolyline))
                {
                    existingPolyline.Points = points;
                }
                else
                {
                    var polyline = new Polyline
                    {
                        Points = points,
                        Stroke = new SolidColorBrush(themeColor),    // 深红实线
                        StrokeThickness = 1.2,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        Fill = Brushes.Transparent,
                        IsHitTestVisible = false,
                        Opacity = 0.9
                    };
                    Panel.SetZIndex(polyline, 50);
                    DesignCanvas.Children.Add(polyline);
                    _monitoringCardLines[deviceId] = polyline;
                }

                // ── 创建或更新箭头 ───────────────────────────────────
                // 箭头形状(默认朝右,尖端在原点):
                //   尖端 (0,0)
                //   左上 (-7, -4)
                //   左下 (-7, 4)
                var arrowFill = new SolidColorBrush(themeColor);

                if (_monitoringCardArrows.TryGetValue(deviceId, out var existingArrow))
                {
                    // 更新位置和旋转
                    Canvas.SetLeft(existingArrow, arrowTip.X);
                    Canvas.SetTop(existingArrow, arrowTip.Y);
                    ((RotateTransform)existingArrow.RenderTransform).Angle = arrowAngle;
                }
                else
                {
                    var arrow = new Polygon
                    {
                        Points = new PointCollection
                {
                    new Point(0, 0),
                    new Point(-7, -4),
                    new Point(-7, 4)
                },
                        Fill = arrowFill,
                        IsHitTestVisible = false,
                        Opacity = 0.9,
                        RenderTransform = new RotateTransform(arrowAngle)
                    };
                    Canvas.SetLeft(arrow, arrowTip.X);
                    Canvas.SetTop(arrow, arrowTip.Y);
                    Panel.SetZIndex(arrow, 51);
                    DesignCanvas.Children.Add(arrow);
                    _monitoringCardArrows[deviceId] = arrow;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新监控连接线失败: {ex.Message}");
            }
        }
        /// <summary>
        /// 移除设备与监控卡片之间的虚线连接
        /// </summary>
        private void RemoveMonitoringConnectionLine(string deviceId)
        {
            if (_monitoringCardLines.TryGetValue(deviceId, out var line))
            {
                DesignCanvas.Children.Remove(line);
                _monitoringCardLines.Remove(deviceId);
            }

            // ★ 同时移除箭头
            if (_monitoringCardArrows.TryGetValue(deviceId, out var arrow))
            {
                DesignCanvas.Children.Remove(arrow);
                _monitoringCardArrows.Remove(deviceId);
            }

            Debug.WriteLine($"移除监控连接线和箭头: {deviceId}");
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
            // 【修改】设置管道 ZIndex 为 10
            Panel.SetZIndex(pipe, 10);

            //  启用管道级别缓存
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
            //  订阅管道选中事件（新增）
            pipe.PipeSelected += OnPipeSelectedFromControl;
            pipe.PipeDeselected += OnPipeDeselectedFromControl;
            // ★ 新增：双击打开属性对话框
            pipe.PipeDoubleClicked += OnPipeDoubleClicked;
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
                    Debug.WriteLine("警告：发送者不是 FrameworkElement");
                    return;
                }

                //  清除其他选择
                ClearDeviceSelection();
                ClearAllPipeSelectionsExcept(pipeId);

                //  记录选中状态
                _selectedPipeId = pipeId;
                _selectedPipeElement = pipeElement;

                Debug.WriteLine($" 画布已确认管道选中: {pipeId}");

                // 更新状态栏
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"已选中管道: {pipeId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($" 处理管道选中通知失败: {ex.Message}");
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
        // ═══ 3. 添加双击处理方法 ═══
        /// <summary>
        /// 管道双击事件处理 — 打开管道属性对话框（统一所有管道类型）
        /// </summary>
        private void OnPipeDoubleClicked(object sender, string pipeId)
        {
            try
            {
                Debug.WriteLine($"管道双击: {pipeId}, 类型: {sender?.GetType().Name}");

                // ★ 统一通过 IPipeProperties 接口处理所有管道类型
                if (sender is IPipeProperties pipeWithProperties)
                {
                    ShowPipePropertyDialog(pipeWithProperties);
                }
                else
                {
                    Debug.WriteLine($"管道类型 {sender?.GetType().Name} 未实现 IPipeProperties，无法打开属性对话框");
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("此管道类型暂不支持属性编辑");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"管道双击处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 显示管道属性对话框
        /// </summary>
        /// <summary>
        /// 显示管道属性对话框（统一接口版，支持所有管道类型）
        /// </summary>
        private void ShowPipePropertyDialog(IPipeProperties pipe)
        {
            try
            {
                // 获取管道ID（统一提取）
                string pipeId = pipe switch
                {
                    StraightPipeControl sp => sp.PipeId,
                    ElbowPipeControl ep => ep.PipeId,
                    ZShapePipeControl zp => zp.PipeId,
                    TShapePipeControl tp => tp.PipeId,
                    _ => "unknown"
                };

                Debug.WriteLine($"打开管道属性对话框: {pipeId}, 类型: {pipe.GetType().Name}");

                // 关闭之前的弹窗
                if (_pipePropertyWindow != null)
                {
                    _pipePropertyWindow.Close();
                    _pipePropertyWindow = null;
                }

                // 清理拖拽状态，防止对话框关闭后误触发拖拽
                CleanupDragState();

                // 创建对话框
                var dialog = new PipePropertyDialog();
                dialog.SetTargetPipe(pipe);  // ★ PipePropertyDialog.SetTargetPipe 需改为接受 IPipeProperties

                // 创建窗口
                _pipePropertyWindow = new Window
                {
                    Title = "管道属性",
                    Content = dialog,
                    Width = 540,
                    Height = 600,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = Window.GetWindow(this),
                    ResizeMode = ResizeMode.NoResize,
                    WindowStyle = WindowStyle.None,
                    ShowInTaskbar = false,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent
                };

                // 订阅事件
                dialog.CloseRequested += (s, e) =>
                {
                    _pipePropertyWindow?.Close();
                    _pipePropertyWindow = null;
                };

                dialog.DialogResult += (s, result) =>
                {
                    _pipePropertyWindow?.Close();
                    _pipePropertyWindow = null;

                    if (result)
                    {
                        Debug.WriteLine("管道属性已保存");
                        _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();
                        _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("管道属性已更新");
                    }
                    else
                    {
                        Debug.WriteLine("管道属性已取消");
                    }
                };

                _pipePropertyWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"显示管道属性对话框失败: {ex.Message}");
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
                else if (child is TShapePipeControl tPipe && tPipe.PipeId != exceptPipeId)  //  新增
                {
                    tPipe.IsSelected = false;
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
            Panel.SetZIndex(pipe, 10);

            // ★ 新增：启用管道级别缓存（与直管道一致）
            RenderOptions.SetCachingHint(pipe, CachingHint.Cache);
            pipe.CacheMode = new BitmapCache
            {
                EnableClearType = false,
                RenderAtScale = 1.0,
                SnapsToDevicePixels = true
            };

            SubscribeElbowPipeEvents(pipe);

            var addCommand = new AddPipeCommand(
                DesignCanvas, pipe, position, "弯管道", _pipeConnectionService);

            _commandHistory.ExecuteCommand(addCommand);
            _pipes[pipe.PipeId] = pipe;

            // ★ 新增：标记四叉树需要重建
            if (_pipeSnapService is PipeSnapService snapService)
            {
                snapService.MarkQuadTreeDirty();
            }

            _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();
            Debug.WriteLine($"弯管道已创建（带缓存）: {pipe.PipeId}");
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
            //  订阅选中事件
            pipe.PipeSelected += OnPipeSelectedFromControl;
            pipe.PipeDeselected += OnPipeDeselectedFromControl;
            // ★ 新增：双击打开属性对话框
            pipe.PipeDoubleClicked += OnPipeDoubleClicked;
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
            Panel.SetZIndex(pipe, 10);

            // ★ 新增：启用管道级别缓存
            RenderOptions.SetCachingHint(pipe, CachingHint.Cache);
            pipe.CacheMode = new BitmapCache
            {
                EnableClearType = false,
                RenderAtScale = 1.0,
                SnapsToDevicePixels = true
            };

            SubscribeZShapePipeEvents(pipe);

            var addCommand = new AddPipeCommand(
                DesignCanvas, pipe, position, "Z形管道", _pipeConnectionService);

            _commandHistory.ExecuteCommand(addCommand);
            _pipes[pipe.PipeId] = pipe;

            // ★ 新增：标记四叉树需要重建
            if (_pipeSnapService is PipeSnapService snapService)
            {
                snapService.MarkQuadTreeDirty();
            }

            _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();
            Debug.WriteLine($"Z形管道已创建（带缓存）: {pipe.PipeId}");
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
            //  订阅选中事件
            pipe.PipeSelected += OnPipeSelectedFromControl;
            pipe.PipeDeselected += OnPipeDeselectedFromControl;
            // ★ 新增：双击打开属性对话框
            pipe.PipeDoubleClicked += OnPipeDoubleClicked;
        }

        /// <summary>
        /// 创建 T 型管道 - 使用命令模式
        /// </summary>
        private void CreateTShapePipe(Point position)
        {
            var pipe = new TShapePipeControl
            {
                PipeId = Guid.NewGuid().ToString(),
                MainPipeLength = 40,   
                BranchPipeLength = 20,    
                BranchPosition = 20,     
                PipeWidth = 8             
            };
            // 【修改】设置管道 ZIndex 为 10
            Panel.SetZIndex(pipe, 10);
            //  启用管道级别缓存
            RenderOptions.SetCachingHint(pipe, CachingHint.Cache);
            pipe.CacheMode = new BitmapCache
            {
                EnableClearType = false,
                RenderAtScale = 1.0,
                SnapsToDevicePixels = true
            };

            // 订阅事件
            SubscribeTShapePipeEvents(pipe);

            // 使用命令创建管道
            var addCommand = new AddPipeCommand(
                DesignCanvas,
                pipe,
                position,
                "T型管道",
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

            Debug.WriteLine($"T型管道已创建（带缓存）: {pipe.PipeId}");
        }
        /// <summary>
        /// 订阅 T 型管道事件
        /// </summary>
        private void SubscribeTShapePipeEvents(TShapePipeControl pipe)
        {
            pipe.PipeDragStarted += OnTShapePipeDragStarted;
            pipe.PipeDragging += OnTShapePipeDragging;
            pipe.PipeDragCompleted += OnTShapePipeDragCompleted;
            pipe.PipeResizeStarted += OnTShapePipeResizeStarted;
            pipe.PipeResizing += OnTShapePipeResizing;
            pipe.PipeResizeCompleted += OnTShapePipeResizeCompleted;
            pipe.PipeRotateStarted += OnTShapePipeRotateStarted;
            pipe.PipeRotating += OnTShapePipeRotating;
            pipe.PipeRotateCompleted += OnTShapePipeRotateCompleted;
            //  订阅选中事件
            pipe.PipeSelected += OnPipeSelectedFromControl;
            pipe.PipeDeselected += OnPipeDeselectedFromControl;
            // ★ 新增：双击打开属性对话框
            pipe.PipeDoubleClicked += OnPipeDoubleClicked;
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
                    IsHitTestVisible = true,  //  阀门需要交互（点击切换状态）
                    Cursor = System.Windows.Input.Cursors.Hand,
                    IsOpen = false  // 初始为关闭状态
                };

                //  移除：Loaded 事件中的边框隐藏逻辑
                // 因为设备库中的阀门不需要选择功能，画布上的阀门才需要

                //  包装在 Viewbox 中
                var viewbox = new System.Windows.Controls.Viewbox
                {
                    Width = targetWidth,
                    Height = targetHeight,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    StretchDirection = System.Windows.Controls.StretchDirection.Both,
                    Child = valveControl,
                    IsHitTestVisible = true  //  允许交互
                };

                Debug.WriteLine($"创建阀门图标容器: {targetWidth}x{targetHeight}");
                return viewbox;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($" 创建阀门图标失败: {ex.Message}");
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
                    Debug.WriteLine($" 手动阀门事件订阅完成: {valve.ValveId}");
                }

                //  新增：查找电磁阀
                var solenoidValve = FindVisualChild<SolenoidValveControl>(deviceControl);
                if (solenoidValve != null)
                {
                    Debug.WriteLine($"发现电磁阀控件，准备订阅事件: {solenoidValve.ValveId}");
                    SubscribeSolenoidValveEvents(solenoidValve);
                    Debug.WriteLine($" 电磁阀事件订阅完成: {solenoidValve.ValveId}");
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

                //  包装在 Viewbox 中
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
                Debug.WriteLine($" 创建电磁阀图标失败: {ex.Message}");
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

            //  旋转事件
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
        ///  新增：清除所有阀门选择
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

                //  清除电磁阀
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
        ///  新增：查找画布中所有阀门控件
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
        ///  新增：查找画布中所有电磁阀控件
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
                    IsHitTestVisible = true,  //  允许交互（点击切换流动状态）
                    Cursor = System.Windows.Input.Cursors.Hand,
                    CurrentFlow = 0.0,        // 初始流量为0
                    TargetFlow = 20.0,       // 默认目标流量
                    MaxFlow = 150.0,          // 最大流量
                    IsFlowing = false         // 初始为关闭状态
                };

                //  订阅流量计事件（如果需要）
                SubscribeMassFlowMeterEvents(meterControl);

                //  包装在 Viewbox 中
                var viewbox = new System.Windows.Controls.Viewbox
                {
                    Width = targetWidth,
                    Height = targetHeight,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    StretchDirection = System.Windows.Controls.StretchDirection.Both,
                    Child = meterControl,
                    IsHitTestVisible = true  //  允许交互
                };

                Debug.WriteLine($"创建流量计图标容器: {targetWidth}x{targetHeight}");
                return viewbox;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($" 创建流量计图标失败: {ex.Message}");
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
                //  新增：订阅状态切换和流量设置请求事件
                meter.StateChangeRequested += async (s, e) =>
                {
                    await HandleFlowMeterStateChangeRequestAsync(meter, e);
                };

                meter.TargetFlowChangeRequested += async (s, e) =>
                {
                    await HandleFlowMeterTargetFlowChangeRequestAsync(meter, e);
                };

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
                ClearAllHeatExchangerSelections(); //  清除换热器选择
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
        /// <summary>
        /// 处理流量计状态切换请求（UI → 设备驱动）
        /// </summary>
        private async Task HandleFlowMeterStateChangeRequestAsync(
            MassFlowMeterControl meter,
            FlowMeterStateChangeRequestEventArgs e)
        {
            try
            {
                Debug.WriteLine($"========== 处理流量计状态切换请求 ==========");
                Debug.WriteLine($"MeterId: {e.MeterId}");
                Debug.WriteLine($"当前状态: {e.CurrentState}");
                Debug.WriteLine($"请求状态: {e.RequestedState}");
                Debug.WriteLine($"目标流量: {e.TargetFlow:F1}");

                // 1. 查找对应的设备
                var canvasDevice = _viewModel?.CanvasDevices?.FirstOrDefault(d =>
                {
                    var deviceElement = FindDeviceElementInCanvas(d.DeviceId);
                    if (deviceElement == null) return false;

                    var meterInDevice = FindVisualChild<MassFlowMeterControl>(deviceElement);
                    return meterInDevice?.MeterId == e.MeterId;
                });

                if (canvasDevice?.Device == null)
                {
                    Debug.WriteLine($"未找到流量计对应的设备");
                    meter.HideLoading();
                    e.OnCompleted?.Invoke(false);
                    return;
                }

                // 2. 检查设备是否支持UI交互
                if (canvasDevice.Device is not IDeviceUIInteraction uiInteraction)
                {
                    Debug.WriteLine($"设备 {canvasDevice.Device.Name} 不支持UI交互");
                    meter.HideLoading();
                    e.OnCompleted?.Invoke(false);
                    return;
                }

                // 3. 确定命令和参数
                string commandName;
                Dictionary<string, object> parameters = null;

                if (e.RequestedState) // 启动流量控制
                {
                    commandName = "设置基准流量";

                    // 获取当前密度（可从UI或默认值）
                    double density = 1.0; // 默认水的密度

                    //  尝试从设备获取当前密度
                    var currentState = uiInteraction.GetCurrentState();
                    if (currentState?.States?.ContainsKey("Density") == true)
                    {
                        density = Convert.ToDouble(currentState.States["Density"]);
                    }

                    parameters = new Dictionary<string, object>
                    {
                        ["TargetFlow"] = e.TargetFlow,
                        ["Density"] = density,
                        ["EnableAutoControl"] = true
                    };

                    Debug.WriteLine($"执行命令: {commandName}, 流量={e.TargetFlow:F1}, 密度={density:F2}");
                }
                else // 停止流量控制
                {
                    commandName = "停止流量控制";
                    Debug.WriteLine($"执行命令: {commandName}");
                }

                // 4. 调用设备命令
                var result = await uiInteraction.ExecuteUICommandAsync(commandName, parameters);

                // 5. 隐藏加载动画
                meter.HideLoading();

                // 6. 处理执行结果
                if (result.Success)
                {
                    Debug.WriteLine($" 命令执行成功: {result.Message}");

                    //  UI状态将通过 OnDeviceStateChanged 自动更新
                    // meter.IsFlowing = e.RequestedState; // 不需要手动设置

                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $" {commandName}成功");

                    e.OnCompleted?.Invoke(true);
                }
                else
                {
                    Debug.WriteLine($" 命令执行失败: {result.Message}");

                    meter.ShowOperationFailure();

                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $" {commandName}失败: {result.Message}");

                    e.OnCompleted?.Invoke(false);
                }

                Debug.WriteLine($"==========================================");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($" 处理流量计状态切换请求失败: {ex.Message}");
                meter.HideLoading();
                meter.ShowOperationFailure();
                e.OnCompleted?.Invoke(false);

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $" 操作失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理流量计目标流量变化请求
        /// </summary>
        private async Task HandleFlowMeterTargetFlowChangeRequestAsync(
            MassFlowMeterControl meter,
            FlowMeterTargetFlowChangeRequestEventArgs e)
        {
            try
            {
                Debug.WriteLine($"========== 处理流量计目标流量变化 ==========");
                Debug.WriteLine($"MeterId: {e.MeterId}");
                Debug.WriteLine($"当前目标: {e.CurrentTargetFlow:F1}");
                Debug.WriteLine($"请求目标: {e.RequestedTargetFlow:F1}");

                // 查找设备（与状态切换相同）
                var canvasDevice = _viewModel?.CanvasDevices?.FirstOrDefault(d =>
                {
                    var deviceElement = FindDeviceElementInCanvas(d.DeviceId);
                    if (deviceElement == null) return false;

                    var meterInDevice = FindVisualChild<MassFlowMeterControl>(deviceElement);
                    return meterInDevice?.MeterId == e.MeterId;
                });

                if (canvasDevice?.Device is not IDeviceUIInteraction uiInteraction)
                {
                    e.OnCompleted?.Invoke(false);
                    return;
                }

                //  如果流量控制正在运行，更新目标流量
                if (meter.IsFlowing)
                {
                    var currentState = uiInteraction.GetCurrentState();
                    double density = 1.0;
                    if (currentState?.States?.ContainsKey("Density") == true)
                    {
                        density = Convert.ToDouble(currentState.States["Density"]);
                    }

                    var parameters = new Dictionary<string, object>
                    {
                        ["TargetFlow"] = e.RequestedTargetFlow,
                        ["Density"] = density,
                        ["EnableAutoControl"] = true
                    };

                    var result = await uiInteraction.ExecuteUICommandAsync("设置基准流量", parameters);

                    if (result.Success)
                    {
                        //  UI 将通过 OnDeviceStateChanged 自动更新
                        _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                            $" 目标流量设置为 {e.RequestedTargetFlow:F1}");
                    }

                    e.OnCompleted?.Invoke(result.Success);
                }
                else
                {
                    // 仅更新显示，不启动控制
                    meter.TargetFlow = e.RequestedTargetFlow;
                    e.OnCompleted?.Invoke(true);
                }

                Debug.WriteLine($"==========================================");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($" 处理目标流量变化失败: {ex.Message}");
                e.OnCompleted?.Invoke(false);
            }
        }
        #endregion

        /// <summary>
        ///  新增：清除所有流量计选择
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
        ///  新增：查找画布中所有流量计控件
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
                    IsHitTestVisible = true,  //  允许交互
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Temperature1 = 85.5,
                    Temperature2 = 92.3,
                    Temperature3 = 98.7,
                    IsReacting = false,  // 初始为关闭状态
                    IsFlow1Enabled = false,
                    IsFlow2Enabled = false,
                    //IsFlow3Enabled = true,
                    FlowRate = 100.0         // 默认流速
                };
                //  订阅微反应器事件
                SubscribeMicroreactorEvents(reactorControl);

                //  包装在 Viewbox 中
                var viewbox = new System.Windows.Controls.Viewbox
                {
                    Width = targetWidth,
                    Height = targetHeight,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    StretchDirection = System.Windows.Controls.StretchDirection.Both,
                    Child = reactorControl,
                    IsHitTestVisible = true  //  允许交互
                };

                Debug.WriteLine($"创建微反应器图标容器: {targetWidth}x{targetHeight}");
                return viewbox;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($" 创建微反应器图标失败: {ex.Message}");
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
                ClearAllMicroreactorSelections(); //  清除其他微反应器选择（保留当前）
                ClearAllHeatExchangerSelections(); //  新增
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
                Debug.WriteLine($"状态: {(e.IsEnabled ? " 启动" : " 停止")}");
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
        ///  新增：清除所有微反应器选择
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
        ///  新增:查找画布中所有微反应器控件
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
        #region 管式反应器创建
        /// <summary>
        /// 创建管式反应器
        /// </summary>
        /// <param name="targetWidth"></param>
        /// <param name="targetHeight"></param>
        /// <returns></returns>
        /// <summary>
        /// 创建管式反应器图标（用于画布上的小图标显示）
        /// </summary>
        private UIElement CreateTubularReactorIcon(double targetWidth, double targetHeight)
        {
            try
            {
                var reactorControl = new TubularReactorControl
                {
                    IsHitTestVisible = true,
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Temperature = 120.0,
                    Pressure = 1.50,
                    IsReacting = false,
                    IsFlowEnabled = false,
                    FlowRate = 100.0
                };

                // 订阅管式反应器事件
                SubscribeTubularReactorEvents(reactorControl);

                var viewbox = new System.Windows.Controls.Viewbox
                {
                    Width = targetWidth,
                    Height = targetHeight,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    StretchDirection = System.Windows.Controls.StretchDirection.Both,
                    Child = reactorControl,
                    IsHitTestVisible = true
                };

                Debug.WriteLine($"创建管式反应器图标容器: {targetWidth}x{targetHeight}");
                return viewbox;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"创建管式反应器图标失败: {ex.Message}");
                return CreateDefaultIcon(targetWidth, targetHeight, "管式反应器");
            }
        }
        #endregion

        #region 管式反应器事件处理
        /// <summary>
        /// 订阅管式反应器控件事件
        /// </summary>
        /// 
        private void SubscribeTubularReactorEvents(TubularReactorControl reactor)
        {
            try
            {
                // 选中/取消选中事件
                reactor.ReactorSelected += OnTubularReactorSelectedFromControl;
                reactor.ReactorDeselected += OnTubularReactorDeselectedFromControl;

                // 状态变化事件（反应状态）
                reactor.ReactorStateChanged += OnTubularReactorStateChanged;

                // 流动状态变化事件
                reactor.FlowStateChanged += OnTubularReactorFlowStateChanged;

                // 温度变化事件
                reactor.TemperatureChanged += OnTubularReactorTemperatureChanged;

                // 压力变化事件
                reactor.PressureChanged += OnTubularReactorPressureChanged;

                Debug.WriteLine($"管式反应器事件已订阅: {reactor.ReactorId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"订阅管式反应器事件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 管式反应器选中
        /// </summary>
        private void OnTubularReactorSelectedFromControl(object sender, string reactorId)
        {
            try
            {
                Debug.WriteLine($"管式反应器被选中: {reactorId}");

                // 清除其他选择
                ClearDeviceSelection();
                ClearAllPipeSelectionsExcept(null);
                ClearAllValveSelections();
                ClearAllFlowMeterSelections();
                ClearAllMicroreactorSelections();
                ClearAllHeatExchangerSelections();
                ClearAllDigitalScaleSelections();
                ClearAllTemperatureCirculatorSelections();
                ClearAllTubularReactorSelections(); // 清除其他管式反应器选择

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"已选中管式反应器: {reactorId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理管式反应器选中失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 管式反应器取消选中
        /// </summary>
        private void OnTubularReactorDeselectedFromControl(object sender, string reactorId)
        {
            try
            {
                Debug.WriteLine($"管式反应器取消选中: {reactorId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理管式反应器取消选中失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 管式反应器状态变化（反应启动/停止）
        /// </summary>
        private void OnTubularReactorStateChanged(object sender, TubularReactorStateChangedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"管式反应器 {e.ReactorId} 状态变化: " +
                                $"{(e.IsReacting ? "启动反应" : "停止反应")}");

                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    e.IsReacting ? "管式反应器已启动反应" : "管式反应器已停止反应");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"管式反应器状态变化处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 管式反应器流动状态变化
        /// </summary>
        private void OnTubularReactorFlowStateChanged(object sender, TubularFlowStateChangedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"管式反应器 {e.ReactorId} 流动状态变化: " +
                                $"{(e.IsEnabled ? "启动流动" : "停止流动")}");

                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    e.IsEnabled ? "管式反应器流动已启动" : "管式反应器流动已停止");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"管式反应器流动状态变化处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 管式反应器温度变化（来自控件内部仿真）
        /// </summary>
        private void OnTubularReactorTemperatureChanged(object sender, TubularTemperatureChangedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"管式反应器 {e.ReactorId} 温度变化: " +
                                $"{e.OldValue:F1}°C -> {e.NewValue:F1}°C");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"管式反应器温度变化处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 管式反应器压力变化（来自控件内部仿真）
        /// </summary>
        private void OnTubularReactorPressureChanged(object sender, TubularPressureChangedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"管式反应器 {e.ReactorId} 压力变化: " +
                                $"{e.OldValue:F2}MPa -> {e.NewValue:F2}MPa");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"管式反应器压力变化处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 清除所有管式反应器选择
        /// </summary>
        private void ClearAllTubularReactorSelections()
        {
            try
            {
                var reactors = FindAllTubularReactorsInCanvas();
                foreach (var reactor in reactors)
                {
                    reactor.IsSelected = false;
                    Debug.WriteLine($"清除管式反应器选择: {reactor.ReactorId}");
                }

                Debug.WriteLine($"清除了 {reactors.Count} 个管式反应器的选中状态");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清除管式反应器选择失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 查找画布中所有管式反应器控件
        /// </summary>
        private List<TubularReactorControl> FindAllTubularReactorsInCanvas()
        {
            var reactors = new List<TubularReactorControl>();

            try
            {
                foreach (var child in DesignCanvas.Children)
                {
                    if (child is Border deviceBorder)
                    {
                        var reactor = FindVisualChild<TubularReactorControl>(deviceBorder);
                        if (reactor != null)
                            reactors.Add(reactor);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"查找管式反应器控件失败: {ex.Message}");
            }

            return reactors;
        }

        #endregion
        #region 换热器

        /// <summary>
        /// 创建换热器图标（用于画布上的小图标显示）
        /// </summary>
        private UIElement CreateHeatExchangerIcon(double targetWidth, double targetHeight)
        {
            try
            {
                var exchangerControl = new HeatExchangerControl
                {
                    IsHitTestVisible = true,  //  允许交互
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Temperature = 85.0,       // 默认温度
                    Pressure = 2.5,           // 默认压力
                    IsOperating = false       // 初始为关闭状态
                };

                //  订阅换热器事件
                SubscribeHeatExchangerEvents(exchangerControl);

                //  包装在 Viewbox 中
                var viewbox = new System.Windows.Controls.Viewbox
                {
                    Width = targetWidth,
                    Height = targetHeight,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    StretchDirection = System.Windows.Controls.StretchDirection.Both,
                    Child = exchangerControl,
                    IsHitTestVisible = true  //  允许交互
                };

                Debug.WriteLine($"创建换热器图标容器: {targetWidth}x{targetHeight}");
                return viewbox;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($" 创建换热器图标失败: {ex.Message}");
                return CreateDefaultIcon(targetWidth, targetHeight, "换热器");
            }
        }

        #endregion
        #region 换热器事件处理

        /// <summary>
        /// 订阅换热器控件事件
        /// </summary>
        private void SubscribeHeatExchangerEvents(HeatExchangerControl exchanger)
        {
            try
            {
                // 选中事件
                exchanger.ExchangerSelected += OnHeatExchangerSelectedFromControl;
                exchanger.ExchangerDeselected += OnHeatExchangerDeselectedFromControl;

                // 旋转事件
                exchanger.ExchangerRotateStarted += OnHeatExchangerRotateStarted;
                exchanger.ExchangerRotating += OnHeatExchangerRotating;
                exchanger.ExchangerRotateCompleted += OnHeatExchangerRotateCompleted;

                // 状态变化事件
                exchanger.OperatingStateChanged += OnHeatExchangerStateChanged;

                Debug.WriteLine($"换热器事件已订阅: {exchanger.ExchangerId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"订阅换热器事件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 换热器选中
        /// </summary>
        private void OnHeatExchangerSelectedFromControl(object sender, string exchangerId)
        {
            try
            {
                Debug.WriteLine($"换热器被选中: {exchangerId}");

                // 清除其他选择
                ClearDeviceSelection();
                ClearAllPipeSelectionsExcept(null);
                ClearAllValveSelections();
                ClearAllFlowMeterSelections();
                ClearAllMicroreactorSelections();
                ClearAllHeatExchangerSelections(); //  清除其他换热器选择

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"已选中换热器: {exchangerId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理换热器选中失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 换热器取消选中
        /// </summary>
        private void OnHeatExchangerDeselectedFromControl(object sender, string exchangerId)
        {
            try
            {
                Debug.WriteLine($"换热器取消选中: {exchangerId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理换热器取消选中失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 换热器旋转开始
        /// </summary>
        private void OnHeatExchangerRotateStarted(object sender, ExchangerRotateEventArgs e)
        {
            try
            {
                Debug.WriteLine($"换热器旋转开始: {e.ExchangerId}");
                Debug.WriteLine($"原始角度: {e.OriginalAngle:F1}°");

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    "正在旋转换热器... (按住 Shift 键可15度步进)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"换热器旋转开始处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 换热器旋转中
        /// </summary>
        private void OnHeatExchangerRotating(object sender, ExchangerRotateEventArgs e)
        {
            try
            {
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"旋转角度: {e.CurrentAngle:F1}°");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"换热器旋转处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 换热器旋转完成
        /// </summary>
        private void OnHeatExchangerRotateCompleted(object sender, ExchangerRotateEventArgs e)
        {
            try
            {
                Debug.WriteLine($"换热器旋转完成: {e.ExchangerId}");
                Debug.WriteLine($"原始角度: {e.OriginalAngle:F1}°");
                Debug.WriteLine($"最终角度: {e.CurrentAngle:F1}°");

                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"换热器已旋转至 {e.CurrentAngle:F1}°");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"换热器旋转完成处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 换热器状态变化
        /// </summary>
        private void OnHeatExchangerStateChanged(object sender, ExchangerStateChangedEventArgs e)
        {
            try
            {
                Debug.WriteLine($"换热器 {e.ExchangerId} 状态变化: {(e.IsOperating ? "启动" : "停止")}");

                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    e.IsOperating ? "换热器已启动" : "换热器已停止");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"换热器状态变化处理失败: {ex.Message}");
            }
        }

        /// <summary>
        ///  新增：清除所有换热器选择
        /// </summary>
        private void ClearAllHeatExchangerSelections()
        {
            try
            {
                var exchangers = FindAllHeatExchangersInCanvas();
                foreach (var exchanger in exchangers)
                {
                    exchanger.HideSelection();
                    Debug.WriteLine($"清除换热器选择: {exchanger.ExchangerId}");
                }

                Debug.WriteLine($"清除了 {exchangers.Count} 个换热器的选中状态");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清除换热器选择失败: {ex.Message}");
            }
        }

        /// <summary>
        ///  新增：查找画布中所有换热器控件
        /// </summary>
        private List<HeatExchangerControl> FindAllHeatExchangersInCanvas()
        {
            var exchangers = new List<HeatExchangerControl>();

            try
            {
                foreach (var child in DesignCanvas.Children)
                {
                    if (child is Border deviceBorder)
                    {
                        var exchanger = FindVisualChild<HeatExchangerControl>(deviceBorder);
                        if (exchanger != null)
                        {
                            exchangers.Add(exchanger);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"查找换热器控件失败: {ex.Message}");
            }

            return exchangers;
        }

        #endregion
        #region 电子秤

        /// <summary>
        /// 创建电子秤图标（用于画布上的小图标显示）
        /// </summary>
        private UIElement CreateDigitalScaleIcon(double targetWidth, double targetHeight)
        {
            try
            {
                var scaleControl = new DigitalScaleControl
                {
                    IsHitTestVisible = true,  // 允许交互
                    Cursor = System.Windows.Input.Cursors.Hand,
                    IsPoweredOn = true,      // 初始为关闭状态
                    WeightValue = 0.0,        // 初始重量为0
                    CurrentUnit = "g",        // 默认单位为克
                    IsTared = false           // 初始未去皮
                };

                // 订阅电子秤事件
                SubscribeDigitalScaleEvents(scaleControl);

                // 包装在 Viewbox 中
                var viewbox = new System.Windows.Controls.Viewbox
                {
                    Width = targetWidth,
                    Height = targetHeight,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    StretchDirection = System.Windows.Controls.StretchDirection.Both,
                    Child = scaleControl,
                    IsHitTestVisible = true  // 允许交互
                };

                Debug.WriteLine($"创建电子秤图标容器: {targetWidth}x{targetHeight}");
                return viewbox;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"创建电子秤图标失败: {ex.Message}");
                return CreateDefaultIcon(targetWidth, targetHeight, "电子秤");
            }
        }

        /// <summary>
        /// 创建加热磁力搅拌器图标容器
        /// </summary>
        private UIElement CreateMagneticStirrerIcon(double targetWidth, double targetHeight)
        {
            try
            {
                // 画布上这个控件只负责"显示"(温度/转速/加热辉光/搅拌动画),交互一律走
                // 双击 → 设备屏幕。所以整体关掉命中测试:否则控件自带的 DraggableArea
                // 会把鼠标抢走,设计器自己的选中和拖动就失灵了。
                var stirrerControl = new MagneticStirrerControl
                {
                    IsHitTestVisible = false,
                    Temperature = 0.0,
                    Speed = 0.0,
                    IsHeating = false,
                    IsStirring = false
                };

                var viewbox = new System.Windows.Controls.Viewbox
                {
                    Width = targetWidth,
                    Height = targetHeight,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    StretchDirection = System.Windows.Controls.StretchDirection.Both,
                    Child = stirrerControl,
                    IsHitTestVisible = false
                };

                Debug.WriteLine($"创建磁力搅拌器图标容器: {targetWidth}x{targetHeight}");
                return viewbox;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"创建磁力搅拌器图标失败: {ex.Message}");
                return CreateDefaultIcon(targetWidth, targetHeight, "磁力搅拌器");
            }
        }

        /// <summary>
        /// 创建精睿柱塞泵图标容器
        /// </summary>
        private UIElement CreateJingRuiPumpIcon(double targetWidth, double targetHeight)
        {
            try
            {
                // 与磁力搅拌器同理:画布上这个控件只负责"显示"(流量/压力/状态灯/柱塞与液流动画),
                // 交互一律走双击 → 设备屏幕。整体关掉命中测试,否则控件自带的 DraggableArea
                // 会把鼠标抢走,设计器自己的选中和拖动就失灵了。
                var pumpControl = new JingRuiPumpControl
                {
                    IsHitTestVisible = false,
                    Flow = 0.0,
                    FlowSetpoint = 0.0,
                    Pressure = 0.0,
                    IsRunning = false,
                    IsDosing = false,
                    FaultCode = 0.0
                };

                var viewbox = new System.Windows.Controls.Viewbox
                {
                    Width = targetWidth,
                    Height = targetHeight,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    StretchDirection = System.Windows.Controls.StretchDirection.Both,
                    Child = pumpControl,
                    IsHitTestVisible = false
                };

                Debug.WriteLine($"创建精睿柱塞泵图标容器: {targetWidth}x{targetHeight}");
                return viewbox;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"创建精睿柱塞泵图标失败: {ex.Message}");
                return CreateDefaultIcon(targetWidth, targetHeight, "精睿泵");
            }
        }

        #endregion
        #region 电子秤事件处理

        /// <summary>
        /// 订阅电子秤控件事件
        /// </summary>
        private void SubscribeDigitalScaleEvents(DigitalScaleControl scale)
        {
            try
            {
                // 选中事件
                scale.ScaleSelected += OnDigitalScaleSelectedFromControl;
                scale.ScaleDeselected += OnDigitalScaleDeselectedFromControl;

                // 拖动事件
                scale.ScaleDragStarted += OnDigitalScaleDragStarted;
                scale.ScaleDragging += OnDigitalScaleDragging;
                scale.ScaleDragCompleted += OnDigitalScaleDragCompleted;

                // UI请求事件（UI → 设备驱动）
                scale.PowerStateChangeRequested += async (s, e) =>
                {
                    await HandleScalePowerStateChangeRequestAsync(scale, e);
                };

                scale.TareRequested += async (s, e) =>
                {
                    await HandleScaleTareRequestAsync(scale, e);
                };

                scale.UnitChangeRequested += async (s, e) =>
                {
                    await HandleScaleUnitChangeRequestAsync(scale, e);
                };

                Debug.WriteLine($" 电子秤事件已订阅: {scale.ScaleId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"订阅电子秤事件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 电子秤选中
        /// </summary>
        private void OnDigitalScaleSelectedFromControl(object sender, string scaleId)
        {
            try
            {
                Debug.WriteLine($"电子秤被选中: {scaleId}");

                // 清除其他选择
                ClearDeviceSelection();
                ClearAllPipeSelectionsExcept(null);
                ClearAllValveSelections();
                ClearAllFlowMeterSelections();
                ClearAllMicroreactorSelections();
                ClearAllHeatExchangerSelections();
                ClearAllDigitalScaleSelections(); // 清除其他电子秤选择

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"已选中电子秤: {scaleId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理电子秤选中失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 电子秤取消选中
        /// </summary>
        private void OnDigitalScaleDeselectedFromControl(object sender, string scaleId)
        {
            try
            {
                Debug.WriteLine($"电子秤取消选中: {scaleId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理电子秤取消选中失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 电子秤拖动开始
        /// </summary>
        private void OnDigitalScaleDragStarted(object sender, ScaleDragEventArgs e)
        {
            try
            {
                Debug.WriteLine($"电子秤拖动开始: {e.ScaleId}");
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("正在移动电子秤...");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"电子秤拖动开始处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 电子秤拖动中（节流优化）
        /// </summary>
        private void OnDigitalScaleDragging(object sender, ScaleDragEventArgs e)
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
                Debug.WriteLine($"电子秤拖动处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 电子秤拖动完成
        /// </summary>
        private void OnDigitalScaleDragCompleted(object sender, ScaleDragEventArgs e)
        {
            try
            {
                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("电子秤移动完成");

                Debug.WriteLine($"电子秤拖动完成: {e.ScaleId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"电子秤拖动完成处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理电子秤电源状态切换请求（UI → 设备驱动）
        /// </summary>
        private async Task HandleScalePowerStateChangeRequestAsync(
            DigitalScaleControl scale,
            PowerStateChangeRequestEventArgs e)
        {
            try
            {
                Debug.WriteLine($"========== 处理电子秤电源状态切换 ==========");
                Debug.WriteLine($"ScaleId: {e.ScaleId}");
                Debug.WriteLine($"当前状态: {e.CurrentState}");
                Debug.WriteLine($"请求状态: {e.RequestedState}");

                // 1. 查找对应的设备
                var canvasDevice = _viewModel?.CanvasDevices?.FirstOrDefault(d =>
                {
                    var deviceElement = FindDeviceElementInCanvas(d.DeviceId);
                    if (deviceElement == null) return false;

                    var scaleInDevice = FindVisualChild<DigitalScaleControl>(deviceElement);
                    return scaleInDevice?.ScaleId == e.ScaleId;
                });

                if (canvasDevice?.Device == null)
                {
                    Debug.WriteLine($"未找到电子秤对应的设备");
                    scale.HideLoading();
                    e.OnCompleted?.Invoke(false);
                    return;
                }

                // 2. 检查设备是否支持UI交互
                if (canvasDevice.Device is not IDeviceUIInteraction uiInteraction)
                {
                    Debug.WriteLine($"设备 {canvasDevice.Device.Name} 不支持UI交互");
                    scale.HideLoading();
                    e.OnCompleted?.Invoke(false);
                    return;
                }

                // 3. 确定命令名称（根据你的驱动实现调整）
                string commandName = e.RequestedState ? "开机" : "关机";
                Debug.WriteLine($"执行命令: {commandName}");

                // 4. 调用设备命令
                var result = await uiInteraction.ExecuteUICommandAsync(commandName);

                // 5. 隐藏加载动画
                scale.HideLoading();

                // 6. 处理执行结果
                if (result.Success)
                {
                    Debug.WriteLine($" 命令执行成功: {result.Message}");

                    // UI状态将通过 OnDeviceStateChanged 自动更新
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $" {commandName}成功");

                    e.OnCompleted?.Invoke(true);
                }
                else
                {
                    Debug.WriteLine($"命令执行失败: {result.Message}");

                    scale.ShowOperationFailure();

                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $"{commandName}失败: {result.Message}");

                    e.OnCompleted?.Invoke(false);
                }

                Debug.WriteLine($"==========================================");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理电子秤电源状态切换失败: {ex.Message}");
                scale.HideLoading();
                scale.ShowOperationFailure();
                e.OnCompleted?.Invoke(false);

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"操作失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理电子秤去皮请求（UI → 设备驱动）
        /// </summary>
        private async Task HandleScaleTareRequestAsync(
            DigitalScaleControl scale,
            TareRequestEventArgs e)
        {
            try
            {
                Debug.WriteLine($"========== 处理电子秤去皮请求 ==========");
                Debug.WriteLine($"ScaleId: {e.ScaleId}");
                Debug.WriteLine($"当前重量: {e.CurrentWeight}");

                // 查找设备（逻辑同上）
                var canvasDevice = _viewModel?.CanvasDevices?.FirstOrDefault(d =>
                {
                    var deviceElement = FindDeviceElementInCanvas(d.DeviceId);
                    if (deviceElement == null) return false;

                    var scaleInDevice = FindVisualChild<DigitalScaleControl>(deviceElement);
                    return scaleInDevice?.ScaleId == e.ScaleId;
                });

                if (canvasDevice?.Device is not IDeviceUIInteraction uiInteraction)
                {
                    scale.HideLoading();
                    e.OnCompleted?.Invoke(false);
                    return;
                }

                // 调用去皮命令
                var result = await uiInteraction.ExecuteUICommandAsync("去皮");

                scale.HideLoading();

                if (result.Success)
                {
                    Debug.WriteLine($" 去皮成功");
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(" 电子秤已去皮");
                    e.OnCompleted?.Invoke(true);
                }
                else
                {
                    Debug.WriteLine($"去皮失败: {result.Message}");
                    scale.ShowOperationFailure();
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"去皮失败: {result.Message}");
                    e.OnCompleted?.Invoke(false);
                }

                Debug.WriteLine($"==========================================");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理去皮请求失败: {ex.Message}");
                scale.HideLoading();
                scale.ShowOperationFailure();
                e.OnCompleted?.Invoke(false);
            }
        }

        /// <summary>
        /// 处理电子秤单位切换请求（UI → 设备驱动）
        /// </summary>
        private async Task HandleScaleUnitChangeRequestAsync(
            DigitalScaleControl scale,
            UnitChangeRequestEventArgs e)
        {
            try
            {
                Debug.WriteLine($"========== 处理电子秤单位切换 ==========");
                Debug.WriteLine($"ScaleId: {e.ScaleId}");
                Debug.WriteLine($"当前单位: {e.CurrentUnit}");
                Debug.WriteLine($"请求单位: {e.RequestedUnit}");

                // 查找设备（逻辑同上）
                var canvasDevice = _viewModel?.CanvasDevices?.FirstOrDefault(d =>
                {
                    var deviceElement = FindDeviceElementInCanvas(d.DeviceId);
                    if (deviceElement == null) return false;

                    var scaleInDevice = FindVisualChild<DigitalScaleControl>(deviceElement);
                    return scaleInDevice?.ScaleId == e.ScaleId;
                });

                if (canvasDevice?.Device is not IDeviceUIInteraction uiInteraction)
                {
                    scale.HideLoading();
                    e.OnCompleted?.Invoke(false);
                    return;
                }

                // 调用切换单位命令（带参数）
                var parameters = new Dictionary<string, object>
                {
                    ["TargetUnit"] = MapUnitStringToEnum(e.RequestedUnit) // 需要实现单位映射
                };

                var result = await uiInteraction.ExecuteUICommandAsync("切换单位", parameters);

                scale.HideLoading();

                if (result.Success)
                {
                    Debug.WriteLine($" 单位切换成功: {e.RequestedUnit}");
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($" 单位已切换为 {e.RequestedUnit}");
                    e.OnCompleted?.Invoke(true);
                }
                else
                {
                    Debug.WriteLine($"单位切换失败: {result.Message}");
                    scale.ShowOperationFailure();
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"切换单位失败: {result.Message}");
                    e.OnCompleted?.Invoke(false);
                }

                Debug.WriteLine($"==========================================");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理单位切换请求失败: {ex.Message}");
                scale.HideLoading();
                scale.ShowOperationFailure();
                e.OnCompleted?.Invoke(false);
            }
        }

        /// <summary>
        /// 单位字符串映射到枚举（根据你的驱动调整）
        /// </summary>
        private object MapUnitStringToEnum(string unitStr)
        {
            // 假设驱动中有 WeightUnit 枚举
            return unitStr switch
            {
                "g" => 0,   // WeightUnit.Gram
                "kg" => 1,  // WeightUnit.Kilogram
                "lb" => 2,  // WeightUnit.Pound
                "oz" => 3,  // WeightUnit.Ounce
                _ => 0
            };
        }

        /// <summary>
        /// 清除所有电子秤选择
        /// </summary>
        private void ClearAllDigitalScaleSelections()
        {
            try
            {
                var scales = FindAllDigitalScalesInCanvas();
                foreach (var scale in scales)
                {
                    scale.IsSelected = false;
                    Debug.WriteLine($"清除电子秤选择: {scale.ScaleId}");
                }

                Debug.WriteLine($"清除了 {scales.Count} 个电子秤的选中状态");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清除电子秤选择失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 查找画布中所有电子秤控件
        /// </summary>
        private List<DigitalScaleControl> FindAllDigitalScalesInCanvas()
        {
            var scales = new List<DigitalScaleControl>();

            try
            {
                foreach (var child in DesignCanvas.Children)
                {
                    if (child is Border deviceBorder)
                    {
                        var scale = FindVisualChild<DigitalScaleControl>(deviceBorder);
                        if (scale != null)
                        {
                            scales.Add(scale);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"查找电子秤控件失败: {ex.Message}");
            }

            return scales;
        }

        #endregion
        #region 高低温循环器

        /// <summary>
        /// 创建高低温循环器图标（用于画布上的小图标显示）
        /// </summary>
        private UIElement CreateTemperatureCirculatorIcon(double targetWidth, double targetHeight)
        {
            try
            {
                var circulatorControl = new TemperatureControlSystemControl
                {
                    IsHitTestVisible = true,  // 允许交互
                    Cursor = System.Windows.Input.Cursors.Hand,
                    SetTemperature = -40.0,
                    CurrentTemperature = 24.5,
                    
                };

                // 订阅高低温循环器事件
                SubscribeTemperatureCirculatorEvents(circulatorControl);

                // 包装在 Viewbox 中
                var viewbox = new System.Windows.Controls.Viewbox
                {
                    Width = targetWidth,
                    Height = targetHeight,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                    StretchDirection = System.Windows.Controls.StretchDirection.Both,
                    Child = circulatorControl,
                    IsHitTestVisible = true  // 允许交互
                };

                Debug.WriteLine($"创建高低温循环器图标容器: {targetWidth}x{targetHeight}");
                return viewbox;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"创建高低温循环器图标失败: {ex.Message}");
                return CreateDefaultIcon(targetWidth, targetHeight, "高低温循环器");
            }
        }

        #endregion
        #region 高低温循环器事件处理

        /// <summary>
        /// 订阅高低温循环器控件事件
        /// </summary>
        private void SubscribeTemperatureCirculatorEvents(TemperatureControlSystemControl circulator)
        {
            try
            {
                // 拖动事件
                circulator.SystemDragStarted += OnCirculatorDragStarted;
                circulator.SystemDragging += OnCirculatorDragging;
                circulator.SystemDragCompleted += OnCirculatorDragCompleted;

                // 选中事件
                circulator.SystemSelected += OnCirculatorSelectedFromControl;
                circulator.SystemDeselected += OnCirculatorDeselectedFromControl;

                // UI请求事件（UI → 设备驱动）
                circulator.TemperatureSetRequested += async (s, e) =>
                {
                    await HandleCirculatorTemperatureSetRequestAsync(circulator, e);
                };

                circulator.CirculationRequested += async (s, e) =>
                {
                    await HandleCirculatorCirculationRequestAsync(circulator, e);
                };

                circulator.HeatingRequested += async (s, e) =>
                {
                    await HandleCirculatorHeatingRequestAsync(circulator, e);
                };

                circulator.CoolingRequested += async (s, e) =>
                {
                    await HandleCirculatorCoolingRequestAsync(circulator, e);
                };

                Debug.WriteLine($"高低温循环器事件已订阅: {circulator.SystemId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"订阅高低温循环器事件失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 高低温循环器拖动开始
        /// </summary>
        private void OnCirculatorDragStarted(object sender, TempSystemDragEventArgs e)
        {
            try
            {
                Debug.WriteLine($"高低温循环器拖动开始: {e.SystemId}");
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("正在移动高低温循环器...");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"高低温循环器拖动开始处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 高低温循环器拖动中
        /// </summary>
        private void OnCirculatorDragging(object sender, TempSystemDragEventArgs e)
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
                // 实时显示位置
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"位置: ({e.CurrentPosition.X:F0}, {e.CurrentPosition.Y:F0})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"高低温循环器拖动处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 高低温循环器拖动完成
        /// </summary>
        private void OnCirculatorDragCompleted(object sender, TempSystemDragEventArgs e)
        {
            try
            {
                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("高低温循环器移动完成");

                Debug.WriteLine($"高低温循环器拖动完成: {e.SystemId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"高低温循环器拖动完成处理失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 高低温循环器选中
        /// </summary>
        private void OnCirculatorSelectedFromControl(object sender, string systemId)
        {
            try
            {
                Debug.WriteLine($"高低温循环器被选中: {systemId}");

                // 清除其他选择
                ClearDeviceSelection();
                ClearAllPipeSelectionsExcept(null);
                ClearAllValveSelections();
                ClearAllFlowMeterSelections();
                ClearAllMicroreactorSelections();
                ClearAllHeatExchangerSelections();
                ClearAllDigitalScaleSelections();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"已选中高低温循环器: {systemId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理高低温循环器选中失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 高低温循环器取消选中
        /// </summary>
        private void OnCirculatorDeselectedFromControl(object sender, string systemId)
        {
            try
            {
                Debug.WriteLine($"高低温循环器取消选中: {systemId}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理高低温循环器取消选中失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理温度设置请求（UI → 设备驱动）
        /// </summary>
        private async Task HandleCirculatorTemperatureSetRequestAsync(
            TemperatureControlSystemControl circulator,
            TemperatureSetRequestEventArgs e)
        {
            try
            {
                Debug.WriteLine($"========== 处理高低温循环器温度设置 ==========");
                Debug.WriteLine($"SystemId: {e.SystemId}");
                Debug.WriteLine($"当前温度: {e.CurrentTemperature}℃");
                Debug.WriteLine($"请求温度: {e.RequestedTemperature}℃");

                // 1. 查找对应的设备
                var canvasDevice = _viewModel?.CanvasDevices?.FirstOrDefault(d =>
                {
                    var deviceElement = FindDeviceElementInCanvas(d.DeviceId);
                    if (deviceElement == null) return false;

                    var circulatorInDevice = FindVisualChild<TemperatureControlSystemControl>(deviceElement);
                    return circulatorInDevice?.SystemId == e.SystemId;
                });

                if (canvasDevice?.Device == null)
                {
                    Debug.WriteLine($"未找到高低温循环器对应的设备");
                    circulator.HideLoading();
                    e.OnCompleted?.Invoke(false);
                    return;
                }

                // 2. 检查设备是否支持UI交互
                if (canvasDevice.Device is not IDeviceUIInteraction uiInteraction)
                {
                    Debug.WriteLine($"设备 {canvasDevice.Device.Name} 不支持UI交互");
                    circulator.HideLoading();
                    e.OnCompleted?.Invoke(false);
                    return;
                }

                // 3. 构造参数
                var parameters = new Dictionary<string, object>
                {
                    ["TargetTemperature"] = e.RequestedTemperature
                };

                Debug.WriteLine($"执行命令: 设置温度, 参数={e.RequestedTemperature}℃");

                // 4. 调用设备命令
                var result = await uiInteraction.ExecuteUICommandAsync("设置温度", parameters);

                // 5. 隐藏加载动画
                circulator.HideLoading();

                // 6. 处理执行结果
                if (result.Success)
                {
                    Debug.WriteLine($"温度设置成功: {result.Message}");

                    // UI状态将通过 OnDeviceStateChanged 自动更新
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $"温度已设置为 {e.RequestedTemperature}℃");

                    e.OnCompleted?.Invoke(true);
                }
                else
                {
                    Debug.WriteLine($"温度设置失败: {result.Message}");

                    circulator.ShowOperationFailure();

                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $"温度设置失败: {result.Message}");

                    e.OnCompleted?.Invoke(false);
                }

                Debug.WriteLine($"==========================================");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理温度设置请求失败: {ex.Message}");
                circulator.HideLoading();
                circulator.ShowOperationFailure();
                e.OnCompleted?.Invoke(false);

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"操作失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 处理循环请求（UI → 设备驱动）
        /// </summary>
        private async Task HandleCirculatorCirculationRequestAsync(
            TemperatureControlSystemControl circulator,
            CirculationRequestEventArgs e)
        {
            try
            {
                Debug.WriteLine($"========== 处理循环状态切换 ==========");
                Debug.WriteLine($"SystemId: {e.SystemId}");
                Debug.WriteLine($"当前状态: {e.CurrentState}");
                Debug.WriteLine($"请求状态: {e.RequestedState}");

                var canvasDevice = FindCirculatorDevice(e.SystemId);
                if (canvasDevice?.Device is not IDeviceUIInteraction uiInteraction)
                {
                    circulator.HideLoading();
                    e.OnCompleted?.Invoke(false);
                    return;
                }

                string commandName = e.RequestedState ? "启动循环" : "停止循环";
                Debug.WriteLine($"执行命令: {commandName}");

                var result = await uiInteraction.ExecuteUICommandAsync(commandName);

                circulator.HideLoading();

                if (result.Success)
                {
                    Debug.WriteLine($"命令执行成功: {result.Message}");
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $"{commandName}成功");
                    e.OnCompleted?.Invoke(true);
                }
                else
                {
                    Debug.WriteLine($"命令执行失败: {result.Message}");
                    circulator.ShowOperationFailure();
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $"{commandName}失败: {result.Message}");
                    e.OnCompleted?.Invoke(false);
                }

                Debug.WriteLine($"==========================================");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理循环请求失败: {ex.Message}");
                circulator.HideLoading();
                circulator.ShowOperationFailure();
                e.OnCompleted?.Invoke(false);
            }
        }

        /// <summary>
        /// 处理加热请求（UI → 设备驱动）
        /// </summary>
        private async Task HandleCirculatorHeatingRequestAsync(
            TemperatureControlSystemControl circulator,
            HeatingRequestEventArgs e)
        {
            try
            {
                Debug.WriteLine($"========== 处理加热状态切换 ==========");
                Debug.WriteLine($"SystemId: {e.SystemId}");
                Debug.WriteLine($"当前状态: {e.CurrentState}");
                Debug.WriteLine($"请求状态: {e.RequestedState}");

                var canvasDevice = FindCirculatorDevice(e.SystemId);
                if (canvasDevice?.Device is not IDeviceUIInteraction uiInteraction)
                {
                    circulator.HideLoading();
                    e.OnCompleted?.Invoke(false);
                    return;
                }

                string commandName = e.RequestedState ? "启动加热" : "停止加热";
                Debug.WriteLine($"执行命令: {commandName}");

                var result = await uiInteraction.ExecuteUICommandAsync(commandName);

                circulator.HideLoading();

                if (result.Success)
                {
                    Debug.WriteLine($"命令执行成功: {result.Message}");
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $"{commandName}成功");
                    e.OnCompleted?.Invoke(true);
                }
                else
                {
                    Debug.WriteLine($"命令执行失败: {result.Message}");
                    circulator.ShowOperationFailure();
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $"{commandName}失败: {result.Message}");
                    e.OnCompleted?.Invoke(false);
                }

                Debug.WriteLine($"==========================================");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理加热请求失败: {ex.Message}");
                circulator.HideLoading();
                circulator.ShowOperationFailure();
                e.OnCompleted?.Invoke(false);
            }
        }

        /// <summary>
        /// 处理制冷请求（UI → 设备驱动）
        /// </summary>
        private async Task HandleCirculatorCoolingRequestAsync(
            TemperatureControlSystemControl circulator,
            CoolingRequestEventArgs e)
        {
            try
            {
                Debug.WriteLine($"========== 处理制冷状态切换 ==========");
                Debug.WriteLine($"SystemId: {e.SystemId}");
                Debug.WriteLine($"当前状态: {e.CurrentState}");
                Debug.WriteLine($"请求状态: {e.RequestedState}");

                var canvasDevice = FindCirculatorDevice(e.SystemId);
                if (canvasDevice?.Device is not IDeviceUIInteraction uiInteraction)
                {
                    circulator.HideLoading();
                    e.OnCompleted?.Invoke(false);
                    return;
                }

                string commandName = e.RequestedState ? "启动制冷" : "停止制冷";
                Debug.WriteLine($"执行命令: {commandName}");

                var result = await uiInteraction.ExecuteUICommandAsync(commandName);

                circulator.HideLoading();

                if (result.Success)
                {
                    Debug.WriteLine($"命令执行成功: {result.Message}");
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $"{commandName}成功");
                    e.OnCompleted?.Invoke(true);
                }
                else
                {
                    Debug.WriteLine($"命令执行失败: {result.Message}");
                    circulator.ShowOperationFailure();
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $"{commandName}失败: {result.Message}");
                    e.OnCompleted?.Invoke(false);
                }

                Debug.WriteLine($"==========================================");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理制冷请求失败: {ex.Message}");
                circulator.HideLoading();
                circulator.ShowOperationFailure();
                e.OnCompleted?.Invoke(false);
            }
        }

        /// <summary>
        /// 查找高低温循环器设备
        /// </summary>
        private CanvasDeviceViewModel FindCirculatorDevice(string systemId)
        {
            try
            {
                return _viewModel?.CanvasDevices?.FirstOrDefault(d =>
                {
                    var deviceElement = FindDeviceElementInCanvas(d.DeviceId);
                    if (deviceElement == null) return false;

                    var circulatorInDevice = FindVisualChild<TemperatureControlSystemControl>(deviceElement);
                    return circulatorInDevice?.SystemId == systemId;
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"查找高低温循环器设备失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 清除所有高低温循环器选择
        /// </summary>
        private void ClearAllTemperatureCirculatorSelections()
        {
            try
            {
                var circulators = FindAllTemperatureCirculatorsInCanvas();
                foreach (var circulator in circulators)
                {
                    circulator.IsSelected = false;
                    Debug.WriteLine($"清除高低温循环器选择: {circulator.SystemId}");
                }

                Debug.WriteLine($"清除了 {circulators.Count} 个高低温循环器的选中状态");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"清除高低温循环器选择失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 查找画布中所有高低温循环器控件
        /// </summary>
        private List<TemperatureControlSystemControl> FindAllTemperatureCirculatorsInCanvas()
        {
            var circulators = new List<TemperatureControlSystemControl>();

            try
            {
                foreach (var child in DesignCanvas.Children)
                {
                    if (child is Border deviceBorder)
                    {
                        var circulator = FindVisualChild<TemperatureControlSystemControl>(deviceBorder);
                        if (circulator != null)
                        {
                            circulators.Add(circulator);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"查找高低温循环器控件失败: {ex.Message}");
            }

            return circulators;
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
            // ========== 新增：设置参数事件 ==========
            _eventAggregator?.GetEvent<VoiceSetDeviceParameterEvent>()?.Subscribe(
                OnVoiceSetDeviceParameter,
                ThreadOption.UIThread);

            // ========== 新增：控制状态事件 ==========
            _eventAggregator?.GetEvent<VoiceControlDeviceStateEvent>()?.Subscribe(
                OnVoiceControlDeviceState,
                ThreadOption.UIThread);
            Debug.WriteLine(" 语音助手事件已订阅");
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
                    Debug.WriteLine($"未找到设备: {args.DeviceName}");
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $"未找到 {args.DeviceName}");
                    return;
                }

                // 2. 计算放置位置（智能避让）
                var dropPosition = CalculateSmartPosition();

                // 3. 创建设备
                CreateDeviceFromVoiceCommand(device, dropPosition);

                Debug.WriteLine($"语音创建设备成功: {args.DeviceName}");
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    $"已创建 {args.DeviceName}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"语音创建设备失败: {ex.Message}");
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                    "创建设备失败");
            }
        }
        // ========== 新增：智能计算位置（避免重叠）==========
        private Point CalculateSmartPosition()
        {
            try
            {
                var canvasWidth = DesignCanvas.ActualWidth > 0 ? DesignCanvas.ActualWidth : 1200;
                var canvasHeight = DesignCanvas.ActualHeight > 0 ? DesignCanvas.ActualHeight : 800;

                // 策略1：如果画布为空，放在中心
                if (_viewModel?.CanvasDevices?.Count == 0)
                {
                    Debug.WriteLine(" 画布为空，放置在中心位置");
                    return new Point(canvasWidth / 2, canvasHeight / 2);
                }

                // 策略2：尝试在中心附近螺旋查找空位
                var centerPosition = new Point(canvasWidth / 2, canvasHeight / 2);
                var emptyPosition = FindEmptyPositionSpiral(centerPosition);

                if (emptyPosition != null)
                {
                    Debug.WriteLine($" 找到空位: ({emptyPosition.Value.X:F0}, {emptyPosition.Value.Y:F0})");
                    return emptyPosition.Value;
                }

                // 策略3：如果中心附近都满了，随机放置在边缘区域
                var edgePosition = FindEmptyPositionInEdgeArea();
                Debug.WriteLine($" 使用边缘位置: ({edgePosition.X:F0}, {edgePosition.Y:F0})");

                return edgePosition;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"计算位置失败: {ex.Message}");
                // 降级方案：完全随机
                var random = new Random();
                return new Point(
                    random.Next(100, (int)DesignCanvas.ActualWidth - 100),
                    random.Next(100, (int)DesignCanvas.ActualHeight - 100)
                );
            }
        }

        // ========== 新增：螺旋查找空位 ==========
        private Point? FindEmptyPositionSpiral(Point center, int maxRadius = 300, int step = 50)
        {
            try
            {
                const double deviceSpacing = 120.0; // 设备间最小间距

                // 从内到外螺旋搜索
                for (int radius = step; radius <= maxRadius; radius += step)
                {
                    // 每个半径上测试多个角度
                    int angleSteps = Math.Max(8, radius / 20); // 半径越大，测试点越多

                    for (int i = 0; i < angleSteps; i++)
                    {
                        double angle = (360.0 / angleSteps) * i;
                        double radians = angle * Math.PI / 180.0;

                        var testPosition = new Point(
                            center.X + radius * Math.Cos(radians),
                            center.Y + radius * Math.Sin(radians)
                        );

                        // 检查位置是否有效
                        if (!IsValidDropPosition(testPosition))
                        {
                            continue;
                        }

                        // 检查是否与现有设备重叠
                        if (!IsPositionOccupiedWithSpacing(testPosition, deviceSpacing))
                        {
                            Debug.WriteLine($" 螺旋搜索找到空位: 半径={radius}, 角度={angle:F1}°");
                            return testPosition;
                        }
                    }
                }

                Debug.WriteLine(" 螺旋搜索未找到空位");
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"螺旋搜索失败: {ex.Message}");
                return null;
            }
        }

        // ========== 新增：在边缘区域查找空位 ==========
        private Point FindEmptyPositionInEdgeArea()
        {
            try
            {
                var canvasWidth = DesignCanvas.ActualWidth > 0 ? DesignCanvas.ActualWidth : 1200;
                var canvasHeight = DesignCanvas.ActualHeight > 0 ? DesignCanvas.ActualHeight : 800;
                var random = new Random();

                const int maxAttempts = 50;
                const double edgeMargin = 100.0;
                const double deviceSpacing = 120.0;

                // 定义四个边缘区域
                var edgeAreas = new[]
                {
            new { Name = "上边缘", MinX = edgeMargin, MaxX = canvasWidth - edgeMargin,
                  MinY = edgeMargin, MaxY = canvasHeight * 0.25 },
            new { Name = "下边缘", MinX = edgeMargin, MaxX = canvasWidth - edgeMargin,
                  MinY = canvasHeight * 0.75, MaxY = canvasHeight - edgeMargin },
            new { Name = "左边缘", MinX = edgeMargin, MaxX = canvasWidth * 0.25,
                  MinY = edgeMargin, MaxY = canvasHeight - edgeMargin },
            new { Name = "右边缘", MinX = canvasWidth * 0.75, MaxX = canvasWidth - edgeMargin,
                  MinY = edgeMargin, MaxY = canvasHeight - edgeMargin }
        };

                // 随机尝试不同边缘区域
                var shuffledAreas = edgeAreas.OrderBy(_ => random.Next()).ToArray();

                foreach (var area in shuffledAreas)
                {
                    for (int attempt = 0; attempt < maxAttempts; attempt++)
                    {
                        var testPosition = new Point(
                            random.Next((int)area.MinX, (int)area.MaxX),
                            random.Next((int)area.MinY, (int)area.MaxY)
                        );

                        if (!IsPositionOccupiedWithSpacing(testPosition, deviceSpacing))
                        {
                            Debug.WriteLine($" 在{area.Name}找到空位: ({testPosition.X:F0}, {testPosition.Y:F0})");
                            return testPosition;
                        }
                    }
                }

                // 如果还是没找到，返回一个安全的随机位置
                Debug.WriteLine(" 边缘区域也满了，使用随机位置");
                return new Point(
                    random.Next(100, (int)canvasWidth - 100),
                    random.Next(100, (int)canvasHeight - 100)
                );
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"边缘位置查找失败: {ex.Message}");
                var random = new Random();
                return new Point(
                    random.Next(100, 1100),
                    random.Next(100, 700)
                );
            }
        }

        // ========== 新增：检查位置是否被占用（带间距）==========
        private bool IsPositionOccupiedWithSpacing(Point position, double minSpacing = 100.0)
        {
            try
            {
                if (_viewModel?.CanvasDevices == null)
                {
                    return false;
                }

                foreach (var existingDevice in _viewModel.CanvasDevices)
                {
                    // 计算距离
                    double dx = position.X - existingDevice.Position.X;
                    double dy = position.Y - existingDevice.Position.Y;
                    double distance = Math.Sqrt(dx * dx + dy * dy);

                    if (distance < minSpacing)
                    {
                        // 位置太近，视为占用
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"检查位置占用失败: {ex.Message}");
                return false;
            }
        }

        // ========== 新增：语音设置设备参数处理 ==========
        private void OnVoiceSetDeviceParameter(VoiceSetParameterEventArgs args)
        {
            try
            {
                Debug.WriteLine($"========== 语音设置设备参数 ==========");
                Debug.WriteLine($"设备类型: {args.DeviceType}");
                Debug.WriteLine($"参数名称: {args.ParameterName}");
                Debug.WriteLine($"参数值: {args.ParameterValue}");
                Debug.WriteLine($"语音命令: {args.Command}");

                // 1. 查找画布上第一个匹配类型的设备
                var targetDevice = FindDeviceByType(args.DeviceType);
                if (targetDevice == null)
                {
                    Debug.WriteLine($"画布上没有 {args.DeviceType}");
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $"画布上没有 {args.DeviceType}");
                    return;
                }

                Debug.WriteLine($"找到目标设备: {targetDevice.DeviceId}");

                // 2. 根据设备类型调用对应的设置方法
                if (args.DeviceType == "柱塞泵")
                {
                    SetPumpParameter(targetDevice, args);
                }
                else if (args.DeviceType == "流量计")
                {
                    SetFlowMeterParameter(targetDevice, args);
                }
                else if (args.DeviceType == "换热器")
                {
                    SetHeatExchangerParameter(targetDevice, args);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"语音设置设备参数失败: {ex.Message}");
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("设置参数失败");
            }
        }

        // ========== 新增：设置柱塞泵参数 ==========
        private async void SetPumpParameter(CanvasDeviceViewModel canvasDevice, VoiceSetParameterEventArgs args)
        {
            try
            {
                if (canvasDevice.Device is not IDeviceUIInteraction uiInteraction)
                {
                    Debug.WriteLine("设备不支持UI交互");
                    return;
                }

                // 查找柱塞泵控件
                var deviceElement = FindDeviceElementInCanvas(canvasDevice.DeviceId);
                var pumpControl = FindVisualChild<PistonPumpControl>(deviceElement);

                if (pumpControl == null)
                {
                    Debug.WriteLine("未找到柱塞泵控件");
                    return;
                }

                // 显示加载动画
                pumpControl.ShowLoading();
                
                // 构造参数字典
                var parameters = new Dictionary<string, object>
                {
                    [args.ParameterName] = args.ParameterValue
                };
                pumpControl.PumpSpeed = (int)args.ParameterValue;
                Debug.WriteLine($" 调用设备命令: 设置泵速, 参数={args.ParameterValue}");

                // 调用设备驱动
                var result = await uiInteraction.ExecuteUICommandAsync("设置泵速", parameters);
                
                pumpControl.HideLoading();

                if (result.Success)
                {
                    Debug.WriteLine($"参数设置成功");
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $"柱塞泵流量已设置为 {args.ParameterValue}");
                }
                else
                {
                    Debug.WriteLine($"参数设置失败: {result.Message}");
                    pumpControl.ShowOperationFailure();
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $"设置失败: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"设置柱塞泵参数失败: {ex.Message}");
            }
        }

        // ========== 新增：设置流量计参数 ==========
        private async void SetFlowMeterParameter(CanvasDeviceViewModel canvasDevice, VoiceSetParameterEventArgs args)
        {
            try
            {
                if (canvasDevice.Device is not IDeviceUIInteraction uiInteraction)
                {
                    Debug.WriteLine("设备不支持UI交互");
                    return;
                }

                var deviceElement = FindDeviceElementInCanvas(canvasDevice.DeviceId);
                var meterControl = FindVisualChild<MassFlowMeterControl>(deviceElement);

                if (meterControl == null)
                {
                    Debug.WriteLine("未找到流量计控件");
                    return;
                }

                meterControl.ShowLoading();

                // 获取当前密度
                double density = 1.0;
                var currentState = uiInteraction.GetCurrentState();
                if (currentState?.States?.ContainsKey("Density") == true)
                {
                    density = Convert.ToDouble(currentState.States["Density"]);
                }

                var parameters = new Dictionary<string, object>
                {
                    ["TargetFlow"] = args.ParameterValue,
                    ["Density"] = density,
                    ["EnableAutoControl"] = true
                };

                var result = await uiInteraction.ExecuteUICommandAsync("设置基准流量", parameters);

                meterControl.HideLoading();

                if (result.Success)
                {
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $"流量计目标流量已设置为 {args.ParameterValue}");
                }
                else
                {
                    meterControl.ShowOperationFailure();
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $"设置失败: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"设置流量计参数失败: {ex.Message}");
            }
        }

        // ========== 新增：设置换热器参数 ==========
        private async void SetHeatExchangerParameter(CanvasDeviceViewModel canvasDevice, VoiceSetParameterEventArgs args)
        {
            try
            {
                if (canvasDevice.Device is not IDeviceUIInteraction uiInteraction)
                {
                    Debug.WriteLine("设备不支持UI交互");
                    return;
                }

                var deviceElement = FindDeviceElementInCanvas(canvasDevice.DeviceId);
                var exchangerControl = FindVisualChild<HeatExchangerControl>(deviceElement);

                if (exchangerControl == null)
                {
                    Debug.WriteLine("未找到换热器控件");
                    return;
                }

                var parameters = new Dictionary<string, object>
                {
                    [args.ParameterName] = args.ParameterValue
                };

                // 假设换热器有 "设置温度" 命令
                var result = await uiInteraction.ExecuteUICommandAsync("设置温度", parameters);

                if (result.Success)
                {
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $"换热器温度已设置为 {args.ParameterValue}°C");
                }
                else
                {
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $"设置失败: {result.Message}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"设置换热器参数失败: {ex.Message}");
            }
        }

        // ========== 新增：语音控制设备状态 ==========
        private void OnVoiceControlDeviceState(VoiceControlStateEventArgs args)
        {
            try
            {
                Debug.WriteLine($"========== 语音控制设备状态 ==========");
                Debug.WriteLine($"设备类型: {args.DeviceType}");
                Debug.WriteLine($"目标状态: {args.TargetState}");

                var targetDevice = FindDeviceByType(args.DeviceType);
                if (targetDevice == null)
                {
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                        $"画布上没有 {args.DeviceType}");
                    return;
                }

                if (args.DeviceType == "柱塞泵")
                {
                    ControlPumpState(targetDevice, args.TargetState);
                }
                else if (args.DeviceType == "电磁阀")
                {
                    ControlSolenoidValveState(targetDevice, args.TargetState);
                }
                else if (args.DeviceType == "流量计")
                {
                    ControlFlowMeterState(targetDevice, args.TargetState);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"语音控制设备状态失败: {ex.Message}");
            }
        }

        // ========== 新增：控制柱塞泵状态 ==========
        private async void ControlPumpState(CanvasDeviceViewModel canvasDevice, bool targetState)
        {
            try
            {
                if (canvasDevice.Device is not IDeviceUIInteraction uiInteraction)
                {
                    return;
                }

                var deviceElement = FindDeviceElementInCanvas(canvasDevice.DeviceId);
                var pumpControl = FindVisualChild<PistonPumpControl>(deviceElement);

                if (pumpControl != null)
                {
                    pumpControl.ShowLoading();

                    string commandName = targetState ? "启动泵" : "停止泵";
                    var result = await uiInteraction.ExecuteUICommandAsync(commandName);

                    pumpControl.HideLoading();

                    if (result.Success)
                    {
                        pumpControl.IsRunning = targetState;
                        _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                            $"柱塞泵已{(targetState ? "启动" : "停止")}");
                    }
                    else
                    {
                        pumpControl.ShowOperationFailure();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"控制柱塞泵状态失败: {ex.Message}");
            }
        }

        // ========== 新增：控制电磁阀状态 ==========
        private async void ControlSolenoidValveState(CanvasDeviceViewModel canvasDevice, bool targetState)
        {
            try
            {
                if (canvasDevice.Device is not IDeviceUIInteraction uiInteraction)
                {
                    return;
                }

                var deviceElement = FindDeviceElementInCanvas(canvasDevice.DeviceId);
                var solenoidValve = FindVisualChild<SolenoidValveControl>(deviceElement);

                if (solenoidValve != null)
                {
                    solenoidValve.ShowLoading();

                    string commandName = targetState ? "打开电磁阀" : "关闭电磁阀";
                    var result = await uiInteraction.ExecuteUICommandAsync(commandName);

                    solenoidValve.HideLoading();

                    if (result.Success)
                    {
                        solenoidValve.IsOpen = targetState;
                        _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                            $"电磁阀已{(targetState ? "打开" : "关闭")}");
                    }
                    else
                    {
                        solenoidValve.ShowOperationFailure();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"控制电磁阀状态失败: {ex.Message}");
            }
        }

        // ========== 新增：控制流量计状态 ==========
        private async void ControlFlowMeterState(CanvasDeviceViewModel canvasDevice, bool targetState)
        {
            try
            {
                if (canvasDevice.Device is not IDeviceUIInteraction uiInteraction)
                {
                    return;
                }

                var deviceElement = FindDeviceElementInCanvas(canvasDevice.DeviceId);
                var meterControl = FindVisualChild<MassFlowMeterControl>(deviceElement);

                if (meterControl != null)
                {
                    meterControl.ShowLoading();

                    string commandName;
                    Dictionary<string, object> parameters = null;

                    if (targetState)
                    {
                        commandName = "设置基准流量";
                        parameters = new Dictionary<string, object>
                        {
                            ["TargetFlow"] = meterControl.TargetFlow,
                            ["Density"] = 1.0,
                            ["EnableAutoControl"] = true
                        };
                    }
                    else
                    {
                        commandName = "停止流量控制";
                    }

                    var result = await uiInteraction.ExecuteUICommandAsync(commandName, parameters);

                    meterControl.HideLoading();

                    if (result.Success)
                    {
                        _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(
                            $"流量计已{(targetState ? "启动" : "停止")}");
                    }
                    else
                    {
                        meterControl.ShowOperationFailure();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"控制流量计状态失败: {ex.Message}");
            }
        }

        // ========== 新增：根据设备类型查找设备 ==========
        private CanvasDeviceViewModel FindDeviceByType(string deviceType)
        {
            try
            {
                // 支持模糊匹配
                var keywords = new Dictionary<string, string[]>
                {
                    ["柱塞泵"] = new[] { "柱塞泵", "Piston", "Pump" },
                    ["流量计"] = new[] { "流量计", "FlowMeter", "Flow" },
                    ["电磁阀"] = new[] { "电磁阀", "Solenoid", "Valve" },
                    ["换热器"] = new[] { "换热器", "HeatExchanger", "Exchanger" }
                };

                if (!keywords.TryGetValue(deviceType, out var searchKeys))
                {
                    searchKeys = new[] { deviceType };
                }

                // 先尝试精确匹配
                var exactMatch = _viewModel?.CanvasDevices?.FirstOrDefault(d =>
                    searchKeys.Any(key => d.Device?.Name?.Contains(key) == true));

                if (exactMatch != null)
                {
                    Debug.WriteLine($"精确匹配到设备: {exactMatch.Device.Name}");
                    return exactMatch;
                }

                // 尝试类型名称匹配
                var typeMatch = _viewModel?.CanvasDevices?.FirstOrDefault(d =>
                    searchKeys.Any(key => d.Device?.GetType().Name.Contains(key) == true));

                if (typeMatch != null)
                {
                    Debug.WriteLine($"类型匹配到设备: {typeMatch.Device.GetType().Name}");
                }

                return typeMatch;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"查找设备失败: {ex.Message}");
                return null;
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
                    Debug.WriteLine("设备管理器中没有设备");
                    return null;
                }

                // 模糊匹配设备名称
                var device = allDevices.FirstOrDefault(d =>
                    d.Name?.Contains(deviceName) == true ||
                    deviceName.Contains(d.Name));

                if (device != null)
                {
                    Debug.WriteLine($" 找到设备: {device.Name} ({device.GetType().Name})");
                }

                return device;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"查找设备失败: {ex.Message}");
                return null;
            }
        }

        // ========== 保留原有方法（用于向后兼容）==========
        /// <summary>
        /// 计算画布中心位置（已废弃，保留用于兼容）
        /// </summary>
        [Obsolete("请使用 CalculateSmartPosition() 代替")]
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
                    Debug.WriteLine(" 创建设备视图模型失败");
                    return;
                }

                // 使用命令创建设备（支持撤销重做）
                var addCommand = new AddDeviceCommand(_viewModel, canvasDevice);
                _commandHistory.ExecuteCommand(addCommand);

                Debug.WriteLine($" 设备创建完成: {device.Name}, ID: {canvasDevice.DeviceId}");

                // 标记画布已修改
                _eventAggregator?.GetEvent<CanvasModifiedEvent>()?.Publish();

                //  选中新创建的设备
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    OnDeviceSelected(canvasDevice.DeviceId);
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($" 创建设备失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 查找附近的空位置（优化版）
        /// </summary>
        private Point FindNearbyEmptyPosition(Point originalPosition)
        {
            const int maxRadius = 200;       // 最大搜索半径
            const int radiusStep = 30;       // 半径步进
            const double minSpacing = 120.0; // 最小间距

            try
            {
                // 先尝试螺旋搜索
                var spiralPosition = FindEmptyPositionSpiral(originalPosition, maxRadius, radiusStep);
                if (spiralPosition != null)
                {
                    return spiralPosition.Value;
                }

                // 如果螺旋搜索失败，使用网格搜索
                Debug.WriteLine(" 螺旋搜索失败，尝试网格搜索");

                for (int radius = radiusStep; radius <= maxRadius; radius += radiusStep)
                {
                    for (int angle = 0; angle < 360; angle += 45)
                    {
                        var radians = angle * Math.PI / 180;
                        var testPosition = new Point(
                            originalPosition.X + radius * Math.Cos(radians),
                            originalPosition.Y + radius * Math.Sin(radians)
                        );

                        if (!IsPositionOccupiedWithSpacing(testPosition, minSpacing) &&
                            IsValidDropPosition(testPosition))
                        {
                            Debug.WriteLine($" 网格搜索找到空位: ({testPosition.X:F0}, {testPosition.Y:F0})");
                            return testPosition;
                        }
                    }
                }

                // 如果还是没找到，使用边缘区域
                Debug.WriteLine(" 网格搜索也失败，使用边缘区域");
                return FindEmptyPositionInEdgeArea();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"查找附近空位置失败: {ex.Message}");

                // 最终降级方案：完全随机
                var random = new Random();
                return new Point(
                    originalPosition.X + random.Next(-200, 200),
                    originalPosition.Y + random.Next(-200, 200)
                );
            }
        }


        #endregion

        private void DesignCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize == e.PreviousSize)
            {
                return;
            }

            var canvas = e.Source as Canvas;
            if (canvas == null || !canvas.Equals(DesignCanvas))
            {
                return;
            }

            if (e.WidthChanged)
            {
                DesignCanvas.Width = Math.Max(e.NewSize.Width, CanvasScrollViewer.ActualWidth);
            }

            if (e.HeightChanged)
            {
                DesignCanvas.Height = Math.Max(e.NewSize.Height, CanvasScrollViewer.ActualHeight);
            }

            e.Handled = true;
        }
    }
}