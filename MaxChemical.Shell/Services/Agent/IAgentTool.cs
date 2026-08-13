using System.Text.Json;
using System.Threading.Tasks;

namespace MaxChemical.Shell.Services.Agent
{
    /// <summary>
    /// 小桐的一项技能(可被大模型函数调用)。
    /// ParametersSchema 用 OpenAI function calling 的 JSON Schema 字符串描述。
    /// RequiresConfirmation=true 的工具(写设备/启动流程等)执行前必须经用户确认。
    /// </summary>
    public interface IAgentTool
    {
        /// <summary>工具名(英文蛇形,给模型用),如 set_device_parameter。</summary>
        string Name { get; }

        /// <summary>给人看的名字,如「设置设备参数」。</summary>
        string DisplayName { get; }

        /// <summary>给模型看的功能描述(何时用、注意什么)。</summary>
        string Description { get; }

        /// <summary>参数 JSON Schema(对象字面量字符串)。</summary>
        string ParametersSchema { get; }

        /// <summary>是否为需要用户确认的写操作。</summary>
        bool RequiresConfirmation { get; }

        /// <summary>生成给用户看的操作复述(确认框文案)。仅确认类工具需要。</summary>
        string DescribeAction(JsonElement args);

        Task<AgentToolResult> ExecuteAsync(JsonElement args);
    }
}
