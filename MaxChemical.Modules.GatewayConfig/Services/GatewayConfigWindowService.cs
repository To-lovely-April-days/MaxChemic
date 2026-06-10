using System.Windows;
using MaxChemical.Modules.GatewayConfig.ViewModels;
using MaxChemical.Modules.GatewayConfig.Views;
using Prism.Ioc;

namespace MaxChemical.Modules.GatewayConfig.Services
{
    /// <summary>
    /// Shell 通过此接口打开网关配置主窗口。
    /// 与 IRegionSettingsDialogProvider / IProjectNameDialogProvider 模式一致。
    /// </summary>
    public interface IGatewayConfigWindowService
    {
        void ShowGatewayConfig(Window owner = null);
    }

    public class GatewayConfigWindowService : IGatewayConfigWindowService
    {
        private GatewayConfigMainView _currentWindow;

        public void ShowGatewayConfig(Window owner = null)
        {
            if (_currentWindow != null && _currentWindow.IsLoaded)
            {
                if (_currentWindow.WindowState == WindowState.Minimized)
                    _currentWindow.WindowState = WindowState.Normal;
                _currentWindow.Activate();
                return;
            }

            _currentWindow = new GatewayConfigMainView();
            _currentWindow.DataContext = ContainerLocator.Container.Resolve<GatewayConfigMainViewModel>();
            if (owner != null) _currentWindow.Owner = owner;
            _currentWindow.Closed += (s, e) => _currentWindow = null;
            _currentWindow.Show();
        }
    }
}
