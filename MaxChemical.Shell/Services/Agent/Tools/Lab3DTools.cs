using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using MaxChemical.Modules.Lab3D.Models;
using MaxChemical.Modules.Lab3D.Services;

namespace MaxChemical.Shell.Services.Agent.Tools
{
    /// <summary>打开或激活3D实验室窗口,不操作设备。</summary>
    public sealed class OpenLab3DTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public OpenLab3DTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "open_lab3d";
        public string DisplayName => "打开3D实验室";
        public string Description => "打开或激活3D实验室独立窗口。用户说打开/进入3D实验室、三维实验室时使用。只操作界面,不连接或控制设备;窗口显示后Three.js场景可能仍在加载。";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "打开3D实验室";

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            try
            {
                bool wasOpen = false;
                AgentToolContext.RunOnUi(() =>
                {
                    var service = _ctx.Resolve<ILab3DWindowService>();
                    wasOpen = service.IsOpen;
                    service.ShowLab3D(Application.Current?.MainWindow);
                });
                return Task.FromResult(AgentToolResult.Ok(wasOpen
                    ? "3D实验室窗口已激活。"
                    : "3D实验室窗口已打开,三维场景正在加载。"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(AgentToolResult.Fail("打开3D实验室失败:" + ex.Message));
            }
        }
    }

    /// <summary>最大化3D窗口并收起右侧详情栏,不重新加载场景。</summary>
    public sealed class ShowLab3DFullscreenTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public ShowLab3DFullscreenTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "fullscreen_lab3d";
        public string DisplayName => "全屏展示3D实验室";
        public string Description => "打开或激活3D实验室,将窗口最大化并收起右侧设备详情栏。用户说全屏播放、全屏展示、全屏显示或最大化显示3D实验室/三维实验室时使用。只改变窗口布局,不重载场景、不操作设备。";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "全屏展示3D实验室";

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            try
            {
                AgentToolContext.RunOnUi(() => _ctx.Resolve<ILab3DWindowService>()
                    .ShowLab3DFullScreen(Application.Current?.MainWindow));
                return Task.FromResult(AgentToolResult.Ok("3D实验室已最大化显示,右侧设备详情栏已收起。"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(AgentToolResult.Fail("全屏展示3D实验室失败:" + ex.Message));
            }
        }
    }

    /// <summary>关闭3D实验室窗口,不影响设备采集。</summary>
    public sealed class CloseLab3DTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public CloseLab3DTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "close_lab3d";
        public string DisplayName => "关闭3D实验室";
        public string Description => "关闭3D实验室独立窗口。用户说关闭/退出3D实验室、三维实验室时使用。只关闭界面,不停止设备、不停止数据采集;未打开时幂等返回。";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "关闭3D实验室";

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            try
            {
                bool closed = false;
                AgentToolContext.RunOnUi(() => closed = _ctx.Resolve<ILab3DWindowService>().CloseLab3D());
                return Task.FromResult(AgentToolResult.Ok(closed
                    ? "3D实验室窗口已关闭,设备采集不受影响。"
                    : "3D实验室当前未打开。"));
            }
            catch (Exception ex)
            {
                return Task.FromResult(AgentToolResult.Fail("关闭3D实验室失败:" + ex.Message));
            }
        }
    }

    /// <summary>查询3D页面全部设备的绑定、连接和页面参数。</summary>
    public sealed class ListLab3DDevicesTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public ListLab3DDevicesTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "list_lab3d_devices";
        public string DisplayName => "查看3D设备状态";
        public string Description => "查询3D实验室页面实际显示的全部展台,包括内置与自定义设备,返回绑定、连接、数据新鲜状态和页面主次参数。只读缓存,页面关闭也可查询,不主动连接或执行设备命令。";
        public string ParametersSchema => @"{""type"":""object"",""properties"":{}}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "查看3D实验室全部设备";

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            try
            {
                var devices = _ctx.Resolve<ILab3DDeviceQueryService>().GetDevices();
                if (devices.Count == 0)
                    return Task.FromResult(AgentToolResult.Ok("3D实验室当前没有展示设备。"));

                int fresh = devices.Count(d => d.IsConnected && d.HasFreshData);
                int waiting = devices.Count(d => d.IsConnected && !d.HasFreshData);
                int disconnected = devices.Count(d => !d.IsCustom && d.SourceDeviceId != null && !d.IsConnected);
                int unbound = devices.Count(d => !d.IsCustom && d.SourceDeviceId == null);
                int local = devices.Count(d => d.IsCustom && d.SourceDeviceId == null);

                var sb = new StringBuilder();
                sb.AppendLine($"3D实验室共{devices.Count}台设备,{fresh}台数据正常,{waiting}台等待数据,{disconnected}台未连接,{unbound}台未绑定,{local}台本地展示。");
                sb.AppendLine("| 展台 | 设备 | 状态 | 主参数 | 次参数 | 最后数据时间 |");
                sb.AppendLine("|---|---|---|---|---|---|");
                foreach (var device in devices)
                {
                    sb.AppendLine($"| {device.SlotId} | {device.Name} | {Lab3DToolHelper.StatusText(device)} | " +
                        $"{Lab3DToolHelper.ParameterText(device.Primary)} | {Lab3DToolHelper.ParameterText(device.Secondary)} | " +
                        $"{Lab3DToolHelper.TimeText(device.LastUpdateTime)} |");
                }
                sb.Append("以上为3D页面实时缓存快照;未连接、未绑定和数据过期均不代表数值为0。需要单台全部参数时使用 get_lab3d_device_status。");
                return Task.FromResult(AgentToolResult.Ok(sb.ToString()));
            }
            catch (Exception ex)
            {
                return Task.FromResult(AgentToolResult.Fail("查询3D设备失败:" + ex.Message));
            }
        }
    }

    /// <summary>查询3D单台设备的详细状态与全部实时样本。</summary>
    public sealed class GetLab3DDeviceStatusTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public GetLab3DDeviceStatusTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "get_lab3d_device_status";
        public string DisplayName => "查看3D单台设备";
        public string Description => "按展台编号(如D03)或页面设备名查询3D实验室单台设备,返回绑定、连接、协议、数据新鲜状态、页面参数和全部已缓存实时输出;parameter_name可只查指定参数。只读,不主动连接或执行设备命令。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""device_name"":{""type"":""string"",""description"":""展台编号或页面设备名称,如D03、智能磁力搅拌器""},
    ""parameter_name"":{""type"":""string"",""description"":""可选,参数名称,如SpeedMeasured、速度测量值、物料温度""}
  },
  ""required"":[""device_name""]
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "查看3D设备详细状态";

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string name = AgentToolContext.GetString(args, "device_name")?.Trim();
            string parameterName = AgentToolContext.GetString(args, "parameter_name")?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return Task.FromResult(AgentToolResult.Fail("device_name必填:请提供展台编号或设备名称。"));

            try
            {
                var devices = _ctx.Resolve<ILab3DDeviceQueryService>().GetDevices();
                var exact = devices.Where(d =>
                    string.Equals(d.SlotId, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                var matches = exact.Count > 0 ? exact : devices.Where(d =>
                    d.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                    name.Contains(d.Name, StringComparison.OrdinalIgnoreCase)).ToList();

                if (matches.Count == 0)
                    return Task.FromResult(AgentToolResult.Fail($"未找到3D设备「{name}」。可用设备:" +
                        string.Join("、", devices.Select(d => $"{d.SlotId}{d.Name}"))));
                if (matches.Count > 1)
                    return Task.FromResult(AgentToolResult.Fail($"匹配到多台3D设备,请指明展台编号:" +
                        string.Join("、", matches.Select(d => $"{d.SlotId}{d.Name}"))));

                var device = matches[0];
                var samples = device.Samples;
                if (!string.IsNullOrWhiteSpace(parameterName))
                {
                    bool pageParameter = device.Primary.Name.Contains(parameterName, StringComparison.OrdinalIgnoreCase) ||
                                         device.Secondary.Name.Contains(parameterName, StringComparison.OrdinalIgnoreCase);
                    samples = samples.Where(s => s.Name.Contains(parameterName, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (!pageParameter && samples.Count == 0)
                        return Task.FromResult(AgentToolResult.Fail(
                            $"{device.SlotId}{device.Name}没有实时参数「{parameterName}」。已缓存参数:" +
                            (device.Samples.Count == 0 ? "暂无" : string.Join("、", device.Samples.Select(s => s.Name)))));
                }

                var sb = new StringBuilder();
                sb.AppendLine($"{device.SlotId}{device.Name}:{Lab3DToolHelper.StatusText(device)};" +
                    $"{device.Primary.Name}{Lab3DToolHelper.ParameterText(device.Primary)}," +
                    $"{device.Secondary.Name}{Lab3DToolHelper.ParameterText(device.Secondary)}。");
                sb.AppendLine($"协议:{device.Protocol};绑定设备ID:{device.SourceDeviceId ?? "未绑定"};" +
                    $"最后数据时间:{Lab3DToolHelper.TimeText(device.LastUpdateTime)}。");
                if (device.IsCustom && device.SourceDeviceId is null)
                {
                    sb.Append("这是自定义本地展示设备,尚未绑定主程序设备,无实时参数样本。");
                    return Task.FromResult(AgentToolResult.Ok(sb.ToString()));
                }

                if (!string.IsNullOrWhiteSpace(parameterName) && samples.Count == 0)
                {
                    sb.Append("该页面参数暂无新鲜实时样本,不能提供当前实测值。");
                    return Task.FromResult(AgentToolResult.Ok(sb.ToString()));
                }

                sb.AppendLine($"实时参数样本({samples.Count}/{device.Samples.Count}):");
                if (samples.Count == 0)
                {
                    sb.Append("暂无实时样本;空值不代表0。");
                }
                else
                {
                    foreach (var sample in samples.Take(40))
                    {
                        string unit = string.IsNullOrWhiteSpace(sample.Unit) ? "" : " " + sample.Unit;
                        sb.AppendLine($"- {sample.Name}: {sample.Value}{unit};" +
                            $"{(sample.IsFresh ? "新鲜" : "已过期")};时间 {sample.Timestamp:HH:mm:ss}");
                    }
                    if (samples.Count > 40) sb.AppendLine($"其余 {samples.Count - 40} 项省略,请指定 parameter_name 查询。");
                }
                return Task.FromResult(AgentToolResult.Ok(sb.ToString()));
            }
            catch (Exception ex)
            {
                return Task.FromResult(AgentToolResult.Fail("查询3D设备详情失败:" + ex.Message));
            }
        }
    }

    internal static class Lab3DToolHelper
    {
        internal static string StatusText(Lab3DDeviceQueryResult device)
        {
            if (device.SourceDeviceId == null) return device.IsCustom ? "本地展示·未绑定" : "未绑定";
            if (!device.IsConnected) return "未连接";
            return device.HasFreshData ? "已连接·数据正常" : "已连接·等待数据";
        }

        internal static string ParameterText(Lab3DParameterSnapshot parameter)
        {
            if (string.IsNullOrWhiteSpace(parameter.Value)) return string.Empty;
            string unit = string.IsNullOrWhiteSpace(parameter.Unit) ? "" : " " + parameter.Unit;
            return $"{parameter.Value}{unit}";
        }

        internal static string TimeText(DateTime? timestamp)
            => timestamp.HasValue ? timestamp.Value.ToString("yyyy-MM-dd HH:mm:ss") : "暂无";
    }
}
