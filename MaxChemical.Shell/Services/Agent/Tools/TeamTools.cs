using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace MaxChemical.Shell.Services.Agent.Tools
{
    /// <summary>
    /// 派工工具:总控小桐把任务交给某位专员执行,拿回工作汇报。
    /// 专员在同一进程内以独立提示词与工具子集跑自己的工具循环,
    /// 确认卡、选择卡、图表直插与主对话共用同一条通道。
    /// </summary>
    internal static class SpecialistDispatch
    {
        public const string TaskDescription = "交给专员的任务描述。必须自包含:专员看不到对话历史,用户原话里的数值(带单位)、设备名、项目/批次/预览编号、用户已确认的结论都要原样写进来;物理常数(体积、浓度)只能照抄用户原话,不许换算或省略。";
        public const string ContextDescription = "可选的补充背景:与任务相关的前情(上一步专员汇报的关键结论、用户的偏好等)。";

        public static string SchemaJson => @"{
  ""type"":""object"",
  ""properties"":{
    ""task"":{""type"":""string"",""description"":""" + TaskDescription + @"""},
    ""context"":{""type"":""string"",""description"":""" + ContextDescription + @"""}
  },
  ""required"":[""task""]
}";

        public static async Task<AgentToolResult> RunAsync(AgentToolContext ctx, SpecialistProfile profile, JsonElement args)
        {
            string task = AgentToolContext.GetString(args, "task");
            if (string.IsNullOrWhiteSpace(task))
                return AgentToolResult.Fail("task 必填:写清楚要专员做什么,并附上用户原话中的关键数值、单位与编号。");

            string context = AgentToolContext.GetString(args, "context");

            XiaoTongAgent agent;
            try { agent = ctx.Resolve<XiaoTongAgent>(); }
            catch (Exception ex) { return AgentToolResult.Fail("无法联系专员:" + ex.Message); }

            // 传入当前轮的取消令牌:用户点「停止」能立刻中断专员子循环(OCE 向上传播终止整轮)
            string report = await agent.RunSpecialistAsync(profile, task, context, agent.CurrentTurnToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(report))
                report = "(专员没有返回汇报,请把任务拆小或补充信息后重试一次;仍失败就向用户说明。)";

            return AgentToolResult.Ok($"【{profile.DisplayName}汇报】\n{report}");
        }

        public static string Describe(SpecialistProfile profile, JsonElement args)
        {
            string task = AgentToolContext.GetString(args, "task") ?? "";
            if (task.Length > 60) task = task.Substring(0, 60) + "…";
            return $"派工给{profile.DisplayName}:{task}";
        }
    }

    /// <summary>派工:设计专员(实验设计与建库)。</summary>
    public sealed class AssignDesignTaskTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public AssignDesignTaskTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "assign_design_task";
        public string DisplayName => "指派设计专员";
        public string Description => "把实验设计类任务派给设计专员:因子候选分析、DOE 设计预览与入库、化学家坐标系设计(停留时间/当量比)、口述搭建流程、按智能决策建议生成下一轮预览。专员完成后返回工作汇报。" + SpecialistDispatch.TaskDescription;
        public string ParametersSchema => SpecialistDispatch.SchemaJson;
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => SpecialistDispatch.Describe(SpecialistTeam.Design, args);
        public Task<AgentToolResult> ExecuteAsync(JsonElement args) => SpecialistDispatch.RunAsync(_ctx, SpecialistTeam.Design, args);
    }

    /// <summary>派工:执行专员(设备与执行)。</summary>
    public sealed class AssignExecutionTaskTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public AssignExecutionTaskTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "assign_execution_task";
        public string DisplayName => "指派执行专员";
        public string Description => "把设备与执行类任务派给执行专员:设备清单/状态/连接实测、设置参数、执行设备命令、流程启停、DOE 批次执行与进度查询、化学家坐标系联动调参(停留时间/当量比一次写入)。专员完成后返回工作汇报。" + SpecialistDispatch.TaskDescription;
        public string ParametersSchema => SpecialistDispatch.SchemaJson;
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => SpecialistDispatch.Describe(SpecialistTeam.Execution, args);
        public Task<AgentToolResult> ExecuteAsync(JsonElement args) => SpecialistDispatch.RunAsync(_ctx, SpecialistTeam.Execution, args);
    }

    /// <summary>派工:分析专员(数据分析与预测)。</summary>
    public sealed class AssignAnalysisTaskTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public AssignAnalysisTaskTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "assign_analysis_task";
        public string DisplayName => "指派分析专员";
        public string Description => "把数据分析与预测类任务派给分析专员:批次/项目统计分析、实验历史查询、项目检索、智能决策报告解读、What-if 预测、反向寻优、实验室历史记忆检索(以前做过什么、上次最优条件)、生成项目 PDF 实验报告。专员完成后返回工作汇报。" + SpecialistDispatch.TaskDescription;
        public string ParametersSchema => SpecialistDispatch.SchemaJson;
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => SpecialistDispatch.Describe(SpecialistTeam.Analysis, args);
        public Task<AgentToolResult> ExecuteAsync(JsonElement args) => SpecialistDispatch.RunAsync(_ctx, SpecialistTeam.Analysis, args);
    }

    /// <summary>派工:机理专员(机理动力学)。</summary>
    public sealed class AssignMechanismTaskTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public AssignMechanismTaskTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "assign_mechanism_task";
        public string DisplayName => "指派机理专员";
        public string Description => "把机理动力学类任务派给机理专员:速率常数与活化能拟合、机理外推预测(超范围转化率、达标所需停留时间、放大换算)、取样浓度数据录入、多步反应网络拟合与浓度曲线仿真。专员完成后返回工作汇报。" + SpecialistDispatch.TaskDescription;
        public string ParametersSchema => SpecialistDispatch.SchemaJson;
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => SpecialistDispatch.Describe(SpecialistTeam.Mechanism, args);
        public Task<AgentToolResult> ExecuteAsync(JsonElement args) => SpecialistDispatch.RunAsync(_ctx, SpecialistTeam.Mechanism, args);
    }
}
