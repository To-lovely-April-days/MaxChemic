using System;

namespace DevicePlugins.Devices
{
    /// <summary>
    /// 驱动级自动采集的样本发布中枢（进程内静态事件）。
    ///
    /// 分层约束：DevicePlugins 不依赖 Designer / Shell，驱动采到的数据只能
    /// 通过本枢纽向上抛；上层（Designer 的 AutoCollectService）订阅后负责
    /// 转发给监控图表、监控卡片与实验数据入库。
    ///
    /// 发布方：Device 基类的自动采集循环（设备连接后自动启动）。
    /// 线程说明：事件在采集线程上触发，订阅方自行负责线程切换与耗时控制。
    /// </summary>
    public static class DeviceSampleHub
    {
        /// <summary>一帧自动采集样本（一条命令的一次输出）。</summary>
        public static event EventHandler<DeviceSampleEventArgs>? SampleCollected;

        public static void Publish(DeviceSampleEventArgs args)
        {
            if (args == null) return;
            try
            {
                SampleCollected?.Invoke(null, args);
            }
            catch
            {
                // 订阅方异常不能打断驱动采集循环
            }
        }
    }

    /// <summary>自动采集样本事件参数。</summary>
    public class DeviceSampleEventArgs : EventArgs
    {
        /// <summary>画布设备实例 ID（与监控图表 / 监控卡片使用的 DeviceId 同源）。</summary>
        public string DeviceId { get; set; } = string.Empty;

        /// <summary>设备名称（驱动 Name，如「平流泵」）。</summary>
        public string DeviceName { get; set; } = string.Empty;

        /// <summary>样本命令键。自动采集样本恒为 Device.AutoCollectCommandKey（"自动采集"），
        /// 一帧聚合该设备全部声明参数，不再对应具体读命令。</summary>
        public string CommandName { get; set; } = string.Empty;

        /// <summary>命令输出参数（含数值与单位）。</summary>
        public DeviceParameters? Output { get; set; }

        /// <summary>采集时刻。</summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
}
