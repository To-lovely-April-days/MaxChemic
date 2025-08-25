using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MaxChemical.Core;

namespace DevicePlugins.Devices
{
    public interface IControllableDevice : IDevice
    {
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
    }

}
