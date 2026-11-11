using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using MaxChemical.Shell.Services.Agent.Memory;

namespace MaxChemical.Shell.Services.Agent.Tools
{
    /// <summary>
    /// 保存长期记忆(save_memory):把用户明确表达的长期偏好、约定、别名存入本地记忆文件。
    /// 系统在每轮对话开始时会自动召回相关记忆注入提示词。
    /// 只存值得长期记住的事实;严禁保存密钥、密码、试剂配方等敏感信息。
    /// </summary>
    public sealed class SaveMemoryTool : IAgentTool
    {
        private readonly MemoryStore _store;

        public SaveMemoryTool(MemoryStore store) => _store = store;

        public string Name => "save_memory";
        public string DisplayName => "保存长期记忆";
        public string Description => @"把用户明确表达的长期偏好、约定、别名、个人习惯存入本地记忆(仅本机 JSON 文件),下次对话会自动召回。只在对话收尾时保存真正值得长期记住的事实,一轮最多存 2 条;严禁保存 API 密钥、密码、试剂配方等敏感信息;实验数据与项目信息不写进记忆。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""content"":{""type"":""string"",""description"":""值得长期记住的事实,一句话,不超过 200 字""},
    ""category"":{""type"":""string"",""description"":""类别,如 用户偏好 / 项目约定 / 设备别名,默认「通用」""},
    ""tags"":{""type"":""array"",""items"":{""type"":""string""},""description"":""关键词标签,便于日后召回,最多 5 个""}
  },
  ""required"":[""content""]
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "保存长期记忆";

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string content = AgentToolContext.GetString(args, "content");
            string category = AgentToolContext.GetString(args, "category");

            var tags = new List<string>();
            if (args.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tagsEl.EnumerateArray())
                    if (t.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(t.GetString()))
                        tags.Add(t.GetString());
            }

            var (ok, message) = _store.Save(content, category, tags);
            return Task.FromResult(ok
                ? AgentToolResult.Ok("记忆已保存。不必向用户复述保存过程,简短确认即可。")
                : AgentToolResult.Ok("记忆未保存:" + message + "。如实转告用户即可,不要重试。"));
        }
    }
}
