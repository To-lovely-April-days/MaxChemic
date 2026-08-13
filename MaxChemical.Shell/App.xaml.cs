using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.Device;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Prism.Ioc;
using Prism.Modularity;
using System.IO;
using System.Windows;
using Prism.Unity;
using MaxChemical.Shell.Views;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Services;
using MaxChemical.Modules.Designer.Service;
using MaxChemical.Modules.Designer.Plugins;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Service.Execution;
using MaxChemical.Modules.Designer.Services.Execution;
using System.Windows.Input;
using Prism.Events;
using MaxChemical.Data.Services;
using MaxChemical.Data.Repositories;
using MaxChemical.Core;
using MaxChemical.Shell.Services;
using MaxChemical.Modules.DOE;
using MaxChemical.Infrastructure.DOE;
using System.Diagnostics;

namespace MaxChemical.Shell;

public partial class App : PrismApplication
{
    private IHost? _host;
    private static ILogService? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 初始化日志系统
        MaxChemical.Logging.LogManager.Initialize(
            logDirectory: Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs"),
            minimumLevel: Serilog.Events.LogEventLevel.Debug
        );

        _logger = MaxChemical.Logging.LogManager.GetLogger<App>();
        _logger.LogInformation("MaxChemical 应用程序启动");

        // WPF 绑定诊断:换成「已知噪音过滤」监听器——只吞掉系统主题样式在容器回收时
        // 必然产生的 FindAncestor 报错(见 BindingNoiseFilterListener 注释),其余绑定错误照常输出
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Clear();
        PresentationTraceSources.DataBindingSource.Listeners.Add(
            new MaxChemical.Shell.Services.BindingNoiseFilterListener());

        // 创建和配置Host
        CreateHost();

        base.OnStartup(e);

        // 应用启动时初始化Python环境
        //PythonEnvironmentManager.Instance.Initialize();
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
    }

    private void CreateHost()
    {
        try
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureAppConfiguration((context, config) =>
                {
                    config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                    config.AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true);
                    config.AddEnvironmentVariables();
                })
                .ConfigureServices((context, services) =>
                {
                    // 注册日志服务 - 使用工厂模式
                    services.AddSingleton<ILogService>(serviceProvider =>
                    {
                        return MaxChemical.Logging.LogManager.GetLogger("HostServices");
                    });

                    // 如果您的ILogService需要特定的实现，可以这样注册
                    // services.AddSingleton<ILogService, YourLogServiceImplementation>();
                    
                    // 注册配置相关服务
                    services.AddSingleton<IDatabaseConfigService, DatabaseConfigService>();
                    services.AddSingleton<ICustomVariablePersistenceService, CustomVariableDatabaseService>();
                })
                .Build();

            _logger?.LogInformation("Host创建完成");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "创建Host失败");
        }
    }

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // ========== 基础服务 ==========
        containerRegistry.RegisterSingleton<IRecentFileService, RecentFileService>();
        containerRegistry.RegisterSingleton<ILocalizationService, LocalizationService>();
        containerRegistry.RegisterSingleton<IDialogService, DialogService>();
        containerRegistry.RegisterSingleton<IProjectService, ProjectService>();
        containerRegistry.RegisterSingleton<IRegionSettingsDialogProvider, RegionSettingsDialogProvider>();
        containerRegistry.RegisterSingleton<IProjectNameDialogProvider, ProjectNameDialogProvider>();
        containerRegistry.RegisterSingleton<IRegionOverlayService, RegionOverlayService>();
        containerRegistry.RegisterSingleton<IDragDropService, DragDropService>();
        containerRegistry.Register<IConnectionService, ConnectionService>();
        containerRegistry.RegisterSingleton<ICanvasStateService, CanvasStateService>();
        containerRegistry.RegisterSingleton<ICanvasDataService, CanvasDataService>();
        containerRegistry.RegisterSingleton<IDeviceManager, DeviceManager>();
        containerRegistry.RegisterSingleton<ICommandHistory, CommandHistoryService>();
        containerRegistry.RegisterSingleton<ILogicCommandPluginManager, LogicCommandPluginManager>();
        containerRegistry.RegisterSingleton<INodeExecutionStatusService, NodeExecutionStatusService>();
        containerRegistry.RegisterSingleton<IFlowExecutionEngine, FlowExecutionEngine>();
        containerRegistry.RegisterSingleton<IDeviceNameResolverService, DeviceNameResolverService>();
        containerRegistry.RegisterSingleton<IVariablePersistenceBackgroundService, VariablePersistenceBackgroundService>();
        containerRegistry.RegisterSingleton<IParameterMonitoringService, ParameterMonitoringService>();
        containerRegistry.RegisterSingleton<IProjectDataService, ProjectDataService>();
        containerRegistry.RegisterSingleton<ProjectCoordinator>();
        containerRegistry.RegisterSingleton<IPipeConnectionService, PipeConnectionService>();
        containerRegistry.RegisterSingleton<IPipeSnapService, PipeSnapService>();
        //     // ========== DOE 解耦接口 ==========
        containerRegistry.RegisterSingleton<IFlowExecutionService, FlowExecutionAdapter>();
        containerRegistry.RegisterSingleton<IFlowParameterProvider, FlowParameterAdapter>();
        // 等待节点枚举:动态稳态等待挑选保温节点用;适配器无状态,独立实例无副作用
        containerRegistry.RegisterSingleton<IFlowWaitNodeProvider, FlowParameterAdapter>();
        // ========== Prism日志服务 ==========
        containerRegistry.RegisterSingleton<ILogService>(() => MaxChemical.Logging.LogManager.GetLogger("PrismServices"));
        // 网关配置 — 打开主窗口的服务,必须在 Shell 这层注册
        // (因为 MainMenuBarViewModel 在 Shell 启动早期就被解析,那时模块还没加载)
        containerRegistry.RegisterSingleton< MaxChemical.Modules.GatewayConfig.Services.IGatewayConfigWindowService,MaxChemical.Modules.GatewayConfig.Services.GatewayConfigWindowService >();
     
        // ==========  新增：注册配置服务 ==========
        if (_host != null)
        {
            try
            {
                var configuration = _host.Services.GetRequiredService<IConfiguration>();
                containerRegistry.RegisterInstance<IConfiguration>(configuration);
                _logger?.LogInformation(" IConfiguration 已从 Host 注册到 Prism 容器");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "从 Host 获取 IConfiguration 失败");
            }
        }

        // ==========  小桐 Agent(DeepSeek 工具调用) ==========
        containerRegistry.RegisterSingleton<MaxChemical.Shell.Services.Agent.DeepSeekClient>();
        containerRegistry.RegisterSingleton<MaxChemical.Shell.Services.Agent.XiaoTongAgent>();
        containerRegistry.RegisterSingleton<MaxChemical.Shell.Services.Agent.AgentBootstrapper>();
        containerRegistry.RegisterSingleton<MaxChemical.Shell.Services.Agent.AgentChatWindowService>();
        containerRegistry.RegisterSingleton<MaxChemical.Shell.Services.Agent.XiaoTongSentinel>();
        containerRegistry.RegisterSingleton<ViewModels.AgentChatViewModel>();

        // ==========  注册语音助手服务 ==========
        containerRegistry.RegisterSingleton<IVoiceAssistantService, PiperVoiceAssistantService>();

        // ========== 数据库相关服务 ==========
        if (_host != null)
        {
            try
            {
                var databaseConfigService = _host.Services.GetRequiredService<IDatabaseConfigService>();
                var customVariablePersistenceService = _host.Services.GetRequiredService<ICustomVariablePersistenceService>();

                containerRegistry.RegisterInstance<IDatabaseConfigService>(databaseConfigService);
                containerRegistry.RegisterInstance<ICustomVariablePersistenceService>(customVariablePersistenceService);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "从Host注册服务失败，使用降级方案");
                containerRegistry.RegisterSingleton<IDatabaseConfigService, DatabaseConfigService>();
                containerRegistry.RegisterSingleton<ICustomVariablePersistenceService, CustomVariableDatabaseService>();
            }
        }
        else
        {
            containerRegistry.RegisterSingleton<IDatabaseConfigService, DatabaseConfigService>();
            containerRegistry.RegisterSingleton<ICustomVariablePersistenceService, CustomVariableDatabaseService>();
        }

        containerRegistry.RegisterSingleton<IDatabaseConnectionFactory, DatabaseConnectionFactory>();
        containerRegistry.RegisterSingleton<IExperimentDataRepository, ExperimentDataRepository>();
        containerRegistry.RegisterSingleton<IGatewayBindingRepository, GatewayBindingRepository>();
        containerRegistry.RegisterSingleton<IDeviceTransportFactory, DeviceTransportFactory>();
        containerRegistry.RegisterSingleton<IExperimentDataCollectionService, ExperimentDataCollectionService>();
        containerRegistry.RegisterSingleton<IExcelExportService, ExcelExportService>();
        containerRegistry.RegisterSingleton<IExperimentDataAdapter, ExperimentDataAdapter>();
        // 云链路安全监护(断线 HOLD 流程 + 告警)
        containerRegistry.RegisterSingleton<RemoteLinkSafetyMonitor>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var logger = MaxChemical.Logging.LogManager.GetLogger<App>();
        logger.LogInformation("MaxChemical 应用程序退出");
        // ↓ 新增
        try
        {
            Container.Resolve<MaxChemical.Shell.Services.Agent.AgentBootstrapper>().ShutdownMcp();
        }
        catch (Exception ex)
        {
            logger.LogWarning($"停止外部 MCP 接口时出错(不影响退出): {ex.Message}");
        }

        // 停止Host
        _host?.StopAsync().Wait(TimeSpan.FromSeconds(5));
        _host?.Dispose();

        MaxChemical.Logging.LogManager.Shutdown();
        try
        {
            //  应用退出时才关闭Python环境
            //PythonEnvironmentManager.Instance.Dispose();

            // 清理日志记录缓存
            LogMessageCache.Instance.Clear();
        }
        catch (Exception ex)
        {
            // 记录错误但不影响程序退出
            System.Diagnostics.Debug.WriteLine($"关闭Python环境失败: {ex.Message}");
        }
        base.OnExit(e);
    }

    protected override Window CreateShell()
    {
        return Container.Resolve<MainWindow>();
    }

 

    protected override void ConfigureModuleCatalog(IModuleCatalog moduleCatalog)
    {
        try
        {
            moduleCatalog.AddModule<DeviceModule>();
            moduleCatalog.AddModule<DesignerModule>();
            moduleCatalog.AddModule<DOEModule>();
            moduleCatalog.AddModule<MaxChemical.Modules.GatewayConfig.GatewayConfigModule>(); // ★ 新增这一行
            _logger?.LogInformation("模块目录配置完成");
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "配置模块目录时发生错误");
            throw;
        }
    }

    protected override async void OnInitialized()
    {
        try
        {
            base.OnInitialized();

            if (_host != null)
            {
                await _host.StartAsync();
                _logger?.LogInformation("Host启动完成");
            }

            await InitializeDatabaseAsync();

            // 填充设备插件可访问的服务入口
            MaxChemical.Core.DeviceServices.TransportFactory =
                Container.Resolve<MaxChemical.Core.IDeviceTransportFactory>();

            var persistenceService = Container.Resolve<IVariablePersistenceBackgroundService>();
            persistenceService.StartListening();

            // 启动云链路安全监护(订阅链路健康，断线自动 HOLD 流程并告警)
            Container.Resolve<RemoteLinkSafetyMonitor>();

            // 装配小桐 Agent(注册技能表 + DeepSeek 配置;模块服务此时已就绪)
            Container.Resolve<MaxChemical.Shell.Services.Agent.AgentBootstrapper>().Initialize();

            _logger?.LogInformation("应用程序初始化完成");

          
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "应用程序初始化失败");
            MessageBox.Show($"应用程序初始化失败:{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task InitializeDatabaseAsync()
    {
        try
        {
            var customVariablePersistenceService = Container.Resolve<ICustomVariablePersistenceService>();

            _logger?.LogInformation("开始初始化数据库...");
            await customVariablePersistenceService.InitializeDatabaseAsync();
            // 新增:建网关绑定表
            var gatewayBindingRepo = Container.Resolve<IGatewayBindingRepository>();
            await gatewayBindingRepo.InitializeAsync();

            _logger?.LogInformation("数据库初始化完成");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "数据库初始化失败");

            var result = MessageBox.Show(
                $"数据库初始化失败：{ex.Message}\n\n是否继续启动应用程序？",
                "数据库连接错误",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.No)
            {
                Application.Current.Shutdown();
            }
        }
    }
}