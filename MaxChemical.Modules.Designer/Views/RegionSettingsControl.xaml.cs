using System;
using System.Windows.Controls;
using MaxChemical.Core;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.Designer.ViewModels;
using MaxChemical.Modules.Designer.ViewModels.MaxChemical.Modules.Designer.ViewModels;
using Prism.Events;
using Prism.Ioc;

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
            this.DataContext = new RegionSettingsViewModel(
                ContainerLocator.Container.Resolve<IEventAggregator>(),
                ContainerLocator.Container.Resolve<ILocalizationService>(),
                layout);
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