using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MaxChemical.Shell.Services.Agent
{
    /// <summary>对话消息角色。</summary>
    public static class AgentRole
    {
        public const string System = "system";
        public const string User = "user";
        public const string Assistant = "assistant";
        public const string Tool = "tool";
    }

    /// <summary>
    /// OpenAI 兼容的对话消息(DeepSeek 同协议)。
    /// assistant 消息可携带 tool_calls;tool 消息必须带 tool_call_id。
    /// </summary>
    public sealed class AgentMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; }

        [JsonPropertyName("content")]
        public string Content { get; set; }

        [JsonPropertyName("tool_calls")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ToolCall> ToolCalls { get; set; }

        [JsonPropertyName("tool_call_id")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string ToolCallId { get; set; }

        /// <summary>
        /// 入库历史时免于长度截断(专员汇报:编号与待决事项散布全文)。
        /// 仅进程内标记,不上 API 也不落存档。
        /// </summary>
        [JsonIgnore]
        public bool PreserveFullInHistory { get; set; }

        public static AgentMessage FromSystem(string content) => new() { Role = AgentRole.System, Content = content };
        public static AgentMessage FromUser(string content) => new() { Role = AgentRole.User, Content = content };
        public static AgentMessage FromTool(string toolCallId, string content)
            => new() { Role = AgentRole.Tool, ToolCallId = toolCallId, Content = content };
    }

    public sealed class ToolCall
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        [JsonPropertyName("function")]
        public ToolCallFunction Function { get; set; }
    }

    public sealed class ToolCallFunction
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>模型给出的实参,JSON 字符串。</summary>
        [JsonPropertyName("arguments")]
        public string Arguments { get; set; }
    }

    /// <summary>工具执行结果。</summary>
    public sealed class AgentToolResult
    {
        public bool Success { get; set; } = true;
        public string Content { get; set; } = "";

        public static AgentToolResult Ok(string content) => new() { Success = true, Content = content };
        public static AgentToolResult Fail(string message) => new() { Success = false, Content = "错误:" + message };

        public string ToLlmText() => Content;
    }

    /// <summary>
    /// 待确认的写操作:Agent 循环在此暂停,等待用户在界面/语音上确认或取消。
    /// </summary>
    public sealed class PendingToolAction
    {
        public string ToolName { get; set; }
        public string DisplayName { get; set; }
        public string Summary { get; set; }      // 给人看的复述,如"将 高低温#1 的 TargetValue 设为 90"
        public JsonElement Arguments { get; set; }
    }

    /// <summary>Agent 向界面推送的事件类型。</summary>
    public enum AgentEventKind
    {
        UserMessage,        // 用户消息已入列
        AssistantThinking,  // 已发起一次模型调用
        AssistantMessage,   // 助手文本回复(最终或中间)
        ToolCallStarted,    // 开始执行某个工具
        ToolCallFinished,   // 工具执行完毕(含结果摘要)
        ConfirmRequired,    // 需要用户确认写操作
        Error
    }

    public sealed class AgentEvent
    {
        public AgentEventKind Kind { get; set; }
        public string Text { get; set; }
        public string ToolName { get; set; }
        public bool ToolSuccess { get; set; } = true;
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
}
