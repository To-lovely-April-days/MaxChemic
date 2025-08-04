using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace MaxChemical.Modules.Designer.Models
{
    public class BranchContainer
    {
        public string BranchId { get; set; } = Guid.NewGuid().ToString();
        public string ParentStepId { get; set; }
        public string BranchType { get; set; }
        public StackPanel Container { get; set; }
        public List<CommandNode> Steps { get; set; } = new List<CommandNode>();
        public int NestingLevel { get; set; } = 0;
    }
}
