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
    public partial class SetVariablePropertiesView : UserControl
    {
        private CancellationTokenSource _debounceCts;
        private IEventAggregator _eventAggregator;

        // 添加变量管理器窗口管理
        private static VariableManagerWindow _sharedVariableWindow;
        private static readonly object _windowLock = new object();

        public SetVariablePropertiesView()
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

            NodeTitle.Text = service.GetString("SetVar_Title");
            NodeInfoTitle.Text = service.GetString("SetVar_Info_Title");
            NodeNameLabel.Text = service.GetString("SetVar_Info_Name");
            VarSetTitle.Text = service.GetString("SetVar_Info_Desc");
            VarNameLabel.Text = service.GetString("SetVar_Set_Title");
            VarTypeLabel.Text = service.GetString("SetVar_Set_Name");
            VarValueLabel.Text = service.GetString("SetVar_Set_Type");
            VarSelect.Content = service.GetString("SetVar_Set_Select");
            ValueSelect.Content = service.GetString("SetVar_Set_Select");
            NodePreviewTitle.Text = service.GetString("SetVar_Set_Preview");
            NodeUseDescTitle.Text = service.GetString("SetVar_Use_Title");
            Desc1.Text = service.GetString("SetVar_Use_Desc1");
            Desc2.Text = service.GetString("SetVar_Use_Desc2");
            Desc3.Text = service.GetString("SetVar_Use_Desc3");
            WarnInfo.Text = service.GetString("SetVar_WarnInfo");
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateVariablePreview();

            OnLanguageChanged("");
        }

        private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is CommandNode node && node.LogicCommand != null)
            {
                // 确保属性存在默认值
                EnsureDefaultProperties(node.LogicCommand);
                UpdateVariablePreview();
            }
        }

        private void EnsureDefaultProperties(LogicCommand logicCommand)
        {
            if (!logicCommand.Properties.ContainsKey("VariableName"))
                logicCommand.Properties["VariableName"] = "";

            if (!logicCommand.Properties.ContainsKey("VariableValue"))
                logicCommand.Properties["VariableValue"] = "";

            if (!logicCommand.Properties.ContainsKey("VariableType"))
                logicCommand.Properties["VariableType"] = "String";

            if (!logicCommand.Properties.ContainsKey("Description"))
                logicCommand.Properties["Description"] = "设置变量";
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

                UpdateVariablePreview();

                // 新添加：刷新节点控件显示
                RefreshNodeDisplay();
            }
            catch (TaskCanceledException)
            {
                // 忽略取消
            }
        }

        // 新添加：刷新节点显示的方法
        private void RefreshNodeDisplay()
        {
            try
            {
                if (DataContext is not CommandNode node) return;

                // 方法1：通过NodeControl属性直接访问
                if (node.NodeControl is SetVariableNodeControlView setVarControl)
                {
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        setVarControl.RefreshVariableDisplay();
                    }), System.Windows.Threading.DispatcherPriority.Render);
                    return;
                }

                // 方法2：搜索可视化树查找控件
                Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                {
                    var setVarNodeControl = FindSetVariableNodeControlForNode(node);
                    if (setVarNodeControl != null)
                    {
                        setVarNodeControl.RefreshVariableDisplay();
                    }
                }), System.Windows.Threading.DispatcherPriority.Render);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RefreshNodeDisplay error: {ex.Message}");
            }
        }

        // 查找对应的设置变量节点控件
        private SetVariableNodeControlView FindSetVariableNodeControlForNode(CommandNode targetNode)
        {
            try
            {
                var mainWindow = Application.Current.MainWindow;
                if (mainWindow != null)
                {
                    var setVarControls = FindVisualChildren<SetVariableNodeControlView>(mainWindow);
                    foreach (var control in setVarControls)
                    {
                        if (control.DataContext == targetNode)
                        {
                            return control;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"FindSetVariableNodeControlForNode error: {ex.Message}");
            }

            return null;
        }

        private void UpdateVariablePreview()
        {
            if (DataContext is not CommandNode node) return;
            if (node.LogicCommand?.Properties == null) return;

            try
            {
                var variableName = node.LogicCommand.Properties.ContainsKey("VariableName")
                    ? node.LogicCommand.Properties["VariableName"]?.ToString() ?? ""
                    : "";

                var variableValue = node.LogicCommand.Properties.ContainsKey("VariableValue")
                    ? node.LogicCommand.Properties["VariableValue"]?.ToString() ?? ""
                    : "";

                var variableType = node.LogicCommand.Properties.ContainsKey("VariableType")
                    ? node.LogicCommand.Properties["VariableType"]?.ToString() ?? "String"
                    : "String";

                string typeDisplay = variableType switch
                {
                    "String" => "字符串",
                    "Integer" => "整数",
                    "Number" => "数字",
                    "Double" => "小数",
                    "Boolean" => "布尔值",
                    "DateTime" => "日期时间",
                    _ => "字符串"
                };

                string previewText;
                if (string.IsNullOrWhiteSpace(variableName) && string.IsNullOrWhiteSpace(variableValue))
                {
                    previewText = "请配置变量名和变量值";
                }
                else if (string.IsNullOrWhiteSpace(variableName))
                {
                    previewText = "请输入变量名";
                }
                else if (string.IsNullOrWhiteSpace(variableValue))
                {
                    previewText = $"{variableName} ({typeDisplay}) = 请输入值";
                }
                else
                {
                    previewText = $"{variableName} ({typeDisplay}) = {variableValue}";
                }

                if (VariablePreview != null)
                {
                    VariablePreview.Text = previewText;
                }
            }
            catch (Exception ex)
            {
                if (VariablePreview != null)
                {
                    VariablePreview.Text = "预览生成失败";
                }
            }
        }

        private void SelectVariableName_Click(object sender, RoutedEventArgs e)
        {
            ShowVariableSelector("VariableName");
        }

        private void SelectVariableValue_Click(object sender, RoutedEventArgs e)
        {
            ShowVariableSelector("VariableValue");
        }

        private void ShowVariableSelector(string targetProperty)
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
                        variableWindow = new VariableManagerWindow(_eventAggregator, allNodes, customVariableOnlyMode: true);
                        _sharedVariableWindow = variableWindow;

                        variableWindow.Closed += (s, e) =>
                        {
                            // 窗口关闭处理
                        };
                    }
                }

                variableWindow.Owner = Window.GetWindow(this);
                variableWindow.Title = targetProperty == "VariableName" ? "选择变量名" : "选择变量值";

                if (variableWindow.ShowDialog() == true && variableWindow.SelectedVariable != null)
                {
                    var selectedVar = variableWindow.SelectedVariable;

                    // 更新文本框值
                    if (targetProperty == "VariableName")
                    {
                        VariableNameTextBox.Text = selectedVar.FullVariableName;

                        // 如果选择的是变量名，同步变量类型
                        SyncVariableType(selectedVar.VariableType);
                    }
                    else if (targetProperty == "VariableValue")
                    {
                        VariableValueTextBox.Text = selectedVar.FullVariableName;
                        // 选择变量值时不改变类型，因为可能是引用其他类型的变量
                    }

                    // 最后统一触发更新
                    Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        UpdateVariablePreview();
                        RefreshNodeDisplay();
                    }), System.Windows.Threading.DispatcherPriority.Render);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"选择变量时发生错误: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // 新添加：同步变量类型到ComboBox
        private void SyncVariableType(string selectedType)
        {
            if (string.IsNullOrEmpty(selectedType)) return;

            try
            {
                // 映射 VariableViewModel 的类型字符串到 ComboBox 的 Tag
                string mappedType = selectedType switch
                {
                    "String" => "String",
                    "Int32" => "Integer",
                    "Integer" => "Integer",
                    "Double" => "Double",
                    "Single" => "Double",  // float 映射到 Double
                    "Boolean" => "Boolean",
                    "DateTime" => "DateTime",
                    "Number" => "Number",
                    "Decimal" => "Double", // Decimal 映射到 Double
                    _ => "String"  // 默认
                };

                // 同时更新 ComboBox 显示和底层数据模型
                if (DataContext is CommandNode node && node.LogicCommand?.Properties != null)
                {
                    // 更新底层数据模型
                    node.LogicCommand.Properties["VariableType"] = mappedType;

                    // 更新 ComboBox 显示
                    VariableTypeCombo.SelectedValue = mappedType;

                    // 触发值变化事件，更新预览和刷新显示
                    OnValueChanged(VariableTypeCombo, null);

                    System.Diagnostics.Debug.WriteLine($"SyncVariableType: {selectedType} -> {mappedType}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SyncVariableType error: {ex.Message}");
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
    }
}