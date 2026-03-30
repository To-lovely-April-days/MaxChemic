using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MaxChemical.Modules.DOE.ViewModels;

namespace MaxChemical.Modules.DOE.Views
{
    public partial class DOEGPRModelView : UserControl
    {
        public DOEGPRModelView() { InitializeComponent(); }

        private void ModelTab_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is GPRModelTabItem tab)
            {
                if (DataContext is DOEGPRModelViewModel vm)
                    vm.SelectModel(tab.ModelId);
            }
        }
    }
}
