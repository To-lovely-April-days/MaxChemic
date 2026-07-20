using System;
using System.Windows;
using MaxChemical.Logging;
using MaxChemical.Shell.ViewModels;
using MaxChemical.Shell.Views;
using Prism.Ioc;

namespace MaxChemical.Shell.Services.Agent
{
    /// <summary>
    /// 小桐对话窗口管理:全局单例窗口,关闭即隐藏;写操作待确认时自动弹出。
    /// </summary>
    public sealed class AgentChatWindowService
    {
        private readonly IContainerProvider _container;
        private readonly ILogService _logger = LogManager.GetLogger<AgentChatWindowService>();
        private AgentChatWindow _window;

        public AgentChatWindowService(IContainerProvider container)
        {
            _container = container;
        }

        public void ShowWindow()
        {
            var app = Application.Current;
            if (app == null) return;

            void Do()
            {
                try
                {
                    if (_window == null)
                    {
                        // ShowWindowRequested → ShowWindow 的订阅在 AgentBootstrapper 中完成(单次),这里只建窗口
                        var vm = _container.Resolve<AgentChatViewModel>();
                        _window = new AgentChatWindow(vm);
                        if (app.MainWindow != null && app.MainWindow.IsLoaded)
                            _window.Owner = app.MainWindow;
                    }
                    if (!_window.IsVisible) _window.Show();
                    if (_window.WindowState == WindowState.Minimized)
                        _window.WindowState = WindowState.Normal;
                    _window.Activate();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"打开小桐窗口失败: {ex.Message}");
                }
            }

            if (app.Dispatcher.CheckAccess()) Do();
            else app.Dispatcher.BeginInvoke(Do);
        }
    }
}
