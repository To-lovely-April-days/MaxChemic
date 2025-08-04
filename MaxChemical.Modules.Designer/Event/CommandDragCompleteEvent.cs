using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Prism.Events;

namespace MaxChemical.Modules.Designer.Event
{
    public class CommandDragFCompleteEvent : PubSubEvent<CommandDragCompleteEventArgs>
    {
    }

    public class CommandDragCompleteEventArgs
    {
        public object Command { get; set; }
        public System.Windows.Point DropPosition { get; set; }
        public string SourceType { get; set; } // "DeviceCommand" 或 "LogicCommand"

        public CommandDragCompleteEventArgs(object command, System.Windows.Point dropPosition, string sourceType)
        {
            Command = command;
            DropPosition = dropPosition;
            SourceType = sourceType;
        }
    }
}
