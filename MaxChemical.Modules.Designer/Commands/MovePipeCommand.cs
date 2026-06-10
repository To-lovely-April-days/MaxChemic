using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MaxChemical.Modules.Designer.Service;
using MaxChemical.Modules.Designer.Views.Controls;

namespace MaxChemical.Modules.Designer.Commands
{
    /// <summary>
    /// 移动管道命令 - 支持撤销/重做
    /// </summary>
    public class MovePipeCommand : ICommand
    {
        private readonly FrameworkElement _pipe;
        private readonly Point _oldPosition;
        private readonly Point _newPosition;
        private readonly string _pipeType;

        public MovePipeCommand(
            FrameworkElement pipe,
            Point oldPosition,
            Point newPosition)
        {
            _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
            _oldPosition = oldPosition;
            _newPosition = newPosition;
            _pipeType = GetPipeTypeName(pipe);
        }

        public string Description => $"移动{_pipeType}";

        public void Execute()
        {
            try
            {
                Canvas.SetLeft(_pipe, _newPosition.X);
                Canvas.SetTop(_pipe, _newPosition.Y);
                Debug.WriteLine($"执行移动管道到: ({_newPosition.X:F0}, {_newPosition.Y:F0})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"执行移动管道失败: {ex.Message}");
                throw;
            }
        }

        public void Undo()
        {
            try
            {
                Canvas.SetLeft(_pipe, _oldPosition.X);
                Canvas.SetTop(_pipe, _oldPosition.Y);
                Debug.WriteLine($"撤销移动管道到: ({_oldPosition.X:F0}, {_oldPosition.Y:F0})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"撤销移动管道失败: {ex.Message}");
                throw;
            }
        }

        private string GetPipeTypeName(FrameworkElement pipe)
        {
            if (pipe is StraightPipeControl)
                return "直管道";
            if (pipe is ElbowPipeControl)
                return "弯管道";
            if (pipe is ZShapePipeControl)
                return "Z形管道";
            return "管道";
        }
    }
}