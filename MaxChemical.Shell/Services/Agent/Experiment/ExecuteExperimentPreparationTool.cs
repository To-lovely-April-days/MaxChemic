using System.Text.Json;
using MaxChemical.Shell.Services.Agent.Tools;

namespace MaxChemical.Shell.Services.Agent.Experiment
{
    /// <summary>
    /// 执行已确认的实验准备。确认语义覆盖设备放置、真实连接、清空替换流程和保存工程，
    /// 但不授予启动实验或执行设备命令的权限。
    /// </summary>
    public sealed class ExecuteExperimentPreparationTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;

        public ExecuteExperimentPreparationTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "execute_experiment_preparation";
        public string DisplayName => "执行实验准备";
        public string Description => "在用户明确确认后执行实验准备主流程：切换工艺设计器、幂等复用/添加设备、逐台真实连接检查、必需设备失败门禁、复核真实命令参数、复用 BuildFlowTool 生成顺序流程并保存工程。会清空替换当前流程画布，可能覆盖工程文件；绝不自动运行实验或执行流程中的设备命令。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""preparation_id"":{""type"":""string"",""description"":""create_experiment_preparation 返回的实验准备计划 ID""}
  },
  ""required"":[""preparation_id""]
}";
        public bool RequiresConfirmation => true;

        public string DescribeAction(JsonElement args)
        {
            string id = AgentToolContext.GetString(args, "preparation_id") ?? "";
            var coordinator = _ctx.Resolve<ExperimentPreparationCoordinator>();
            var plan = coordinator.GetPlan(id);
            return plan == null
                ? $"执行实验准备计划 {id}：将添加/复用设备、连接检查、清空替换流程并保存，但不运行实验。"
                : coordinator.DescribeExecution(plan);
        }

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string id = AgentToolContext.GetString(args, "preparation_id");
            var ct = _ctx.Resolve<XiaoTongAgent>().CurrentTurnToken;
            var result = await _ctx.Resolve<ExperimentPreparationCoordinator>()
                .ExecuteAsync(_ctx, id, ct).ConfigureAwait(false);
            return result.Success
                ? AgentToolResult.Ok(result.Message)
                : AgentToolResult.Fail(result.Message);
        }
    }
}
