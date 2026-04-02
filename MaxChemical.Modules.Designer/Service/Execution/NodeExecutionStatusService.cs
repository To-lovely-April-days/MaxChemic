using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using Prism.Events;
using static MaxChemical.Modules.Designer.Models.CommandNode;

namespace MaxChemical.Modules.Designer.Service.Execution
{
    public class NodeExecutionStatusService : INodeExecutionStatusService
    {
        private readonly ILogService _logger;
        private readonly IEventAggregator _eventAggregator;
        private readonly ConcurrentDictionary<string, NodeExecutionStatus> _nodeStatuses = new();
        private FlowDesignerViewModel _currentFlowViewModel;

        public event EventHandler<NodeExecutionStatusChangedEventArgs> NodeExecutionStatusChanged;

        public NodeExecutionStatusService(IEventAggregator eventAggregator)
        {
            _logger = _logger?.ForContext<NodeExecutionStatusService>() ?? new LogService().ForContext<NodeExecutionStatusService>();
            _eventAggregator = eventAggregator;
        }

        public void SetCurrentFlowViewModel(FlowDesignerViewModel flowViewModel)
        {
            _currentFlowViewModel = flowViewModel;
        }

        public FlowDesignerViewModel GetCurrentFlowViewModel()
        {
            return _currentFlowViewModel;
        }

        public void SetNodeExecutionStatus(string nodeId, NodeExecutionStatus status)
        {
            try
            {
                var oldStatus = _nodeStatuses.GetValueOrDefault(nodeId, NodeExecutionStatus.Idle);
                _nodeStatuses.AddOrUpdate(nodeId, status, (key, oldValue) => status);

                // 在UI线程中更新节点状态
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var node = FindNodeById(nodeId);
                    if (node != null)
                    {
                        node.ExecutionStatus = status;

                        // 触发状态变化事件
                        NodeExecutionStatusChanged?.Invoke(this, new NodeExecutionStatusChangedEventArgs
                        {
                            NodeId = nodeId,
                            OldStatus = oldStatus,
                            NewStatus = status,
                            Node = node
                        });
                    }
                }), System.Windows.Threading.DispatcherPriority.Normal);

                _logger.LogDebug("节点 {NodeId} 执行状态更新: {OldStatus} -> {NewStatus}", nodeId, oldStatus, status);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设置节点执行状态失败: {NodeId}", nodeId);
            }
        }

        public void StartNodeExecution(string nodeId)
        {
            try
            {
                var oldStatus = _nodeStatuses.GetValueOrDefault(nodeId, NodeExecutionStatus.Idle);
                _nodeStatuses.AddOrUpdate(nodeId, NodeExecutionStatus.Executing, (key, old) => NodeExecutionStatus.Executing);

                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var node = FindNodeById(nodeId);
                    if (node != null)
                    {
                        node.StartExecution();
                        node.ExecutionStatus = NodeExecutionStatus.Executing;

                        NodeExecutionStatusChanged?.Invoke(this, new NodeExecutionStatusChangedEventArgs
                        {
                            NodeId = nodeId,
                            OldStatus = oldStatus,
                            NewStatus = NodeExecutionStatus.Executing,
                            Node = node
                        });
                    }
                }), System.Windows.Threading.DispatcherPriority.Normal);

                _logger.LogDebug("开始执行节点: {NodeId}", nodeId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "开始节点执行失败: {NodeId}", nodeId);
            }
        }

        public void CompleteNodeExecution(string nodeId, bool isSuccessful = true, string errorMessage = null)
        {
            try
            {
                var status = isSuccessful ? NodeExecutionStatus.Completed : NodeExecutionStatus.Failed;
                var oldStatus = _nodeStatuses.GetValueOrDefault(nodeId, NodeExecutionStatus.Executing);
                _nodeStatuses.AddOrUpdate(nodeId, status, (key, old) => status);

                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var node = FindNodeById(nodeId);
                    if (node != null)
                    {
                        node.CompleteExecution(isSuccessful, errorMessage);
                        node.ExecutionStatus = status;

                        NodeExecutionStatusChanged?.Invoke(this, new NodeExecutionStatusChangedEventArgs
                        {
                            NodeId = nodeId,
                            OldStatus = oldStatus,
                            NewStatus = status,
                            Node = node
                        });
                    }
                }), System.Windows.Threading.DispatcherPriority.Normal);

                _logger.LogDebug("完成节点执行: {NodeId}, 成功: {IsSuccessful}", nodeId, isSuccessful);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "完成节点执行失败: {NodeId}", nodeId);
            }
        }
        public void FinalizeExecutionStatus()
        {
            try
            {
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_currentFlowViewModel?.CommandNodes != null)
                    {
                        foreach (var node in _currentFlowViewModel.CommandNodes)
                        {
                            FinalizeNodeAndChildren(node);
                        }
                    }
                }), System.Windows.Threading.DispatcherPriority.Normal);

                _logger.LogDebug("已最终化所有节点执行状态（保留 Completed/Failed/Skipped）");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "最终化节点执行状态失败");
            }
        }

        private void FinalizeNodeAndChildren(CommandNode node)
        {
            // 只重置还在执行中或等待中的节点，保留已有结果
            if (node.ExecutionStatus == NodeExecutionStatus.Executing ||
                node.ExecutionStatus == NodeExecutionStatus.Waiting)
            {
                node.ResetExecutionStatus();
                _nodeStatuses.TryRemove(node.NodeId, out _);
            }

            if (node.HasChildren)
            {
                foreach (var child in node.Children)
                {
                    FinalizeNodeAndChildren(child);
                }
            }
        }
        public void ResetAllExecutionStatus()
        {
            try
            {
                _nodeStatuses.Clear();

                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_currentFlowViewModel?.CommandNodes != null)
                    {
                        foreach (var node in _currentFlowViewModel.CommandNodes)
                        {
                            ResetNodeAndChildren(node);
                        }
                    }
                    //  新增：通知 ViewModel 流程已重置
                    _currentFlowViewModel?.ResetFlowStartTime();
                }), System.Windows.Threading.DispatcherPriority.Normal);

                _logger.LogDebug("已重置所有节点执行状态");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "重置所有节点执行状态失败");
            }
        }

        private void ResetNodeAndChildren(CommandNode node)
        {
            node.ResetExecutionStatus();

            if (node.HasChildren)
            {
                foreach (var child in node.Children)
                {
                    ResetNodeAndChildren(child);
                }
            }
        }

        public NodeExecutionStatus GetNodeExecutionStatus(string nodeId)
        {
            return _nodeStatuses.GetValueOrDefault(nodeId, NodeExecutionStatus.Idle);
        }

        private CommandNode FindNodeById(string nodeId)
        {
            if (_currentFlowViewModel?.CommandNodes == null)
                return null;

            return FindNodeByIdRecursive(_currentFlowViewModel.CommandNodes, nodeId);
        }

        private CommandNode FindNodeByIdRecursive(IEnumerable<CommandNode> nodes, string nodeId)
        {
            foreach (var node in nodes)
            {
                if (node.NodeId == nodeId)
                    return node;

                if (node.HasChildren)
                {
                    var found = FindNodeByIdRecursive(node.Children, nodeId);
                    if (found != null)
                        return found;
                }
            }
            return null;
        }
    }
}