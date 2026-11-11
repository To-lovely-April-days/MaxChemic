using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MaxChemical.Shell.Services.Agent.Knowledge;

namespace MaxChemical.Shell.Services.Agent.Tools
{
    /// <summary>
    /// 知识库检索(kb_search):检索用户导入的文档(操作手册、SOP、论文、规程等)。
    /// 查不到就如实说知识库没有,严禁凭印象编造;作答必须标注出处文档名。
    /// 与 search_lab_history(查做过的实验)完全不同。
    /// </summary>
    public sealed class KnowledgeSearchTool : IAgentTool
    {
        private readonly KnowledgeBaseService _kb;

        public KnowledgeSearchTool(KnowledgeBaseService kb) => _kb = kb;

        public string Name => "kb_search";
        public string DisplayName => "检索知识库";
        public string Description => @"检索用户导入的知识库文档(操作手册、SOP、论文、规程等)。回答「按资料/手册/SOP 上怎么说」这类问题必须先查它;查不到就如实说知识库没有该资料,严禁凭印象编造;作答须标注来源文档名。与实验历史检索(search_lab_history,查做过的实验)完全不同,别混用。只读。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""query"":{""type"":""string"",""description"":""检索问题或关键词,自然语言""},
    ""top_k"":{""type"":""integer"",""description"":""返回片段数,默认 3,上限 8""}
  },
  ""required"":[""query""]
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "检索知识库文档";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string query = AgentToolContext.GetString(args, "query");
            if (string.IsNullOrWhiteSpace(query))
                return AgentToolResult.Fail("query 必填:给出要检索的问题或关键词。");

            int topK = (int)(AgentToolContext.GetNumber(args, "top_k") ?? 3);

            var docs = _kb.ListDocuments();
            if (docs.Count == 0)
                return AgentToolResult.Ok(
                    "知识库目前是空的(用户还没有导入任何文档)。如实告知用户知识库里没有资料,可建议用户点主界面右上角小桐旁的「知识库」按钮导入文档;严禁编造资料内容。");

            var hits = await _kb.SearchAsync(query, topK).ConfigureAwait(false);
            if (hits.Count == 0)
                return AgentToolResult.Ok(
                    $"知识库中检索不到与「{query}」相关的资料。如实告知用户内部资料中没有找到相关内容,严禁编造;可建议用户更换关键词或确认资料是否已导入(用 kb_list_documents 查看库内文档)。");

            var sb = new StringBuilder();
            sb.AppendLine($"知识库检索:命中 {hits.Count} 个相关片段(按相关度排序)。");
            sb.AppendLine();
            for (int i = 0; i < hits.Count; i++)
            {
                sb.AppendLine($"**片段 {i + 1}**(出自《{hits[i].DocName}》,相关度 {hits[i].Score:0.##}):");
                sb.AppendLine(hits[i].Text);
                sb.AppendLine();
            }
            sb.Append("请严格基于以上片段作答,并标注出处文档名;片段里没有的信息严禁编造,如实告知用户知识库中没有相关内容。");
            return AgentToolResult.Ok(sb.ToString());
        }
    }

    /// <summary>列出知识库全部文档(kb_list_documents),供用户确认资料在不在库里。只读。</summary>
    public sealed class KnowledgeListDocumentsTool : IAgentTool
    {
        private readonly KnowledgeBaseService _kb;

        public KnowledgeListDocumentsTool(KnowledgeBaseService kb) => _kb = kb;

        public string Name => "kb_list_documents";
        public string DisplayName => "列出知识库文档";
        public string Description => "列出知识库中已导入的全部文档(名称/块数/导入时间/是否已向量化),供用户确认资料在不在库里。用户问「库里有哪些资料」「某手册导入了吗」时使用。只读。";
        public string ParametersSchema => @"{ ""type"":""object"", ""properties"":{} }";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "列出知识库文档";

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var docs = _kb.ListDocuments();
            if (docs.Count == 0)
                return Task.FromResult(AgentToolResult.Ok("知识库目前是空的,还没有导入任何文档。可建议用户点主界面右上角小桐旁的「知识库」按钮导入。"));
            var sb = new StringBuilder();
            sb.AppendLine($"知识库现有 {docs.Count} 份文档:");
            sb.AppendLine();
            sb.AppendLine("| 文档名 | 块数 | 导入时间 | 向量状态 |");
            sb.AppendLine("| --- | --- | --- | --- |");
            foreach (var d in docs)
                sb.AppendLine($"| {d.Name} | {d.ChunkCount} | {d.ImportedAt:yyyy-MM-dd HH:mm} | {(d.HasVectors ? "已向量化" : "关键词模式")} |");
            sb.Append("原样转述给用户即可;用户可点主界面右上角小桐旁的「知识库」按钮管理文档。");
            return Task.FromResult(AgentToolResult.Ok(sb.ToString()));
        }
    }
}
