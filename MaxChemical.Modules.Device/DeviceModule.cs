using MaxChemical.Infrastructure.Constants;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.Designer.ViewModels;
using MaxChemical.Modules.Designer.Views;
using MaxChemical.Modules.Device.ViewModels;
using MaxChemical.Modules.Device.Views;
using Prism.Ioc;
using Prism.Modularity;
using Prism.Regions;

namespace MaxChemical.Modules.Device;

public class DeviceModule : IModule
{
    public void OnInitialized(IContainerProvider containerProvider)
    {
        var regionManager = containerProvider.Resolve<IRegionManager>();
        regionManager.RegisterViewWithRegion(RegionNames.DeviceLibraryRegion, typeof(DeviceLibraryView));
        regionManager.RegisterViewWithRegion(RegionNames.CommadLibraryRegion, typeof(CommadLibraryView));
    }

    public void RegisterTypes(IContainerRegistry containerRegistry)
    {
        // 注册设备模块的服务和视图

        // 注册设备管理器
        //containerRegistry.RegisterSingleton<IDeviceManager, DeviceManager>();

        // 注册视图和ViewModel
        containerRegistry.Register<DeviceLibraryView>();
        containerRegistry.Register<DeviceLibraryViewModel>();
        containerRegistry.Register<CommadLibraryView>();
        containerRegistry.Register<CommadLibraryViewModel>();
     

    }
}