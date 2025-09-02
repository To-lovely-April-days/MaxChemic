using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Core
{
    /// <summary>
    /// 设备命令执行异常
    /// </summary>
    public class DeviceCommandExecutionException : Exception
    {
        public string DeviceName { get; }
        public string CommandName { get; }

        public DeviceCommandExecutionException(string message) : base(message) { }

        public DeviceCommandExecutionException(string message, Exception innerException)
            : base(message, innerException) { }

        public DeviceCommandExecutionException(string deviceName, string commandName, string message, Exception innerException = null)
            : base($"设备 {deviceName} 命令 {commandName} 执行失败: {message}", innerException)
        {
            DeviceName = deviceName;
            CommandName = commandName;
        }
    }
}
