using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using Prism.Events;
using Prism.Ioc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class IfCommandPropertiesView : UserControl
    {
        private CancellationTokenSource _debounceCts;
        private IEventAggregator _eventAggregator;

        // 添加变量管理器窗口管理
        private static VariableManagerWindow _sharedVariableWindow;
        private static readonly object _windowLock = new object();

        public IfCommandPropertiesView()
        {
            InitializeComponent();

            _eventAggregator = ContainerLocator.Container.Resolve<IEventAggregator>();
            _eventAggregator?.GetEvent<LanguageChangedEvent>()?.Subscribe(OnLanguageChanged);

            this.DataContextChanged += OnDataContextChanged;
            this.Loaded += OnLoaded;
        }

        /// <summary>
        /// 本地化
        /// </summary>
        /// <param name="s"></param>
        private void OnLanguageChanged(string s)
        {
            ILocalizationService service = ContainerLocator.Container.Resolve<ILocalizationService>();
            if (service is null)
            {
                return;
            }

            NodeTitle.Text = service.GetString("Logic_Title");
            OperatorTitle.Text = service.GetString("Logic_Op_Title");
            LeftOpLabel.Text = service.GetString("Logic_Op_Llabel");
            RightOpLabel.Text = service.GetString("Logic_Op_Rlabel");
            LeftButton.Content = service.GetString("Logic_Op_btnlabel");
            RightButton.Content = service.GetString("Logic_Op_btnlabel");
            LeftOperandBox.ToolTip = service.GetString("Logic_Op_Ltip");
            RightOperandBox.ToolTip = service.GetString("Logic_Op_Rtip");
            OperatorLabel.Text = service.GetString("Logic_Compar");
            PreviewLabel.Text = service.GetString("Logic_Preview");
            UseDescLabel.Text = service.GetString("Logic_Use_Title");
            Desc1.Text = service.GetString("Logic_Use_Desc1");
            Desc2.Text = service.GetString("Logic_Use_Desc2");
            Desc3.Text = service.GetString("Logic_Use_Desc3");
            Desc4.Text = service.GetString("Logic_Use_Desc4");
        }


        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateConditionPreview();
            SyncOperatorRadio(); // ← 新增

            OnLanguageChanged("");
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (DataContext is CommandNode node && node.LogicCommand != null)
            {
                EnsureDefaultValues(node);
                UpdateConditionPreview();
                SyncOperatorRadio();
            }
        }
        private void SyncOperatorRadio()
        {
            if (DataContext is not CommandNode node) return;
            var op = node.LogicCommand?.Properties?
                         .GetValueOrDefault("Operator")?.ToString() ?? "Equal";

            foreach (var rb in new[]
            {
        OpEqual, OpNotEqual, OpGt, OpLt, OpGte, OpLte
    })
            {
                if (rb != null)
                    rb.IsChecked = rb.Tag?.ToString() == op;
            }
        }

        private void EnsureDefaultValues(CommandNode node)
        {
            if (node.LogicCommand?.Properties == null) return;

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

            NormalizeValues(node);
            UpdateConditionPreview();
            GenerateConditionExpression(node);
            RefreshIfNodeDisplay(node);
        }

        private void RefreshIfNodeDisplay(CommandNode node)
        {
            try
            {
                if (node.NodeControl is IfNodeControlView ifControl)
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        ifControl.RefreshConditionDisplay();
                    }), System.Windows.Threading.DispatcherPriority.Render);
                    return;
                }

                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var ifNodeControl = FindIfNodeControlForNode(node);
                    if (ifNodeControl != null)
                    {
                        ifNodeControl.RefreshConditionDisplay();
                    }
                    else
                    {
                        RefreshViaDataContext(node);
                    }
                }), System.Windows.Threading.DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                // 静默处理异常
            }
        }

        private void RefreshViaDataContext(CommandNode node)
        {
            try
            {
                var ifNodeControls = FindAllIfNodeControls();
                foreach (var control in ifNodeControls)
                {
                    if (control.DataContext == node)
                    {
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
                // 静默处理异常
            }
        }

        private IfNodeControlView FindIfNodeControlForNode(CommandNode targetNode)
        {
            try
            {
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
                // 静默处理异常
            }

            return null;
        }

        private List<IfNodeControlView> FindAllIfNodeControls()
        {
            var controls = new List<IfNodeControlView>();

            try
            {
                var mainWindow = Application.Current.MainWindow;
                if (mainWindow != null)
                {
                    controls.AddRange(FindVisualChildren<IfNodeControlView>(mainWindow));
                }

                var currentWindow = FindVisualParent<Window>(this);
                if (currentWindow != null && currentWindow != mainWindow)
                {
                    controls.AddRange(FindVisualChildren<IfNodeControlView>(currentWindow));
                }
            }
            catch (Exception ex)
            {
                // 静默处理异常
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

                string condition = GenerateConditionString(leftOperand, operatorStr, rightOperand);
                node.LogicCommand.Properties["Condition"] = condition;
            }
            catch (Exception ex)
            {
                // 静默处理异常
            }
        }

        private string GenerateConditionString(string left, string op, string right)
        {
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
            }
            catch (Exception ex)
            {
                if (ConditionPreview != null)
                {
                    ConditionPreview.Text = "条件预览生成失败";
                }
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
                if (!node.LogicCommand.Properties.ContainsKey("LeftOperand") ||
                    string.IsNullOrWhiteSpace(node.LogicCommand.Properties["LeftOperand"]?.ToString()))
                {
                    node.LogicCommand.Properties["LeftOperand"] = "A";
                }

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

                if (!node.LogicCommand.Properties.ContainsKey("RightOperand") ||
                    string.IsNullOrWhiteSpace(node.LogicCommand.Properties["RightOperand"]?.ToString()))
                {
                    node.LogicCommand.Properties["RightOperand"] = "B";
                }

                if (!node.LogicCommand.Properties.ContainsKey("Description") ||
                    string.IsNullOrWhiteSpace(node.LogicCommand.Properties["Description"]?.ToString()))
                {
                    node.LogicCommand.Properties["Description"] = "条件判断";
                }
            }
            catch (Exception ex)
            {
                // 静默处理异常
            }
        }

        private void SelectLeftVariable_Click(object sender, RoutedEventArgs e)
        {
            ShowVariableSelector("LeftOperand");
        }

        private void SelectRightVariable_Click(object sender, RoutedEventArgs e)
        {
            ShowVariableSelector("RightOperand");
        }

        private void ShowVariableSelector(string operandType)
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

                VariableManagerWindow variableWindow = null;

                lock (_windowLock)
                {
                    if (_sharedVariableWindow != null)
                    {
                        try
                        {
                            if (_sharedVariableWindow.IsLoaded && !_sharedVariableWindow.IsClosed)
                            {
                                variableWindow = _sharedVariableWindow;
                                variableWindow.RefreshData(allNodes);
                                variableWindow.WindowState = WindowState.Normal;
                                variableWindow.Activate();
                            }
                            else
                            {
                                _sharedVariableWindow = null;
                            }
                        }
                        catch
                        {
                            _sharedVariableWindow = null;
                        }
                    }

                    if (variableWindow == null)
                    {
                        variableWindow = new VariableManagerWindow(_eventAggregator, allNodes);
                        _sharedVariableWindow = variableWindow;

                        variableWindow.Closed += (s, e) =>
                        {
                            lock (_windowLock)
                            {
                                if (_sharedVariableWindow == s)
                                {
                                    _sharedVariableWindow = null;
                                }
                            }
                        };
                    }
                }

                variableWindow.Owner = Window.GetWindow(this);
                variableWindow.Title = operandType == "LeftOperand" ? "选择左操作数变量" : "选择右操作数变量";

                if (variableWindow.ShowDialog() == true && variableWindow.SelectedVariable != null)
                {
                    var selectedVar = variableWindow.SelectedVariable;

                    if (operandType == "LeftOperand")
                    {
                        LeftOperandBox.Text = selectedVar.FullVariableName;
                        OnValueChanged(LeftOperandBox, null);
                    }
                    else
                    {
                        RightOperandBox.Text = selectedVar.FullVariableName;
                        OnValueChanged(RightOperandBox, null);
                    }
                }
            }
            catch (Exception ex)
            {
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
        private void Operator_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb
                && DataContext is CommandNode node
                && node.LogicCommand?.Properties != null)
            {
                node.LogicCommand.Properties["Operator"] = rb.Tag?.ToString() ?? "Equal";
                ApplyChanges();
            }
        }
    }
}