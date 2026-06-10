using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DevicePlugins.Devices;
using Prism.Events;

namespace MaxChemical.Modules.Designer.Event
{
    public class NodeExecutionResultUpdatedEvent : PubSubEvent<NodeExecutionResultEventArgs> { }

    public class NodeExecutionResultEventArgs
    {
        public string NodeId { get; set; }
        public string DeviceName { get; set; }
        public string CommandName { get; set; }
        public DeviceParameters OutputParameters { get; set; }

        public bool IsSimulationMode { get; set; }
    }
}
