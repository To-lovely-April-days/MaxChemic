using MaxChemical.Core;
using MaxChemical.Shell.ViewModels;
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
using System.Windows.Shapes;

namespace MaxChemical.Shell.Views
{
    /// <summary>
    /// LogMessageViewer.xaml 的交互逻辑
    /// </summary>
    public partial class LogMessageViewer : Window
    {
        public LogMessageViewer()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 标题栏鼠标按下事件 拖动窗体
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void DragMoveBorder_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
            // 标记已处理，中断冒泡
            e.Handled = true;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            //LogMessageViewerViewModel viewModel = this.DataContext as LogMessageViewerViewModel;
            //viewModel.LogMessages = new System.Collections.ObjectModel.ObservableCollection<LogMessage>(LogMessageCache.Instance.Clone());
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            (DataContext as LogMessageViewerViewModel)?.Dispose();
        }
    }
}
