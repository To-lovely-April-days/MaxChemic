using DevicePlugins.Devices;

namespace MaxChemical.DataModule.Model
{
    // 命令拖拽信息类
    public class CommandDragInfo
    {
        public DeviceCommand Command { get; set; }
        public System.Windows.Controls.Control SourceControl { get; set; }
        public DateTime DragStartTime { get; set; }
    }
}
