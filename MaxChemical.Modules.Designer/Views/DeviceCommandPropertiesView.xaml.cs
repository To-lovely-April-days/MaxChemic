using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.ViewModels;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class DeviceCommandPropertiesView : UserControl
    {
        private ObservableCollection<ParamItem> _paramItems = new ObservableCollection<ParamItem>();
        public class ParamItem
        {
            public string Name { get; set; }
            public string Value { get; set; }
        }

        public DeviceCommandPropertiesView()
        {
            InitializeComponent();
            this.Loaded += OnLoaded;
            this.DataContextChanged += OnDataContextChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            TryBuildParams();
            HookDeviceSelectionChange();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            TryBuildParams();
            HookDeviceSelectionChange();
        }

        private void HookDeviceSelectionChange()
        {
            if (DataContext is CommandNode node)
            {
                // 当 BoundDeviceId 变化时，更新 BoundDeviceName 与摘要
                node.PropertyChanged -= Node_PropertyChanged;
                node.PropertyChanged += Node_PropertyChanged;
            }
        }

        private void Node_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is CommandNode node && e.PropertyName == nameof(CommandNode.BoundDeviceId))
            {
                // 找到显示名字典（从父VM）
                if (FindFlowVm(out var vm))
                {
                    // 简化：从 vm.DeviceOptionsForSelectedNode 找显示名
                    var disp = vm.DeviceOptionsForSelectedNode?.FirstOrDefault(o => o.Id == node.BoundDeviceId)?.Display;
                    node.BoundDeviceName = string.IsNullOrEmpty(disp) ? node.BoundDeviceId : disp;
                }
            }
        }

        private void TryBuildParams()
        {
            _paramItems.Clear();

            if (DataContext is CommandNode node && node.DeviceCommand?.InputParameters?.Variables != null)
            {
                var defs = node.DeviceCommand.InputParameters.Variables;
                var overrides = node.ParameterOverrides ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var def in defs)
                {
                    var name = def.Name;
                    var val = overrides.TryGetValue(name, out var ov)
                        ? ov
                        : ToInvariantString(def.Value);

                    _paramItems.Add(new ParamItem { Name = name, Value = val });
                }

                ParamsItems.ItemsSource = _paramItems;

                // 即时提交：监听每个 ParamItem 的变化
                foreach (var p in _paramItems)
                {
                    // 使用简单事件：TextBox TwoWay 已经绑定到 ParamItem.Value，此处统一提交一次
                }

                // 订阅集合项变化，通过 TextBox 的 UpdateSourceTrigger=PropertyChanged，下面统一提交
                this.ParamsItems.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((s, e) =>
                {
                    ApplyParamsToNode();
                }));
            }
            else
            {
                ParamsItems.ItemsSource = null;
            }
        }

        private void ApplyParamsToNode()
        {
            if (DataContext is not CommandNode node) return;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in _paramItems)
                map[item.Name] = item.Value;

            node.ApplyParameterOverrides(map);
        }

        private static string ToInvariantString(object value)
        {
            if (value == null) return string.Empty;
            return value is IFormattable f
                ? f.ToString(null, CultureInfo.InvariantCulture)
                : value.ToString();
        }

        private bool FindFlowVm(out FlowDesignerViewModel vm)
        {
            vm = null;
            System.Windows.DependencyObject parent = this; // 关键：用 DependencyObject
            while (parent != null)
            {
                if (parent is FlowDesignerView view && view.DataContext is FlowDesignerViewModel m)
                {
                    vm = m;
                    return true;
                }
                parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
            }
            return false;
        }
    }
}