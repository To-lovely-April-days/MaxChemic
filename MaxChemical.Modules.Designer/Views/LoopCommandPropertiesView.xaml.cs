using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using Prism.Events;
using Prism.Ioc;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class LoopCommandPropertiesView : UserControl
    {
        private readonly ILogService _logger = new LogService().ForContext<LoopCommandPropertiesView>();
        private IEventAggregator _eventAggregator;

        // 添加变量管理器窗口管理
        private static VariableManagerWindow _sharedVariableWindow;
        private static readonly object _windowLock = new object();

        public LoopCommandPropertiesView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Loaded += OnLoaded;
        }

        private CommandNode CurrentNode => DataContext as CommandNode;

        #region 依赖属性（统一双向绑定入口）

        public static readonly DependencyProperty LoopTypeNormalizedProperty =
            DependencyProperty.Register(nameof(LoopTypeNormalized), typeof(LoopType), typeof(LoopCommandPropertiesView),
                new PropertyMetadata(LoopType.FixedCount, OnLoopTypeNormalizedChanged));

        public LoopType LoopTypeNormalized
        {
            get => (LoopType)GetValue(LoopTypeNormalizedProperty);
            set => SetValue(LoopTypeNormalizedProperty, value);
        }

        private static void OnLoopTypeNormalizedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (LoopCommandPropertiesView)d;
            view.WriteBack("LoopType", e.NewValue);
            view.UpdateConditionPreviewText();
            view.UpdateLoopSummaryText();
            view.RefreshLoopNodeDisplay();
        }

        public static readonly DependencyProperty LoopCountProperty =
            DependencyProperty.Register(nameof(LoopCount), typeof(int), typeof(LoopCommandPropertiesView),
                new PropertyMetadata(0, OnLoopCountChanged, CoerceLoopCount));

        public int LoopCount
        {
            get => (int)GetValue(LoopCountProperty);
            set => SetValue(LoopCountProperty, value);
        }

        private static object CoerceLoopCount(DependencyObject d, object baseValue)
        {
            if (baseValue is int i && i >= 0) return i;
            return 0;
        }

        private static void OnLoopCountChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (LoopCommandPropertiesView)d;
            view.WriteBack("LoopCount", e.NewValue);
            view.UpdateLoopSummaryText();
            view.RefreshLoopNodeDisplay();
        }

        public static readonly DependencyProperty LeftOperandProperty =
            DependencyProperty.Register(nameof(LeftOperand), typeof(string), typeof(LoopCommandPropertiesView),
                new PropertyMetadata("A", OnLeftOperandChanged));

        public string LeftOperand
        {
            get => (string)GetValue(LeftOperandProperty);
            set => SetValue(LeftOperandProperty, value);
        }

        private static void OnLeftOperandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (LoopCommandPropertiesView)d;
            view.WriteBack("LeftOperand", e.NewValue ?? "");
            view.UpdateConditionPreviewText();
        }

        public static readonly DependencyProperty OperatorNormalizedProperty =
            DependencyProperty.Register(nameof(OperatorNormalized), typeof(ComparisonOperator), typeof(LoopCommandPropertiesView),
                new PropertyMetadata(ComparisonOperator.Equal, OnOperatorNormalizedChanged));

        public ComparisonOperator OperatorNormalized
        {
            get => (ComparisonOperator)GetValue(OperatorNormalizedProperty);
            set => SetValue(OperatorNormalizedProperty, value);
        }

        private static void OnOperatorNormalizedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (LoopCommandPropertiesView)d;
            view.WriteBack("Operator", e.NewValue);
            view.UpdateConditionPreviewText();
        }

        public static readonly DependencyProperty RightOperandProperty =
            DependencyProperty.Register(nameof(RightOperand), typeof(string), typeof(LoopCommandPropertiesView),
                new PropertyMetadata("B", OnRightOperandChanged));

        public string RightOperand
        {
            get => (string)GetValue(RightOperandProperty);
            set => SetValue(RightOperandProperty, value);
        }

        private static void OnRightOperandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (LoopCommandPropertiesView)d;
            view.WriteBack("RightOperand", e.NewValue ?? "");
            view.UpdateConditionPreviewText();
        }

        public static readonly DependencyProperty DescriptionProperty =
            DependencyProperty.Register(nameof(Description), typeof(string), typeof(LoopCommandPropertiesView),
                new PropertyMetadata("循环执行", OnDescriptionChanged));

        public string Description
        {
            get => (string)GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (LoopCommandPropertiesView)d;
            view.WriteBack("Description", e.NewValue ?? "");
            view.RefreshLoopNodeDisplay();
        }

        public static readonly DependencyProperty WaitTimeProperty =
            DependencyProperty.Register(nameof(WaitTime), typeof(int), typeof(LoopCommandPropertiesView),
                new PropertyMetadata(10, OnWaitTimeChanged, CoerceWaitTime));

        public int WaitTime
        {
            get => (int)GetValue(WaitTimeProperty);
            set => SetValue(WaitTimeProperty, value);
        }

        private static object CoerceWaitTime(DependencyObject d, object baseValue)
        {
            if (baseValue is int i && i >= 0) return i;
            return 0;
        }

        private static void OnWaitTimeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (LoopCommandPropertiesView)d;
            view.WriteBack("WaitTime", e.NewValue);
            view.UpdateLoopSummaryText();
            view.RefreshLoopNodeDisplay();
        }

        public static readonly DependencyProperty ConditionPreviewTextProperty =
            DependencyProperty.Register(nameof(ConditionPreviewText), typeof(string), typeof(LoopCommandPropertiesView),
                new PropertyMetadata(string.Empty));

        public string ConditionPreviewText
        {
            get => (string)GetValue(ConditionPreviewTextProperty);
            set => SetValue(ConditionPreviewTextProperty, value);
        }

        public static readonly DependencyProperty LoopSummaryTextProperty =
            DependencyProperty.Register(nameof(LoopSummaryText), typeof(string), typeof(LoopCommandPropertiesView),
                new PropertyMetadata(string.Empty));

        public string LoopSummaryText
        {
            get => (string)GetValue(LoopSummaryTextProperty);
            set => SetValue(LoopSummaryTextProperty, value);
        }

        public static readonly DependencyProperty IsDoWhileProperty =
            DependencyProperty.Register(nameof(IsDoWhile), typeof(bool), typeof(LoopCommandPropertiesView),
                new PropertyMetadata(false, OnIsDoWhileChanged));

        public bool IsDoWhile
        {
            get => (bool)GetValue(IsDoWhileProperty);
            set => SetValue(IsDoWhileProperty, value);
        }

        private static void OnIsDoWhileChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (LoopCommandPropertiesView)d;
            view.WriteBack("IsDoWhile", e.NewValue);
            view.UpdateConditionPreviewText();
            view.UpdateLoopSummaryText();
            view.RefreshLoopNodeDisplay();
        }

        #endregion

        #region 生命周期

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Dispatcher.BeginInvoke(SyncFromNode, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue != null)
            {
                
                Dispatcher.BeginInvoke(SyncFromNode, System.Windows.Threading.DispatcherPriority.DataBind);
            }
        }

        #endregion

        #region 同步/写回

        private void SyncFromNode()
        {
            try
            {
                var node = CurrentNode;
                if (node?.LogicCommand?.Properties == null) return;

                var p = node.LogicCommand.Properties;

                // 确保默认值
                if (!p.ContainsKey("LoopType")) p["LoopType"] = LoopType.FixedCount;
                if (!p.ContainsKey("Operator")) p["Operator"] = ComparisonOperator.Equal;

                // 🔥 关键：延迟设置UI值
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    LoopTypeNormalized = ReadLoopType(p["LoopType"]);
                    OperatorNormalized = ReadOperator(p["Operator"]);

                    // 其他属性...
                    LoopCount = SafeToInt(p["LoopCount"], 3);
                    LeftOperand = p["LeftOperand"]?.ToString() ?? "A";
                    RightOperand = p["RightOperand"]?.ToString() ?? "B";
                    Description = p["Description"]?.ToString() ?? "循环执行";
                    WaitTime = SafeToInt(p["WaitTime"], 10);
                    IsDoWhile = SafeToBool(p["IsDoWhile"], false);

                    UpdateConditionPreviewText();
                    UpdateLoopSummaryText();
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SyncFromNode failed");
            }
        }

        private void WriteBack(string key, object value)
        {
            try
            {
                var node = CurrentNode;
                if (node?.LogicCommand?.Properties == null) return;

                node.LogicCommand.Properties[key] = value ?? "";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WriteBack key={Key} failed", key);
            }
        }

        #endregion

        #region 预览与刷新

        private void UpdateConditionPreviewText()
        {
            if (LoopTypeNormalized != LoopType.Conditional)
            {
                ConditionPreviewText = string.Empty;
                return;
            }

            string operatorText = OperatorNormalized switch
            {
                ComparisonOperator.Equal => "等于",
                ComparisonOperator.NotEqual => "不等于",
                ComparisonOperator.GreaterThan => "大于",
                ComparisonOperator.GreaterThanOrEqual => "大于等于",
                ComparisonOperator.LessThan => "小于",
                ComparisonOperator.LessThanOrEqual => "小于等于",
                ComparisonOperator.Contains => "包含",
                ComparisonOperator.NotContains => "不包含",
                _ => "等于"
            };

            string loopMode = IsDoWhile ? "Do-While" : "While";
            string description = IsDoWhile
                ? $"【{loopMode}】先执行子节点，然后判断：如果 {LeftOperand} {operatorText} {RightOperand}，则继续循环"
                : $"【{loopMode}】先判断：如果 {LeftOperand} {operatorText} {RightOperand}，则执行子节点并继续循环";

            ConditionPreviewText = description;

            var node = CurrentNode;
            if (node?.LogicCommand?.Properties != null)
            {
                string condition = OperatorNormalized switch
                {
                    ComparisonOperator.Equal => $"{LeftOperand} == {RightOperand}",
                    ComparisonOperator.NotEqual => $"{LeftOperand} != {RightOperand}",
                    ComparisonOperator.GreaterThan => $"{LeftOperand} > {RightOperand}",
                    ComparisonOperator.GreaterThanOrEqual => $"{LeftOperand} >= {RightOperand}",
                    ComparisonOperator.LessThan => $"{LeftOperand} < {RightOperand}",
                    ComparisonOperator.LessThanOrEqual => $"{LeftOperand} <= {RightOperand}",
                    ComparisonOperator.Contains => $"{LeftOperand}.Contains({RightOperand})",
                    ComparisonOperator.NotContains => $"!{LeftOperand}.Contains({RightOperand})",
                    _ => $"{LeftOperand} == {RightOperand}"
                };
                node.LogicCommand.Properties["Condition"] = condition;
            }
        }

        private void UpdateLoopSummaryText()
        {
            string summary = LoopTypeNormalized switch
            {
                LoopType.FixedCount => $"固定循环 {LoopCount} 次，每次间隔 {WaitTime} 毫秒",
                LoopType.Conditional => IsDoWhile
                    ? $"Do-While 条件循环（先执行后判断），每次间隔 {WaitTime} 毫秒"
                    : $"While 条件循环（先判断后执行），每次间隔 {WaitTime} 毫秒",
                LoopType.Infinite => $"无限循环，每次间隔 {WaitTime} 毫秒",
                _ => $"循环执行，每次间隔 {WaitTime} 毫秒"
            };

            LoopSummaryText = summary;
        }

        private void RefreshLoopNodeDisplay()
        {
            try
            {
                var node = CurrentNode;
                if (node == null) return;

                if (node.NodeControl is LoopNodeControlView loopControl)
                {
                    var tmp = loopControl.DataContext;
                    loopControl.DataContext = null;
                    loopControl.UpdateLayout();
                    loopControl.DataContext = tmp;
                    return;
                }

                var mainWindow = Application.Current.MainWindow;
                foreach (var ctrl in FindVisualChildren<LoopNodeControlView>(mainWindow))
                {
                    if (ctrl.DataContext == node)
                    {
                        var tmp = ctrl.DataContext;
                        ctrl.DataContext = null;
                        ctrl.UpdateLayout();
                        ctrl.DataContext = tmp;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RefreshLoopNodeDisplay failed");
            }
        }

        #endregion

        #region 变量选择按钮

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
                        LeftOperand = selectedVar.FullVariableName;
                    }
                    else
                    {
                        RightOperand = selectedVar.FullVariableName;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "选择变量失败");
                MessageBox.Show("选择变量时发生错误", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region 工具与限制

        private static bool SafeToBool(object val, bool @default)
        {
            try
            {
                if (val is bool b) return b;
                if (val is string s)
                {
                    if (bool.TryParse(s, out var result)) return result;
                    if (s == "1") return true;
                    if (s == "0") return false;
                }
                if (val is int i) return i != 0;
                if (val is double d) return d != 0.0;
            }
            catch { }
            return @default;
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

        private static int SafeToInt(object val, int @default)
        {
            try
            {
                if (val is int i) return i;
                if (val is string s && int.TryParse(s, out var j)) return j;
                if (val != null)
                {
                    var conv = System.Convert.ToInt32(val, CultureInfo.InvariantCulture);
                    return conv;
                }
            }
            catch { }
            return @default;
        }

        private static LoopType ReadLoopType(object val)
        {
            if (val is LoopType lt) return lt;
            if (val is string s && Enum.TryParse<LoopType>(s, out var p)) return p;

            // 直接转换整数，不用IsDefined检查
            if (val is int i)
            {
                if (i >= 0 && i <= 2) // LoopType有3个值：0,1,2
                    return (LoopType)i;
            }

            // 兼容其他数值类型
            try
            {
                var intVal = Convert.ToInt32(val);
                if (intVal >= 0 && intVal <= 2)
                    return (LoopType)intVal;
            }
            catch { }

            return LoopType.FixedCount;
        }

        private static ComparisonOperator ReadOperator(object val)
        {
            if (val is ComparisonOperator op) return op;
            if (val is string s && Enum.TryParse<ComparisonOperator>(s, out var p)) return p;

            // 直接转换整数
            if (val is int i)
            {
                if (i >= 0 && i <= 7) // ComparisonOperator有8个值：0-7
                    return (ComparisonOperator)i;
            }

            //兼容其他数值类型
            try
            {
                var intVal = Convert.ToInt32(val);
                if (intVal >= 0 && intVal <= 7)
                    return (ComparisonOperator)intVal;
            }
            catch { }

            return ComparisonOperator.Equal;
        }

        

        private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(DependencyObject depObj) where T : DependencyObject
        {
            if (depObj == null) yield break;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
            {
                var child = VisualTreeHelper.GetChild(depObj, i);
                if (child is T match) yield return match;
                foreach (T sub in FindVisualChildren<T>(child)) yield return sub;
            }
        }

        private static readonly Regex NumericRegex = new Regex("^[0-9]+$", RegexOptions.Compiled);

        private void LoopCountBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !NumericRegex.IsMatch(e.Text);
        }

        private void LoopCountBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(typeof(string)))
            {
                e.CancelCommand();
                return;
            }
            var text = (string)e.DataObject.GetData(typeof(string));
            if (!NumericRegex.IsMatch(text)) e.CancelCommand();
        }

        private void WaitTimeBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !NumericRegex.IsMatch(e.Text);
        }

        private void WaitTimeBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(typeof(string)))
            {
                e.CancelCommand();
                return;
            }
            var text = (string)e.DataObject.GetData(typeof(string));
            if (!NumericRegex.IsMatch(text)) e.CancelCommand();
        }

        #endregion
    }
}