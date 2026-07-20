using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using DevicePlugins.Devices;
using MaxChemical.Modules.Designer.Service;
using MaxChemical.Modules.Designer.ViewModels;
using Prism.Events;
using Prism.Ioc;
using CanvasRequestEvent = MaxChemical.Modules.Designer.Event.RequestCanvasDevicesEvent;

namespace MaxChemical.Shell.Services.Agent.Tools
{
    /// <summary>
    /// 工具共享上下文:容器懒解析 + 画布设备访问 + 设备名解析。
    /// 服务一律在使用时解析(模块注册的服务在启动早期可能尚未就绪)。
    /// </summary>
    public sealed class AgentToolContext
    {
        private readonly IContainerProvider _container;

        public AgentToolContext(IContainerProvider container)
        {
            _container = container;
        }

        public T Resolve<T>() => _container.Resolve<T>();

        public IEventAggregator Events => _container.Resolve<IEventAggregator>();

        /// <summary>取当前画布上的设备列表(请求-回调事件,必须在 UI 线程发布)。</summary>
        public List<CanvasDeviceViewModel> GetCanvasDevices()
        {
            List<CanvasDeviceViewModel> result = null;
            RunOnUi(() =>
            {
                Events.GetEvent<CanvasRequestEvent>()
                      .Publish(devices => result = devices);
            });
            return result ?? new List<CanvasDeviceViewModel>();
        }

        /// <summary>画布设备的显示名(如 泵#1)。</summary>
        public string GetDisplayName(string deviceId)
        {
            try
            {
                string name = null;
                RunOnUi(() => name = _container.Resolve<IDeviceNameResolverService>()
                                               .GetDisplayNameByDeviceId(deviceId));
                return string.IsNullOrEmpty(name) ? deviceId : name;
            }
            catch { return deviceId; }
        }

        /// <summary>
        /// 按用户口中的名字找画布设备:先精确匹配显示名,再包含匹配(显示名/蓝图名)。
        /// 命中多台时返回 null 并给出候选,让模型追问用户。
        /// </summary>
        public (CanvasDeviceViewModel Device, string DisplayName, string Error) FindDevice(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return (null, null, "设备名为空");

            var devices = GetCanvasDevices();
            if (devices.Count == 0)
                return (null, null, "当前画布上没有任何设备");

            var named = devices
                .Select(d => (Vm: d, Display: GetDisplayName(d.DeviceId)))
                .ToList();

            var exact = named.Where(t =>
                string.Equals(t.Display, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return (exact[0].Vm, exact[0].Display, null);

            var fuzzy = named.Where(t =>
                (t.Display?.IndexOf(name, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                (t.Vm.Device?.Name?.IndexOf(name, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0).ToList();

            if (fuzzy.Count == 1) return (fuzzy[0].Vm, fuzzy[0].Display, null);
            if (fuzzy.Count > 1)
                return (null, null, "匹配到多台设备,请让用户指明:" +
                                    string.Join("、", fuzzy.Select(t => t.Display)));

            return (null, null, $"未找到设备「{name}」。当前画布设备:" +
                                string.Join("、", named.Select(t => t.Display)));
        }

        /// <summary>
        /// 查询全局模拟模式(主工具栏的开关是权威来源,请求-回调事件)。
        /// 无订阅方(极早期)时返回 null。
        /// </summary>
        public bool? GetGlobalSimulationMode()
        {
            bool? mode = null;
            try
            {
                RunOnUi(() => Events
                    .GetEvent<MaxChemical.Modules.Designer.Event.GetSimulationModeRequestEvent>()
                    .Publish(v => mode = v));
            }
            catch { }
            return mode;
        }

        /// <summary>执行前设备就绪检查的分类结论:全局模拟/离线/实机已连接。</summary>
        public sealed class DeviceReadiness
        {
            /// <summary>全局模拟模式:无物理链路,整体跳过连接检查。</summary>
            public bool GlobalSimulation { get; set; }

            public List<string> Offline { get; } = new();
            public List<string> Simulated { get; } = new();
            public List<string> Connected { get; } = new();

            public bool AllReady => GlobalSimulation || Offline.Count == 0;
            public string OfflineText => string.Join("、", Offline);
            public string SimText => string.Join("、", Simulated);

            /// <summary>面向用户的检查结论一句话。</summary>
            public string Summary()
            {
                if (GlobalSimulation)
                    return "设备检查:当前为全局模拟模式(无物理链路),已跳过全部设备连接检查。";

                var parts = new List<string>();
                if (Simulated.Count > 0)
                    parts.Add($"{Simulated.Count} 台为模拟模式,已跳过连接检查({SimText})");
                if (Connected.Count > 0)
                    parts.Add($"{Connected.Count} 台实机已连接");
                return parts.Count == 0 ? "" : "设备检查:" + string.Join(";", parts) + "。";
            }
        }

        /// <summary>
        /// 执行前设备就绪检查:先看全局模拟模式(工具栏开关)——模拟模式下无物理链路,
        /// 整体跳过逐台检查;实际模式下遍历画布上所有可通讯设备并分类,
        /// 未连接的记为离线。展示型设备(无通讯配置)不参与检查。
        /// </summary>
        public DeviceReadiness GetDeviceReadiness()
        {
            if (GetGlobalSimulationMode() == true)
                return new DeviceReadiness { GlobalSimulation = true };

            var r = new DeviceReadiness();
            foreach (var d in GetCanvasDevices())
            {
                try
                {
                    if (d.Device == null) continue;
                    if (!ConnectDevicesTool.IsConnectable(d.Device)) continue;
                    string display = GetDisplayName(d.DeviceId);
                    if (d.Device.IsSimulationMode) r.Simulated.Add(display);
                    else if (d.Device.IsConnected) r.Connected.Add(display);
                    else r.Offline.Add(display);
                }
                catch { }
            }
            return r;
        }

        /// <summary>兼容入口:全部就绪返回 null,否则返回离线设备名清单。</summary>
        public string CheckDevicesReady()
        {
            var r = GetDeviceReadiness();
            return r.AllReady ? null : r.OfflineText;
        }

        /// <summary>把 JSON 值转成参数可接受的 CLR 值。</summary>
        public static object JsonToClr(JsonElement el)
        {
            return el.ValueKind switch
            {
                JsonValueKind.Number => el.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Null => null,
                _ => el.GetRawText()
            };
        }

        public static string GetString(JsonElement args, string name)
            => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        /// <summary>读布尔参数:缺省返回 null;兼容字符串形式的 true/false。</summary>
        public static bool? GetBool(JsonElement args, string name)
        {
            if (!args.TryGetProperty(name, out var v)) return null;
            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(v.GetString(), out bool b) ? b : (bool?)null,
                _ => null
            };
        }

        public static double? GetNumber(JsonElement args, string name)
        {
            if (!args.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
            if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out double d)) return d;
            return null;
        }

        /// <summary>同步切到 UI 线程执行(Prism 事件订阅方大多在 UI 线程持有状态)。</summary>
        public static void RunOnUi(Action action)
        {
            var app = Application.Current;
            if (app?.Dispatcher == null || app.Dispatcher.CheckAccess()) action();
            else app.Dispatcher.Invoke(action);
        }

        /// <summary>
        /// 设备参数逐条转文字,带语义元数据(只读标志/单位/说明/量程),
        /// 让模型能分辨"设定参数"与"监控/显示参数",不至于把只读量当可调因子。
        /// </summary>
        public static string DumpParameters(IDevice device, int max = 40)
        {
            try
            {
                var lines = new List<string>();
                foreach (ParameterBase p in device.Parameters.Variables.Take(max))
                {
                    object v = null;
                    try { v = p.Value; } catch { }

                    var meta = new List<string>();
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(p.Unit)) meta.Add(p.Unit);
                        if (p.IsReadOnly) meta.Add("只读/监控");
                        if (!string.IsNullOrWhiteSpace(p.HelpText)) meta.Add(p.HelpText);
                        if (p is NumberParameter np &&
                            np.MinValue > double.MinValue && np.MaxValue < double.MaxValue)
                            meta.Add($"范围{np.MinValue}~{np.MaxValue}");
                    }
                    catch { }

                    lines.Add($"{p.Name}={v ?? "null"}" +
                              (meta.Count > 0 ? "(" + string.Join(",", meta) + ")" : ""));
                }
                return lines.Count == 0 ? "(无参数)" : string.Join("; ", lines);
            }
            catch (Exception ex)
            {
                return "(参数读取失败:" + ex.Message + ")";
            }
        }
    }
}
