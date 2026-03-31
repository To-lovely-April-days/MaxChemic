using MaxChemical.Modules.DOE.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MaxChemical.Modules.DOE.Views
{
    public partial class DOEModelAnalysisView : UserControl
    {
        public DOEModelAnalysisView()
        {
            InitializeComponent();
        }

        private void ModelTab_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is GPRModelTabItem tab)
            {
                if (DataContext is DOEModelAnalysisViewModel vm)
                    vm.SelectModel(tab.ModelId);
            }
        }

        /// <summary>
        /// 训练数据集 DataGrid 自动列格式化
        /// </summary>
        private void TrainingData_AutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
        {
            if (e.Column is DataGridTextColumn col)
            {
                switch (e.PropertyName)
                {
                    case "#":
                        col.Width = new DataGridLength(40);
                        break;
                    case "来源":
                        col.Width = new DataGridLength(70);
                        break;
                    case "批次名称":
                        col.Width = new DataGridLength(160);
                        break;
                    case "时间":
                        col.Width = new DataGridLength(130);
                        break;
                    case "响应值":
                        col.Width = new DataGridLength(70);
                        break;
                    default:
                        col.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
                        break;
                }
            }
        }
    }
}