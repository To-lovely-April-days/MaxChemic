using System;
using System.Text;
using MaxChemical.Infrastructure.DOE;
using MaxChemical.Logging;
using MaxChemical.Shell.Services.Agent.Tools;
using Microsoft.Extensions.Configuration;
using Prism.Ioc;

namespace MaxChemical.Shell.Services.Agent
{
    /// <summary>
    /// 小桐 Agent 装配:读取 DeepSeek 配置、注册全部技能、注入实验室快照。
    /// 在 App.OnInitialized(模块加载完成后)调用一次。
    /// </summary>
    public sealed class AgentBootstrapper
    {
        private readonly IContainerProvider _container;
        private readonly ILogService _logger = LogManager.GetLogger<AgentBootstrapper>();
        private bool _initialized;
        // ↓ 新增
        private Mcp.McpPipeServer _mcpServer;
        public void ShutdownMcp() => _mcpServer?.Stop();
        public AgentBootstrapper(IContainerProvider container)
        {
            _container = container;
        }

        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                var config = _container.Resolve<IConfiguration>();
                var client = _container.Resolve<DeepSeekClient>();
                var agent = _container.Resolve<XiaoTongAgent>();

                client.ApiKey = config["DeepSeek:ApiKey"] ?? "";
                client.Model = string.IsNullOrWhiteSpace(config["DeepSeek:Model"]) ? "deepseek-chat" : config["DeepSeek:Model"];
                string baseUrl = config["DeepSeek:BaseUrl"];
                if (!string.IsNullOrWhiteSpace(baseUrl)) client.BaseUrl = baseUrl;

                // 默认绕过系统代理直连 DeepSeek;确需走代理时配置 DeepSeek:UseProxy=true
                if (string.Equals(config["DeepSeek:UseProxy"], "true", StringComparison.OrdinalIgnoreCase))
                    client.SetUseProxy(true);

                var ctx = new AgentToolContext(_container);

                // ── 技能表 ──
                agent.RegisterTool(new ListDevicesTool(ctx));
                agent.RegisterTool(new ConnectDevicesTool(ctx));
                agent.RegisterTool(new GetDeviceStatusTool(ctx));
                agent.RegisterTool(new SetDeviceParameterTool(ctx));
                agent.RegisterTool(new ExecuteDeviceCommandTool(ctx));
                agent.RegisterTool(new GetFlowStatusTool(ctx));
                agent.RegisterTool(new ListFactorCandidatesTool(ctx));
                agent.RegisterTool(new RunFlowTool(ctx));
                agent.RegisterTool(new PauseFlowTool(ctx));
                agent.RegisterTool(new ResumeFlowTool(ctx));
                agent.RegisterTool(new StopFlowTool(ctx));
                agent.RegisterTool(new BuildFlowTool(ctx));
                agent.RegisterTool(new PreviewDoeDesignTool(ctx));
                agent.RegisterTool(new CreateDoeBatchTool(ctx));
                agent.RegisterTool(new ExecuteDoeBatchTool(ctx));
                agent.RegisterTool(new GetDoeProgressTool(ctx));
                agent.RegisterTool(new GetDoeAutopilotConfigTool(ctx));
                agent.RegisterTool(new AnalyzeDoeBatchTool(ctx));
                agent.RegisterTool(new QueryExperimentHistoryTool(ctx));
                agent.RegisterTool(new FindDoeProjectTool(ctx));
                agent.RegisterTool(new AnalyzeDoeProjectTool(ctx));
                agent.RegisterTool(new GetDecisionReportTool(ctx));
                agent.RegisterTool(new CreateNextRoundPreviewTool(ctx));
                agent.RegisterTool(new PredictResponseTool(ctx));
                agent.RegisterTool(new SuggestOptimalConditionsTool(ctx));
                agent.RegisterTool(new SetProcessConditionTool(ctx));
                agent.RegisterTool(new AskUserChoiceTool(ctx));
                agent.RegisterTool(new FitKineticsTool(ctx));
                agent.RegisterTool(new PredictKineticsTool(ctx));
                agent.RegisterTool(new RecordRunSamplesTool(ctx));
                agent.RegisterTool(new FitReactionNetworkTool(ctx));
                agent.RegisterTool(new SimulateReactionTool(ctx));
                agent.RegisterTool(new SearchLabHistoryTool(ctx));
                agent.RegisterTool(new GenerateProjectReportTool(ctx));
                agent.RegisterTool(new ListFlowWaitNodesTool(ctx));
                agent.RegisterTool(new PreviewStaircaseSweepTool(ctx));
                agent.RegisterTool(new RecordRunResponseTool(ctx));
                agent.RegisterTool(new InspectExperimentFileTool(ctx));
                agent.RegisterTool(new CreateImportPreviewTool(ctx));

                // ── 多专员协作 ──
                // 注册表持有全部工具(专员执行、历史会话旧调用都从这里查),
                // 但总控只对模型暴露派工与交互工具:提示词短、路由准,领域工具由各专员分工持有。
                agent.RegisterTool(new AssignDesignTaskTool(ctx));
                agent.RegisterTool(new AssignExecutionTaskTool(ctx));
                agent.RegisterTool(new AssignAnalysisTaskTool(ctx));
                agent.RegisterTool(new AssignMechanismTaskTool(ctx));
                agent.SetAdvertisedTools(SpecialistTeam.OrchestratorToolNames);

                // ── 实验室快照(每轮对话注入 system prompt) ──
                agent.SetLabSnapshotProvider(() => BuildSnapshot(ctx));

                // ── 提前装配确认通道 ──
                // AgentChatViewModel 是 XiaoTongAgent.ConfirmationRequested 的唯一订阅者;
                // 若等用户点开窗口才创建,纯语音链路的写操作会因"无确认通道"被静默拒绝。
                var chatVm = _container.Resolve<ViewModels.AgentChatViewModel>();
                var windowService = _container.Resolve<AgentChatWindowService>();
                chatVm.ShowWindowRequested += windowService.ShowWindow;

                // ── 语音提问改走聊天 ViewModel ──
                // AskFromVoice 会占用 IsBusy 并锁定本轮会话路由;直连 Agent 的旧链路
                // 在多专员长轮次下会让「轮次进行中切会话/发消息」把事件落错会话。
                try
                {
                    if (_container.Resolve<IVoiceAssistantService>() is PiperVoiceAssistantService piper)
                        piper.SetUiAskHandler(chatVm.AskFromVoice);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"语音提问通道装配失败(退回直连链路): {ex.Message}");
                }

                // ── 主动值守:DOE 执行播报 + 设备掉线看门狗 ──
                // 默认开启;appsettings 配 XiaoTong:Sentinel=false 可关闭
                if (!string.Equals(config["XiaoTong:Sentinel"], "false", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        _container.Resolve<XiaoTongSentinel>().Start(chatVm, agent);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"主动值守启动失败(不影响其他功能): {ex.Message}");
                    }
                }
                // ↓↓↓ 新增这一整段 ↓↓↓
                try
                {
                    var policy = new Mcp.McpToolPolicy(config["MaxChemicalMcp:Mode"]);
                    _mcpServer = new Mcp.McpPipeServer();
                    _mcpServer.Start(agent, policy, config["MaxChemicalMcp:PipeName"]);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"外部 MCP 接口启动失败(不影响其他功能): {ex.Message}");
                }
                // ↑↑↑ 到这里 ↑↑↑
                _logger.LogInformation(agent.IsConfigured
                    ? $"小桐 Agent 已就绪:模型 {client.Model},技能 {agent.Tools.Count} 项,专员 {SpecialistTeam.All.Count} 名(总控派工模式)"
                    : "小桐 Agent 已装配,但未配置 DeepSeek ApiKey(将回退到本地意图识别)");
            }
            catch (Exception ex)
            {
                _logger.LogError($"小桐 Agent 装配失败: {ex.Message}");
            }
        }

        private string BuildSnapshot(AgentToolContext ctx)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"时间:{DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            try
            {
                bool? sim = ctx.GetGlobalSimulationMode();
                if (sim != null)
                    sb.AppendLine($"运行模式:{(sim.Value ? "模拟模式(全局开关,无物理链路,设备连接检查整体跳过)" : "实际模式(执行前需逐台校验连接)")}");
            }
            catch { }

            try
            {
                var devices = ctx.GetCanvasDevices();
                if (devices.Count == 0)
                {
                    sb.AppendLine("画布设备:无");
                }
                else
                {
                    sb.Append("画布设备(").Append(devices.Count).AppendLine(" 台):");
                    foreach (var d in devices)
                    {
                        string display = ctx.GetDisplayName(d.DeviceId);
                        string state = "";
                        try
                        {
                            state = (d.Device?.IsConnected == true ? "已连接" : "未连接") +
                                    (d.Device?.IsSimulationMode == true ? "/模拟" : "");
                        }
                        catch { }
                        sb.AppendLine($"- {display}({d.Device?.Name},{state})");
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("画布设备:获取失败 " + ex.Message);
            }

            try
            {
                var exec = _container.Resolve<IFlowExecutionService>();
                var pp = _container.Resolve<IFlowParameterProvider>();
                sb.AppendLine($"当前流程:{pp.GetCurrentFlowName() ?? "(无)"},状态 {exec.State}");
            }
            catch { sb.AppendLine("当前流程:未知"); }

            return sb.ToString();
        }
    }
}
