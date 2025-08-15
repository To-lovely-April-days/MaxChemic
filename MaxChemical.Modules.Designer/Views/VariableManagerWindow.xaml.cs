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
        private Dictionary<string, VariableViewModel> _variableMap;
        private IEnumerable<CommandNode> _allNodes;
        private IEventAggregator _eventAggregator;
        private SubscriptionToken _updateToken;
        private bool _isSubscribed = false;
        private bool _isWindowActive = false;

        public VariableViewModel SelectedVariable { get; private set; }
        public bool IsClosed { get; private set; } = false;

        public VariableManagerWindow(IEventAggregator eventAggregator, IEnumerable<CommandNode> allNodes = null)
        {
            InitializeComponent();

            _allVariables = new ObservableCollection<VariableViewModel>();
            _variableMap = new Dictionary<string, VariableViewModel>();
            _allNodes = allNodes;
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));

            LoadVariables(allNodes);

            _variablesView = CollectionViewSource.GetDefaultView(_allVariables);
            _variablesView.Filter = FilterVariables;
            VariablesDataGrid.ItemsSource = _variablesView;

            Loaded += VariableManagerWindow_Loaded;
            Unloaded += VariableManagerWindow_Unloaded;
        }

        private void VariableManagerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _isWindowActive = true;
            SubscribeToEvents();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                RefreshVariableValues();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void VariableManagerWindow_Unloaded(object sender, RoutedEventArgs e)
        {
            _isWindowActive = false;
            UnsubscribeFromEvents();
        }

        private void SubscribeToEvents()
        {
            try
            {
                UnsubscribeFromEvents();

                if (_eventAggregator != null && _isWindowActive)
                {
                    _updateToken = _eventAggregator.GetEvent<NodeExecutionResultUpdatedEvent>()
                        .Subscribe(OnNodeExecutionResultUpdated, ThreadOption.UIThread, true);

                    _isSubscribed = true;
                }
            }
            catch (Exception ex)
            {
                // 静默处理异常
            }
        }

        private void UnsubscribeFromEvents()
        {
            try
            {
                if (_updateToken != null && _eventAggregator != null && _isSubscribed)
                {
                    _eventAggregator.GetEvent<NodeExecutionResultUpdatedEvent>().Unsubscribe(_updateToken);
                }
            }
            catch (Exception ex)
            {
                // 静默处理异常
            }
            finally
            {
                _updateToken = null;
                _isSubscribed = false;
            }
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);

            if (!_isSubscribed && _isWindowActive)
            {
                SubscribeToEvents();
            }

            RefreshVariableValues();
        }

        public void ForceRefresh()
        {
            try
            {
                if (!_isWindowActive) return;

                SubscribeToEvents();
                RefreshVariableValues();
                _variablesView?.Refresh();
            }
            catch (Exception ex)
            {
                // 静默处理异常
            }
        }

        public void RefreshData(IEnumerable<CommandNode> allNodes)
        {
            try
            {
                _allNodes = allNodes;
                LoadVariables(allNodes);
                _variablesView?.Refresh();

                if (!_isSubscribed && _isWindowActive)
                {
                    SubscribeToEvents();
                }
            }
            catch (Exception ex)
            {
                // 静默处理异常
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
            try
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(() => OnNodeExecutionResultUpdated(args)));
                    return;
                }

                if (!_isWindowActive || !_isSubscribed) return;
                if (args?.OutputParameters?.Variables == null) return;

                bool hasUpdates = false;

                foreach (var param in args.OutputParameters.Variables)
                {
                    if (!string.IsNullOrEmpty(args.NodeId))
                    {
                        string exactKey = $"{args.DeviceName}.{args.CommandName}.{param.Name}.{args.NodeId}";
                        if (_variableMap.TryGetValue(exactKey, out var exactVariable))
                        {
                            UpdateVariableValue(exactVariable, param);
                            hasUpdates = true;
                            continue;
                        }
                    }

                    string key = $"{args.DeviceName}.{args.CommandName}.{param.Name}";
                    var matchingVariables = _variableMap.Where(kvp => kvp.Key.StartsWith(key)).ToList();

                    foreach (var kvp in matchingVariables)
                    {
                        UpdateVariableValue(kvp.Value, param);
                        hasUpdates = true;
                    }
                }

                if (hasUpdates)
                {
                    _variablesView?.Refresh();
                }
            }
            catch (Exception ex)
            {
                // 静默处理异常
            }
        }

        private void UpdateVariableValue(VariableViewModel variable, ParameterBase param)
        {
            variable.VariableValue = param.Value;

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

            var uniqueVariables = new Dictionary<string, (CommandNode Node, ParameterBase Param, DateTime? LastExecutionTime)>();
            var allNodesFlattened = GetAllNodesRecursive(allNodes);

            foreach (var node in allNodesFlattened)
            {
                if (node.DeviceCommand != null)
                {
                    string deviceName = node.DeviceName;
                    string commandName = node.CommandName;

                    DeviceParameters outputParams = null;
                    DateTime? lastExecutionTime = node.LastExecutionResult?.ExecutionTime;

                    if (node.LastOutputParameters?.Variables != null && node.LastOutputParameters.Variables.Count > 0)
                    {
                        outputParams = node.LastOutputParameters;
                    }
                    else if (node.DeviceCommand.OutputParameters?.Variables != null && node.DeviceCommand.OutputParameters.Variables.Count > 0)
                    {
                        outputParams = node.DeviceCommand.OutputParameters;
                        lastExecutionTime = null;
                    }

                    if (outputParams?.Variables != null)
                    {
                        foreach (var param in outputParams.Variables)
                        {
                            if (param.IsVisibleInVariableManager)
                            {
                                string uniqueKey = $"{deviceName}.{commandName}.{param.Name}";

                                if (uniqueVariables.TryGetValue(uniqueKey, out var existing))
                                {
                                    bool shouldReplace = false;

                                    if (lastExecutionTime.HasValue && !existing.LastExecutionTime.HasValue)
                                    {
                                        shouldReplace = true;
                                    }
                                    else if (lastExecutionTime.HasValue && existing.LastExecutionTime.HasValue)
                                    {
                                        shouldReplace = lastExecutionTime.Value > existing.LastExecutionTime.Value;
                                    }
                                    else if (!lastExecutionTime.HasValue && !existing.LastExecutionTime.HasValue)
                                    {
                                        shouldReplace = false;
                                    }

                                    if (shouldReplace)
                                    {
                                        uniqueVariables[uniqueKey] = (node, param, lastExecutionTime);
                                    }
                                }
                                else
                                {
                                    uniqueVariables[uniqueKey] = (node, param, lastExecutionTime);
                                }
                            }
                        }
                    }
                }
            }

            foreach (var kvp in uniqueVariables)
            {
                var (node, param, lastExecutionTime) = kvp.Value;
                string deviceName = node.DeviceName;
                string commandName = node.CommandName;

                var variable = CreateVariableViewModel(deviceName, commandName, param, false, node.NodeId);

                variable.HasExecutionData = lastExecutionTime.HasValue;
                variable.LastExecutionTime = lastExecutionTime;

                _allVariables.Add(variable);

                string exactKey = $"{deviceName}.{commandName}.{param.Name}.{node.NodeId}";
                string compatKey = $"{deviceName}.{commandName}.{param.Name}";

                _variableMap[exactKey] = variable;
                if (!_variableMap.ContainsKey(compatKey))
                {
                    _variableMap[compatKey] = variable;
                }
            }

            LoadCustomVariables();
        }

        private IEnumerable<CommandNode> GetAllNodesRecursive(IEnumerable<CommandNode> nodes)
        {
            if (nodes == null) yield break;

            foreach (var node in nodes)
            {
                yield return node;

                if (node.HasChildren)
                {
                    foreach (var child in GetAllNodesRecursive(node.Children))
                    {
                        yield return child;
                    }
                }
            }
        }

        private VariableViewModel CreateVariableViewModel(string deviceName, string commandName, ParameterBase param, bool isCustom, string nodeId = null)
        {
            string typeName = GetParameterTypeName(param);
            object value = param.Value;

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
                            variable.VariableValue = param.Value;

                            if (param is NumberParameter numParam && param.Value is double d)
                            {
                                variable.VariableValue = Math.Round(d, numParam.DecimalPlaces);
                            }
                        }
                    }
                }
            }

            _variablesView?.Refresh();
        }

        private void LoadCustomVariables()
        {
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

            VariablesDataGrid.SelectedItem = newVariable;
            VariablesDataGrid.ScrollIntoView(newVariable);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                var row = VariablesDataGrid.ItemContainerGenerator.ContainerFromItem(newVariable) as DataGridRow;
                if (row != null)
                {
                    var cell = GetCell(VariablesDataGrid, row, 2);
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

            if (ShowCustomOnlyCheckBox.IsChecked == true && !variable.IsCustomVariable)
                return false;

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
            IsClosed = true;
            _isWindowActive = false;
            UnsubscribeFromEvents();
            base.OnClosed(e);
        }
    }
}