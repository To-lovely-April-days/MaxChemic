using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Modules.Designer.Service
{
    public class CanvasStateService : ICanvasStateService
    {
        private bool _hasUnsavedChanges = false;
        private string _currentFilePath;
        public bool HasUnsavedChanges
        {
            get => _hasUnsavedChanges;
            private set
            {
                if (_hasUnsavedChanges != value)
                {
                    _hasUnsavedChanges = value;
                    ModificationStateChanged?.Invoke(value);
                }
            }
        }
        public string CurrentFilePath
        {
            get => _currentFilePath;
            private set => _currentFilePath = value;
        }
        public event Action<bool> ModificationStateChanged;

        public void MarkAsModified()
        {
            HasUnsavedChanges = true;
        }

        public void MarkAsSaved()
        {
            HasUnsavedChanges = false;
        }

        public void Reset()
        {
            HasUnsavedChanges = false;
            CurrentFilePath = null;
        }

        public void SetCurrentFile(string filePath)
        {
            CurrentFilePath = filePath;
            HasUnsavedChanges = false;
            System.Diagnostics.Debug.WriteLine($"设置当前文件: {filePath}");
        }
    }
}
