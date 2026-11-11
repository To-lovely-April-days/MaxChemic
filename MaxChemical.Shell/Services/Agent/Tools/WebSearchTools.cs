using System;
using System.Text.Json;
using System.Threading.Tasks;
using MaxChemical.Shell.Services.Agent.Search;

namespace MaxChemical.Shell.Services.Agent.Tools
{
    /// <summary>
    /// 联网搜索(web_search):检索公开信息(新闻、文献、通用知识),结果附来源链接。
    /// 只读;未配置 Bocha ApiKey 时返回可读提示而不是报错。
    /// 系统提示词已规定:本实验室设备参数、实验数据、工艺条件严禁写进搜索词。
    /// </summary>
    public sealed class WebSearchTool : IAgentTool
    {
        private readonly WebSearchService _search;

        public WebSearchTool(WebSearchService search) => _search = search;

        public string Name => "web_search";
        public string DisplayName => "联网搜索";
        public string Description => @"联网搜索公开信息(行业动态、通用知识、外部资料)。回答用户问到的最新公开资料、新闻、通用知识时使用;本实验室的设备、实验数据、工艺参数严禁上传搜索,这类问题必须派专员用本地工具。结果附来源链接,引用时必须标注。只读。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""query"":{""type"":""string"",""description"":""搜索词;仅限公开信息;不涉及本实验室设备参数与实验数据""},
    ""count"":{""type"":""integer"",""description"":""返回条数,默认 5,上限 10""}
  },
  ""required"":[""query""]
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "联网搜索公开信息";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            if (!_search.IsConfigured)
                return AgentToolResult.Fail(
                    "联网搜索未配置(appsettings.json → WebSearch:ApiKey)。请如实告知用户联网搜索功能暂不可用,改用已有知识回答,不要编造。");

            string query = AgentToolContext.GetString(args, "query");
            if (string.IsNullOrWhiteSpace(query))
                return AgentToolResult.Fail("query 必填:给出要搜索的问题或关键词。");

            try
            {
                string text = await _search.SearchAsync(query).ConfigureAwait(false);
                return AgentToolResult.Ok(text);
            }
            catch (Exception ex)
            {
                return AgentToolResult.Fail("搜索失败:" + ex.Message +
                    "。请如实告知用户联网搜索暂时失败,不要编造搜索结果。");
            }
        }
    }
}
