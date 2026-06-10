// EventArgs.cs
using System;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.DataModule.Model;

namespace MaxChemical.Modules.Designer.Events
{
    public class NestedCommandEventArgs : EventArgs
    {
        public CommandNode NestedCommand { get; set; }
        public bool IsTrueBranch { get; set; }
        public CommandNode ParentIfNode { get; set; }
        public int BranchIndex { get; set; } = -1;
    }

    public class LogicCommandRequestEventArgs : EventArgs
    {
        public string PluginId { get; set; }
        public LogicCommand ResultCommand { get; set; }
    }

    public class CommandDragInfo
    {
        public object Command { get; set; }
    }

    public class NestedNodeInfo
    {
        public CommandNode Node { get; set; }
        public int BranchIndex { get; set; }
    }

    public class ParallelBranchInfo
    {
        public int Index { get; set; }
        public System.Collections.Generic.List<CommandNode> Commands { get; set; } = new System.Collections.Generic.List<CommandNode>();
        public System.Windows.Controls.Border Container { get; set; }
    }

    // 委托定义
    public delegate void NodeClickHandler(CommandNode node);
    public delegate void NodeDeleteHandler(CommandNode nodeToDelete);
    public delegate void NestedCommandAddedHandler(NestedCommandEventArgs args);
    public delegate void NestedCommandRemovedHandler(NestedCommandEventArgs args);
    public delegate void ContentChangedHandler();
    public delegate void LogicCommandRequestHandler(LogicCommandRequestEventArgs args);
}