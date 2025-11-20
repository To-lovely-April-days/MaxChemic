using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Core
{
    /// <summary>
    /// 柱塞泵运行状态
    /// </summary>
    public enum PumpState
    {
        /// <summary>
        /// 停止
        /// </summary>
        Stopped = 0,

        /// <summary>
        /// 正在启动
        /// </summary>
        Starting = 1,

        /// <summary>
        /// 运行中
        /// </summary>
        Running = 2,

        /// <summary>
        /// 正在停止
        /// </summary>
        Stopping = 3,

        /// <summary>
        /// 设置速度中
        /// </summary>
        SettingSpeed = 4,

        /// <summary>
        /// 操作失败
        /// </summary>
        Failed = 5,

        /// <summary>
        /// 通讯错误
        /// </summary>
        Error = 6
    }
}
