using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DevicePlugins.Devices;

namespace MaxChemical.Shell.Services.Agent.Tools
{
    /// <summary>
    /// 读取设备实时测量值(只读,执行驱动的「读命令」取数)。
    ///
    /// ── 为什么需要这个工具 ──────────────────────────────────────────
    /// get_device_status 只 dump 参数表里的静态值;而实时读数(温度、压力、流量)
    /// 在本平台上要靠执行驱动的读命令(如「获取温度数据」)才能拿到——
    /// 模拟模式下驱动会在读命令内部返回模拟值,所以执行读命令同样有效。
    /// 通用的 execute_device_command 不对外部 MCP 客户端开放(它也能执行写命令),
    /// 于是外部客户端问「现在几度」时无路可走。本工具补上这条窄路。
    ///
    /// ── 安全判据:什么算「读命令」──────────────────────────────────
    /// 满足任一条即放行,其余一律拒绝:
    ///   a. 命令被本设备 CollectableParameters 声明为取数来源(SourceCommand)。
    ///      这是驱动声明制,权威判据;
    ///   b. 命令**没有任何输入参数**,且名称以「获取 / 读取 / 查询」开头。
    ///      无输入意味着它改变不了任何设定值;动词前缀再排除「启动 / 停止 / 设置」类。
    /// 判据 b 是回退:当前仓库里还没有驱动声明过 CollectableParameters,
    /// 只靠判据 a 工具将处处失效。将来驱动补上声明后,判据 a 自然优先生效。
    ///
    /// 保守性代价:带输入的读命令(如「获取某通道数据(通道号)」)会被判据 b 排除,
    /// 这是有意的取舍——宁可少读一个通道,不冒执行写命令的险。
    /// 这类命令请驱动通过 CollectableParameters 声明来放行。
    /// </summary>
    public sealed class ReadDeviceMeasurementTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public ReadDeviceMeasurementTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "read_device_measurement";
        public string DisplayName => "读取设备实时测量值";
        public string Description =>
            "执行设备的只读取数命令,读取实时测量值(温度、压力、流量等)。模拟模式下返回驱动生成的模拟读数。" +
            "不传 command_name 时,返回该设备可用的读命令清单供选择;传入后执行并返回带单位的测量值。" +
            "用户问「现在几度/压力多少/流速多少」这类实时数值时用本工具,不要用 get_device_status(它只回显参数表,不发起实测)。" +
            "本工具只能执行无副作用的读命令,启动/停止/设置类命令会被拒绝。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""device_name"":{""type"":""string"",""description"":""设备显示名,如 高低温#1;不确定时先调用 list_devices""},
    ""command_name"":{""type"":""string"",""description"":""要执行的读命令名,须与本工具返回的读命令清单一致。省略则仅列出清单""}
  },
  ""required"":[""device_name""]
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args)
        {
            string d = AgentToolContext.GetString(args, "device_name");
            string c = AgentToolContext.GetString(args, "command_name");
            return string.IsNullOrEmpty(c) ? $"查看设备「{d}」的读命令清单" : $"读取设备「{d}」的「{c}」";
        }

        /// <summary>判据 b 的动词前缀。只认这三个,不做更聪明的猜测。</summary>
        private static readonly string[] ReadVerbs = { "获取", "读取", "查询" };

        /// <summary>本设备允许执行的读命令集合(判据 a ∪ 判据 b)。</summary>
        private static List<DeviceCommand> ResolveReadCommands(IDevice dev)
        {
            var commands = dev.Commands;
            if (commands == null || commands.Count == 0) return new List<DeviceCommand>();

            // 判据 a:驱动声明的取数命令(CollectableParameters 在基类 Device 上,不在 IDevice 接口上)
            var declared = new HashSet<string>(StringComparer.Ordinal);
            if (dev is Device baseDevice)
            {
                foreach (var cp in baseDevice.CollectableParameters)
                    declared.Add(cp.SourceCommand);
            }

            var result = new List<DeviceCommand>();
            foreach (var cmd in commands)
            {
                if (cmd?.Name == null) continue;

                if (declared.Contains(cmd.Name))
                {
                    result.Add(cmd);
                    continue;
                }

                // 判据 b:无输入参数 + 读动词前缀
                bool noInputs;
                try { noInputs = cmd.InputParameters?.Variables == null || cmd.InputParameters.Variables.Count == 0; }
                catch { noInputs = false; }

                if (noInputs && ReadVerbs.Any(v => cmd.Name.StartsWith(v, StringComparison.Ordinal)))
                    result.Add(cmd);
            }
            return result;
        }

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string name = AgentToolContext.GetString(args, "device_name");
            var (vm, display, err) = _ctx.FindDevice(name);
            if (vm == null) return AgentToolResult.Fail(err);

            var dev = vm.Device;
            var readCommands = ResolveReadCommands(dev);
            if (readCommands.Count == 0)
                return AgentToolResult.Ok(
                    $"设备「{display}」没有可用的只读取数命令(展示型设备或全部命令带输入/为控制类)。" +
                    "如需查看参数表当前值,请用 get_device_status。");

            string cmdName = AgentToolContext.GetString(args, "command_name");

            // 不传命令名:列清单让模型选,不自动全跑——模拟模式下驱动每条命令内置约 1 秒延时,全跑太慢
            if (string.IsNullOrWhiteSpace(cmdName))
            {
                var sb0 = new StringBuilder();
                sb0.AppendLine($"设备「{display}」可用的读命令({readCommands.Count} 条),选择一条后再次调用本工具:");
                foreach (var c in readCommands)
                    sb0.AppendLine($"- {c.Name}");
                return AgentToolResult.Ok(sb0.ToString());
            }

            var cmd = readCommands.FirstOrDefault(c => c.Name == cmdName);
            if (cmd == null)
            {
                // 分两种拒绝:命令存在但不是读命令(说清原因),和命令根本不存在
                bool existsButDenied = dev.Commands?.Any(c => c.Name == cmdName) == true;
                return AgentToolResult.Fail(existsButDenied
                    ? $"命令「{cmdName}」不是只读取数命令(带输入参数或属控制类),本工具不能执行它。可用读命令:" +
                      string.Join("、", readCommands.Select(c => c.Name))
                    : $"设备「{display}」没有命令「{cmdName}」。可用读命令:" +
                      string.Join("、", readCommands.Select(c => c.Name)));
            }

            try
            {
                DeviceCommandResult r = await cmd.ExecuteAsync(cmd.InputParameters).ConfigureAwait(false);
                if (r != null && !r.Success)
                    return AgentToolResult.Fail(
                        $"读取失败:{r.ErrorMessage ?? "设备未返回原因"}" +
                        (dev.IsSimulationMode ? "" : "(设备未连接时请先在主程序里连接,或切换到模拟模式)"));

                var sb = new StringBuilder();
                sb.Append($"「{display}」{cmdName}");
                sb.AppendLine(dev.IsSimulationMode ? "(模拟模式,以下为模拟读数):" : ":");

                int shown = 0;
                if (r?.OutputParameters?.Variables != null)
                {
                    foreach (ParameterBase p in r.OutputParameters.Variables.Take(20))
                    {
                        object v = null;
                        try { v = p.Value; } catch { }

                        // 中文说明(HelpText)在前、键名在后,单位跟在数值后——给模型转述用户时直接可读
                        string label = string.IsNullOrWhiteSpace(p.HelpText) ? p.Name : $"{p.HelpText}({p.Name})";
                        string unit = string.IsNullOrWhiteSpace(p.Unit) ? "" : " " + p.Unit;
                        sb.AppendLine($"- {label} = {v ?? "null"}{unit}");
                        shown++;
                    }
                }

                if (shown == 0)
                    sb.AppendLine("(命令执行成功,但没有返回输出参数)");

                return AgentToolResult.Ok(sb.ToString());
            }
            catch (Exception ex)
            {
                return AgentToolResult.Fail(
                    $"读取异常:{ex.Message}" +
                    (dev.IsSimulationMode ? "" : "(设备未连接时请先在主程序里连接,或切换到模拟模式)"));
            }
        }
    }
}
