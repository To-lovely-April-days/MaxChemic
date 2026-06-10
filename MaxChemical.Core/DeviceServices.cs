namespace MaxChemical.Core
{
    /// <summary>
    /// 设备插件可用的基础设施服务入口。
    /// 由宿主应用 (Shell) 在启动时填充,设备插件通过 Device 基类访问,不感知具体 DI 框架。
    /// </summary>
    public static class DeviceServices
    {
        /// <summary>
        /// 设备字节流通信工厂。Shell 启动时由 DI 容器解析并赋值。
        /// </summary>
        public static IDeviceTransportFactory TransportFactory { get; set; }
    }
}