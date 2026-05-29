using MaxChemical.Data.Repositories;
using MaxChemical.Data.Services;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Service;
using MaxChemical.Modules.Designer.ViewModels;
using MaxChemical.Modules.GatewayConfig.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;

namespace MaxChemical.Shell.ViewModels;

public class MainMenuBarViewModel : BindableBase
{
    private readonly IEventAggregator _eventAggregator;
    private readonly ILocalizationService _localizationService;

    private readonly IExperimentDataRepository _experimentRepository;
    private readonly IExcelExportService _excelExportService;
    private readonly ILogService _logger;
    private readonly IGatewayConfigWindowService _gatewayConfigWindowService;
    private readonly IProjectNameDialogProvider _projectNameDialogProvider;
    private readonly IProjectDataService _projectDataService;
    private readonly ICanvasDataService _canvasDataService;
    private readonly ICanvasStateService _canvasStateService;
    private readonly IDialogService _dialogService;

    private bool _isToolbarVisible = true;
    private bool _isStatusBarVisible = true;
    private bool _isPropertyPanelVisible = true;
    private bool _isDevicePanelVisible = true;

    // 本地化属性
    private string _menuFile = string.Empty;
    private string _menuHistory = string.Empty;
    private string _menuWindow = string.Empty;
    private string _menuTools = string.Empty;
    private string _menuHelp = string.Empty;
    private string _menuNewProject = string.Empty;
    private string _menuOpenProject = string.Empty;
    private string _menuSave = string.Empty;
    private string _menuSaveAs = string.Empty;
    private string _menuRecentFiles = string.Empty;
    private string _menuViewHistory = string.Empty;
    private string _menuExportData = string.Empty;
    private string _menuExperimentStats = string.Empty;
    private string _menuGenerateReport = string.Empty;
    private string _menuToolbar = string.Empty;
    private string _menuLanguage = string.Empty;
    private string _menuLanguageChinese = string.Empty;
    private string _menuLanguageEnglish = string.Empty;
    private string _menuUserManual = string.Empty;
    private string _menuVideoTutorial = string.Empty;
    private string _menuTechSupport = string.Empty;
    private string _menuCheckUpdate = string.Empty;
    private string _menuAbout = string.Empty;

    public MainMenuBarViewModel(
        IEventAggregator eventAggregator,
        ILocalizationService localizationService,
        IExperimentDataRepository experimentRepository,
        IExcelExportService excelExportService,
        ILogService logger,
        IGatewayConfigWindowService gatewayConfigWindowService,
        ICanvasDataService canvasDataService,
        ICanvasStateService canvasStateService,
        IProjectNameDialogProvider projectNameDialogProvider,
        IProjectDataService projectDataService,
        IDialogService dialogService)
    {
        _eventAggregator = eventAggregator;
        _localizationService = localizationService;
        _experimentRepository = experimentRepository ?? throw new ArgumentNullException(nameof(experimentRepository));
        _excelExportService = excelExportService ?? throw new ArgumentNullException(nameof(excelExportService));
        _logger = logger.ForContext<MainMenuBarViewModel>();
        _gatewayConfigWindowService = gatewayConfigWindowService;  //  新增
        _projectNameDialogProvider = projectNameDialogProvider ?? throw new ArgumentNullException(nameof(projectNameDialogProvider));
        _projectDataService = projectDataService ?? throw new ArgumentNullException(nameof(projectDataService));
        _canvasDataService = canvasDataService ?? throw new ArgumentNullException(nameof(canvasDataService));
        _canvasStateService = canvasStateService ?? throw new ArgumentNullException(nameof(canvasStateService));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));

        InitializeCommands();
        LoadLocalizedStrings();
        LoadRecentFiles();
        SubscribeEvents();
    }

    #region Localized Properties

    public string MenuFile
    {
        get => _menuFile;
        set => SetProperty(ref _menuFile, value);
    }

    public string MenuHistory
    {
        get => _menuHistory;
        set => SetProperty(ref _menuHistory, value);
    }

    public string MenuWindow
    {
        get => _menuWindow;
        set => SetProperty(ref _menuWindow, value);
    }

    public string MenuTools
    {
        get => _menuTools;
        set => SetProperty(ref _menuTools, value);
    }

    public string MenuHelp
    {
        get => _menuHelp;
        set => SetProperty(ref _menuHelp, value);
    }

    public string MenuNewProject
    {
        get => _menuNewProject;
        set => SetProperty(ref _menuNewProject, value);
    }

    public string MenuOpenProject
    {
        get => _menuOpenProject;
        set => SetProperty(ref _menuOpenProject, value);
    }

    public string MenuSave
    {
        get => _menuSave;
        set => SetProperty(ref _menuSave, value);
    }

    public string MenuSaveAs
    {
        get => _menuSaveAs;
        set => SetProperty(ref _menuSaveAs, value);
    }

    public string MenuRecentFiles
    {
        get => _menuRecentFiles;
        set => SetProperty(ref _menuRecentFiles, value);
    }

    public string MenuViewHistory
    {
        get => _menuViewHistory;
        set => SetProperty(ref _menuViewHistory, value);
    }

    public string MenuExportData
    {
        get => _menuExportData;
        set => SetProperty(ref _menuExportData, value);
    }

    public string MenuExperimentStats
    {
        get => _menuExperimentStats;
        set => SetProperty(ref _menuExperimentStats, value);
    }

    public string MenuGenerateReport
    {
        get => _menuGenerateReport;
        set => SetProperty(ref _menuGenerateReport, value);
    }

    public string MenuToolbar
    {
        get => _menuToolbar;
        set => SetProperty(ref _menuToolbar, value);
    }

    public string MenuLanguage
    {
        get => _menuLanguage;
        set => SetProperty(ref _menuLanguage, value);
    }

    public string MenuLanguageChinese
    {
        get => _menuLanguageChinese;
        set => SetProperty(ref _menuLanguageChinese, value);
    }

    public string MenuLanguageEnglish
    {
        get => _menuLanguageEnglish;
        set => SetProperty(ref _menuLanguageEnglish, value);
    }

    public string MenuUserManual
    {
        get => _menuUserManual;
        set => SetProperty(ref _menuUserManual, value);
    }

    public string MenuVideoTutorial
    {
        get => _menuVideoTutorial;
        set => SetProperty(ref _menuVideoTutorial, value);
    }

    public string MenuTechSupport
    {
        get => _menuTechSupport;
        set => SetProperty(ref _menuTechSupport, value);
    }

    public string MenuCheckUpdate
    {
        get => _menuCheckUpdate;
        set => SetProperty(ref _menuCheckUpdate, value);
    }

    public string MenuAbout
    {
        get => _menuAbout;
        set => SetProperty(ref _menuAbout, value);
    }

    #endregion

    #region Other Properties

    public bool IsToolbarVisible
    {
        get => _isToolbarVisible;
        set => SetProperty(ref _isToolbarVisible, value);
    }

    public bool IsStatusBarVisible
    {
        get => _isStatusBarVisible;
        set => SetProperty(ref _isStatusBarVisible, value);
    }

    public bool IsPropertyPanelVisible
    {
        get => _isPropertyPanelVisible;
        set => SetProperty(ref _isPropertyPanelVisible, value);
    }

    public bool IsDevicePanelVisible
    {
        get => _isDevicePanelVisible;
        set => SetProperty(ref _isDevicePanelVisible, value);
    }

    public ObservableCollection<RecentFileItem> RecentFiles { get; private set; } = new();

    #endregion

    #region Commands

    // 文件命令
    public DelegateCommand NewProjectCommand { get; private set; } = null!;
    public DelegateCommand OpenProjectCommand { get; private set; } = null!;
    public DelegateCommand SaveCommand { get; private set; } = null!;
    public DelegateCommand SaveAsCommand { get; private set; } = null!;
    public DelegateCommand<string> OpenRecentCommand { get; private set; } = null!;
    public DelegateCommand ExitCommand { get; private set; } = null!;

    // 历史实验命令
    public DelegateCommand ViewHistoryCommand { get; private set; } = null!;
    public DelegateCommand ExportDataCommand { get; private set; } = null!;
    public DelegateCommand ExperimentStatsCommand { get; private set; } = null!;
    public DelegateCommand GenerateReportCommand { get; private set; } = null!;
    public DelegateCommand CompareExperimentsCommand { get; private set; } = null!;
    public DelegateCommand ClearHistoryCommand { get; private set; } = null!;

    // 视图命令
    public DelegateCommand ZoomInCommand { get; private set; } = null!;
    public DelegateCommand ZoomOutCommand { get; private set; } = null!;
    public DelegateCommand ZoomFitCommand { get; private set; } = null!;
    public DelegateCommand ZoomActualCommand { get; private set; } = null!;
    public DelegateCommand FullScreenCommand { get; private set; } = null!;
    public DelegateCommand ResetLayoutCommand { get; private set; } = null!;

    // 工具命令
    public DelegateCommand AddDeviceCommand { get; private set; } = null!;
    public DelegateCommand ManageDeviceLibraryCommand { get; private set; } = null!;
    public DelegateCommand ComponentEditorCommand { get; private set; } = null!;
    public DelegateCommand ProcessDesignCommand { get; private set; } = null!;
    public DelegateCommand ProcessSimulationCommand { get; private set; } = null!;
    public DelegateCommand DataMonitoringCommand { get; private set; } = null!;
    public DelegateCommand SwitchToChineseCommand { get; private set; } = null!;
    public DelegateCommand SwitchToEnglishCommand { get; private set; } = null!;
    public DelegateCommand CommunicationSettingsCommand { get; private set; } = null!;
    public DelegateCommand AlarmConfigurationCommand { get; private set; } = null!;
    public DelegateCommand ImportConfigCommand { get; private set; } = null!;
    public DelegateCommand ExportConfigCommand { get; private set; } = null!;
    public DelegateCommand OptionsCommand { get; private set; } = null!;

    // 帮助命令
    public DelegateCommand UserManualCommand { get; private set; } = null!;
    public DelegateCommand QuickStartCommand { get; private set; } = null!;
    public DelegateCommand VideoTutorialCommand { get; private set; } = null!;
    public DelegateCommand SampleProjectsCommand { get; private set; } = null!;
    public DelegateCommand TechSupportCommand { get; private set; } = null!;
    public DelegateCommand OnlineHelpCommand { get; private set; } = null!;
    public DelegateCommand FeedbackCommand { get; private set; } = null!;
    public DelegateCommand CheckUpdateCommand { get; private set; } = null!;
    public DelegateCommand AboutCommand { get; private set; } = null!;
    public DelegateCommand GatewayConfigCommand { get; private set; } = null!;
    #endregion

    private void InitializeCommands()
    {
        // 文件命令
        NewProjectCommand = new DelegateCommand(ExecuteNewProject);
        OpenProjectCommand = new DelegateCommand(ExecuteOpenProject);
        SaveCommand = new DelegateCommand(ExecuteSave);
        SaveAsCommand = new DelegateCommand(ExecuteSaveAs);
        OpenRecentCommand = new DelegateCommand<string>(ExecuteOpenRecent);
        ExitCommand = new DelegateCommand(ExecuteExit);

        // 历史实验命令
        ViewHistoryCommand = new DelegateCommand(ExecuteViewHistory);
        ExportDataCommand = new DelegateCommand(ExecuteExportData);
        ExperimentStatsCommand = new DelegateCommand(ExecuteExperimentStats);
        GenerateReportCommand = new DelegateCommand(ExecuteGenerateReport);
        CompareExperimentsCommand = new DelegateCommand(ExecuteCompareExperiments);
        ClearHistoryCommand = new DelegateCommand(ExecuteClearHistory);

        // 视图命令
        ZoomInCommand = new DelegateCommand(ExecuteZoomIn);
        ZoomOutCommand = new DelegateCommand(ExecuteZoomOut);
        ZoomFitCommand = new DelegateCommand(ExecuteZoomFit);
        ZoomActualCommand = new DelegateCommand(ExecuteZoomActual);
        FullScreenCommand = new DelegateCommand(ExecuteFullScreen);
        ResetLayoutCommand = new DelegateCommand(ExecuteResetLayout);

        // 工具命令
        AddDeviceCommand = new DelegateCommand(ExecuteAddDevice);
        ManageDeviceLibraryCommand = new DelegateCommand(ExecuteManageDeviceLibrary);
        ComponentEditorCommand = new DelegateCommand(ExecuteComponentEditor);
        ProcessDesignCommand = new DelegateCommand(ExecuteProcessDesign);
        ProcessSimulationCommand = new DelegateCommand(ExecuteProcessSimulation);
        DataMonitoringCommand = new DelegateCommand(ExecuteDataMonitoring);
        SwitchToChineseCommand = new DelegateCommand(ExecuteSwitchToChinese);
        SwitchToEnglishCommand = new DelegateCommand(ExecuteSwitchToEnglish);
        CommunicationSettingsCommand = new DelegateCommand(ExecuteCommunicationSettings);
        AlarmConfigurationCommand = new DelegateCommand(ExecuteAlarmConfiguration);
        ImportConfigCommand = new DelegateCommand(ExecuteImportConfig);
        ExportConfigCommand = new DelegateCommand(ExecuteExportConfig);
        OptionsCommand = new DelegateCommand(ExecuteOptions);

        // 帮助命令
        UserManualCommand = new DelegateCommand(ExecuteUserManual);
        QuickStartCommand = new DelegateCommand(ExecuteQuickStart);
        VideoTutorialCommand = new DelegateCommand(ExecuteVideoTutorial);
        SampleProjectsCommand = new DelegateCommand(ExecuteSampleProjects);
        TechSupportCommand = new DelegateCommand(ExecuteTechSupport);
        OnlineHelpCommand = new DelegateCommand(ExecuteOnlineHelp);
        FeedbackCommand = new DelegateCommand(ExecuteFeedback);
        CheckUpdateCommand = new DelegateCommand(ExecuteCheckUpdate);
        AboutCommand = new DelegateCommand(ExecuteAbout);
        GatewayConfigCommand = new DelegateCommand(ExecuteGatewayConfig);
    }

    private void LoadLocalizedStrings()
    {
        MenuFile = _localizationService.GetString("Menu_File");
        MenuHistory = _localizationService.GetString("Menu_History");
        MenuWindow = _localizationService.GetString("Menu_Window");
        MenuTools = _localizationService.GetString("Menu_Tools");
        MenuHelp = _localizationService.GetString("Menu_Help");
        MenuNewProject = _localizationService.GetString("Menu_NewProject");
        MenuOpenProject = _localizationService.GetString("Menu_OpenProject");
        MenuSave = _localizationService.GetString("Menu_Save");
        MenuSaveAs = _localizationService.GetString("Menu_SaveAs");
        MenuRecentFiles = _localizationService.GetString("Menu_RecentFiles");
        MenuViewHistory = _localizationService.GetString("Menu_ViewHistory");
        MenuExportData = _localizationService.GetString("Menu_ExportData");
        MenuExperimentStats = _localizationService.GetString("Menu_ExperimentStats");
        MenuGenerateReport = _localizationService.GetString("Menu_GenerateReport");
        MenuToolbar = _localizationService.GetString("Menu_Toolbar");
        MenuLanguage = _localizationService.GetString("Menu_Language");
        MenuLanguageChinese = _localizationService.GetString("Menu_Language_Chinese");
        MenuLanguageEnglish = _localizationService.GetString("Menu_Language_English");
        MenuUserManual = _localizationService.GetString("Menu_UserManual");
        MenuVideoTutorial = _localizationService.GetString("Menu_VideoTutorial");
        MenuTechSupport = _localizationService.GetString("Menu_TechSupport");
        MenuCheckUpdate = _localizationService.GetString("Menu_CheckUpdate");
        MenuAbout = _localizationService.GetString("Menu_About");
    }

    private void SubscribeEvents()
    {
        _eventAggregator.GetEvent<LanguageChangedEvent>().Subscribe(OnLanguageChanged);
    }

    private void OnLanguageChanged(string cultureName)
    {
        LoadLocalizedStrings();
    }

    // 新增：加载最近文件列表
    private void LoadRecentFiles()
    {
        var recentFiles = GetRecentFilesFromStorage();

        RecentFiles.Clear();
        foreach (var file in recentFiles)
        {
            RecentFiles.Add(file);
        }
    }

    private List<RecentFileItem> GetRecentFilesFromStorage()
    {
        return new List<RecentFileItem>
        {
            new RecentFileItem { FileName = "化工流程设计_001.spc", FilePath = @"C:\Projects\化工流程设计_001.spc", LastAccessed = DateTime.Now.AddDays(-1) },
            new RecentFileItem { FileName = "反应器优化项目.spc", FilePath = @"C:\Projects\反应器优化项目.spc", LastAccessed = DateTime.Now.AddDays(-2) },
            new RecentFileItem { FileName = "蒸馏塔设计方案.spc", FilePath = @"C:\Projects\蒸馏塔设计方案.spc", LastAccessed = DateTime.Now.AddDays(-3) },
            new RecentFileItem { FileName = "热交换器网络.spc", FilePath = @"C:\Projects\热交换器网络.spc", LastAccessed = DateTime.Now.AddDays(-5) },
            new RecentFileItem { FileName = "催化反应研究.spc", FilePath = @"C:\Projects\催化反应研究.spc", LastAccessed = DateTime.Now.AddDays(-7) }
        };
    }

    public void AddToRecentFiles(string fileName, string filePath)
    {
        var existingFile = RecentFiles.FirstOrDefault(f => f.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));
        if (existingFile != null)
        {
            RecentFiles.Remove(existingFile);
        }

        RecentFiles.Insert(0, new RecentFileItem
        {
            FileName = fileName,
            FilePath = filePath,
            LastAccessed = DateTime.Now
        });

        while (RecentFiles.Count > 10)
        {
            RecentFiles.RemoveAt(RecentFiles.Count - 1);
        }

        SaveRecentFilesToStorage();
    }

    private void SaveRecentFilesToStorage()
    {
        // 保存到配置文件或数据库
    }

    #region Command Implementations

    // 文件命令实现
    private void ExecuteNewProject()
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

    private void CreateNewProjectWithName(string projectName)
    {
        try
        {
            // 获取当前画布尺寸
            var (canvasWidth, canvasHeight) = GetCurrentCanvasSize();

            // 创建新的画布数据
            var newCanvas = _canvasDataService.CreateNewCanvas(canvasWidth, canvasHeight);

            // 设置当前区域布局
            //CurrentRegionLayout = newCanvas.RegionLayout;

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
    /// 统一的打开功能
    /// </summary>
    private void ExecuteOpenProject()
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

    private void ExecuteOpenRecent(string? filePath)
    {
        if (!string.IsNullOrEmpty(filePath))
        {
            var message = string.Format(_localizationService.GetString("Message_OpenRecentFile"), filePath);
            MessageBox.Show(message, _localizationService.GetString("Message_Info"));

            var recentFile = RecentFiles.FirstOrDefault(f => f.FilePath == filePath);
            if (recentFile != null)
            {
                RecentFiles.Remove(recentFile);
                recentFile.LastAccessed = DateTime.Now;
                RecentFiles.Insert(0, recentFile);
                SaveRecentFilesToStorage();
            }
        }
    }

    private void ExecuteExit() => Application.Current.Shutdown();

    // 历史实验命令实现
    private void ExecuteViewHistory()
    {
        try
        {
            var historyViewModel = new ExperimentHistoryViewModel(_experimentRepository, _excelExportService, _logger);
            var historyWindow = new Window
            {
                Title = _localizationService.GetString("ExperimentHistory_Win_Title", "实验历史记录"),
                Content = new MaxChemical.Modules.Designer.Views.ExperimentHistoryView { DataContext = historyViewModel },
                Width = 980,
                Height = 800,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            historyWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开实验历史记录失败");
            MessageBox.Show($"打开实验历史记录失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void ExecuteExportData() => MessageBox.Show(_localizationService.GetString("Message_ExportData"), _localizationService.GetString("Message_Info"));
    private void ExecuteExperimentStats()
    {
        try
        {
            var statsViewModel = new ExperimentStatisticsViewModel(_experimentRepository, _excelExportService, _logger);
            var statsWindow = new Window
            {
                Title = _localizationService.GetString("Statistic_Win_Title", "实验统计分析"),
                Content = new MaxChemical.Modules.Designer.Views.ExperimentStatisticsView { DataContext = statsViewModel },
                Width = 1200,
                Height = 800,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            statsWindow.ShowDialog();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打开实验统计分析失败");
            MessageBox.Show($"打开实验统计分析失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private async void ExecuteGenerateReport()
    {
        try
        {
            var saveDialog = new SaveFileDialog
            {
                Filter = "Excel文件 (*.xlsx)|*.xlsx",
                FileName = $"设备参数变化记录_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
            };

            if (saveDialog.ShowDialog() == true)
            {
                // 默认导出最近30天的参数变化记录
                var endDate = DateTime.Now;
                var startDate = endDate.AddDays(-30);

                var parameterChanges = await _experimentRepository.GetParameterChangesForExportAsync(startDate, endDate);

                if (!parameterChanges.Any())
                {
                    MessageBox.Show("最近30天内没有参数变化记录", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                await _excelExportService.ExportParameterChangesAsync(parameterChanges, saveDialog.FileName);

                MessageBox.Show($"导出完成！\n文件位置: {saveDialog.FileName}\n共导出 {parameterChanges.Count} 条参数变化记录", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{saveDialog.FileName}\"");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "导出参数变化数据失败");
            MessageBox.Show($"导出参数变化数据失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void ExecuteCompareExperiments() => MessageBox.Show("实验对比", _localizationService.GetString("Message_Info"));
    private void ExecuteClearHistory() => MessageBox.Show("清除历史记录", _localizationService.GetString("Message_Info"));

    // 视图命令实现
    private void ExecuteZoomIn() => MessageBox.Show("放大", _localizationService.GetString("Message_Info"));
    private void ExecuteZoomOut() => MessageBox.Show("缩小", _localizationService.GetString("Message_Info"));
    private void ExecuteZoomFit() => MessageBox.Show("适合窗口", _localizationService.GetString("Message_Info"));
    private void ExecuteZoomActual() => MessageBox.Show("100% 显示", _localizationService.GetString("Message_Info"));
    private void ExecuteFullScreen() => MessageBox.Show("全屏显示", _localizationService.GetString("Message_Info"));
    private void ExecuteResetLayout() => MessageBox.Show("重置窗口布局", _localizationService.GetString("Message_Info"));

    // 工具命令实现
    private void ExecuteAddDevice() => _eventAggregator.GetEvent<NavigationRequestEvent>().Publish("DeviceModule");
    private void ExecuteManageDeviceLibrary() => MessageBox.Show("设备库管理", _localizationService.GetString("Message_Info"));
    private void ExecuteComponentEditor() => MessageBox.Show("组件编辑器", _localizationService.GetString("Message_Info"));
    private void ExecuteProcessDesign() => _eventAggregator.GetEvent<NavigationRequestEvent>().Publish("DesignerModule");
    private void ExecuteProcessSimulation() => MessageBox.Show("流程仿真", _localizationService.GetString("Message_Info"));
    private void ExecuteDataMonitoring() => MessageBox.Show("数据监控", _localizationService.GetString("Message_Info"));

    private void ExecuteSwitchToChinese()
    {
        _localizationService.SetCulture(new CultureInfo("zh-CN"));
        _eventAggregator.GetEvent<LanguageChangedEvent>().Publish("zh-CN");
    }

    private void ExecuteSwitchToEnglish()
    {
        _localizationService.SetCulture(new CultureInfo("en-US"));
        _eventAggregator.GetEvent<LanguageChangedEvent>().Publish("en-US");
    }

    private void ExecuteCommunicationSettings() => MessageBox.Show("通讯设置", _localizationService.GetString("Message_Info"));
    private void ExecuteAlarmConfiguration() => MessageBox.Show("报警设置", _localizationService.GetString("Message_Info"));
    private void ExecuteImportConfig() => MessageBox.Show("导入配置", _localizationService.GetString("Message_Info"));
    private void ExecuteExportConfig() => MessageBox.Show("导出配置", _localizationService.GetString("Message_Info"));
    private void ExecuteOptions() => MessageBox.Show("选项设置", _localizationService.GetString("Message_Info"));

    // 帮助命令实现
    private void ExecuteUserManual() => MessageBox.Show(_localizationService.GetString("Message_UserManual"), _localizationService.GetString("Message_Info"));
    private void ExecuteQuickStart() => MessageBox.Show("快速入门指南", _localizationService.GetString("Message_Info"));
    private void ExecuteVideoTutorial() => MessageBox.Show(_localizationService.GetString("Message_VideoTutorial"), _localizationService.GetString("Message_Info"));
    private void ExecuteSampleProjects() => MessageBox.Show("示例项目", _localizationService.GetString("Message_Info"));
    private void ExecuteTechSupport() => MessageBox.Show(_localizationService.GetString("Message_TechSupport"), _localizationService.GetString("Message_Info"));
    private void ExecuteOnlineHelp() => MessageBox.Show("在线帮助", _localizationService.GetString("Message_Info"));
    private void ExecuteFeedback() => MessageBox.Show("反馈建议", _localizationService.GetString("Message_Info"));
    private void ExecuteCheckUpdate() => MessageBox.Show(_localizationService.GetString("Message_CheckUpdate"), _localizationService.GetString("Message_Info"));
    private void ExecuteAbout() => MessageBox.Show(_localizationService.GetString("Message_About"), _localizationService.GetString("Menu_About"));
    private void ExecuteGatewayConfig()
    {
        try
        {
            // 把 Shell 主窗口作为 Owner 传进去
            var mainWindow = Application.Current.MainWindow;
            _gatewayConfigWindowService.ShowGatewayConfig(mainWindow);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "打开网关配置窗口失败");
            MessageBox.Show($"打开网关配置失败:{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    #endregion
}

public class RecentFileItem
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTime LastAccessed { get; set; }
    public string DisplayName => System.IO.Path.GetFileName(FileName);
}