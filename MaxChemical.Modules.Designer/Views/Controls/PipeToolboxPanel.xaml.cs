// Views/Controls/PipeToolboxPanel.xaml.cs
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MaxChemical.Modules.Designer.Views.Controls
{
    public partial class PipeToolboxPanel : UserControl
    {
        public event EventHandler<PipeDragStartEventArgs> PipeDragStarted;

        public PipeToolboxPanel()
        {
            InitializeComponent();
        }

        #region 鼠标悬停效果

        private void OnToolboxItemMouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Border border)
            {
                border.Background = new SolidColorBrush(Color.FromRgb(230, 240, 250));
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(100, 150, 200));
                border.BorderThickness = new Thickness(2);
            }
        }

        private void OnToolboxItemMouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Border border)
            {
                border.Background = new SolidColorBrush(Color.FromRgb(245, 245, 245));
                border.BorderBrush = Brushes.Transparent;
                border.BorderThickness = new Thickness(0);
            }
        }

        #endregion

        #region 直管道拖拽

        private void OnStraightPipeMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                var pipeData = new PipeDragData
                {
                    PipeType = PipeType.Straight,
                    Width = 96,
                    Height = 45
                };

                var dataObject = new DataObject("PipeData", pipeData);
                DragDrop.DoDragDrop(this, dataObject, DragDropEffects.Copy);

                PipeDragStarted?.Invoke(this, new PipeDragStartEventArgs
                {
                    PipeType = PipeType.Straight
                });
            }
        }

        #endregion

        #region 弯管道拖拽

        private void OnElbowPipeMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                var pipeData = new PipeDragData
                {
                    PipeType = PipeType.Elbow,
                    Width = 80,
                    Height = 80
                };

                var dataObject = new DataObject("PipeData", pipeData);
                DragDrop.DoDragDrop(this, dataObject, DragDropEffects.Copy);

                PipeDragStarted?.Invoke(this, new PipeDragStartEventArgs
                {
                    PipeType = PipeType.Elbow
                });
            }
        }

        #endregion

        #region Z字形管道拖拽

        private void OnZShapePipeMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                var pipeData = new PipeDragData
                {
                    PipeType = PipeType.ZShape,
                    Width = 200,
                    Height = 140
                };

                var dataObject = new DataObject("PipeData", pipeData);
                DragDrop.DoDragDrop(this, dataObject, DragDropEffects.Copy);

                PipeDragStarted?.Invoke(this, new PipeDragStartEventArgs
                {
                    PipeType = PipeType.ZShape
                });
            }
        }

        #endregion

        /// <summary>
        /// 锥形瓶拖拽
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void OnConicalFlaskMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                var pipeData = new PipeDragData
                {
                    PipeType = PipeType.Conical,  // ✅ 新增枚举值
                    Width = 80,
                    Height = 120
                };

                var dataObject = new DataObject("PipeData", pipeData);
                DragDrop.DoDragDrop(this, dataObject, DragDropEffects.Copy);

                PipeDragStarted?.Invoke(this, new PipeDragStartEventArgs
                {
                    PipeType = PipeType.Conical
                });
            }
        }
    }

    #region 事件参数和数据类

    public class PipeDragStartEventArgs : EventArgs
    {
        public PipeType PipeType { get; set; }
    }

    public class PipeDragData
    {
        public PipeType PipeType { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    public enum PipeType
    {
        Straight,   // 直管道
        Elbow,      // 弯管道（90度）
        ZShape,     // Z字形管道
        Conical,    // ✅ 锥形瓶
        Tee,        // 三通管道
        Cross,      // 四通管道
        Valve       // 阀门
    }

    #endregion
}