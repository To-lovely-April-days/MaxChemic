using Prism.Mvvm;

namespace MaxChemical.Modules.GatewayConfig.Models
{
    /// <summary>
    /// 通道配置弹窗"绑定设备"下拉框里的一项。
    /// 来源:IDeviceManager.GetAllDevices() 中 SupportsZLanGateway == true 的设备。
    /// </summary>
    public class BindableDeviceCandidate : BindableBase
    {
        /// <summary>设备类型完整类名,如 "SiphonInfusionPump_ModbusRTU"</summary>
        public string DeviceTypeName { get; set; } = "";

        /// <summary>设备显示名 (来自 IDevice.Name),如 "平流输液泵"</summary>
        public string DisplayName { get; set; } = "";

        /// <summary>设备实例 ID (用户给设备起的名字),如 "SIP0001"</summary>
        public string InstanceId { get; set; } = "";

        /// <summary>图标 URI (来自 IDevice.ImageLocation)</summary>
        public string ImageLocation { get; set; } = "";

        /// <summary>此组合是否已被其他通道绑定 (双向唯一约束下变灰显)</summary>
        private bool _isBoundElsewhere;
        public bool IsBoundElsewhere
        {
            get => _isBoundElsewhere;
            set => SetProperty(ref _isBoundElsewhere, value);
        }

        /// <summary>下拉框里展示用的副标题 (如 "SIP0001 · ModbusRTU"),供 XAML 直接绑定</summary>
        public string SubtitleText => string.IsNullOrEmpty(InstanceId) ? "" : InstanceId;

        /// <summary>等价比较 — 用于检测两个候选是否表示同一设备</summary>
        public bool Matches(string typeName, string instanceId) =>
            DeviceTypeName == typeName && InstanceId == instanceId;
    }
}