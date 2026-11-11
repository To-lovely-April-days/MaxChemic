using System;
using System.Windows;

namespace MaxChemical.Modules.Designer.Views
{
    internal static class FlowInteractionCoordinator
    {
        private static Window? _currentWindow;
        private static Func<bool>? _advance;

        internal static void Register(Window window, Func<bool> advance)
        {
            if (!window.Dispatcher.CheckAccess())
            {
                window.Dispatcher.Invoke(() => Register(window, advance));
                return;
            }

            _currentWindow = window;
            _advance = advance;
        }

        internal static void Unregister(Window window)
        {
            if (!window.Dispatcher.CheckAccess())
            {
                window.Dispatcher.Invoke(() => Unregister(window));
                return;
            }

            if (!ReferenceEquals(_currentWindow, window)) return;

            _currentWindow = null;
            _advance = null;
        }

        internal static bool TryAdvance()
        {
            var window = _currentWindow;
            if (window == null) return false;

            if (!window.Dispatcher.CheckAccess())
                return window.Dispatcher.Invoke(TryAdvance);

            if (!ReferenceEquals(_currentWindow, window) || !window.IsVisible)
                return false;

            return _advance?.Invoke() == true;
        }
    }
}
