using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Views;

namespace MaxChemical.Modules.Designer
{
    /// <summary>
    /// 节点尺寸预估器 - 避免依赖实际渲染
    /// </summary>
    public static class NodeSizeEstimator
    {
        // 预定义的节点尺寸
        public const double DEVICE_NODE_WIDTH = 231;
        public const double DEVICE_NODE_HEIGHT = 64;
        public const double IF_NODE_MIN_WIDTH = 231;
        public const double IF_NODE_MIN_HEIGHT = 200;
        public const double PARALLEL_NODE_MIN_WIDTH = 231; // 新增
        public const double PARALLEL_NODE_MIN_HEIGHT = 231; // 新增
        public const double LOOP_NODE_MIN_WIDTH = 231; // 循环节点
        public const double LOOP_NODE_MIN_HEIGHT = 150;
        public const double WAIT_NODE_MIN_WIDTH = 231; // 等待节点
        public const double WAIT_NODE_MIN_HEIGHT = 100;
        public const double LOGIC_NODE_WIDTH = 231;
        public const double LOGIC_NODE_HEIGHT = 40;

        /// <summary>
        /// 预估IF节点的尺寸（基于内容数量）
        /// </summary>
        public static Size EstimateIfNodeSize(int trueBranchCount, int falseBranchCount, bool hasNestedIf = false)
        {
            // 计算分支内容高度
            double trueBranchHeight = CalculateBranchHeight(trueBranchCount, hasNestedIf);
            double falseBranchHeight = CalculateBranchHeight(falseBranchCount, hasNestedIf);

            // 计算分支内容宽度（考虑嵌套IF的情况）
            double trueBranchWidth = CalculateBranchWidth(trueBranchCount, hasNestedIf);
            double falseBranchWidth = CalculateBranchWidth(falseBranchCount, hasNestedIf);

            // IF节点总高度 = 主卡片高度 + 最大分支高度 + 间距
            double totalHeight = 74 + Math.Max(trueBranchHeight, falseBranchHeight) + 80;

            // IF节点总宽度 = 两个分支宽度 + 中间间距
            double totalWidth = Math.Max(IF_NODE_MIN_WIDTH, falseBranchWidth + trueBranchWidth + 100);

            return new Size(totalWidth, totalHeight);
        }

        /// <summary>
        /// 预估并行节点的尺寸（基于分支数量和内容）
        /// </summary>
        public static Size EstimateParallelNodeSize(CommandNode node)
        {
            try
            {
                // 获取并行分支数量
                int branchCount = 4; // 默认值
                if (node?.LogicCommand?.Properties?.ContainsKey("MaxParallelCount") == true)
                {
                    branchCount = Convert.ToInt32(node.LogicCommand.Properties["MaxParallelCount"]);
                }

                // 每个分支基础宽度 + 间距
                double branchWidth = 200; // 每个分支宽度
                double branchSpacing = 20; // 分支间距
                double totalBranchWidth = (branchCount * branchWidth) + ((branchCount - 1) * branchSpacing);

                // 总宽度 = 分支宽度 + 左右边距
                double totalWidth = Math.Max(PARALLEL_NODE_MIN_WIDTH, totalBranchWidth + 100);

                // 高度基于预估内容
                double totalHeight = PARALLEL_NODE_MIN_HEIGHT;

                return new Size(totalWidth, totalHeight);
            }
            catch
            {
                return new Size(PARALLEL_NODE_MIN_WIDTH, PARALLEL_NODE_MIN_HEIGHT);
            }
        }

        /// <summary>
        /// 动态估算并行节点尺寸（基于实际控件内容）
        /// </summary>
        public static Size EstimateParallelNodeSizeFromControl(ParallelNodeControlView parallelControl)
        {
            try
            {
                if (parallelControl == null)
                    return new Size(PARALLEL_NODE_MIN_WIDTH, PARALLEL_NODE_MIN_HEIGHT);

                // 首先尝试获取实际渲染尺寸
                parallelControl.UpdateLayout();
                if (parallelControl.ActualHeight > 0 && parallelControl.ActualWidth > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"并行节点使用实际尺寸: {parallelControl.ActualWidth}x{parallelControl.ActualHeight}");
                    return new Size(parallelControl.ActualWidth, parallelControl.ActualHeight);
                }

                // 如果无法获取实际尺寸，基于分支数量和内容估算
                int branchCount = parallelControl.GetBranchCount();
                int totalCommands = parallelControl.GetTotalCommandCount();

                var estimatedSize = EstimateParallelNodeSizeByContent(branchCount, totalCommands);
                System.Diagnostics.Debug.WriteLine($"并行节点基于内容估算尺寸: {estimatedSize.Width}x{estimatedSize.Height} (分支:{branchCount}, 命令:{totalCommands})");

                return estimatedSize;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"估算并行节点尺寸失败: {ex.Message}");
                return new Size(PARALLEL_NODE_MIN_WIDTH, PARALLEL_NODE_MIN_HEIGHT);
            }
        }

        /// <summary>
        /// 根据内容估算并行节点尺寸
        /// </summary>
        private static Size EstimateParallelNodeSizeByContent(int branchCount, int totalCommands)
        {
            // 计算宽度
            double branchWidth = 200;
            double branchSpacing = 20;
            double totalWidth = Math.Max(PARALLEL_NODE_MIN_WIDTH,
                (branchCount * branchWidth) + ((branchCount - 1) * branchSpacing) + 100);

            // 计算高度（基于平均每个分支的命令数量）
            double avgCommandsPerBranch = branchCount > 0 ? (double)totalCommands / branchCount : 0;
            double branchContentHeight = Math.Max(60, avgCommandsPerBranch * (DEVICE_NODE_HEIGHT + 8));
            double totalHeight = Math.Max(PARALLEL_NODE_MIN_HEIGHT,
                74 + branchContentHeight + 80); // 主卡片 + 分支内容 + 间距

            return new Size(totalWidth, totalHeight);
        }

        /// <summary>
        /// 动态估算IF节点尺寸（基于实际控件内容）
        /// </summary>
        public static Size EstimateIfNodeSizeFromControl(IfNodeControlView ifControl)
        {
            try
            {
                if (ifControl == null) return new Size(IF_NODE_MIN_WIDTH, IF_NODE_MIN_HEIGHT);

                // 首先尝试获取实际渲染尺寸
                ifControl.UpdateLayout();
                if (ifControl.ActualHeight > 0 && ifControl.ActualWidth > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"IF节点使用实际尺寸: {ifControl.ActualWidth}x{ifControl.ActualHeight}");
                    return new Size(ifControl.ActualWidth, ifControl.ActualHeight);
                }

                // 如果无法获取实际尺寸，基于内容数量估算
                int trueBranchCount = ifControl.GetTrueBranchCount();
                int falseBranchCount = ifControl.GetFalseBranchCount();

                // 检查是否有嵌套IF
                bool hasNestedIf = HasNestedIfNode(ifControl);

                var estimatedSize = EstimateIfNodeSize(trueBranchCount, falseBranchCount, hasNestedIf);
                System.Diagnostics.Debug.WriteLine($"IF节点基于内容估算尺寸: {estimatedSize.Width}x{estimatedSize.Height} (TRUE:{trueBranchCount}, FALSE:{falseBranchCount}, 嵌套IF:{hasNestedIf})");

                return estimatedSize;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"估算IF节点尺寸失败: {ex.Message}");
                return new Size(IF_NODE_MIN_WIDTH, IF_NODE_MIN_HEIGHT);
            }
        }

        /// <summary>
        /// 检查IF节点是否包含嵌套IF
        /// </summary>
        private static bool HasNestedIfNode(IfNodeControlView ifControl)
        {
            try
            {
                // 检查TRUE分支
                foreach (System.Windows.UIElement child in ifControl.TrueBranchContent.Children)
                {
                    if (child is IfNodeControlView)
                    {
                        return true;
                    }
                }

                // 检查FALSE分支
                foreach (System.Windows.UIElement child in ifControl.FalseBranchContent.Children)
                {
                    if (child is IfNodeControlView)
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"检查嵌套IF失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 计算分支内容高度
        /// </summary>
        private static double CalculateBranchHeight(int itemCount, bool hasNestedIf)
        {
            if (itemCount == 0) return 60; // 最小高度

            double totalHeight = 16; // 容器padding

            for (int i = 0; i < itemCount; i++)
            {
                if (hasNestedIf && i == 0) // 假设第一个是嵌套IF
                {
                    totalHeight += IF_NODE_MIN_HEIGHT; // 嵌套IF的预估高度
                }
                else
                {
                    totalHeight += DEVICE_NODE_HEIGHT; // 普通节点高度
                }

                if (i < itemCount - 1)
                {
                    totalHeight += 8; // 节点间距
                }
            }

            return Math.Max(60, totalHeight);
        }

        /// <summary>
        /// 计算分支内容宽度
        /// </summary>
        private static double CalculateBranchWidth(int itemCount, bool hasNestedIf)
        {
            if (itemCount == 0) return 200; // 最小宽度

            double maxWidth = 200;

            if (hasNestedIf)
            {
                maxWidth = Math.Max(maxWidth, IF_NODE_MIN_WIDTH + 20); // 嵌套IF需要更大宽度
            }
            else
            {
                maxWidth = Math.Max(maxWidth, DEVICE_NODE_WIDTH + 20);
            }

            return maxWidth;
        }

        /// <summary>
        /// 根据节点类型预估尺寸（增强版）- 支持所有逻辑节点类型
        /// </summary>
        public static Size EstimateNodeSize(CommandNode node)
        {
            if (node == null) return new Size(DEVICE_NODE_WIDTH, DEVICE_NODE_HEIGHT);

            if (node.IsDeviceCommand)
            {
                return new Size(DEVICE_NODE_WIDTH, DEVICE_NODE_HEIGHT);
            }
            else if (node.IsLogicCommand)
            {
                return EstimateLogicNodeSize(node);
            }
            else
            {
                return new Size(LOGIC_NODE_WIDTH, LOGIC_NODE_HEIGHT);
            }
        }

        /// <summary>
        /// 根据逻辑命令类型估算尺寸
        /// </summary>
        public static Size EstimateLogicNodeSize(CommandNode node)
        {
            if (node?.LogicCommand == null)
                return new Size(LOGIC_NODE_WIDTH, LOGIC_NODE_HEIGHT);

            var commandType = node.LogicCommand.CommandType.ToString();

            switch (commandType.ToLower())
            {
                case "if":
                    // IF节点 - 如果有控件实例，尝试动态估算
                    if (node.NodeControl is IfNodeControlView ifControl)
                    {
                        return EstimateIfNodeSizeFromControl(ifControl);
                    }
                    return new Size(IF_NODE_MIN_WIDTH, IF_NODE_MIN_HEIGHT);

                case "parallel":
                    // 并行节点 - 如果有控件实例，尝试动态估算
                    if (node.NodeControl is ParallelNodeControlView parallelControl)
                    {
                        return EstimateParallelNodeSizeFromControl(parallelControl);
                    }
                    return EstimateParallelNodeSize(node);

                case "loop":
                case "for":
                case "while":
                    // 循环节点 - 使用动态估算
                    if (node.NodeControl is LoopNodeControlView loopControl)
                    {
                        return EstimateLoopNodeSizeFromControl(loopControl);
                    }
                    return EstimateLoopNodeSize(node);

                case "wait":
                case "delay":
                case "sleep":
                    // 等待节点
                    return new Size(WAIT_NODE_MIN_WIDTH, WAIT_NODE_MIN_HEIGHT);

                default:
                    // 其他逻辑节点
                    return new Size(LOGIC_NODE_WIDTH, LOGIC_NODE_HEIGHT);
            }
        }

        /// <summary>
        /// 判断是否为复杂节点（需要居中对齐的节点）
        /// </summary>
        public static bool IsComplexNode(CommandNode node)
        {
            if (node?.IsLogicCommand != true) return false;

            var commandType = node.LogicCommand.CommandType.ToString().ToLower();
            return commandType == "if" ||
                   commandType == "parallel" ||
                   commandType == "loop" ||
                   commandType == "for" ||
                   commandType == "while";
        }

        /// <summary>
        /// 获取复杂节点的预估宽度（用于居中对齐计算）
        /// </summary>
        public static double GetComplexNodeWidth(LogicCommand logicCommand)
        {
            if (logicCommand == null) return DEVICE_NODE_WIDTH;

            var commandType = logicCommand.CommandType.ToString().ToLower();

            switch (commandType)
            {
                case "if":
                    return IF_NODE_MIN_WIDTH;
                case "parallel":
                    return PARALLEL_NODE_MIN_WIDTH;
                case "loop":
                case "for":
                case "while":
                    return LOOP_NODE_MIN_WIDTH;
                case "wait":
                case "delay":
                case "sleep":
                    return WAIT_NODE_MIN_WIDTH;
                default:
                    return LOGIC_NODE_WIDTH;
            }
        }

        /// <summary>
        /// 获取节点的实际或预估高度
        /// </summary>
        public static double GetNodeHeight(CommandNode node)
        {
            try
            {
                // 首先尝试获取实际高度
                if (node?.NodeControl != null)
                {
                    var actualHeight = node.GetActualHeight();
                    if (actualHeight > 0)
                    {
                        return actualHeight;
                    }
                }

                // 使用预估高度
                var estimatedSize = EstimateNodeSize(node);
                return estimatedSize.Height;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取节点高度失败: {ex.Message}");
                // 返回默认高度
                return GetDefaultNodeHeight(node);
            }
        }

        /// <summary>
        /// 获取节点的实际或预估宽度
        /// </summary>
        public static double GetNodeWidth(CommandNode node)
        {
            try
            {
                // 首先尝试获取实际宽度
                if (node?.NodeControl != null)
                {
                    var actualWidth = node.GetActualWidth();
                    if (actualWidth > 0)
                    {
                        return actualWidth;
                    }
                }

                // 使用预估宽度
                var estimatedSize = EstimateNodeSize(node);
                return estimatedSize.Width;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取节点宽度失败: {ex.Message}");
                // 返回默认宽度
                return GetDefaultNodeWidth(node);
            }
        }

        /// <summary>
        /// 获取默认节点高度
        /// </summary>
        public static double GetDefaultNodeHeight(CommandNode node)
        {
            if (node?.IsLogicCommand == true)
            {
                var commandType = node.LogicCommand.CommandType.ToString().ToLower();
                switch (commandType)
                {
                    case "if": return IF_NODE_MIN_HEIGHT;
                    case "parallel": return PARALLEL_NODE_MIN_HEIGHT;
                    case "loop":
                    case "for":
                    case "while": return LOOP_NODE_MIN_HEIGHT;
                    case "wait":
                    case "delay":
                    case "sleep": return WAIT_NODE_MIN_HEIGHT;
                    default: return LOGIC_NODE_HEIGHT;
                }
            }
            return DEVICE_NODE_HEIGHT;
        }

        /// <summary>
        /// 获取默认节点宽度
        /// </summary>
        public static double GetDefaultNodeWidth(CommandNode node)
        {
            if (node?.IsLogicCommand == true)
            {
                var commandType = node.LogicCommand.CommandType.ToString().ToLower();
                switch (commandType)
                {
                    case "if": return IF_NODE_MIN_WIDTH;
                    case "parallel": return PARALLEL_NODE_MIN_WIDTH;
                    case "loop":
                    case "for":
                    case "while": return LOOP_NODE_MIN_WIDTH;
                    case "wait":
                    case "delay":
                    case "sleep": return WAIT_NODE_MIN_WIDTH;
                    default: return LOGIC_NODE_WIDTH;
                }
            }
            return DEVICE_NODE_WIDTH;
        }

        /// <summary>
        /// 动态估算循环节点尺寸（基于实际控件内容）
        /// </summary>
        public static Size EstimateLoopNodeSizeFromControl(LoopNodeControlView loopControl)
        {
            try
            {
                if (loopControl == null)
                    return new Size(LOOP_NODE_MIN_WIDTH, LOOP_NODE_MIN_HEIGHT);

                // 首先尝试获取实际渲染尺寸
                loopControl.UpdateLayout();
                if (loopControl.ActualHeight > 0 && loopControl.ActualWidth > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"循环节点使用实际尺寸: {loopControl.ActualWidth}x{loopControl.ActualHeight}");
                    return new Size(loopControl.ActualWidth, loopControl.ActualHeight);
                }

                // 如果无法获取实际尺寸，基于内容数量估算
                var loopCommands = loopControl.GetLoopCommands();
                int commandCount = loopCommands?.Count ?? 0;

                var estimatedSize = EstimateLoopNodeSizeByContent(commandCount, loopCommands);
                System.Diagnostics.Debug.WriteLine($"循环节点基于内容估算尺寸: {estimatedSize.Width}x{estimatedSize.Height} (命令数:{commandCount})");

                return estimatedSize;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"估算循环节点尺寸失败: {ex.Message}");
                return new Size(LOOP_NODE_MIN_WIDTH, LOOP_NODE_MIN_HEIGHT);
            }
        }

        /// <summary>
        /// 根据内容估算循环节点尺寸
        /// </summary>
        public static Size EstimateLoopNodeSizeByContent(int commandCount, IReadOnlyList<CommandNode> commands = null)
        {
            try
            {
                // 计算基础宽度
                double totalWidth = LOOP_NODE_MIN_WIDTH;

                // 计算高度
                double totalHeight = 60; // 基础高度：标题 + 拖放区域基础高度

                if (commandCount > 0 && commands != null)
                {
                    double maxChildWidth = 0;

                    foreach (var command in commands)
                    {
                        // 计算每个子命令的高度
                        double childHeight = EstimateNodeSize(command).Height;
                        totalHeight += childHeight + 8; // 子项高度 + 间距

                        // 计算每个子命令的宽度，找出最大值
                        double childWidth = EstimateNodeSize(command).Width;
                        maxChildWidth = Math.Max(maxChildWidth, childWidth);
                    }

                    // 根据子命令的最大宽度调整循环节点宽度
                    totalWidth = Math.Max(totalWidth, maxChildWidth + 40); // 子项宽度 + 内边距
                }

                // 添加底部边距
                totalHeight += 20;

                return new Size(totalWidth, Math.Max(LOOP_NODE_MIN_HEIGHT, totalHeight));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"根据内容估算循环节点尺寸失败: {ex.Message}");
                return new Size(LOOP_NODE_MIN_WIDTH, LOOP_NODE_MIN_HEIGHT);
            }
        }

        /// <summary>
        /// 估算循环节点的尺寸（基于CommandNode）
        /// </summary>
        public static Size EstimateLoopNodeSize(CommandNode loopNode)
        {
            try
            {
                if (loopNode == null || !loopNode.IsLoopNode)
                    return new Size(LOOP_NODE_MIN_WIDTH, LOOP_NODE_MIN_HEIGHT);

                // 如果有控件实例，使用动态估算
                if (loopNode.NodeControl is LoopNodeControlView loopControl)
                {
                    return EstimateLoopNodeSizeFromControl(loopControl);
                }

                // 否则基于Children集合估算
                var children = loopNode.Children?.ToList();
                return EstimateLoopNodeSizeByContent(children?.Count ?? 0, children?.AsReadOnly());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"估算循环节点尺寸失败: {ex.Message}");
                return new Size(LOOP_NODE_MIN_WIDTH, LOOP_NODE_MIN_HEIGHT);
            }
        }



    }
}