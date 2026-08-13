using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MaxChemical.Shell.ViewModels;

namespace MaxChemical.Shell.Services.Agent.Tools
{
    /// <summary>
    /// 请用户选择:在聊天窗弹出选择卡(单选=单选框,多选=复选框),阻塞等待用户点选。
    /// 设备重名、批次/项目歧义、方案二选一这类「有限候选」的澄清,点选比让用户打字
    /// 又快又不会敲错名字;选择结果原样回传给模型继续当前回合。
    /// </summary>
    public sealed class AskUserChoiceTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public AskUserChoiceTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "ask_user_choice";
        public string DisplayName => "请用户选择";
        public string Description => @"需要用户在有限候选里做选择或澄清时(设备重名、批次/项目歧义、方案二选一、多选因子等),用本工具弹出选择卡让用户点选,不要用纯文字提问让用户打字。
单选传 multi_select=false(单选框),允许多个答案时传 true(复选框)。选项 2~8 个,每项文字要完整可辨(带编号/型号/特征,用户不用回忆上下文也能选对)。
自由文本问题(项目命名、数值输入)不适用本工具,照常文字提问。返回用户所选项;用户取消或超时(3 分钟)时不要再次弹同一张卡,改用文字沟通。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""question"":{""type"":""string"",""description"":""要问用户的问题(一句话,说清选了之后做什么)""},
    ""options"":{""type"":""array"",""minItems"":2,""maxItems"":8,""items"":{""type"":""string""},""description"":""候选项文本,2~8 个,逐项完整可辨""},
    ""multi_select"":{""type"":""boolean"",""description"":""true=可多选(复选框),false/省略=单选(单选框)""}
  },
  ""required"":[""question"",""options""]
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "弹出选择卡等待用户点选";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string question = AgentToolContext.GetString(args, "question");
            if (string.IsNullOrWhiteSpace(question))
                return AgentToolResult.Fail("question 必填。");

            var options = new List<string>();
            if (args.TryGetProperty("options", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString()))
                        options.Add(el.GetString().Trim());
            options = options.Distinct().ToList();

            if (options.Count < 2)
                return AgentToolResult.Fail("options 至少要有 2 个互不相同的候选项;只有一个候选就不必问,直接用它并向用户说明。");
            if (options.Count > 8)
                return AgentToolResult.Fail($"options 最多 8 个(当前 {options.Count}),请先收窄候选(按用户提过的特征过滤)再问。");

            bool multi = AgentToolContext.GetBool(args, "multi_select") ?? false;

            AgentChatViewModel vm;
            try { vm = _ctx.Resolve<AgentChatViewModel>(); }
            catch (Exception ex) { return AgentToolResult.Fail("聊天窗不可用,无法弹选择卡:" + ex.Message); }

            string answer = await vm.AskUserChoiceAsync(question, options, multi).ConfigureAwait(false);

            return string.IsNullOrEmpty(answer)
                ? AgentToolResult.Ok("用户取消了选择(或超时未选)。不要再次弹出同一张选择卡,改用文字与用户沟通,或换一个角度澄清。")
                : AgentToolResult.Ok($"用户选择了:{answer}");
        }
    }
}
