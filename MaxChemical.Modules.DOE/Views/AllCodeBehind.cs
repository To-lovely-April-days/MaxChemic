// ═══ DOEExecutionDashboardView.xaml.cs ═══
// 文件路径: Views/DOEExecutionDashboardView.xaml.cs
/*
using System.Windows.Controls;
namespace MaxChemical.Modules.DOE.Views
{
    public partial class DOEExecutionDashboardView : UserControl
    {
        public DOEExecutionDashboardView() { InitializeComponent(); }
    }
}
*/

// ═══ DOEAnalysisView.xaml.cs ═══
// 文件路径: Views/DOEAnalysisView.xaml.cs
/*
using System.Windows.Controls;
namespace MaxChemical.Modules.DOE.Views
{
    public partial class DOEAnalysisView : UserControl
    {
        public DOEAnalysisView() { InitializeComponent(); }
    }
}
*/

// ═══ DOEHistoryView.xaml.cs ═══
// 文件路径: Views/DOEHistoryView.xaml.cs
/*
using System.Windows.Controls;
using System.Windows.Input;
using MaxChemical.Modules.DOE.Data;
namespace MaxChemical.Modules.DOE.Views
{
    public partial class DOEHistoryView : UserControl
    {
        public DOEHistoryView() { InitializeComponent(); }

        private void BatchList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGrid dg && dg.SelectedItem is DOEBatchSummary summary)
                if (Window.GetWindow(this)?.DataContext is ViewModels.DOEMainViewModel mainVm)
                    mainVm.NavigateToExecution(summary.BatchId);
        }
    }
}
*/

// ═══ DOEGPRModelView.xaml.cs ═══
// 文件路径: Views/DOEGPRModelView.xaml.cs
/*
using System.Windows.Controls;
namespace MaxChemical.Modules.DOE.Views
{
    public partial class DOEGPRModelView : UserControl
    {
        public DOEGPRModelView() { InitializeComponent(); }
    }
}
*/
