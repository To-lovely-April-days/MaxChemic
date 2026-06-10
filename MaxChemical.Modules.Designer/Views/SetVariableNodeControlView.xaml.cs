using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Models;

namespace MaxChemical.Modules.Designer.Views
{
    /// <summary>
    /// SetVariableNodeControlView.xaml 的交互逻辑
    /// </summary>
    public partial class SetVariableNodeControlView : UserControl
    {
        public event EventHandler<CommandNode> NodeDeleteRequested;
        private readonly ILogService _logger;

        public SetVariableNodeControlView()
        {
            InitializeComponent();
            _logger = new LogService().ForContext<SetVariableNodeControlView>();
        }

        private void Node_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && DataContext is CommandNode node)
            {
                var routedEventArgs = new RoutedEventArgs(NodeClickEvent, node);
                RaiseEvent(routedEventArgs);
                e.Handled = true;
                _logger.LogDebug("设置变量节点被点击: {NodeName}", node.DisplayName);
            }
        }
        private void DeleteButton_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (DataContext is CommandNode node)
                {
                    _logger.LogDebug("请求删除设置变量节点: {NodeName}", node.DisplayName);
                    NodeDeleteRequested?.Invoke(this, node);
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除设置变量节点失败");
            }
        }
        // 新添加：刷新变量显示的方法
        public void RefreshVariableDisplay()
        {
            try
            {
                if (DataContext is not CommandNode node) return;
                if (node.LogicCommand?.Properties == null) return;

                var variableName = node.LogicCommand.Properties.GetValueOrDefault("VariableName")?.ToString() ?? "";
                var variableValue = node.LogicCommand.Properties.GetValueOrDefault("VariableValue")?.ToString() ?? "";

                // 构造显示文本
                string displayText;
                if (string.IsNullOrWhiteSpace(variableName) && string.IsNullOrWhiteSpace(variableValue))
                {
                    displayText = "未配置";
                }
                else if (string.IsNullOrWhiteSpace(variableName))
                {
                    displayText = "请设置变量名";
                }
                else if (string.IsNullOrWhiteSpace(variableValue))
                {
                    displayText = $"{variableName} = 请设置值";
                }
                else
                {
                    displayText = $"{variableName} = {variableValue}";
                }

                // 找到显示变量信息的TextBlock并更新
                // 根据你的XAML结构，这个TextBlock应该在Grid的第1列的StackPanel中
                var stackPanel = FindVisualChild<StackPanel>(this);
                if (stackPanel != null)
                {
                    var textBlocks = FindVisualChildren<TextBlock>(stackPanel).ToList();
                    var variableInfoTextBlock = textBlocks.FirstOrDefault(tb =>
                        tb.FontSize == 11 && tb.Foreground is SolidColorBrush brush &&
                        brush.Color == Color.FromRgb(0x99, 0x99, 0x99));

                    if (variableInfoTextBlock != null)
                    {
                        variableInfoTextBlock.Text = displayText;
                    }
                }

                System.Diagnostics.Debug.WriteLine($"RefreshVariableDisplay: {displayText}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"RefreshVariableDisplay error: {ex.Message}");
            }
        }

        // 辅助方法：查找可视化子元素
        private T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result)
                    return result;

                var childResult = FindVisualChild<T>(child);
                if (childResult != null)
                    return childResult;
            }
            return null;
        }

        private IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result)
                    yield return result;

                foreach (var childResult in FindVisualChildren<T>(child))
                    yield return childResult;
            }
        }
        public static readonly RoutedEvent NodeClickEvent = EventManager.RegisterRoutedEvent(
            "NodeClick", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(SetVariableNodeControlView));

        public event RoutedEventHandler NodeClick
        {
            add { AddHandler(NodeClickEvent, value); }
            remove { RemoveHandler(NodeClickEvent, value); }
        }
    }
}
