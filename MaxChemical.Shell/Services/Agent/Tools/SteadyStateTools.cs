using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MaxChemical.Infrastructure.DOE;

namespace MaxChemical.Shell.Services.Agent.Tools
{
    /// <summary>
    /// 列出当前流程里的固定时长 Wait(保温/等待)节点,供「按 τ 动态稳态等待」挑选被驱动的节点。
    /// 等待时长是流程控制量,不在因子候选里,所以单开这个查询。只读。
    /// </summary>
    public sealed class ListFlowWaitNodesTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public ListFlowWaitNodesTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "list_flow_wait_nodes";
        public string DisplayName => "列出流程等待节点";
        public string Description => @"列出当前流程里的固定时长 Wait(保温/等待)节点:节点ID、名称、当前等待时长,以及是否勾选了「停留时间等待节点」标记。
「按 τ 动态稳态等待」的保温节点按标记自动定位:流程里恰有一个标记节点时,preview_doe_design 可省略 wait_node_id;
本工具用于核对标记状态,或在没有标记时列出候选让用户选定(选定后传 node_id)。只有固定时长等待节点能被动态驱动(条件等待不在此列)。只读。";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "列出流程等待节点";

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            List<FlowWaitNodeInfo> nodes;
            try { nodes = _ctx.Resolve<IFlowWaitNodeProvider>().GetFixedWaitNodes() ?? new List<FlowWaitNodeInfo>(); }
            catch (Exception ex) { return Task.FromResult(AgentToolResult.Fail("读取流程等待节点失败:" + ex.Message)); }

            if (nodes.Count == 0)
                return Task.FromResult(AgentToolResult.Ok(
                    "当前流程里没有固定时长的 Wait 等待节点。要按 τ 动态等待,需先在工艺设计器里往流程加一个「等待(固定时长)」节点作为稳态保温步(时长随便填,执行时会被动态覆盖),并在其属性里勾选「停留时间等待节点(DOE 稳态保温)」,再回来配置。"));

            var sb = new StringBuilder();
            sb.AppendLine($"当前流程有 {nodes.Count} 个固定时长等待节点:");
            sb.AppendLine();
            sb.AppendLine("| node_id | 节点名 | 当前等待 | 停留时间等待标记 | 等待设备稳定 |");
            sb.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (var n in nodes)
                sb.AppendLine($"| {n.NodeId} | {n.DisplayName} | {n.CurrentWaitTime:0.###} {UnitCn(n.TimeUnit)} | {(n.IsResidenceTimeWait ? "已勾选" : "-")} | {(n.WaitForStability ? "是" : "否")} |");
            sb.AppendLine();

            int markedCount = nodes.Count(n => n.IsResidenceTimeWait);
            if (markedCount == 1)
                sb.Append("有且仅有一个节点勾选了「停留时间等待节点」标记:动态稳态等待会自动驱动它,preview_doe_design 无需传 wait_node_id。");
            else if (markedCount == 0)
                sb.Append("没有节点勾选「停留时间等待节点」标记。最稳妥:请用户在设计器里打开保温等待节点的属性勾选该标记(一个流程只勾一个);" +
                          "或把这张表交给用户选定保温步,再把其 node_id 传给 transform.steady_state.wait_node_id。");
            else
                sb.Append($"注意:有 {markedCount} 个节点都勾选了「停留时间等待节点」标记(应只勾一个),动态等待无法自动定位。" +
                          "请用户在设计器里只保留一个标记后重试。");
            return Task.FromResult(AgentToolResult.Ok(sb.ToString()));
        }

        private static string UnitCn(string unit) => unit switch
        {
            "Milliseconds" => "毫秒",
            "Seconds" => "秒",
            "Minutes" => "分钟",
            "Hours" => "小时",
            _ => unit
        };
    }
}
