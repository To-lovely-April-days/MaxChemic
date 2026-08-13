using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MaxChemical.Modules.DOE.Data;

namespace MaxChemical.Shell.Services.Agent.Tools
{
    /// <summary>
    /// 实验室记忆:按关键词检索全部历史项目与独立批次(名称、目标、因子名、响应名、批次名),
    /// 回答「我们以前做过某某实验吗」「上次类似反应的最优条件是什么」这类跨项目问题。
    /// 纯 SQL 检索,查得到就给编号方便深挖,查不到就如实说没有——不靠印象回答。
    /// </summary>
    public sealed class SearchLabHistoryTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public SearchLabHistoryTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "search_lab_history";
        public string DisplayName => "检索实验室历史";
        public string Description => @"实验室记忆检索:按关键词(物质名、反应名、因子名、响应名、项目名片段)在全部历史项目与独立批次中查找。回答「以前做过某某实验吗」「上次类似实验的最优条件」这类跨项目历史问题时必须用本工具,严禁凭印象回答。
多个关键词用空格分隔,须同时命中(AND);宁可先用单个宽泛词查,再加词收窄。返回项目/批次的编号、状态、轮次、最优结果——编号可直接传给 analyze_doe_project / analyze_doe_batch 深入分析。检索不到时如实告知没有相关记录。只读。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""keywords"":{""type"":""string"",""description"":""检索关键词,多个用空格分隔(AND 关系,最多 5 个)""},
    ""limit"":{""type"":""integer"",""description"":""最多返回条数,默认 8,上限 20""}
  },
  ""required"":[""keywords""]
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "检索实验室历史记录";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string keywords = AgentToolContext.GetString(args, "keywords");
            if (string.IsNullOrWhiteSpace(keywords))
                return AgentToolResult.Fail("keywords 必填:给出要检索的物质名、反应名、因子名或项目名片段。");

            var terms = keywords
                .Split(new[] { ' ', '　', '\t', '\r', '\n', ',', ',', '、', ';', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => t.Length > 0)
                .Distinct()
                .ToList();
            if (terms.Count == 0)
                return AgentToolResult.Fail("keywords 解析后为空,请给出有效关键词。");
            if (terms.Count > 5) terms = terms.Take(5).ToList();

            int limit = (int)(AgentToolContext.GetNumber(args, "limit") ?? 8);

            IDOERepository repo;
            try { repo = _ctx.Resolve<IDOERepository>(); }
            catch (Exception ex) { return AgentToolResult.Fail("数据库服务不可用:" + ex.Message); }

            List<LabHistoryHit> hits;
            try { hits = await repo.SearchLabHistoryAsync(terms, limit).ConfigureAwait(false); }
            catch (Exception ex) { return AgentToolResult.Fail("历史检索失败:" + ex.Message); }

            string termText = string.Join("、", terms.Select(t => $"「{t}」"));
            if (hits == null || hits.Count == 0)
                return AgentToolResult.Ok(
                    $"实验室历史里没有同时命中 {termText} 的项目或批次。" +
                    "如实告诉用户没有相关记录;若关键词较多,可减少关键词或换同义词再查一次(最多再试一次,不要反复盲试)。");

            var sb = new StringBuilder();
            sb.AppendLine($"实验室历史检索:{termText},命中 {hits.Count} 条(按最近更新排序)。");
            sb.AppendLine();
            sb.AppendLine("| 类型 | 编号 | 名称 | 状态/阶段 | 轮次 | 累计实验 | 最优值 | 最优条件 | 最近更新 | 命中 |");
            sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
            foreach (var h in hits)
            {
                string best = h.BestResponseValue.HasValue ? h.BestResponseValue.Value.ToString("0.####") : "-";
                string cond = FormatBestFactors(h.BestFactorsJson);
                string phase = string.IsNullOrEmpty(h.Phase) ? h.Status : $"{h.Status}/{h.Phase}";
                sb.AppendLine($"| {h.Kind} | {h.Id} | {h.Name} | {phase} | {(h.Kind == "项目" ? h.Rounds.ToString() : "-")} | {h.TotalExperiments} | {best} | {cond} | {h.UpdatedTime:yyyy-MM-dd} | {h.MatchedOn} |");
            }
            sb.AppendLine();
            sb.Append("把这张表转述给用户(保留编号)。需要深挖某个项目的过程与结论时,把编号当 project_name 传给 analyze_doe_project;" +
                      "单个批次传编号给 analyze_doe_batch。最优值一列来自项目档案的历史记录,精确复现条件前建议先做分析确认。");
            return AgentToolResult.Ok(sb.ToString());
        }

        /// <summary>把 best_factors_json 压成一行「因子=值」;解析失败给短横。</summary>
        private static string FormatBestFactors(string json)
        {
            var nums = DoeAnalysisHelper.ParseNumbers(json);
            if (nums.Count == 0) return "-";
            return string.Join(", ", nums.Take(4).Select(kv => $"{kv.Key}={kv.Value:0.###}"));
        }
    }
}
