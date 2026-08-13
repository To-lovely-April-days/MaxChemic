using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using Prism.Events;
using Prism.Ioc;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class LoopCommandPropertiesView : UserControl
    {
        private readonly ILogService _logger = new LogService().ForContext<LoopCommandPropertiesView>();
        private IEventAggregator _eventAggregator;
        ILocalizationService _localization;

        // 添加变量管理器窗口管理
        private static VariableManagerWindow _sharedVariableWindow;
        private static readonly object _windowLock = new object();

        public LoopCommandPropertiesView()
        {
            InitializeComponent();

            _localization = ContainerLocator.Container.Resolve<ILocalizationService>();
            _eventAggregator = ContainerLocator.Container.Resolve<IEventAggregator>();
            _eventAggregator?.GetEvent<LanguageChangedEvent>()?.Subscribe(OnLanguageChanged);
            OnLanguageChanged("");

            DataContextChanged += OnDataContextChanged;
            Loaded += OnLoaded;
        }

        /// <summary>
        /// 本地化
        /// </summary>
        private void OnLanguageChanged(string s)
        {
            NodeTitle.Text = _localization.GetString("Loop_Title");
            LoopTypeTitle.Text = _localization.GetString("Loop_Type_Title");
            RbFixed.Content = _localization.GetString("Loop_Type_Fixed");
            RbConditionLoop.Content = _localization.GetString("Loop_Type_Condition");
            RbInfiniteLoop.Content = _localization.GetString("Loop_Type_Infinite");
            LoopParamTitle.Text = _localization.GetString("Loop_Param_Title");
            LoopParamLabel.Text = _localization.GetString("Loop_Param_Number");
            LoopCountUnitLabel.Text = _localization.GetString("Loop_Param_Numberunit");
            LoopConditionTitle.Text = _localization.GetString("Loop_Condition_Title");
            LoopModeLabel.Text = _localization.GetString("Loop_Condition_Mode");
            IsDoWhileCheckBox.Content = _localization.GetString("Loop_Condition_Mode_Dowhile");
            LeftOpLabel.Text = _localization.GetString("Loop_Condition_Leftoperand");
            OperatorLabel.Text = _localization.GetString("Loop_Condition_Operator");
            RightOpLabel.Text = _localization.GetString("Loop_Condition_Rightoperand");
            LeftOperandBox.ToolTip = _localization.GetString("Loop_Condition_Lefttip");
            RightOperandBox.ToolTip = _localization.GetString("Loop_Condition_Righttip");
            LeftVarButton.Content = _localization.GetString("Loop_Condition_VarLabel");
            RightVarButton.Content = _localization.GetString("Loop_Condition_VarLabel");
            LoopIntervalLabel.Text = _localization.GetString("Loop_Interval_Title");
            TimeUnitLabel.Text = _localization.GetString("Loop_Interval_Unit");
            DescriptionLabel.Text = _localization.GetString("Loop_Description");
            ConditionPreviewLabel.Text = _localization.GetString("Loop_Preview_Title");
            AbstractLabel.Text = _localization.GetString("Loop_Abstract_Title");
            AttentionLabel.Text = _localization.GetString("Loop_Attention_Title");
            AttentionText.Text = _localization.GetString("Loop_Attention_Text");
            ExecModeTb.Text = _localization.GetString("WaitNode_ExecuteMode_Title");
            RbExecSync.Content = _localization.GetString("WaitNode_Sync");
            RbExecAsync.Content = _localization.GetString("WaitNode_Async");
            AsyncModeTip.Text = _localization.GetString("Loop_AsyncMode_Tip");
            TbTolerance.Text = _localization.GetString("Loop_Tolerance_set");
            RbDisable.Content = _localization.GetString("Loop_Tolerance_Disable");
            RbRela.Content = _localization.GetString("Loop_Tolerance_Relative");
            RbAbso.Content = _localization.GetString("Loop_Tolerance_Absolute");

            UpdateLoopSummaryText();
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

            // 防呆：切到无限循环时，自动把异步拉回同步
            if (e.NewValue is LoopType lt && lt == LoopType.Infinite
                && view.ExecutionModeNormalized == ExecutionMode.Asynchronous)
            {
                view.ExecutionModeNormalized = ExecutionMode.Synchronous;
            }

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

        // ★ 新增：执行模式依赖属性（同步/异步）
        public static readonly DependencyProperty ExecutionModeNormalizedProperty =
            DependencyProperty.Register(
                nameof(ExecutionModeNormalized),
                typeof(ExecutionMode),
                typeof(LoopCommandPropertiesView),
                new PropertyMetadata(ExecutionMode.Synchronous, OnExecutionModeChanged));

        public ExecutionMode ExecutionModeNormalized
        {
            get => (ExecutionMode)GetValue(ExecutionModeNormalizedProperty);
            set => SetValue(ExecutionModeNormalizedProperty, value);
        }

        private static void OnExecutionModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (LoopCommandPropertiesView)d;
            view.WriteBack("ExecutionMode", e.NewValue);
            view.UpdateLoopSummaryText();
            view.RefreshLoopNodeDisplay();
        }
        // ★ 新增：容差模式依赖属性
        public static readonly DependencyProperty ToleranceModeNormalizedProperty =
            DependencyProperty.Register(
                nameof(ToleranceModeNormalized),
                typeof(ToleranceMode),
                typeof(LoopCommandPropertiesView),
                new PropertyMetadata(ToleranceMode.None, OnToleranceModeChanged));

        public ToleranceMode ToleranceModeNormalized
        {
            get => (ToleranceMode)GetValue(ToleranceModeNormalizedProperty);
            set => SetValue(ToleranceModeNormalizedProperty, value);
        }

        private static void OnToleranceModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (LoopCommandPropertiesView)d;
            view.WriteBack("ToleranceMode", e.NewValue);
            view.UpdateConditionPreviewText();
            view.UpdateLoopSummaryText();
        }

        // ★ 新增：容差数值依赖属性（UI 显示用）
        // 绝对模式：等于实际值
        // 相对模式：以百分比展示（如显示 5 表示 5%，存储时存 0.05）
        public static readonly DependencyProperty ToleranceValueDisplayProperty =
            DependencyProperty.Register(
                nameof(ToleranceValueDisplay),
                typeof(double),
                typeof(LoopCommandPropertiesView),
                new PropertyMetadata(0.0, OnToleranceValueDisplayChanged, CoerceToleranceValue));

        public double ToleranceValueDisplay
        {
            get => (double)GetValue(ToleranceValueDisplayProperty);
            set => SetValue(ToleranceValueDisplayProperty, value);
        }

        private static object CoerceToleranceValue(DependencyObject d, object baseValue)
        {
            if (baseValue is double v && v >= 0) return v;
            return 0.0;
        }

        private static void OnToleranceValueDisplayChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var view = (LoopCommandPropertiesView)d;
            if (e.NewValue is double display)
            {
                // 相对模式：UI 输入 5 → 存储 0.05；绝对模式：原值
                double stored = view.ToleranceModeNormalized == ToleranceMode.Relative
                    ? display / 100.0
                    : display;
                view.WriteBack("ToleranceValue", stored);
            }
            view.UpdateConditionPreviewText();
        }

        // ★ 新增：容差预览文本（实时显示接受区间）
        public static readonly DependencyProperty ToleranceHintTextProperty =
            DependencyProperty.Register(nameof(ToleranceHintText), typeof(string), typeof(LoopCommandPropertiesView),
                new PropertyMetadata(string.Empty));

        public string ToleranceHintText
        {
            get => (string)GetValue(ToleranceHintTextProperty);
            set => SetValue(ToleranceHintTextProperty, value);
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
                if (!p.ContainsKey("ExecutionMode")) p["ExecutionMode"] = ExecutionMode.Synchronous;
                if (!p.ContainsKey("ToleranceMode")) p["ToleranceMode"] = ToleranceMode.None;
                if (!p.ContainsKey("ToleranceValue")) p["ToleranceValue"] = 0.0;
                // 🔥 关键：延迟设置UI值
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    LoopTypeNormalized = ReadLoopType(p["LoopType"]);
                    OperatorNormalized = ReadOperator(p["Operator"]);
                    ExecutionModeNormalized = ReadExecutionMode(p["ExecutionMode"]);
                    // ★ 新增：读取容差
                    ToleranceModeNormalized = ReadToleranceMode(p.TryGetValue("ToleranceMode", out var tm) ? tm : null);
                    double storedTolerance = SafeToDouble(p.TryGetValue("ToleranceValue", out var tv) ? tv : null, 0.0);
                    ToleranceValueDisplay = ToleranceModeNormalized == ToleranceMode.Relative
                        ? storedTolerance * 100.0   // 0.05 → 显示 5
                        : storedTolerance;
                    // 其他属性...
                    LoopCount = SafeToInt(p.TryGetValue("LoopCount", out var lc) ? lc : null, 3);
                    LeftOperand = p.TryGetValue("LeftOperand", out var l) ? l?.ToString() ?? "A" : "A";
                    RightOperand = p.TryGetValue("RightOperand", out var r) ? r?.ToString() ?? "B" : "B";
                    Description = p.TryGetValue("Description", out var ds) ? ds?.ToString() ?? "循环执行" : "循环执行";
                    WaitTime = SafeToInt(p.TryGetValue("WaitTime", out var wt) ? wt : null, 10);
                    IsDoWhile = SafeToBool(p.TryGetValue("IsDoWhile", out var dw) ? dw : null, false);

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
                ToleranceHintText = string.Empty;
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

            // 容差描述（仅 ==/!= 生效）
            string toleranceDesc = "";
            bool isEqualityOp = OperatorNormalized == ComparisonOperator.Equal
                                || OperatorNormalized == ComparisonOperator.NotEqual;
            if (isEqualityOp && ToleranceModeNormalized != ToleranceMode.None && ToleranceValueDisplay > 0)
            {
                toleranceDesc = ToleranceModeNormalized == ToleranceMode.Absolute
                    ? $"（容差 ±{ToleranceValueDisplay}）"
                    : $"（容差 ±{ToleranceValueDisplay}%）";
            }

            string loopMode = IsDoWhile ? "Do-While" : "While";
            string description = IsDoWhile
                ? $"【{loopMode}】先执行子节点，然后判断：如果 {LeftOperand} {operatorText} {RightOperand}{toleranceDesc}，则继续循环"
                : $"【{loopMode}】先判断：如果 {LeftOperand} {operatorText} {RightOperand}{toleranceDesc}，则执行子节点并继续循环";

            ConditionPreviewText = description;

            // 容差提示行（区间预览）
            if (isEqualityOp && ToleranceModeNormalized != ToleranceMode.None && ToleranceValueDisplay > 0)
            {
                if (ToleranceModeNormalized == ToleranceMode.Absolute
                    && double.TryParse(RightOperand, NumberStyles.Float, CultureInfo.InvariantCulture, out var rv))
                {
                    var lo = rv - ToleranceValueDisplay;
                    var hi = rv + ToleranceValueDisplay;
                    ToleranceHintText = $"接受区间：{lo} ~ {hi}";
                }
                else if (ToleranceModeNormalized == ToleranceMode.Relative)
                {
                    ToleranceHintText = $"接受偏差：±{ToleranceValueDisplay}% 相对值";
                }
                else
                {
                    ToleranceHintText = $"参考值为变量时，运行时按 {RightOperand} ± {ToleranceValueDisplay} 计算";
                }
            }
            else
            {
                ToleranceHintText = string.Empty;
            }

            var node = CurrentNode;
            if (node?.LogicCommand?.Properties != null)
            {
                string condition = OperatorNormalized switch
                {
                    ComparisonOperator.Equal => $"{LeftOperand} == {RightOperand}{toleranceDesc}",
                    ComparisonOperator.NotEqual => $"{LeftOperand} != {RightOperand}{toleranceDesc}",
                    ComparisonOperator.GreaterThan => $"{LeftOperand} > {RightOperand}",
                    ComparisonOperator.GreaterThanOrEqual => $"{LeftOperand} >= {RightOperand}",
                    ComparisonOperator.LessThan => $"{LeftOperand} < {RightOperand}",
                    ComparisonOperator.LessThanOrEqual => $"{LeftOperand} <= {RightOperand}",
                    ComparisonOperator.Contains => $"{LeftOperand}.Contains({RightOperand})",
                    ComparisonOperator.NotContains => $"!{LeftOperand}.Contains({RightOperand})",
                    _ => $"{LeftOperand} == {RightOperand}{toleranceDesc}"
                };
                node.LogicCommand.Properties["Condition"] = condition;
            }
        }

        private void UpdateLoopSummaryText()
        {
            string modeTag = ExecutionModeNormalized == ExecutionMode.Asynchronous ? _localization.GetString("Loop_Async") : _localization.GetString("Loop_Sync");

            string summary = LoopTypeNormalized switch
            {
                LoopType.FixedCount => string.Format(_localization.GetString("Loop_Abstract_Fixed"),modeTag,LoopCount,WaitTime),
                LoopType.Conditional => IsDoWhile
                    ? string.Format(_localization.GetString("Loop_Abstract_Dowhile"),modeTag,WaitTime)
                    : string.Format(_localization.GetString("Loop_Abstract_While"), modeTag, WaitTime),
                LoopType.Infinite => string.Format(_localization.GetString("Loop_Abstract_infiLoop"), modeTag, WaitTime),
                _ => string.Format(_localization.GetString("Loop_Abstract_LoopExec"), modeTag, WaitTime)
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

            // 兼容其他数值类型
            try
            {
                var intVal = Convert.ToInt32(val);
                if (intVal >= 0 && intVal <= 7)
                    return (ComparisonOperator)intVal;
            }
            catch { }

            return ComparisonOperator.Equal;
        }

        private static ExecutionMode ReadExecutionMode(object val)
        {
            if (val is ExecutionMode em) return em;
            if (val is string s && Enum.TryParse<ExecutionMode>(s, true, out var p)) return p;

            if (val is int i)
            {
                if (i >= 0 && i <= 1) return (ExecutionMode)i;
            }
            try
            {
                var intVal = Convert.ToInt32(val);
                if (intVal >= 0 && intVal <= 1) return (ExecutionMode)intVal;
            }
            catch { }

            return ExecutionMode.Synchronous;
        }
        private static ToleranceMode ReadToleranceMode(object val)
        {
            if (val is ToleranceMode tm) return tm;
            if (val is string s && Enum.TryParse<ToleranceMode>(s, true, out var p)) return p;
            if (val is int i && i >= 0 && i <= 2) return (ToleranceMode)i;
            try
            {
                var intVal = Convert.ToInt32(val);
                if (intVal >= 0 && intVal <= 2) return (ToleranceMode)intVal;
            }
            catch { }
            return ToleranceMode.None;
        }

        private static double SafeToDouble(object val, double @default)
        {
            try
            {
                if (val is double d) return d;
                if (val is float f) return f;
                if (val is int i) return i;
                if (val is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                    return p;
                if (val != null)
                    return Convert.ToDouble(val, CultureInfo.InvariantCulture);
            }
            catch { }
            return @default;
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
        private static readonly Regex DecimalRegex = new Regex("^[0-9]*\\.?[0-9]*$", RegexOptions.Compiled);
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
        private void ToleranceValueBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (sender is TextBox tb)
            {
                var future = tb.Text.Insert(tb.SelectionStart, e.Text);
                e.Handled = !DecimalRegex.IsMatch(future);
            }
        }

        private void ToleranceValueBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (!e.DataObject.GetDataPresent(typeof(string)))
            {
                e.CancelCommand();
                return;
            }
            var text = (string)e.DataObject.GetData(typeof(string));
            if (!DecimalRegex.IsMatch(text)) e.CancelCommand();
        }
        #endregion
    }

    // ===== 转换器 =====

    public class LoopTypeEqualityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is LoopType lt && parameter is string p)
                return Enum.TryParse<LoopType>(p, out var target) && lt == target;
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is true && parameter is string p && Enum.TryParse<LoopType>(p, out var target))
                return target;
            return Binding.DoNothing;
        }
    }

    public class ComparisonOperatorEqualityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ComparisonOperator op && parameter is string p)
                return Enum.TryParse<ComparisonOperator>(p, out var target) && op == target;
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is true && parameter is string p && Enum.TryParse<ComparisonOperator>(p, out var target))
                return target;
            return Binding.DoNothing;
        }
    }

    public class ExecutionModeEqualityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ExecutionMode em && parameter is string p)
                return Enum.TryParse<ExecutionMode>(p, out var target) && em == target;
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is true && parameter is string p && Enum.TryParse<ExecutionMode>(p, out var target))
                return target;
            return Binding.DoNothing;
        }
    }

    public class ToleranceModeEqualityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ToleranceMode tm && parameter is string p)
                return Enum.TryParse<ToleranceMode>(p, out var target) && tm == target;
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is true && parameter is string p && Enum.TryParse<ToleranceMode>(p, out var target))
                return target;
            return Binding.DoNothing;
        }
    }

    /// <summary>
    /// 当 ToleranceMode != None 时返回 Visible，否则 Collapsed
    /// </summary>
    public class ToleranceModeToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ToleranceMode tm && tm != ToleranceMode.None)
                return Visibility.Visible;
            return Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => Binding.DoNothing;
    }
}