using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.Device;
using Microsoft.Extensions.Hosting;
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

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        var logger = MaxChemical.Logging.LogManager.GetLogger<App>();
        logger.LogInformation("MaxChemical 应用程序退出");

        MaxChemical.Logging.LogManager.Shutdown();
        base.OnExit(e);
    }

    protected override Window CreateShell()
    {
        return Container.Resolve<MainWindow>();
    }

    protected override void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 注册基础服务
        containerRegistry.RegisterSingleton<ILocalizationService, LocalizationService>();
        containerRegistry.RegisterSingleton<IDialogService, DialogService>();
        containerRegistry.RegisterSingleton<IProjectService, ProjectService>();
        containerRegistry.RegisterSingleton<IRegionSettingsDialogProvider, RegionSettingsDialogProvider>();
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
    }

    protected override void ConfigureModuleCatalog(IModuleCatalog moduleCatalog)
    {
        try
        {
            moduleCatalog.AddModule<DeviceModule>();
            moduleCatalog.AddModule<DesignerModule>();
            _logger?.LogInformation("模块目录配置完成");
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "配置模块目录时发生错误");
            throw;
        }
    }

    protected override void OnInitialized()
    {
        try
        {
            base.OnInitialized();

            // 启动 Host
            _host?.StartAsync();

            _logger?.LogInformation("应用程序初始化完成");
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "应用程序初始化失败");
            throw;
        }
    }
}