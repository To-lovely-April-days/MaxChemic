using System.Windows.Controls;
using MaxChemical.Modules.DOE.ViewModels;

namespace MaxChemical.Modules.DOE.Views
{
    public partial class DOEExecutionDashboardView : UserControl
    {
        /// <summary>
        /// XAML 设计器要求的无参构造函数
        /// </summary>
        public DOEExecutionDashboardView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// DI 注入构造函数
        /// </summary>
        public DOEExecutionDashboardView(DOEExecutionDashboardViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}
