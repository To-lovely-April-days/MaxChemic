using System;
using System.Windows.Controls;
using MaxChemical.Core;
using MaxChemical.Modules.Designer.ViewModels;
using MaxChemical.Modules.Designer.ViewModels.MaxChemical.Modules.Designer.ViewModels;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class RegionSettingsControl : UserControl
    {
        public RegionSettingsControl()
        {
            InitializeComponent();
        }

        public RegionSettingsControl(RegionLayout layout)
        {
            InitializeComponent();
            this.DataContext = new RegionSettingsViewModel(layout);
        }

        public RegionSettingsViewModel ViewModel => DataContext as RegionSettingsViewModel;

        public void SetCanvasSizeProvider(Func<(double Width, double Height)> provider)
        {
            if (ViewModel != null)
            {
                ViewModel.GetCanvasSize = provider;
            }
        }

        public RegionLayout GetResult()
        {
            return ViewModel?.Result;
        }
    }
}