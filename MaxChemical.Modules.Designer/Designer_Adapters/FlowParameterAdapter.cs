using MaxChemical.Infrastructure.DOE;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using Prism.Events;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MaxChemical.Modules.Designer.Service
{
    /// <summary>
    /// 流程参数适配器 — 从 Designer 的流程中提取可作为 DOE 因子的参数列表，
    /// 并在每组实验前向流程注入参数值。
    /// 复用 FlowDesignerViewModel 已订阅的 GetCurrentFlowDesignerRequestEvent。
    /// </summary>
    public class FlowParameterAdapter : IFlowParameterProvider, IFlowWaitNodeProvider
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly ILogService _logger;

        public FlowParameterAdapter(IEventAggregator eventAggregator, ILogService logger)
        {
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _logger = logger?.ForContext<FlowParameterAdapter>() ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 通过已有事件获取 FlowDesignerViewModel
        /// </summary>
        private FlowDesignerViewModel? GetFlowViewModel()
        {
            FlowDesignerViewModel? vm = null;
            try
            {
                _eventAggregator.GetEvent<GetCurrentFlowDesignerRequestEvent>()?.Publish(v => vm = v);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取 FlowDesignerViewModel 失败");
            }
            return vm;
        }

        public string? GetCurrentFlowId()
        {
            var vm = GetFlowViewModel();
            return vm?.GetHashCode().ToString("X8");
        }

        public string? GetCurrentFlowName()
        {
            var vm = GetFlowViewModel();
            return vm != null ? "当前流程" : null;
        }

        /// <summary>
        /// 获取当前流程中所有可作为 DOE 因子的参数候选列表
        /// </summary>
        public List<FactorCandidate> GetFactorCandidates()
        {
            var candidates = new List<FactorCandidate>();

            try
            {
                var flowVm = GetFlowViewModel();
                if (flowVm?.CommandNodes == null)
                {
                    _logger.LogWarning("未获取到流程节点,无法提取因子候选列表");
                    return candidates;
                }

                CollectFactorCandidatesFromNodes(flowVm.CommandNodes, candidates);

                // ★ 新增: 同名候选项消歧
                // 场景: 流程里有多台同型号设备(都叫"泵"),它们的 speed 参数会产生
                //       多个 DisplayName 完全相同的 candidate ("泵 → speed")。
                //       NodeId 不同,注入正确,但用户在下拉框里选错。
                DisambiguateDisplayNames(candidates);

                _logger.LogInformation("共找到 {Count} 个因子候选项", candidates.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取因子候选列表失败");
            }

            return candidates;
        }

       
        /// <summary>
        /// ★ 新增: 检测 DisplayName 重复的候选项,在设备名后追加序号区分。
        /// 例:
        ///   "泵 → speed" + "泵 → speed"
        ///   → "泵#1 → speed" + "泵#2 → speed"
        ///
        /// 同名分组内按收集顺序编号(1, 2, 3...)。
        /// 仅修改 DisplayName,不影响 ParameterId/NodeId/ParameterName 等用于注入的字段。
        /// </summary>
        private void DisambiguateDisplayNames(List<FactorCandidate> candidates)
        {
            var groups = candidates.GroupBy(c => c.DisplayName).ToList();
            foreach (var group in groups)
            {
                var list = group.ToList();
                if (list.Count <= 1) continue;  // 没有重名,跳过

                _logger.LogInformation(
                    "检测到 {Count} 个同名候选项 \"{Name}\",自动追加序号区分",
                    list.Count, group.Key);

                for (int i = 0; i < list.Count; i++)
                {
                    var c = list[i];
                    // 拆分 "泵 → speed" → ["泵", "speed"]
                    var parts = c.DisplayName.Split(new[] { " → " }, 2, StringSplitOptions.None);
                    if (parts.Length == 2)
                    {
                        c.DisplayName = $"{parts[0]}#{i + 1} → {parts[1]}";
                    }
                    else
                    {
                        // 兜底: 没有 " → " 分隔符就直接尾部加序号
                        c.DisplayName = $"{c.DisplayName} #{i + 1}";
                    }
                }
            }
        }

        /// <summary>
        /// 向当前流程注入一组参数值
        /// </summary>
        public bool InjectParameters(Dictionary<string, object> parameterValues)
        {
            try
            {
                var flowVm = GetFlowViewModel();
                if (flowVm?.CommandNodes == null)
                {
                    _logger.LogError("未获取到流程节点，无法注入参数");
                    return false;
                }

                int injected = 0;
                foreach (var kvp in parameterValues)
                {
                    var parameterId = kvp.Key;
                    var value = kvp.Value;

                    if (parameterId.Contains('.'))
                    {
                        // 格式: {NodeId}.{ParamName}
                        var parts = parameterId.Split('.', 2);
                        var nodeId = parts[0];
                        var paramName = parts[1];
                        var node = FindNodeById(flowVm.CommandNodes, nodeId);

                        if (node == null)
                        {
                            _logger.LogWarning("未找到节点 {NodeId}，跳过参数 {ParamName}", nodeId, paramName);
                            continue;
                        }

                        if (node.DeviceCommand != null)
                        {
                            var overrides = node.ParameterOverrides != null
                                ? new Dictionary<string, string>(node.ParameterOverrides)
                                : new Dictionary<string, string>();
                            overrides[paramName] = value?.ToString() ?? "";
                            node.ApplyParameterOverrides(overrides);
                            injected++;
                            _logger.LogDebug("注入参数: {NodeName}.{ParamName} = {Value}", node.DisplayName, paramName, value);
                        }
                        else if (node.LogicCommand?.Properties != null)
                        {
                            node.LogicCommand.SetProperty(paramName, value);
                            injected++;
                            _logger.LogDebug("注入逻辑参数: {NodeName}.{ParamName} = {Value}", node.DisplayName, paramName, value);
                        }
                    }
                }

                _logger.LogInformation("成功注入 {Count}/{Total} 个参数", injected, parameterValues.Count);
                return injected > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "注入参数失败");
                return false;
            }
        }

        /// <summary>列出当前流程里的固定时长 Wait 节点(排除条件等待),供动态稳态等待挑选保温节点。</summary>
        public List<FlowWaitNodeInfo> GetFixedWaitNodes()
        {
            var result = new List<FlowWaitNodeInfo>();
            try
            {
                var flowVm = GetFlowViewModel();
                if (flowVm?.CommandNodes == null) return result;
                CollectFixedWaitNodes(flowVm.CommandNodes, result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "枚举等待节点失败");
            }
            return result;
        }

        private void CollectFixedWaitNodes(IEnumerable<CommandNode> nodes, List<FlowWaitNodeInfo> acc)
        {
            foreach (var node in nodes)
            {
                var lc = node.LogicCommand;
                if (lc != null && lc.CommandType == LogicCommandType.Wait)
                {
                    // 条件等待(ConditionLeftVariable 非空)不是固定时长,动态等待驱动不了它,排除
                    string condVar = lc.GetProperty<string>("ConditionLeftVariable", "") ?? "";
                    if (string.IsNullOrWhiteSpace(condVar))
                    {
                        double waitTime = 5;
                        try { waitTime = Convert.ToDouble(lc.GetProperty<object>("WaitTime", 5.0) ?? 5.0); } catch { }
                        acc.Add(new FlowWaitNodeInfo
                        {
                            NodeId = node.NodeId,
                            DisplayName = string.IsNullOrEmpty(node.DisplayName) ? node.NodeId : node.DisplayName,
                            CurrentWaitTime = waitTime,
                            TimeUnit = lc.GetProperty<object>("TimeUnit", "Seconds")?.ToString() ?? "Seconds",
                            WaitForStability = string.Equals(lc.GetProperty<object>("WaitForStability", false)?.ToString(),
                                "True", StringComparison.OrdinalIgnoreCase),
                            IsResidenceTimeWait = string.Equals(lc.GetProperty<object>("IsResidenceTimeWait", false)?.ToString(),
                                "True", StringComparison.OrdinalIgnoreCase)
                        });
                    }
                }
                if (node.HasChildren)
                    CollectFixedWaitNodes(node.Children, acc);
            }
        }

        #region 私有方法

        private void CollectFactorCandidatesFromNodes(IEnumerable<CommandNode> nodes, List<FactorCandidate> candidates)
        {
            foreach (var node in nodes)
            {
                // 设备命令节点的输入参数
                if (node.DeviceCommand?.InputParameters?.Variables != null)
                {
                    // ★ 修改: 拼"设备身份名"而不是命令名
                    //   优先用 BoundDeviceName(画布上绑定的具体设备,如"反应釜A"),
                    //   未绑定则 fallback 到命令名(操作类型)。
                    //   逻辑与 FlowDesignerViewModel.SyncSwimlaneRows 保持一致。
                    string deviceLabel = !string.IsNullOrEmpty(node.BoundDeviceName)
                        ? node.BoundDeviceName
                        : node.DisplayName;

                    foreach (var param in node.DeviceCommand.InputParameters.Variables)
                    {
                        // ★ 因子候选只收数值量程型参数(NumberParameter)且非只读:
                        //   枚举/布尔/文本参数(如电子秤的称量单位 Unit)底层值即使是数字索引,
                        //   也不是可连续扫描的工艺量,不能作 DOE 因子。
                        if (param is not DevicePlugins.Devices.NumberParameter || param.IsReadOnly)
                            continue;

                        if (IsNumericValue(param.Value))
                        {
                            var overrideValue = node.ParameterOverrides != null
                                && node.ParameterOverrides.TryGetValue(param.Name, out var ov)
                                ? ov : param.Value;

                            candidates.Add(new FactorCandidate
                            {
                                ParameterId = $"{node.NodeId}.{param.Name}",
                                DisplayName = $"{deviceLabel} → {param.Name}",   // ★ 用设备身份
                                CurrentValue = overrideValue,
                                SourceType = FactorSourceType.ParameterOverride,
                                NodeId = node.NodeId,
                                NodeDisplayName = deviceLabel,                   // ★ 同步更新
                                ParameterName = param.Name,
                                DataType = "Double"
                            });
                        }
                    }
                }

                // ★ 逻辑指令参数(LoopCount/WaitTime/Interval)不再作为因子候选:
                //   它们是流程控制量而非工艺量,混进 DOE 绑定下拉与 AI 因子分析会误导实验设计。
                //   如确需把"保温时间"作因子,应通过设备命令参数或自定义变量表达。

                // 递归子节点
                if (node.HasChildren)
                    CollectFactorCandidatesFromNodes(node.Children, candidates);
            }
        }

        private static CommandNode? FindNodeById(IEnumerable<CommandNode> nodes, string nodeId)
        {
            foreach (var node in nodes)
            {
                if (node.NodeId == nodeId) return node;
                if (node.HasChildren)
                {
                    var found = FindNodeById(node.Children, nodeId);
                    if (found != null) return found;
                }
            }
            return null;
        }

        private static bool IsNumericValue(object? value)
        {
            if (value == null) return false;
            return value is int or double or float or decimal or long or short
                   || (value is string s && double.TryParse(s, out _));
        }

        #endregion
    }
}
