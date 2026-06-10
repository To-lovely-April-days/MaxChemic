using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MaxChemical.Modules.Designer.Service;
using MaxChemical.Modules.Designer.Views.Controls;

namespace MaxChemical.Modules.Designer.Commands
{
    /// <summary>
    /// 添加管道命令 - 支持撤销/重做
    /// </summary>
    public class AddPipeCommand : ICommand
    {
        private readonly Canvas _canvas;
        private readonly FrameworkElement _pipe;
        private readonly Point _position;
        private readonly string _pipeType;
        private readonly IPipeConnectionService _pipeConnectionService;

        public AddPipeCommand(
            Canvas canvas,
            FrameworkElement pipe,
            Point position,
            string pipeType,
            IPipeConnectionService pipeConnectionService)
        {
            _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
            _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
            _position = position;
            _pipeType = pipeType ?? "未知管道";
            _pipeConnectionService = pipeConnectionService ?? throw new ArgumentNullException(nameof(pipeConnectionService));
        }

        public string Description => $"添加{_pipeType}";

        public void Execute()
        {
            try
            {
                // 添加管道到画布
                if (!_canvas.Children.Contains(_pipe))
                {
                    _pipeConnectionService.AddPipeToCanvas(_pipe, _position);
                    Debug.WriteLine($"执行添加管道: {GetPipeId()}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"执行添加管道失败: {ex.Message}");
                throw;
            }
        }

        public void Undo()
        {
            try
            {
                // 从画布移除管道
                if (_canvas.Children.Contains(_pipe))
                {
                    _canvas.Children.Remove(_pipe);
                    Debug.WriteLine($"撤销添加管道: {GetPipeId()}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"撤销添加管道失败: {ex.Message}");
                throw;
            }
        }

        private string GetPipeId()
        {
            if (_pipe is StraightPipeControl straightPipe)
                return straightPipe.PipeId;
            if (_pipe is ElbowPipeControl elbowPipe)
                return elbowPipe.PipeId;
            if (_pipe is ZShapePipeControl zPipe)
                return zPipe.PipeId;
            return "未知";
        }
    }
}