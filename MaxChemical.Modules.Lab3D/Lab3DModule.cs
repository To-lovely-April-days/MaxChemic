using MaxChemical.Modules.Lab3D.Services;
using MaxChemical.Modules.Lab3D.Views;
using Prism.Ioc;
using Prism.Modularity;

namespace MaxChemical.Modules.Lab3D;

public sealed class Lab3DModule : IModule
{
    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        containerRegistry.RegisterSingleton<ILab3DWindowService, Lab3DWindowService>();
        containerRegistry.RegisterSingleton<ILab3DRealtimeService, Lab3DRealtimeService>();
        containerRegistry.RegisterSingleton<ILab3DDeviceQueryService, Lab3DDeviceQueryService>();
        containerRegistry.RegisterSingleton<ILab3DInspectionService, Lab3DInspectionService>();
        containerRegistry.Register<Lab3DWindow>();
    }

    public void OnInitialized(IContainerProvider containerProvider)
    {
        containerProvider.Resolve<ILab3DRealtimeService>();
    }
}
