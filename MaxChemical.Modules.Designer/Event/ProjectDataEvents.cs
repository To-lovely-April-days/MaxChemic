// Event/ProjectDataEvents.cs
using Prism.Events;
using MaxChemical.Modules.Designer.Models;
using System;
using System.Collections.Generic;
using MaxChemical.Core;

namespace MaxChemical.Modules.Designer.Event
{
    /// <summary>
    /// 获取当前项目数据请求事件
    /// </summary>
    public class GetCurrentProjectDataRequestEvent : PubSubEvent<Action<ProjectDocument>>
    {
    }

    /// <summary>
    /// 清空项目请求事件
    /// </summary>
    public class ClearProjectRequestEvent : PubSubEvent
    {
    }

    /// <summary>
    /// 新项目创建事件
    /// </summary>
    public class NewProjectCreatedEvent : PubSubEvent<ProjectDocument>
    {
    }

    /// <summary>
    /// 加载项目请求事件
    /// </summary>
    public class LoadProjectRequestEvent : PubSubEvent<ProjectDocument>
    {
    }

    /// <summary>
    /// 项目加载完成事件
    /// </summary>
    public class ProjectLoadedEvent : PubSubEvent<ProjectDocument>
    {
    }
    /// <summary>
    /// 项目名称更新事件
    /// </summary>
    public class ProjectNameUpdatedEvent : PubSubEvent<string>
    {
    }
    /// <summary>
    /// 项目保存事件
    /// </summary>
    public class ProjectSavedEvent : PubSubEvent<string>
    {
    }

    /// <summary>
    /// 项目修改事件
    /// </summary>
    public class ProjectModifiedEvent : PubSubEvent
    {
    }

    /// <summary>
    /// 恢复设备显示名事件
    /// </summary>
    public class RestoreDeviceDisplayNamesEvent : PubSubEvent<Dictionary<string, string>>
    {
    }

    /// <summary>
    /// 恢复设备名称计数器事件
    /// </summary>
    public class RestoreDeviceNameCountersEvent : PubSubEvent<Dictionary<string, int>>
    {
    }

    /// <summary>
    /// 获取设备名称计数器请求事件
    /// </summary>
    public class GetDeviceNameCountersRequestEvent : PubSubEvent<Action<Dictionary<string, int>>>
    {
    }

    /// <summary>
    /// 获取当前流程数据请求事件
    /// </summary>
    public class GetCurrentFlowDataRequestEvent : PubSubEvent<Action<FlowDocument>>
    {
    }

    /// <summary>
    /// 清空流程请求事件
    /// </summary>
    public class ClearFlowRequestEvent : PubSubEvent
    {
    }

    /// <summary>
    /// 新流程创建事件
    /// </summary>
    public class NewFlowCreatedEvent : PubSubEvent<FlowDocument>
    {
    }

    /// <summary>
    /// 加载流程请求事件
    /// </summary>
    public class LoadFlowRequestEvent : PubSubEvent<FlowDocument>
    {
    }

    /// <summary>
    /// 流程加载完成事件
    /// </summary>
    public class FlowLoadedEvent : PubSubEvent<FlowDocument>
    {
    }

    /// <summary>
    /// 流程保存事件
    /// </summary>
    public class FlowSavedEvent : PubSubEvent<string>
    {
    }

    /// <summary>
    /// 流程修改事件
    /// </summary>
    public class FlowModifiedEvent : PubSubEvent
    {
    }

    /// <summary>
    /// 恢复区域布局事件
    /// </summary>
    public class RestoreRegionLayoutEvent : PubSubEvent<RegionLayout>
    {
    }

   
   
    /// <summary>
    /// 选择设备事件
    /// </summary>
    public class SelectDeviceEvent : PubSubEvent<string>
    {
    }

    /// <summary>
    /// 获取选中管道请求事件
    /// </summary>
    public class GetSelectedPipeRequestEvent : PubSubEvent<Action<string>>
    {
    }

    /// <summary>
    /// 删除选中管道事件
    /// </summary>
    public class DeleteSelectedPipeEvent : PubSubEvent<string>
    {
    }

    /// <summary>
    /// 请求收集管道数据事件
    /// </summary>
    public class CollectPipeDataRequestEvent : PubSubEvent<Action<List<CanvasPipeData>>>
    {
    }

    /// <summary>
    /// 请求恢复管道数据事件
    /// </summary>
    public class RestorePipeDataRequestEvent : PubSubEvent<List<CanvasPipeData>>
    {
    }
}
