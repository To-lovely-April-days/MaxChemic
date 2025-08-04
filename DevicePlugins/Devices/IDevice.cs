using System.Collections.Generic;
using System.ComponentModel;
using MaxChemical.Core;

namespace DevicePlugins.Devices
{
    /// <summary>
    /// 设备基础接口
    /// </summary>
    public interface IDevice : INotifyPropertyChanged
    {
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
    }
}