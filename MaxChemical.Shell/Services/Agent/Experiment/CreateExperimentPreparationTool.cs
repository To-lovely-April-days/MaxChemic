using System.Text.Json;
using MaxChemical.Shell.Services.Agent.Tools;

namespace MaxChemical.Shell.Services.Agent.Experiment
{
    /// <summary>创建实验准备计划：只调研、读取设备库和生成计划，不修改画布、不连接设备。</summary>
    public sealed class CreateExperimentPreparationTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;

        public CreateExperimentPreparationTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "create_experiment_preparation";
        public string DisplayName => "创建实验准备计划";
        public string Description => "根据实验目标并行检索公开资料与本地知识库，读取真实左侧设备库，调用一次 DeepSeek 生成并校验结构化实验计划。只读调研与规划，不添加设备、不连接、不生成流程、不运行实验。public_search_query 只能填写公开实验主题，不得包含本地设备配置、私有工艺参数或敏感信息。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""experiment_description"":{""type"":""string"",""description"":""实验目标、物料、工艺条件和安全约束；用于本地知识库检索和计划生成""},
    ""public_search_query"":{""type"":""string"",""description"":""仅公开实验主题的检索词，不得带本地私有参数、设备配置、路径或密钥""}
  },
  ""required"":[""experiment_description"",""public_search_query""]
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "创建实验准备计划（只读调研和规划，不修改设备或流程）";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string description = AgentToolContext.GetString(args, "experiment_description");
            string query = AgentToolContext.GetString(args, "public_search_query");
            var ct = _ctx.Resolve<XiaoTongAgent>().CurrentTurnToken;
            var result = await _ctx.Resolve<ExperimentPreparationCoordinator>()
                .CreatePlanAsync(_ctx, description, query, ct).ConfigureAwait(false);
            return result.Success
                ? AgentToolResult.Ok(result.Message)
                : AgentToolResult.Fail(result.Message);
        }
    }
}
