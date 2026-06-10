using Prism.Events;

namespace MaxChemical.Infrastructure.Events
{
    /// <summary>
    /// 切换到画布设计器事件
    /// </summary>
    public class SwitchToCanvasDesignerEvent : PubSubEvent
    {
    }

    /// <summary>
    /// 切换到流程设计器事件
    /// </summary>
    public class SwitchToFlowDesignerEvent : PubSubEvent
    {
    }

    /// <summary>
    /// 打开数据分析窗口事件
    /// </summary>
    public class OpenDataAnalysisWindowEvent : PubSubEvent
    {
    }
}