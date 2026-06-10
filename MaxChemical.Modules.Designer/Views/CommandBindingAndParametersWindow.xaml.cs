using MaxChemical.Modules.Designer.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class CommandBindingAndParametersWindow : Window
    {
        public class DeviceOption
        {
            public string Id { get; set; }       // DeviceId
            public string Display { get; set; }  // 画布显示名（如 泵#2）
        }

        public class ParamItem
        {
            public string Name { get; set; }
            public string Value { get; set; }
        }

        private readonly CommandNode _node;
        private readonly Dictionary<string, string> _displayNames;

        public CommandBindingAndParametersWindow(
            CommandNode node,
            IEnumerable<DeviceOption> options,
            Dictionary<string, string> displayNames)
        {
            InitializeComponent();

            _node = node;
            _displayNames = displayNames ?? new Dictionary<string, string>();

            TitleText.Text = $"绑定设备与参数（{_node?.DeviceCommand?.Name ?? "设备命令"}）";
            DeviceCombo.ItemsSource = options?.ToList() ?? new List<DeviceOption>();

            // 无节点：隐藏参数面板，仅做设备选择（兼容 SelectDevice 调用）
            if (_node == null)
            {
                ParamsPanel.Visibility = Visibility.Collapsed;
                if (DeviceCombo.Items.Count > 0) DeviceCombo.SelectedIndex = 0;
                return;
            }

            // 预选已绑定设备
            if (!string.IsNullOrEmpty(_node.BoundDeviceId))
                DeviceCombo.SelectedValue = _node.BoundDeviceId;
            else if (DeviceCombo.Items.Count > 0)
                DeviceCombo.SelectedIndex = 0;

            // 生成参数编辑项：优先用节点覆盖值，否则用模板默认值
            var items = new List<ParamItem>();
            var defs = _node.DeviceCommand?.InputParameters?.Variables;
            if (defs != null)
            {
                var overrides = _node.ParameterOverrides?.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
                                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var d in defs)
                {
                    var name = d.Name;
                    var valStr = overrides.TryGetValue(name, out var ov)
                        ? ov
                        : ToInvariantString(d.Value);

                    items.Add(new ParamItem { Name = name, Value = valStr });
                }
            }
            ParamsItems.ItemsSource = items;

            if (items.Count == 0)
                ParamsPanel.Visibility = Visibility.Collapsed;
        }

        private static string ToInvariantString(object value)
        {
            if (value == null) return string.Empty;
            return value is IFormattable f
                ? f.ToString(null, CultureInfo.InvariantCulture)
                : value.ToString();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var selectedId = DeviceCombo.SelectedValue as string;
            if (string.IsNullOrEmpty(selectedId))
            {
                MessageBox.Show("请选择要绑定的设备。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_node != null)
            {
                // 保存设备绑定
                _node.BoundDeviceId = selectedId;
                _node.BoundDeviceName = _displayNames != null && _displayNames.TryGetValue(selectedId, out var dn)
                    ? dn
                    : selectedId;

                // 收集参数覆盖值并保存到节点
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (ParamsItems.ItemsSource is IEnumerable<ParamItem> list)
                {
                    foreach (var item in list)
                    {
                        map[item.Name] = item.Value;
                    }
                }
                _node.ApplyParameterOverrides(map);
            }

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 支持拖动窗口
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { /* ignore */ }
            }
        }

        // 你现在要用的方式：带节点，返回是否保存成功
        public static bool ShowForNode(Window owner, CommandNode node, IEnumerable<DeviceOption> options, Dictionary<string, string> displayNames)
        {
            var win = new CommandBindingAndParametersWindow(node, options, displayNames) { Owner = owner };
            return win.ShowDialog() == true;
        }

        // 兼容旧调用（仅选择设备，不编辑参数）
        public static string SelectDevice(Window owner, IEnumerable<DeviceOption> options, string title = "绑定设备")
        {
            var win = new CommandBindingAndParametersWindow(null, options, new Dictionary<string, string>())
            {
                Owner = owner,
            };
            win.TitleText.Text = title;
            var result = win.ShowDialog();
            return result == true ? (win.DeviceCombo.SelectedValue as string) : null;
        }
    }
}