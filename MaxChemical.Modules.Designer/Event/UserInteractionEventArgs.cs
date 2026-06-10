using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Modules.Designer.Event
{
    public class UserInteractionEventArgs : EventArgs
    {
        public string NodeId { get; set; }
        public string NodeName { get; set; }
        public string InteractionType { get; set; }
        public string Description { get; set; }
        public DateTime StartTime { get; set; }
    }
}
