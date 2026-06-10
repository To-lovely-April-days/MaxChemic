using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Modules.Designer.Service
{
    public interface ICommand
    {
        string Description { get; }
        void Execute();
        void Undo();
    }

    public interface ICommandHistory
    {
        void ExecuteCommand(ICommand command);
        bool CanUndo { get; }
        bool CanRedo { get; }
        void Undo();
        void Redo();
        void Clear();
        string GetUndoDescription();
        string GetRedoDescription();
        event System.Action<bool, bool> CanUndoRedoChanged; // CanUndo, CanRedo
    }
}
