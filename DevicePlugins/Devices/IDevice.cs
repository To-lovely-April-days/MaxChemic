using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using MaxChemical.Core;

namespace DevicePlugins.Devices
{
    /// <summary>
    /// 设备基础接口
    /// </summary>
    public interface IDevice : INotifyPropertyChanged
    {   // 画布设备ID（与ComId区分）
        string DeviceId { get; set; }  // 画布上的唯一ID

        /// <summary>
        /// 设备名称
        /// </summary>
        string Name { get; }

        /// <summary>
        /// 允许的区域类型列表
        /// </summary>
        List<RegionType> AllowedRegions { get; }

        /// <summary>
        /// 制造商
        /// </summary>
        string Manufacturer { get; }

        /// <summary>
        /// 图标位置
        /// </summary>
        string ImageLocation { get; }

        /// <summary>
        /// 设备类别
        /// </summary>
        string Category { get; }

        /// <summary>
        /// 设备参数
        /// </summary>
        DeviceParameters Parameters { get; }

        /// <summary>
        /// 设备命令
        /// </summary>
        List<DeviceCommand> Commands { get; }

        /// <summary>
        /// 初始化设备
        /// </summary>
        void Initialize();

        /// <summary>
        /// 关闭设备
        /// </summary>
        void Shutdown();

        /// <summary>
        /// 连接状态
        /// </summary>
        DeviceConnectionStatus ConnectionStatus { get; }

        /// <summary>
        /// 是否已连接
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// 连接时间
        /// </summary>
        DateTime? ConnectedTime { get; }

        /// <summary>
        /// 最后心跳时间
        /// </summary>
        DateTime? LastHeartbeatTime { get; }

        /// <summary>
        /// 连接超时时间(秒)
        /// </summary>
        int ConnectionTimeout { get; set; }

        /// <summary>
        /// 连接状态变更事件
        /// </summary>
        event EventHandler<DeviceConnectionStatusChangedEventArgs> ConnectionStatusChanged;

        /// <summary>
        /// 连接设备
        /// </summary>
        /// <returns>连接结果</returns>
        Task<bool> ConnectAsync();

        /// <summary>
        /// 断开设备连接
        /// </summary>
        /// <returns>断开结果</returns>
        Task<bool> DisconnectAsync();

        /// <summary>
        /// 检查连接状态
        /// </summary>
        /// <returns>连接状态</returns>
        Task<bool> CheckConnectionAsync();

        /// <summary>
        /// 是否为模拟模式
        /// </summary>
        bool IsSimulationMode { get; set; }

        /// <summary>
        /// 当前活跃的状态列表（哪些功能正在运行）
        /// </summary>
        List<DeviceActiveState> ActiveStates { get; }

        /// <summary>
        /// 暂停所有当前活跃的状态
        /// </summary>
        Task<bool> PauseAllActiveStatesAsync();

        /// <summary>
        /// 恢复所有被暂停的状态
        /// </summary>
        Task<bool> ResumeAllPausedStatesAsync();

        /// <summary>
        /// 停止所有当前活跃的状态
        /// </summary>
        Task<bool> StopAllActiveStatesAsync();

        /// <summary>
        /// 设备状态变更事件
        /// </summary>
        event EventHandler<DeviceStateChangedEventArgs> StateChanged;

      

        /// <summary>
        /// 获取当前设备状态快照（包含 IsStable 等状态字段）
        /// </summary>
        DeviceState GetCurrentState();

        /// <summary>
        /// 设备控制状态变更事件（用于流量稳定/不稳定等状态广播）
        /// </summary>
        event EventHandler<DeviceControlStateChangedEventArgs> ControlStateChanged;

        /// <summary>
        /// 是否支持通过 ZLAN 网关通信。
        /// true:此设备可在网关配置界面被选择绑定到通道
        /// false (默认):此设备只能走物理串口/TCP直连,不出现在网关绑定下拉框
        /// </summary>
        bool SupportsZLanGateway { get; }
    }
}