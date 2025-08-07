using MaxChemical.Core;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Modules.Designer.Service;
using MaxChemical.Modules.Designer.Service.Execution;
using MaxChemical.Modules.Designer.ViewModels;
using MaxChemical.Modules.Designer.Views;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using System.Windows;

namespace MaxChemical.Shell.ViewModels
{
    public class MainToolBarViewModel : BindableBase
    {
        private readonly ICommandHistory _commandHistory; // 新增

        // 新增属性
        private bool _canUndo;
        private bool _canRedo;

        public bool CanUndo
        {
            get => _canUndo;
            set => SetProperty(ref _canUndo, value);
        }

        public bool CanRedo
        {
            get => _canRedo;
            set => SetProperty(ref _canRedo, value);
        }
        private readonly IEventAggregator _eventAggregator;
        private readonly IRegionSettingsDialogProvider _regionSettingsDialogProvider;
        private readonly ILocalizationService _localizationService;
        private readonly ICanvasDataService _canvasDataService; // 新增
        private readonly IDialogService _dialogService; // 新增
        private readonly ICanvasStateService _canvasStateService; // 新增
        private RegionLayout _currentRegionLayout;
        private readonly ILogService _logger;
        private readonly IFlowExecutionEngine _flowExecutionEngine;
        private bool _isExecuting = false;
        // 本地化属性
        private string _tooltipOpenFolder = string.Empty;
        private string _tooltipNewDocument = string.Empty;
        private string _tooltipSave = string.Empty;
        private string _tooltipSaveAs = string.Empty;
        private string _tooltipNew = string.Empty;
        private string _tooltipUndo = string.Empty;
        private string _tooltipRedo = string.Empty;
        private string _tooltipDelete = string.Empty;
        private string _tooltipZoomIn = string.Empty;
        private string _tooltipZoomOut = string.Empty;
        private string _tooltipFitCanvas = string.Empty;
        private string _tooltipCanvasSettings = string.Empty;
        private string _tooltipDataAnalysis = string.Empty;
        private string _tooltipLog = string.Empty;
        private string _tooltipStart = string.Empty;
        private string _tooltipPause = string.Empty;
        private string _tooltipStop = string.Empty;
        private string _tooltipSimulationMode = string.Empty;
        private string _buttonStart = string.Empty;
        private string _buttonPause = string.Empty;
        private string _buttonStop = string.Empty;
        private string _textSimulationMode = string.Empty;

        public MainToolBarViewModel(
          IEventAggregator eventAggregator,
          IRegionSettingsDialogProvider regionSettingsDialogProvider,
          ILocalizationService localizationService,
          ICanvasDataService canvasDataService,
          IDialogService dialogService,
          ICanvasStateService canvasStateService,
          ICommandHistory commandHistory,
          IFlowExecutionEngine flowExecutionEngine) // 新增参数
        {
            _logger = new LogService().ForContext<MainToolBarViewModel>();
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _regionSettingsDialogProvider = regionSettingsDialogProvider ?? throw new ArgumentNullException(nameof(regionSettingsDialogProvider));
            _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));
            _canvasDataService = canvasDataService ?? throw new ArgumentNullException(nameof(canvasDataService));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            _canvasStateService = canvasStateService ?? throw new ArgumentNullException(nameof(canvasStateService));
            _commandHistory = commandHistory ?? throw new ArgumentNullException(nameof(commandHistory)); // 新增
            _flowExecutionEngine = flowExecutionEngine ?? throw new ArgumentNullException(nameof(flowExecutionEngine));
            InitializeCommands();
            LoadLocalizedStrings();
            SubscribeEvents();
            SubscribeExecutionEvents();
        }




        #region Localized Properties

        public string TooltipOpenFolder
        {
            get => _tooltipOpenFolder;
            set => SetProperty(ref _tooltipOpenFolder, value);
        }

        public string TooltipNewDocument
        {
            get => _tooltipNewDocument;
            set => SetProperty(ref _tooltipNewDocument, value);
        }

        public string TooltipSave
        {
            get => _tooltipSave;
            set => SetProperty(ref _tooltipSave, value);
        }

        public string TooltipSaveAs
        {
            get => _tooltipSaveAs;
            set => SetProperty(ref _tooltipSaveAs, value);
        }

        public string TooltipNew
        {
            get => _tooltipNew;
            set => SetProperty(ref _tooltipNew, value);
        }

        public string TooltipUndo
        {
            get => _tooltipUndo;
            set => SetProperty(ref _tooltipUndo, value);
        }

        public string TooltipRedo
        {
            get => _tooltipRedo;
            set => SetProperty(ref _tooltipRedo, value);
        }

        public string TooltipDelete
        {
            get => _tooltipDelete;
            set => SetProperty(ref _tooltipDelete, value);
        }

        public string TooltipZoomIn
        {
            get => _tooltipZoomIn;
            set => SetProperty(ref _tooltipZoomIn, value);
        }

        public string TooltipZoomOut
        {
            get => _tooltipZoomOut;
            set => SetProperty(ref _tooltipZoomOut, value);
        }

        public string TooltipFitCanvas
        {
            get => _tooltipFitCanvas;
            set => SetProperty(ref _tooltipFitCanvas, value);
        }

        public string TooltipCanvasSettings
        {
            get => _tooltipCanvasSettings;
            set => SetProperty(ref _tooltipCanvasSettings, value);
        }

        public string TooltipDataAnalysis
        {
            get => _tooltipDataAnalysis;
            set => SetProperty(ref _tooltipDataAnalysis, value);
        }

        public string TooltipLog
        {
            get => _tooltipLog;
            set => SetProperty(ref _tooltipLog, value);
        }

        public string TooltipStart
        {
            get => _tooltipStart;
            set => SetProperty(ref _tooltipStart, value);
        }

        public string TooltipPause
        {
            get => _tooltipPause;
            set => SetProperty(ref _tooltipPause, value);
        }

        public string TooltipStop
        {
            get => _tooltipStop;
            set => SetProperty(ref _tooltipStop, value);
        }

        public string TooltipSimulationMode
        {
            get => _tooltipSimulationMode;
            set => SetProperty(ref _tooltipSimulationMode, value);
        }

        public string ButtonStart
        {
            get => _buttonStart;
            set => SetProperty(ref _buttonStart, value);
        }

        public string ButtonPause
        {
            get => _buttonPause;
            set => SetProperty(ref _buttonPause, value);
        }

        public string ButtonStop
        {
            get => _buttonStop;
            set => SetProperty(ref _buttonStop, value);
        }

        public string TextSimulationMode
        {
            get => _textSimulationMode;
            set => SetProperty(ref _textSimulationMode, value);
        }

        #endregion

        public RegionLayout CurrentRegionLayout
        {
            get => _currentRegionLayout;
            set => SetProperty(ref _currentRegionLayout, value);
        }
        private double _zoomStep = 0.1; // 缩放步长
        private double _deviceZoomStep = 0.2; // 设备缩放步长
        #region Commands

        public DelegateCommand OpenFolderCommand { get; private set; }
        public DelegateCommand NewDocumentCommand { get; private set; }
        public DelegateCommand SaveCommand { get; private set; }
        public DelegateCommand SaveAsCommand { get; private set; }
        public DelegateCommand GoBackCommand { get; private set; }
        public DelegateCommand GoForwardCommand { get; private set; }
        public DelegateCommand DeleteCommand { get; private set; }
        public DelegateCommand UndoCommand { get; private set; }
        public DelegateCommand RedoCommand { get; private set; }
        public DelegateCommand FitToViewCommand { get; private set; } // 新增自适应命令
        public DelegateCommand SettingsCommand { get; private set; }
        public DelegateCommand StartCommand { get; private set; }
        public DelegateCommand PauseCommand { get; private set; }
        public DelegateCommand StopCommand { get; private set; }
        public DelegateCommand StatusCommand { get; private set; }

        #endregion

        private void InitializeCommands()
        {
            System.Diagnostics.Debug.WriteLine("MainToolBarViewModel InitializeCommands 开始");

            OpenFolderCommand = new DelegateCommand(ExecuteOpenFolder);
            NewDocumentCommand = new DelegateCommand(ExecuteNewDocument);
            SaveCommand = new DelegateCommand(ExecuteSave);
            SaveAsCommand = new DelegateCommand(ExecuteSaveAs); // 新增
            GoBackCommand = new DelegateCommand(ExecuteGoBack, CanExecuteGoBack);
            GoForwardCommand = new DelegateCommand(ExecuteGoForward, CanExecuteGoForward);
            UndoCommand = new DelegateCommand(ExecuteZoomOut); // 缩小功能
            RedoCommand = new DelegateCommand(ExecuteZoomIn); // 放大功能
            FitToViewCommand = new DelegateCommand(ExecuteFitToView); // 自适应功能
            DeleteCommand = new DelegateCommand(ExecuteDelete);
            SettingsCommand = new DelegateCommand(ExecuteSettings);
            StartCommand = new DelegateCommand(ExecuteStart, CanExecuteStart);
            PauseCommand = new DelegateCommand(ExecutePause, CanExecutePause);
            StopCommand = new DelegateCommand(ExecuteStop, CanExecuteStop);
            StatusCommand = new DelegateCommand(ExecuteStatus);

            System.Diagnostics.Debug.WriteLine("MainToolBarViewModel InitializeCommands 完成");
        }

        private void LoadLocalizedStrings()
        {
            TooltipOpenFolder = _localizationService.GetString("ToolTip_OpenFolder");
            TooltipNewDocument = _localizationService.GetString("ToolTip_NewDocument");
            TooltipSave = _localizationService.GetString("ToolTip_Save");
            TooltipSaveAs = _localizationService.GetString("Tooltip_SaveAs");
            TooltipNew = _localizationService.GetString("ToolTip_New");
            TooltipUndo = _localizationService.GetString("ToolTip_Undo");
            TooltipRedo = _localizationService.GetString("ToolTip_Redo");
            TooltipDelete = _localizationService.GetString("ToolTip_Delete");
            TooltipZoomIn = _localizationService.GetString("ToolTip_ZoomIn");
            TooltipZoomOut = _localizationService.GetString("ToolTip_ZoomOut");
            TooltipFitCanvas = _localizationService.GetString("ToolTip_FitCanvas");
            TooltipCanvasSettings = _localizationService.GetString("ToolTip_CanvasSettings");
            TooltipDataAnalysis = _localizationService.GetString("ToolTip_DataAnalysis");
            TooltipLog = _localizationService.GetString("ToolTip_Log");
            TooltipStart = _localizationService.GetString("ToolTip_Start");
            TooltipPause = _localizationService.GetString("ToolTip_Pause");
            TooltipStop = _localizationService.GetString("ToolTip_Stop");
            TooltipSimulationMode = _localizationService.GetString("ToolTip_SimulationMode");
            ButtonStart = _localizationService.GetString("Button_Start");
            ButtonPause = _localizationService.GetString("Button_Pause");
            ButtonStop = _localizationService.GetString("Button_Stop");
            TextSimulationMode = _localizationService.GetString("Text_SimulationMode");
        }
        private void SubscribeExecutionEvents()
        {
            _flowExecutionEngine.StateChanged += OnFlowExecutionStateChanged;
            _flowExecutionEngine.NodeExecuting += OnNodeExecuting;
            _flowExecutionEngine.NodeExecuted += OnNodeExecuted;
            _flowExecutionEngine.ExecutionError += OnExecutionError;
            _flowExecutionEngine.ProgressChanged += OnExecutionProgressChanged;
        }
        private void OnFlowExecutionStateChanged(object sender, FlowExecutionStateChangedEventArgs e)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _isExecuting = e.NewState == FlowExecutionState.Running || e.NewState == FlowExecutionState.Paused;

                // 更新所有相关命令的状态
                StartCommand.RaiseCanExecuteChanged();
                PauseCommand.RaiseCanExecuteChanged();
                StopCommand.RaiseCanExecuteChanged();

                // 更新状态消息
                var statusMessage = e.NewState switch
                {
                    FlowExecutionState.Idle => "就绪",
                    FlowExecutionState.Running => "流程执行中...",
                    FlowExecutionState.Paused => "流程已暂停",
                    FlowExecutionState.Completed => "流程执行完成",
                    FlowExecutionState.Error => "流程执行出错",
                    FlowExecutionState.Stopped => "流程已停止",
                    FlowExecutionState.Stopping => "正在停止...",
                    _ => "未知状态"
                };

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(statusMessage);

                System.Diagnostics.Debug.WriteLine($"执行引擎状态变化: {e.OldState} -> {e.NewState}");
                System.Diagnostics.Debug.WriteLine($"Start按钮可用: {CanExecuteStart()}");
                System.Diagnostics.Debug.WriteLine($"Pause按钮可用: {CanExecutePause()}");
                System.Diagnostics.Debug.WriteLine($"Stop按钮可用: {CanExecuteStop()}");
            });
        }

        private void OnNodeExecuting(object sender, NodeExecutionEventArgs e)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"正在执行: {e.Node.DisplayName}");
            });
        }

        private void OnNodeExecuted(object sender, NodeExecutionEventArgs e)
        {
            var status = e.IsSuccessful ? "完成" : "失败";
            var message = $"节点 {e.Node.DisplayName} 执行{status}";
            if (!e.IsSuccessful && !string.IsNullOrEmpty(e.ErrorMessage))
            {
                message += $": {e.ErrorMessage}";
            }

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(message);
            });
        }

        private void OnExecutionError(object sender, FlowExecutionErrorEventArgs e)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _dialogService?.ShowError($"流程执行错误: {e.ErrorMessage}", "执行错误");
            });
        }

        private void OnExecutionProgressChanged(object sender, FlowExecutionProgressEventArgs e)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var message = $"执行进度: {e.CompletedNodes}/{e.TotalNodes} ({e.ProgressPercentage:F1}%)";
                if (e.CurrentNode != null)
                {
                    message += $" - {e.CurrentNode.DisplayName}";
                }
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(message);
            });
        }
        private void SubscribeEvents()
        {
            _eventAggregator.GetEvent<LanguageChangedEvent>().Subscribe(OnLanguageChanged);

            // 新增：订阅画布修改事件
            _eventAggregator.GetEvent<CanvasModifiedEvent>().Subscribe(OnCanvasModified);

            // 新增：订阅文件路径相关事件
            _eventAggregator.GetEvent<GetCurrentFilePathRequestEvent>().Subscribe(OnGetCurrentFilePathRequest);
            _eventAggregator.GetEvent<SetCurrentFilePathEvent>().Subscribe(OnSetCurrentFilePath);

            _commandHistory.CanUndoRedoChanged += OnCanUndoRedoChanged;
        }
        private void OnCanUndoRedoChanged(bool canUndo, bool canRedo)
        {
            CanUndo = canUndo;
            CanRedo = canRedo;

            // 更新命令状态
            GoBackCommand.RaiseCanExecuteChanged();
            GoForwardCommand.RaiseCanExecuteChanged();

            System.Diagnostics.Debug.WriteLine($"撤销重做状态更新: CanUndo={canUndo}, CanRedo={canRedo}");
        }
        private void OnLanguageChanged(string cultureName)
        {
            LoadLocalizedStrings();
        }

        private void OnCanvasModified()
        {
            _canvasStateService.MarkAsModified();
        }
        private void OnGetCurrentFilePathRequest(Action<string> callback)
        {
            callback?.Invoke(_canvasStateService.CurrentFilePath);
            System.Diagnostics.Debug.WriteLine($"返回当前文件路径: {_canvasStateService.CurrentFilePath ?? "无"}");
        }

        private void OnSetCurrentFilePath(string filePath)
        {
            _canvasStateService.SetCurrentFile(filePath);
            System.Diagnostics.Debug.WriteLine($"当前文件路径已更新: {filePath}");
        }
        private (double, double) GetCurrentCanvasSize()
        {
            double width = 1200, height = 800;

            try
            {
                var resetEvent = new ManualResetEventSlim(false);

                _eventAggregator?.GetEvent<GetCanvasSizeRequestEvent>()?.Publish((w, h) =>
                {
                    width = w;
                    height = h;
                    resetEvent.Set();
                });

                if (!resetEvent.Wait(TimeSpan.FromSeconds(1)))
                {
                    // 超时处理
                }
            }
            catch (Exception ex)
            {
                // 异常处理
            }

            return (width, height);
        }

        private RegionLayout CreateDefaultRegionLayout()
        {
            var layout = new RegionLayout
            {
                Rows = 2,
                Cols = 2,
                UseIndependentRegions = true,
                IndependentRegions = new List<IndependentRegion>(),
                Cells = new List<RegionCell>
                {
                    new RegionCell { Row = 0, Col = 0, Type = RegionType.Feed },
                    new RegionCell { Row = 0, Col = 1, Type = RegionType.Reaction },
                    new RegionCell { Row = 1, Col = 0, Type = RegionType.PostProcess },
                    new RegionCell { Row = 1, Col = 1, Type = RegionType.PostProcess },
                }
            };

            return layout;
        }

        #region Command Implementations

        private void ExecuteOpenFolder()
        {
            try
            {
                // 1. 检查当前画布是否有未保存的修改
                if (_canvasStateService.HasUnsavedChanges)
                {
                    var result = _dialogService.ShowConfirmation(
                        "当前画布有未保存的修改，是否先保存？",
                        "打开文件");

                    if (result)
                    {
                        ExecuteSave();
                        // 如果保存失败，取消打开操作
                        if (_canvasStateService.HasUnsavedChanges)
                        {
                            return;
                        }
                    }
                }

                // 2. 显示打开文件对话框
                var fileName = _dialogService.ShowOpenFileDialog(
                    "MaxChemical 画布文件|*.mcc|JSON 文件|*.json|所有文件|*.*");

                if (string.IsNullOrEmpty(fileName))
                    return;

                // 3. 加载画布文档
                var canvasDocument = _canvasDataService.LoadCanvas(fileName);

                if (canvasDocument == null)
                {
                    _dialogService.ShowError("无法加载画布文件，文件可能已损坏", "打开失败");
                    return;
                }

                // 4. 设置当前文件路径
                _canvasStateService.SetCurrentFile(fileName);

                // 5. 清空当前画布并加载新内容
                _eventAggregator?.GetEvent<ClearCanvasRequestEvent>()?.Publish();

                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (canvasDocument.RegionLayout != null)
                        {
                            CurrentRegionLayout = canvasDocument.RegionLayout;
                            _eventAggregator?.GetEvent<RegionLayoutUpdatedEvent>()?.Publish(canvasDocument.RegionLayout);
                        }

                        _eventAggregator?.GetEvent<LoadCanvasRequestEvent>()?.Publish(canvasDocument);
                        _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"画布加载成功: {System.IO.Path.GetFileName(fileName)}");
                        _eventAggregator?.GetEvent<CanvasLoadedEvent>()?.Publish(canvasDocument);
                    }
                    catch (Exception ex)
                    {
                        _dialogService.ShowError($"加载画布内容失败: {ex.Message}", "加载失败");
                    }
                }), System.Windows.Threading.DispatcherPriority.Normal);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"打开文件失败: {ex.Message}", "打开失败");
            }
        }
        private void ExecuteNewDocument()
        {
            try
            {
                if (_canvasStateService.HasUnsavedChanges)
                {
                    var result = _dialogService.ShowConfirmation(
                        "当前画布有未保存的修改，是否先保存？",
                        "新建画布");

                    if (result)
                    {
                        ExecuteSave();
                        if (_canvasStateService.HasUnsavedChanges)
                        {
                            return;
                        }
                    }
                }

                var (canvasWidth, canvasHeight) = GetCurrentCanvasSize();
                var newCanvas = _canvasDataService.CreateNewCanvas(canvasWidth, canvasHeight);

                CurrentRegionLayout = newCanvas.RegionLayout;

                // 重置状态（清空当前文件路径）
                _canvasStateService.Reset();

                _eventAggregator?.GetEvent<ClearCanvasRequestEvent>()?.Publish();

                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    _eventAggregator?.GetEvent<RegionLayoutUpdatedEvent>()?.Publish(newCanvas.RegionLayout);
                    _eventAggregator?.GetEvent<NewCanvasCreatedEvent>()?.Publish(newCanvas);
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"新建画布创建成功 (尺寸: {canvasWidth:F0} x {canvasHeight:F0})");
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"创建新画布失败: {ex.Message}", "错误");
            }
        }
        private void ExecuteSave()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("执行保存命令");
                System.Diagnostics.Debug.WriteLine($"当前文件路径: {_canvasStateService.CurrentFilePath ?? "无"}");

                // 如果有当前文件路径，直接保存；否则执行另存为
                if (!string.IsNullOrEmpty(_canvasStateService.CurrentFilePath))
                {
                    // 直接保存到现有文件
                    SaveToFile(_canvasStateService.CurrentFilePath);
                }
                else
                {
                    // 没有当前文件路径，执行另存为
                    System.Diagnostics.Debug.WriteLine("没有当前文件路径，执行另存为");
                    ExecuteSaveAs();
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"保存失败: {ex.Message}", "保存失败");
            }
        }
        // 新增另存为方法
        private void ExecuteSaveAs()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("执行另存为命令");

                var fileName = _dialogService.ShowSaveFileDialog(
                    "MaxChemical 画布文件|*.mcc|JSON 文件|*.json|所有文件|*.*",
                    ".mcc");

                if (string.IsNullOrEmpty(fileName))
                {
                    System.Diagnostics.Debug.WriteLine("用户取消了另存为操作");
                    return;
                }

                // 保存到指定文件
                SaveToFile(fileName);

                // 更新当前文件路径
                _canvasStateService.SetCurrentFile(fileName);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"另存为失败: {ex.Message}", "另存为失败");
            }
        }

        // 提取公共保存逻辑
        private void SaveToFile(string fileName)
        {
            System.Diagnostics.Debug.WriteLine($"开始保存文件到: {fileName}");

            var currentCanvasData = _canvasDataService.GetCurrentCanvasData();

            if (currentCanvasData == null)
            {
                _dialogService.ShowError("无法获取当前画布数据", "保存失败");
                return;
            }

            // 设置文档名称
            currentCanvasData.Name = System.IO.Path.GetFileNameWithoutExtension(fileName);

            bool success = _canvasDataService.SaveCanvas(currentCanvasData, fileName);

            if (success)
            {
                // 标记为已保存
                _canvasStateService.MarkAsSaved();

                _eventAggregator?.GetEvent<CanvasSavedEvent>()?.Publish(fileName);
                _eventAggregator?.GetEvent<FileSavedEvent>()?.Publish(fileName);
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"画布已保存到: {System.IO.Path.GetFileName(fileName)}");

                System.Diagnostics.Debug.WriteLine($"文件保存成功: {fileName}");
                _dialogService.ShowInfo("画布保存成功", "保存");
            }
            else
            {
                _dialogService.ShowError("画布保存失败", "保存");
            }
        }
        private void ExecuteGoBack()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("执行撤销操作");
                var description = _commandHistory.GetUndoDescription();

                _commandHistory.Undo();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"已撤销: {description}");
                System.Diagnostics.Debug.WriteLine($"撤销完成: {description}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"撤销失败: {ex.Message}");
                _dialogService?.ShowError($"撤销操作失败: {ex.Message}", "撤销失败");
            }
        }
        private void ExecuteGoForward()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("执行重做操作");
                var description = _commandHistory.GetRedoDescription();

                _commandHistory.Redo();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"已重做: {description}");
                System.Diagnostics.Debug.WriteLine($"重做完成: {description}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"重做失败: {ex.Message}");
                _dialogService?.ShowError($"重做操作失败: {ex.Message}", "重做失败");
            }
        }
        private bool CanExecuteGoBack()
        {
            return _commandHistory?.CanUndo ?? false;
        }

        private bool CanExecuteGoForward()
        {
            return _commandHistory?.CanRedo ?? false;
        }
        private void ExecuteDelete()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("执行删除命令");

                // 获取当前选中的设备
                var selectedDeviceId = GetCurrentSelectedDevice();

                if (!string.IsNullOrEmpty(selectedDeviceId))
                {
                    // 有选中设备，删除设备及其连接
                    System.Diagnostics.Debug.WriteLine($"删除选中设备: {selectedDeviceId}");

                    // 显示确认对话框
                    var result = _dialogService.ShowConfirmation(
                        $"确定要删除选中的设备吗？\n\n此操作将同时删除与该设备相关的所有连接线。",
                        "删除确认");

                    if (result)
                    {
                        // 发布删除设备事件
                        _eventAggregator?.GetEvent<DeleteSelectedDeviceEvent>()?.Publish(selectedDeviceId);
                        _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("设备已删除");
                    }
                }
                else
                {
                    // 没有选中设备，尝试删除选中的连接线
                    System.Diagnostics.Debug.WriteLine("没有选中设备，尝试删除选中的连接线");
                    _eventAggregator?.GetEvent<DeleteSelectedConnectionsEvent>()?.Publish();
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("已删除选中的连接线");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"删除操作失败: {ex.Message}");
                _dialogService?.ShowError($"删除操作失败: {ex.Message}", "删除失败");
            }
        }
        #region Zoom Command Implementations

        // 放大功能（原 Redo）
        private void ExecuteZoomIn()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("执行放大命令");

                var selectedDeviceId = GetCurrentSelectedDevice();

                if (!string.IsNullOrEmpty(selectedDeviceId))
                {
                    // 有选中设备，放大设备
                    System.Diagnostics.Debug.WriteLine($"放大选中设备: {selectedDeviceId}");
                    _eventAggregator?.GetEvent<ZoomSelectedDeviceEvent>()?.Publish(1.0 + _deviceZoomStep);
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("设备已放大");
                }
                else
                {
                    // 没有选中设备，放大画布
                    System.Diagnostics.Debug.WriteLine("放大画布");
                    var currentZoom = GetCurrentCanvasZoom();
                    var newZoom = Math.Min(currentZoom + _zoomStep, 3.0); // 最大3倍
                    _eventAggregator?.GetEvent<ZoomCanvasEvent>()?.Publish(newZoom);
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"画布已放大到 {newZoom:P0}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"放大操作失败: {ex.Message}");
                _dialogService?.ShowError($"放大操作失败: {ex.Message}", "错误");
            }
        }

        // 缩小功能（原 Undo）
        private void ExecuteZoomOut()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("执行缩小命令");

                var selectedDeviceId = GetCurrentSelectedDevice();

                if (!string.IsNullOrEmpty(selectedDeviceId))
                {
                    // 有选中设备，缩小设备
                    System.Diagnostics.Debug.WriteLine($"缩小选中设备: {selectedDeviceId}");
                    _eventAggregator?.GetEvent<ZoomSelectedDeviceEvent>()?.Publish(1.0 - _deviceZoomStep);
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("设备已缩小");
                }
                else
                {
                    // 没有选中设备，缩小画布
                    System.Diagnostics.Debug.WriteLine("缩小画布");
                    var currentZoom = GetCurrentCanvasZoom();
                    var newZoom = Math.Max(currentZoom - _zoomStep, 0.1); // 最小10%
                    _eventAggregator?.GetEvent<ZoomCanvasEvent>()?.Publish(newZoom);
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"画布已缩小到 {newZoom:P0}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"缩小操作失败: {ex.Message}");
                _dialogService?.ShowError($"缩小操作失败: {ex.Message}", "错误");
            }
        }

        // 画布自适应大小
        private void ExecuteFitToView()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("执行画布自适应命令");
                _eventAggregator?.GetEvent<FitCanvasToViewEvent>()?.Publish();
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("画布已自适应显示");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"画布自适应失败: {ex.Message}");
                _dialogService?.ShowError($"画布自适应失败: {ex.Message}", "错误");
            }
        }

        #endregion

        #region Helper Methods

        // 获取当前选中的设备
        private string GetCurrentSelectedDevice()
        {
            string selectedDeviceId = null;
            var waitHandle = new ManualResetEventSlim(false);

            try
            {
                _eventAggregator?.GetEvent<GetSelectedDeviceRequestEvent>()?.Publish((deviceId) =>
                {
                    selectedDeviceId = deviceId;
                    waitHandle.Set();
                });

                // 等待500毫秒，如果没有响应就认为没有选中设备
                waitHandle.Wait(500);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取选中设备失败: {ex.Message}");
            }
            finally
            {
                waitHandle.Dispose();
            }

            System.Diagnostics.Debug.WriteLine($"当前选中设备: {selectedDeviceId ?? "无"}");
            return selectedDeviceId;
        }

        // 获取当前画布缩放级别
        private double GetCurrentCanvasZoom()
        {
            double currentZoom = 1.0;
            var waitHandle = new ManualResetEventSlim(false);

            try
            {
                _eventAggregator?.GetEvent<GetCanvasZoomRequestEvent>()?.Publish((zoom) =>
                {
                    currentZoom = zoom;
                    waitHandle.Set();
                });

                // 等待500毫秒
                waitHandle.Wait(500);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取画布缩放级别失败: {ex.Message}");
            }
            finally
            {
                waitHandle.Dispose();
            }

            System.Diagnostics.Debug.WriteLine($"当前画布缩放级别: {currentZoom}");
            return currentZoom;
        }

        #endregion
        private bool CanExecuteStart()
        {
            return _flowExecutionEngine.State == FlowExecutionState.Idle ||
                   _flowExecutionEngine.State == FlowExecutionState.Paused ||
                   _flowExecutionEngine.State == FlowExecutionState.Completed ||
                   _flowExecutionEngine.State == FlowExecutionState.Stopped ||
                   _flowExecutionEngine.State == FlowExecutionState.Error;
        }

        private bool CanExecutePause()
        {
            return _flowExecutionEngine.State == FlowExecutionState.Running;
        }

        private bool CanExecuteStop()
        {
            return _flowExecutionEngine.State == FlowExecutionState.Running ||
                   _flowExecutionEngine.State == FlowExecutionState.Paused;
        }
        private void ExecuteStart()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"执行Start命令，当前状态: {_flowExecutionEngine.State}");

                if (_flowExecutionEngine.State == FlowExecutionState.Paused)
                {
                    // 恢复执行
                    _ = _flowExecutionEngine.ResumeAsync();
                    _logger.LogInformation("恢复流程执行");
                }
                else if (_flowExecutionEngine.State == FlowExecutionState.Idle ||
                         _flowExecutionEngine.State == FlowExecutionState.Completed ||
                         _flowExecutionEngine.State == FlowExecutionState.Stopped ||
                         _flowExecutionEngine.State == FlowExecutionState.Error)
                {
                    // 开始新的执行
                    var flowViewModel = GetCurrentFlowDesigner();
                    if (flowViewModel == null)
                    {
                        _dialogService?.ShowError("没有找到流程设计器", "执行失败");
                        return;
                    }

                    if (!flowViewModel.CommandNodes.Any())
                    {
                        _dialogService?.ShowError("流程中没有任何节点，请先添加节点", "执行失败");
                        return;
                    }

                    // 验证流程
                    var validation = _flowExecutionEngine.ValidateFlow(flowViewModel);
                    if (!validation.IsValid)
                    {
                        var errorMessage = "流程验证失败:\n" + string.Join("\n", validation.Errors);
                        if (validation.Warnings.Any())
                        {
                            errorMessage += "\n\n警告:\n" + string.Join("\n", validation.Warnings);
                        }
                        _dialogService?.ShowError(errorMessage, "验证失败");
                        return;
                    }

                    // 如果有警告，询问是否继续
                    if (validation.Warnings.Any())
                    {
                        var warningMessage = "流程验证通过，但有以下警告:\n" + string.Join("\n", validation.Warnings) + "\n\n是否继续执行？";
                        if (!_dialogService.ShowConfirmation(warningMessage, "执行确认"))
                        {
                            return;
                        }
                    }

                    // 异步执行流程
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var result = await _flowExecutionEngine.ExecuteAsync(flowViewModel);

                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            {
                                if (result.IsSuccessful)
                                {
                                    var message = $"流程执行成功！\n耗时: {result.Duration:mm\\:ss\\.fff}\n执行节点数: {result.TotalNodesExecuted}";
                                    _dialogService?.ShowInfo(message, "执行完成");
                                }
                                else
                                {
                                    var message = $"流程执行失败！\n错误: {result.ErrorMessage}\n耗时: {result.Duration:mm\\:ss\\.fff}";
                                    _dialogService?.ShowError(message, "执行失败");
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                            {
                                _dialogService?.ShowError($"启动流程执行失败: {ex.Message}", "启动失败");
                            });
                        }
                    });

                    _logger.LogInformation("开始执行流程");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"不支持的执行状态: {_flowExecutionEngine.State}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "执行开始命令失败");
                _dialogService?.ShowError($"执行失败: {ex.Message}", "错误");
            }
        }

        private void ExecutePause()
        {
            try
            {
                _ = _flowExecutionEngine.PauseAsync();
                _logger.LogInformation("暂停流程执行");
                _dialogService.ShowInfo("暂停流程执行");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "暂停流程失败");
                _dialogService?.ShowError($"暂停失败: {ex.Message}", "错误");
            }
        }

        private void ExecuteStop()
        {
            try
            {
                if (_flowExecutionEngine.State == FlowExecutionState.Running ||
                    _flowExecutionEngine.State == FlowExecutionState.Paused)
                {
                    var result = _dialogService?.ShowConfirmation("确定要停止流程执行吗？", "停止确认");
                    if (result == true)
                    {
                        _ = _flowExecutionEngine.StopAsync();
                        _logger.LogInformation("停止流程执行");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "停止流程失败");
                _dialogService?.ShowError($"停止失败: {ex.Message}", "错误");
            }
        }

        private void ExecuteStatus() => ShowMessage("ToolTip_SimulationMode");
        private FlowDesignerViewModel GetCurrentFlowDesigner()
        {
            FlowDesignerViewModel flowViewModel = null;
            var resetEvent = new ManualResetEventSlim(false);

            try
            {
                _eventAggregator?.GetEvent<GetCurrentFlowDesignerRequestEvent>()?.Publish((viewModel) =>
                {
                    flowViewModel = viewModel;
                    resetEvent.Set();
                });

                if (!resetEvent.Wait(TimeSpan.FromSeconds(1)))
                {
                    _logger.LogWarning("获取流程设计器超时");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取流程设计器失败");
            }
            finally
            {
                resetEvent.Dispose();
            }

            return flowViewModel;
        }
        private void ShowMessage(string messageKey)
        {
            var message = _localizationService.GetString(messageKey);
            var title = _localizationService.GetString("Message_Toolbar");
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ExecuteSettings()
        {
            try
            {
                var initialLayout = CurrentRegionLayout ?? CreateDefaultRegionLayout();

                _regionSettingsDialogProvider.ShowWindow(
                    initialLayout,
                    () => (1140, 650),
                    () => GetCurrentCanvasSize(),
                    (newLayout) =>
                    {
                        if (newLayout != null)
                        {
                            CurrentRegionLayout = newLayout;
                            _eventAggregator?.GetEvent<RegionLayoutUpdatedEvent>()?.Publish(newLayout);
                        }
                    });
            }
            catch
            {
                var errorMessage = _localizationService.GetString("Message_SettingsDialogFailed");
                var errorTitle = _localizationService.GetString("Message_Error");
                MessageBox.Show(errorMessage, errorTitle, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion
    }
}