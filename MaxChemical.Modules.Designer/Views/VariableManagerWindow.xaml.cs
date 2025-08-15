using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DevicePlugins.Devices;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;
using MaxChemical.Modules.Designer.Event;
using Prism.Events;
using System.Windows.Controls.Primitives;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class VariableManagerWindow : Window
    {
        private ObservableCollection<VariableViewModel> _allVariables;
        private ICollectionView _variablesView;
        private Dictionary<string, VariableViewModel> _variableMap; // 用于快速查找和更新
        private IEnumerable<CommandNode> _allNodes;
        private IEventAggregator _eventAggregator;
        private SubscriptionToken _updateToken;

        public VariableViewModel SelectedVariable { get; private set; }

        public VariableManagerWindow(IEventAggregator eventAggregator,IEnumerable<CommandNode> allNodes = null )
        {
            InitializeComponent();

            _allVariables = new ObservableCollection<VariableViewModel>();
            _variableMap = new Dictionary<string, VariableViewModel>();
            _allNodes = allNodes;
            _eventAggregator = eventAggregator;

            LoadVariables(allNodes);

            _variablesView = CollectionViewSource.GetDefaultView(_allVariables);
            _variablesView.Filter = FilterVariables;
            VariablesDataGrid.ItemsSource = _variablesView;

            // 订阅执行结果更新事件
            if (_eventAggregator != null)
            {
                _updateToken = _eventAggregator.GetEvent<NodeExecutionResultUpdatedEvent>()
                    .Subscribe(OnNodeExecutionResultUpdated, ThreadOption.UIThread);
            }
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1)
            {
                DragMove();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void OnNodeExecutionResultUpdated(NodeExecutionResultEventArgs args)
        {
            if (args?.OutputParameters?.Variables == null) return;

            // 更新对应的变量值
            foreach (var param in args.OutputParameters.Variables)
            {
                // 尝试通过NodeId精确匹配
                if (!string.IsNullOrEmpty(args.NodeId))
                {
                    string exactKey = $"{args.DeviceName}.{args.CommandName}.{param.Name}.{args.NodeId}";
                    if (_variableMap.TryGetValue(exactKey, out var exactVariable))
                    {
                        UpdateVariableValue(exactVariable, param);
                        continue;
                    }

                }

                // 回退到原来的匹配方式（兼容旧数据）
                string key = $"{args.DeviceName}.{args.CommandName}.{param.Name}";
                var matchingVariables = _variableMap.Where(kvp => kvp.Key.StartsWith(key)).ToList();

                foreach (var kvp in matchingVariables)
                {
                    UpdateVariableValue(kvp.Value, param);
                }
            }

            // 刷新视图
            _variablesView?.Refresh();
        }

        private void UpdateVariableValue(VariableViewModel variable, ParameterBase param)
        {
            // 更新变量值
            variable.VariableValue = param.Value;

            // 如果是数值类型，格式化显示
            if (param is NumberParameter numParam && param.Value is double d)
            {
                variable.VariableValue = Math.Round(d, numParam.DecimalPlaces);
            }
        }
        private void LoadVariables(IEnumerable<CommandNode> allNodes)
        {
            if (allNodes == null) return;

            _allVariables.Clear();
            _variableMap.Clear();

            // 用于去重的字典：Key = "DeviceName.CommandName.VariableName"，Value = 最新的变量信息
            var uniqueVariables = new Dictionary<string, (CommandNode Node, ParameterBase Param, DateTime? LastExecutionTime)>();

            // 递归获取所有节点（包括子节点）
            var allNodesFlattened = GetAllNodesRecursive(allNodes);

            // 遍历所有节点，收集设备命令的输出参数并去重
            foreach (var node in allNodesFlattened)
            {
                if (node.DeviceCommand != null)
                {
                    string deviceName = node.DeviceName;
                    string commandName = node.CommandName;

                    // 优先使用执行后的实际输出参数
                    DeviceParameters outputParams = null;
                    DateTime? lastExecutionTime = node.LastExecutionResult?.ExecutionTime;

                    // 1. 首先尝试获取最后执行的输出参数（实际值）
                    if (node.LastOutputParameters?.Variables != null && node.LastOutputParameters.Variables.Count > 0)
                    {
                        outputParams = node.LastOutputParameters;
                    }
                    // 2. 如果没有执行过，使用预定义的输出参数（默认值）
                    else if (node.DeviceCommand.OutputParameters?.Variables != null && node.DeviceCommand.OutputParameters.Variables.Count > 0)
                    {
                        outputParams = node.DeviceCommand.OutputParameters;
                        lastExecutionTime = null; // 未执行过的节点没有执行时间
                    }

                    if (outputParams?.Variables != null)
                    {
                        foreach (var param in outputParams.Variables)
                        {
                            // 只处理设置为在变量管理器中可见的参数
                            if (param.IsVisibleInVariableManager)
                            {
                                string uniqueKey = $"{deviceName}.{commandName}.{param.Name}";

                                // 检查是否已存在相同的变量
                                if (uniqueVariables.TryGetValue(uniqueKey, out var existing))
                                {
                                    // 如果当前节点有更新的执行时间，则替换
                                    bool shouldReplace = false;

                                    if (lastExecutionTime.HasValue && !existing.LastExecutionTime.HasValue)
                                    {
                                        // 当前有执行时间，已存在的没有 -> 替换
                                        shouldReplace = true;
                                    }
                                    else if (lastExecutionTime.HasValue && existing.LastExecutionTime.HasValue)
                                    {
                                        // 都有执行时间，比较时间 -> 选择更新的
                                        shouldReplace = lastExecutionTime.Value > existing.LastExecutionTime.Value;
                                    }
                                    else if (!lastExecutionTime.HasValue && !existing.LastExecutionTime.HasValue)
                                    {
                                        // 都没有执行时间，保持第一个（不替换）
                                        shouldReplace = false;
                                    }
                                    // 如果当前没有执行时间但已存在的有，则不替换

                                    if (shouldReplace)
                                    {
                                        uniqueVariables[uniqueKey] = (node, param, lastExecutionTime);
                                    }
                                }
                                else
                                {
                                    // 首次遇到该变量，直接添加
                                    uniqueVariables[uniqueKey] = (node, param, lastExecutionTime);
                                }
                            }
                        }
                    }
                }
            }

            // 将去重后的变量添加到集合中
            foreach (var kvp in uniqueVariables)
            {
                var (node, param, lastExecutionTime) = kvp.Value;
                string deviceName = node.DeviceName;
                string commandName = node.CommandName;

                var variable = CreateVariableViewModel(deviceName, commandName, param, false, node.NodeId);

                // 添加额外的状态信息
                variable.HasExecutionData = lastExecutionTime.HasValue;
                variable.LastExecutionTime = lastExecutionTime;

                _allVariables.Add(variable);

                // 添加到映射表以便快速更新
                string mapKey = kvp.Key; // 使用去重的Key
                _variableMap[mapKey] = variable;
            }

            // 加载已保存的自定义变量
            LoadCustomVariables();
        }

        /// <summary>
        /// 递归获取所有节点（包括子节点）
        /// </summary>
        private IEnumerable<CommandNode> GetAllNodesRecursive(IEnumerable<CommandNode> nodes)
        {
            if (nodes == null) yield break;

            foreach (var node in nodes)
            {
                yield return node;

                // 递归获取子节点
                if (node.HasChildren)
                {
                    foreach (var child in GetAllNodesRecursive(node.Children))
                    {
                        yield return child;
                    }
                }
            }
        }

        /// <summary>
        /// 创建变量视图模型
        /// </summary>
        private VariableViewModel CreateVariableViewModel(string deviceName, string commandName, ParameterBase param, bool isCustom, string nodeId = null)
        {
            string typeName = GetParameterTypeName(param);
            object value = param.Value;

            // 格式化显示值
            if (param is NumberParameter numParam && value is double d)
            {
                value = Math.Round(d, numParam.DecimalPlaces);
            }

            return new VariableViewModel
            {
                DeviceName = deviceName,
                CommandName = commandName,
                VariableName = param.Name,
                VariableValue = value,
                VariableType = typeName,
                IsCustomVariable = isCustom,
                NodeId = nodeId
            };
        }



        /// <summary>
        /// 根据参数对象获取其类型名称
        /// </summary>
        private string GetParameterTypeName(ParameterBase parameter)
        {
            return parameter switch
            {
                StringParameter => "String",
                NumberParameter => "Double",
                BooleanParameter => "Boolean",
                EnumParameter enumParam => enumParam.EnumType?.Name ?? "Enum",
                _ => parameter.Value?.GetType().Name ?? "Object"
            };
        }

        /// <summary>
        /// 刷新变量值（可以由外部调用）
        /// </summary>
        public void RefreshVariableValues()
        {
            if (_allNodes == null) return;

            foreach (var node in _allNodes)
            {
                if (node.DeviceCommand != null && node.LastOutputParameters?.Variables != null)
                {
                    var deviceName = node.DeviceName;
                    var commandName = node.CommandName;

                    foreach (var param in node.LastOutputParameters.Variables)
                    {
                        string key = $"{deviceName}.{commandName}.{param.Name}";
                        if (_variableMap.TryGetValue(key, out var variable))
                        {
                            // 更新变量值
                            variable.VariableValue = param.Value;

                            // 如果是数值类型，格式化显示
                            if (param is NumberParameter numParam && param.Value is double d)
                            {
                                variable.VariableValue = Math.Round(d, numParam.DecimalPlaces);
                            }
                        }
                    }
                }
            }

            // 刷新视图
            _variablesView?.Refresh();
        }

        private void LoadCustomVariables()
        {
            // TODO: 从配置文件或数据库加载自定义变量
            var customVar = new VariableViewModel
            {
                VariableName = "Counter",
                VariableValue = 0,
                VariableType = "Int32",
                IsCustomVariable = true
            };
            _allVariables.Add(customVar);
            _variableMap[$"custom.{customVar.VariableName}"] = customVar;
        }

        private void AddVariable_Click(object sender, RoutedEventArgs e)
        {
            // 添加新的空白变量行
            var newVariable = new VariableViewModel
            {
                VariableName = $"Variable{_allVariables.Count(v => v.IsCustomVariable) + 1}",
                VariableValue = "",
                VariableType = "String",
                IsCustomVariable = true
            };

            _allVariables.Add(newVariable);
            _variableMap[$"custom.{newVariable.VariableName}"] = newVariable;
            _variablesView.Refresh();

            // 选中新添加的行并开始编辑
            VariablesDataGrid.SelectedItem = newVariable;
            VariablesDataGrid.ScrollIntoView(newVariable);

            // 延迟执行，确保UI更新完成
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var row = VariablesDataGrid.ItemContainerGenerator.ContainerFromItem(newVariable) as DataGridRow;
                if (row != null)
                {
                    var cell = GetCell(VariablesDataGrid, row, 2); // 变量名称列
                    if (cell != null)
                    {
                        cell.Focus();
                        VariablesDataGrid.BeginEdit();
                    }
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private DataGridCell GetCell(DataGrid dataGrid, DataGridRow row, int column)
        {
            if (row != null)
            {
                var presenter = GetVisualChild<DataGridCellsPresenter>(row);
                if (presenter != null)
                {
                    var cell = (DataGridCell)presenter.ItemContainerGenerator.ContainerFromIndex(column);
                    if (cell == null)
                    {
                        dataGrid.ScrollIntoView(row, dataGrid.Columns[column]);
                        cell = (DataGridCell)presenter.ItemContainerGenerator.ContainerFromIndex(column);
                    }
                    return cell;
                }
            }
            return null;
        }

        private T GetVisualChild<T>(Visual parent) where T : Visual
        {
            T child = default(T);
            int numVisuals = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < numVisuals; i++)
            {
                var v = (Visual)System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                child = v as T ?? GetVisualChild<T>(v);
                if (child != null) break;
            }
            return child;
        }

        private void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            var selectedItems = VariablesDataGrid.SelectedItems.Cast<VariableViewModel>()
                .Where(v => v.IsCustomVariable).ToList();

            if (selectedItems.Count == 0)
            {
                MessageBox.Show("请选择要删除的自定义变量", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var item in selectedItems)
            {
                _allVariables.Remove(item);
                _variableMap.Remove($"custom.{item.VariableName}");
            }
            _variablesView.Refresh();
        }

        private void DeleteRow_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is VariableViewModel variable && variable.IsCustomVariable)
            {
                _allVariables.Remove(variable);
                _variableMap.Remove($"custom.{variable.VariableName}");
                _variablesView.Refresh();
            }
        }

        private bool FilterVariables(object item)
        {
            if (item is not VariableViewModel variable) return false;

            // 过滤自定义变量
            if (ShowCustomOnlyCheckBox.IsChecked == true && !variable.IsCustomVariable)
                return false;

            // 搜索过滤
            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                var searchText = SearchBox.Text.ToLower();
                return variable.DeviceName?.ToLower().Contains(searchText) == true ||
                       variable.CommandName?.ToLower().Contains(searchText) == true ||
                       variable.VariableName?.ToLower().Contains(searchText) == true;
            }

            return true;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _variablesView?.Refresh();
        }

        private void FilterChanged(object sender, RoutedEventArgs e)
        {
            _variablesView?.Refresh();
        }

        private void DataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var dataGrid = sender as DataGrid;
            if (dataGrid?.CurrentCell != null)
            {
                dataGrid.BeginEdit();
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (VariablesDataGrid.SelectedItem is VariableViewModel variable)
            {
                SelectedVariable = variable;
                DialogResult = true;
                Close();
            }
            else
            {
                MessageBox.Show("请选择一个变量", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            // 取消订阅
            if (_updateToken != null && _eventAggregator != null)
            {
                _eventAggregator.GetEvent<NodeExecutionResultUpdatedEvent>().Unsubscribe(_updateToken);
            }
        }
    }
}