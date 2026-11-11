using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MaxChemical.Data.Models;
using MaxChemical.Data.Repositories;
using MaxChemical.Data.Services;
using MaxChemical.Modules.Lab3D.Models;
using MaxChemical.Modules.Lab3D.Services;
using Microsoft.Web.WebView2.Core;

namespace MaxChemical.Modules.Lab3D.Views;

public partial class Lab3DWindow : Window, INotifyPropertyChanged
{
    private readonly ILab3DRealtimeService _realtimeService;
    private readonly IExperimentDataRepository _experimentRepository;
    private readonly IExperimentDataCollectionService _experimentDataCollection;
    private readonly ILab3DInspectionService _inspectionService;
    private readonly DispatcherTimer _dashboardTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _foregroundTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private ExperimentStatistics? _statistics;
    private ExperimentStatistics? _todayStatistics;
    private DeviceUsageStatistics? _selectedDeviceUsage;
    private Lab3DInspectionSnapshot _inspection = Lab3DInspectionSnapshot.Empty;
    private DateTime _overviewUpdatedAt = DateTime.Now;
    private double _trendAxisMaximum = 4;
    private ExperimentRecord? _activeExperiment;
    private bool _dashboardRefreshing;
    private bool _dashboardRefreshQueued;
    private int _selectionVersion;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _wwwroot = Path.Combine(AppContext.BaseDirectory, "Lab3D", "wwwroot");
    private readonly DeviceLibraryService _deviceLibrary = new();
    private readonly DeletedDeviceStore _deletedDevices = new();
    private readonly Lab3DOverlayWindow SidePanelsPopup;
    private ContentControl SidePanelsPopupRoot => SidePanelsPopup.Root;
    private Grid SidePanelsPopupGrid => SidePanelsPopup.PanelGrid;
    private const double SelectionDurationMs = 460;
    private LabDevice? _selectedDevice;
    private bool _sceneReady;
    private bool _initializing;
    private bool _isDeleting;
    private bool _isEditing;
    private bool _isClosed;
    private bool _isSceneFullscreen;
    private bool _sidePanelsOverlayInitialized;
    private WindowState _windowStateBeforeSceneFullscreen = WindowState.Normal;
    private double _selectionFromX;
    private double _selectionToX;
    private double _selectionFromWidth;
    private double _selectionToWidth;
    private double _selectionFromRight;
    private double _selectionToRight;
    private TimeSpan _selectionStarted;
    private bool _selectionAnimating;
    private bool _selectionInitialized;
    private LabDevice? _selectionAnchorDevice;
    private Image? _selectionImage;
    private bool _touchTracking;
    private bool _touchGestureActive;
    private Point _touchStart;
    private Point _touchLast;
    private DateTime _touchLastTime;
    private double _touchVelocityX;
    private int _touchStartIndex = -1;
    private int _touchTargetIndex = -1;
    private double _touchFromX;
    private double _touchFromWidth;
    private double _touchFromRight;
    private double _touchTargetX;
    private double _touchTargetRight;
    private DateTime _suppressDeviceMouseUntil;

    public static Lab3DWindow? Current { get; private set; }

    public ObservableCollection<LabDevice> Devices { get; } = new();
    public ObservableCollection<string> Events { get; } = new();
    public string OnlineSummary => $"{Devices.Count(d => d.IsOnline)} / {Devices.Count} ONLINE";
    public string DeviceCountText => $"DEVICES · {Devices.Count:00}";
    public int DeviceRowCount => Math.Max(1, Devices.Count);
    public string OnlineDeviceCountText => Devices.Count(d => d.IsOnline).ToString();
    public ObservableCollection<Lab3DTrendItem> TrendItems { get; } = new();
    public ObservableCollection<Lab3DHistoryItem> RecentExperiments { get; } = new();
    public ObservableCollection<Lab3DHistoryItem> SelectedDeviceExperiments { get; } = new();
    public string OverviewUpdatedText => $"更新于 {_overviewUpdatedAt:HH:mm}";
    public string ExperimentCountText => _statistics?.TotalExperiments.ToString("N0") ?? "—";
    public string TodayExperimentCountText => _todayStatistics?.TotalExperiments.ToString("N0") ?? "—";
    public string ExperimentSuccessRateText => _statistics?.TotalExperiments > 0 ? $"{_statistics.SuccessRate:F1}%" : "--";
    public Geometry ExperimentSuccessArc => CreateSuccessArc(_statistics?.TotalExperiments > 0 ? _statistics.SuccessRate : 0);
    public string TrendAxisTopText => _trendAxisMaximum.ToString("0.#");
    public string TrendAxisUpperText => (_trendAxisMaximum * 0.75).ToString("0.#");
    public string TrendAxisMiddleText => (_trendAxisMaximum * 0.5).ToString("0.#");
    public string TrendAxisLowerText => (_trendAxisMaximum * 0.25).ToString("0.#");
    public string SelectedExperimentCountText => _selectedDeviceUsage?.UsageCount.ToString() ?? "—";
    public string SelectedSuccessRateText => _selectedDeviceUsage?.UsageCount > 0 ? $"{_selectedDeviceUsage.SuccessRate:F1}%" : "--";
    public Lab3DInspectionSnapshot Inspection
    {
        get => _inspection;
        private set
        {
            _inspection = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(InspectionTargetText));
            OnPropertyChanged(nameof(InspectionDataCoverageText));
            OnPropertyChanged(nameof(InspectionSuccessRateText));
            OnPropertyChanged(nameof(InspectionTimeText));
            OnPropertyChanged(nameof(InspectionAnalysisText));
            OnPropertyChanged(nameof(InspectionNoticeCountText));
        }
    }
    public string InspectionTargetText => string.IsNullOrEmpty(Inspection.TargetId) ? Inspection.TargetName : $"{Inspection.TargetId} · {Inspection.TargetName}";
    public string InspectionDataCoverageText => Inspection.DataCoverage.HasValue ? $"{Inspection.DataCoverage:F0}%" : "--";
    public string InspectionSuccessRateText => Inspection.ExperimentSuccessRate.HasValue ? $"{Inspection.ExperimentSuccessRate:F1}%" : "--";
    public string InspectionTimeText => $"本地规则引擎 · 更新于 {Inspection.UpdatedAt:HH:mm:ss}";
    public string InspectionAnalysisText => Inspection.TargetStatus switch
    {
        "等待绑定" => "设备绑定检查",
        "等待连接" => "离线影响评估",
        "等待数据" => "数据完整性检查",
        _ => Inspection.Task
    };
    public string InspectionNoticeCountText => $"当前 {Inspection.Notices.Count} 条";
    private ExperimentRecord? DisplayedExperiment => _activeExperiment;
    public string ActiveExperimentName => DisplayedExperiment?.ExperimentName ?? "暂无进行中实验";
    public string ActiveExperimentStatus => DisplayedExperiment is null ? "等待实验开始" : $"{DisplayedExperiment.CompletedNodes} / {DisplayedExperiment.TotalNodes} 个节点 · {DisplayedExperiment.ModeText}";
    public double ActiveExperimentProgress => DisplayedExperiment?.ProgressPercentage ?? 0;
    public string ActiveExperimentProgressText => DisplayedExperiment is null ? "--" : $"{DisplayedExperiment.ProgressPercentage:F0}%";
    public string SelectedHistoryEmptyText => SelectedDevice is null ? "暂无相关实验记录" : $"{SelectedDevice.Name} 暂无相关实验记录";
    public bool HasSelectedHistory => SelectedDeviceExperiments.Count > 0;
    public bool HasNoSelectedHistory => SelectedDeviceExperiments.Count == 0;
    public bool HasRecentHistory => RecentExperiments.Count > 0;
    public bool HasNoRecentHistory => RecentExperiments.Count == 0;
    public bool HasTrend => TrendItems.Count > 0;
    public bool HasNoTrend => TrendItems.Count == 0;
    public Visibility SelectedHistoryEmptyVisibility => HasNoSelectedHistory ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SelectedHistoryListVisibility => HasSelectedHistory ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RecentHistoryEmptyVisibility => HasNoRecentHistory ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RecentHistoryListVisibility => HasRecentHistory ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TrendEmptyVisibility => HasNoTrend ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TrendItemsVisibility => HasTrend ? Visibility.Visible : Visibility.Collapsed;
    public string SceneModeTitle => "实验室总览";

    public LabDevice? SelectedDevice
    {
        get => _selectedDevice;
        private set
        {
            _selectedDevice = value;
            _selectionVersion++;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedHistoryEmptyText));
            if (IsLoaded)
            {
                RightPanelScrollViewer.ScrollToTop();
                QueueDeviceSelectionAnimation();
                _dashboardRefreshQueued = true;
                _ = RefreshDashboardAsync();
            }
        }
    }

    public Lab3DWindow(
        ILab3DRealtimeService realtimeService,
        IExperimentDataRepository experimentRepository,
        IExperimentDataCollectionService experimentDataCollection,
        ILab3DInspectionService inspectionService)
    {
        _realtimeService = realtimeService ?? throw new ArgumentNullException(nameof(realtimeService));
        _experimentRepository = experimentRepository ?? throw new ArgumentNullException(nameof(experimentRepository));
        _experimentDataCollection = experimentDataCollection ?? throw new ArgumentNullException(nameof(experimentDataCollection));
        _inspectionService = inspectionService ?? throw new ArgumentNullException(nameof(inspectionService));

        Current = this;
        InitializeComponent();
        SidePanelsPopup = new Lab3DOverlayWindow();
        CreateDevices();
        LoadCustomDevices();
        SelectedDevice = Devices.Count > 0 ? Devices[Random.Shared.Next(Devices.Count)] : null;
        if (SelectedDevice is not null) SelectedDevice.IsSelected = true;
        DataContext = this;

        AddEvent("高保真数字孪生引擎正在启动");
        AddEvent("正在等待主程序设备数据");

        _realtimeService.SnapshotChanged += OnRealtimeSnapshotChanged;
        _realtimeService.PublishSnapshot();
        _inspectionService.Changed += OnInspectionChanged;
        _inspectionService.SetDevices(Devices);
        _realtimeService.SetDevices(Devices);
        _inspectionService.SelectDevice(SelectedDevice);
        _inspectionService.SetActive(true);

        _dashboardTimer.Tick += async (_, _) => await RefreshDashboardAsync();
        _dashboardTimer.Start();
        Loaded += (_, _) => QueueDeviceSelectionAnimation(true);
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() => RightPanelScrollViewer.ScrollToTop()), DispatcherPriority.Loaded);
        Loaded += (_, _) => InitializeSidePanelsOverlay();
        Loaded += async (_, _) => await InitializeWebViewAsync();
        Loaded += async (_, _) => await RefreshDashboardAsync();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized)
            {
                SidePanelsPopup.IsOpen = false;
                BrainView.SetAnimationActive(false);
            }
            else if (!_isSceneFullscreen && _sidePanelsOverlayInitialized)
            {
                SidePanelsPopup.IsOpen = true;
                BrainView.SetAnimationActive(true);
            }
        };
        Activated += (_, _) =>
        {
            if (_isClosed || _isSceneFullscreen || WindowState == WindowState.Minimized || !_sidePanelsOverlayInitialized) return;
            if (IsOtherApplicationForeground())
            {
                SidePanelsPopup.IsOpen = false;
                BrainView.SetAnimationActive(false);
                return;
            }
            SidePanelsPopup.IsOpen = true;
            BrainView.SetAnimationActive(true);
        };
        Deactivated += (_, _) =>
        {
            if (_isClosed) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_isClosed || !IsOtherApplicationForeground()) return;
                SidePanelsPopup.IsOpen = false;
                BrainView.SetAnimationActive(false);
            }), DispatcherPriority.Background);
        };
        _foregroundTimer.Tick += (_, _) =>
        {
            if (_isClosed || !_sidePanelsOverlayInitialized || !IsOtherApplicationForeground()) return;
            SidePanelsPopup.IsOpen = false;
            BrainView.SetAnimationActive(false);
        };
        _foregroundTimer.Start();
        LocationChanged += (_, _) => UpdateSidePanelsOverlayPosition();
        SceneViewport.SizeChanged += (_, _) =>
        {
            UpdateSidePanelsOverlaySize();
            Dispatcher.BeginInvoke(new Action(FitSidePanelsContent), DispatcherPriority.Loaded);
        };
        SidePanelsPopup.IsVisibleChanged += (_, _) =>
        {
            if (SidePanelsPopup.IsOpen)
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateSidePanelsOverlayPosition();
                    FitSidePanelsContent();
                }), DispatcherPriority.Render);
        };
        Closed += (_, _) =>
        {
            _isClosed = true;
            if (Current == this) Current = null;
            _foregroundTimer.Stop();
            SidePanelsPopup.IsOpen = false;
            BrainView.SetAnimationActive(false);
            if (_selectionAnimating) CompositionTarget.Rendering -= DeviceSelectionRendering;
            _dashboardTimer.Stop();
            _realtimeService.SnapshotChanged -= OnRealtimeSnapshotChanged;
            _inspectionService.SetActive(false);
            _inspectionService.Changed -= OnInspectionChanged;
            if (LabWebView.CoreWebView2 is not null)
            {
                LabWebView.CoreWebView2.WebMessageReceived -= WebMessageReceived;
                LabWebView.CoreWebView2.NavigationCompleted -= NavigationCompleted;
            }
            LabWebView.Dispose();
        };
    }

    private void InitializeSidePanelsOverlay()
    {
        if (_sidePanelsOverlayInitialized) return;
        ContentGrid.Children.Remove(LeftPanel);
        ContentGrid.Children.Remove(RightPanel);
        SidePanelsPopupRoot.DataContext = DataContext;
        Grid.SetColumn(LeftPanel, 0);
        Grid.SetColumn(RightPanel, 2);
        SidePanelsPopupGrid.Children.Add(LeftPanel);
        SidePanelsPopupGrid.Children.Add(RightPanel);
        _sidePanelsOverlayInitialized = true;
        SidePanelsPopup.Owner = this;
        UpdateSidePanelsOverlaySize();
        SidePanelsPopup.IsOpen = true;
        BrainView.SetAnimationActive(true);
        Dispatcher.BeginInvoke(new Action(FitSidePanelsContent), DispatcherPriority.Loaded);
    }

    private void FitSidePanelsContent()
    {
        if (!_sidePanelsOverlayInitialized) return;
        FitPanelContent(LeftPanelScrollViewer, LeftPanelContent, preserveWidth: true);
        FitPanelContent(RightPanelScrollViewer, RightPanelContent, preserveWidth: true);
    }

    private static void FitPanelContent(ScrollViewer scrollViewer, Grid content, bool preserveWidth = false)
    {
        if (scrollViewer.ViewportWidth <= 0 || scrollViewer.ViewportHeight <= 0) return;
        content.LayoutTransform = Transform.Identity;
        content.MinHeight = 0;
        content.Height = double.NaN;
        content.Measure(new Size(scrollViewer.ViewportWidth, double.PositiveInfinity));
        double availableHeight = scrollViewer.ViewportHeight;
        double contentHeight = content.DesiredSize.Height;
        if (contentHeight <= availableHeight)
        {
            content.MinHeight = availableHeight;
            content.Height = availableHeight;
            content.LayoutTransform = Transform.Identity;
        }
        else
        {
            content.MinHeight = 0;
            content.Height = contentHeight;
            content.LayoutTransform = preserveWidth
                ? Transform.Identity
                : new ScaleTransform(availableHeight / contentHeight, availableHeight / contentHeight, 0, 0);
        }
    }

    private void UpdateSidePanelWidths()
    {
        if (_isSceneFullscreen)
        {
            SceneModeBorder.Margin = new Thickness(12);
            return;
        }

        bool maximized = WindowState == WindowState.Maximized;
        double leftWidth = maximized ? 280 : 240;
        double rightWidth = maximized ? 300 : 244;
        ContentGrid.ColumnDefinitions[0].Width = new GridLength(leftWidth);
        RightPanelColumn.Width = new GridLength(rightWidth);
        SidePanelsPopupGrid.ColumnDefinitions[0].Width = new GridLength(leftWidth);
        SidePanelsPopupGrid.ColumnDefinitions[2].Width = new GridLength(rightWidth);
        SceneModeBorder.Margin = new Thickness(leftWidth + 12, 12, 12, 12);
        SendToScene("scene-panels-width-changed", new { left = leftWidth, right = rightWidth });
    }

    private void UpdateSidePanelsOverlaySize()
    {
        if (!_sidePanelsOverlayInitialized) return;
        UpdateSidePanelWidths();
        double width = Math.Max(1, SceneViewport.ActualWidth);
        double height = Math.Max(1, SceneViewport.ActualHeight);
        SidePanelsPopup.Width = width;
        SidePanelsPopup.Height = height;
        SidePanelsPopupRoot.Width = width;
        SidePanelsPopupRoot.Height = height;
        UpdateSidePanelsOverlayPosition();
    }

    private void UpdateSidePanelsOverlayPosition()
    {
        if (!_sidePanelsOverlayInitialized || !SidePanelsPopup.IsOpen) return;
        var popupSource = HwndSource.FromVisual(SidePanelsPopupRoot) as HwndSource;
        var ownerSource = HwndSource.FromVisual(this) as HwndSource;
        if (popupSource?.CompositionTarget is null || ownerSource is null || popupSource.Handle == ownerSource.Handle) return;

        var origin = SceneViewport.PointToScreen(new Point());
        var transform = popupSource.CompositionTarget.TransformToDevice;
        int width = Math.Max(1, (int)Math.Round(SceneViewport.ActualWidth * transform.M11));
        int height = Math.Max(1, (int)Math.Round(SceneViewport.ActualHeight * transform.M22));
        SetWindowPos(popupSource.Handle, IntPtr.Zero, (int)Math.Round(origin.X), (int)Math.Round(origin.Y),
            width, height, 0x0004 | 0x0010 | 0x0200);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    private async Task InitializeWebViewAsync()
    {
        if (_initializing) return;
        _initializing = true;
        try
        {
            if (!Directory.Exists(_wwwroot))
                throw new DirectoryNotFoundException($"找不到本地 3D 资源目录：{_wwwroot}");

            if (LabWebView.CoreWebView2 is null)
            {
                string userData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MaxChemical", "Lab3D", "WebView2");
                var options = new CoreWebView2EnvironmentOptions
                {
                    AdditionalBrowserArguments = "--enable-gpu-rasterization --enable-zero-copy"
                };
                var environment = await CoreWebView2Environment.CreateAsync(null, userData, options);
                await LabWebView.EnsureCoreWebView2Async(environment);
                LabWebView.CoreWebView2!.SetVirtualHostNameToFolderMapping(
                    "lab.nexus", _wwwroot, CoreWebView2HostResourceAccessKind.Allow);
                LabWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "devices.local", _deviceLibrary.StorageDirectory, CoreWebView2HostResourceAccessKind.Allow);
                LabWebView.CoreWebView2.WebMessageReceived += WebMessageReceived;
                LabWebView.CoreWebView2.NavigationCompleted += NavigationCompleted;
                LabWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                LabWebView.CoreWebView2.Settings.AreDevToolsEnabled = true;
                LabWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                LabWebView.CoreWebView2.Settings.IsZoomControlEnabled = false;
            }

            LabLoadingTitle.Text = "正在构建高保真实验室";
            LabLoadingText.Text = "霍桐仪器";
            LabWebView.CoreWebView2.Navigate("https://lab.nexus/index.html");
        }
        catch (Exception ex)
        {
            ShowSceneError(ex);
        }
        finally
        {
            _initializing = false;
        }
    }

    private void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            LabLoadingTitle.Text = "3D 页面加载失败";
            LabLoadingText.Text = $"WebView2 错误：{e.WebErrorStatus}";
            LabLoadingProgress.IsIndeterminate = false;
            LabRetryButton.Visibility = Visibility.Visible;
        }
    }

    private void WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            string type = root.GetProperty("type").GetString() ?? string.Empty;
            switch (type)
            {
                case "scene-ready":
                    _sceneReady = false;
                    SendToScene("initialize", new
                    {
                        devices = Devices.Select(CreateDevicePayload),
                        selectedId = SelectedDevice?.Id,
                        inspectionId = Inspection.TargetId,
                        inspectionTask = Inspection.Task
                    });
                    break;
                case "scene-loading-progress":
                    LabLoadingText.Text = root.GetProperty("stage").GetString() switch
                    {
                        "models" => "正在加载设备图片与模型",
                        "compile" => "霍桐仪器",
                        "first-frame" => "正在呈现首个 3D 画面",
                        _ => "正在初始化 3D 场景"
                    };
                    break;
                case "scene-initialized":
                    _sceneReady = true;
                    UpdateSidePanelWidths();
                    LabWebView.Visibility = Visibility.Visible;
                    LabLoading.Visibility = Visibility.Collapsed;
                    AddEvent("Three.js 高保真场景加载完成");
                    SendToScene("scene-fullscreen-changed", new { enabled = _isSceneFullscreen });
                    SendTelemetry();
                    SendInspectionFocus();
                    break;
                case "toggle-scene-fullscreen":
                    SetSceneFullscreen(!_isSceneFullscreen);
                    break;
                case "exit-scene-fullscreen":
                    SetSceneFullscreen(false);
                    break;
                case "device-selected":
                    string id = root.GetProperty("id").GetString() ?? string.Empty;
                    var device = Devices.FirstOrDefault(d => d.Id == id);
                    if (device is not null)
                    {
                        SelectDevice(device, false, false);
                        AddEvent($"3D 场景选中 {device.Id} {device.Name}");
                    }
                    break;
                case "device-context-request":
                    string deleteId = root.GetProperty("id").GetString() ?? string.Empty;
                    var deletedDevice = Devices.FirstOrDefault(d => d.Id == deleteId);
                    if (deletedDevice is not null)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (!_isClosed && Devices.Contains(deletedDevice)) ShowDeviceContextMenu(deletedDevice);
                        }));
                    }
                    break;
                case "scene-event":
                    AddEvent(root.GetProperty("message").GetString() ?? "场景状态已更新");
                    break;
                case "scene-error":
                    string error = root.GetProperty("message").GetString() ?? "未知 WebGL 错误";
                    AddEvent($"3D 引擎错误：{error}");
                    LabLoading.Visibility = Visibility.Visible;
                    LabLoadingTitle.Text = "3D 场景初始化失败";
                    LabLoadingText.Text = error;
                    LabLoadingText.TextWrapping = TextWrapping.Wrap;
                    LabLoadingProgress.IsIndeterminate = false;
                    LabRetryButton.Visibility = Visibility.Visible;
                    break;
            }
        }
        catch (Exception ex)
        {
            AddEvent($"场景消息处理失败：{ex.Message}");
        }
    }

    private async Task RefreshDashboardAsync()
    {
        if (_dashboardRefreshing || _isClosed) return;
        _dashboardRefreshing = true;
        _dashboardRefreshQueued = false;
        int selectionVersion = _selectionVersion;
        var selected = SelectedDevice;
        try
        {
            var now = DateTime.Now;
            var dashboard = await Task.Run(async () =>
            {
                var statistics = await _experimentRepository.GetStatisticsAsync();
                var todayStatistics = await _experimentRepository.GetStatisticsAsync(now.Date, now);
                var trend = await _experimentRepository.GetTrendDataAsync(now.AddDays(-6), now, "day");
                var recent = await _experimentRepository.GetExperimentsPagedAsync(startDate: now.AddDays(-90), endDate: now, pageSize: 6);
                var usage = selected is null
                    ? null
                    : await _experimentRepository.GetDeviceUsageStatisticsAsync();

                string? currentId = _experimentDataCollection.GetCurrentExperimentId();
                ExperimentRecord? active = currentId is not null
                    && await _experimentDataCollection.IsExperimentActiveAsync(currentId)
                    ? await _experimentRepository.GetExperimentAsync(currentId)
                    : null;

                var selectedUsage = selected is null
                    ? null
                    : usage?.FirstOrDefault(item => MatchesDevice(item, selected));
                var selectedHistory = new List<Lab3DHistoryItem>();
                if (selected is not null && selectedUsage is not null)
                {
                    foreach (var record in recent.Records.Take(5))
                    {
                        var nodes = await _experimentRepository.GetNodeRecordsAsync(record.ExperimentId);
                        if (nodes.Any(node => MatchesDevice(node.DeviceId, node.DeviceName, selected))
                            && selectedHistory.All(item => item.Title != record.ExperimentName))
                        {
                            selectedHistory.Add(CreateHistoryItem(record, active));
                        }
                    }
                }

                return (
                    Statistics: statistics,
                    TodayStatistics: todayStatistics,
                    Trend: trend,
                    Recent: recent,
                    Active: active,
                    SelectedUsage: selectedUsage,
                    SelectedHistory: selectedHistory
                );
            });

            if (_isClosed || selectionVersion != _selectionVersion)
            {
                _dashboardRefreshQueued = true;
                return;
            }

            _statistics = dashboard.Statistics;
            _todayStatistics = dashboard.TodayStatistics;
            _activeExperiment = dashboard.Active;
            _selectedDeviceUsage = dashboard.SelectedUsage;
            _overviewUpdatedAt = now;
            int maximum = dashboard.Trend.Count == 0 ? 0 : dashboard.Trend.Max(entry => entry.TotalCount);
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1, maximum))));
            double normalized = maximum / magnitude;
            double axisMaximum = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 4 ? 4 : normalized <= 5 ? 5 : normalized <= 8 ? 8 : 10;
            _trendAxisMaximum = Math.Max(4, axisMaximum * magnitude);
            var trendByDate = dashboard.Trend.GroupBy(item => item.Date.Date).ToDictionary(group => group.Key, group => group.First());
            ReplaceCollection(RecentExperiments, dashboard.Recent.Records.Take(4).Select(record => CreateHistoryItem(record, dashboard.Active)));
            ReplaceCollection(SelectedDeviceExperiments, dashboard.SelectedHistory);
            ReplaceCollection(TrendItems, Enumerable.Range(0, 7).Select(offset =>
            {
                var date = now.Date.AddDays(offset - 6);
                trendByDate.TryGetValue(date, out var item);
                int total = item?.TotalCount ?? 0;
                int successful = item?.SuccessCount ?? 0;
                int failed = total - successful;
                return new Lab3DTrendItem(
                    offset == 6 ? "今天" : date.ToString("M/d"),
                    total,
                    successful,
                    total * 88.0 / _trendAxisMaximum,
                    successful * 88.0 / _trendAxisMaximum,
                    failed * 88.0 / _trendAxisMaximum);
            }));
            OnPropertyChanged(string.Empty);
        }
        catch (Exception ex)
        {
            AddEvent($"数据面板刷新失败：{ex.Message}");
        }
        finally
        {
            _dashboardRefreshing = false;
        }

        if (_dashboardRefreshQueued) await RefreshDashboardAsync();
    }

    private static Geometry CreateSuccessArc(double percentage)
    {
        const double radius = 33.5;
        var center = new Point(36, 36);
        double normalized = Math.Clamp(percentage, 0, 100) / 100;
        if (normalized >= 1) return new EllipseGeometry(center, radius, radius);

        var geometry = new StreamGeometry();
        if (normalized > 0)
        {
            double angle = normalized * Math.PI * 2;
            double endAngle = angle - Math.PI / 2;
            var end = new Point(
                center.X + Math.Cos(endAngle) * radius,
                center.Y + Math.Sin(endAngle) * radius);
            using (var context = geometry.Open())
            {
                context.BeginFigure(new Point(center.X, center.Y - radius), false, false);
                context.ArcTo(end, new Size(radius, radius), 0, angle > Math.PI,
                    SweepDirection.Clockwise, true, false);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    private static Lab3DHistoryItem CreateHistoryItem(ExperimentRecord record, ExperimentRecord? active)
    {
        bool isActive = active?.ExperimentId == record.ExperimentId;
        return new Lab3DHistoryItem(
            record.ExperimentName,
            $"{record.StartTime:MM-dd HH:mm} · {record.FlowName}",
            isActive ? "进行中" : record.StatusText,
            isActive,
            record.IsSuccessful,
            record.StartTime);
    }

    private static bool MatchesDevice(DeviceUsageStatistics usage, LabDevice device) =>
        (device.SourceDeviceId is not null && usage.DeviceId.Equals(device.SourceDeviceId, StringComparison.OrdinalIgnoreCase))
        || usage.DeviceId.Equals(device.Id, StringComparison.OrdinalIgnoreCase)
        || usage.DeviceName.Equals(device.Name, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesDevice(string? deviceId, string? deviceName, LabDevice device) =>
        (device.SourceDeviceId is not null && deviceId?.Equals(device.SourceDeviceId, StringComparison.OrdinalIgnoreCase) == true)
        || deviceId?.Equals(device.Id, StringComparison.OrdinalIgnoreCase) == true
        || deviceName?.Equals(device.Name, StringComparison.OrdinalIgnoreCase) == true;

    private static void ReplaceCollection<T>(ObservableCollection<T> collection, IEnumerable<T> items)
    {
        collection.Clear();
        foreach (var item in items) collection.Add(item);
    }

    private static object CreateDevicePayload(LabDevice device) => new
    {
        id = device.Id,
        name = device.Name,
        englishName = device.EnglishName,
        kind = device.Kind,
        image = device.WebImagePath ?? $"assets/devices/{Path.GetFileName(device.ImagePath)}",
        imageAspect = device.ImageAspect,
        modelHeight = device.ModelHeight,
        libraryId = device.LibraryId,
        online = device.IsOnline,
        sourceDeviceId = device.SourceDeviceId,
        hasFreshData = device.HasFreshData,
        lastUpdateTime = device.LastUpdateTime,
        position = new[] { device.Position.X, device.Position.Y, device.Position.Z },
        focusDistance = device.FocusDistance,
        primaryValue = device.PrimaryDisplay,
        secondaryValue = device.SecondaryDisplay,
        primaryUnit = device.PrimaryUnit,
        secondaryUnit = device.SecondaryUnit
    };

    private void SendToScene(string type, object data)
    {
        if (_isClosed || LabWebView.CoreWebView2 is null) return;
        try
        {
            string json = JsonSerializer.Serialize(new { type, data }, _jsonOptions);
            LabWebView.CoreWebView2.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"发送 3D 场景消息失败: {ex.Message}");
        }
    }

    private void OnInspectionChanged(object? sender, Lab3DInspectionChangedEventArgs e)
    {
        if (_isClosed) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnInspectionChanged(sender, e)));
            return;
        }

        Inspection = e.Snapshot;
        BrainView.SetFocus(e.Snapshot.TargetId);
        foreach (var device in Devices) device.SetTrend(_inspectionService.GetTrend(device.Id));
        SendInspectionFocus();
    }

    private void SendInspectionFocus()
    {
        if (!_sceneReady) return;
        SendToScene("ai-inspection-focus", new { id = Inspection.TargetId, task = Inspection.Task });
    }

    private void QueueDeviceSelectionAnimation(bool immediate = false)
    {
        Dispatcher.BeginInvoke(new Action(() => AnimateDeviceSelection(immediate)), DispatcherPriority.Loaded);
    }

    private void AnimateDeviceSelection(bool immediate)
    {
        if (_isClosed || !IsLoaded) return;
        if (SelectedDevice is null || !Devices.Contains(SelectedDevice))
        {
            SelectionIndicator.Opacity = 0;
            return;
        }

        DeviceList.UpdateLayout();
        if (DeviceList.ItemContainerGenerator.ContainerFromItem(SelectedDevice) is not FrameworkElement container) return;
        AnimateDeviceIcon(container);
        Point offset = container.TranslatePoint(new Point(0, 0), DeviceTrackHost);
        double targetWidth = container.ActualWidth > 0 ? container.ActualWidth : 212;
        double currentX = offset.X;
        double currentWidth = targetWidth;
        var anchorDevice = _selectionAnchorDevice ?? SelectedDevice;
        var anchorContainer = ReferenceEquals(anchorDevice, SelectedDevice)
            ? container
            : DeviceList.ItemContainerGenerator.ContainerFromItem(anchorDevice) as FrameworkElement;
        if (anchorContainer is not null)
        {
            currentX = anchorContainer.TranslatePoint(new Point(0, 0), DeviceTrackHost).X;
            currentWidth = anchorContainer.ActualWidth > 0 ? anchorContainer.ActualWidth : 212;
        }

        _selectionFromX = currentX;
        _selectionFromWidth = currentWidth;
        _selectionFromRight = currentX + currentWidth;
        _selectionToX = offset.X;
        _selectionToWidth = targetWidth;
        _selectionToRight = offset.X + targetWidth;
        SelectionIndicator.Height = container.ActualHeight > 0 ? container.ActualHeight : 70;
        SelectionIndicator.Opacity = 1;

        if (immediate || !_selectionInitialized)
        {
            if (_selectionAnimating) CompositionTarget.Rendering -= DeviceSelectionRendering;
            _selectionAnimating = false;
            _selectionInitialized = true;
            _selectionAnchorDevice = SelectedDevice;
            RenderDeviceSelection(1);
            return;
        }

        _selectionStarted = TimeSpan.Zero;
        if (!_selectionAnimating)
        {
            _selectionAnimating = true;
            CompositionTarget.Rendering += DeviceSelectionRendering;
        }
    }

    private void DeviceSelectionRendering(object? sender, EventArgs e)
    {
        if (_isClosed)
        {
            _selectionAnimating = false;
            CompositionTarget.Rendering -= DeviceSelectionRendering;
            return;
        }

        TimeSpan frameTime = ((RenderingEventArgs)e).RenderingTime;
        if (_selectionStarted == TimeSpan.Zero) _selectionStarted = frameTime;
        double progress = Math.Clamp((frameTime - _selectionStarted).TotalMilliseconds / SelectionDurationMs, 0, 1);
        RenderDeviceSelection(progress);
        if (progress >= 1)
        {
            _selectionAnimating = false;
            _selectionAnchorDevice = SelectedDevice;
            CompositionTarget.Rendering -= DeviceSelectionRendering;
        }
    }

    private void RenderDeviceSelection(double progress)
    {
        bool forward = _selectionToX + _selectionToWidth / 2 >= _selectionFromX + _selectionFromWidth / 2;
        double left;
        double right;
        if (forward)
        {
            if (progress <= 0.45)
            {
                left = _selectionFromX;
                right = Lerp(_selectionFromRight, _selectionToRight, EaseSelection(progress / 0.45));
            }
            else
            {
                left = Lerp(_selectionFromX, _selectionToX, EaseSelection((progress - 0.45) / 0.55));
                right = _selectionToRight;
            }
            right += Math.Sin(Math.PI * progress) * 3;
        }
        else
        {
            if (progress <= 0.45)
            {
                left = Lerp(_selectionFromX, _selectionToX, EaseSelection(progress / 0.45));
                right = _selectionFromRight;
            }
            else
            {
                left = _selectionToX;
                right = Lerp(_selectionFromRight, _selectionToRight, EaseSelection((progress - 0.45) / 0.55));
            }
            left -= Math.Sin(Math.PI * progress) * 3;
        }

        SelectionTranslate.X = left;
        SelectionIndicator.Width = Math.Max(40, right - left);
        SelectionScale.ScaleX = 1;
        SelectionScale.ScaleY = 1 + Math.Sin(Math.PI * progress) * 0.045;
        SelectionIndicator.CornerRadius = new CornerRadius(35 + Math.Sin(Math.PI * progress) * 10);
        SelectionIndicator.Opacity = 0.9 + Math.Sin(Math.PI * progress) * 0.1;
    }

    private static double Lerp(double from, double to, double progress) => from + (to - from) * progress;
    private static double EaseSelection(double progress) => progress * progress * (3 - 2 * progress);

    private void AnimateDeviceIcon(FrameworkElement container)
    {
        if (FindVisualChild<Image>(container) is not Image image
            || image.RenderTransform is not ScaleTransform scale || scale.IsFrozen) return;

        if (!ReferenceEquals(_selectionImage, image))
        {
            if (_selectionImage?.RenderTransform is ScaleTransform previousScale && !previousScale.IsFrozen)
                AnimateIconScale(previousScale, 1, 180);
            _selectionImage = image;
        }
        AnimateIconScale(scale, 1.08, 350, 0.94);
    }

    private static void AnimateIconScale(ScaleTransform scale, double to, int milliseconds, double? from = null)
    {
        var scaleX = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds));
        var scaleY = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds));
        if (from.HasValue)
        {
            scaleX.From = from;
            scaleY.From = from;
            scaleX.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            scaleY.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
        }
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            var descendant = FindVisualChild<T>(child);
            if (descendant is not null) return descendant;
        }
        return null;
    }

    private void SelectDevice(LabDevice device, bool focus, bool notifyScene = true, bool bringIntoView = true)
    {
        foreach (var item in Devices) item.IsSelected = item == device;
        SelectedDevice = device;
        _inspectionService.SelectDevice(device);
        if (notifyScene && _sceneReady)
            SendToScene("select-device", new { id = device.Id });
        if (bringIntoView && DeviceList.ItemContainerGenerator.ContainerFromItem(device) is FrameworkElement container)
            container.BringIntoView();
    }

    private void LoadCustomDevices()
    {
        foreach (var device in Devices.Where(device => device.LibraryId is not null).ToArray())
            Devices.Remove(device);
        foreach (var entry in _deviceLibrary.LoadEntries())
        {
            if (entry.Position.Length < 3) continue;
            var builtInDevice = Devices.FirstOrDefault(device =>
                device.Id == entry.Id && device.LibraryId is null);
            if (builtInDevice is not null) Devices.Remove(builtInDevice);
            bool isLeft = entry.Position[0] < 0;
            int rowIndex = Devices.Count(device => isLeft ? device.Position.X < 0 : device.Position.X > 0);
            entry.Position[2] = GetDeviceRowZ(rowIndex);
            Devices.Add(ToLabDevice(entry));
        }
        _inspectionService.SetDevices(Devices);
        _realtimeService.SetDevices(Devices);
        OnPropertyChanged(nameof(DeviceCountText));
        OnPropertyChanged(nameof(OnlineSummary));
        if (!string.IsNullOrEmpty(_deviceLibrary.LastError) && _deviceLibrary.LastError is string error)
            AddEvent(error);
    }

    private static LabDevice ToLabDevice(DeviceLibraryEntry entry) => new()
    {
        Id = entry.Id,
        Name = entry.Name,
        EnglishName = entry.EnglishName,
        Kind = entry.Kind,
        ImagePath = entry.ImagePath,
        WebImagePath = $"https://devices.local/{entry.LibraryId}/front.png",
        LibraryId = entry.LibraryId,
        Location = entry.Location,
        Description = entry.Description,
        Position = new System.Windows.Media.Media3D.Point3D(entry.Position[0], entry.Position[1], entry.Position[2]),
        PrimaryName = entry.PrimaryName,
        PrimaryUnit = entry.PrimaryUnit,
        PrimaryValue = entry.PrimaryValue,
        SecondaryName = entry.SecondaryName,
        SecondaryUnit = entry.SecondaryUnit,
        SecondaryValue = entry.SecondaryValue,
        Protocol = entry.Protocol,
        IsOnline = entry.IsOnline,
        FocusDistance = entry.FocusDistance,
        ImageAspect = entry.ImageAspect,
        ModelHeight = entry.ModelHeight
    };

    private static double GetDeviceRowZ(int rowIndex) =>
        rowIndex < 3 ? -5.8 + rowIndex * 5.0 : 4.2 + (rowIndex - 2) * 3.0;

    private double[] GetNextDevicePosition()
    {
        int leftCount = Devices.Count(device => device.Position.X < 0);
        int rightCount = Devices.Count(device => device.Position.X > 0);
        bool useLeft = leftCount <= rightCount;
        int rowIndex = useLeft ? leftCount : rightCount;
        return new[] { useLeft ? -3.4 : 3.4, 0.88, GetDeviceRowZ(rowIndex) };
    }

    private void AddDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_isDeleting || _isClosed) return;
        AddEvent("打开设备图片选择器");
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择设备图片",
            Filter = "设备图片|*.png;*.jpg;*.jpeg;*.webp;*.bmp",
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = Path.Combine(_wwwroot, "assets", "devices")
        };
        if (dialog.ShowDialog(this) != true) return;
        AddEvent($"已选择设备图片：{Path.GetFileName(dialog.FileName)}");
        try
        {
            var editor = new DeviceEditorWindow(dialog.FileName, _deviceLibrary.StorageDirectory) { Owner = this };
            if (editor.ShowDialog() != true) return;
            string previousDirectory = _deviceLibrary.StorageDirectory;
            var entry = _deviceLibrary.AddDevice(
                editor.DeviceName,
                editor.EnglishName,
                double.Parse(editor.ModelHeightText, System.Globalization.CultureInfo.InvariantCulture),
                dialog.FileName,
                editor.StorageDirectory,
                GetNextDevicePosition(),
                Devices.Select(device => device.Id));
            if (!string.Equals(
                Path.TrimEndingDirectorySeparator(previousDirectory),
                Path.TrimEndingDirectorySeparator(_deviceLibrary.StorageDirectory),
                StringComparison.OrdinalIgnoreCase))
            {
                LoadCustomDevices();
                var restoredDevice = Devices.First(device => device.LibraryId == entry.LibraryId);
                SelectDevice(restoredDevice, false, false);
                ReloadSceneAfterLibraryChange();
                AddEvent($"设备库新增 {entry.Id} {entry.Name}");
                return;
            }

            var device = ToLabDevice(entry);
            Devices.Add(device);
            _inspectionService.SetDevices(Devices);
            _realtimeService.SetDevices(Devices);
            OnPropertyChanged(nameof(DeviceCountText));
            OnPropertyChanged(nameof(DeviceRowCount));
            OnPropertyChanged(nameof(OnlineSummary));
            if (_sceneReady) SendToScene("add-device", CreateDevicePayload(device));
            SelectDevice(device, true);
            AddEvent($"设备库新增 {entry.Id} {device.Name}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"添加设备失败：{ex.Message}", "设备库", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void OpenDeviceEditor(LabDevice device)
    {
        if (_isDeleting || _isEditing || _isClosed) return;

        _isEditing = true;
        try
        {
            string previousDirectory = _deviceLibrary.StorageDirectory;
            var editor = new DeviceEditorWindow(device, previousDirectory) { Owner = this };
            if (editor.ShowDialog() != true) return;

            double modelHeight = double.Parse(editor.ModelHeightText, System.Globalization.CultureInfo.InvariantCulture);
            DeviceLibraryEntry entry;
            if (device.LibraryId is not null)
            {
                entry = await Task.Run(() => _deviceLibrary.UpdateDevice(
                    device.LibraryId,
                    editor.DeviceName,
                    editor.EnglishName,
                    modelHeight,
                    editor.StorageDirectory));
            }
            else
            {
                entry = await Task.Run(() => _deviceLibrary.AddDevice(
                    editor.DeviceName,
                    editor.EnglishName,
                    modelHeight,
                    device.ImagePath,
                    editor.StorageDirectory,
                    new[] { device.Position.X, device.Position.Y, device.Position.Z },
                    Devices.Select(item => item.Id),
                    device.Id));
            }

            if (_isClosed) return;

            ApplyLibraryEntry(device, entry);
            _inspectionService.SetDevices(Devices);
            _realtimeService.SetDevices(Devices);
            if (!string.Equals(
                Path.TrimEndingDirectorySeparator(previousDirectory),
                Path.TrimEndingDirectorySeparator(_deviceLibrary.StorageDirectory),
                StringComparison.OrdinalIgnoreCase))
            {
                UpdateDeviceLibraryHostMapping();
            }

            if (_sceneReady) SendToScene("update-device", CreateDevicePayload(device));
            if (!string.IsNullOrEmpty(_deviceLibrary.LastError)) AddEvent(_deviceLibrary.LastError);
            AddEvent($"设备参数已更新 {device.Id} {device.Name}");
        }
        catch (Exception ex)
        {
            if (!_isClosed)
                MessageBox.Show(this, $"编辑设备失败：{ex.Message}", "编辑设备", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isEditing = false;
        }
    }

    private static void ApplyLibraryEntry(LabDevice device, DeviceLibraryEntry entry)
    {
        device.Name = entry.Name;
        device.EnglishName = entry.EnglishName;
        device.ModelHeight = entry.ModelHeight;
        device.FocusDistance = entry.FocusDistance;
        device.ImagePath = entry.ImagePath;
        device.WebImagePath = $"https://devices.local/{entry.LibraryId}/front.png";
        device.LibraryId = entry.LibraryId;
    }

    private void UpdateDeviceLibraryHostMapping()
    {
        if (LabWebView.CoreWebView2 is null) return;
        LabWebView.CoreWebView2.ClearVirtualHostNameToFolderMapping("devices.local");
        LabWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "devices.local", _deviceLibrary.StorageDirectory, CoreWebView2HostResourceAccessKind.Allow);
    }

    private void ShowDeviceContextMenu(LabDevice device)
    {
        if (_isDeleting || _isEditing || _isClosed) return;

        var menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            StaysOpen = true,
            Style = (Style)FindResource("LabContextMenuStyle")
        };
        var editItem = new MenuItem
        {
            Header = "编辑",
            Style = (Style)FindResource("LabMenuItemStyle")
        };
        editItem.Click += (_, _) => OpenDeviceEditor(device);
        var deleteItem = new MenuItem
        {
            Header = "删除",
            Style = (Style)FindResource("LabMenuItemStyle")
        };
        deleteItem.Click += (_, _) => DeleteDevice(device);
        menu.Items.Add(editItem);
        menu.Items.Add(deleteItem);
        AddEvent($"打开设备菜单 {device.Id}");
        menu.IsOpen = true;
    }

    private async void DeleteDevice(LabDevice device)
    {
        if (_isDeleting) return;
        var confirmation = new DeleteDeviceConfirmationWindow(device) { Owner = this };
        if (confirmation.ShowDialog() != true) return;

        _isDeleting = true;
        try
        {
            if (device.LibraryId is not null)
                await Task.Run(() => _deviceLibrary.DeleteDevice(device.LibraryId));
            else
                await Task.Run(() => _deletedDevices.Add(device.Id));

            if (_isClosed || !Devices.Contains(device)) return;

            int nextIndex = Devices.IndexOf(device);
            Devices.Remove(device);

            if (Devices.Count == 0)
            {
                SelectedDevice = null;
                _inspectionService.SelectDevice(null);
            }
            else
            {
                SelectDevice(Devices[Math.Min(nextIndex, Devices.Count - 1)], false, false);
            }

            _inspectionService.SetDevices(Devices);
            _realtimeService.SetDevices(Devices);
            OnPropertyChanged(nameof(DeviceCountText));
            OnPropertyChanged(nameof(DeviceRowCount));
            OnPropertyChanged(nameof(OnlineSummary));
            if (_sceneReady) SendToScene("remove-device", new { id = device.Id });
            if (!string.IsNullOrEmpty(_deviceLibrary.LastError)) AddEvent(_deviceLibrary.LastError);
            AddEvent($"设备库已删除 {device.Id} {device.Name}");
        }
        catch (Exception ex)
        {
            if (!_isClosed)
                MessageBox.Show(this, $"删除设备失败：{ex.Message}", "设备库", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _isDeleting = false;
        }
    }

    private void ReloadSceneAfterLibraryChange()
    {
        if (LabWebView.CoreWebView2 is null) return;
        LabWebView.CoreWebView2.ClearVirtualHostNameToFolderMapping("devices.local");
        LabWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "devices.local", _deviceLibrary.StorageDirectory, CoreWebView2HostResourceAccessKind.Allow);
        _sceneReady = false;
        LabLoadingTitle.Text = "正在同步设备库";
        LabLoadingText.Text = "更新设备模型、图片纹理与展厅布局";
        LabLoadingProgress.IsIndeterminate = true;
        LabRetryButton.Visibility = Visibility.Collapsed;
        LabLoading.Visibility = Visibility.Visible;
        LabWebView.Visibility = Visibility.Hidden;
        LabWebView.CoreWebView2.Reload();
    }

    private void DeviceScrollLeft_Click(object sender, RoutedEventArgs e) =>
        DeviceScrollViewer.ScrollToHorizontalOffset(DeviceScrollViewer.HorizontalOffset - 270);

    private void DeviceScrollRight_Click(object sender, RoutedEventArgs e) =>
        DeviceScrollViewer.ScrollToHorizontalOffset(DeviceScrollViewer.HorizontalOffset + 270);

    private void DeviceScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        DeviceScrollViewer.ScrollToHorizontalOffset(DeviceScrollViewer.HorizontalOffset - e.Delta * 0.75);
        e.Handled = true;
    }

    private static void ClearDeviceLiveData(LabDevice device)
    {
        device.SourceDeviceId = null;
        device.Protocol = "未绑定";
        device.IsOnline = false;
        device.HasFreshData = false;
        device.LastUpdateTime = null;
        device.PrimaryValue = string.Empty;
        device.SecondaryValue = string.Empty;
        device.SetTrend(Lab3DTrendSeries.Empty);
    }

    private void OnRealtimeSnapshotChanged(object? sender, Lab3DRealtimeSnapshotEventArgs e)
    {
        if (_isClosed) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnRealtimeSnapshotChanged(sender, e)));
            return;
        }

        var snapshots = e.Devices.ToDictionary(snapshot => snapshot.SlotId, StringComparer.OrdinalIgnoreCase);
        foreach (var device in Devices)
        {
            if (!snapshots.TryGetValue(device.Id, out var snapshot))
            {
                if (device.SourceDeviceId is not null) ClearDeviceLiveData(device);
                continue;
            }

            bool wasOnline = device.IsOnline;
            string? previousSourceId = device.SourceDeviceId;
            if (previousSourceId != snapshot.SourceDeviceId && previousSourceId is not null)
                ClearDeviceLiveData(device);

            device.SourceDeviceId = snapshot.SourceDeviceId;
            device.Protocol = snapshot.Protocol;
            device.PrimaryName = snapshot.Primary.Name;
            device.PrimaryUnit = snapshot.Primary.Unit;
            device.PrimaryValue = snapshot.Primary.Value;
            device.SecondaryName = snapshot.Secondary.Name;
            device.SecondaryUnit = snapshot.Secondary.Unit;
            device.SecondaryValue = snapshot.Secondary.Value;
            device.LastUpdateTime = snapshot.LastUpdateTime;
            device.HasFreshData = snapshot.HasFreshData;
            device.IsOnline = snapshot.IsConnected;

            if (previousSourceId != snapshot.SourceDeviceId && snapshot.SourceDeviceId is not null)
                AddEvent($"{device.Id} 已绑定主程序设备");
            if (wasOnline != snapshot.IsConnected)
                AddEvent($"{device.Id} {(snapshot.IsConnected ? "已连接" : "未连接")}");
        }

        OnPropertyChanged(nameof(OnlineSummary));
        OnPropertyChanged(nameof(OnlineDeviceCountText));
        SendTelemetry();
    }

    private void SendTelemetry()
    {
        if (!_sceneReady) return;
        SendToScene("telemetry", Devices.Select(device => new
        {
            id = device.Id,
            online = device.IsOnline,
            sourceDeviceId = device.SourceDeviceId,
            hasFreshData = device.HasFreshData,
            lastUpdateTime = device.LastUpdateTime,
            primaryValue = device.PrimaryDisplay,
            primaryUnit = device.PrimaryUnit,
            secondaryValue = device.SecondaryDisplay,
            secondaryUnit = device.SecondaryUnit
        }));
    }

    private void DeviceScroll_TouchDown(object sender, TouchEventArgs e)
    {
        if (Devices.Count == 0 || _touchTracking) return;
        _touchTracking = true;
        _touchGestureActive = false;
        _touchStartIndex = SelectedDevice is null ? 0 : Math.Max(0, Devices.IndexOf(SelectedDevice));
        _touchTargetIndex = _touchStartIndex;
        _touchStart = e.GetTouchPoint(DeviceScrollViewer).Position;
        _touchLast = _touchStart;
        _touchLastTime = DateTime.UtcNow;
        _touchVelocityX = 0;
        if (_selectionAnimating)
        {
            _selectionAnimating = false;
            CompositionTarget.Rendering -= DeviceSelectionRendering;
        }
        _selectionAnchorDevice = SelectedDevice;
        if (SelectedDevice is not null && DeviceList.ItemContainerGenerator.ContainerFromItem(SelectedDevice) is FrameworkElement selectedContainer)
        {
            _touchFromX = selectedContainer.TranslatePoint(new Point(0, 0), DeviceTrackHost).X;
            _touchFromWidth = selectedContainer.ActualWidth > 0 ? selectedContainer.ActualWidth : 212;
        }
        else
        {
            _touchFromX = SelectionTranslate.X;
            _touchFromWidth = SelectionIndicator.ActualWidth;
        }
        _touchFromRight = _touchFromX + _touchFromWidth;
    }

    private void DeviceScroll_TouchMove(object sender, TouchEventArgs e)
    {
        if (!_touchTracking) return;
        Point current = e.GetTouchPoint(DeviceScrollViewer).Position;
        DateTime now = DateTime.UtcNow;
        double elapsed = Math.Max(1, (now - _touchLastTime).TotalMilliseconds);
        _touchVelocityX = 0.65 * _touchVelocityX + 0.35 * ((current.X - _touchLast.X) / elapsed);
        _touchLast = current;
        _touchLastTime = now;

        double deltaX = current.X - _touchStart.X;
        double deltaY = current.Y - _touchStart.Y;
        if (!_touchGestureActive && Math.Abs(deltaX) > 9 && Math.Abs(deltaX) > Math.Abs(deltaY) * 1.2)
        {
            _touchGestureActive = true;
            DeviceScrollViewer.CaptureTouch(e.TouchDevice);
        }
        if (!_touchGestureActive) return;
        _suppressDeviceMouseUntil = DateTime.UtcNow.AddSeconds(1);
        e.Handled = true;

        const double stepDistance = 90;
        int steps = (int)Math.Truncate(deltaX / stepDistance);
        int targetIndex = Math.Clamp(_touchStartIndex + steps, 0, Devices.Count - 1);
        if (targetIndex != _touchTargetIndex)
        {
            _touchTargetIndex = targetIndex;
            SelectDevice(Devices[targetIndex], false, true, false);
            return;
        }
        if (steps != 0) return;

        int previewIndex = Math.Clamp(_touchStartIndex + (deltaX < 0 ? 1 : -1), 0, Devices.Count - 1);
        if (previewIndex == _touchStartIndex)
        {
            SelectionTranslate.X = _touchFromX + Math.Clamp(deltaX * 0.12, -18, 18);
            SelectionIndicator.Width = _touchFromWidth;
            SelectionScale.ScaleY = 1 + Math.Min(1, Math.Abs(deltaX) / 200) * 0.025;
            return;
        }
        if (!UpdateTouchTarget(previewIndex)) return;
        RenderTouchSelection(Math.Min(1, Math.Abs(deltaX) / stepDistance));
    }

    private bool UpdateTouchTarget(int targetIndex)
    {
        if (targetIndex == _touchTargetIndex) return true;
        DeviceList.UpdateLayout();
        if (DeviceList.ItemContainerGenerator.ContainerFromItem(Devices[targetIndex]) is not FrameworkElement container) return false;
        Point offset = container.TranslatePoint(new Point(0, 0), DeviceTrackHost);
        _touchTargetX = offset.X;
        _touchTargetRight = offset.X + (container.ActualWidth > 0 ? container.ActualWidth : 212);
        return true;
    }

    private void RenderTouchSelection(double progress)
    {
        double left;
        double right;
        if (_touchTargetIndex > _touchStartIndex)
        {
            left = _touchFromX;
            right = Lerp(_touchFromRight, _touchTargetRight, EaseSelection(progress));
        }
        else
        {
            left = Lerp(_touchFromX, _touchTargetX, EaseSelection(progress));
            right = _touchFromRight;
        }
        SelectionTranslate.X = left;
        SelectionIndicator.Width = Math.Max(40, right - left);
        SelectionScale.ScaleX = 1;
        SelectionScale.ScaleY = 1 + progress * 0.045;
        SelectionIndicator.CornerRadius = new CornerRadius(35 + progress * 10);
        SelectionIndicator.Opacity = 0.95 + progress * 0.05;
    }

    private void DeviceScroll_TouchUp(object sender, TouchEventArgs e)
    {
        if (!_touchTracking) return;
        if (!_touchGestureActive)
        {
            _touchTracking = false;
            return;
        }

        bool selectionChanged = _touchTargetIndex != _touchStartIndex;
        _suppressDeviceMouseUntil = DateTime.UtcNow.AddSeconds(1);
        e.Handled = true;
        EndTouchGesture(e.TouchDevice);
        if (selectionChanged) SelectDevice(Devices[_touchTargetIndex], false);
        else QueueDeviceSelectionAnimation();
    }

    private void DeviceScroll_LostTouchCapture(object sender, TouchEventArgs e)
    {
        if (!_touchTracking) return;
        if (_touchGestureActive)
        {
            _suppressDeviceMouseUntil = DateTime.UtcNow.AddSeconds(1);
            e.Handled = true;
            EndTouchGesture(e.TouchDevice);
            QueueDeviceSelectionAnimation();
        }
        else _touchTracking = false;
    }

    private void EndTouchGesture(TouchDevice touchDevice)
    {
        _touchTracking = false;
        _touchGestureActive = false;
        DeviceScrollViewer.ReleaseTouchCapture(touchDevice);
    }

    private void DeviceCard_Click(object sender, RoutedEventArgs e)
    {
        if (DateTime.UtcNow < _suppressDeviceMouseUntil) return;
        if (sender is FrameworkElement { DataContext: LabDevice device })
        {
            SelectDevice(device, true);
            if (_sceneReady) SendToScene("locate-device", new { id = device.Id });
        }
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not null) DeleteDevice(SelectedDevice);
    }

    /// <summary>
    /// 进入语音全屏展示模式:窗口最大化并保留右侧设备详情栏。
    /// 不重建 WebView2,也不影响实时设备数据。
    /// </summary>
    public void EnterFullScreen()
    {
        WindowState = WindowState.Maximized;
        Activate();
        AddEvent("已进入全屏展示模式");
    }

    /// <summary>退出全屏展示:恢复普通窗口大小。</summary>
    public void ExitFullScreen()
    {
        WindowState = WindowState.Normal;
        Activate();
        AddEvent("已退出全屏展示模式");
    }

    private async void RetryScene_Click(object sender, RoutedEventArgs e)
    {
        LabRetryButton.Visibility = Visibility.Collapsed;
        LabLoadingProgress.IsIndeterminate = true;
        _sceneReady = false;
        LabWebView.Visibility = Visibility.Hidden;
        await InitializeWebViewAsync();
    }

    private void SetSceneFullscreen(bool enabled)
    {
        if (_isSceneFullscreen == enabled) return;
        _isSceneFullscreen = enabled;
        UpdateSidePanelWidths();
        SidePanelsPopup.IsOpen = !enabled && _sidePanelsOverlayInitialized;
        BrainView.SetAnimationActive(!enabled);

        if (enabled)
        {
            _windowStateBeforeSceneFullscreen = WindowState;
            ContentGrid.Children.Remove(SceneViewport);
            RootGrid.Children.Add(SceneViewport);
            Grid.SetRow(SceneViewport, 0);
            Grid.SetRowSpan(SceneViewport, RootGrid.RowDefinitions.Count);
            Grid.SetColumn(SceneViewport, 0);
            Grid.SetColumnSpan(SceneViewport, 1);
            Panel.SetZIndex(SceneViewport, 100);
            SceneModeBorder.Margin = new Thickness(12);
            TitleBarBorder.Visibility = Visibility.Collapsed;
            DeviceNavigationBorder.Visibility = Visibility.Collapsed;
            ContentGrid.Visibility = Visibility.Collapsed;
            WindowState = WindowState.Maximized;
        }
        else
        {
            RootGrid.Children.Remove(SceneViewport);
            ContentGrid.Children.Add(SceneViewport);
            Grid.SetRow(SceneViewport, 0);
            Grid.SetColumn(SceneViewport, 0);
            Grid.SetRowSpan(SceneViewport, 1);
            Grid.SetColumnSpan(SceneViewport, 3);
            Panel.SetZIndex(SceneViewport, 0);
            SceneModeBorder.Margin = new Thickness(252, 12, 12, 12);
            TitleBarBorder.Visibility = Visibility.Visible;
            DeviceNavigationBorder.Visibility = Visibility.Visible;
            ContentGrid.Visibility = Visibility.Visible;
            WindowState = _windowStateBeforeSceneFullscreen;
        }

        _isSceneFullscreen = enabled;
        SendToScene("scene-fullscreen-changed", new { enabled });
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && _isSceneFullscreen)
        {
            SetSceneFullscreen(false);
            e.Handled = true;
            return;
        }
        if (!_sceneReady) return;
        switch (e.Key)
        {
            case Key.F9:
                if (SelectedDevice is not null) ShowDeviceContextMenu(SelectedDevice);
                e.Handled = true;
                break;
            case Key.F10:
                if (Keyboard.Modifiers == ModifierKeys.Shift && SelectedDevice is not null)
                    ShowDeviceContextMenu(SelectedDevice);
                e.Handled = true;
                break;
            case Key.Home:
            case Key.R: SendToScene("overview", new { }); break;
            case Key.F:
                if (SelectedDevice is not null) SelectDevice(SelectedDevice, true);
                break;
        }
    }

    private void ShowSceneError(Exception ex)
    {
        LabLoading.Visibility = Visibility.Visible;
        LabLoadingTitle.Text = "无法启动 WebView2 3D 引擎";
        LabLoadingText.Text = ex.Message;
        LabLoadingText.TextWrapping = TextWrapping.Wrap;
        LabLoadingProgress.IsIndeterminate = false;
        LabRetryButton.Visibility = Visibility.Visible;
        AddEvent($"3D 引擎启动失败：{ex.Message}");
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleMaximize();
        else if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void AddEvent(string message)
    {
        Events.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
        while (Events.Count > 4) Events.RemoveAt(Events.Count - 1);
    }

    private string DeviceImage(string fileName) => Path.Combine(_wwwroot, "assets", "devices", fileName);

    private void CreateDevices()
    {
        Devices.Add(new LabDevice
        {
            Id = "D01", Name = "高低温循环一体机", EnglishName = "THERMAL CIRCULATOR", Kind = "Temperature",
            ImagePath = DeviceImage("highLowtemperature.png"), Location = "左展区 · 01 展台", Protocol = "未绑定",
            Position = new System.Windows.Media.Media3D.Point3D(-3.4, 0.88, -5.8), IsOnline = false, FocusDistance = 9,
            Description = "为反应系统提供精准、稳定的高低温循环环境。支持远程设定温度、实时曲线记录与异常状态报警。",
            PrimaryName = "物料温度", PrimaryUnit = "℃", PrimaryValue = "--",
            SecondaryName = "出口温度", SecondaryUnit = "℃", SecondaryValue = "--"
        });
        Devices.Add(new LabDevice
        {
            Id = "D02", Name = "微型反应器", EnglishName = "MICRO REACTOR ARRAY", Kind = "Reactor",
            ImagePath = DeviceImage("MicroReactor.png"), Location = "左展区 · 02 展台", Protocol = "未绑定",
            Position = new System.Windows.Media.Media3D.Point3D(-3.4, 0.88, -0.8), IsOnline = false, FocusDistance = 8,
            Description = "多通道微型反应阵列，用于连续流工艺筛选。各通道独立监测压力与温度，支持高通量平行实验。",
            PrimaryName = "出口压力", PrimaryUnit = "MPa", PrimaryValue = "--",
            SecondaryName = "实际温度", SecondaryUnit = "℃", SecondaryValue = "--"
        });
        Devices.Add(new LabDevice
        {
            Id = "D03", Name = "智能磁力搅拌器", EnglishName = "MAGNETIC STIRRER", Kind = "Stirrer",
            ImagePath = DeviceImage("magneticstirrer.png"), Location = "左展区 · 03 展台", Protocol = "未绑定",
            Position = new System.Windows.Media.Media3D.Point3D(-3.4, 0.88, 4.2), IsOnline = false, FocusDistance = 8,
            Description = "集加热、恒温与磁力搅拌于一体。无刷电机提供稳定转速，适配材料合成及溶液前处理实验。",
            PrimaryName = "速度测量值", PrimaryUnit = "RPM", PrimaryValue = "--",
            SecondaryName = "内置温度", SecondaryUnit = "℃", SecondaryValue = "--"
        });
        Devices.Add(new LabDevice
        {
            Id = "D04", Name = "低温冷却循环器", EnglishName = "PRECISION CHILLER", Kind = "Chiller",
            ImagePath = DeviceImage("ChilLer.png"), Location = "右展区 · 01 展台", Protocol = "未绑定",
            Position = new System.Windows.Media.Media3D.Point3D(3.4, 0.88, -5.8), IsOnline = false, FocusDistance = 8.5,
            Description = "高精度低温冷却循环设备，采用闭环 PID 控制，为分析仪器及反应装置持续提供稳定冷源。",
            PrimaryName = "液体温度", PrimaryUnit = "℃", PrimaryValue = "--",
            SecondaryName = "设定温度", SecondaryUnit = "℃", SecondaryValue = "--"
        });
        Devices.Add(new LabDevice
        {
            Id = "D05", Name = "恒温培养箱", EnglishName = "CLIMATE CHAMBER", Kind = "Climate",
            ImagePath = DeviceImage("ClimateChamber.png"), Location = "右展区 · 02 展台", Protocol = "未绑定",
            Position = new System.Windows.Media.Media3D.Point3D(3.4, 0.88, -0.8), IsOnline = false, FocusDistance = 8.5,
            Description = "全温振荡培养箱，具备多段程序控制与远程监测能力，用于样品培养、老化和环境适应性实验。",
            PrimaryName = "箱内温度", PrimaryUnit = "℃", PrimaryValue = "--",
            SecondaryName = "温度设定", SecondaryUnit = "℃", SecondaryValue = "--"
        });
        Devices.Add(new LabDevice
        {
            Id = "D06", Name = "蓝牙多参数检测笔", EnglishName = "BLUETOOTH PEN METER", Kind = "Meter",
            ImagePath = DeviceImage("PenMeter_BLE.png"), Location = "右展区 · 03 展台", Protocol = "未绑定",
            Position = new System.Windows.Media.Media3D.Point3D(3.4, 0.88, 4.2), IsOnline = false, FocusDistance = 7,
            Description = "便携式 pH / 电导率检测终端，支持蓝牙数据同步与自动温度补偿。",
            PrimaryName = "测量值", PrimaryUnit = "", PrimaryValue = "--",
            SecondaryName = "温度", SecondaryUnit = "℃", SecondaryValue = "--"
        });

        var deletedIds = _deletedDevices.Load();
        foreach (var deletedDevice in Devices.Where(device => deletedIds.Contains(device.Id)).ToArray())
            Devices.Remove(deletedDevice);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private static bool IsOtherApplicationForeground()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        GetWindowThreadProcessId(foreground, out uint processId);
        return processId != Environment.ProcessId;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
