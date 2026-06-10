using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Prism.Events;

namespace MaxChemical.Modules.Designer.Event
{
    public class VariablePersistedEvent : PubSubEvent<VariablePersistedEventArgs>
    {
    }

    public class VariablePersistedEventArgs
    {
        public string VariableName { get; set; }
        public object VariableValue { get; set; }
        public string VariableType { get; set; }
        public DateTime UpdateTime { get; set; }
        public bool IsNewVariable { get; set; }
    }
}
