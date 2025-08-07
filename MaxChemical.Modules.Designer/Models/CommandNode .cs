using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using DevicePlugins.Devices;
using MaxChemical.Modules.Designer.Views;
using Prism.Mvvm;
using System.Linq;
using System.ComponentModel;

namespace MaxChemical.Modules.Designer.Models
{
    public class CommandNode : BindableBase
    {
        private double _x;
        private double _y;
        private int _order;
        private bool _isSelected;
        private bool _isExecuting;
        private UserControl _nodeControl;
        private ObservableCollection<CommandNode> _children;
        private Dictionary<string, object> _executionProperties = new Dictionary<string, object>();

        public CommandNode()
        {
            NodeId = Guid.NewGuid().ToString();
            _children = new ObservableCollection<CommandNode>();

            // 监听子集合变化以通知相关属性更新
            _children.CollectionChanged += (s, e) =>
            {
                RaisePropertyChanged(nameof(HasChildren));
                RaisePropertyChanged(nameof(ChildrenCount));
            };
        }

        public CommandNode(DeviceCommand deviceCommand) : this()
        {
            DeviceCommand = deviceCommand ?? throw new ArgumentNullException(nameof(deviceCommand));
            DisplayName = deviceCommand.Name;
            DisplayHelpText = deviceCommand.HelpText ?? "设备命令";
            DisplayImageLocation = deviceCommand.ImageLocation;
            HasImage = !string.IsNullOrEmpty(deviceCommand.ImageLocation);
        }

        public CommandNode(LogicCommand logicCommand) : this()
        {
            LogicCommand = logicCommand ?? throw new ArgumentNullException(nameof(logicCommand));
            DisplayName = logicCommand.Name;
            DisplayHelpText = logicCommand.HelpText ?? "逻辑命令";
            DisplayImageLocation = logicCommand.ImageLocation;
            HasImage = !string.IsNullOrEmpty(logicCommand.ImageLocation);
        }

        #region 基本属性

        /// <summary>
        /// 节点唯一标识
        /// </summary>
        public string NodeId { get; set; }

        /// <summary>
        /// 显示名称
        /// </summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// 帮助文本
        /// </summary>
        public string DisplayHelpText { get; set; }

        /// <summary>
        /// 图标路径
        /// </summary>
        public string DisplayImageLocation { get; set; }

        /// <summary>
        /// 是否有图标
        /// </summary>
        public bool HasImage { get; set; }

        /// <summary>
        /// 设备命令（如果是设备命令节点）
        /// </summary>
        public DeviceCommand DeviceCommand { get; set; }

        /// <summary>
        /// 逻辑命令（如果是逻辑命令节点）
        /// </summary>
        public LogicCommand LogicCommand { get; set; }

        /// <summary>
        /// 节点对应的用户控件（由插件创建）
        /// </summary>
        public UserControl NodeControl
        {
            get => _nodeControl;
            set => SetProperty(ref _nodeControl, value);
        }
        /// <summary>
        /// 插入高亮属性
        /// </summary>
        private bool _isInsertHighlight;
        public bool IsInsertHighlight
        {
            get => _isInsertHighlight;
            set => SetProperty(ref _isInsertHighlight, value);
        }
        /// <summary>
        /// 子命令集合（统一用于所有容器类型）
        /// </summary>
        public ObservableCollection<CommandNode> Children
        {
            get => _children;
            set
            {
                if (_children != value)
                {
                    _children = value;
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(HasChildren));
                    RaisePropertyChanged(nameof(ChildrenCount));
                }
            }
        }

        /// <summary>
        /// 是否有子命令
        /// </summary>
        public bool HasChildren => Children?.Count > 0;

        /// <summary>
        /// 子命令数量
        /// </summary>
        public int ChildrenCount => Children?.Count ?? 0;

        /// <summary>
        /// 执行相关属性（如分支标识、分支索引等）
        /// </summary>
        public Dictionary<string, object> ExecutionProperties => _executionProperties;

        #endregion

        #region 位置和状态属性
        private bool _isVisible = true;

        /// <summary>
        /// 节点是否可见（用于控制渲染时机）
        /// </summary>
        public bool IsVisible
        {
            get => _isVisible;
            set => SetProperty(ref _isVisible, value);
        }

        /// <summary>
        /// X坐标
        /// </summary>
        public double X
        {
            get => _x;
            set => SetProperty(ref _x, value);
        }

        /// <summary>
        /// Y坐标
        /// </summary>
        public double Y
        {
            get => _y;
            set => SetProperty(ref _y, value);
        }

        /// <summary>
        /// 执行顺序
        /// </summary>
        public int Order
        {
            get => _order;
            set => SetProperty(ref _order, value);
        }

        /// <summary>
        /// 是否被选中
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        /// <summary>
        /// 是否正在执行
        /// </summary>
        public bool IsExecuting
        {
            get => _isExecuting;
            set => SetProperty(ref _isExecuting, value);
        }

        #endregion

        #region 只读属性

        /// <summary>
        /// 是否为设备命令节点
        /// </summary>
        public bool IsDeviceCommand => DeviceCommand != null;

        /// <summary>
        /// 是否为逻辑命令节点
        /// </summary>
        public bool IsLogicCommand => LogicCommand != null;

        /// <summary>
        /// 是否为IF条件判断节点
        /// </summary>
        public bool IsIfNode => IsLogicCommand && LogicCommand.CommandType.ToString() == "If";

        /// <summary>
        /// 是否为并行执行节点
        /// </summary>
        public bool IsParallelNode => IsLogicCommand && LogicCommand.CommandType.ToString().Equals("Parallel", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 是否为循环节点
        /// </summary>
        public bool IsLoopNode => IsLogicCommand &&
            (LogicCommand.CommandType.ToString().Equals("Loop", StringComparison.OrdinalIgnoreCase) ||
             LogicCommand.CommandType.ToString().Equals("For", StringComparison.OrdinalIgnoreCase) ||
             LogicCommand.CommandType.ToString().Equals("While", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// 是否为等待节点
        /// </summary>
        public bool IsWaitNode => IsLogicCommand &&
            (LogicCommand.CommandType.ToString().Equals("Wait", StringComparison.OrdinalIgnoreCase) ||
             LogicCommand.CommandType.ToString().Equals("Delay", StringComparison.OrdinalIgnoreCase) ||
             LogicCommand.CommandType.ToString().Equals("Sleep", StringComparison.OrdinalIgnoreCase));
        /// <summary>
        /// 是否为赋值节点
        /// </summary>
        public bool IsSetVariableNode => IsLogicCommand &&
            (LogicCommand.CommandType.ToString().Equals("SetVariable", StringComparison.OrdinalIgnoreCase) ||
             LogicCommand.CommandType.ToString().Equals("Variable", StringComparison.OrdinalIgnoreCase) ||
             LogicCommand.CommandType.ToString().Equals("VariableSet", StringComparison.OrdinalIgnoreCase));
        
        /// <summary>
        /// 是否为复杂节点（需要特殊布局处理的节点）
        /// </summary>
        public bool IsComplexNode => IsIfNode || IsParallelNode || IsLoopNode;

        /// <summary>
        /// 是否为容器类型节点（可以包含子命令）
        /// </summary>
        public bool IsContainerNode => IsLoopNode || IsIfNode || IsParallelNode;

        /// <summary>
        /// 命令类型描述
        /// </summary>
        public string CommandType
        {
            get
            {
                if (IsDeviceCommand)
                    return "设备命令";
                if (IsLogicCommand)
                    return LogicCommand.CommandType.ToString();
                return "未知命令";
            }
        }

        #endregion

        #region 执行属性管理

        /// <summary>
        /// 设置执行属性
        /// </summary>
        public void SetExecutionProperty<T>(string key, T value)
        {
            _executionProperties[key] = value;
        }

        /// <summary>
        /// 获取执行属性
        /// </summary>
        public T GetExecutionProperty<T>(string key, T defaultValue = default(T))
        {
            return _executionProperties.TryGetValue(key, out var value) && value is T result
                ? result : defaultValue;
        }

        /// <summary>
        /// 添加子命令到指定分支（IF节点用）
        /// </summary>
        public void AddChildToBranch(CommandNode child, bool isTrueBranch)
        {
            child.SetExecutionProperty("BranchType", isTrueBranch ? "True" : "False");
            Children.Add(child);
        }

        /// <summary>
        /// 添加子命令到指定并行分支
        /// </summary>
        public void AddChildToParallelBranch(CommandNode child, int branchIndex)
        {
            child.SetExecutionProperty("BranchIndex", branchIndex);
            Children.Add(child);
        }
        public void AddChildToBranchAtIndex(CommandNode child, bool isTrueBranch, int index)
        {
            try
            {
                child.SetExecutionProperty("BranchType", isTrueBranch ? "True" : "False");

                // 获取同一分支的所有子节点的全局索引
                var sameBranchIndices = new List<int>();

                for (int i = 0; i < Children.Count; i++)
                {
                    var existingChild = Children[i];
                    var existingBranchType = existingChild.GetExecutionProperty<string>("BranchType");

                    if (existingBranchType == (isTrueBranch ? "True" : "False"))
                    {
                        sameBranchIndices.Add(i);
                    }
                }

                // 如果索引超出当前分支范围，添加到分支末尾
                if (index < 0 || index >= sameBranchIndices.Count)
                {
                    // 找到同分支的最后一个元素，插入到它后面
                    if (sameBranchIndices.Count > 0)
                    {
                        int lastBranchIndex = sameBranchIndices[sameBranchIndices.Count - 1];
                        Children.Insert(lastBranchIndex + 1, child);
                    }
                    else
                    {
                        // 分支为空，直接添加
                        Children.Add(child);
                    }
                    return;
                }

                // 在指定的分支位置插入
                if (index == 0 && sameBranchIndices.Count == 0)
                {
                    // 分支为空，直接添加
                    Children.Add(child);
                }
                else if (index == 0)
                {
                    // 插入到第一个同分支元素之前
                    Children.Insert(sameBranchIndices[0], child);
                }
                else if (index >= sameBranchIndices.Count)
                {
                    // 插入到最后一个同分支元素之后
                    Children.Insert(sameBranchIndices[sameBranchIndices.Count - 1] + 1, child);
                }
                else
                {
                    // 插入到指定同分支元素之前
                    Children.Insert(sameBranchIndices[index], child);
                }

                // 调试输出
                System.Diagnostics.Debug.WriteLine($"AddChildToBranchAtIndex: 分支={isTrueBranch}, 索引={index}, 节点={child.DisplayName}");
                System.Diagnostics.Debug.WriteLine($"  同分支索引列表: [{string.Join(", ", sameBranchIndices)}]");
                System.Diagnostics.Debug.WriteLine($"  插入后Children总数: {Children.Count}");
            }
            catch (Exception ex)
            {
                // 记录错误，回退到普通添加
                System.Diagnostics.Debug.WriteLine($"AddChildToBranchAtIndex失败: {ex.Message}");
                Children.Add(child);
            }
        }
        /// <summary>
        /// 添加子命令到循环
        /// </summary>
        public void AddChildToLoop(CommandNode child)
        {
            child.SetExecutionProperty("ParentType", "Loop");
            Children.Add(child);
        }

        /// <summary>
        /// 获取指定分支的子命令
        /// </summary>
        public IEnumerable<CommandNode> GetBranchChildren(bool isTrueBranch)
        {
            var branchType = isTrueBranch ? "True" : "False";
            return Children.Where(c => c.GetExecutionProperty<string>("BranchType") == branchType);
        }

        /// <summary>
        /// 获取指定并行分支的子命令
        /// </summary>
        public IEnumerable<CommandNode> GetParallelBranchChildren(int branchIndex)
        {
            return Children.Where(c => c.GetExecutionProperty<int>("BranchIndex") == branchIndex);
        }

        #endregion

        #region 子命令管理方法

        /// <summary>
        /// 添加子命令
        /// </summary>
        public void AddChild(CommandNode child)
        {
            if (child == null) return;
            Children.Add(child);
        }

        /// <summary>
        /// 移除子命令
        /// </summary>
        public bool RemoveChild(CommandNode child)
        {
            if (child == null) return false;
            return Children.Remove(child);
        }

        /// <summary>
        /// 清空所有子命令
        /// </summary>
        public void ClearChildren()
        {
            Children?.Clear();
        }

        /// <summary>
        /// 获取所有子命令（递归）
        /// </summary>
        public IEnumerable<CommandNode> GetAllDescendants()
        {
            if (!HasChildren) yield break;

            foreach (var child in Children)
            {
                yield return child;

                foreach (var descendant in child.GetAllDescendants())
                {
                    yield return descendant;
                }
            }
        }

        /// <summary>
        /// 获取指定索引的子命令
        /// </summary>
        public CommandNode GetChild(int index)
        {
            if (Children == null || index < 0 || index >= Children.Count)
                return null;
            return Children[index];
        }

        /// <summary>
        /// 在指定位置插入子命令
        /// </summary>
        public void InsertChild(int index, CommandNode child)
        {
            if (child == null) return;

            if (index < 0) index = 0;
            if (index > Children.Count) index = Children.Count;

            Children.Insert(index, child);
        }

        #endregion

        #region 尺寸计算方法

        /// <summary>
        /// 获取节点的实际渲染尺寸
        /// </summary>
        public Size GetActualSize()
        {
            try
            {
                if (IsIfNode && NodeControl is IfNodeControlView ifControl)
                {
                    ifControl.UpdateLayout();
                    double actualWidth = ifControl.ActualWidth > 0 ? ifControl.ActualWidth : ifControl.Width;
                    double actualHeight = ifControl.ActualHeight > 0 ? ifControl.ActualHeight : ifControl.Height;

                    if (actualWidth <= 0 || actualHeight <= 0)
                    {
                        ifControl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        var measuredSize = ifControl.DesiredSize;
                        actualWidth = measuredSize.Width > 0 ? measuredSize.Width : 600;
                        actualHeight = measuredSize.Height > 0 ? measuredSize.Height : 300;
                    }

                    return new Size(actualWidth, actualHeight);
                }
                else if (IsParallelNode && NodeControl is ParallelNodeControlView parallelControl)
                {
                    parallelControl.UpdateLayout();
                    double actualWidth = parallelControl.ActualWidth > 0 ? parallelControl.ActualWidth : parallelControl.Width;
                    double actualHeight = parallelControl.ActualHeight > 0 ? parallelControl.ActualHeight : parallelControl.Height;

                    if (actualWidth <= 0 || actualHeight <= 0)
                    {
                        parallelControl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        var measuredSize = parallelControl.DesiredSize;
                        actualWidth = measuredSize.Width > 0 ? measuredSize.Width : 800;
                        actualHeight = measuredSize.Height > 0 ? measuredSize.Height : 250;
                    }

                    System.Diagnostics.Debug.WriteLine($"并行节点 {DisplayName} 实际尺寸: {actualWidth}x{actualHeight}");
                    return new Size(actualWidth, actualHeight);
                }
                else if (IsLoopNode && NodeControl is LoopNodeControlView loopControl)
                {
                    loopControl.UpdateLayout();
                    double actualWidth = loopControl.ActualWidth > 0 ? loopControl.ActualWidth : loopControl.Width;
                    double actualHeight = loopControl.ActualHeight > 0 ? loopControl.ActualHeight : loopControl.Height;

                    if (actualWidth <= 0 || actualHeight <= 0)
                    {
                        loopControl.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                        var measuredSize = loopControl.DesiredSize;
                        actualWidth = measuredSize.Width > 0 ? measuredSize.Width : 300;
                        actualHeight = measuredSize.Height > 0 ? measuredSize.Height : 150;
                    }

                    return new Size(actualWidth, actualHeight);
                }
                else if (NodeControl != null)
                {
                    NodeControl.UpdateLayout();
                    double width = NodeControl.ActualWidth > 0 ? NodeControl.ActualWidth : 231;
                    double height = NodeControl.ActualHeight > 0 ? NodeControl.ActualHeight : 64;
                    return new Size(width, height);
                }
                else
                {
                    // 返回默认尺寸
                    if (IsIfNode) return new Size(600, 300);
                    if (IsParallelNode) return new Size(800, 250);
                    if (IsLoopNode) return new Size(300, 150);
                    return new Size(231, 64);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取节点 {DisplayName} 尺寸失败: {ex.Message}");
                // 返回默认尺寸
                if (IsIfNode) return new Size(600, 300);
                if (IsParallelNode) return new Size(800, 250);
                if (IsLoopNode) return new Size(300, 150);
                return new Size(231, 64);
            }
        }

        /// <summary>
        /// 获取节点的实际宽度
        /// </summary>
        public double GetActualWidth()
        {
            return GetActualSize().Width;
        }

        /// <summary>
        /// 获取节点的实际高度
        /// </summary>
        public double GetActualHeight()
        {
            return GetActualSize().Height;
        }

        /// <summary>
        /// 获取节点的实际中心点X坐标
        /// </summary>
        public double GetActualCenterX()
        {
            return X + GetActualWidth() / 2;
        }

        /// <summary>
        /// 获取主卡片的中心X坐标（用于连接线计算）
        /// </summary>
        public double GetMainCardCenterX()
        {
            if (IsComplexNode)
            {
                double nodeWidth = GetActualWidth();
                return X + nodeWidth / 2;
            }
            else
            {
                return X + GetActualWidth() / 2;
            }
        }

        #endregion

        #region 公共方法

        /// <summary>
        /// 获取节点底部连接点坐标（用于向下连接的起点）
        /// </summary>
        public System.Windows.Point GetBottomConnectionPoint()
        {
            double centerX = GetMainCardCenterX();
            double nodeHeight = GetActualHeight();
            return new System.Windows.Point(centerX, Y + nodeHeight);
        }

        /// <summary>
        /// 获取节点顶部连接点坐标（用于向上连接的终点）
        /// </summary>
        public System.Windows.Point GetTopConnectionPoint()
        {
            double centerX = GetMainCardCenterX();
            return new System.Windows.Point(centerX, Y);
        }

        /// <summary>
        /// 获取节点的中心点坐标
        /// </summary>
        public System.Windows.Point GetCenterPoint()
        {
            var size = GetActualSize();
            return new System.Windows.Point(X + size.Width / 2, Y + size.Height / 2);
        }

        /// <summary>
        /// 获取主卡片的中心点坐标（用于连接线）
        /// </summary>
        public System.Windows.Point GetMainCardCenterPoint()
        {
            if (IsIfNode)
            {
                double nodeWidth = GetActualWidth();
                double mainCardWidth = 200;
                double mainCardHeight = 74;

                return new System.Windows.Point(
                    X + nodeWidth / 2,
                    Y + 10 + mainCardHeight / 2
                );
            }
            else
            {
                return new System.Windows.Point(X + 115.5, Y + 32);
            }
        }

        /// <summary>
        /// 获取连接点坐标（连接线的起点或终点）
        /// </summary>
        public System.Windows.Point GetConnectionPoint(bool isTop = true)
        {
            double centerX = GetMainCardCenterX();

            if (isTop)
            {
                return new System.Windows.Point(centerX, Y);
            }
            else
            {
                if (IsIfNode)
                {
                    return new System.Windows.Point(centerX, Y + 10 + 74);
                }
                return new System.Windows.Point(centerX, Y + 64);
            }
        }

        /// <summary>
        /// 获取节点的边界矩形
        /// </summary>
        public System.Windows.Rect GetBounds()
        {
            var size = GetActualSize();
            return new System.Windows.Rect(X, Y, size.Width, size.Height);
        }

        /// <summary>
        /// 检查点是否在节点内
        /// </summary>
        public bool Contains(System.Windows.Point point)
        {
            return GetBounds().Contains(point);
        }

        /// <summary>
        /// 克隆节点（深拷贝）
        /// </summary>
        public CommandNode Clone()
        {
            CommandNode clone;

            if (IsDeviceCommand)
            {
                clone = new CommandNode(DeviceCommand);
            }
            else if (IsLogicCommand)
            {
                clone = new CommandNode(LogicCommand);
            }
            else
            {
                clone = new CommandNode();
            }

            clone.X = X;
            clone.Y = Y;
            clone.Order = Order;
            clone.IsSelected = false;
            clone.IsExecuting = false;

            // 深拷贝子命令
            if (HasChildren)
            {
                foreach (var child in Children)
                {
                    clone.Children.Add(child.Clone());
                }
            }

            // 复制执行属性
            foreach (var prop in _executionProperties)
            {
                clone.SetExecutionProperty(prop.Key, prop.Value);
            }

            return clone;
        }

        /// <summary>
        /// 重置节点状态
        /// </summary>
        public void ResetState()
        {
            IsSelected = false;
            IsExecuting = false;
        }

        #endregion

        #region 重写方法

        public override string ToString()
        {
            return $"{DisplayName} ({CommandType}) - Order: {Order}, Position: ({X:F1}, {Y:F1}), Children: {ChildrenCount}";
        }

        public override bool Equals(object obj)
        {
            return obj is CommandNode other && NodeId == other.NodeId;
        }

        public override int GetHashCode()
        {
            return NodeId?.GetHashCode() ?? 0;
        }

        #endregion
    }
}