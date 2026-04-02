using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using Prism.Events;
using Prism.Ioc;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class WaitPropertiesView : UserControl
    {
        private CancellationTokenSource _debounceCts;
        private IEventAggregator _eventAggregator;

        private static VariableManagerWindow _sharedVariableWindow;
        private static readonly object _windowLock = new object();

        public WaitPropertiesView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            InitializeFromProperties();
            UpdateWaitPreview();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is CommandNode node && node.LogicCommand != null)
            {
                EnsureDefaultProperties(node.LogicCommand);
                InitializeFromProperties();
                UpdateWaitPreview();
            }
        }

        private void EnsureDefaultProperties(LogicCommand logicCommand)
        {
            if (!logicCommand.Properties.ContainsKey("WaitTime"))
                logicCommand.Properties["WaitTime"] = 5.0;

            if (!logicCommand.Properties.ContainsKey("TimeUnit"))
                logicCommand.Properties["TimeUnit"] = "Seconds";

            if (!logicCommand.Properties.ContainsKey("ConditionLeftVariable"))
                logicCommand.Properties["ConditionLeftVariable"] = "";

            if (!logicCommand.Properties.ContainsKey("ConditionOperator"))
                logicCommand.Properties["ConditionOperator"] = "Equals";

            if (!logicCommand.Properties.ContainsKey("ConditionRightValue"))
                logicCommand.Properties["ConditionRightValue"] = "";

            if (!logicCommand.Properties.ContainsKey("TimeoutSeconds"))
                logicCommand.Properties["TimeoutSeconds"] = 30.0;

            if (!logicCommand.Properties.ContainsKey("CheckIntervalSeconds"))
                logicCommand.Properties["CheckIntervalSeconds"] = 1.0;

            if (!logicCommand.Properties.ContainsKey("IsInfiniteTimeout"))
                logicCommand.Properties["IsInfiniteTimeout"] = false;

            if (!logicCommand.Properties.ContainsKey("ExecutionMode"))
                logicCommand.Properties["ExecutionMode"] = "Synchronous";

            if (!logicCommand.Properties.ContainsKey("Description"))
                logicCommand.Properties["Description"] = "等待指定时间";

            //  新增：IsWarning 默认值
            if (!logicCommand.Properties.ContainsKey("IsWarning"))
                logicCommand.Properties["IsWarning"] = false;
        }

        private void InitializeFromProperties()
        {
            if (DataContext is not CommandNode node) return;
            if (node.LogicCommand?.Properties == null) return;

            var leftVariable = node.LogicCommand.Properties.GetValueOrDefault("ConditionLeftVariable")?.ToString() ?? "";

            if (string.IsNullOrWhiteSpace(leftVariable))
            {
                FixedTimeRadio.IsChecked = true;
                ConditionRadio.IsChecked = false;
                FixedTimeGroup.Visibility = Visibility.Visible;
                ConditionGroup.Visibility = Visibility.Collapsed;
            }
            else
            {
                FixedTimeRadio.IsChecked = false;
                ConditionRadio.IsChecked = true;
                FixedTimeGroup.Visibility = Visibility.Collapsed;
                ConditionGroup.Visibility = Visibility.Visible;
            }

            // 设置执行模式
            var executionMode = node.LogicCommand.Properties.GetValueOrDefault("ExecutionMode")?.ToString() ?? "Synchronous";
            switch (executionMode)
            {
                case "Synchronous":
                    SynchronousRadio.IsChecked = true;
                    break;
                case "Asynchronous":
                    AsynchronousRadio.IsChecked = true;
                    break;
            }

            //  设置 IsWarning 复选框
            var isWarning = Convert.ToBoolean(node.LogicCommand.Properties.GetValueOrDefault("IsWarning", false));
            IsWarningCheckBox.IsChecked = isWarning;
        }

        private void ExecutionMode_Changed(object sender, RoutedEventArgs e)
        {
            if (DataContext is not CommandNode node) return;
            if (node.LogicCommand?.Properties == null) return;

            if (SynchronousRadio.IsChecked == true)
            {
                node.LogicCommand.Properties["ExecutionMode"] = "Synchronous";
            }
            else if (AsynchronousRadio.IsChecked == true)
            {
                node.LogicCommand.Properties["ExecutionMode"] = "Asynchronous";
            }

            UpdateWaitPreview();
        }

        private void WaitMode_Changed(object sender, RoutedEventArgs e)
        {
            if (DataContext is not CommandNode node) return;
            if (node.LogicCommand?.Properties == null) return;

            if (FixedTimeRadio.IsChecked == true)
            {
                node.LogicCommand.Properties["ConditionLeftVariable"] = "";
                node.LogicCommand.Properties["ConditionRightValue"] = "";
                node.LogicCommand.Properties["ExecutionMode"] = "Synchronous";

                FixedTimeGroup.Visibility = Visibility.Visible;
                ConditionGroup.Visibility = Visibility.Collapsed;
                ExecutionModeGroup.Visibility = Visibility.Collapsed;
            }
            else if (ConditionRadio.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(node.LogicCommand.Properties.GetValueOrDefault("ConditionLeftVariable")?.ToString()))
                {
                    node.LogicCommand.Properties["ConditionLeftVariable"] = "{variable}";
                    node.LogicCommand.Properties["ConditionRightValue"] = "true";
                }

                FixedTimeGroup.Visibility = Visibility.Collapsed;
                ConditionGroup.Visibility = Visibility.Visible;
                ExecutionModeGroup.Visibility = Visibility.Visible;
            }

            UpdateWaitPreview();
        }

        /// <summary>
        ///  新增：IsWarning 开关变化
        /// </summary>
        private void IsWarning_Changed(object sender, RoutedEventArgs e)
        {
            if (DataContext is not CommandNode node) return;
            if (node.LogicCommand?.Properties == null) return;

            node.LogicCommand.Properties["IsWarning"] = IsWarningCheckBox.IsChecked == true;
            UpdateWaitPreview();
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

                UpdateWaitPreview();
                ValidateConfiguration();
            }
            catch (TaskCanceledException)
            {
                // 忽略取消
            }
        }

        private void SelectLeftVariable_Click(object sender, RoutedEventArgs e)
        {
            ShowVariableSelector();
        }

        private void ShowVariableSelector()
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
                            if (!_sharedVariableWindow.IsClosed)
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
                            // 窗口关闭处理
                        };
                    }
                }

                variableWindow.Owner = Window.GetWindow(this);
                variableWindow.Title = "选择条件变量";

                if (variableWindow.ShowDialog() == true && variableWindow.SelectedVariable != null)
                {
                    var selectedVar = variableWindow.SelectedVariable;
                    ConditionLeftVariableTextBox.Text = selectedVar.FullVariableName;
                    OnValueChanged(ConditionLeftVariableTextBox, null);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("选择变量时发生错误", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateWaitPreview()
        {
            if (DataContext is not CommandNode node) return;
            if (node.LogicCommand?.Properties == null) return;

            try
            {
                var leftVariable = node.LogicCommand.Properties.GetValueOrDefault("ConditionLeftVariable")?.ToString() ?? "";

                string previewText;

                if (string.IsNullOrWhiteSpace(leftVariable))
                {
                    // 固定时间等待
                    var waitTime = Convert.ToDouble(node.LogicCommand.Properties.GetValueOrDefault("WaitTime", 5.0));
                    var timeUnit = node.LogicCommand.Properties.GetValueOrDefault("TimeUnit")?.ToString() ?? "Seconds";

                    var unitDisplay = timeUnit switch
                    {
                        "Milliseconds" => "毫秒",
                        "Seconds" => "秒",
                        "Minutes" => "分钟",
                        "Hours" => "小时",
                        _ => "秒"
                    };

                    previewText = $"[同步] 等待 {waitTime} {unitDisplay}";
                }
                else
                {
                    // 条件等待
                    var executionMode = node.LogicCommand.Properties.GetValueOrDefault("ExecutionMode")?.ToString() ?? "Synchronous";
                    var executionModeText = executionMode == "Asynchronous" ? "[异步]" : "[同步]";

                    var operatorValue = node.LogicCommand.Properties.GetValueOrDefault("ConditionOperator")?.ToString() ?? "Equals";
                    var rightValue = node.LogicCommand.Properties.GetValueOrDefault("ConditionRightValue")?.ToString() ?? "";
                    var isInfiniteTimeout = Convert.ToBoolean(node.LogicCommand.Properties.GetValueOrDefault("IsInfiniteTimeout", false));
                    var timeoutSeconds = Convert.ToDouble(node.LogicCommand.Properties.GetValueOrDefault("TimeoutSeconds", 30.0));
                    var checkInterval = Convert.ToDouble(node.LogicCommand.Properties.GetValueOrDefault("CheckIntervalSeconds", 1.0));
                    var isWarning = Convert.ToBoolean(node.LogicCommand.Properties.GetValueOrDefault("IsWarning", false));

                    var operatorDisplay = operatorValue switch
                    {
                        "Equals" => "==",
                        "NotEquals" => "!=",
                        "GreaterThan" => ">",
                        "GreaterThanOrEqual" => ">=",
                        "LessThan" => "<",
                        "LessThanOrEqual" => "<=",
                        "Contains" => "包含",
                        "NotContains" => "不包含",
                        "IsTrue" => "为真",
                        "IsFalse" => "为假",
                        "IsEmpty" => "为空",
                        "IsNotEmpty" => "不为空",
                        _ => "=="
                    };

                    var conditionText = operatorValue switch
                    {
                        "IsTrue" or "IsFalse" or "IsEmpty" or "IsNotEmpty" => $"{leftVariable} {operatorDisplay}",
                        _ => $"{leftVariable} {operatorDisplay} {rightValue}"
                    };

                    //  预览中显示警告模式标记
                    var warningTag = isWarning ? " ⚠警告" : "";

                    if (isInfiniteTimeout)
                    {
                        previewText = $"{executionModeText}{warningTag} 循环等待条件：{conditionText}\n检查间隔：{checkInterval}秒\n无限超时";
                    }
                    else
                    {
                        previewText = $"{executionModeText}{warningTag} 循环等待条件：{conditionText}\n检查间隔：{checkInterval}秒\n超时时间：{timeoutSeconds}秒";
                    }
                }

                if (WaitPreview != null)
                {
                    WaitPreview.Text = previewText;
                }

                // 处理超时输入框的显示/隐藏
                if (TimeoutInputGroup != null && IsInfiniteTimeoutCheckBox != null)
                {
                    TimeoutInputGroup.IsEnabled = !(IsInfiniteTimeoutCheckBox.IsChecked ?? false);
                    TimeoutInputGroup.Opacity = TimeoutInputGroup.IsEnabled ? 1.0 : 0.5;
                }
            }
            catch (Exception ex)
            {
                if (WaitPreview != null)
                {
                    WaitPreview.Text = "预览生成失败";
                }
            }
        }

        private void ValidateConfiguration()
        {
            if (DataContext is not CommandNode node) return;
            if (node.LogicCommand?.Properties == null) return;

            try
            {
                var leftVariable = node.LogicCommand.Properties.GetValueOrDefault("ConditionLeftVariable")?.ToString() ?? "";
                string errorMessage = null;

                if (string.IsNullOrWhiteSpace(leftVariable))
                {
                    if (!double.TryParse(node.LogicCommand.Properties.GetValueOrDefault("WaitTime")?.ToString(), out double waitTime) || waitTime <= 0)
                    {
                        errorMessage = "等待时间必须大于0";
                    }
                }
                else
                {
                    var operatorValue = node.LogicCommand.Properties.GetValueOrDefault("ConditionOperator")?.ToString() ?? "Equals";
                    var rightValue = node.LogicCommand.Properties.GetValueOrDefault("ConditionRightValue")?.ToString() ?? "";
                    var isInfiniteLoop = Convert.ToBoolean(node.LogicCommand.Properties.GetValueOrDefault("IsInfiniteLoop", false));

                    if (string.IsNullOrWhiteSpace(leftVariable))
                    {
                        errorMessage = "左侧变量不能为空";
                    }
                    else if (!operatorValue.Equals("IsTrue") && !operatorValue.Equals("IsFalse") &&
                             !operatorValue.Equals("IsEmpty") && !operatorValue.Equals("IsNotEmpty") &&
                             string.IsNullOrWhiteSpace(rightValue))
                    {
                        errorMessage = "右侧值不能为空";
                    }
                    else if (!isInfiniteLoop)
                    {
                        if (!double.TryParse(node.LogicCommand.Properties.GetValueOrDefault("TimeoutSeconds")?.ToString(), out double timeout) || timeout <= 0)
                        {
                            errorMessage = "超时时间必须大于0";
                        }
                        else if (!double.TryParse(node.LogicCommand.Properties.GetValueOrDefault("CheckIntervalSeconds")?.ToString(), out double interval) || interval <= 0)
                        {
                            errorMessage = "检查间隔必须大于0";
                        }
                    }
                    else
                    {
                        if (!double.TryParse(node.LogicCommand.Properties.GetValueOrDefault("CheckIntervalSeconds")?.ToString(), out double interval) || interval <= 0)
                        {
                            errorMessage = "检查间隔必须大于0";
                        }
                    }
                }

                if (!string.IsNullOrEmpty(errorMessage))
                {
                    ValidationText.Text = errorMessage;
                    ValidationPanel.Visibility = Visibility.Visible;
                }
                else
                {
                    ValidationPanel.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception)
            {
                ValidationText.Text = "配置验证失败";
                ValidationPanel.Visibility = Visibility.Visible;
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
    }

    public class StringEqualityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value?.ToString() == parameter?.ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is true)
                return parameter?.ToString();
            return Binding.DoNothing;
        }
    }
}
