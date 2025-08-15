using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using Prism.Events;
using Prism.Ioc;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class IfCommandPropertiesView : UserControl
    {
        private CancellationTokenSource _debounceCts;
        private IEventAggregator _eventAggregator;
        public IfCommandPropertiesView()
        {
            InitializeComponent();
            this.DataContextChanged += OnDataContextChanged;
            this.Loaded += OnLoaded;
           
        }
     
        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateConditionPreview();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // 初始化默认值
            if (DataContext is CommandNode node && node.LogicCommand != null)
            {
                EnsureDefaultValues(node);
                UpdateConditionPreview();
            }
        }

        private void EnsureDefaultValues(CommandNode node)
        {
            if (node.LogicCommand?.Properties == null) return;

            // 根据插件的默认值设置
            if (!node.LogicCommand.Properties.ContainsKey("LeftOperand"))
                node.LogicCommand.Properties["LeftOperand"] = "A";

            if (!node.LogicCommand.Properties.ContainsKey("Operator"))
                node.LogicCommand.Properties["Operator"] = "Equal";

            if (!node.LogicCommand.Properties.ContainsKey("RightOperand"))
                node.LogicCommand.Properties["RightOperand"] = "B";

            if (!node.LogicCommand.Properties.ContainsKey("Description"))
                node.LogicCommand.Properties["Description"] = "条件判断";
        }

        private async void OnValueChanged(object sender, RoutedEventArgs e)
        {
            // 防抖处理
            _debounceCts?.Cancel();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;

            try
            {
                await Task.Delay(150, token);
                if (token.IsCancellationRequested) return;

                ApplyChanges();
            }
            catch (TaskCanceledException)
            {
                // 忽略取消
            }
        }

        private void ApplyChanges()
        {
            if (DataContext is not CommandNode node) return;

            System.Diagnostics.Debug.WriteLine($"[IfCommandProperties] ApplyChanges called for node: {node.DisplayName}");

            // 规范化值
            NormalizeValues(node);

            // 更新条件预览
            UpdateConditionPreview();

            // 生成条件表达式并存储
            GenerateConditionExpression(node);

            // 刷新IF节点控件显示
            RefreshIfNodeDisplay(node);
        }

        private void RefreshIfNodeDisplay(CommandNode node)
        {
            try
            {
                // 方法1：直接刷新NodeControl（如果存在）
                if (node.NodeControl is IfNodeControlView ifControl)
                {
                    System.Diagnostics.Debug.WriteLine("[IfCommandProperties] Found IfNodeControlView directly, refreshing...");

                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ifControl.RefreshConditionDisplay();
                    }), System.Windows.Threading.DispatcherPriority.Render);

                    return;
                }

                // 方法2：通过视觉树查找IfNodeControlView
                System.Diagnostics.Debug.WriteLine("[IfCommandProperties] NodeControl not found directly, searching visual tree...");

                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var ifNodeControl = FindIfNodeControlForNode(node);
                    if (ifNodeControl != null)
                    {
                        System.Diagnostics.Debug.WriteLine("[IfCommandProperties] Found IfNodeControlView in visual tree, refreshing...");
                        ifNodeControl.RefreshConditionDisplay();
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[IfCommandProperties] IfNodeControlView not found in visual tree");

                        // 方法3：重新设置DataContext来触发绑定刷新
                        RefreshViaDataContext(node);
                    }
                }), System.Windows.Threading.DispatcherPriority.Render);

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IfCommandProperties] RefreshIfNodeDisplay error: {ex.Message}");
            }
        }

        private void RefreshViaDataContext(CommandNode node)
        {
            try
            {
                // 查找包含这个节点的所有控件并刷新它们的DataContext
                var ifNodeControls = FindAllIfNodeControls();
                foreach (var control in ifNodeControls)
                {
                    if (control.DataContext == node)
                    {
                        System.Diagnostics.Debug.WriteLine("[IfCommandProperties] Refreshing via DataContext reset");
                        var temp = control.DataContext;
                        control.DataContext = null;
                        control.UpdateLayout();
                        control.DataContext = temp;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IfCommandProperties] RefreshViaDataContext error: {ex.Message}");
            }
        }

        private IfNodeControlView FindIfNodeControlForNode(CommandNode targetNode)
        {
            try
            {
                // 从当前窗口开始查找
                var mainWindow = Application.Current.MainWindow;
                if (mainWindow != null)
                {
                    var ifControls = FindVisualChildren<IfNodeControlView>(mainWindow);
                    foreach (var ifControl in ifControls)
                    {
                        if (ifControl.DataContext == targetNode)
                        {
                            return ifControl;
                        }
                    }
                }

                // 如果主窗口没找到，尝试从当前控件的父级查找
                var parent = FindVisualParent<Window>(this);
                if (parent != null && parent != mainWindow)
                {
                    var ifControls = FindVisualChildren<IfNodeControlView>(parent);
                    foreach (var ifControl in ifControls)
                    {
                        if (ifControl.DataContext == targetNode)
                        {
                            return ifControl;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IfCommandProperties] FindIfNodeControlForNode error: {ex.Message}");
            }

            return null;
        }

        private List<IfNodeControlView> FindAllIfNodeControls()
        {
            var controls = new List<IfNodeControlView>();

            try
            {
                // 从主窗口查找
                var mainWindow = Application.Current.MainWindow;
                if (mainWindow != null)
                {
                    controls.AddRange(FindVisualChildren<IfNodeControlView>(mainWindow));
                }

                // 从当前窗口查找（如果不同于主窗口）
                var currentWindow = FindVisualParent<Window>(this);
                if (currentWindow != null && currentWindow != mainWindow)
                {
                    controls.AddRange(FindVisualChildren<IfNodeControlView>(currentWindow));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IfCommandProperties] FindAllIfNodeControls error: {ex.Message}");
            }

            return controls;
        }

        private void GenerateConditionExpression(CommandNode node)
        {
            if (node.LogicCommand?.Properties == null) return;

            try
            {
                var leftOperand = node.LogicCommand.Properties.ContainsKey("LeftOperand")
                    ? node.LogicCommand.Properties["LeftOperand"]?.ToString() ?? "A"
                    : "A";

                var operatorStr = node.LogicCommand.Properties.ContainsKey("Operator")
                    ? node.LogicCommand.Properties["Operator"]?.ToString() ?? "Equal"
                    : "Equal";

                var rightOperand = node.LogicCommand.Properties.ContainsKey("RightOperand")
                    ? node.LogicCommand.Properties["RightOperand"]?.ToString() ?? "B"
                    : "B";

                // 生成条件表达式字符串（用于执行引擎）
                string condition = GenerateConditionString(leftOperand, operatorStr, rightOperand);

                // 将生成的条件存储到Properties中（执行引擎可能会用到）
                node.LogicCommand.Properties["Condition"] = condition;

                System.Diagnostics.Debug.WriteLine($"[IfCommandProperties] Generated condition: {condition}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IfCommandProperties] GenerateConditionExpression error: {ex.Message}");
            }
        }

        private string GenerateConditionString(string left, string op, string right)
        {
            // 根据操作符生成条件字符串
            return op switch
            {
                "Equal" => $"{left} == {right}",
                "NotEqual" => $"{left} != {right}",
                "GreaterThan" => $"{left} > {right}",
                "GreaterThanOrEqual" => $"{left} >= {right}",
                "LessThan" => $"{left} < {right}",
                "LessThanOrEqual" => $"{left} <= {right}",
                "Contains" => $"{left}.Contains({right})",
                "NotContains" => $"!{left}.Contains({right})",
                "StartsWith" => $"{left}.StartsWith({right})",
                "EndsWith" => $"{left}.EndsWith({right})",
                _ => $"{left} == {right}"
            };
        }

        private void UpdateConditionPreview()
        {
            if (DataContext is not CommandNode node) return;
            if (node.LogicCommand?.Properties == null) return;

            try
            {
                var leftOperand = node.LogicCommand.Properties.ContainsKey("LeftOperand")
                    ? node.LogicCommand.Properties["LeftOperand"]?.ToString() ?? "A"
                    : "A";

                var operatorStr = node.LogicCommand.Properties.ContainsKey("Operator")
                    ? node.LogicCommand.Properties["Operator"]?.ToString() ?? "Equal"
                    : "Equal";

                var rightOperand = node.LogicCommand.Properties.ContainsKey("RightOperand")
                    ? node.LogicCommand.Properties["RightOperand"]?.ToString() ?? "B"
                    : "B";

                // 生成预览文本
                string previewText = GetOperatorDisplay(operatorStr) switch
                {
                    "等于 (==)" => $"如果 {leftOperand} 等于 {rightOperand}",
                    "不等于 (!=)" => $"如果 {leftOperand} 不等于 {rightOperand}",
                    "大于 (>)" => $"如果 {leftOperand} 大于 {rightOperand}",
                    "大于等于 (>=)" => $"如果 {leftOperand} 大于等于 {rightOperand}",
                    "小于 (<)" => $"如果 {leftOperand} 小于 {rightOperand}",
                    "小于等于 (<=)" => $"如果 {leftOperand} 小于等于 {rightOperand}",
                    "包含" => $"如果 {leftOperand} 包含 {rightOperand}",
                    "不包含" => $"如果 {leftOperand} 不包含 {rightOperand}",
                    "开始于" => $"如果 {leftOperand} 开始于 {rightOperand}",
                    "结束于" => $"如果 {leftOperand} 结束于 {rightOperand}",
                    _ => $"如果 {leftOperand} {operatorStr} {rightOperand}"
                };

                if (ConditionPreview != null)
                {
                    ConditionPreview.Text = previewText;
                }

                System.Diagnostics.Debug.WriteLine($"[IfCommandProperties] Updated condition preview: {previewText}");
            }
            catch (Exception ex)
            {
                if (ConditionPreview != null)
                {
                    ConditionPreview.Text = "条件预览生成失败";
                }
                System.Diagnostics.Debug.WriteLine($"[IfCommandProperties] UpdateConditionPreview error: {ex.Message}");
            }
        }

        private string GetOperatorDisplay(string operatorValue)
        {
            return operatorValue switch
            {
                "Equal" => "等于 (==)",
                "NotEqual" => "不等于 (!=)",
                "GreaterThan" => "大于 (>)",
                "GreaterThanOrEqual" => "大于等于 (>=)",
                "LessThan" => "小于 (<)",
                "LessThanOrEqual" => "小于等于 (<=)",
                "Contains" => "包含",
                "NotContains" => "不包含",
                "StartsWith" => "开始于",
                "EndsWith" => "结束于",
                _ => "等于 (==)"
            };
        }

        private void NormalizeValues(CommandNode node)
        {
            if (node.LogicCommand?.Properties == null) return;

            try
            {
                // 确保左操作数不为空
                if (!node.LogicCommand.Properties.ContainsKey("LeftOperand") ||
                    string.IsNullOrWhiteSpace(node.LogicCommand.Properties["LeftOperand"]?.ToString()))
                {
                    node.LogicCommand.Properties["LeftOperand"] = "A";
                }

                // 确保操作符有效
                var validOperators = new[] {
                    "Equal", "NotEqual", "GreaterThan", "GreaterThanOrEqual",
                    "LessThan", "LessThanOrEqual", "Contains", "NotContains",
                    "StartsWith", "EndsWith"
                };

                if (node.LogicCommand.Properties.ContainsKey("Operator"))
                {
                    var op = node.LogicCommand.Properties["Operator"]?.ToString();
                    if (!validOperators.Contains(op))
                    {
                        node.LogicCommand.Properties["Operator"] = "Equal";
                    }
                }
                else
                {
                    node.LogicCommand.Properties["Operator"] = "Equal";
                }

                // 确保右操作数不为空
                if (!node.LogicCommand.Properties.ContainsKey("RightOperand") ||
                    string.IsNullOrWhiteSpace(node.LogicCommand.Properties["RightOperand"]?.ToString()))
                {
                    node.LogicCommand.Properties["RightOperand"] = "B";
                }

                // 确保描述不为空
                if (!node.LogicCommand.Properties.ContainsKey("Description") ||
                    string.IsNullOrWhiteSpace(node.LogicCommand.Properties["Description"]?.ToString()))
                {
                    node.LogicCommand.Properties["Description"] = "条件判断";
                }

                System.Diagnostics.Debug.WriteLine($"[IfCommandProperties] Normalized values - Left: {node.LogicCommand.Properties["LeftOperand"]}, Op: {node.LogicCommand.Properties["Operator"]}, Right: {node.LogicCommand.Properties["RightOperand"]}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IfCommandProperties] NormalizeValues error: {ex.Message}");
            }
        }
        private void SelectLeftVariable_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var flowViewModel = FindFlowViewModel();
                IEnumerable<CommandNode> allNodes = null;

                if (flowViewModel != null)
                {
                    allNodes = flowViewModel.CommandNodes;
                }
                _eventAggregator = ContainerLocator.Container.Resolve<IEventAggregator>();
                var variableWindow = new VariableManagerWindow( _eventAggregator, allNodes);
                variableWindow.Owner = Window.GetWindow(this);

                if (variableWindow.ShowDialog() == true && variableWindow.SelectedVariable != null)
                {
                    var selectedVar = variableWindow.SelectedVariable;
                    LeftOperandBox.Text = selectedVar.FullVariableName;
                    OnValueChanged(LeftOperandBox, null);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"选择变量失败: {ex.Message}");
                MessageBox.Show("选择变量时发生错误", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SelectRightVariable_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var flowViewModel = FindFlowViewModel();
                IEnumerable<CommandNode> allNodes = null;

                if (flowViewModel != null)
                {
                    allNodes = flowViewModel.CommandNodes;
                }
                _eventAggregator = ContainerLocator.Container.Resolve<IEventAggregator>();
                // 打开变量管理窗口
                var variableWindow = new VariableManagerWindow(_eventAggregator, allNodes);
                variableWindow.Owner = Window.GetWindow(this);

                // 可以设置窗口标题以区分是选择右操作数
                variableWindow.Title = "选择右操作数变量";

                if (variableWindow.ShowDialog() == true && variableWindow.SelectedVariable != null)
                {
                    var selectedVar = variableWindow.SelectedVariable;

                    // 设置右操作数的值
                    RightOperandBox.Text = selectedVar.FullVariableName;

                    // 触发值变更事件
                    OnValueChanged(RightOperandBox, null);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"选择右操作数变量失败: {ex.Message}");
                MessageBox.Show("选择变量时发生错误", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        private FlowDesignerViewModel FindFlowViewModel()
        {
            DependencyObject parent = this;
            while (parent != null)
            {
                if (parent is FlowDesignerView view && view.DataContext is FlowDesignerViewModel vm)
                    return vm;
                parent = VisualTreeHelper.GetParent(parent);
            }
            return null;
        }

        // 辅助方法：查找视觉树中的子元素
        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
        {
            if (depObj != null)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
                {
                    DependencyObject child = VisualTreeHelper.GetChild(depObj, i);
                    if (child != null && child is T)
                    {
                        yield return (T)child;
                    }

                    foreach (T childOfChild in FindVisualChildren<T>(child))
                    {
                        yield return childOfChild;
                    }
                }
            }
        }

        // 辅助方法：查找视觉树中的父元素
        private static T FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parentObject = VisualTreeHelper.GetParent(child);

            if (parentObject == null) return null;

            T parent = parentObject as T;
            if (parent != null)
            {
                return parent;
            }
            else
            {
                return FindVisualParent<T>(parentObject);
            }
        }
    }
}