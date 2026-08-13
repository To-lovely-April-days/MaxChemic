using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.Designer.Event;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using Prism.Events;
using Prism.Ioc;
using System;
using System.Collections.Generic;
using System.ComponentModel;
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

            _eventAggregator = ContainerLocator.Container.Resolve<IEventAggregator>();
            _eventAggregator?.GetEvent<LanguageChangedEvent>()?.Subscribe(OnLanguageChanged);

            DataContextChanged += OnDataContextChanged;
            Loaded += OnLoaded;
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

            NodeTitle.Text = service.GetString("WaitNode_Title");
            NodeInfoTitle.Text = service.GetString("WaitNode_Info_Title");
            NodeNameLabel.Text = service.GetString("WaitNode_Info_Name");
            NodeDescLabel.Text = service.GetString("WaitNode_info_Desc");
            NodeModeTitle.Text = service.GetString("WaitNode_Mode_Title");
            FixedTimeRadio.Content = service.GetString("WaitNode_Mode_Fixed");
            ConditionRadio.Content = service.GetString("WaitNode_Mode_Condition");
            NodeTimeTitle.Text = service.GetString("WaitNode_Set_Title");
            NodeWaitLabel.Text = service.GetString("WaitNode_Set_Label");
            NodePreviewTitle.Text = service.GetString("WaitNode_Preview");
            NodeUseDescTitle.Text = service.GetString("WaitNode_Use_Title");
            Desc1.Text = service.GetString("WaitNode_Use_Desc1");
            Desc2.Text = service.GetString("WaitNode_Use_Desc2");
            Desc3.Text = service.GetString("WaitNode_Use_Desc3");
            Desc4.Text = service.GetString("WaitNode_Use_Desc4");
            Desc5.Text = service.GetString("WaitNode_Use_Desc5");
            ValidationText.Text = service.GetString("WaitNode_Valide");
            WaitConditionTitle.Text = service.GetString("WaitNode_Condition_Title");
            LeftVariableLabel.Text = service.GetString("WaitNode_Condition_Var");
            SelectVarLabel.Content = service.GetString("WaitNode_Condition_SelectVar");
            OperatorLabel.Text = service.GetString("WaitNode_Condition_Op");
            RightValueLabel.Text = service.GetString("WaitNode_Condition_Value");
            CheckedIntervalLabel.Text = service.GetString("WaitNode_Condition_Interval");
            IntervalLabel.Text = service.GetString("WaitNode_SecLabel");
            TimeoutSetLabel.Text = service.GetString("WaitNode_Condition_Timeout");
            IsInfiniteTimeoutCheckBox.Content = service.GetString("WaitNode_Condition_Infinite");
            TimeoutLabel.Text = service.GetString("WaitNode_SecLabel");
            IsWarningCheckBox.Content = service.GetString("WaitNode_Condition_Warning");
            IsWarningTblock.Text = service.GetString("WaitNode_Warning");
            ExecuteModeTitle.Text = service.GetString("WaitNode_ExecuteMode_Title");
            SynchronousRadio.Content = service.GetString("WaitNode_Sync");
            AsynchronousRadio.Content = service.GetString("WaitNode_Async");
            WaitForStabilityCheckBox.Content = service.GetString("WaitNode_WaitFor");
            WaitForSub.Text = service.GetString("WaitNode_WaitForSub");
            StabDevTb.Text = service.GetString("WaitNode_StabDev");
            StabSecTb.Text = service.GetString("WaitNode_StabSec");
            StabTipTb.Text = service.GetString("WaitNode_StabTip");
            ResidenceTimeWaitCheckBox.Content = service.GetString("WAitNode_StabWaitNode");
            TbXiaotongStabTip.Text = service.GetString("WaitNode_StabNodeTip");

        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            InitializeFromProperties();
            UpdateWaitPreview();

            OnLanguageChanged("");
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
            //  新增：等待稳定相关默认值
            if (!logicCommand.Properties.ContainsKey("WaitForStability"))
                logicCommand.Properties["WaitForStability"] = false;

            if (!logicCommand.Properties.ContainsKey("StabilityDeviceIds"))
                logicCommand.Properties["StabilityDeviceIds"] = new List<string>();

            if (!logicCommand.Properties.ContainsKey("StabilityTimeoutSeconds"))
                logicCommand.Properties["StabilityTimeoutSeconds"] = 600.0;

            //  新增：停留时间等待节点标记默认值(老流程无此键,读取处均带默认 false 兜底)
            if (!logicCommand.Properties.ContainsKey("IsResidenceTimeWait"))
                logicCommand.Properties["IsResidenceTimeWait"] = false;
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
            //  新增：初始化"等待稳定"开关和设备列表
            var waitForStability = Convert.ToBoolean(
                node.LogicCommand.Properties.GetValueOrDefault("WaitForStability", false));
            WaitForStabilityCheckBox.IsChecked = waitForStability;
            StabilityDevicesPanel.Visibility = waitForStability ? Visibility.Visible : Visibility.Collapsed;

            //  新增：初始化"停留时间等待节点"标记
            ResidenceTimeWaitCheckBox.IsChecked = Convert.ToBoolean(
                node.LogicCommand.Properties.GetValueOrDefault("IsResidenceTimeWait", false));

            // 加载并刷新设备列表
            LoadStabilityDeviceList();
        }
        /// <summary>
        ///  新增：停留时间等待节点标记切换(DOE 稳态保温:小桐的动态等待只认此标记)
        /// </summary>
        private void ResidenceTimeWait_Changed(object sender, RoutedEventArgs e)
        {
            if (DataContext is not CommandNode node) return;
            if (node.LogicCommand?.Properties == null) return;
            node.LogicCommand.Properties["IsResidenceTimeWait"] = ResidenceTimeWaitCheckBox.IsChecked == true;
        }

        /// <summary>
        ///  新增：等待稳定开关切换
        /// </summary>
        private void WaitForStability_Changed(object sender, RoutedEventArgs e)
        {
            if (DataContext is not CommandNode node) return;
            if (node.LogicCommand?.Properties == null) return;

            bool isChecked = WaitForStabilityCheckBox.IsChecked == true;
            node.LogicCommand.Properties["WaitForStability"] = isChecked;

            if (StabilityDevicesPanel != null)
            {
                StabilityDevicesPanel.Visibility = isChecked ? Visibility.Visible : Visibility.Collapsed;
            }

            if (isChecked)
            {
                LoadStabilityDeviceList();
            }

            UpdateWaitPreview();
        }

        /// <summary>
        ///  新增：单个设备复选框变化
        /// </summary>
        private void StabilityDevice_Changed(object sender, RoutedEventArgs e)
        {
            if (DataContext is not CommandNode node) return;
            if (node.LogicCommand?.Properties == null) return;
            if (StabilityDevicesList?.ItemsSource is not IEnumerable<StabilityDeviceItem> items) return;

            var selectedIds = items.Where(i => i.IsSelected).Select(i => i.DeviceId).ToList();
            node.LogicCommand.Properties["StabilityDeviceIds"] = selectedIds;

            UpdateWaitPreview();
        }

        /// <summary>
        ///  新增：从画布加载设备列表填充到 ItemsControl
        /// </summary>
        private void LoadStabilityDeviceList()
        {
            try
            {
                if (StabilityDevicesList == null) return;
                if (DataContext is not CommandNode node) return;

                // 通过事件请求画布上所有设备
                List<CanvasDeviceViewModel> canvasDevices = null;
                _eventAggregator?.GetEvent<RequestCanvasDevicesEvent>()?.Publish(list => canvasDevices = list);

                if (canvasDevices == null || canvasDevices.Count == 0)
                {
                    StabilityDevicesList.ItemsSource = new List<StabilityDeviceItem>();
                    return;
                }

                // 读取已选的 DeviceId 列表
                var selectedIds = ParseSelectedDeviceIds(node.LogicCommand.Properties);

                // 构建条目（不过滤设备类型，让所有设备都能选；运行时未实现 IsStable 的设备会被视为"已稳定"）
                var items = canvasDevices
                    .Where(d => d?.Device != null)
                    .Select(d => new StabilityDeviceItem
                    {
                        DeviceId = d.DeviceId,
                        DisplayName = BuildDeviceDisplayName(d),
                        IsSelected = selectedIds.Contains(d.DeviceId)
                    })
                    .OrderBy(i => i.DisplayName)
                    .ToList();

                StabilityDevicesList.ItemsSource = items;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载稳定设备列表失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 从 Properties 里解析已选设备ID（兼容多种存储格式）
        /// </summary>
        private static List<string> ParseSelectedDeviceIds(IDictionary<string, object> props)
        {
            if (props == null || !props.TryGetValue("StabilityDeviceIds", out var raw) || raw == null)
                return new List<string>();

            if (raw is List<string> list) return list;
            if (raw is IEnumerable<string> strs) return strs.ToList();
            if (raw is IEnumerable<object> objs)
                return objs.Select(o => o?.ToString())
                           .Where(s => !string.IsNullOrEmpty(s))
                           .ToList();
            if (raw is string s)
                return s.Split(',')
                        .Select(x => x.Trim())
                        .Where(x => !string.IsNullOrEmpty(x))
                        .ToList();

            return new List<string>();
        }

        /// <summary>
        /// 构造设备显示名（优先用画布显示名，回退到设备 Name）
        /// </summary>
        private string BuildDeviceDisplayName(CanvasDeviceViewModel canvasDevice)
        {
            try
            {
                // 优先尝试通过解析器拿显示名（如"流量计#1"）
                var resolver = ContainerLocator.Container.Resolve<MaxChemical.Modules.Designer.Service.IDeviceNameResolverService>();
                var displayName = resolver?.GetDisplayNameByDeviceId(canvasDevice.DeviceId);
                if (!string.IsNullOrEmpty(displayName))
                    return displayName;
            }
            catch { }

            return canvasDevice.Device?.Name ?? canvasDevice.DeviceId;
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

                    var waitForStability = Convert.ToBoolean(
    node.LogicCommand.Properties.GetValueOrDefault("WaitForStability", false));

                    if (waitForStability)
                    {
                        var deviceIds = ParseSelectedDeviceIds(node.LogicCommand.Properties);
                        var deviceCountText = deviceIds.Count == 0 ? "未选择设备" : $"{deviceIds.Count} 个设备";
                        var stabilityTimeout = Convert.ToDouble(
                            node.LogicCommand.Properties.GetValueOrDefault("StabilityTimeoutSeconds", 600.0));
                        previewText = $"[异步并行] 等待 {deviceCountText} 稳定（超时{stabilityTimeout:F0}s），" +
                                      $"再停留 {waitTime} {unitDisplay}";
                    }
                    else
                    {
                        previewText = $"[同步] 等待 {waitTime} {unitDisplay}";
                    }
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
    /// <summary>
    ///  新增：稳定设备列表项（绑定用）
    /// </summary>
    public class StabilityDeviceItem : INotifyPropertyChanged
    {
        public string DeviceId { get; set; }
        public string DisplayName { get; set; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
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
