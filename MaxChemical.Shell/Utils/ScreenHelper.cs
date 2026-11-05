using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MaxChemical.Shell.Utils
{
    /// <summary>
    /// 多显示器辅助：获取窗口当前所在屏幕的工作区域。
    /// SystemParameters.WorkArea 永远返回主屏工作区，多屏场景需要用此类替代。
    /// </summary>
    public static class ScreenHelper
    {
        private const int MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
        }

        /// <summary>
        /// 返回窗口当前所在显示器的工作区域（不含任务栏），单位为 WPF 设备无关单位(DIP)。
        /// 窗口句柄尚未创建或调用失败时退回到主屏工作区域。
        /// </summary>
        public static Rect GetCurrentScreenWorkArea(Window window)
        {
            return GetMonitorRect(window, workArea: true, SystemParameters.WorkArea);
        }

        /// <summary>
        /// 返回窗口当前所在显示器的完整边界（含任务栏区域），单位为 WPF 设备无关单位(DIP)。
        /// 用于真正的全屏播放。取不到时退回到主屏边界。
        /// </summary>
        public static Rect GetCurrentScreenBounds(Window window)
        {
            var primary = new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
            return GetMonitorRect(window, workArea: false, primary);
        }

        private static Rect GetMonitorRect(Window window, bool workArea, Rect fallback)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                    var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(monitor, ref info))
                    {
                        var rc = workArea ? info.rcWork : info.rcMonitor;
                        // rcWork/rcMonitor 是物理像素坐标，需要按窗口当前 DPI 换算成 WPF DIP
                        var fromDevice = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice
                                         ?? Matrix.Identity;
                        return Rect.Transform(new Rect(
                            rc.Left,
                            rc.Top,
                            rc.Right - rc.Left,
                            rc.Bottom - rc.Top), fromDevice);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"获取屏幕区域失败: {ex.Message}");
            }

            return fallback;
        }
    }
}
