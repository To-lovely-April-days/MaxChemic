using System.Windows.Controls;
using MaxChemical.Modules.DOE.ViewModels;

namespace MaxChemical.Modules.DOE.Views
{
    public partial class DOEAnalysisView : UserControl
    {
        public DOEAnalysisView()
        {
            InitializeComponent();
        }

        public DOEAnalysisView(DOEAnalysisViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
