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
    public class FlowParameterAdapter : IFlowParameterProvider
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
                    _logger.LogWarning("未获取到流程节点，无法提取因子候选列表");
                    return candidates;
                }

                CollectFactorCandidatesFromNodes(flowVm.CommandNodes, candidates);
                _logger.LogInformation("共找到 {Count} 个因子候选项", candidates.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取因子候选列表失败");
            }

            return candidates;
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

        #region 私有方法

        private void CollectFactorCandidatesFromNodes(IEnumerable<CommandNode> nodes, List<FactorCandidate> candidates)
        {
            foreach (var node in nodes)
            {
                // 设备命令节点的输入参数
                if (node.DeviceCommand?.InputParameters?.Variables != null)
                {
                    foreach (var param in node.DeviceCommand.InputParameters.Variables)
                    {
                        if (IsNumericValue(param.Value))
                        {
                            var overrideValue = node.ParameterOverrides != null
                                && node.ParameterOverrides.TryGetValue(param.Name, out var ov)
                                ? ov : param.Value;

                            candidates.Add(new FactorCandidate
                            {
                                ParameterId = $"{node.NodeId}.{param.Name}",
                                DisplayName = $"{node.DisplayName} → {param.Name}",
                                CurrentValue = overrideValue,
                                SourceType = FactorSourceType.ParameterOverride,
                                NodeId = node.NodeId,
                                NodeDisplayName = node.DisplayName,
                                ParameterName = param.Name,
                                DataType = "Double"
                            });
                        }
                    }
                }

                // 逻辑命令节点的数值属性
                if (node.LogicCommand?.Properties != null)
                {
                    var numericProps = new[] { "LoopCount", "WaitTime", "Interval" };
                    foreach (var propName in numericProps)
                    {
                        if (node.LogicCommand.Properties.TryGetValue(propName, out var propValue)
                            && IsNumericValue(propValue))
                        {
                            candidates.Add(new FactorCandidate
                            {
                                ParameterId = $"{node.NodeId}.{propName}",
                                DisplayName = $"{node.DisplayName} → {propName}",
                                CurrentValue = propValue,
                                SourceType = FactorSourceType.LogicParam,
                                NodeId = node.NodeId,
                                NodeDisplayName = node.DisplayName,
                                ParameterName = propName,
                                DataType = "Double"
                            });
                        }
                    }
                }

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
