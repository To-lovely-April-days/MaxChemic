using System;

namespace DevicePlugins.Devices
{
    /// <summary>
    /// 驱动声明的「可监控参数」:白名单式的一等公民。
    ///
    /// 一条声明同时定死四件事:
    ///   Name        —— 稳定键名(英文,入库 parameter_name、图表数据键都用它);
    ///   DisplayName —— 界面显示名(监控参数列表/图例上用户看到的中文名);
    ///   Unit        —— 单位(监控图表按它归轴:℃/压力/流量);
    ///   SourceCommand/SourceParam —— 取数来源(复用驱动既有读命令及其输出参数,通信代码零重写)。
    ///
    /// 约定:声明了的参数才会出现在监控界面,且设备连接后由基类自动采集;
    /// 没声明的参数(Success、启停状态、设定回显、上下限等)不进入监控体系。
    /// </summary>
    public sealed class CollectableParameter
    {
        public CollectableParameter(string name, string displayName, string unit,
            string source, string? sourceParam = null, bool isKey = false)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            DisplayName = string.IsNullOrEmpty(displayName) ? name : displayName;
            Unit = unit ?? string.Empty;
            SourceCommand = source ?? throw new ArgumentNullException(nameof(source));
            SourceParam = string.IsNullOrEmpty(sourceParam) ? name : sourceParam!;
            IsKey = isKey;
        }

        /// <summary>稳定键名(英文)。</summary>
        public string Name { get; }

        /// <summary>界面显示名(中文)。</summary>
        public string DisplayName { get; }

        /// <summary>单位(℃ / MPa / mL/min ...)。</summary>
        public string Unit { get; }

        /// <summary>取数用的驱动读命令名(必须是本设备 Commands 中已存在的无输入读命令)。</summary>
        public string SourceCommand { get; }

        /// <summary>该命令输出里的参数名(默认与 Name 相同)。</summary>
        public string SourceParam { get; }

        /// <summary>预留标记:本设备的默认关键参数(如泵=流量、温控=实际温度),
        /// 供后续大屏/徽标类展示做兜底选择;当前尚无消费方。</summary>
        public bool IsKey { get; }
    }
}
