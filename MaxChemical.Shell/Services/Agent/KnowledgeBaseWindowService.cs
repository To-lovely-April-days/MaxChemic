using System;
using System.Windows;
using MaxChemical.Logging;
using MaxChemical.Shell.ViewModels;
using MaxChemical.Shell.Views;
using Prism.Ioc;

namespace MaxChemical.Shell.Services.Agent
{
    /// <summary>
    /// 知识库管理窗口管理:全局单例窗口,关闭即隐藏。
    /// 入口在主界面标题栏(小桐按钮旁的「知识库」按钮)。
    /// </summary>
    public sealed class KnowledgeBaseWindowService
    {
        private readonly IContainerProvider _container;
        private readonly ILogService _logger = LogManager.GetLogger<KnowledgeBaseWindowService>();
        private KnowledgeBaseWindow _window;

        public KnowledgeBaseWindowService(IContainerProvider container)
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
                        var vm = _container.Resolve<KnowledgeBaseViewModel>();
                        _window = new KnowledgeBaseWindow(vm);
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
                    _logger.LogError($"打开知识库窗口失败: {ex.Message}");
                }
            }

            if (app.Dispatcher.CheckAccess()) Do();
            else app.Dispatcher.BeginInvoke(Do);
        }
    }
}
