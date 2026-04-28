using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MaxChemical.Modules.Designer.Views.Controls
{
    /// <summary>
    /// 统一选中视觉:L 型角标 + 边中点小标记 + 0.5px 半透明外框。
    /// 单实例附着在 DesignCanvas 上,跟随当前选中目标。
    /// </summary>
    public class SelectionOverlay
    {
        private readonly Canvas _hostCanvas;
        private readonly Canvas _root;          // 选中标记的容器(可旋转)
        private readonly Rectangle _outline;    // 半透明外框
        private readonly Path[] _corners;       // 4 个 L 型角标
        private readonly Line[] _midMarks;      // 4 个边中点小标记
        private readonly RotateTransform _rotate;

        private FrameworkElement _target;

        // ─── 视觉常量 ───
        private static readonly Brush AccentBrush =
            new SolidColorBrush(Color.FromRgb(0x37, 0x8A, 0xDD));   // #378ADD
        private static readonly Brush OutlineBrush =
            new SolidColorBrush(Color.FromArgb(0x59, 0x37, 0x8A, 0xDD)); // 35% 透明蓝
        private const double CornerSize = 8;       // L 型角标的边长
        private const double CornerThickness = 2;  // 角标线粗
        private const double MidMarkLen = 8;       // 边中点标记长度
        private const double Padding = 4;          // 外框比目标外扩多少像素
        private bool _updateScheduled;       // 是否已经排队了一次更新
        private bool _suspendUpdates;        // 拖拽时临时挂起更新
        public SelectionOverlay(Canvas hostCanvas)
        {
            _hostCanvas = hostCanvas ?? throw new ArgumentNullException(nameof(hostCanvas));

            AccentBrush.Freeze();
            OutlineBrush.Freeze();

            _root = new Canvas
            {
                IsHitTestVisible = false,   // 不挡鼠标
                Visibility = Visibility.Collapsed
            };

            _rotate = new RotateTransform(0);
            _root.RenderTransform = _rotate;
            _root.RenderTransformOrigin = new Point(0.5, 0.5);

            _outline = new Rectangle
            {
                Stroke = OutlineBrush,
                StrokeThickness = 0.5,
                Fill = Brushes.Transparent,
                RadiusX = 2,
                RadiusY = 2,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            };
            _root.Children.Add(_outline);

            _corners = new Path[4];
            for (int i = 0; i < 4; i++)
            {
                _corners[i] = new Path
                {
                    Stroke = AccentBrush,
                    StrokeThickness = CornerThickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Fill = null,
                    IsHitTestVisible = false,
                    SnapsToDevicePixels = true
                };
                _root.Children.Add(_corners[i]);
            }

            _midMarks = new Line[4];
            for (int i = 0; i < 4; i++)
            {
                _midMarks[i] = new Line
                {
                    Stroke = AccentBrush,
                    StrokeThickness = CornerThickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    IsHitTestVisible = false,
                    SnapsToDevicePixels = true
                };
                _root.Children.Add(_midMarks[i]);
            }

            // 永远在最上层
            Panel.SetZIndex(_root, 9999);
            _hostCanvas.Children.Add(_root);
        }

        /// <summary>
        /// 显示选中标记,贴合给定的目标元素。
        /// </summary>
        public void Attach(FrameworkElement target)
        {
            if (target == null) { Detach(); return; }

            _target = target;
            target.UpdateLayout();

            // 1. 先确保 _root 在 Canvas 里且置顶
            if (_hostCanvas.Children.Contains(_root))
                _hostCanvas.Children.Remove(_root);
            _hostCanvas.Children.Add(_root);
            Panel.SetZIndex(_root, 99999);

            // 2. 同步算一次位置(此时 ActualWidth 可能是 0,先算个大概)
            UpdatePosition();

            // 3. 显示
            _root.Visibility = Visibility.Visible;

            // 4. 挂上 LayoutUpdated 监听
            _hostCanvas.LayoutUpdated -= OnHostLayoutUpdated;
            _hostCanvas.LayoutUpdated += OnHostLayoutUpdated;

            // 5. 跳过一帧再算一次,等 layout 完成后位置才准
            _hostCanvas.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_target != null) UpdatePosition();
            }), System.Windows.Threading.DispatcherPriority.Loaded);

         
        }

        /// <summary>
        /// 隐藏选中标记。
        /// </summary>
        public void Detach()
        {
            _target = null;
            _root.Visibility = Visibility.Collapsed;
            _hostCanvas.LayoutUpdated -= OnHostLayoutUpdated;
        }

        /// <summary>
        /// 主动刷新一次位置(目标尺寸或位置改变后调用)。
        /// </summary>
        public void Refresh()
        {
            if (_target != null) UpdatePosition();
        }
        /// <summary>
        /// 拖拽期间挂起自动跟随,避免高频重绘
        /// </summary>
        public void SuspendUpdates()
        {
            _suspendUpdates = true;
        }

        /// <summary>
        /// 拖拽完成后恢复跟随并立即对齐一次
        /// </summary>
        public void ResumeUpdates()
        {
            _suspendUpdates = false;
            if (_target != null) UpdatePosition();
        }
        private void OnHostLayoutUpdated(object sender, EventArgs e)
        {
            if (_target == null || _suspendUpdates) return;
            if (_updateScheduled) return;   // 已经排了一次,这帧不重复

            _updateScheduled = true;
            _hostCanvas.Dispatcher.BeginInvoke(new Action(() =>
            {
                _updateScheduled = false;
                if (_target != null && !_suspendUpdates)
                    UpdatePosition();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void UpdatePosition()
        {
            try
            {
                if (_target == null) return;

                // ★ 找出真正要贴合的元素:优先贴合 Viewbox(设备本体),
                //   没有 Viewbox 就贴合 _target 自己。
                FrameworkElement bindElement = FindVisualChildOfType<Viewbox>(_target) ?? _target;

                // 计算 bindElement 在 _target 坐标系里的位置和尺寸
                double bindW = bindElement.ActualWidth > 0 ? bindElement.ActualWidth : bindElement.Width;
                double bindH = bindElement.ActualHeight > 0 ? bindElement.ActualHeight : bindElement.Height;
                if (double.IsNaN(bindW) || bindW <= 0) bindW = 80;
                if (double.IsNaN(bindH) || bindH <= 0) bindH = 60;

                // bindElement 相对于 _target 的偏移
                Point bindOffset = new Point(0, 0);
                if (bindElement != _target)
                {
                    try
                    {
                        bindOffset = bindElement.TranslatePoint(new Point(0, 0), _target);
                    }
                    catch { bindOffset = new Point(0, 0); }
                }

                double left = Canvas.GetLeft(_target);
                double top = Canvas.GetTop(_target);
                if (double.IsNaN(left)) left = 0;
                if (double.IsNaN(top)) top = 0;

                // _target 自身的总尺寸(用来定位 _root)
                double targetW = _target.ActualWidth > 0 ? _target.ActualWidth : _target.Width;
                double targetH = _target.ActualHeight > 0 ? _target.ActualHeight : _target.Height;
                if (double.IsNaN(targetW) || targetW <= 0) targetW = bindW;
                if (double.IsNaN(targetH) || targetH <= 0) targetH = bindH;

                double angle = ReadRotationAngle(_target);

                // _root 跟 _target 同位置同尺寸,旋转中心一致
                Canvas.SetLeft(_root, left);
                Canvas.SetTop(_root, top);
                _root.Width = targetW;
                _root.Height = targetH;
                _rotate.Angle = angle;
                _root.RenderTransformOrigin = new Point(0.5, 0.5);

                // ★ 选中框是基于 bindElement(Viewbox 或本体),不是整个 _target
                double bx = bindOffset.X - Padding;
                double by = bindOffset.Y - Padding;
                double bw = bindW + Padding * 2;
                double bh = bindH + Padding * 2;

                // 外框
                Canvas.SetLeft(_outline, bx);
                Canvas.SetTop(_outline, by);
                _outline.Width = bw;
                _outline.Height = bh;

                // 4 个 L 型角标
                SetCornerPath(_corners[0], bx, by, +1, +1);
                SetCornerPath(_corners[1], bx + bw, by, -1, +1);
                SetCornerPath(_corners[2], bx + bw, by + bh, -1, -1);
                SetCornerPath(_corners[3], bx, by + bh, +1, -1);

                // 4 条边中点小标记
                double midX = bx + bw / 2;
                double midY = by + bh / 2;
                SetLine(_midMarks[0], midX, by - 4, midX, by + 4);
                SetLine(_midMarks[1], bx + bw - 4, midY, bx + bw + 4, midY);
                SetLine(_midMarks[2], midX, by + bh - 4, midX, by + bh + 4);
                SetLine(_midMarks[3], bx - 4, midY, bx + 4, midY);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SelectionOverlay.UpdatePosition error: {ex.Message}");
            }
        }

        /// <summary>
        /// 在 (x,y) 画一个 L 型角标,方向由 dx, dy 决定。
        /// dx=+1 表示向右展开,dy=+1 表示向下展开。
        /// </summary>
        private void SetCornerPath(Path path, double x, double y, int dx, int dy)
        {
            // L 形:从 (x, y+dy*L) -> (x, y) -> (x+dx*L, y)
            var fig = new PathFigure
            {
                StartPoint = new Point(x, y + dy * CornerSize)
            };
            fig.Segments.Add(new LineSegment(new Point(x, y), true));
            fig.Segments.Add(new LineSegment(new Point(x + dx * CornerSize, y), true));

            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            geo.Freeze();
            path.Data = geo;
        }

        private void SetLine(Line line, double x1, double y1, double x2, double y2)
        {
            line.X1 = x1; line.Y1 = y1;
            line.X2 = x2; line.Y2 = y2;
        }
        /// <summary>
        /// 在视觉树里查找指定类型的第一个子元素
        /// </summary>
        private static T FindVisualChildOfType<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;

                var deeper = FindVisualChildOfType<T>(child);
                if (deeper != null) return deeper;
            }
            return null;
        }
        /// <summary>
        /// 从目标的 RenderTransform 里读出旋转角度(度)。
        /// 支持 RotateTransform 或 TransformGroup 里包含 RotateTransform 的情况。
        /// </summary>
        private static double ReadRotationAngle(FrameworkElement el)
        {
            if (el.RenderTransform is RotateTransform r)
                return r.Angle;

            if (el.RenderTransform is TransformGroup g)
            {
                foreach (var t in g.Children)
                {
                    if (t is RotateTransform rr) return rr.Angle;
                }
            }
            return 0;
        }
    }
}