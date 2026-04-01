// Services/DeviceColorService.cs — 设备颜色映射服务
using System.Collections.Generic;
using System.Windows.Media;

namespace MaxChemical.Modules.Designer.Services
{
    /// <summary>
    /// 统一管理设备类型 → 颜色的映射。
    /// 泳道图条块颜色、图例颜色都从这里取。
    /// 
    /// 使用方法：
    ///   var brush = DeviceColorService.GetDeviceColor("PF400");
    ///   var hex   = DeviceColorService.GetDeviceHex("Cytomat");
    /// 
    /// 如果设备名未注册，会自动从备选色板中分配一个新颜色并缓存。
    /// </summary>
    public static class DeviceColorService
    {
        // ── 预定义设备颜色（可根据实际设备名称修改） ──
        private static readonly Dictionary<string, string> _predefinedColors = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            // 实验室常见设备
            { "Cytomat",         "#7c3aed" },  // 紫色
            { "MultiDrop",       "#84cc16" },  // 黄绿色
            { "MultiDrop Combi", "#84cc16" },  // 黄绿色
            { "PF400",           "#2563eb" },  // 蓝色
            { "Reader",          "#0d9488" },  // 青色
            { "Washer",          "#ea580c" },  // 橙色
            { "Incubator",       "#7c3aed" },  // 紫色
            { "Dispenser",       "#84cc16" },  // 黄绿色
            { "Robotic Arm",     "#2563eb" },  // 蓝色
            { "Shaker",          "#d946ef" },  // 粉紫
            { "Centrifuge",      "#f59e0b" },  // 琥珀
            { "Barcode Reader",  "#06b6d4" },  // 天蓝
            { "Sealer",          "#64748b" },  // 灰蓝
            { "Delidder",        "#78716c" },  // 灰棕
        };

        // ── 备选色板（用于未预定义的设备） ──
        private static readonly string[] _fallbackPalette = new[]
        {
            "#8b5cf6", "#10b981", "#f97316", "#06b6d4",
            "#ec4899", "#eab308", "#6366f1", "#14b8a6",
            "#f43f5e", "#a855f7", "#22c55e", "#3b82f6",
        };
        private static int _nextFallbackIndex = 0;

        // ── 运行时缓存（包含预定义 + 动态分配） ──
        private static readonly Dictionary<string, string> _colorCache = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, SolidColorBrush> _brushCache = new Dictionary<string, SolidColorBrush>(System.StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 获取设备对应的 Hex 颜色值
        /// </summary>
        public static string GetDeviceHex(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName))
                return "#888888";

            // 1. 先查缓存
            if (_colorCache.TryGetValue(deviceName, out var cached))
                return cached;

            // 2. 查预定义
            if (_predefinedColors.TryGetValue(deviceName, out var predefined))
            {
                _colorCache[deviceName] = predefined;
                return predefined;
            }

            // 3. 模糊匹配：设备名包含预定义关键字
            foreach (var kv in _predefinedColors)
            {
                if (deviceName.Contains(kv.Key) || kv.Key.Contains(deviceName))
                {
                    _colorCache[deviceName] = kv.Value;
                    return kv.Value;
                }
            }

            // 4. 从备选色板分配
            var fallback = _fallbackPalette[_nextFallbackIndex % _fallbackPalette.Length];
            _nextFallbackIndex++;
            _colorCache[deviceName] = fallback;
            return fallback;
        }

        /// <summary>
        /// 获取设备对应的 SolidColorBrush（已冻结，线程安全）
        /// </summary>
        public static SolidColorBrush GetDeviceColor(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName))
                return Brushes.Gray;

            if (_brushCache.TryGetValue(deviceName, out var cached))
                return cached;

            var hex = GetDeviceHex(deviceName);
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            _brushCache[deviceName] = brush;
            return brush;
        }

        /// <summary>
        /// 获取所有已注册的设备颜色（用于构建图例）
        /// </summary>
        public static IReadOnlyDictionary<string, string> GetAllRegisteredColors()
        {
            return _colorCache;
        }

        /// <summary>
        /// 手动注册设备颜色（可在启动时调用）
        /// </summary>
        public static void RegisterDeviceColor(string deviceName, string hexColor)
        {
            if (string.IsNullOrEmpty(deviceName) || string.IsNullOrEmpty(hexColor)) return;
            _colorCache[deviceName] = hexColor;
            _brushCache.Remove(deviceName); // 清除旧的 Brush 缓存
        }
    }
}