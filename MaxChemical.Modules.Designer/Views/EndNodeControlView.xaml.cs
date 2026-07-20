using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Models;

namespace MaxChemical.Modules.Designer.Views
{
    /// <summary>
    /// EndNodeControlView.xaml 的交互逻辑（结束指令节点）
    /// </summary>
    public partial class EndNodeControlView : UserControl
    {
        public event EventHandler<CommandNode> NodeDeleteRequested;
        private readonly ILogService _logger;

        public EndNodeControlView()
        {
            InitializeComponent();
            _logger = new LogService().ForContext<EndNodeControlView>();
        }

        private void Node_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && DataContext is CommandNode node)
            {
                var routedEventArgs = new RoutedEventArgs(NodeClickEvent, node);
                RaiseEvent(routedEventArgs);
                e.Handled = true;
                _logger.LogDebug("结束节点被点击: {NodeName}", node.DisplayName);
            }
        }

        private void DeleteButton_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (DataContext is CommandNode node)
                {
                    _logger.LogDebug("请求删除结束节点: {NodeName}", node.DisplayName);
                    NodeDeleteRequested?.Invoke(this, node);
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除结束节点失败");
            }
        }

        public static readonly RoutedEvent NodeClickEvent = EventManager.RegisterRoutedEvent(
            "NodeClick", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(EndNodeControlView));

        public event RoutedEventHandler NodeClick
        {
            add { AddHandler(NodeClickEvent, value); }
            remove { RemoveHandler(NodeClickEvent, value); }
        }
    }
}
