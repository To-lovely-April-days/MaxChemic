using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MaxChemical.Modules.Designer.Service.Execution;
using Prism.Events;

namespace MaxChemical.Modules.Designer.Event
{
    // 执行流程请求事件
    public class ExecuteFlowRequestEvent : PubSubEvent<ExecuteFlowRequestEventArgs>
    {
    }

    public class ExecuteFlowRequestEventArgs
    {
        public bool IsSimulationMode { get; set; }
    }

    // 暂停流程请求事件
    public class PauseFlowRequestEvent : PubSubEvent
    {
    }

    // 恢复流程请求事件
    public class ResumeFlowRequestEvent : PubSubEvent
    {
    }

    // 停止流程请求事件
    public class StopFlowRequestEvent : PubSubEvent
    {
    }

    // 流程执行状态变化事件
    public class FlowExecutionStateChangedEvent : PubSubEvent<FlowExecutionStateChangedEventArgs>
    {
    }

    // 获取当前流程设计器事件
    public class GetCurrentFlowDesignerRequestEvent : PubSubEvent<Action<MaxChemical.Modules.Designer.ViewModels.FlowDesignerViewModel>>
    {
    }
}
