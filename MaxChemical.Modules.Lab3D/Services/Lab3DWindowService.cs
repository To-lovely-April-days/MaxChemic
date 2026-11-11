using System.Windows;
using MaxChemical.Modules.Lab3D.Views;
using Prism.Ioc;

namespace MaxChemical.Modules.Lab3D.Services;

public sealed class Lab3DWindowService : ILab3DWindowService
{
    private Lab3DWindow? _currentWindow;

    public bool IsOpen => InvokeOnUiThread(() => _currentWindow is { IsLoaded: true });

    public void ShowLab3D(Window? owner = null)
    {
        InvokeOnUiThread(() =>
        {
            if (_currentWindow is { IsLoaded: true })
            {
                if (_currentWindow.WindowState == WindowState.Minimized)
                    _currentWindow.WindowState = WindowState.Normal;
                _currentWindow.Activate();
                return;
            }

            var window = ContainerLocator.Container.Resolve<Lab3DWindow>();
            if (owner is not null)
            {
                window.Owner = owner;
                window.Width = Math.Min(window.Width, owner.ActualWidth);
                window.Height = Math.Min(window.Height, owner.ActualHeight);
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            _currentWindow = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_currentWindow, window)) _currentWindow = null;
            };
            window.Show();
        });
    }

    public void ShowLab3DFullScreen(Window? owner = null)
    {
        InvokeOnUiThread(() =>
        {
            // 先复用原有打开/激活逻辑,再进入展示模式;未打开时也能一条语音完成。
            ShowLab3D(owner);
            _currentWindow?.EnterFullScreen();
        });
    }

    public bool ExitLab3DFullScreen()
    {
        return InvokeOnUiThread(() =>
        {
            if (_currentWindow is not { IsLoaded: true }) return false;
            _currentWindow.ExitFullScreen();
            return true;
        });
    }

    public bool CloseLab3D()
    {
        return InvokeOnUiThread(() =>
        {
            if (_currentWindow is not { IsLoaded: true })
            {
                _currentWindow = null;
                return false;
            }

            _currentWindow.Close();
            return true;
        });
    }

    private static void InvokeOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }
        dispatcher.Invoke(action);
    }

    private static T InvokeOnUiThread<T>(Func<T> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) return action();
        return dispatcher.Invoke(action);
    }
}
