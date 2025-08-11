using MaxChemical.Modules.Designer.Models;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

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

            // 参数列表
            var items = new List<ParamItem>();
            var vars = _node.DeviceCommand?.InputParameters?.Variables;
            if (vars != null)
            {
                foreach (var v in vars)
                    items.Add(new ParamItem { Name = v.Name, Value = v.Value.ToString() });
            }
            ParamsItems.ItemsSource = items;

            // 若没有参数，隐藏参数面板
            if (items.Count == 0)
                ParamsPanel.Visibility = Visibility.Collapsed;
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

                // 保存参数
                var list = ParamsItems.ItemsSource as IEnumerable<ParamItem>;
                if (list != null && _node.DeviceCommand?.InputParameters?.Variables != null)
                {
                    foreach (var item in list)
                    {
                        var v = _node.DeviceCommand.InputParameters.Variables.FirstOrDefault(x => x.Name == item.Name);
                        if (v != null) v.Value = item.Value;
                    }
                }

                // 生成摘要，刷新节点 UI
                _node.RebuildParameterSummary();
            }

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
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
                Title = title
            };
            var result = win.ShowDialog();
            return result == true ? (win.DeviceCombo.SelectedValue as string) : null;
        }
    }
}