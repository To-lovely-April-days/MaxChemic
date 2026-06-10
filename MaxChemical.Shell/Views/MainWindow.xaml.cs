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
    /// MainWindow.xaml 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            SetWindowSizeToCurrentScreen();
        }

        private void SetWindowSizeToCurrentScreen()
        {
            try
            {
                // 获取当前屏幕的工作区域
                var workingArea = SystemParameters.WorkArea;

                // 设置窗口位置和大小
                this.Left = workingArea.Left;
                this.Top = workingArea.Top;
                this.Width = workingArea.Width;
                this.Height = workingArea.Height;
                this.WindowState = WindowState.Normal;

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"设置窗口大小失败: {ex.Message}");
                // 如果出错，使用默认大小
                this.Width = 1200;
                this.Height = 800;
            }
        }
    }
}
