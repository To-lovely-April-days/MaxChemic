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
    /// WaitNodeControlView.xaml 的交互逻辑
    /// </summary>
    public partial class WaitNodeControlView : UserControl
    {
        public event EventHandler<CommandNode> NodeDeleteRequested;
        private readonly ILogService _logger;
        public WaitNodeControlView()
        {
            InitializeComponent();
            _logger = new LogService().ForContext<WaitNodeControlView>();
        }

        private void Node_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && DataContext is CommandNode node)
            {
                var routedEventArgs = new RoutedEventArgs(NodeClickEvent, node);
                RaiseEvent(routedEventArgs);
                e.Handled = true;
                _logger.LogDebug("等待节点被点击: {NodeName}", node.DisplayName);
            }
        }
        private void DeleteButton_Click(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (DataContext is CommandNode node)
                {
                    _logger.LogDebug("请求删除等待节点: {NodeName}", node.DisplayName);
                    NodeDeleteRequested?.Invoke(this, node);
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除等待节点失败");
            }
        }
        // 定义路由事件
        public static readonly RoutedEvent NodeClickEvent = EventManager.RegisterRoutedEvent(
            "NodeClick", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(WaitNodeControlView));

        public event RoutedEventHandler NodeClick
        {
            add { AddHandler(NodeClickEvent, value); }
            remove { RemoveHandler(NodeClickEvent, value); }
        }
    }
}
