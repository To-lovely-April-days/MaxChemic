using System.Text;
using System.Text.Json;
using MaxChemical.Shell.Services.Agent.Tools;

namespace MaxChemical.Shell.Services.Agent.Experiment
{
    /// <summary>只读查询计划状态、阶段进度、设备连接复核结果和最近错误。</summary>
    public sealed class GetExperimentPreparationStatusTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;

        public GetExperimentPreparationStatusTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "get_experiment_preparation_status";
        public string DisplayName => "查询实验准备状态";
        public string Description => "只读查询实验准备计划的状态、进度、设备映射和逐台连接结果、部分失败、保存路径及调研来源；不修改设备、流程或工程。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""preparation_id"":{""type"":""string"",""description"":""实验准备计划 ID""}
  },
  ""required"":[""preparation_id""]
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "查询实验准备状态（只读）";

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string id = AgentToolContext.GetString(args, "preparation_id");
            if (string.IsNullOrWhiteSpace(id))
                return Task.FromResult(AgentToolResult.Fail("preparation_id 必填。"));

            var plan = _ctx.Resolve<ExperimentPreparationCoordinator>().GetPlan(id);
            if (plan == null)
                return Task.FromResult(AgentToolResult.Fail("找不到实验准备计划：" + id));

            var sb = new StringBuilder();
            sb.AppendLine($"计划 ID：{plan.PreparationId}");
            sb.AppendLine($"状态：{plan.Status}");
            sb.AppendLine($"进度：{plan.Progress}");
            sb.AppendLine($"实验摘要：{plan.ExperimentSummary}");
            sb.AppendLine($"流程名称：{plan.FlowName}；步骤数：{plan.Steps.Count}");
            if (!string.IsNullOrWhiteSpace(plan.LastError)) sb.AppendLine("最近错误：" + plan.LastError);
            if (!string.IsNullOrWhiteSpace(plan.SavedPath)) sb.AppendLine("工程路径：" + plan.SavedPath);

            sb.AppendLine("\n设备映射与连接复核：");
            foreach (var device in plan.Devices)
            {
                sb.AppendLine($"- 设备库：{device.LibraryName}；类型：{device.DeviceType}；{(device.Required ? "必需" : "可选")}");
                sb.AppendLine($"  画布：{device.CanvasDisplayName}（DeviceId：{device.DeviceId}）；放置：{device.PlacementMessage}");
                sb.AppendLine($"  连接：{device.ConnectionState}；{device.ConnectionMessage}");
            }

            if (plan.MissingInformation.Count > 0)
            {
                sb.AppendLine("\n待补充信息：");
                foreach (var item in plan.MissingInformation) sb.AppendLine("- " + item);
            }
            if (plan.PartialFailures.Count > 0)
            {
                sb.AppendLine("\n部分失败：");
                foreach (var item in plan.PartialFailures) sb.AppendLine("- " + item);
            }
            if (plan.SafetyNotes.Count > 0)
            {
                sb.AppendLine("\n安全说明：");
                foreach (var item in plan.SafetyNotes) sb.AppendLine("- " + item);
            }
            sb.AppendLine("\n调研渠道：联网=" + plan.WebResearchStatus + "；知识库=" + plan.KnowledgeResearchStatus);
            sb.AppendLine("调研文本：\n" + plan.ResearchText);
            sb.AppendLine("\n安全边界：该查询不启动实验，不执行设备命令。");
            return Task.FromResult(AgentToolResult.Ok(sb.ToString().Trim()));
        }
    }
}
