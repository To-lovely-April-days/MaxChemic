using MaxChemical.Modules.Designer.ViewModels;
using OxyPlot;
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class ExperimentDetailWindow : Window
    {
        public ExperimentDetailWindow()
        {
            InitializeComponent();

            Closing += (s, e) =>
            {
                if (DataContext is ExperimentDetailWindowViewModel vm)
                    vm.ChartModel = new PlotModel();
            };
        }

        /// <summary>
        /// 接收已经加载好数据的 ViewModel，直接展示
        /// </summary>
        public ExperimentDetailWindow(ExperimentDetailWindowViewModel vm, Window? owner = null) : this()
        {
            if (owner != null)
                Owner = owner;

            DataContext = vm;

            Loaded += (s, e) => CenterOnScreen();
        }

        // ═══ 窗口定位 ═══

        private void CenterOnScreen()
        {
            if (Owner != null)
            {
                Left = Owner.Left + (Owner.ActualWidth - ActualWidth) / 2;
                Top = Owner.Top + (Owner.ActualHeight - ActualHeight) / 2;
            }
            else
            {
                Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
                Top = (SystemParameters.PrimaryScreenHeight - ActualHeight) / 2;
            }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // ═══ 折叠/展开 ═══

        private void ToggleNodes_Click(object sender, MouseButtonEventArgs e)
        {
            if (NodeGrid.Visibility == Visibility.Visible)
            {
                NodeGrid.Visibility = Visibility.Collapsed;
                NodeChevron.Data = Geometry.Parse("M0,0 L4,0 L4,8");
                NodeChevron.Stroke = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
                NodeChevron.Width = 5;
                NodeChevron.Height = 8;
            }
            else
            {
                NodeGrid.Visibility = Visibility.Visible;
                NodeChevron.Data = Geometry.Parse("M0,0 L4,4 L8,0");
                NodeChevron.Stroke = new SolidColorBrush(Color.FromRgb(0x2D, 0x3A, 0x8C));
                NodeChevron.Width = 8;
                NodeChevron.Height = 5;
            }
        }

        private void ToggleParams_Click(object sender, MouseButtonEventArgs e)
        {
            if (ParamGrid.Visibility == Visibility.Visible)
            {
                ParamGrid.Visibility = Visibility.Collapsed;
                ParamChevron.Data = Geometry.Parse("M0,0 L4,0 L4,8");
                ParamChevron.Stroke = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
                ParamChevron.Width = 5;
                ParamChevron.Height = 8;
            }
            else
            {
                ParamGrid.Visibility = Visibility.Visible;
                ParamChevron.Data = Geometry.Parse("M0,0 L4,4 L8,0");
                ParamChevron.Stroke = new SolidColorBrush(Color.FromRgb(0x2D, 0x3A, 0x8C));
                ParamChevron.Width = 8;
                ParamChevron.Height = 5;
            }
        }
    }
}