using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MaxChemical.Infrastructure.DOE;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Event;
using DesignerFlowDocument = MaxChemical.Modules.Designer.Models.FlowDocument;
using MaxChemical.Modules.Designer.Models;

namespace MaxChemical.Shell.Services.Agent.Tools
{
    /// <summary>查询当前流程状态(只读)。</summary>
    public sealed class GetFlowStatusTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public GetFlowStatusTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "get_flow_status";
        public string DisplayName => "查看流程状态";
        public string Description => "查看当前打开的流程(工艺)名称与执行状态(空闲/运行中/已暂停等)。";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "查看流程状态";

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var exec = _ctx.Resolve<IFlowExecutionService>();
            var param = _ctx.Resolve<IFlowParameterProvider>();

            string flowName = null, flowId = null;
            int candidates = 0;
            try { flowName = param.GetCurrentFlowName(); } catch { }
            try { flowId = param.GetCurrentFlowId(); } catch { }
            try { candidates = param.GetFactorCandidates()?.Count ?? 0; } catch { }

            string stateText = exec.State switch
            {
                FlowRunState.Idle => "空闲",
                FlowRunState.Running => "运行中",
                FlowRunState.Paused => "已暂停",
                FlowRunState.Stopping => "正在停止",
                FlowRunState.Completed => "已完成",
                FlowRunState.Failed => "失败",
                _ => exec.State.ToString()
            };

            // 刚下发启动的窗口期内读到"空闲",大概率是引擎还在初始化,提示模型稍候复查而不是下结论
            string startupNote = "";
            if (exec.State == FlowRunState.Idle &&
                (DateTime.UtcNow - RunFlowTool.LastStartUtc).TotalSeconds < 12)
            {
                startupNote = ";注意:流程刚下发启动不久,引擎可能仍在初始化,此\"空闲\"不代表未运行,请稍等几秒再查,不要重复启动";
            }

            return Task.FromResult(AgentToolResult.Ok(
                $"当前流程:{flowName ?? "(未命名/未打开)"};执行状态:{stateText}{startupNote};" +
                $"流程含 {candidates} 个参数节点(注意:其中既有可调的设定参数,也有监控/读取类参数——后者不能作 DOE 因子;" +
                "需要区分时调用 list_factor_candidates)" +
                (string.IsNullOrEmpty(flowId) ? "" : $";流程ID:{flowId}")));
        }
    }

    /// <summary>逐个列出流程参数候选并按语义分类:设定类(可作 DOE 因子)vs 监控/读取类(不可作因子)。</summary>
    public sealed class ListFactorCandidatesTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public ListFactorCandidatesTool(AgentToolContext ctx) => _ctx = ctx;

        // 监控/读取类命令的关键词(节点名含这些词 → 该节点参数是采集显示用途,不是实验因子)
        private static readonly string[] MonitorKeywords =
            { "获取", "读取", "查询", "监控", "刷新", "状态", "显示", "采集", "上报" };

        public string Name => "list_factor_candidates";
        public string DisplayName => "分析流程可调参数";
        public string Description => "逐个列出当前流程中的参数节点,并区分:设定类参数(设置温度/流量等,可作为 DOE 因子)与监控/读取类参数(获取温度、刷新间隔等,只是采集显示,不能作为因子)。循环次数、等待时间等流程控制量已在系统层排除,不会出现在结果里。设计实验前必须先调用它,不要只看参数总数。分类是按节点名称启发式判断的,拿不准的项要与用户确认。";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "分析流程可调参数";

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            List<MaxChemical.Infrastructure.DOE.FactorCandidate> candidates;
            try
            {
                candidates = _ctx.Resolve<IFlowParameterProvider>().GetFactorCandidates()
                             ?? new List<MaxChemical.Infrastructure.DOE.FactorCandidate>();
            }
            catch (Exception ex)
            {
                return Task.FromResult(AgentToolResult.Fail("获取参数候选失败:" + ex.Message));
            }

            if (candidates.Count == 0)
                return Task.FromResult(AgentToolResult.Ok("当前流程没有参数节点(流程为空或节点未带参数)。"));

            bool IsMonitor(MaxChemical.Infrastructure.DOE.FactorCandidate c)
            {
                string basis = (c.NodeDisplayName ?? "") + "|" + (c.DisplayName ?? "") + "|" + (c.ParameterName ?? "");
                return MonitorKeywords.Any(k => basis.Contains(k));
            }

            var settable = candidates.Where(c => !IsMonitor(c)).ToList();
            var monitor = candidates.Where(IsMonitor).ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"流程参数节点共 {candidates.Count} 个,分类如下(按节点名启发式判断,拿不准的请与用户确认):");
            sb.AppendLine($"【设定类,可作 DOE 因子】{settable.Count} 个:");
            foreach (var c in settable.Take(30))
                sb.AppendLine($"- {c.DisplayName}(当前值 {c.CurrentValue ?? "?"},参数ID:{c.ParameterId})");
            sb.AppendLine("创建 DOE 批次时把参数ID填入因子的 bind_parameter_id,即可自动完成因子与流程参数的绑定。");
            sb.AppendLine($"【监控/读取类,不能作因子】{monitor.Count} 个:");
            foreach (var c in monitor.Take(30))
                sb.AppendLine($"- {c.DisplayName}(节点:{c.NodeDisplayName})");

            return Task.FromResult(AgentToolResult.Ok(sb.ToString()));
        }
    }

    /// <summary>运行当前流程(写操作,需确认)。</summary>
    public sealed class RunFlowTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        private readonly ILogService _logger = LogManager.GetLogger<RunFlowTool>();
        public RunFlowTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "run_current_flow";
        public string DisplayName => "运行当前流程";
        public string Description => "启动执行当前设计器中打开的流程(只跑一遍),并等待确认引擎真正进入运行状态后才返回。注意:本工具不会执行 DOE 实验矩阵——要逐组执行 DOE 批次(带执行看板小窗口),必须用 execute_doe_batch,严禁用本工具冒充。可用 get_flow_status 跟踪进度,仅在流程空闲时可启动。";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";
        public bool RequiresConfirmation => true;
        public string DescribeAction(JsonElement args) => "启动运行当前流程(设备将真实动作)";

        /// <summary>最近一次下发启动的时刻:供状态查询识别"刚启动窗口期",避免把初始化中的 Idle 误判为未运行。</summary>
        internal static DateTime LastStartUtc { get; private set; } = DateTime.MinValue;

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var exec = _ctx.Resolve<IFlowExecutionService>();
            if (exec.State == FlowRunState.Running || exec.State == FlowRunState.Paused)
                return AgentToolResult.Fail($"流程当前状态为 {exec.State},不能重复启动");

            // 执行前硬性检查:所有参与通讯的设备必须已连接或处于模拟模式;
            // 检查结论(含模拟模式跳过项)要如实回传给用户
            var readiness = _ctx.GetDeviceReadiness();
            if (!readiness.AllReady)
                return AgentToolResult.Fail(
                    $"执行前检查未通过:以下设备未连接且非模拟模式——{readiness.OfflineText}。" +
                    (readiness.Simulated.Count > 0 ? $"(另有 {readiness.Simulated.Count} 台模拟模式已跳过检查:{readiness.SimText})" : "") +
                    "请先用 connect_devices 完成真实连接(或把设备切换到模拟模式),确认全部就绪后再运行流程。");
            string readinessNote = readiness.Summary();

            LastStartUtc = DateTime.UtcNow;

            // 长任务:后台启动,完成情况由日志与状态查询体现
            _ = Task.Run(async () =>
            {
                try
                {
                    var r = await exec.ExecuteCurrentFlowAsync();
                    _logger.LogInformation($"[小桐] 流程执行结束:成功={r.IsSuccessful},节点 {r.CompletedNodes}/{r.TotalNodes},{r.ErrorMessage}");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[小桐] 流程执行异常:{ex.Message}");
                }
            });

            // 启动需要时间(引擎初始化/设备预备),等到状态真正翻转再返回,避免"启动后立即查仍是空闲"的误导
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                switch (exec.State)
                {
                    case FlowRunState.Running:
                        return AgentToolResult.Ok(
                            (string.IsNullOrEmpty(readinessNote) ? "" : readinessNote + "\n") +
                            "流程已启动,并已确认进入运行状态。可随时问我进度或让我暂停/停止。");
                    case FlowRunState.Failed:
                        return AgentToolResult.Fail("流程启动后立即失败,请检查设备连接与节点配置(查看运行日志)。");
                    case FlowRunState.Completed:
                        return AgentToolResult.Ok("流程已启动并快速执行完成(流程较短)。");
                }
                await Task.Delay(250).ConfigureAwait(false);
            }

            return AgentToolResult.Ok(
                "启动指令已下发,但 8 秒内尚未观察到运行状态——流程初始化可能较慢,属正常现象。" +
                "请等待几秒后用 get_flow_status 复查,不要立即判定启动失败,也不要重复启动。");
        }
    }

    /// <summary>暂停流程(安全操作,不需确认)。</summary>
    public sealed class PauseFlowTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public PauseFlowTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "pause_flow";
        public string DisplayName => "暂停流程";
        public string Description => "暂停正在运行的流程(保持当前设备状态)。这是安全动作,无需用户确认。";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "暂停流程";

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var exec = _ctx.Resolve<IFlowExecutionService>();
            if (exec.State != FlowRunState.Running)
                return Task.FromResult(AgentToolResult.Fail($"流程当前状态为 {exec.State},无法暂停"));
            exec.PauseExecution();
            return Task.FromResult(AgentToolResult.Ok("流程已暂停"));
        }
    }

    /// <summary>继续流程(需确认——恢复运行意味着设备继续动作)。</summary>
    public sealed class ResumeFlowTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public ResumeFlowTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "resume_flow";
        public string DisplayName => "继续流程";
        public string Description => "继续已暂停的流程。";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";
        public bool RequiresConfirmation => true;
        public string DescribeAction(JsonElement args) => "继续执行已暂停的流程(设备将继续动作)";

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var exec = _ctx.Resolve<IFlowExecutionService>();
            if (exec.State != FlowRunState.Paused)
                return Task.FromResult(AgentToolResult.Fail($"流程当前状态为 {exec.State},无法继续"));
            exec.ResumeExecution();
            return Task.FromResult(AgentToolResult.Ok("流程已继续运行"));
        }
    }

    /// <summary>停止流程(需确认)。</summary>
    public sealed class StopFlowTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public StopFlowTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "stop_flow";
        public string DisplayName => "停止流程";
        public string Description => "停止当前流程的执行(不可恢复到断点,需重新启动)。";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";
        public bool RequiresConfirmation => true;
        public string DescribeAction(JsonElement args) => "停止当前流程执行";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var exec = _ctx.Resolve<IFlowExecutionService>();
            if (exec.State != FlowRunState.Running && exec.State != FlowRunState.Paused)
                return AgentToolResult.Fail($"流程当前状态为 {exec.State},无需停止");
            await exec.StopExecutionAsync().ConfigureAwait(false);
            return AgentToolResult.Ok("流程已停止");
        }
    }

    /// <summary>
    /// 口述搭流程:按步骤清单生成流程节点并铺到设计器画布(写操作,需确认)。
    /// 生成前逐步校验:设备必须在画布上、命令必须存在、参数名必须匹配——模型臆造会被拦下。
    /// </summary>
    public sealed class BuildFlowTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public BuildFlowTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "build_flow";
        public string DisplayName => "生成流程到画布";
        public string Description => @"根据步骤清单在流程设计器中生成一条顺序流程并显示到画布(不会自动运行)。
steps 按执行顺序排列,每步二选一:
- 设备步骤:type=device,给 device_name(画布显示名)、command_name(设备命令名)、parameters(该命令的输入参数覆盖值,键必须是真实参数名);
- 等待步骤:type=wait,给 wait_seconds(秒)与 description(等待原因)。
调用前必须先用 list_devices/get_device_status 核实设备名、命令名、参数名;生成后用户可在画布上微调,再让我运行。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""flow_name"":{""type"":""string"",""description"":""流程名称""},
    ""steps"":{
      ""type"":""array"",
      ""items"":{
        ""type"":""object"",
        ""properties"":{
          ""type"":{""type"":""string"",""enum"":[""device"",""wait""]},
          ""device_name"":{""type"":""string""},
          ""command_name"":{""type"":""string""},
          ""parameters"":{""type"":""object""},
          ""wait_seconds"":{""type"":""number""},
          ""description"":{""type"":""string""}
        },
        ""required"":[""type""]
      }
    }
  },
  ""required"":[""flow_name"",""steps""]
}";
        public bool RequiresConfirmation => true;

        public string DescribeAction(JsonElement args)
        {
            string name = AgentToolContext.GetString(args, "flow_name") ?? "未命名";
            int n = args.TryGetProperty("steps", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.GetArrayLength() : 0;
            return $"生成流程「{name}」({n} 个步骤)并铺到设计器画布——将先清空画布上现有流程节点(不会自动运行)";
        }

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string flowName = AgentToolContext.GetString(args, "flow_name") ?? "小桐生成的流程";
            if (!args.TryGetProperty("steps", out var stepsEl) || stepsEl.ValueKind != JsonValueKind.Array)
                return Task.FromResult(AgentToolResult.Fail("缺少 steps 数组"));

            var steps = stepsEl.EnumerateArray().ToList();
            if (steps.Count == 0)
                return Task.FromResult(AgentToolResult.Fail("steps 为空"));
            if (steps.Count > 40)
                return Task.FromResult(AgentToolResult.Fail("步骤过多(>40),请拆分"));

            var nodes = new List<FlowCommandNodeData>();
            var summary = new StringBuilder();

            // 设计器为横排布局(节点宽 231 + 间距 80,参照 CalculateNextNodePosition),
            // 竖排会导致 RebuildAllConnections 算出负的连线长度、渲染错乱。
            const double NodeWidth = 231, NodeGap = 80;
            const double StartNodeX = 100, StartNodeY = 20;
            double x0 = StartNodeX + NodeWidth + NodeGap;

            for (int i = 0; i < steps.Count; i++)
            {
                var st = steps[i];
                string type = AgentToolContext.GetString(st, "type");
                var node = new FlowCommandNodeData
                {
                    NodeId = Guid.NewGuid().ToString(),
                    X = x0 + i * (NodeWidth + NodeGap),
                    Y = StartNodeY,
                    Order = i,
                    IsVisible = true
                };

                if (type == "device")
                {
                    string devName = AgentToolContext.GetString(st, "device_name");
                    string cmdName = AgentToolContext.GetString(st, "command_name");
                    if (string.IsNullOrWhiteSpace(devName) || string.IsNullOrWhiteSpace(cmdName))
                        return Task.FromResult(AgentToolResult.Fail($"第 {i + 1} 步缺少 device_name/command_name"));

                    // ── 严格校验:设备在画布上、命令存在、参数名真实 ──
                    var (vm, display, err) = _ctx.FindDevice(devName);
                    if (vm == null)
                        return Task.FromResult(AgentToolResult.Fail($"第 {i + 1} 步:{err}"));

                    var cmd = vm.Device.Commands?.FirstOrDefault(c => c.Name == cmdName);
                    if (cmd == null)
                        return Task.FromResult(AgentToolResult.Fail(
                            $"第 {i + 1} 步:设备「{display}」没有命令 {cmdName}。可用:" +
                            string.Join(",", vm.Device.Commands?.Select(c => c.Name) ?? Array.Empty<string>())));

                    var overrides = new Dictionary<string, string>();
                    if (st.TryGetProperty("parameters", out var ps) && ps.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var p in ps.EnumerateObject())
                        {
                            bool exists = cmd.InputParameters.Variables.Any(v => v.Name == p.Name);
                            if (!exists)
                                return Task.FromResult(AgentToolResult.Fail(
                                    $"第 {i + 1} 步:命令 {cmdName} 没有输入参数 {p.Name}。有效输入:" +
                                    string.Join(",", cmd.InputParameters.Variables.Select(v => v.Name))));
                            overrides[p.Name] = p.Value.ValueKind == JsonValueKind.String
                                ? p.Value.GetString()
                                : p.Value.ToString();
                        }
                    }

                    node.CommandType = "DeviceCommand";
                    node.DisplayName = $"{display} · {cmdName}";
                    node.DeviceCommandName = cmdName;
                    // 加载侧按 CLR 类型名匹配驱动(d.GetType().Name),不能用中文显示名
                    node.DeviceCommandType = vm.Device.GetType().Name;
                    node.BoundDeviceId = vm.DeviceId;
                    node.BoundDeviceName = display;
                    node.ParameterOverrides = overrides;

                    summary.AppendLine($"{i + 1}. {display} → {cmdName}" +
                        (overrides.Count > 0 ? "(" + string.Join(",", overrides.Select(kv => $"{kv.Key}={kv.Value}")) + ")" : ""));
                }
                else if (type == "wait")
                {
                    double seconds = AgentToolContext.GetNumber(st, "wait_seconds") ?? 10;
                    string desc = AgentToolContext.GetString(st, "description") ?? "等待";
                    if (seconds <= 0 || seconds > 86400)
                        return Task.FromResult(AgentToolResult.Fail($"第 {i + 1} 步等待时长非法:{seconds} 秒"));

                    node.CommandType = "LogicCommand";
                    node.DisplayName = desc;
                    node.LogicCommandType = "Wait";
                    node.LogicCommandName = "等待";
                    node.LogicCommandProperties = new Dictionary<string, object>
                    {
                        ["WaitTime"] = seconds,
                        ["TimeUnit"] = "Seconds",
                        ["Description"] = desc
                    };

                    summary.AppendLine($"{i + 1}. 等待 {seconds} 秒({desc})");
                }
                else
                {
                    return Task.FromResult(AgentToolResult.Fail($"第 {i + 1} 步 type 非法:{type}(仅支持 device/wait)"));
                }

                nodes.Add(node);
            }

            // 顺序连线
            var connections = new List<FlowConnectionData>();
            for (int i = 0; i < nodes.Count - 1; i++)
            {
                connections.Add(new FlowConnectionData
                {
                    FromNodeId = nodes[i].NodeId,
                    ToNodeId = nodes[i + 1].NodeId
                });
            }

            var flow = new DesignerFlowDocument
            {
                Name = flowName,
                Description = "由小桐根据口述生成",
                CommandNodes = nodes,
                Connections = connections
            };

            try
            {
                AgentToolContext.RunOnUi(() =>
                {
                    // 加载事件是"追加"语义,先清空旧流程,否则会与已有节点混叠(与 ProjectCoordinator 的加载路径一致)
                    _ctx.Events.GetEvent<ClearFlowRequestEvent>().Publish();
                    _ctx.Events.GetEvent<LoadFlowRequestEvent>().Publish(flow);
                });
            }
            catch (Exception ex)
            {
                return Task.FromResult(AgentToolResult.Fail("流程铺到画布失败:" + ex.Message));
            }

            return Task.FromResult(AgentToolResult.Ok(
                $"流程「{flowName}」已生成到设计器画布,共 {nodes.Count} 步:\n{summary}" +
                "已按顺序连线。请提醒用户在画布上检查后再运行(可以让我 run_current_flow)。"));
        }
    }
}
