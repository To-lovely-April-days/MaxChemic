using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MaxChemical.Modules.DOE.Data;

namespace MaxChemical.Modules.DOE.Views
{
    public partial class DOEOverviewView : UserControl
    {
        public DOEOverviewView() { InitializeComponent(); }

        private void RecentList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGrid dg && dg.SelectedItem is DOEBatchSummary summary)
            {
                if (Window.GetWindow(this)?.DataContext is ViewModels.DOEMainViewModel mainVm)
                    mainVm.NavigateToExecution(summary.BatchId);
            }
        }
        private void NewProjectCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (Window.GetWindow(this)?.DataContext is ViewModels.DOEMainViewModel mainVm)
                mainVm.CreateProjectCommand.Execute();
        }
        private void ProjectCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ViewModels.ProjectCardItem card)
            {
                if (DataContext is ViewModels.DOEOverviewViewModel vm)
                {
                    vm.SelectProjectCommand.Execute(card.ProjectId);
                }
            }
        }
    }
}
