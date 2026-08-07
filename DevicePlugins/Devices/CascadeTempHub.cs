using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace DevicePlugins.Devices
{
    /// <summary>
    /// 串级控温注册中枢（进程内静态注册表，仿 DeviceSampleHub 的分层约定）。
    ///
    /// 背景：部分反应器（如碳化硅微反应器）只带测温点（T1~T6），本体不控温，
    /// 实际温度由外部温控设备（高低温循环器、高温炉等）决定。要实现
    /// 「以反应器实测温度为准的闭环控温」，反应器驱动必须能把修正后的
    /// 设定值写给温控设备——但驱动之间不允许直接互相引用。
    ///
    /// 约定：具备「设置温度」能力的温控设备驱动在构造时把自己的设温/读设定
    /// 能力注册到本中枢；需要串级控温的反应器驱动按显示名查询并调用。
    /// 同名 Key 重复注册视为替换（多实例场景由注册方通过委托内部解析 ComId）。
    /// </summary>
    public static class CascadeTempHub
    {
        private static readonly ConcurrentDictionary<string, CascadeTempController> _controllers
            = new ConcurrentDictionary<string, CascadeTempController>();

        /// <summary>注册表变化（供下拉选项刷新）。构造期同线程触发，订阅方自行做好异常兜底。</summary>
        public static event EventHandler? ControllersChanged;

        /// <summary>注册（或替换）一个温控设备。</summary>
        public static void Register(CascadeTempController controller)
        {
            if (controller == null || string.IsNullOrEmpty(controller.Key)) return;
            _controllers[controller.Key] = controller;
            SafeRaiseChanged();
        }

        /// <summary>注销（仅当当前注册项就是该实例时才移除，防止误删后注册的替换者）。</summary>
        public static void Unregister(string key, CascadeTempController? instance = null)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (instance != null)
            {
                if (_controllers.TryGetValue(key, out var cur) && !ReferenceEquals(cur, instance)) return;
            }
            if (_controllers.TryRemove(key, out _)) SafeRaiseChanged();
        }

        /// <summary>当前已注册温控设备的显示名快照（下拉选项用）。</summary>
        public static IReadOnlyList<string> ControllerDisplayNames
            => _controllers.Values.Select(c => c.DisplayName).Distinct().ToList();

        /// <summary>按显示名（或 Key）查找温控设备；找不到返回 null。</summary>
        public static CascadeTempController? Find(string displayNameOrKey)
        {
            if (string.IsNullOrEmpty(displayNameOrKey)) return null;
            if (_controllers.TryGetValue(displayNameOrKey, out var byKey)) return byKey;
            return _controllers.Values.FirstOrDefault(c => c.DisplayName == displayNameOrKey);
        }

        private static void SafeRaiseChanged()
        {
            try { ControllersChanged?.Invoke(null, EventArgs.Empty); }
            catch
            {
                // 订阅方异常不能影响注册方
            }
        }
    }

    /// <summary>一个已注册的温控设备（设温能力的委托封装）。</summary>
    public sealed class CascadeTempController
    {
        public CascadeTempController(string key, string displayName,
            Func<double, Task<bool>> setTemperatureAsync,
            Func<Task<double?>>? getSetpointAsync = null)
        {
            Key = key ?? throw new ArgumentNullException(nameof(key));
            DisplayName = string.IsNullOrEmpty(displayName) ? key : displayName;
            SetTemperatureAsync = setTemperatureAsync ?? throw new ArgumentNullException(nameof(setTemperatureAsync));
            GetSetpointAsync = getSetpointAsync;
        }

        /// <summary>注册键（驱动类型级，稳定）。</summary>
        public string Key { get; }

        /// <summary>界面显示名（如「高低温循环器」）。</summary>
        public string DisplayName { get; }

        /// <summary>把目标设定值(℃)写到温控设备。返回是否成功。ComId 等由注册方委托内部自行解析。</summary>
        public Func<double, Task<bool>> SetTemperatureAsync { get; }

        /// <summary>读取温控设备当前设定值(℃)，用于串级起步基准；不支持可为 null。</summary>
        public Func<Task<double?>>? GetSetpointAsync { get; }
    }
}
