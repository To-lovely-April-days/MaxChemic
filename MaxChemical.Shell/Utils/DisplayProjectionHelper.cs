using MaxChemical.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MaxChemical.Shell.Utils
{
    /// <summary>
    /// 一块显示器。坐标全部是 Win32 屏幕坐标(虚拟桌面坐标系,物理像素),不做 WPF DIP 换算:
    /// 两块屏缩放比例不同时,用 DIP 在屏之间搬窗口很容易算错,直接用像素最稳。
    /// </summary>
    public sealed class MonitorDescriptor
    {
        public string DeviceName { get; init; } = string.Empty;   // 例如 \\.\DISPLAY2
        public Int32Rect Bounds { get; init; }                    // 整块屏
        public Int32Rect WorkArea { get; init; }                  // 去掉任务栏
        public bool IsPrimary { get; init; }

        public long Area => (long)Bounds.Width * Bounds.Height;

        public bool IsSameDevice(string? deviceName) =>
            string.Equals(DeviceName, deviceName, StringComparison.OrdinalIgnoreCase);

        public override string ToString() =>
            $"{DeviceName} {Bounds.Width}x{Bounds.Height} @({Bounds.X},{Bounds.Y}){(IsPrimary ? " 主屏" : "")}";
    }

    /// <summary>
    /// 投放到大屏用到的 Win32 能力:枚举显示器、判断是否处于"复制"模式、切换到"扩展"模式、
    /// 打开系统投放面板(Win+K)、把窗口搬到指定显示器并铺满。
    /// </summary>
    public static class DisplayProjectionHelper
    {
        #region Win32

        private const int MONITORINFOF_PRIMARY = 0x1;
        private const int MONITOR_DEFAULTTONEAREST = 2;

        private const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
        private const uint SDC_TOPOLOGY_EXTEND = 0x4;
        private const uint SDC_APPLY = 0x80;

        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        private const byte VK_LWIN = 0x5B;
        private const byte VK_K = 0x4B;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public int dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int SetDisplayConfig(uint numPathArrayElements, IntPtr pathArray,
                                                   uint numModeInfoArrayElements, IntPtr modeInfoArray, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        #endregion

        /// <summary>当前桌面上的所有显示器("复制"模式下两块屏只算一块)。</summary>
        public static IReadOnlyList<MonitorDescriptor> GetMonitors()
        {
            var list = new List<MonitorDescriptor>();
            MonitorEnumProc callback = (hMonitor, _, _, _) =>
            {
                var d = Describe(hMonitor);
                if (d != null) list.Add(d);
                return true;
            };

            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
            }
            finally
            {
                GC.KeepAlive(callback);
            }
            return list;
        }

        /// <summary>窗口当前所在的显示器;窗口句柄还没创建时返回 null。</summary>
        public static MonitorDescriptor? GetMonitorOf(Window window)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            return hwnd == IntPtr.Zero ? null : Describe(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));
        }

        /// <summary>
        /// 是否有屏处于"复制"模式:活动的显示通路(每块物理屏/无线屏一条)比桌面上的显示器多,
        /// 说明有两条通路共用了同一块桌面。
        /// </summary>
        public static bool IsAnyDisplayCloned(int monitorCount)
        {
            try
            {
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint paths, out _) != 0)
                    return false;
                return paths > monitorCount;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 切换为"扩展"模式(等同于 Win+P 里选"扩展")。Windows 会按显示器组合记住这个选择,
        /// 下次连同一块大屏通常直接就是扩展。
        /// </summary>
        public static bool SwitchToExtendTopology(ILogService? logger)
        {
            try
            {
                int rc = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_EXTEND);
                if (rc == 0) return true;
                logger?.LogWarning($"SetDisplayConfig 切换扩展模式失败,返回码 {rc},改用 DisplaySwitch.exe");
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"SetDisplayConfig 调用异常: {ex.Message},改用 DisplaySwitch.exe");
            }

            try
            {
                var exe = Path.Combine(Environment.SystemDirectory, "DisplaySwitch.exe");
                if (File.Exists(exe))
                {
                    Process.Start(new ProcessStartInfo(exe, "/extend") { UseShellExecute = false, CreateNoWindow = true });
                    return true;
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "DisplaySwitch.exe 切换扩展模式失败");
            }
            return false;
        }

        /// <summary>打开系统的无线投放面板(模拟 Win+K),由用户在里面选大屏。</summary>
        public static void OpenCastPanel()
        {
            keybd_event(VK_LWIN, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
            keybd_event(VK_K, 0, 0, UIntPtr.Zero);
            keybd_event(VK_K, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        /// <summary>
        /// 把窗口搬到指定显示器并铺满(fillWholeScreen=true 盖住整块屏,false 只铺工作区)。
        /// 完成后在 UI 线程回调 completed。
        /// </summary>
        public static void PlaceWindow(Window window, MonitorDescriptor target, bool fillWholeScreen, Action? completed = null)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            if (window.WindowState != WindowState.Normal)
                window.WindowState = WindowState.Normal;

            var rect = fillWholeScreen ? target.Bounds : target.WorkArea;
            bool alreadyThere = GetMonitorOf(window)?.IsSameDevice(target.DeviceName) == true;

            if (!alreadyThere)
            {
                // 第一步:先用小尺寸把窗口整个挪进目标屏。两块屏缩放比例不同时,系统会在这一步发
                // WM_DPICHANGED,WPF 会按新 DPI 把窗口尺寸按比例改一次。如果一步到位直接设成
                // 目标屏大小,这次按比例的调整会落在铺满之后,窗口就会铺不满或超出屏幕。
                int w = Math.Min(800, rect.Width);
                int h = Math.Min(600, rect.Height);
                SetWindowPos(hwnd, IntPtr.Zero,
                    rect.X + (rect.Width - w) / 2, rect.Y + (rect.Height - h) / 2, w, h,
                    SWP_NOZORDER | SWP_NOACTIVATE);
            }

            // 第二步:排在 DPI 变化处理之后再铺满
            window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                SetWindowPos(hwnd, IntPtr.Zero, rect.X, rect.Y, rect.Width, rect.Height, SWP_NOZORDER | SWP_SHOWWINDOW);
                window.Activate();
                completed?.Invoke();
            }));
        }

        private static MonitorDescriptor? Describe(IntPtr hMonitor)
        {
            if (hMonitor == IntPtr.Zero) return null;

            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = string.Empty };
            if (!GetMonitorInfo(hMonitor, ref info)) return null;

            return new MonitorDescriptor
            {
                DeviceName = info.szDevice ?? string.Empty,
                Bounds = ToInt32Rect(info.rcMonitor),
                WorkArea = ToInt32Rect(info.rcWork),
                IsPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0
            };
        }

        private static Int32Rect ToInt32Rect(RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }
}
