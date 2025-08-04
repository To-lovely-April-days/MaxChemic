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
using MaxChemical.Modules.Designer.Models;

namespace MaxChemical.Modules.Designer.Views
{
    /// <summary>
    /// WaitNodeControlView.xaml 的交互逻辑
    /// </summary>
    public partial class WaitNodeControlView : UserControl
    {
        public WaitNodeControlView()
        {
            InitializeComponent();
        }

        private void Node_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && DataContext is CommandNode node)
            {
                // 触发节点选中事件
                var routedEventArgs = new RoutedEventArgs(NodeClickEvent, node);
                RaiseEvent(routedEventArgs);
                e.Handled = true;
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
