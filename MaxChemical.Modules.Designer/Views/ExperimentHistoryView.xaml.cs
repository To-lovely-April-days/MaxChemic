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
    /// ExperimentHistoryView.xaml 的交互逻辑
    /// </summary>
    public partial class ExperimentHistoryView : UserControl
    {
        public ExperimentHistoryView()
        {
            InitializeComponent();
        }

        private void DataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DataContext is ExperimentHistoryViewModel viewModel)
            {
                viewModel.OnSelectedExperimentChanged();
            }
        }
    }
}
