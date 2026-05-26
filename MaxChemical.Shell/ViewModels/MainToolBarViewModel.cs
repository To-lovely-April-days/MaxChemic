using MaxChemical.Core;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Service;
using MaxChemical.Modules.Designer.Service.Execution;
using MaxChemical.Modules.Designer.Services;
using MaxChemical.Modules.Designer.ViewModels;
using MaxChemical.Modules.Designer.Views;
using MaxChemical.Modules.DOE.ViewModels;
using MaxChemical.Modules.DOE.Views;
using MaxChemical.Shell.Views;
using Microsoft.Extensions.DependencyInjection;
using Prism.Commands;
using Prism.Events;
using Prism.Ioc;
using Prism.Mvvm;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace MaxChemical.Shell.ViewModels
{
    public class MainToolBarViewModel : BindableBase
    {
        private readonly IContainerProvider _container;
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

        private readonly IProjectDataService _projectDataService; // 替换为项目数据服务
        private readonly ProjectCoordinator _projectCoordinator; // 添加项目协调器
        private readonly IProjectNameDialogProvider _projectNameDialogProvider;
        private IParameterMonitoringService _parameterMonitoringService;
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
        private string _tooltipPredictiveAssessment = string.Empty;
        private string _tooltipDoE = string.Empty;
        private string _tooltipStart = string.Empty;
        private string _tooltipPause = string.Empty;
        private string _tooltipStop = string.Empty;
        private string _tooltipSimulationMode = string.Empty;
        private string _buttonStart = string.Empty;
        private string _buttonPause = string.Empty;
        private string _buttonStop = string.Empty;
        private string _textSimulationMode = string.Empty;

        public MainToolBarViewModel(
            IContainerProvider container,
          IEventAggregator eventAggregator,
          IRegionSettingsDialogProvider regionSettingsDialogProvider,
          ILocalizationService localizationService,
          IProjectDataService projectDataService,
          ICanvasDataService canvasDataService,
          IDialogService dialogService,
          ICanvasStateService canvasStateService,
          ICommandHistory commandHistory,
          IFlowExecutionEngine flowExecutionEngine,
          ProjectCoordinator projectCoordinator,
          IProjectNameDialogProvider projectNameDialogProvider) // 新增参数
        {
            _container = container ?? throw new ArgumentNullException(nameof(container));
            _logger = new LogService().ForContext<MainToolBarViewModel>();
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _regionSettingsDialogProvider = regionSettingsDialogProvider ?? throw new ArgumentNullException(nameof(regionSettingsDialogProvider));
            _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));
            _projectDataService = projectDataService ?? throw new ArgumentNullException(nameof(projectDataService));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            _canvasStateService = canvasStateService ?? throw new ArgumentNullException(nameof(canvasStateService));
            _commandHistory = commandHistory ?? throw new ArgumentNullException(nameof(commandHistory));
            _flowExecutionEngine = flowExecutionEngine ?? throw new ArgumentNullException(nameof(flowExecutionEngine));
            _projectCoordinator = projectCoordinator ?? throw new ArgumentNullException(nameof(projectCoordinator));
            _canvasDataService= canvasDataService ?? throw new ArgumentNullException(nameof(canvasDataService));
            _projectNameDialogProvider = projectNameDialogProvider ?? throw new ArgumentNullException(nameof(projectNameDialogProvider));
            _parameterMonitoringService= _container.Resolve<IParameterMonitoringService>();
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
        public string TooltipPredictiveAssessment
        {
            get => _tooltipPredictiveAssessment;
            set => SetProperty(ref _tooltipPredictiveAssessment, value);
        }
        public string TooltipDoE
        {
            get => _tooltipDoE;
            set => SetProperty(ref _tooltipDoE, value);
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


        // 新增：模拟模式状态
        private bool _isSimulationMode = false;

        public bool IsSimulationMode
        {
            get => _isSimulationMode;
            set
            {
                if (SetProperty(ref _isSimulationMode, value))
                {
                    // 状态改变时更新UI和通知设备
                    OnSimulationModeChanged();
                }
            }
        }

        // 新增：开关动画状态
        private double _switchPosition = 10; // 开关位置，2=左侧，26=右侧

        public double SwitchPosition
        {
            get => _switchPosition;
            set => SetProperty(ref _switchPosition, value);
        }

        // 开关背景色(默认实际模式 = 灰)
        private string _switchBackgroundColor = "#D3D1C7";

        public string SwitchBackgroundColor
        {
            get => _switchBackgroundColor;
            set => SetProperty(ref _switchBackgroundColor, value);
        }

        // 开关边框色(默认实际模式 = 灰)
        private string _switchBorderColor = "#D3D1C7";

        public string SwitchBorderColor
        {
            get => _switchBorderColor;
            set => SetProperty(ref _switchBorderColor, value);
        }

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
        public DelegateCommand PredictiveAssessmentCommand { get; set; }//数据分析预测
        public DelegateCommand DOECommand { get; private set; }
        public DelegateCommand SettingsChartCommand { get; private set; }
        public DelegateCommand StartCommand { get; private set; }
        public DelegateCommand PauseCommand { get; private set; }
        public DelegateCommand StopCommand { get; private set; }
        public DelegateCommand StatusCommand { get; private set; }
        public DelegateCommand ShowLogViewCommand { get; private set; }

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
            SettingsChartCommand = new DelegateCommand(ShowParameterMonitorChart);
            PredictiveAssessmentCommand = new DelegateCommand(PredictiveAssessment);
            StartCommand = new DelegateCommand(ExecuteStart, CanExecuteStart);
            PauseCommand = new DelegateCommand(ExecutePause, CanExecutePause);
            StopCommand = new DelegateCommand(ExecuteStop, CanExecuteStop);
            StatusCommand = new DelegateCommand(ExecuteStatus);
            DOECommand = new DelegateCommand(ExecuteOpenDOE);
            ShowLogViewCommand = new DelegateCommand(ExecuteShowLogViewCommand);
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
            TooltipPredictiveAssessment = _localizationService.GetString("ToolTip_PredictiveAssessment");
            TooltipDoE = _localizationService.GetString("ToolTip_DOE");
            TooltipStart = _localizationService.GetString("ToolTip_Start");
            TooltipPause = _localizationService.GetString("ToolTip_Pause");
            TooltipStop = _localizationService.GetString("ToolTip_Stop");
            TooltipSimulationMode = _localizationService.GetString("ToolTip_SimulationMode");
            ButtonStart = _localizationService.GetString("Button_Start");
            ButtonPause = _localizationService.GetString("Button_Pause");
            ButtonStop = _localizationService.GetString("Button_Stop");
            TextSimulationMode = _localizationService.GetString("Text_SimulationMode");
            UpdateSimulationModeText();
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
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
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
                // *** 新增：更新工具提示文本 ***
                UpdateExecutionTooltips(e.NewState);
                System.Diagnostics.Debug.WriteLine($"执行引擎状态变化: {e.OldState} -> {e.NewState}");
                System.Diagnostics.Debug.WriteLine($"Start按钮可用: {CanExecuteStart()}");
                System.Diagnostics.Debug.WriteLine($"Pause按钮可用: {CanExecutePause()}");
                System.Diagnostics.Debug.WriteLine($"Stop按钮可用: {CanExecuteStop()}");
            });
        }
        /// <summary>
        /// 根据执行状态更新按钮工具提示
        /// </summary>
        private void UpdateExecutionTooltips(FlowExecutionState state)
        {
            switch (state)
            {
                case FlowExecutionState.Idle:
                case FlowExecutionState.Completed:
                case FlowExecutionState.Stopped:
                case FlowExecutionState.Error:
                    TooltipStart = _localizationService.GetString("ToolTip_Start") ?? "开始执行流程";
                    TooltipPause = _localizationService.GetString("ToolTip_Pause_Disabled") ?? "暂停（需要先开始执行）";
                    TooltipStop = _localizationService.GetString("ToolTip_Stop_Disabled") ?? "停止（需要先开始执行）";
                    break;

                case FlowExecutionState.Running:
                    TooltipStart = _localizationService.GetString("ToolTip_Start_Disabled") ?? "开始（流程正在执行中）";
                    TooltipPause = _localizationService.GetString("ToolTip_Pause") ?? "暂停流程执行";
                    TooltipStop = _localizationService.GetString("ToolTip_Stop") ?? "停止流程执行";
                    break;

                case FlowExecutionState.Paused:
                    TooltipStart = _localizationService.GetString("ToolTip_Resume") ?? "恢复流程执行";
                    TooltipPause = _localizationService.GetString("ToolTip_Pause_Disabled") ?? "暂停（流程已暂停）";
                    TooltipStop = _localizationService.GetString("ToolTip_Stop") ?? "停止流程执行";
                    break;

                case FlowExecutionState.Stopping:
                    TooltipStart = _localizationService.GetString("ToolTip_Start_Disabled") ?? "开始（流程正在停止中）";
                    TooltipPause = _localizationService.GetString("ToolTip_Pause_Disabled") ?? "暂停（流程正在停止中）";
                    TooltipStop = _localizationService.GetString("ToolTip_Stop_Disabled") ?? "停止（流程正在停止中）";
                    break;
            }
        }
        private void OnNodeExecuting(object sender, NodeExecutionEventArgs e)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
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

            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish(message);
            });
        }

        private void OnExecutionError(object sender, FlowExecutionErrorEventArgs e)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                _dialogService?.ShowError($"流程执行错误: {e.ErrorMessage}", "执行错误");
            });
        }

        private void OnExecutionProgressChanged(object sender, FlowExecutionProgressEventArgs e)
        {
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
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

            _eventAggregator.GetEvent<GetSimulationModeRequestEvent>().Subscribe(OnGetSimulationModeRequest);

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

        // *** 新增：响应模拟模式状态请求 ***
        private void OnGetSimulationModeRequest(Action<bool> callback)
        {
            try
            {
                callback?.Invoke(IsSimulationMode);
                _logger.LogDebug("响应模拟模式状态请求: {Mode}", IsSimulationMode ? "模拟模式" : "实际模式");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "响应模拟模式状态请求失败");
            }
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

        #region 文件操作命令

        /// <summary>
        /// 统一的保存功能 - 保存整个项目（包含画布和流程）
        /// </summary>
        private void ExecuteSave()
        {
            try
            {
                Debug.WriteLine("执行统一保存命令");
                Debug.WriteLine($"当前文件路径: {_canvasStateService.CurrentFilePath ?? "无"}");

                // 获取当前完整项目数据
                var currentProjectData = _projectDataService.GetCurrentProjectData();
                if (currentProjectData == null)
                {
                    _dialogService.ShowError("无法获取当前项目数据", "保存失败");
                    return;
                }

                // 如果有当前文件路径，直接保存；否则执行另存为
                if (!string.IsNullOrEmpty(_canvasStateService.CurrentFilePath))
                {
                    SaveProjectToFile(currentProjectData, _canvasStateService.CurrentFilePath);
                }
                else
                {
                    ExecuteSaveAs();
                }
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"保存失败: {ex.Message}", "保存失败");
            }
        }

        /// <summary>
        /// 统一的另存为功能
        /// </summary>
        private void ExecuteSaveAs()
        {
            try
            {
                Debug.WriteLine("执行统一另存为命令");

                var fileName = _dialogService.ShowSaveFileDialog(
                    "MaxChemical 项目文件|*.mcp|JSON 文件|*.json|所有文件|*.*",
                    ".mcp");

                if (string.IsNullOrEmpty(fileName))
                {
                    Debug.WriteLine("用户取消了另存为操作");
                    return;
                }

                var currentProjectData = _projectDataService.GetCurrentProjectData();
                if (currentProjectData == null)
                {
                    _dialogService.ShowError("无法获取当前项目数据", "另存为失败");
                    return;
                }

                SaveProjectToFile(currentProjectData, fileName);
                _canvasStateService.SetCurrentFile(fileName);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"另存为失败: {ex.Message}", "另存为失败");
            }
        }

        /// <summary>
        /// 保存项目到文件
        /// </summary>
        private void SaveProjectToFile(ProjectDocument projectData, string fileName)
        {
            Debug.WriteLine($"开始保存项目到: {fileName}");

            // 设置项目名称
            projectData.Name = System.IO.Path.GetFileNameWithoutExtension(fileName);

            bool success = _projectDataService.SaveProject(projectData, fileName);

            if (success)
            {
                // 标记为已保存
                _canvasStateService.MarkAsSaved();

                _eventAggregator?.GetEvent<Modules.Designer.Event.ProjectSavedEvent>()?.Publish(fileName);
                _eventAggregator?.GetEvent<FileSavedEvent>()?.Publish(fileName);
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"项目已保存到: {System.IO.Path.GetFileName(fileName)}");

                Debug.WriteLine($"项目保存成功: {fileName}");
                _dialogService.ShowInfo("项目保存成功", "保存");
            }
            else
            {
                _dialogService.ShowError("项目保存失败", "保存");
            }
        }

        /// <summary>
        /// 统一的打开功能
        /// </summary>
        private void ExecuteOpenFolder()
        {
            try
            {
                // 检查当前是否有未保存的修改
                if (_canvasStateService.HasUnsavedChanges)
                {
                    var result = _dialogService.ShowConfirmation(
                        "当前项目有未保存的修改，是否先保存？",
                        "打开项目");

                    if (result)
                    {
                        ExecuteSave();
                        if (_canvasStateService.HasUnsavedChanges)
                        {
                            return;
                        }
                    }
                }

                // 显示打开文件对话框
                var fileName = _dialogService.ShowOpenFileDialog(
                    "MaxChemical 项目文件|*.mcp|JSON 文件|*.json|所有文件|*.*");

                if (string.IsNullOrEmpty(fileName))
                    return;

                // 根据文件扩展名判断文件类型
                var extension = System.IO.Path.GetExtension(fileName).ToLower();

                if (extension == ".mcp")
                {
                    // 加载项目文件
                    LoadProjectFile(fileName);
                }
               

            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"打开文件失败: {ex.Message}", "打开失败");
            }
        }
        /// <summary>
        /// 获取当前文件的显示名称
        /// </summary>
        private string GetCurrentFileName()
        {
            try
            {
                var currentFilePath = _canvasStateService.CurrentFilePath;

                if (!string.IsNullOrEmpty(currentFilePath))
                {
                    // 返回不带扩展名的文件名
                    return System.IO.Path.GetFileNameWithoutExtension(currentFilePath);
                }

                // 如果没有文件路径，返回默认名称
                return "未命名流程";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取文件名失败，使用默认名称");
                return "未命名流程";
            }
        }
        /// <summary>
        /// 加载项目文件
        /// </summary>
        private void LoadProjectFile(string fileName)
        {
            // 加载项目文档
            var projectDocument = _projectDataService.LoadProject(fileName);

            if (projectDocument == null)
            {
                _dialogService.ShowError("无法加载项目文件，文件可能已损坏", "打开失败");
                return;
            }

            // 设置当前文件路径
            _canvasStateService.SetCurrentFile(fileName);

            // *** 新增：获取文件名并发布事件 ***
            var projectName = System.IO.Path.GetFileNameWithoutExtension(fileName);

            // 清空当前内容并加载新项目
            _eventAggregator?.GetEvent<ClearProjectRequestEvent>()?.Publish();

            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    // *** 新增：发布项目名称更新事件 ***
                    _eventAggregator?.GetEvent<ProjectNameUpdatedEvent>()?.Publish(projectName);

                    _eventAggregator?.GetEvent<LoadProjectRequestEvent>()?.Publish(projectDocument);
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"项目加载成功: {System.IO.Path.GetFileName(fileName)}");
                }
                catch (Exception ex)
                {
                    _dialogService.ShowError($"加载项目内容失败: {ex.Message}", "加载失败");
                }
            }), System.Windows.Threading.DispatcherPriority.Normal);
        }

     

        /// <summary>
        /// 将画布文档转换为项目文档
        /// </summary>
        private ProjectDocument ConvertCanvasToProject(CanvasDocument canvasDocument, string fileName)
        {
            var projectDocument = new ProjectDocument
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(fileName),
                Description = "从画布文件转换而来",
                CreatedTime = canvasDocument.CreatedTime,
                LastModifiedTime = canvasDocument.LastModifiedTime
            };

            // 转换画布数据
            projectDocument.CanvasDesign = new CanvasDesignData
            {
                Id = canvasDocument.Id,
                Name = canvasDocument.Name,
                CreatedTime = canvasDocument.CreatedTime,
                LastModifiedTime = canvasDocument.LastModifiedTime,
                RegionLayout = canvasDocument.RegionLayout,
                Devices = canvasDocument.Devices,
                Connections = canvasDocument.Connections,
                CanvasWidth = canvasDocument.CanvasWidth,
                CanvasHeight = canvasDocument.CanvasHeight
            };

            // 创建空的流程数据
            projectDocument.FlowDesign = new FlowDesignData
            {
                Name = "流程设计",
                CanvasWidth = 1200,
                CanvasHeight = 800,
                StartNodeX = (1200 - 231) / 2,
                StartNodeY = 20
            };

            return projectDocument;
        }

        /// <summary>
        /// 新建项目
        /// </summary>
        private void ExecuteNewDocument()
        {
            try
            {
                if (_canvasStateService.HasUnsavedChanges)
                {
                    var result = _dialogService.ShowConfirmation(
                        "当前项目有未保存的修改，是否先保存？",
                        "新建项目");

                    if (result)
                    {
                        ExecuteSave();
                        if (_canvasStateService.HasUnsavedChanges)
                        {
                            return;
                        }
                    }
                }
                // 使用项目名称对话框服务
                _projectNameDialogProvider.ShowDialog(
                    defaultName: $"新建项目_{DateTime.Now:yyyyMMdd_HHmmss}",
                    onCompleted: (projectName) =>
                    {
                        if (!string.IsNullOrEmpty(projectName))
                        {
                            CreateNewProjectWithName(projectName);
                        }
                        // 如果projectName为null，表示用户取消了操作
                    });
                
               
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"创建新项目失败: {ex.Message}", "错误");
            }
        }
        private void CreateNewProjectWithName(string projectName)
        {
            try
            {
                // 获取当前画布尺寸
                var (canvasWidth, canvasHeight) = GetCurrentCanvasSize();

                // 创建新的画布数据
                var newCanvas = _canvasDataService.CreateNewCanvas(canvasWidth, canvasHeight);

                // 设置当前区域布局
                CurrentRegionLayout = newCanvas.RegionLayout;

                // 使用用户输入的项目名称创建新项目
                var newProject = CreateNewProjectWithCustomName(projectName, canvasWidth, canvasHeight, newCanvas);

                // 重置状态
                _canvasStateService.Reset();

                _eventAggregator?.GetEvent<ClearProjectRequestEvent>()?.Publish();

                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    // 发布新项目名称
                    _eventAggregator?.GetEvent<ProjectNameUpdatedEvent>()?.Publish(projectName);

                    _eventAggregator?.GetEvent<NewProjectCreatedEvent>()?.Publish(newProject);
                    _eventAggregator?.GetEvent<RegionLayoutUpdatedEvent>()?.Publish(newCanvas.RegionLayout);
                    _eventAggregator?.GetEvent<NewCanvasCreatedEvent>()?.Publish(newCanvas);
                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"新建项目 '{projectName}' 创建成功");
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                _dialogService.ShowError($"创建项目失败: {ex.Message}", "错误");
            }
        }

        private ProjectDocument CreateNewProjectWithCustomName(string projectName, double canvasWidth, double canvasHeight, CanvasDocument newCanvas)
        {
            var newProject = new ProjectDocument
            {
                Name = projectName, // 使用用户输入的名称
                Description = $"项目创建于 {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                CanvasDesign = new CanvasDesignData
                {
                    Id = newCanvas.Id,
                    Name = newCanvas.Name,
                    CreatedTime = newCanvas.CreatedTime,
                    LastModifiedTime = newCanvas.LastModifiedTime,
                    RegionLayout = newCanvas.RegionLayout,
                    Devices = newCanvas.Devices,
                    Connections = newCanvas.Connections,
                    CanvasWidth = canvasWidth,
                    CanvasHeight = canvasHeight
                },
                FlowDesign = new FlowDesignData
                {
                    Name = "流程设计",
                    CanvasWidth = Math.Max(canvasWidth, 1200),
                    CanvasHeight = Math.Max(canvasHeight, 800),
                    StartNodeX = (Math.Max(canvasWidth, 1200) - 231) / 2,
                    StartNodeY = 20
                }
            };

            return newProject;
        }
        /// <summary>
        /// 创建带有指定画布尺寸的新项目
        /// </summary>
        private ProjectDocument CreateNewProjectWithCanvasSize(double canvasWidth, double canvasHeight, CanvasDocument newCanvas)
        {
            var newProject = new ProjectDocument
            {
                Name = $"新建项目_{DateTime.Now:HHmmss}",
                CanvasDesign = new CanvasDesignData
                {
                    Id = newCanvas.Id,
                    Name = newCanvas.Name,
                    CreatedTime = newCanvas.CreatedTime,
                    LastModifiedTime = newCanvas.LastModifiedTime,
                    RegionLayout = newCanvas.RegionLayout,
                    Devices = newCanvas.Devices,
                    Connections = newCanvas.Connections,
                    CanvasWidth = canvasWidth,
                    CanvasHeight = canvasHeight
                },
                FlowDesign = new FlowDesignData
                {
                    Name = "流程设计",
                    CanvasWidth = Math.Max(canvasWidth, 1200), // 流程画布至少1200宽
                    CanvasHeight = Math.Max(canvasHeight, 800), // 流程画布至少800高
                    StartNodeX = (Math.Max(canvasWidth, 1200) - 231) / 2,
                    StartNodeY = 20
                }
            };

            return newProject;
        }


    

  

        #endregion
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

                //  1. 优先检查是否有选中的管道
                var selectedPipeId = GetCurrentSelectedPipe();

                if (!string.IsNullOrEmpty(selectedPipeId))
                {
                    // 有选中管道，删除管道
                    System.Diagnostics.Debug.WriteLine($"删除选中管道: {selectedPipeId}");

                    var result = _dialogService.ShowConfirmation(
                        "确定要删除选中的管道吗？",
                        "删除确认");

                    if (result)
                    {
                        // 发布删除管道事件
                        _eventAggregator?.GetEvent<DeleteSelectedPipeEvent>()?.Publish(selectedPipeId);
                        _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("管道已删除");
                    }
                    return; //  删除管道后直接返回
                }

                //  2. 检查是否有选中的设备
                var selectedDeviceId = GetCurrentSelectedDevice();

                if (!string.IsNullOrEmpty(selectedDeviceId))
                {
                    // 有选中设备，删除设备及其连接
                    System.Diagnostics.Debug.WriteLine($"删除选中设备: {selectedDeviceId}");

                    var result = _dialogService.ShowConfirmation(
                        $"确定要删除选中的设备吗？\n\n此操作将同时删除与该设备相关的所有连接线。",
                        "删除确认");

                    if (result)
                    {
                        _eventAggregator?.GetEvent<DeleteSelectedDeviceEvent>()?.Publish(selectedDeviceId);
                        _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("设备已删除");
                    }
                }
                else
                {
                    //  3. 没有选中设备和管道，尝试删除选中的连接线
                    System.Diagnostics.Debug.WriteLine("没有选中设备或管道，尝试删除选中的连接线");
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

        /// <summary>
        /// 获取当前选中的管道
        /// </summary>
        private string GetCurrentSelectedPipe()
        {
            string selectedPipeId = null;
            var waitHandle = new ManualResetEventSlim(false);

            try
            {
                _eventAggregator?.GetEvent<GetSelectedPipeRequestEvent>()?.Publish((pipeId) =>
                {
                    selectedPipeId = pipeId;
                    waitHandle.Set();
                });

                // 等待500毫秒，如果没有响应就认为没有选中管道
                waitHandle.Wait(500);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取选中管道失败: {ex.Message}");
            }
            finally
            {
                waitHandle.Dispose();
            }

            System.Diagnostics.Debug.WriteLine($"当前选中管道: {selectedPipeId ?? "无"}");
            return selectedPipeId;
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
                Debug.WriteLine("[Toolbar] 执行画布自适应命令");

                // 先通过事件让 ProcessDesignerView 刷新 ScrollViewer 布局，
                // 然后再触发 FitCanvasToViewEvent
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    _eventAggregator?.GetEvent<FitCanvasToViewEvent>()?.Publish();
                }), System.Windows.Threading.DispatcherPriority.Background);

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?
                    .Publish("正在自适应画布...");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"画布自适应失败: {ex.Message}");
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

                            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
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
                            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
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

        private void ExecuteStatus()
        {
            try
            {
                // 切换模拟模式状态
                IsSimulationMode = !IsSimulationMode;

                var mode = IsSimulationMode ? "模拟模式" : "实际模式";
                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish($"已切换到{mode}");

                _logger.LogInformation("设备运行模式切换: {Mode}", mode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "切换模拟模式失败");
                _dialogService?.ShowError($"切换模拟模式失败: {ex.Message}", "错误");
            }
        }

        private void ExecuteOpenDOE()
        {
            try
            {
                _logger.LogInformation("打开 DOE 实验设计与优化窗口");

                var doeMainView = _container.Resolve<MaxChemical.Modules.DOE.Views.DOEMainView>();
                doeMainView.Owner = Application.Current.MainWindow;
                doeMainView.Show();

                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("DOE 实验设计窗口已打开");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "打开 DOE 窗口失败");
                _dialogService?.ShowError($"打开 DOE 模块失败: {ex.Message}", "错误");
            }
        }
        /// <summary>
        /// 模拟模式状态改变处理
        /// </summary>
        private void OnSimulationModeChanged()
        {
            try
            {
                // 更新开关UI状态
                UpdateSwitchUIState();

                // 通知所有设备切换模拟模式
                NotifyDevicesSimulationModeChanged();

                // 更新本地化文本
                UpdateSimulationModeText();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理模拟模式状态改变失败");
            }
        }
        /// <summary>
        /// 更新开关UI状态
        /// </summary>
        private void UpdateSwitchUIState()
        {
            if (IsSimulationMode)
            {
                // 模拟模式:开关到右侧,品牌深红
                SwitchPosition = 26;
                SwitchBackgroundColor = "#A41626";
                SwitchBorderColor = "#A41626";
            }
            else
            {
                // 实际模式:开关到左侧,中性灰
                SwitchPosition = 2;
                SwitchBackgroundColor = "#D3D1C7";
                SwitchBorderColor = "#D3D1C7";
            }
        }

        /// <summary>
        /// 通知所有设备切换模拟模式
        /// </summary>
        private void NotifyDevicesSimulationModeChanged()
        {
            try
            {
                // 发布设备模拟模式切换事件
                _eventAggregator?.GetEvent<DeviceSimulationModeChangedEvent>()?.Publish(new DeviceSimulationModeEventArgs
                {
                    IsSimulationMode = IsSimulationMode,
                    Timestamp = DateTime.Now
                });

                _logger.LogInformation("已通知所有设备切换模拟模式: {Mode}", IsSimulationMode ? "模拟" : "实际");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "通知设备切换模拟模式失败");
            }
        }

        /// <summary>
        /// 更新模拟模式文本
        /// </summary>
        private void UpdateSimulationModeText()
        {
            if (IsSimulationMode)
            {
                TextSimulationMode = _localizationService.GetString("Text_SimulationMode_On") ?? "模拟模式";
                TooltipSimulationMode = _localizationService.GetString("ToolTip_SimulationMode_On") ?? "当前为模拟模式，点击切换到实际模式";
            }
            else
            {
                TextSimulationMode = _localizationService.GetString("Text_SimulationMode_Off") ?? "实际模式";
                TooltipSimulationMode = _localizationService.GetString("ToolTip_SimulationMode_Off") ?? "当前为实际模式，点击切换到模拟模式";
            }
        }

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
        private void ShowParameterMonitorChart()
        {
            try
            {
               
                var viewModel = new ParameterMonitorChartViewModel(_parameterMonitoringService, _eventAggregator);
                var view = new ParameterMonitorChartView
                {
                    DataContext = viewModel
                };
                var window = new Window
                {
                    Title = _localizationService.GetString("ParamMonitor_WinTitle", "参数监控图表 - MaxChemical"),
                    Content = view,
                    Width = 1400,
                    Height = 900,
                    MinWidth = 1000,
                    MinHeight = 700,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                    Icon = Application.Current.MainWindow?.Icon
                };

                // 窗口关闭时清理资源
                window.Closed += (s, e) =>
                {
                    viewModel?.Dispose();
                    _logger.LogInformation("参数监控图表窗口已关闭");
                };

                window.Show();


                _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("参数监控图表已打开");
                _logger.LogInformation("参数监控图表窗口已创建并显示");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建参数监控图表窗口失败");
                _dialogService?.ShowError($"打开参数监控图表失败: {ex.Message}", "错误");
            }
        }

        private async void PredictiveAssessment()
        {
            LoadingWindow loadingWindow = null;

            try
            {
                _logger.LogInformation("打开数据分析与预测评估窗口");

                // 显示加载窗口
                loadingWindow = new LoadingWindow
                {
                    Owner = Application.Current.MainWindow
                };
                loadingWindow.Show();

                // 异步加载数据分析视图
                await Task.Run(async () =>
                {
                    // 模拟初始化步骤
                    await Task.Delay(300);
                    loadingWindow.Dispatcher.Invoke(() =>
                        loadingWindow.UpdateMessage("正在加载Python环境", "初始化机器学习模型..."));

                    await Task.Delay(500);
                    loadingWindow.Dispatcher.Invoke(() =>
                        loadingWindow.UpdateMessage("正在准备数据", "加载历史实验数据..."));

                    await Task.Delay(400);
                    loadingWindow.Dispatcher.Invoke(() =>
                        loadingWindow.UpdateMessage("正在生成图表", "构建可视化组件..."));

                    await Task.Delay(300);
                });

                // 在UI线程创建视图
                DataAnalysisControl dataAnalysisView = null;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    dataAnalysisView = _container.Resolve<DataAnalysisControl>();
                });

                // 短暂延迟确保视图完全加载
                await Task.Delay(200);

                // 关闭加载窗口
                loadingWindow.Dispatcher.Invoke(() =>
                {
                    loadingWindow.Close();
                    loadingWindow = null;
                });

                // 显示主窗口
                Application.Current.Dispatcher.Invoke(() =>
                {
                    var window = new Window
                    {
                        Title = "数据分析与预测评估",
                        Content = dataAnalysisView,
                        Width = 1600,
                        Height = 800,
                        MinWidth = 1400,
                        MinHeight = 700,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen,
                        Icon = Application.Current.MainWindow?.Icon
                    };

                    // 窗口淡入动画
                    window.Opacity = 0;
                    window.Show();

                    var fadeIn = new System.Windows.Media.Animation.DoubleAnimation
                    {
                        From = 0,
                        To = 1,
                        Duration = TimeSpan.FromMilliseconds(300)
                    };
                    window.BeginAnimation(Window.OpacityProperty, fadeIn);

                    // 窗口关闭时只释放ViewModel，不关闭Python环境
                    window.Closed += (s, e) =>
                    {
                        if (dataAnalysisView.DataContext is IDisposable disposable)
                        {
                            disposable.Dispose(); // 只释放ViewModel
                        }

                        _logger.LogInformation("数据分析窗口已关闭");
                    };

                    _eventAggregator?.GetEvent<StatusMessageChangedEvent>()?.Publish("数据分析窗口已打开");
                });
            }
            catch (Exception ex)
            {
                // 确保加载窗口被关闭
                loadingWindow?.Dispatcher.Invoke(() => loadingWindow.Close());

                _logger.LogError(ex, "打开数据分析窗口失败");
                _dialogService?.ShowError($"打开数据分析失败: {ex.Message}", "错误");
            }
        }

        private void ExecuteShowLogViewCommand()
        {
            var win = _container.Resolve<LogMessageViewer>();
            win.Show();
        }
        #endregion
    }
}