using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MaxChemical.Modules.DOE.ViewModels;

namespace MaxChemical.Modules.DOE.Views
{
    public partial class DOEDesignWizardView : Window
    {
        private readonly DOEDesignWizardViewModel _viewModel;

        public DOEDesignWizardView(DOEDesignWizardViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            _viewModel = viewModel;

            // Esc 键关闭窗口（Drawer 打开时先关 Drawer）
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    if (DrawerPanel.Visibility == Visibility.Visible)
                        CloseDrawer();
                    else
                        TryClose();
                }
            };

            // 订阅保存完成事件，自动关闭窗口
            viewModel.BatchSaved += (sender, batchId) =>
            {
                MessageBox.Show($"DOE 方案已保存成功！\n批次ID: {batchId}", "保存成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
                Close();
            };
        }

        /// <summary>
        /// 关闭按钮点击
        /// </summary>
        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            TryClose();
        }

        /// <summary>
        /// 确认关闭（有数据时提示）
        /// </summary>
        private void TryClose()
        {
            if (DataContext is DOEDesignWizardViewModel vm && vm.CurrentStep > 1)
            {
                var result = MessageBox.Show("确定要关闭向导吗？未保存的配置将丢失。",
                    "确认关闭", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (result != MessageBoxResult.Yes)
                    return;
            }
            DialogResult = false;
            Close();
        }

        /// <summary>
        /// 拖拽标题栏移动窗口
        /// </summary>
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        /// <summary>
        /// 左侧导航步骤点击跳转
        /// </summary>
        private void NavStep_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string tagStr && int.TryParse(tagStr, out int step))
            {
                if (DataContext is DOEDesignWizardViewModel vm)
                {
                    // 只允许跳到已完成的步骤或当前步骤+1
                    if (step <= vm.CurrentStep)
                    {
                        vm.CurrentStep = step;
                    }
                }
            }
        }

        // ══════════════════════════════════════════
        //  Drawer 面板逻辑
        // ══════════════════════════════════════════

        /// <summary>
        /// 打开 Drawer：隐藏所有方法参数面板，只显示对应的
        /// </summary>
        private void OpenDrawer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe) return;
            var method = fe.Tag?.ToString() ?? "";

            // 隐藏所有
            DrawerPB.Visibility = Visibility.Collapsed;
            DrawerFF.Visibility = Visibility.Collapsed;
            DrawerFull.Visibility = Visibility.Collapsed;
            DrawerCCD.Visibility = Visibility.Collapsed;
            DrawerBBD.Visibility = Visibility.Collapsed;
            DrawerDOpt.Visibility = Visibility.Collapsed;
            DrawerTaguchi.Visibility = Visibility.Collapsed;
            DrawerCustom.Visibility = Visibility.Collapsed;

            // 显示对应面板 + 设置标题
            switch (method)
            {
                case "PlackettBurman":
                    DrawerPB.Visibility = Visibility.Visible;
                    DrawerTitle.Text = "Plackett-Burman";
                    break;
                case "FractionalFactorial":
                    DrawerFF.Visibility = Visibility.Visible;
                    DrawerTitle.Text = _viewModel.SetDrawerTitleLocalizationTxt("Doe_Guide_Part_Title", "部分因子 (Fractional)");
                    break;
                case "FullFactorial":
                    DrawerFull.Visibility = Visibility.Visible;
                    DrawerTitle.Text = _viewModel.SetDrawerTitleLocalizationTxt("Doe_Guide_Full_Title", "全因子 (Full Factorial)");
                    break;
                case "CCD":
                    DrawerCCD.Visibility = Visibility.Visible;
                    DrawerTitle.Text = _viewModel.SetDrawerTitleLocalizationTxt("Doe_Guide_CCD_Title", "CCD 中心复合设计");
                    break;
                case "BoxBehnken":
                    DrawerBBD.Visibility = Visibility.Visible;
                    DrawerTitle.Text = "Box-Behnken";
                    break;
                case "DOptimal":
                    DrawerDOpt.Visibility = Visibility.Visible;
                    DrawerTitle.Text = "D-Optimal";
                    break;
                case "Taguchi":
                    DrawerTaguchi.Visibility = Visibility.Visible;
                    DrawerTitle.Text = _viewModel.SetDrawerTitleLocalizationTxt("Doe_Guide_Tag_Title", "Taguchi 正交设计");
                    break;
                case "Custom":
                    DrawerCustom.Visibility = Visibility.Visible;
                    DrawerTitle.Text = _viewModel.SetDrawerTitleLocalizationTxt("Doe_Guide_Cust_Title", "自定义导入");
                    break;
                default:
                    return;
            }

            DrawerOverlay.Visibility = Visibility.Visible;
            DrawerPanel.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// 关闭 Drawer（点击遮罩、关闭按钮、确认按钮）
        /// </summary>
        private void CloseDrawer_Click(object sender, RoutedEventArgs e)
        {
            CloseDrawer();
        }

        private void CloseDrawer()
        {
            DrawerOverlay.Visibility = Visibility.Collapsed;
            DrawerPanel.Visibility = Visibility.Collapsed;
        }

    }
}