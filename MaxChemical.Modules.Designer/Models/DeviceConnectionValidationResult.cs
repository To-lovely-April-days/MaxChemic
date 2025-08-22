using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Modules.Designer.Models
{
    /// <summary>
    /// 设备连接验证结果
    /// </summary>
    public class DeviceConnectionValidationResult
    {
        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public bool IsConnected { get; set; }
        public string ErrorMessage { get; set; }
    }
    /// <summary>
    /// 流程所需设备信息
    /// </summary>
    public class RequiredDeviceInfo
    {
        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public List<string> Commands { get; set; } = new List<string>();
    }

}
