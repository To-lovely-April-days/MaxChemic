using MaxChemical.Modules.Designer.ViewModels;
using System;
using System.Windows;
using System.Windows.Controls;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class ExperimentHistoryView : UserControl
    {
        public ExperimentHistoryView()
        {
            InitializeComponent();
        }

        private ExperimentHistoryViewModel? VM => DataContext as ExperimentHistoryViewModel;

        // ═══ 日期范围选择器变更 ═══

        private void DateRange_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || VM == null) return;
            VM.SearchCommand.Execute();
        }

        // ═══ 状态筛选标签页 ═══

        private void StatusAll_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || VM == null) return;
            VM.IsSuccessful = null;
            VM.SearchCommand.Execute();
        }

        private void StatusSuccess_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || VM == null) return;
            VM.IsSuccessful = true;
            VM.SearchCommand.Execute();
        }

        private void StatusFailed_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || VM == null) return;
            VM.IsSuccessful = false;
            VM.SearchCommand.Execute();
        }

        // ═══ 模式筛选标签页 ═══

        private void ModeAll_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || VM == null) return;
            VM.IsSimulation = null;
            VM.SearchCommand.Execute();
        }

        private void ModeReal_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || VM == null) return;
            VM.IsSimulation = false;
            VM.SearchCommand.Execute();
        }

        private void ModeSimulation_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || VM == null) return;
            VM.IsSimulation = true;
            VM.SearchCommand.Execute();
        }

        // ═══ DataGrid 选择变更 ═══

        private void DataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            VM?.OnSelectedExperimentChanged();
        }

        // ═══ 行内操作按钮（无需预先选中行） ═══

        private void SelectRow(object sender)
        {
            if (sender is Button btn && btn.Tag is MaxChemical.Data.Models.ExperimentRecord record && VM != null)
            {
                VM.SelectedExperiment = record;
                VM.OnSelectedExperimentChanged();
            }
        }

        private void ViewDetail_Click(object sender, RoutedEventArgs e)
        {
            SelectRow(sender);
            if (VM?.ViewDetailsCommand.CanExecute() == true)
                VM.ViewDetailsCommand.Execute();
        }

        private void ExportSelected_Click(object sender, RoutedEventArgs e)
        {
            SelectRow(sender);
            if (VM?.ExportSelectedCommand.CanExecute() == true)
                VM.ExportSelectedCommand.Execute();
        }

        private void DeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            SelectRow(sender);
            if (VM?.DeleteSelectedCommand.CanExecute() == true)
                VM.DeleteSelectedCommand.Execute();
        }
    }
}