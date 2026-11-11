using System.Windows;
using DevicePlugins.Devices;
using MaxChemical.Core;
using Prism.Events;

namespace MaxChemical.Infrastructure.Events;

public class LanguageChangedEvent : PubSubEvent<string> { }
public class UserLoginEvent : PubSubEvent<UserInfo> { }
public class UserLogoutEvent : PubSubEvent { }
public class ProjectOpenedEvent : PubSubEvent<string> { }
public class ProjectSavedEvent : PubSubEvent<string> { }
public class StatusMessageChangedEvent : PubSubEvent<string> { }
public class SimulationModeChangedEvent : PubSubEvent<bool> { }
public class LeftPanelVisibilityChangedEvent : PubSubEvent<bool> { }
public class LeftPanelChangeRequestEvent : PubSubEvent<string>
{
}
public class StatusBarVisibilityChangedEvent : PubSubEvent<bool> { }
public class DeviceSelectedEvent : PubSubEvent<DeviceInfo> { }

public record UserInfo(string UserId, string UserName, string Role);
public record DeviceInfo(string DeviceId, string DeviceName, string DeviceType);

public class PanelVisibilityEvent : PubSubEvent<bool> { }

// 设备选择事件
public class IDeviceSelectedEvent : PubSubEvent<IDevice>
{
}
public class RegionLayoutUpdatedEvent : PubSubEvent<RegionLayout>
{
}
public class GetCanvasSizeRequestEvent : PubSubEvent<Action<double, double>>
{ 

}
// 设备拖拽开始事件
public class DeviceDragStartedEvent : PubSubEvent<DeviceDragStartedEventArgs>
{
}
/// <summary>
/// 流程设计器UI刷新请求事件
/// </summary>
public class FlowDesignerUIRefreshRequestEvent : PubSubEvent
{
}
public class DeviceDragStartedEventArgs
{
    public IDevice Device { get; set; }
    public Point StartPosition { get; set; }
    public string SourceType { get; set; } // "Library" 或 "Canvas"
    public object SourceElement { get; set; }
}
// 设备拖拽进行中事件
public class DeviceDraggingEvent : PubSubEvent<DeviceDraggingEventArgs>
{
}

public class DeviceDraggingEventArgs
{
    public IDevice Device { get; set; }
    public Point CurrentPosition { get; set; }
    public Point DeltaPosition { get; set; }
    public bool IsOverValidDropTarget { get; set; }
}

// 设备拖拽结束事件
public class DeviceDragEndedEvent : PubSubEvent<DeviceDragEndedEventArgs>
{
}

public class DeviceDragEndedEventArgs
{
    public IDevice Device { get; set; }
    public Point EndPosition { get; set; }
    public bool IsSuccessful { get; set; }
    public string TargetType { get; set; } // "Canvas", "Trash" 等
}

// 画布拖拽进入事件
public class CanvasDragEnterEvent : PubSubEvent<CanvasDragEventArgs>
{
}

// 画布拖拽悬停事件
public class CanvasDragOverEvent : PubSubEvent<CanvasDragEventArgs>
{
}

// 画布拖拽离开事件
public class CanvasDragLeaveEvent : PubSubEvent<CanvasDragEventArgs>
{
}

// 画布拖拽放置事件
public class CanvasDropEvent : PubSubEvent<CanvasDropEventArgs>
{
}

public class CanvasDragEventArgs
{
    public IDevice Device { get; set; }
    public Point Position { get; set; }
    public DragDropEffects Effects { get; set; }
}

public class CanvasDropEventArgs : CanvasDragEventArgs
{
    public bool IsSuccessful { get; set; }
    public string DeviceId { get; set; } // 生成的设备实例ID
}

// 画布设备位置更新事件
public class CanvasDevicePositionChangedEvent : PubSubEvent<CanvasDevicePositionChangedEventArgs>
{
}

public class CanvasDevicePositionChangedEventArgs
{
    public string DeviceId { get; set; }
    public IDevice Device { get; set; }
    public Point OldPosition { get; set; }
    public Point NewPosition { get; set; }
    public bool IsCompleted { get; set; } // true表示拖拽完成，false表示拖拽中
}

public class DeviceTypeMapUpdatedEvent : PubSubEvent<Dictionary<string, string>> { }

/// <summary>
/// 语音创建设备事件参数
/// </summary>
public class VoiceCreateDeviceEventArgs
{
    /// <summary>
    /// 设备名称
    /// </summary>
    public string DeviceName { get; set; }

    /// <summary>
    /// 语音命令
    /// </summary>
    public string Command { get; set; }

    /// <summary>
    /// 时间戳
    /// </summary>
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// 语音创建设备事件
/// </summary>
public class VoiceCreateDeviceEvent : PubSubEvent<VoiceCreateDeviceEventArgs>
{
}

/// <summary>
/// AI 小桐请求创建设备的结果(请求-回调,让工具拿到新设备的显示名等)。
/// </summary>
public class AgentCreateDeviceResult
{
    public bool Success { get; set; }
    public string DeviceId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Error { get; set; } = "";
}

/// <summary>
/// AI 小桐请求把设备库设备放到工艺设计器画布的事件参数(等效拖拽)。
/// 位置由画布自动排列(CreateDeviceFromVoiceCommand 的智能避让),不指定坐标。
/// </summary>
public class AgentCreateDeviceEventArgs
{
    public string DeviceName { get; set; } = "";
    public Action<AgentCreateDeviceResult> Callback { get; set; }
}

/// <summary>
/// AI 小桐请求创建设备事件(ProcessDesignerView 订阅后走语音链路同一创建路径)。
/// </summary>
public class AgentCreateDeviceEvent : PubSubEvent<AgentCreateDeviceEventArgs>
{
}

/// <summary>
/// AI 小桐请求打开画布设备的连接界面(等效双击)。载荷 = 画布设备 DeviceId。
/// </summary>
public class ShowDevicePropertyDialogRequestEvent : PubSubEvent<string>
{
}
/// <summary>
/// 语音设置设备参数事件参数
/// </summary>
public class VoiceSetParameterEventArgs
{
    public string DeviceType { get; set; }      // 设备类型 (柱塞泵、流量计等)
    public string ParameterName { get; set; }   // 参数名称 (TargetSpeed、TargetFlow等)
    public double ParameterValue { get; set; }  // 参数值
    public string Command { get; set; }         // 原始语音命令
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// 语音控制设备状态事件参数
/// </summary>
public class VoiceControlStateEventArgs
{
    public string DeviceType { get; set; }      // 设备类型
    public bool TargetState { get; set; }       // 目标状态 (true=启动/打开, false=停止/关闭)
    public string Command { get; set; }         // 原始语音命令
    public DateTime Timestamp { get; set; }
}



/// <summary>
/// 语音设置设备参数事件
/// </summary>
public class VoiceSetDeviceParameterEvent : PubSubEvent<VoiceSetParameterEventArgs> { }

/// <summary>
/// 语音控制设备状态事件
/// </summary>
public class VoiceControlDeviceStateEvent : PubSubEvent<VoiceControlStateEventArgs> { }