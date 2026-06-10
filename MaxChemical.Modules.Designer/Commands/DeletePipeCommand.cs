using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using MaxChemical.Modules.Designer.Service;
using MaxChemical.Modules.Designer.Views.Controls;

namespace MaxChemical.Modules.Designer.Commands
{
    /// <summary>
    /// 删除管道命令 - 支持撤销/重做，正确恢复位置和状态
    /// </summary>
    public class DeletePipeCommand : ICommand
    {
        private readonly Canvas _canvas;
        private readonly FrameworkElement _pipe;
        private readonly Point _originalPosition;
        private readonly int _originalZIndex;
        private readonly string _pipeType;
        private readonly IPipeConnectionService _pipeConnectionService;

        //  新增：保存管道的完整状态
        private readonly PipeState _pipeState;

        public DeletePipeCommand(
            Canvas canvas,
            FrameworkElement pipe,
            IPipeConnectionService pipeConnectionService)
        {
            _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
            _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
            _pipeConnectionService = pipeConnectionService ?? throw new ArgumentNullException(nameof(pipeConnectionService));

            //  记录原始位置
            var left = Canvas.GetLeft(pipe);
            var top = Canvas.GetTop(pipe);
            _originalPosition = new Point(
                double.IsNaN(left) ? 0 : left,
                double.IsNaN(top) ? 0 : top);

            //  记录 ZIndex
            _originalZIndex = Panel.GetZIndex(pipe);

            //  保存管道状态
            _pipeState = SavePipeState(pipe);

            // 获取管道类型
            _pipeType = GetPipeTypeName(pipe);

            Debug.WriteLine($"DeletePipeCommand 创建: {GetPipeId()}");
            Debug.WriteLine($"  位置: Left={_originalPosition.X:F0}, Top={_originalPosition.Y:F0}");
            Debug.WriteLine($"  ZIndex: {_originalZIndex}");
        }

        public string Description => $"删除{_pipeType}";

        public void Execute()
        {
            try
            {
                // 从画布移除管道
                if (_canvas.Children.Contains(_pipe))
                {
                    _canvas.Children.Remove(_pipe);
                    Debug.WriteLine($" 执行删除管道: {GetPipeId()}");
                }
                else
                {
                    Debug.WriteLine($" 管道不在画布中: {GetPipeId()}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($" 执行删除管道失败: {ex.Message}");
                throw;
            }
        }

        public void Undo()
        {
            try
            {
                Debug.WriteLine($"开始撤销删除管道: {GetPipeId()}");

                //  恢复管道到画布（直接操作，不依赖服务）
                if (!_canvas.Children.Contains(_pipe))
                {
                    //  先恢复状态
                    RestorePipeState(_pipe, _pipeState);

                    //  直接设置位置
                    Canvas.SetLeft(_pipe, _originalPosition.X);
                    Canvas.SetTop(_pipe, _originalPosition.Y);
                    Panel.SetZIndex(_pipe, _originalZIndex);

                    //  添加到画布
                    _canvas.Children.Add(_pipe);

                    //  强制更新布局
                    _pipe.UpdateLayout();

                    Debug.WriteLine($" 撤销删除管道成功: {GetPipeId()}");
                    Debug.WriteLine($"   恢复位置: Left={_originalPosition.X:F0}, Top={_originalPosition.Y:F0}");
                    Debug.WriteLine($"   恢复 ZIndex: {_originalZIndex}");

                    //  验证位置
                    var verifyLeft = Canvas.GetLeft(_pipe);
                    var verifyTop = Canvas.GetTop(_pipe);
                    Debug.WriteLine($"   验证位置: Left={verifyLeft:F0}, Top={verifyTop:F0}");
                }
                else
                {
                    Debug.WriteLine($" 管道已在画布中: {GetPipeId()}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($" 撤销删除管道失败: {ex.Message}");
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

        //  新增：保存管道状态
        private PipeState SavePipeState(FrameworkElement pipe)
        {
            var state = new PipeState();

            try
            {
                if (pipe is StraightPipeControl straightPipe)
                {
                    state.PipeId = straightPipe.PipeId;
                    state.PipeLength = straightPipe.PipeLength;
                    state.PipeWidth = straightPipe.PipeWidth;
                    state.RotationAngle = straightPipe.RotationAngle;
                    state.IsSelected = straightPipe.IsSelected;
                    Debug.WriteLine($"保存直管道状态: 长度={state.PipeLength}, 角度={state.RotationAngle}");
                }
                else if (pipe is ElbowPipeControl elbowPipe)
                {
                    state.PipeId = elbowPipe.PipeId;
                    state.HorizontalLength = elbowPipe.HorizontalLength;
                    state.VerticalLength = elbowPipe.VerticalLength;
                    state.PipeWidth = elbowPipe.PipeWidth;
                    state.RotationAngle = elbowPipe.RotationAngle;
                    state.IsSelected = elbowPipe.IsSelected;
                    Debug.WriteLine($"保存弯管道状态: H={state.HorizontalLength}, V={state.VerticalLength}");
                }
                else if (pipe is ZShapePipeControl zPipe)
                {
                    state.PipeId = zPipe.PipeId;
                    state.TopHorizontalLength = zPipe.TopHorizontalLength;
                    state.BottomHorizontalLength = zPipe.BottomHorizontalLength;
                    state.VerticalOffset = zPipe.VerticalOffset;
                    state.PipeWidth = zPipe.PipeWidth;
                    state.RotationAngle = zPipe.RotationAngle;
                    state.IsSelected = zPipe.IsSelected;
                    Debug.WriteLine($"保存Z形管道状态: Top={state.TopHorizontalLength}, Bottom={state.BottomHorizontalLength}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"保存管道状态失败: {ex.Message}");
            }

            return state;
        }

        //  新增：恢复管道状态
        private void RestorePipeState(FrameworkElement pipe, PipeState state)
        {
            try
            {
                if (pipe is StraightPipeControl straightPipe)
                {
                    straightPipe.PipeLength = state.PipeLength;
                    straightPipe.PipeWidth = state.PipeWidth;
                    straightPipe.RotationAngle = state.RotationAngle;
                    straightPipe.IsSelected = state.IsSelected;
                    Debug.WriteLine($"恢复直管道状态: 长度={state.PipeLength}, 角度={state.RotationAngle}");
                }
                else if (pipe is ElbowPipeControl elbowPipe)
                {
                    elbowPipe.HorizontalLength = state.HorizontalLength;
                    elbowPipe.VerticalLength = state.VerticalLength;
                    elbowPipe.PipeWidth = state.PipeWidth;
                    elbowPipe.RotationAngle = state.RotationAngle;
                    elbowPipe.IsSelected = state.IsSelected;
                    Debug.WriteLine($"恢复弯管道状态: H={state.HorizontalLength}, V={state.VerticalLength}");
                }
                else if (pipe is ZShapePipeControl zPipe)
                {
                    zPipe.TopHorizontalLength = state.TopHorizontalLength;
                    zPipe.BottomHorizontalLength = state.BottomHorizontalLength;
                    zPipe.VerticalOffset = state.VerticalOffset;
                    zPipe.PipeWidth = state.PipeWidth;
                    zPipe.RotationAngle = state.RotationAngle;
                    zPipe.IsSelected = state.IsSelected;
                    Debug.WriteLine($"恢复Z形管道状态: Top={state.TopHorizontalLength}, Bottom={state.BottomHorizontalLength}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"恢复管道状态失败: {ex.Message}");
            }
        }

        //  新增：管道状态数据类
        private class PipeState
        {
            public string PipeId { get; set; }

            // 通用属性
            public double PipeWidth { get; set; }
            public double RotationAngle { get; set; }
            public bool IsSelected { get; set; }

            // 直管道
            public double PipeLength { get; set; }

            // 弯管道
            public double HorizontalLength { get; set; }
            public double VerticalLength { get; set; }

            // Z形管道
            public double TopHorizontalLength { get; set; }
            public double BottomHorizontalLength { get; set; }
            public double VerticalOffset { get; set; }
        }
    }
}