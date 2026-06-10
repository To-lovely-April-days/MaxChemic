// Service/IProjectDataService.cs
using MaxChemical.Modules.Designer.Models;
using DevicePlugins.Devices;

namespace MaxChemical.Modules.Designer.Service
{
    public interface IProjectDataService
    {
        /// <summary>
        /// 创建新项目
        /// </summary>
        ProjectDocument CreateNewProject();

        /// <summary>
        /// 保存项目
        /// </summary>
        bool SaveProject(ProjectDocument document, string filePath = null);

        /// <summary>
        /// 加载项目
        /// </summary>
        ProjectDocument LoadProject(string filePath);

        /// <summary>
        /// 获取当前项目数据
        /// </summary>
        ProjectDocument GetCurrentProjectData();

        /// <summary>
        /// 从命令节点恢复设备命令
        /// </summary>
        DeviceCommand RestoreDeviceCommandFromData(FlowCommandNodeData nodeData);

        /// <summary>
        /// 从命令节点恢复逻辑命令
        /// </summary>
        LogicCommand RestoreLogicCommandFromData(FlowCommandNodeData nodeData);

        /// <summary>
        /// 从设备数据恢复设备实例
        /// </summary>
        DevicePlugins.Devices.IDevice RestoreDeviceFromData(CanvasDeviceData deviceData);
    }
}