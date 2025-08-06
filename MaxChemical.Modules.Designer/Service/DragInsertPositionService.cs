// DragInsertPositionService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using MaxChemical.Logging;

namespace MaxChemical.Modules.Designer.Services
{
    public class DragInsertPosition
    {
        public int InsertIndex { get; set; } = -1;
        public CommandNode HighlightNode { get; set; }
        public bool IsValidPosition { get; set; }
    }

    public class DragInsertPositionService
    {
        private readonly ILogService _logger;
        private CommandNode _currentHighlightNode;

        public DragInsertPositionService(ILogService logger)
        {
            _logger = logger?.ForContext<DragInsertPositionService>() ?? throw new ArgumentNullException(nameof(logger));
        }

        public DragInsertPosition DetectInsertPosition(Point mousePosition, Canvas canvas, FlowDesignerViewModel viewModel)
        {
            try
            {
                var result = new DragInsertPosition();

                if (viewModel?.CommandNodes == null || viewModel.CommandNodes.Count == 0)
                {
                    result.InsertIndex = 0;
                    result.IsValidPosition = true;
                    return result;
                }

                // 获取所有节点的位置信息
                var nodePositions = GetNodePositions(viewModel);

                // 检测鼠标在哪个节点后面
                var insertInfo = FindInsertPosition(mousePosition, nodePositions, viewModel);

                result.InsertIndex = insertInfo.Index;
                result.HighlightNode = insertInfo.HighlightNode;
                result.IsValidPosition = insertInfo.Index >= 0;

                _logger.LogTrace("检测插入位置: Index={Index}, HighlightNode={NodeName}, MouseY={MouseY}",
                    result.InsertIndex, result.HighlightNode?.DisplayName ?? "null", mousePosition.Y);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检测插入位置失败");
                return new DragInsertPosition { InsertIndex = -1, IsValidPosition = false };
            }
        }

        private List<NodePositionInfo> GetNodePositions(FlowDesignerViewModel viewModel)
        {
            var positions = new List<NodePositionInfo>();

            // 添加启动节点
            positions.Add(new NodePositionInfo
            {
                Index = -1,
                Node = null,
                Y = viewModel.StartNodeY,
                Height = 64,
                Bottom = viewModel.StartNodeY + 64,
                IsStartNode = true
            });

            // 添加所有命令节点
            for (int i = 0; i < viewModel.CommandNodes.Count; i++)
            {
                var node = viewModel.CommandNodes[i];
                double height = GetNodeHeight(node);

                positions.Add(new NodePositionInfo
                {
                    Index = i,
                    Node = node,
                    Y = node.Y,
                    Height = height,
                    Bottom = node.Y + height
                });
            }

            return positions.OrderBy(p => p.Y).ToList();
        }

        private (int Index, CommandNode HighlightNode) FindInsertPosition(Point mousePosition,
            List<NodePositionInfo> nodePositions, FlowDesignerViewModel viewModel)
        {
            double mouseY = mousePosition.Y;

            // 如果鼠标在第一个节点之前，插入到开头
            if (nodePositions.Count > 1 && mouseY < nodePositions[1].Y - 20)
            {
                return (0, null);
            }

            // 检查鼠标在哪两个节点之间
            for (int i = 0; i < nodePositions.Count - 1; i++)
            {
                var current = nodePositions[i];
                var next = nodePositions[i + 1];

                // 计算插入区域：当前节点底部到下一个节点顶部的中点
                double insertRegionStart = current.Bottom + 10;
                double insertRegionEnd = next.Y - 10;
                double midPoint = (current.Bottom + next.Y) / 2;

                if (mouseY >= insertRegionStart && mouseY <= insertRegionEnd)
                {
                    // 如果在前半部分，插入在当前节点后；如果在后半部分，插入在下一个节点后
                    if (mouseY <= midPoint)
                    {
                        int insertIndex = current.IsStartNode ? 0 : current.Index + 1;
                        return (insertIndex, current.Node);
                    }
                    else
                    {
                        int insertIndex = next.Index + 1;
                        return (insertIndex, next.Node);
                    }
                }
            }

            // 如果鼠标在最后一个节点之后，插入到末尾
            var lastNode = nodePositions.LastOrDefault(p => !p.IsStartNode);
            if (lastNode != null && mouseY > lastNode.Bottom + 20)
            {
                return (viewModel.CommandNodes.Count, lastNode.Node);
            }

            return (-1, null);
        }

        private double GetNodeHeight(CommandNode node)
        {
            try
            {
                if (node?.NodeControl != null)
                {
                    return node.GetActualHeight();
                }

                // 使用估算高度
                return NodeSizeEstimator.GetNodeHeight(node);
            }
            catch
            {
                return node?.IsIfNode == true ? 300 :
                       node?.IsParallelNode == true ? 250 :
                       node?.IsLoopNode == true ? 150 : 64;
            }
        }

        public void SetHighlight(CommandNode node, FlowDesignerViewModel viewModel)
        {
            try
            {
                // 清除之前的高亮
                ClearHighlight(viewModel);

                // 设置新的高亮
                if (node != null)
                {
                    node.IsInsertHighlight = true;
                    _currentHighlightNode = node;
                    _logger.LogTrace("设置插入高亮: {NodeName}", node.DisplayName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "设置高亮失败");
            }
        }

        public void ClearHighlight(FlowDesignerViewModel viewModel)
        {
            try
            {
                if (_currentHighlightNode != null)
                {
                    _currentHighlightNode.IsInsertHighlight = false;
                    _logger.LogTrace("清除插入高亮: {NodeName}", _currentHighlightNode.DisplayName);
                    _currentHighlightNode = null;
                }

                // 确保清除所有节点的高亮状态
                if (viewModel?.CommandNodes != null)
                {
                    foreach (var node in viewModel.CommandNodes)
                    {
                        if (node.IsInsertHighlight)
                        {
                            node.IsInsertHighlight = false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "清除高亮失败");
            }
        }

        private class NodePositionInfo
        {
            public int Index { get; set; }
            public CommandNode Node { get; set; }
            public double Y { get; set; }
            public double Height { get; set; }
            public double Bottom { get; set; }
            public bool IsStartNode { get; set; }
        }
    }
}