// Models/ProjectDocument.cs
using System;
using System.Collections.Generic;
using System.Windows;
using MaxChemical.Core;

namespace MaxChemical.Modules.Designer.Models
{
    /// <summary>
    /// 统一的项目文档 - 包含画布设计和流程设计的所有内容
    /// </summary>
    public class ProjectDocument
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "新建项目";
        public string Description { get; set; } = "";
        public DateTime CreatedTime { get; set; } = DateTime.Now;
        public DateTime LastModifiedTime { get; set; } = DateTime.Now;
        public string Version { get; set; } = "1.0";
        public string ProjectType { get; set; } = "MaxChemical";

        // 画布设计部分（ProcessDesignerView）
        public CanvasDesignData CanvasDesign { get; set; } = new CanvasDesignData();

        // 流程设计部分（FlowDesignerView）
        public FlowDesignData FlowDesign { get; set; } = new FlowDesignData();

        // 项目级别的全局配置
        public Dictionary<string, object> GlobalProperties { get; set; } = new Dictionary<string, object>();
    }
    /// <summary>
    /// 画布设计数据（基于现有CanvasDocument)
    /// </summary>
    public class CanvasDesignData
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "新建画布";
        public DateTime CreatedTime { get; set; } = DateTime.Now;
        public DateTime LastModifiedTime { get; set; } = DateTime.Now;
        public RegionLayout RegionLayout { get; set; }
        public List<CanvasDeviceData> Devices { get; set; } = new List<CanvasDeviceData>();
        public List<ConnectionData> Connections { get; set; } = new List<ConnectionData>();

        //  新增：管道数据集合
        public List<CanvasPipeData> Pipes { get; set; } = new List<CanvasPipeData>();

        public double CanvasWidth { get; set; } = 1200;
        public double CanvasHeight { get; set; } = 800;

        // 扩展的画布状态信息
        public Dictionary<string, string> DeviceDisplayNames { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, int> DeviceNameCounters { get; set; } = new Dictionary<string, int>();
        public double CanvasZoom { get; set; } = 1.0;
        public CanvasScrollOffset ScrollOffset { get; set; } = new CanvasScrollOffset();
        public string SelectedDeviceId { get; set; }

        /// <summary>
        /// 设备监控参数选择状态 - DeviceId -> 参数名列表
        /// </summary>
        public Dictionary<string, HashSet<string>> DeviceMonitoringSelections { get; set; } = new Dictionary<string, HashSet<string>>();

        /// <summary>
        /// 设备监控卡片状态 - DeviceId -> 是否显示监控卡片
        /// </summary>
        public Dictionary<string, bool> DeviceMonitoringCardStates { get; set; } = new Dictionary<string, bool>();

        /// <summary>
        /// 监控卡片位置数据 - DeviceId -> (X, Y)
        /// </summary>
        public Dictionary<string, Point> MonitoringCardPositions { get; set; } = new Dictionary<string, Point>();
    }

    /// <summary>
    /// 流程设计数据（来自FlowDesignerView）
    /// </summary>
    public class FlowDesignData
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "新建流程";
        public DateTime CreatedTime { get; set; } = DateTime.Now;
        public DateTime LastModifiedTime { get; set; } = DateTime.Now;
        public double CanvasWidth { get; set; } = 1200;
        public double CanvasHeight { get; set; } = 800;
        public double StartNodeX { get; set; } = 100;
        public double StartNodeY { get; set; } = 20;
        public List<FlowCommandNodeData> CommandNodes { get; set; } = new List<FlowCommandNodeData>();
        public List<FlowConnectionData> Connections { get; set; } = new List<FlowConnectionData>();
        public Dictionary<string, object> FlowProperties { get; set; } = new Dictionary<string, object>();
        public string SelectedNodeId { get; set; }
    }

    /// <summary>
    /// 画布滚动偏移
    /// </summary>
    public class CanvasScrollOffset
    {
        public double X { get; set; } = 0;
        public double Y { get; set; } = 0;
    }

    /// <summary>
    /// 流程命令节点数据
    /// </summary>
    public class FlowCommandNodeData
    {
        // 基本属性
        public string NodeId { get; set; }
        public string DisplayName { get; set; }
        public string CommandType { get; set; } // "DeviceCommand" 或 "LogicCommand"
        public double X { get; set; }
        public double Y { get; set; }
        public int Order { get; set; }
        public bool IsVisible { get; set; } = true;

        // 设备命令相关
        public string DeviceCommandName { get; set; }
        public string DeviceCommandType { get; set; }
        public string BoundDeviceId { get; set; }
        public string BoundDeviceName { get; set; }
        public Dictionary<string, string> ParameterOverrides { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, object> DeviceCommandProperties { get; set; } = new Dictionary<string, object>();

        // 逻辑命令相关
        public string LogicCommandType { get; set; }
        public string LogicCommandName { get; set; }
        public Dictionary<string, object> LogicCommandProperties { get; set; } = new Dictionary<string, object>();

        // 子节点（用于容器类型节点如IF、循环、并行等）
        public List<FlowCommandNodeData> Children { get; set; } = new List<FlowCommandNodeData>();

        // 执行属性（分支标识等）
        public Dictionary<string, object> ExecutionProperties { get; set; } = new Dictionary<string, object>();

        // 节点控件相关
        public Dictionary<string, object> NodeControlProperties { get; set; } = new Dictionary<string, object>();

        // 执行状态
        public string ExecutionStatus { get; set; } = "Idle";
        public DateTime? ExecutionStartTime { get; set; }
        public DateTime? ExecutionEndTime { get; set; }
        public string ExecutionErrorMessage { get; set; }
    }

    /// <summary>
    /// 流程连接线数据
    /// </summary>
    public class FlowConnectionData
    {
        public string FromNodeId { get; set; }
        public string ToNodeId { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public int LineLength { get; set; }
        public int ArrowTop { get; set; }
        public int TotalHeight { get; set; }
    }

    /// <summary>
    /// 流程文档（为了兼容现有代码）
    /// </summary>
    public class FlowDocument
    {
        public string Name { get; set; } = "新建流程";
        public string Description { get; set; } = "";
        public DateTime CreatedTime { get; set; } = DateTime.Now;
        public DateTime LastModifiedTime { get; set; } = DateTime.Now;
        public string Version { get; set; } = "1.0";

        // 画布属性
        public double CanvasWidth { get; set; } = 1200;
        public double CanvasHeight { get; set; } = 800;
        public double StartNodeX { get; set; } = 100;
        public double StartNodeY { get; set; } = 20;

        // 流程内容
        public List<FlowCommandNodeData> CommandNodes { get; set; } = new List<FlowCommandNodeData>();
        public List<FlowConnectionData> Connections { get; set; } = new List<FlowConnectionData>();

        // 流程配置
        public Dictionary<string, object> FlowProperties { get; set; } = new Dictionary<string, object>();
    }
}