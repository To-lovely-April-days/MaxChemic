using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Core
{
    /// <summary>
    /// 设备活跃状态信息
    /// </summary>
    public class DeviceActiveState
    {
        public string StateName { get; set; }           // 状态名称，如"ValveOpen", "Mixing"
        public string ActivateCommand { get; set; }     // 激活该状态的命令名称
        public string DeactivateCommand { get; set; }   // 停止该状态的命令名称
        public string PauseCommand { get; set; }        // 暂停该状态的命令名称（可选）
        public string ResumeCommand { get; set; }       // 恢复该状态的命令名称（可选）
        public DateTime ActivatedTime { get; set; }     // 激活时间
        public bool IsPaused { get; set; }              // 是否被暂停
        public object StateData { get; set; }           // 状态相关数据
    }

    public class DeviceStateChangedEventArgs : EventArgs
    {
        public string StateName { get; set; }
        public bool IsActive { get; set; }
        public DateTime Timestamp { get; set; }
    }
}
