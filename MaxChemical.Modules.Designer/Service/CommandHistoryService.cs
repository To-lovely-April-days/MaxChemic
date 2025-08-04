using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Modules.Designer.Service
{
    public class CommandHistoryService : ICommandHistory
    {
        private readonly List<ICommand> _history;
        private int _currentIndex;
        private const int MaxHistorySize = 50; // 最大历史记录数

        public CommandHistoryService()
        {
            _history = new List<ICommand>();
            _currentIndex = -1;
        }

        public bool CanUndo => _currentIndex >= 0;
        public bool CanRedo => _currentIndex < _history.Count - 1;

        public event Action<bool, bool> CanUndoRedoChanged;

        public void ExecuteCommand(ICommand command)
        {
            try
            {
                Debug.WriteLine($"执行命令: {command.Description}");

                // 执行命令
                command.Execute();

                // 清除当前位置之后的所有命令（因为执行了新命令）
                if (_currentIndex < _history.Count - 1)
                {
                    _history.RemoveRange(_currentIndex + 1, _history.Count - _currentIndex - 1);
                }

                // 添加新命令
                _history.Add(command);
                _currentIndex++;

                // 限制历史记录大小
                if (_history.Count > MaxHistorySize)
                {
                    _history.RemoveAt(0);
                    _currentIndex--;
                }

                NotifyCanUndoRedoChanged();
                Debug.WriteLine($"命令历史: 当前索引={_currentIndex}, 总数={_history.Count}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"执行命令失败: {ex.Message}");
                throw;
            }
        }

        public void Undo()
        {
            if (!CanUndo)
            {
                Debug.WriteLine("无法撤销：没有可撤销的命令");
                return;
            }

            try
            {
                var command = _history[_currentIndex];
                Debug.WriteLine($"撤销命令: {command.Description}");

                command.Undo();
                _currentIndex--;

                NotifyCanUndoRedoChanged();
                Debug.WriteLine($"撤销完成: 当前索引={_currentIndex}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"撤销失败: {ex.Message}");
                throw;
            }
        }

        public void Redo()
        {
            if (!CanRedo)
            {
                Debug.WriteLine("无法重做：没有可重做的命令");
                return;
            }

            try
            {
                _currentIndex++;
                var command = _history[_currentIndex];
                Debug.WriteLine($"重做命令: {command.Description}");

                command.Execute();

                NotifyCanUndoRedoChanged();
                Debug.WriteLine($"重做完成: 当前索引={_currentIndex}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"重做失败: {ex.Message}");
                _currentIndex--; // 回退索引
                throw;
            }
        }

        public void Clear()
        {
            _history.Clear();
            _currentIndex = -1;
            NotifyCanUndoRedoChanged();
            Debug.WriteLine("命令历史已清空");
        }

        public string GetUndoDescription()
        {
            return CanUndo ? _history[_currentIndex].Description : "无可撤销操作";
        }

        public string GetRedoDescription()
        {
            return CanRedo ? _history[_currentIndex + 1].Description : "无可重做操作";
        }

        private void NotifyCanUndoRedoChanged()
        {
            CanUndoRedoChanged?.Invoke(CanUndo, CanRedo);
        }
    }
}
