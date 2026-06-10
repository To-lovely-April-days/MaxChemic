using MaxChemical.Modules.GatewayConfig.Services;
using Prism.Ioc;
using Prism.Modularity;

namespace MaxChemical.Modules.GatewayConfig
{
    /// <summary>
    /// 网关配置模块入口。
    ///
    /// 注意:IGatewayConfigWindowService **不在这里注册**,
    /// 必须在 Shell 的 App.RegisterTypes 里注册,因为 MainMenuBarViewModel
    /// 在 Shell 启动早期被解析,那时模块还没加载。
    /// </summary>
    public class GatewayConfigModule : IModule
    {
        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            containerRegistry.RegisterSingleton<IGatewayConfigService, GatewayConfigService>();
            containerRegistry.Register<ViewModels.GatewayConfigMainViewModel>();
            containerRegistry.RegisterSingleton<IChannelProbeService, ChannelProbeService>();
        }

        public void OnInitialized(IContainerProvider containerProvider)
        {
        }
    }
}
