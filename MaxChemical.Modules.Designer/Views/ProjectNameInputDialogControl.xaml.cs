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
using MaxChemical.Modules.Designer.ViewModels;

namespace MaxChemical.Modules.Designer.Views
{
    /// <summary>
    /// ProjectNameInputDialogControl.xaml 的交互逻辑
    /// </summary>
    public partial class ProjectNameInputDialogControl : UserControl
    {
        public ProjectNameInputDialogControl(string defaultName = null)
        {
            InitializeComponent();

            var viewModel = new ProjectNameInputDialogViewModel(defaultName);

            // 设置焦点动作
            viewModel.SetFocusAction = () =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ProjectNameTextBox.Focus();
                    ProjectNameTextBox.SelectAll();
                }));
            };

            this.DataContext = viewModel;

            // 窗口加载后设置焦点
            this.Loaded += (s, e) => viewModel.SetFocusAction?.Invoke();
        }

        public ProjectNameInputDialogViewModel ViewModel => DataContext as ProjectNameInputDialogViewModel;

        // 公开TitleArea供拖拽使用 - 修复无限递归问题
        public Border GetDragArea() => DragTitleArea;
    }
}
