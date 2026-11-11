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

    // ── 启动分段计时 ──────────────────────────────────────────────
    //  闪屏只是把等待遮住,并不会让启动变快。留着这个打点,才能知道时间
    //  实际花在哪一段、值不值得去优化。日志里搜 [启动计时],看**相邻两行的
    //  差值**,不是看绝对值。
    private static readonly Stopwatch _boot = Stopwatch.StartNew();

    private static void Mark(string stage)
    {
        _logger?.LogInformation($"[启动计时] {_boot.ElapsedMilliseconds,6} ms  {stage}");
        Debug.WriteLine($"[启动计时] {_boot.ElapsedMilliseconds,6} ms  {stage}");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 闪屏必须是第一句。它跑在自己的 STA 线程上(见 SplashScreen 注释),
        // 所以下面这些同步工作再重也不会把它的动画冻住。
        Services.SplashScreen.Show();

        try
        {
            // 初始化日志系统
            MaxChemical.Logging.LogManager.Initialize(
                logDirectory: Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs"),
                minimumLevel: Serilog.Events.LogEventLevel.Debug
            );

            _logger = MaxChemical.Logging.LogManager.GetLogger<App>();
            _logger.LogInformation("MaxChemical 应用程序启动");
            RegisterGlobalExceptionHandlers();
            Mark("日志系统就绪");
            Services.SplashScreen.Step(1);   // 日志系统

            // WPF 绑定诊断:换成「已知噪音过滤」监听器——只吞掉系统主题样式在容器回收时
            // 必然产生的 FindAncestor 报错(见 BindingNoiseFilterListener 注释),其余绑定错误照常输出
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Clear();
            PresentationTraceSources.DataBindingSource.Listeners.Add(
                new MaxChemical.Shell.Services.BindingNoiseFilterListener());

            // 创建和配置Host
            Services.SplashScreen.Step(2);   // 配置文件
            CreateHost();
            Mark("Host 构建完成");

            base.OnStartup(e);
            Mark("Prism 初始化完成(主窗口已显示)");

            // 应用启动时初始化Python环境
            //PythonEnvironmentManager.Instance.Initialize();
            QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        }
        catch (Exception ex)
        {
            // 启动阶段抛异常时,闪屏若不处理就会变成一个不走也不关的窗口,
            // 看着和死机没区别 —— 这个程序此前真出过启动即崩(0xe0434352)。
            _logger?.LogCritical(ex, "启动阶段发生异常");
            Services.SplashScreen.Fail("启动失败", ex.GetType().Name);
            Services.SplashScreen.Kill();
            throw;
        }
    }

    /// <summary>
    /// 全局异常兜底:未处理异常先落日志,再按默认行为处理(不吞异常)。
    /// 没有这层时,UI 线程一个未捕获异常就是无声"闪退",现场无法定位。
    /// 注意:渲染线程/非托管层崩溃(显卡驱动、视频解码器)不会经过这里,
    /// 那种崩溃要看 Windows 事件查看器(应用程序日志里的 .NET Runtime / Application Error)。
    /// </summary>
    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (s, args) =>
        {
            _logger?.LogCritical(args.Exception, "UI 线程未处理异常");
            // 不设置 args.Handled:保持原有行为,只补日志
        };

        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            _logger?.LogCritical(args.ExceptionObject as Exception,
                $"后台线程未处理异常 (IsTerminating={args.IsTerminating})");
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            _logger?.LogError(args.Exception, "未观察到的异步任务异常");
            args.SetObserved();
        };
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
        Mark("RegisterTypes 开始");

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
        // 实验准备协调器保存跨语音轮次的方案与执行状态,必须全应用共享同一个实例。
        containerRegistry.RegisterSingleton<MaxChemical.Shell.Services.Agent.Experiment.ExperimentPreparationCoordinator>();
        containerRegistry.RegisterSingleton<ViewModels.AgentChatViewModel>();

        // ==========  小桐能力扩展:联网搜索 / 知识库 / 记忆 / 审计 / 安全 ==========
        containerRegistry.RegisterSingleton<MaxChemical.Shell.Services.Agent.Search.WebSearchService>();
        containerRegistry.RegisterSingleton<MaxChemical.Shell.Services.Agent.Knowledge.EmbeddingService>();
        containerRegistry.RegisterSingleton<MaxChemical.Shell.Services.Agent.Knowledge.KnowledgeBaseService>();
        containerRegistry.RegisterSingleton<MaxChemical.Shell.Services.Agent.KnowledgeBaseWindowService>();
        containerRegistry.RegisterSingleton<MaxChemical.Shell.Services.Agent.Memory.MemoryStore>();
        containerRegistry.RegisterSingleton<MaxChemical.Shell.Services.Agent.Audit.AiAuditLog>();
        containerRegistry.RegisterSingleton<MaxChemical.Shell.Services.Agent.Safety.VoiceConfirmationService>();
        containerRegistry.RegisterSingleton<MaxChemical.Shell.Services.Agent.Safety.EmergencyStopService>();

        // ==========  注册语音助手服务 ==========
        containerRegistry.RegisterSingleton<IVoiceAssistantService, PiperVoiceAssistantService>();
        // 全文播报会话由独立服务管理,停止时能同时取消当前音频和后续段落。
        containerRegistry.RegisterSingleton<ISpeechPlaybackService, SpeechPlaybackService>();

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

        Mark("RegisterTypes 结束");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 万一走到退出闪屏还挂着(启动中途被关掉等),别留个孤儿窗口
        Services.SplashScreen.Kill();

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
        Mark("CreateShell 开始");
        var shell = Container.Resolve<MainWindow>();
        Mark("CreateShell 结束(主窗口已构造)");
        return shell;
    }



    protected override void ConfigureModuleCatalog(IModuleCatalog moduleCatalog)
    {
        try
        {
            moduleCatalog.AddModule<DeviceModule>();
            moduleCatalog.AddModule<DesignerModule>();
            moduleCatalog.AddModule<DOEModule>();
            moduleCatalog.AddModule<MaxChemical.Modules.Lab3D.Lab3DModule>();
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
            Mark("OnInitialized 开始");

            // 主窗口此时已经 Show 出来了,但界面还不能用 —— 模块、数据库、
            // Agent 都还没就绪。闪屏是 Topmost,会一直盖在前面直到这段跑完,
            // 用户看到的是「闪屏 → 淡出 → 一个已经能用的主窗口」。
            //
            // 下面 Step(n) 的 n 必须**顺次递增**,且与 SplashWindow.Steps 的
            // 数组顺序一一对应。进度是按已完成步骤的权重累加算的,一旦倒序调用
            // 进度条就会往回缩(SetStep 里有只增不减的兜底,但那是保险丝,
            // 不该拿它当常态)。注意模块加载确实排在数据库前面 ——
            // base.OnInitialized() 就在这一行,别凭直觉把数据库挪上去。
            Services.SplashScreen.Step(3);   // 功能模块
            base.OnInitialized();
            Mark("模块加载完成");

            if (_host != null)
            {
                await _host.StartAsync();
                _logger?.LogInformation("Host启动完成");
                Mark("Host 启动完成");
            }

            Services.SplashScreen.Step(4);   // 数据库连接
            await InitializeDatabaseAsync();
            Mark("数据库初始化完成");

            // 填充设备插件可访问的服务入口
            Services.SplashScreen.Step(5);   // 正在加载设备驱动
            MaxChemical.Core.DeviceServices.TransportFactory =
                Container.Resolve<MaxChemical.Core.IDeviceTransportFactory>();

            Services.SplashScreen.Step(6);   // 流程引擎
            var persistenceService = Container.Resolve<IVariablePersistenceBackgroundService>();
            persistenceService.StartListening();
            Mark("变量持久化服务就绪");

            Services.SplashScreen.Step(7);   // 网关代理
            // 启动云链路安全监护(订阅链路健康，断线自动 HOLD 流程并告警)
            Container.Resolve<RemoteLinkSafetyMonitor>();
            Mark("云链路监护就绪");

            // 装配小桐 Agent(注册技能表 + DeepSeek 配置;模块服务此时已就绪)
            Services.SplashScreen.Step(8);   // MCP 接口
            Services.SplashScreen.Step(9);   // 小桐 Agent
            Container.Resolve<MaxChemical.Shell.Services.Agent.AgentBootstrapper>().Initialize();
            Mark("小桐 Agent 装配完成");

            _logger?.LogInformation("应用程序初始化完成");
            Mark("=== 启动全部完成 ===");

            // 走到 100% 再淡出,让最后那一下有个交代
            Services.SplashScreen.Ready();
            await Task.Delay(420);
            Services.SplashScreen.Close();

            // 闪屏是 Topmost,关掉后把焦点还给主窗口
            MainWindow?.Activate();
        }
        catch (Exception ex)
        {
            _logger?.LogCritical(ex, "应用程序初始化失败");

            // 先让闪屏显示失败原因,再拆掉 —— 否则弹出错误框时,
            // 后面还压着一个永远停在半截的闪屏。
            Services.SplashScreen.Fail("初始化失败", ex.GetType().Name);
            await Task.Delay(900);
            Services.SplashScreen.Kill();

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

            // 要弹模态框问用户了,闪屏必须先让开 —— 它是 Topmost,
            // 不拆掉的话对话框会被压在后面,表现为程序完全没反应。
            Services.SplashScreen.Kill();

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
