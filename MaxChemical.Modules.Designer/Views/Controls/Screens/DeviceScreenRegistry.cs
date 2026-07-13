// ============================================================================
//  DeviceScreenRegistry.cs
//  放置位置：MaxChemical.Modules.Designer/Views/Controls/Screens/DeviceScreenRegistry.cs
//  命名空间：MaxChemical.Modules.Designer.Views.Controls.Screens
//
//  作用：
//    屏幕注册表。把设备声明的 ScreenKey 映射到“怎么创建这块屏幕”。
//    这是整个屏幕体系里唯一“知道具体屏幕类”的地方。
//    新增一个设备屏幕 = 在 Register() 里加一行。
//
//  依赖方向：本类在 Designer，自然可以引用 DevicePlugins + 各屏幕控件，
//            不产生循环引用。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using DevicePlugins.Devices;

namespace MaxChemical.Modules.Designer.Views.Controls.Screens
{
    /// <summary>一块屏幕的注册信息：怎么造、设计分辨率、标题来源。</summary>
    public sealed class ScreenRegistration
    {
        /// <summary>屏幕控件工厂。每次打开对话框创建新实例（避免复用导致的轮询/状态残留）。</summary>
        public Func<DeviceScreenBase> Factory { get; set; }

        /// <summary>设计分辨率（逻辑像素），宿主用 Viewbox 按此等比缩放。</summary>
        public Size Resolution { get; set; }
    }

    /// <summary>
    /// 设备屏幕注册表。静态单点，进程启动后填一次即可。
    /// </summary>
    public static class DeviceScreenRegistry
    {
        private static readonly Dictionary<string, ScreenRegistration> _map =
            new Dictionary<string, ScreenRegistration>(StringComparer.OrdinalIgnoreCase);

        private static bool _initialized;
        private static readonly object _lock = new object();

        /// <summary>
        /// 登记所有已知设备屏幕。新增设备屏幕就在这里加一行。
        /// 幂等：重复调用只生效一次。
        /// </summary>
        public static void EnsureRegistered()
        {
            if (_initialized) return;
            lock (_lock)
            {
                if (_initialized) return;

                // ── 两液一气进料系统 ─────────────────────────────
                Register("TwoLiquidOneGasFeed", new ScreenRegistration
                {
                    Factory = () => new TwoLiquidOneGasFeedScreen(),
                    Resolution = new Size(1240, 620),
                });
                Register("DynamicTubularReactor", new ScreenRegistration
                {
                    Factory = () => new DynamicTubularReactorScreen(),
                    Resolution = new Size(1240, 620),
                });
                Register("SiliconCarbideChip", new ScreenRegistration
                {
                    Factory = () => new SiliconCarbideChipScreen(),
                    Resolution = new Size(1240, 620),
                });
                Register("SiphonInfusionPump", new ScreenRegistration
                {
                    Factory = () => new SiphonInfusionPumpScreen(),
                    Resolution = new Size(1240, 620),
                });
                Register("HighPreactorScreen", new ScreenRegistration
                {
                    Factory = () => new HighPreactorScreen(),
                    Resolution = new Size(1240, 620),
                });
                Register("HighLowTemperatureScreen", new ScreenRegistration
                {
                    Factory = () => new HighLowTemperature(),
                    Resolution = new Size(1240, 620),
                });
                Register("HighTempFurnaceScreen", new ScreenRegistration
                {
                    Factory = () => new HighTempFurnaceScreen(),
                    Resolution = new Size(1240, 620),
                });

                // ── 后续设备在此继续 Register("XxxKey", ...) ──────

                _initialized = true;
                Debug.WriteLine($"[ScreenRegistry] 已注册 {_map.Count} 块设备屏幕");
            }
        }

        /// <summary>手动登记一项（也可被插件在自己初始化时调用）。</summary>
        public static void Register(string screenKey, ScreenRegistration reg)
        {
            if (string.IsNullOrWhiteSpace(screenKey) || reg?.Factory == null) return;
            _map[screenKey] = reg;
        }

        /// <summary>设备是否有可用的屏幕。</summary>
        public static bool HasScreen(IDevice device)
        {
            EnsureRegistered();
            return device is IDeviceScreen s
                && !string.IsNullOrEmpty(s.ScreenKey)
                && _map.ContainsKey(s.ScreenKey);
        }

        /// <summary>
        /// 为设备创建屏幕控件并注入设备实例。无对应屏幕时返回 null。
        /// </summary>
        public static DeviceScreenBase CreateScreen(IDevice device, out Size resolution)
        {
            resolution = new Size(0, 0);
            EnsureRegistered();

            if (device is not IDeviceScreen sp || string.IsNullOrEmpty(sp.ScreenKey))
                return null;

            if (!_map.TryGetValue(sp.ScreenKey, out var reg))
            {
                Debug.WriteLine($"[ScreenRegistry] 未注册的 ScreenKey: {sp.ScreenKey}");
                return null;
            }

            try
            {
                var screen = reg.Factory();
                screen.AttachDevice(device);
                resolution = reg.Resolution;
                return screen;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ScreenRegistry] 创建屏幕失败 ({sp.ScreenKey}): {ex.Message}");
                return null;
            }
        }
    }
}