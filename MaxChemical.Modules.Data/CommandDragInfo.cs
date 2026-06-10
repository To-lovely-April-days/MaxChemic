using DevicePlugins.Devices;

namespace MaxChemical.DataModule.Model
{
    // 命令拖拽信息类
    public class CommandDragInfo
    {
        public DeviceCommand Command { get; set; }
        public System.Windows.Controls.Control SourceControl { get; set; }
        public DateTime DragStartTime { get; set; }
        /// <summary>
        /// 命令来源的设备类型(设备类的 GetType().Name,例如 "TemperatureCirculator_ModbusTCP")。
        /// 由命令库拖拽起点写入,在画布 Drop 处取出,传给 FlowDesignerViewModel.AddCommandToCanvas。
        /// 用于让节点知道"我来自哪个设备类型",从而:
        /// 1. 设备绑定下拉框只列出同类型设备
        /// 2. 流程持久化后再加载时,精准匹配回正确的驱动模板
        /// </summary>
        public string SourceDeviceType { get; set; }
    }
}
