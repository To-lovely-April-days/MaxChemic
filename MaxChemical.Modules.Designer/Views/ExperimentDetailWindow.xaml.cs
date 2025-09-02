// MaxChemical.Modules.Designer/Views/ExperimentDetailWindow.xaml.cs
using MaxChemical.Modules.Designer.ViewModels;
using System.Windows;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class ExperimentDetailWindow : Window
    {
        public ExperimentDetailWindow()
        {
            InitializeComponent();
        }

        public ExperimentDetailWindow(string experimentId) : this()
        {
            // 设置实验ID - 在窗口加载完成后设置
            Loaded += (s, e) =>
            {
                if (DataContext is ExperimentDetailWindowViewModel viewModel)
                {
                    viewModel.SetExperimentId(experimentId);
                }
            };
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}