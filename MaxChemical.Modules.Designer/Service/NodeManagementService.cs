// NodeManagementService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using MaxChemical.Logging;
using System.Windows.Controls;

namespace MaxChemical.Modules.Designer.Services
{
    public class NodeManagementService
    {
        private readonly ILogService _logger;

        public NodeManagementService(ILogService logger)
        {
            _logger = logger?.ForContext<NodeManagementService>() ?? throw new ArgumentNullException(nameof(logger));
        }

        public void HandleNodeClick(CommandNode clickedNode, FlowDesignerViewModel viewModel)
        {
            try
            {
                if (clickedNode == null || viewModel == null) return;

                _logger.LogDebug("处理节点点击: {NodeName}", clickedNode.DisplayName);

                // 清除所有选中状态
                ClearAllSelections(viewModel);

                // 选中点击的节点
                clickedNode.IsSelected = true;

                _logger.LogDebug("节点 {NodeName} 已被选中", clickedNode.DisplayName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理节点点击失败: {NodeName}", clickedNode?.DisplayName);
            }
        }

        public void HandleNodeDeleteRequest(CommandNode nodeToDelete, FlowDesignerViewModel viewModel)
        {
            try
            {
                if (nodeToDelete == null || viewModel == null) return;

                _logger.LogDebug("处理节点删除请求: {NodeName}", nodeToDelete.DisplayName);

                var confirmMessage = GenerateDeleteConfirmMessage(nodeToDelete);
                var result = MessageBox.Show(confirmMessage, "确认删除",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

                if (result == MessageBoxResult.Yes)
                {
                    // 直接调用 ViewModel 的删除方法
                    var removeMethod = typeof(FlowDesignerViewModel).GetMethod("RemoveCommandNode",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    if (removeMethod != null)
                    {
                        removeMethod.Invoke(viewModel, new object[] { nodeToDelete });
                        _logger.LogInformation("节点 {NodeName} 已删除", nodeToDelete.DisplayName);
                    }
                    else
                    {
                        _logger.LogError("无法找到 RemoveCommandNode 方法");
                    }
                }
                else
                {
                    _logger.LogDebug("用户取消删除节点 {NodeName}", nodeToDelete.DisplayName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理节点删除请求失败: {NodeName}", nodeToDelete?.DisplayName);
                MessageBox.Show($"删除节点时发生错误：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void ClearAllSelections(FlowDesignerViewModel viewModel)
        {
            try
            {
                if (viewModel?.CommandNodes == null) return;

                foreach (var node in viewModel.CommandNodes)
                {
                    node.IsSelected = false;
                    ClearNodeChildrenSelection(node);
                }

                _logger.LogTrace("已清除所有节点选中状态");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清除节点选中状态失败");
            }
        }

        private void ClearNodeChildrenSelection(CommandNode node)
        {
            if (node?.HasChildren == true)
            {
                foreach (var child in node.Children)
                {
                    child.IsSelected = false;
                    ClearNodeChildrenSelection(child);
                }
            }
        }

        private string GenerateDeleteConfirmMessage(CommandNode node)
        {
            var nodeType = node.IsIfNode ? "条件判断节点" :
                          node.IsLoopNode ? "循环节点" :
                          node.IsParallelNode ? "并行节点" :
                          node.IsWaitNode ? "等待节点" :
                          node.IsSetVariableNode ? "设置变量节点" :
                          "节点";

            var childrenInfo = node.HasChildren ?
                $"\n\n该{nodeType}包含 {node.ChildrenCount} 个子命令，删除后所有子命令也将被移除。" : "";

            return $"确定要删除{nodeType} '{node.DisplayName}' 吗？{childrenInfo}";
        }

        public T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            try
            {
                var parentObject = VisualTreeHelper.GetParent(child);
                if (parentObject == null) return null;
                return parentObject is T parent ? parent : FindVisualParent<T>(parentObject);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "查找可视化树父级失败");
                return null;
            }
        }

        public FlowDesignerViewModel GetFlowDesignerViewModel(DependencyObject element)
        {
            try
            {
                var flowDesignerView = FindVisualParent<Views.FlowDesignerView>(element);
                return flowDesignerView?.DataContext as FlowDesignerViewModel;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取FlowDesignerViewModel失败");
                return null;
            }
        }

        public void ScrollToNode(CommandNode node, FrameworkElement container)
        {
            try
            {
                if (node == null || container == null) return;

                _logger.LogDebug("滚动到节点: {NodeName}", node.DisplayName);

                // 这里可以实现滚动逻辑
                // 由于原代码中的滚动逻辑比较复杂，这里简化处理
                var scrollViewer = FindVisualParent<ScrollViewer>(container);
                if (scrollViewer != null)
                {
                    // 简单的滚动到节点位置
                    var targetY = node.Y;
                    scrollViewer.ScrollToVerticalOffset(Math.Max(0, targetY - 100));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "滚动到节点失败: {NodeName}", node?.DisplayName);
            }
        }
    }
}