using MaxChemical.Infrastructure.Constants;
using MaxChemical.Modules.Designer.ViewModels;
using MaxChemical.Modules.Designer.Views;
using Prism.Ioc;
using Prism.Modularity;
using Prism.Regions;

public class DesignerModule : IModule
{
    public void OnInitialized(IContainerProvider containerProvider)
    {
        var regionManager = containerProvider.Resolve<IRegionManager>();

        // 在标签页区域注册视图
        regionManager.RegisterViewWithRegion(RegionNames.ProcessDesignerRegion, typeof(ProcessDesignerView));
        regionManager.RegisterViewWithRegion(RegionNames.FlowDesignerRegion, typeof(FlowDesignerView));
    }

    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 注册设计器模块的服务和视图
        containerRegistry.Register<ProcessDesignerViewModel>();
        containerRegistry.Register<FlowDesignerViewModel>();

        // 注册视图类型
        containerRegistry.Register<ProcessDesignerView>();
        containerRegistry.Register<FlowDesignerView>();
        containerRegistry.Register<ExperimentHistoryView>();
        containerRegistry.Register<ExperimentHistoryViewModel>();
        containerRegistry.Register<ExperimentStatisticsView>();
        containerRegistry.Register<ExperimentStatisticsViewModel>();
        containerRegistry.RegisterSingleton<ExperimentDetailWindow>();
        containerRegistry.Register<ExperimentDetailWindowViewModel>();
    }
}