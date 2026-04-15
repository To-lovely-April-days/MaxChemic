using MaxChemical.Data.Repositories;
using MaxChemical.Data.Services;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using System.Collections.ObjectModel;
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

    public MainMenuBarViewModel(IEventAggregator eventAggregator, ILocalizationService localizationService,
         IExperimentDataRepository experimentRepository, IExcelExportService excelExportService, ILogService logger)
    {
        _eventAggregator = eventAggregator;
        _localizationService = localizationService;
        _experimentRepository = experimentRepository ?? throw new ArgumentNullException(nameof(experimentRepository));
        _excelExportService = excelExportService ?? throw new ArgumentNullException(nameof(excelExportService));
        _logger=logger.ForContext<MainMenuBarViewModel>();
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
    private void ExecuteNewProject() => MessageBox.Show(_localizationService.GetString("Message_NewProject"), _localizationService.GetString("Message_Info"));

    private void ExecuteOpenProject()
    {
        MessageBox.Show(_localizationService.GetString("Message_OpenProject"), _localizationService.GetString("Message_Info"));
    }

    private void ExecuteSave() => MessageBox.Show(_localizationService.GetString("Message_Save"), _localizationService.GetString("Message_Info"));
    private void ExecuteSaveAs() => MessageBox.Show(_localizationService.GetString("Message_SaveAs"), _localizationService.GetString("Message_Info"));

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
                Title = "实验历史记录",
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
                Title = "实验统计分析",
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

    #endregion
}

public class RecentFileItem
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTime LastAccessed { get; set; }
    public string DisplayName => System.IO.Path.GetFileName(FileName);
}