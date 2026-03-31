
using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Services;
using MaxChemical.Modules.DOE.ViewModels;
using MaxChemical.Modules.DOE.Views;
using Prism.Ioc;
using Prism.Modularity;


namespace MaxChemical.Modules.DOE
{
    /// <summary>
    /// DOE 模块入口 — P1 阶段: 注册设计服务、数据仓储和设计向导视图
    /// </summary>
    public class DOEModule : IModule
    {
        public void OnInitialized(IContainerProvider containerProvider) { }

        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            // 数据仓储
            containerRegistry.RegisterSingleton<IDOERepository, DOERepository>();

            // 核心服务
            containerRegistry.RegisterSingleton<IDOEDesignService, DOEDesignService>();
            containerRegistry.RegisterSingleton<IDOEExecutionService, DOEBatchExecutor>();
            containerRegistry.RegisterSingleton<IGPRModelService, GPRModelService>();
            containerRegistry.RegisterSingleton<IDOEAnalysisService, DOEAnalysisService>();
            containerRegistry.RegisterSingleton<DOEExportService>();

            // ViewModel (5个，原来6个)
            containerRegistry.Register<DOEMainViewModel>();
            containerRegistry.Register<DOEOverviewViewModel>();
            containerRegistry.Register<DOEDesignWizardViewModel>();
            containerRegistry.Register<DOEExecutionDashboardViewModel>();
            containerRegistry.Register<DOEModelAnalysisViewModel>();      
            containerRegistry.Register<DOEHistoryViewModel>();
            // 移除: containerRegistry.Register<DOEAnalysisViewModel>();
            // 移除: containerRegistry.Register<DOEGPRModelViewModel>();

            // View (5个，原来7个)
            containerRegistry.Register<DOEMainView>();
            containerRegistry.Register<DOEOverviewView>();
            containerRegistry.Register<DOEDesignWizardView>();
            containerRegistry.Register<DOEExecutionDashboardView>();
            containerRegistry.Register<DOEModelAnalysisView>();            
            containerRegistry.Register<DOEHistoryView>();
            // 移除: containerRegistry.Register<DOEAnalysisView>();
            // 移除: containerRegistry.Register<DOEGPRModelView>();
        }
    }
}
