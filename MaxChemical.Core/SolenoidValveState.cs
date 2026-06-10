using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Core
{
    /// <summary>
    /// 电磁阀状态枚举
    /// </summary>
    public enum SolenoidValveState
    {
        /// <summary>未知状态</summary>
        Unknown = 0,

        /// <summary>正在打开</summary>
        Opening,

        /// <summary>已打开</summary>
        Open,

        /// <summary>正在关闭</summary>
        Closing,

        /// <summary>已关闭</summary>
        Closed,

        /// <summary>操作失败</summary>
        Failed,

        /// <summary>通讯错误</summary>
        Error
    }
}
