using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DevicePlugins.Devices;

namespace MaxChemical.Shell.Services.Agent.Tools
{
    /// <summary>列出画布上的全部设备(只读)。</summary>
    public sealed class ListDevicesTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public ListDevicesTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "list_devices";
        public string DisplayName => "查看设备列表";
        public string Description => "列出当前工艺画布上已添加的全部设备:显示名、类型、连接状态、是否模拟模式。回答任何设备相关问题前先调用它确认设备存在。";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "查看设备列表";

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var devices = _ctx.GetCanvasDevices();
            if (devices.Count == 0)
                return Task.FromResult(AgentToolResult.Ok("画布上还没有设备。可提示用户先在设计器中添加设备。"));

            var sb = new StringBuilder();
            sb.AppendLine($"画布共 {devices.Count} 台设备:");
            foreach (var d in devices)
            {
                string display = _ctx.GetDisplayName(d.DeviceId);
                string conn = "未知";
                string sim = "";
                try
                {
                    conn = d.Device?.IsConnected == true ? "已连接" : "未连接";
                    sim = d.Device?.IsSimulationMode == true ? " · 模拟模式" : "";
                }
                catch { }
                sb.AppendLine($"- {display}(类型:{d.Device?.Name ?? "?"},{conn}{sim})");
            }
            return Task.FromResult(AgentToolResult.Ok(sb.ToString()));
        }
    }

    /// <summary>真实连接设备(打开串口/TCP)并回报结果——检查连接状态必须用它实测,不能只读状态标志。</summary>
    public sealed class ConnectDevicesTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public ConnectDevicesTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "connect_devices";
        public string DisplayName => "连接设备";
        public string Description => "对设备执行真实连接(打开串口/建立 TCP)并逐台回报结果。用户询问设备连接状态、或设备显示未连接时,必须调用本工具实测,而不是只报 list_devices 里的状态标志。规则:模拟模式设备没有物理链路,不做连接动作,直接视为就绪;展示型设备(无通讯配置,如锥形瓶)自动跳过;device_name 省略时处理画布上全部设备。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""device_name"":{""type"":""string"",""description"":""只连接这一台(显示名);省略则全部连接""}
  }
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "连接设备";

        /// <summary>
        /// 是否为可通讯设备:展示型设备(锥形瓶等被动容器)没有通讯配置参数,不参与连接。
        /// 判据:有命令、且参数表中带任一通讯配置(串口/IP/端口/波特率/DTU/设备ID/从站/Modbus)。
        /// 拿不准时倾向"可连接"(尝试连接无害)。
        /// </summary>
        internal static bool IsConnectable(IDevice dev)
        {
            try
            {
                if (dev.Commands == null || dev.Commands.Count == 0) return false;

                string[] keys = { "串口", "COM", "IP", "端口", "波特率", "DTU", "DeviceId", "设备ID", "从站", "Modbus" };
                foreach (var p in dev.Parameters.Variables)
                {
                    string basis = (p.Name ?? "") + "|" + (p.HelpText ?? "");
                    foreach (var k in keys)
                        if (basis.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
                return false;
            }
            catch { return true; }
        }

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            // 模拟模式是全局开关(主工具栏):模拟状态下没有物理链路,逐台连接没有意义
            if (_ctx.GetGlobalSimulationMode() == true)
                return AgentToolResult.Ok(
                    "当前系统处于全局模拟模式(工具栏开关):没有物理链路,无需逐台连接检查,所有设备视为就绪。" +
                    "切换到实际模式后,我再做真实连接实测。");

            string name = AgentToolContext.GetString(args, "device_name");
            var targets = new List<(string Display, IDevice Device)>();
            var skipped = new List<string>();

            if (!string.IsNullOrWhiteSpace(name))
            {
                var (vm, display, err) = _ctx.FindDevice(name);
                if (vm == null) return AgentToolResult.Fail(err);
                if (!IsConnectable(vm.Device))
                    return AgentToolResult.Ok($"「{display}」是展示型设备(无通讯配置),不需要连接。");
                targets.Add((display, vm.Device));
            }
            else
            {
                foreach (var d in _ctx.GetCanvasDevices())
                {
                    string display = _ctx.GetDisplayName(d.DeviceId);
                    if (IsConnectable(d.Device))
                        targets.Add((display, d.Device));
                    else
                        skipped.Add(display);
                }
            }

            if (targets.Count == 0)
                return AgentToolResult.Ok(skipped.Count > 0
                    ? "画布上只有展示型设备(" + string.Join("、", skipped) + "),无需连接。"
                    : "画布上没有设备,无可连接对象。");

            var sb = new StringBuilder();
            int okCount = 0;
            int simCount = 0;
            foreach (var (display, dev) in targets)
            {
                try
                {
                    // 模拟模式没有物理链路,连接判断无意义:直接视为就绪
                    if (dev.IsSimulationMode)
                    {
                        okCount++;
                        simCount++;
                        sb.AppendLine($"- {display}:模拟模式,无需连接(就绪)");
                        continue;
                    }
                    if (dev.IsConnected)
                    {
                        okCount++;
                        sb.AppendLine($"- {display}:已是连接状态");
                        continue;
                    }
                    bool ok = await dev.ConnectAsync().ConfigureAwait(false);
                    if (ok) okCount++;
                    sb.AppendLine($"- {display}:{(ok ? "连接成功" : "连接失败(设备未应答,检查线路/地址/电源)")}");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"- {display}:连接异常 {ex.Message}");
                }
            }

            if (simCount == targets.Count)
                return AgentToolResult.Ok(
                    $"全部 {targets.Count} 台设备均为模拟模式,无需连接检查,均已就绪。" +
                    (skipped.Count > 0 ? $"(另有展示型设备已跳过:{string.Join("、", skipped)})" : ""));

            string skipNote = skipped.Count > 0
                ? $"已跳过 {skipped.Count} 台展示型设备:{string.Join("、", skipped)}。\n"
                : "";
            return AgentToolResult.Ok($"连接实测完成:{okCount}/{targets.Count} 台可通讯设备在线。\n{sb}{skipNote}");
        }
    }

    /// <summary>读取单台设备的全部参数与可用命令(只读)。</summary>
    public sealed class GetDeviceStatusTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public GetDeviceStatusTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "get_device_status";
        public string DisplayName => "查看设备状态";
        public string Description => "读取指定设备的实时参数值、连接状态、以及它支持的命令清单(含每个命令的输入参数名)。设参数/执行命令前应先用它确认参数名和命令名。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""device_name"":{""type"":""string"",""description"":""设备显示名,如 泵#1、高低温#1;不确定时先调用 list_devices""}
  },
  ""required"":[""device_name""]
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "查看设备状态";

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string name = AgentToolContext.GetString(args, "device_name");
            var (vm, display, err) = _ctx.FindDevice(name);
            if (vm == null) return Task.FromResult(AgentToolResult.Fail(err));

            var dev = vm.Device;
            var sb = new StringBuilder();
            sb.AppendLine($"设备:{display}(类型 {dev.Name},{(dev.IsConnected ? "已连接" : "未连接")}{(dev.IsSimulationMode ? ",模拟模式" : "")})");
            sb.AppendLine("参数:" + AgentToolContext.DumpParameters(dev));

            try
            {
                var cmds = dev.Commands;
                if (cmds != null && cmds.Count > 0)
                {
                    sb.AppendLine("可用命令:");
                    foreach (var c in cmds.Take(30))
                    {
                        string inputs = "";
                        try
                        {
                            inputs = string.Join(",", c.InputParameters.Variables.Select(p => p.Name));
                        }
                        catch { }
                        sb.AppendLine($"- {c.Name}" + (inputs.Length > 0 ? $"(输入:{inputs})" : ""));
                    }
                }
            }
            catch { }

            return Task.FromResult(AgentToolResult.Ok(sb.ToString()));
        }
    }

    /// <summary>设置设备参数(写操作,需确认)。</summary>
    public sealed class SetDeviceParameterTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public SetDeviceParameterTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "set_device_parameter";
        public string DisplayName => "设置设备参数";
        public string Description => "把指定设备的某个参数设置为给定值(写入设备参数表)。参数名必须与 get_device_status 返回的完全一致。若要让硬件立即动作,通常还需再调用 execute_device_command 执行对应命令。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""device_name"":{""type"":""string"",""description"":""设备显示名""},
    ""parameter_name"":{""type"":""string"",""description"":""参数名,须与设备参数表完全一致""},
    ""value"":{""description"":""目标值,数字或字符串""}
  },
  ""required"":[""device_name"",""parameter_name"",""value""]
}";
        public bool RequiresConfirmation => true;

        public string DescribeAction(JsonElement args)
        {
            string d = AgentToolContext.GetString(args, "device_name");
            string p = AgentToolContext.GetString(args, "parameter_name");
            string v = args.TryGetProperty("value", out var ve) ? ve.ToString() : "?";
            return $"将设备「{d}」的参数 {p} 设为 {v}";
        }

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string name = AgentToolContext.GetString(args, "device_name");
            string paramName = AgentToolContext.GetString(args, "parameter_name");
            if (!args.TryGetProperty("value", out var valueEl))
                return Task.FromResult(AgentToolResult.Fail("缺少 value"));

            var (vm, display, err) = _ctx.FindDevice(name);
            if (vm == null) return Task.FromResult(AgentToolResult.Fail(err));

            var param = vm.Device.Parameters.Variables.FirstOrDefault(p => p.Name == paramName);
            if (param == null)
                return Task.FromResult(AgentToolResult.Fail(
                    $"设备「{display}」没有参数 {paramName}。可用参数:" +
                    string.Join(",", vm.Device.Parameters.Variables.Select(p => p.Name))));

            object value = AgentToolContext.JsonToClr(valueEl);
            try
            {
                AgentToolContext.RunOnUi(() => param.Value = value);
                return Task.FromResult(AgentToolResult.Ok(
                    $"已将 {display} 的 {paramName} 设为 {value}(当前读回:{param.Value})"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(AgentToolResult.Fail($"设置失败:{ex.Message}"));
            }
        }
    }

    /// <summary>执行设备命令(写操作,需确认)。</summary>
    public sealed class ExecuteDeviceCommandTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public ExecuteDeviceCommandTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "execute_device_command";
        public string DisplayName => "执行设备命令";
        public string Description => "执行设备的某个命令(如 启动、停止、设置温度、设置泵速),真正驱动硬件动作。命令名与输入参数名必须与 get_device_status 返回的一致;parameters 里只填该命令需要覆盖的输入参数。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""device_name"":{""type"":""string"",""description"":""设备显示名""},
    ""command_name"":{""type"":""string"",""description"":""命令名,须与设备命令清单一致""},
    ""parameters"":{""type"":""object"",""description"":""命令输入参数,键为参数名。可为空对象""}
  },
  ""required"":[""device_name"",""command_name""]
}";
        public bool RequiresConfirmation => true;

        public string DescribeAction(JsonElement args)
        {
            string d = AgentToolContext.GetString(args, "device_name");
            string c = AgentToolContext.GetString(args, "command_name");
            string extra = "";
            if (args.TryGetProperty("parameters", out var ps) && ps.ValueKind == JsonValueKind.Object)
            {
                var parts = ps.EnumerateObject().Select(p => $"{p.Name}={p.Value}").ToList();
                if (parts.Count > 0) extra = "(" + string.Join(", ", parts) + ")";
            }
            return $"在设备「{d}」上执行命令「{c}」{extra}";
        }

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string name = AgentToolContext.GetString(args, "device_name");
            string cmdName = AgentToolContext.GetString(args, "command_name");

            var (vm, display, err) = _ctx.FindDevice(name);
            if (vm == null) return AgentToolResult.Fail(err);

            var cmd = vm.Device.Commands?.FirstOrDefault(c => c.Name == cmdName);
            if (cmd == null)
                return AgentToolResult.Fail(
                    $"设备「{display}」没有命令 {cmdName}。可用命令:" +
                    string.Join(",", vm.Device.Commands?.Select(c => c.Name) ?? Array.Empty<string>()));

            // 覆盖输入参数(仅允许已存在的参数名,避免模型臆造)
            if (args.TryGetProperty("parameters", out var ps) && ps.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in ps.EnumerateObject())
                {
                    var target = cmd.InputParameters.Variables.FirstOrDefault(x => x.Name == p.Name);
                    if (target == null)
                        return AgentToolResult.Fail(
                            $"命令 {cmdName} 没有输入参数 {p.Name}。有效输入:" +
                            string.Join(",", cmd.InputParameters.Variables.Select(x => x.Name)));
                    object v = AgentToolContext.JsonToClr(p.Value);
                    AgentToolContext.RunOnUi(() => target.Value = v);
                }
            }

            try
            {
                DeviceCommandResult r = await cmd.ExecuteAsync(cmd.InputParameters).ConfigureAwait(false);
                if (r != null && !r.Success)
                    return AgentToolResult.Fail($"命令执行失败:{r.ErrorMessage ?? "设备未返回原因"}");

                var outputs = new List<string>();
                try
                {
                    if (r?.OutputParameters != null)
                        outputs = r.OutputParameters.Variables
                            .Select(p => $"{p.Name}={p.Value}").Take(10).ToList();
                }
                catch { }

                return AgentToolResult.Ok(
                    $"已在 {display} 上执行「{cmdName}」成功" +
                    (outputs.Count > 0 ? ",返回:" + string.Join(", ", outputs) : ""));
            }
            catch (Exception ex)
            {
                return AgentToolResult.Fail($"命令执行异常:{ex.Message}");
            }
        }
    }
}
