using Prism.Events;
using System;
using System.Collections.Generic;

namespace MaxChemical.Modules.Designer.Event
{
    public class VariableUpdatedEvent : PubSubEvent<VariableUpdatedEventArgs>
    {
    }

    public class VariableUpdatedEventArgs
    {
        public string VariableName { get; set; }
        public object VariableValue { get; set; }
        public string VariableType { get; set; }
        public DateTime UpdateTime { get; set; } = DateTime.Now;
        public string NodeId { get; set; }
        public string NodeName { get; set; }
    }

    public class RequestContextVariablesEvent : PubSubEvent<Action<Dictionary<string, object>>>
    {
    }
}