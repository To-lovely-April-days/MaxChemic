using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MaxChemical.Modules.Designer.Service;

namespace MaxChemical.Modules.Designer.Views.Controls
{
    public partial class ElbowPipeControl : UserControl, IPipeSnapPoints
    {
        #region 事件定义

        public event EventHandler<ElbowPipeDragEventArgs> PipeDragStarted;
        public event EventHandler<ElbowPipeDragEventArgs> PipeDragging;
        public event EventHandler<ElbowPipeDragEventArgs> PipeDragCompleted;
        public event EventHandler<ElbowPipeResizeEventArgs> PipeResizeStarted;
        public event EventHandler<ElbowPipeResizeEventArgs> PipeResizing;
        public event EventHandler<ElbowPipeResizeEventArgs> PipeResizeCompleted;
        public event EventHandler<ElbowPipeRotateEventArgs> PipeRotateStarted;
        public event EventHandler<ElbowPipeRotateEventArgs> PipeRotating;
        public event EventHandler<ElbowPipeRotateEventArgs> PipeRotateCompleted;
        public event EventHandler<ElbowPipeRotateEventArgs> PipeRotated;
        public event EventHandler<ElbowPipeEventArgs> PipeDeleted;
        public event EventHandler<ElbowPipeEventArgs> ShowPipeProperties;

        // ✅ 管道选中事件
        public event EventHandler<string> PipeSelected;
        public event EventHandler<string> PipeDeselected;

        #endregion

        #region ✅ IsSelected 依赖属性

        /// <summary>
        /// 是否选中的依赖属性
        /// </summary>
        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(
                nameof(IsSelected),
                typeof(bool),
                typeof(ElbowPipeControl),
                new PropertyMetadata(false, OnIsSelectedChanged));

        /// <summary>
        /// 是否选中
        /// </summary>
        public bool IsSelected
        {
            get => (bool)GetValue(IsSelectedProperty);
            set => SetValue(IsSelectedProperty, value);
        }

        /// <summary>
        /// 选中状态变化回调
        /// </summary>
        private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ElbowPipeControl control)
            {
                control.UpdateSelectionVisual((bool)e.NewValue);
            }
        }

        #endregion

        #region 字段

        public string PipeId { get; set; }

        private bool _isDraggingPipe = false;
        private bool _isResizing = false;
        private bool _isRotating = false;
        private ElbowAnchorPosition? _resizingAnchor = null;
        private Point _dragStartPoint;
        private Point _originalPosition;
        private ElbowPipeSize _originalSize;
        private double _originalRotation = 0;
        private Point _fixedPoint;

        private const double MinArmLength = 30;
        private const double MaxArmLength = 300;
        private const double MinPipeWidth = 8;
        private const double MaxPipeWidth = 60;

        // ✅ 法兰尺寸常量（与直管道统一）
        private const double FlangeWidth = 2.54;
        private const double FlangeOffset = 0.35;
        private const double FlangeSpacing = 2.0; // 法兰与管道之间的间距

        private double _horizontalLength = 50;
        private double _verticalLength = 60;
        private double _pipeWidth = 20;
        private double _rotationAngle = 0;

        private RotateTransform _rotateTransform;
        private TransformGroup _transformGroup;
        private ScaleTransform _flipTransform;
        private DispatcherTimer _rotationLabelTimer;

        #endregion

        #region 属性

        public double HorizontalLength
        {
            get => _horizontalLength;
            set
            {
                _horizontalLength = Math.Max(MinArmLength, Math.Min(MaxArmLength, value));
                UpdatePipeGeometry();
            }
        }

        public double VerticalLength
        {
            get => _verticalLength;
            set
            {
                _verticalLength = Math.Max(MinArmLength, Math.Min(MaxArmLength, value));
                UpdatePipeGeometry();
            }
        }

        public double PipeWidth
        {
            get => _pipeWidth;
            set
            {
                _pipeWidth = Math.Max(MinPipeWidth, Math.Min(MaxPipeWidth, value));
                UpdatePipeGeometry();
            }
        }

        public double RotationAngle
        {
            get => _rotationAngle;
            set
            {
                _rotationAngle = value % 360;
                if (_rotationAngle < 0) _rotationAngle += 360;
                UpdateRotation();
            }
        }

        #endregion

        #region 构造函数

        public ElbowPipeControl()
        {
            InitializeComponent();
            PipeId = Guid.NewGuid().ToString();
            this.DataContext = this;

            _rotateTransform = new RotateTransform(0);
            _flipTransform = new ScaleTransform(1, 1);
            _transformGroup = new TransformGroup();
            _transformGroup.Children.Add(_flipTransform);
            _transformGroup.Children.Add(_rotateTransform);
            this.RenderTransform = _transformGroup;
            this.RenderTransformOrigin = new Point(0.5, 0.5);

            bool isInDesignMode = DesignerProperties.GetIsInDesignMode(this);

            if (!isInDesignMode)
            {
                _rotationLabelTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(2)
                };
                _rotationLabelTimer.Tick += (s, e) =>
                {
                    RotationLabel.Visibility = Visibility.Collapsed;
                    _rotationLabelTimer.Stop();
                };

                this.MouseEnter += OnPipeMouseEnter;
                this.MouseLeave += OnPipeMouseLeave;
                this.MouseDown += OnControlMouseDown;
                this.MouseMove += OnControlMouseMove;
                this.MouseUp += OnControlMouseUp;
                this.MouseLeave += OnControlMouseLeave;

                SetupAnchorEvents();
            }

            this.Loaded += (s, e) => UpdatePipeGeometry();
            Dispatcher.BeginInvoke(new Action(() => UpdatePipeGeometry()),
                System.Windows.Threading.DispatcherPriority.Loaded);

            // ========== ✅ 硬件加速配置（优化版） ==========

            // 1. 高质量渲染
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);

            // 2. 启用缓存提示
            RenderOptions.SetCachingHint(this, CachingHint.Cache);
            RenderOptions.SetCacheInvalidationThresholdMinimum(this, 0.5);
            RenderOptions.SetCacheInvalidationThresholdMaximum(this, 2.0);

            // 3. 配置位图缓存（关键优化）
            CacheMode = new BitmapCache
            {
                RenderAtScale = 1.0,
                SnapsToDevicePixels = true,
                EnableClearType = false
            };

            // 4. 像素对齐
            this.SnapsToDevicePixels = true;
            this.UseLayoutRounding = true;

            // ✅ 5. 弯头路径优化（禁用缓存，使用 GPU）
            ElbowPath.CacheMode = null;
            ElbowOuterEdge.CacheMode = null;
            ElbowHighlight.CacheMode = null;

            Debug.WriteLine($" 弯管 {PipeId} 硬件加速已启用");
        }

        #endregion

        #region 锚点显示控制

        private void OnPipeMouseEnter(object sender, MouseEventArgs e)
        {
            if (!_isResizing && !_isDraggingPipe && !_isRotating)
            {
                ShowAllAnchors();
            }
        }

        private void OnPipeMouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isResizing && !_isRotating && !IsSelected)
            {
                HideAllAnchors();
            }
        }

        private void ShowAllAnchors()
        {
            LeftAnchor.Opacity = 0.6;
            TopAnchor.Opacity = 0.6;
            HorizontalWidthAnchor.Opacity = 0.6;
            VerticalWidthAnchor.Opacity = 0.6;
            RotateAnchor.Opacity = 0.6;
            RotateAnchorLine.Opacity = 0.6;
        }

        private void HideAllAnchors()
        {
            LeftAnchor.Opacity = 0;
            TopAnchor.Opacity = 0;
            HorizontalWidthAnchor.Opacity = 0;
            VerticalWidthAnchor.Opacity = 0;
            RotateAnchor.Opacity = 0;
            RotateAnchorLine.Opacity = 0;
        }

        /// <summary>
        /// ✅ 更新选中视觉效果
        /// </summary>
        private void UpdateSelectionVisual(bool isSelected)
        {
            if (isSelected)
            {
                SelectionBorder.Visibility = Visibility.Visible;
                ShowAllAnchors();
            }
            else
            {
                SelectionBorder.Visibility = Visibility.Collapsed;

                if (!this.IsMouseOver && !_isResizing && !_isRotating)
                {
                    HideAllAnchors();
                }
            }
        }

        #endregion

        #region 右键菜单事件处理

        private void RotateClockwise15_Click(object sender, RoutedEventArgs e)
        {
            RotatePipe(15);
        }

        private void RotateClockwise30_Click(object sender, RoutedEventArgs e)
        {
            RotatePipe(30);
        }

        private void RotateClockwise45_Click(object sender, RoutedEventArgs e)
        {
            RotatePipe(45);
        }

        private void RotateClockwise90_Click(object sender, RoutedEventArgs e)
        {
            RotatePipe(90);
        }

        private void Rotate180_Click(object sender, RoutedEventArgs e)
        {
            RotatePipe(180);
        }

        private void RotateCounterClockwise15_Click(object sender, RoutedEventArgs e)
        {
            RotatePipe(-15);
        }

        private void RotateCounterClockwise30_Click(object sender, RoutedEventArgs e)
        {
            RotatePipe(-30);
        }

        private void RotateCounterClockwise45_Click(object sender, RoutedEventArgs e)
        {
            RotatePipe(-45);
        }

        private void RotateCounterClockwise90_Click(object sender, RoutedEventArgs e)
        {
            RotatePipe(-90);
        }

        private void ResetRotation_Click(object sender, RoutedEventArgs e)
        {
            double oldAngle = _rotationAngle;
            RotationAngle = 0;

            PipeRotated?.Invoke(this, new ElbowPipeRotateEventArgs
            {
                PipeId = PipeId,
                OriginalAngle = oldAngle,
                CurrentAngle = _rotationAngle
            });

            ShowRotationLabel();
            Debug.WriteLine($"弯管旋转已重置: {PipeId}");
        }

        private void FlipHorizontal_Click(object sender, RoutedEventArgs e)
        {
            _flipTransform.ScaleX *= -1;
            Debug.WriteLine($"弯管水平翻转: ScaleX={_flipTransform.ScaleX}");
        }

        private void FlipVertical_Click(object sender, RoutedEventArgs e)
        {
            _flipTransform.ScaleY *= -1;
            Debug.WriteLine($"弯管垂直翻转: ScaleY={_flipTransform.ScaleY}");
        }

        private void ShowProperties_Click(object sender, RoutedEventArgs e)
        {
            ShowPipeProperties?.Invoke(this, new ElbowPipeEventArgs { PipeId = PipeId });
            Debug.WriteLine($"显示弯管属性: {PipeId}");
        }

        private void DeletePipe_Click(object sender, RoutedEventArgs e)
        {
            PipeDeleted?.Invoke(this, new ElbowPipeEventArgs { PipeId = PipeId });
            Debug.WriteLine($"删除弯管: {PipeId}");
        }

        private void RotatePipe(double deltaAngle)
        {
            double oldAngle = _rotationAngle;
            RotationAngle = _rotationAngle + deltaAngle;

            PipeRotated?.Invoke(this, new ElbowPipeRotateEventArgs
            {
                PipeId = PipeId,
                OriginalAngle = oldAngle,
                CurrentAngle = _rotationAngle
            });

            ShowRotationLabel();
            Debug.WriteLine($"弯管旋转: {deltaAngle:+#;-#;0}° → 当前角度: {_rotationAngle:F0}°");
        }

        private void ShowRotationLabel()
        {
            RotationLabel.Visibility = Visibility.Visible;
            UpdateRotationLabelPosition();
            _rotationLabelTimer.Stop();
            _rotationLabelTimer.Start();
        }

        private void UpdateRotationLabelPosition()
        {
            double centerX = this.Width / 2;
            Canvas.SetLeft(RotationLabel, centerX - 20);
            Canvas.SetTop(RotationLabel, -50);
        }

        #endregion

        #region 锚点事件设置

        private void SetupAnchorEvents()
        {
            LeftAnchor.MouseEnter += OnAnchorMouseEnter;
            LeftAnchor.MouseLeave += OnAnchorMouseLeave;
            LeftAnchor.MouseDown += (s, e) => OnAnchorMouseDown(ElbowAnchorPosition.Left, e);

            TopAnchor.MouseEnter += OnAnchorMouseEnter;
            TopAnchor.MouseLeave += OnAnchorMouseLeave;
            TopAnchor.MouseDown += (s, e) => OnAnchorMouseDown(ElbowAnchorPosition.Top, e);

            HorizontalWidthAnchor.MouseEnter += OnAnchorMouseEnter;
            HorizontalWidthAnchor.MouseLeave += OnAnchorMouseLeave;
            HorizontalWidthAnchor.MouseDown += (s, e) => OnAnchorMouseDown(ElbowAnchorPosition.HorizontalWidth, e);

            VerticalWidthAnchor.MouseEnter += OnAnchorMouseEnter;
            VerticalWidthAnchor.MouseLeave += OnAnchorMouseLeave;
            VerticalWidthAnchor.MouseDown += (s, e) => OnAnchorMouseDown(ElbowAnchorPosition.VerticalWidth, e);

            RotateAnchor.MouseEnter += OnRotateAnchorMouseEnter;
            RotateAnchor.MouseLeave += OnRotateAnchorMouseLeave;
            RotateAnchor.MouseDown += OnRotateAnchorMouseDown;
        }

        private void OnAnchorMouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Ellipse anchor) anchor.Opacity = 1.0;
        }

        private void OnAnchorMouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Ellipse anchor && !_isResizing) anchor.Opacity = 0.6;
        }

        private void OnRotateAnchorMouseEnter(object sender, MouseEventArgs e)
        {
            RotateAnchor.Opacity = 1.0;
            RotateAnchorLine.Opacity = 1.0;
        }

        private void OnRotateAnchorMouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isRotating)
            {
                RotateAnchor.Opacity = 0.6;
                RotateAnchorLine.Opacity = 0.6;
            }
        }

        #endregion

        #region 管道几何更新

        private void UpdatePipeGeometry()
        {
            // ✅ 开始批量更新（减少重绘次数）
            PipeCanvas.BeginInit();

            this.Width = _horizontalLength + 20;
            this.Height = _verticalLength + 20;

            double elbowRadius = _pipeWidth;
            double horizontalCenterY = this.Height - 10 - _pipeWidth / 2;
            double verticalCenterX = this.Width - 10 - _pipeWidth / 2;
            double elbowCenterX = verticalCenterX;
            double elbowCenterY = horizontalCenterY;

            UpdateHorizontalPipe(horizontalCenterY, elbowCenterX, elbowRadius);
            UpdateVerticalPipe(verticalCenterX, elbowCenterY, elbowRadius);
            UpdateElbow(elbowCenterX, elbowCenterY, elbowRadius);
            UpdateFlanges(horizontalCenterY, verticalCenterX);
            UpdateConnections(horizontalCenterY, verticalCenterX);

            SelectionBorder.Width = this.Width;
            SelectionBorder.Height = this.Height;

            UpdateAnchorPositions(horizontalCenterY, verticalCenterX);
            UpdateRotationLabelPosition();

            // ✅ 结束批量更新
            PipeCanvas.EndInit();
        }

        private void UpdateHorizontalPipe(double centerY, double elbowCenterX, double elbowRadius)
        {
            // ✅ 修改：pipeLeft = 法兰宽度2.54 + 间距2 + 偏移0.35 = 4.89，简化为3.2（与XAML一致）
            double pipeLeft = FlangeWidth + FlangeSpacing + FlangeOffset;
            double pipeWidth = Math.Max(0, elbowCenterX - elbowRadius - pipeLeft);
            double pipeTop = centerY - _pipeWidth / 2;

            HorizontalPipe.Width = pipeWidth;
            HorizontalPipe.Height = _pipeWidth;
            Canvas.SetLeft(HorizontalPipe, pipeLeft);
            Canvas.SetTop(HorizontalPipe, pipeTop);

            // ✅ 边缘线位置微调
            double edgeOffset = 0.38; // (3.58 - 3.2) = 0.38
            HorizontalTopEdge.X1 = pipeLeft + edgeOffset;
            HorizontalTopEdge.X2 = pipeLeft + pipeWidth - edgeOffset;
            HorizontalTopEdge.Y1 = pipeTop + 0.32;
            HorizontalTopEdge.Y2 = pipeTop + 0.32;

            HorizontalBottomEdge.X1 = pipeLeft + edgeOffset;
            HorizontalBottomEdge.X2 = pipeLeft + pipeWidth - edgeOffset;
            HorizontalBottomEdge.Y1 = pipeTop + _pipeWidth - 0.32;
            HorizontalBottomEdge.Y2 = pipeTop + _pipeWidth - 0.32;
        }

        private void UpdateVerticalPipe(double centerX, double elbowCenterY, double elbowRadius)
        {
            // ✅ 修改：pipeTop = 法兰高度2.54 + 间距2 + 偏移0.35 = 4.89，简化为3.2（与XAML一致）
            double pipeTop = FlangeWidth + FlangeSpacing + FlangeOffset;
            double pipeHeight = Math.Max(0, elbowCenterY - elbowRadius - pipeTop);
            double pipeLeft = centerX - _pipeWidth / 2;

            VerticalPipe.Width = _pipeWidth;
            VerticalPipe.Height = pipeHeight;
            Canvas.SetLeft(VerticalPipe, pipeLeft);
            Canvas.SetTop(VerticalPipe, pipeTop);

            // ✅ 边缘线位置微调
            double edgeOffset = 0.38;
            VerticalLeftEdge.X1 = pipeLeft + 0.32;
            VerticalLeftEdge.X2 = pipeLeft + 0.32;
            VerticalLeftEdge.Y1 = pipeTop + edgeOffset;
            VerticalLeftEdge.Y2 = pipeTop + pipeHeight - edgeOffset;

            VerticalRightEdge.X1 = pipeLeft + _pipeWidth - 0.32;
            VerticalRightEdge.X2 = pipeLeft + _pipeWidth - 0.32;
            VerticalRightEdge.Y1 = pipeTop + edgeOffset;
            VerticalRightEdge.Y2 = pipeTop + pipeHeight - edgeOffset;
        }

        private void UpdateElbow(double centerX, double centerY, double radius)
        {
            // ========== 🔧 修正：弯头路径应该严格在管道内部 ==========

            PathGeometry elbowGeometry = new PathGeometry();
            PathFigure figure = new PathFigure();

            // ✅ 外弧起点：弯头左侧底部（水平管道右端底部）
            Point outerStart = new Point(centerX - radius, centerY + _pipeWidth / 2);
            figure.StartPoint = outerStart;

            // ✅ 外弧：从左下到右上（逆时针）
            // 外弧半径 = 弯头中心半径 + 管道宽度一半
            ArcSegment outerArc = new ArcSegment(
                new Point(centerX + _pipeWidth / 2, centerY - radius),
                new Size(radius + _pipeWidth / 2, radius + _pipeWidth / 2),
                0, false, SweepDirection.Counterclockwise, true);
            figure.Segments.Add(outerArc);

            // ✅ 顶部横线：从外弧终点到内弧起点
            LineSegment toInner = new LineSegment(
                new Point(centerX - _pipeWidth / 2, centerY - radius), true);
            figure.Segments.Add(toInner);

            // ✅ 内弧：从右上到左下（顺时针）
            // 内弧半径 = 弯头中心半径 - 管道宽度一半
            ArcSegment innerArc = new ArcSegment(
                new Point(centerX - radius, centerY - _pipeWidth / 2),
                new Size(radius - _pipeWidth / 2, radius - _pipeWidth / 2),
                0, false, SweepDirection.Clockwise, true);
            figure.Segments.Add(innerArc);

            // ✅ 封闭路径
            figure.IsClosed = true;
            elbowGeometry.Figures.Add(figure);
            ElbowPath.Data = elbowGeometry;

            // ========== 弯头外侧强化边缘 ==========
            if (ElbowOuterEdge != null)
            {
                PathGeometry outerEdgeGeometry = new PathGeometry();
                PathFigure outerEdgeFigure = new PathFigure();

                // ✅ 外侧边缘略微偏移（视觉强化）
                double edgeOffset = 0.5;
                Point edgeStart = new Point(centerX - radius, centerY + _pipeWidth / 2 - edgeOffset);
                outerEdgeFigure.StartPoint = edgeStart;

                ArcSegment edgeArc = new ArcSegment(
                    new Point(centerX + _pipeWidth / 2 - edgeOffset, centerY - radius),
                    new Size(radius + _pipeWidth / 2 - edgeOffset, radius + _pipeWidth / 2 - edgeOffset),
                    0, false, SweepDirection.Counterclockwise, true);
                outerEdgeFigure.Segments.Add(edgeArc);

                outerEdgeGeometry.Figures.Add(outerEdgeFigure);
                ElbowOuterEdge.Data = outerEdgeGeometry;
            }

            // ========== 弯头高光路径 ==========
            if (ElbowHighlight != null)
            {
                PathGeometry highlightGeometry = new PathGeometry();
                PathFigure highlightFigure = new PathFigure();

                // ✅ 高光位置：从内弧偏移一定距离
                double highlightOffset = _pipeWidth * 0.25;
                double highlightRadius = radius - highlightOffset;

                Point highlightStart = new Point(
                    centerX - highlightRadius,
                    centerY - highlightOffset);
                highlightFigure.StartPoint = highlightStart;

                ArcSegment highlightArc = new ArcSegment(
                    new Point(centerX - highlightOffset, centerY - highlightRadius),
                    new Size(highlightRadius, highlightRadius),
                    0, false, SweepDirection.Counterclockwise, true);
                highlightFigure.Segments.Add(highlightArc);

                highlightGeometry.Figures.Add(highlightFigure);
                ElbowHighlight.Data = highlightGeometry;
            }
        }

        private void UpdateFlanges(double horizontalY, double verticalX)
        {
            // ✅ 法兰高度/宽度 = 管道宽度 + 4（上下/左右各多出2px）
            double flangeHeight = _pipeWidth + 4;
            double flangeWidth = _pipeWidth + 4;

            // 左侧法兰（垂直方向）
            Canvas.SetLeft(LeftFlangeGroup, FlangeOffset);
            Canvas.SetTop(LeftFlangeGroup, horizontalY - flangeHeight / 2);
            LeftFlangeGroup.Width = FlangeWidth;
            LeftFlangeGroup.Height = flangeHeight;

            LeftFlangeBase.Width = FlangeWidth;
            LeftFlangeBase.Height = flangeHeight;
            LeftFlangeGradient.Width = FlangeWidth;
            LeftFlangeGradient.Height = flangeHeight;
            LeftFlangeBorder.Width = FlangeWidth;
            LeftFlangeBorder.Height = flangeHeight;

            // ✅ 移除螺栓更新
            // UpdateFlangeBoltsVertical(LeftBolt1, LeftBolt2, LeftBolt3, flangeHeight);

            // 顶部法兰（水平方向）
            Canvas.SetLeft(TopFlangeGroup, verticalX - flangeWidth / 2);
            Canvas.SetTop(TopFlangeGroup, FlangeOffset);
            TopFlangeGroup.Width = flangeWidth;
            TopFlangeGroup.Height = FlangeWidth;

            TopFlangeBase.Width = flangeWidth;
            TopFlangeBase.Height = FlangeWidth;
            TopFlangeGradient.Width = flangeWidth;
            TopFlangeGradient.Height = FlangeWidth;
            TopFlangeBorder.Width = flangeWidth;
            TopFlangeBorder.Height = FlangeWidth;

            // ✅ 移除螺栓更新
            // UpdateFlangeBoltsHorizontal(TopBolt1, TopBolt2, TopBolt3, flangeWidth);
        }

        private void UpdateFlangeBoltsVertical(Ellipse bolt1, Ellipse bolt2, Ellipse bolt3, double flangeHeight)
        {
            double boltSize = 1.5;
            double leftPosition = (FlangeWidth - boltSize) / 2; // 居中

            // 螺栓垂直分布
            Canvas.SetLeft(bolt1, leftPosition);
            Canvas.SetTop(bolt1, flangeHeight * 0.2 - boltSize / 2);

            Canvas.SetLeft(bolt2, leftPosition);
            Canvas.SetTop(bolt2, (flangeHeight - boltSize) / 2);

            Canvas.SetLeft(bolt3, leftPosition);
            Canvas.SetTop(bolt3, flangeHeight * 0.8 - boltSize / 2);
        }

        private void UpdateFlangeBoltsHorizontal(Ellipse bolt1, Ellipse bolt2, Ellipse bolt3, double flangeWidth)
        {
            double boltSize = 1.5;
            double topPosition = (FlangeWidth - boltSize) / 2; // 居中

            // 螺栓水平分布
            Canvas.SetLeft(bolt1, flangeWidth * 0.2 - boltSize / 2);
            Canvas.SetTop(bolt1, topPosition);

            Canvas.SetLeft(bolt2, (flangeWidth - boltSize) / 2);
            Canvas.SetTop(bolt2, topPosition);

            Canvas.SetLeft(bolt3, flangeWidth * 0.8 - boltSize / 2);
            Canvas.SetTop(bolt3, topPosition);
        }

        private void UpdateConnections(double horizontalY, double verticalX)
        {
            LeftConnection.Height = _pipeWidth;
            // ✅ 左连接位置 = 法兰宽度2.54 + 偏移0.35 = 2.89
            Canvas.SetLeft(LeftConnection, FlangeWidth + FlangeOffset);
            Canvas.SetTop(LeftConnection, horizontalY - _pipeWidth / 2);

            TopConnection.Width = _pipeWidth;
            Canvas.SetLeft(TopConnection, verticalX - _pipeWidth / 2);
            // ✅ 顶连接位置 = 法兰高度2.54 + 偏移0.35 = 2.89
            Canvas.SetTop(TopConnection, FlangeWidth + FlangeOffset);
        }

        private void UpdateAnchorPositions(double horizontalY, double verticalX)
        {
            Canvas.SetLeft(LeftAnchor, -6);
            Canvas.SetTop(LeftAnchor, horizontalY - 6);

            Canvas.SetLeft(TopAnchor, verticalX - 6);
            Canvas.SetTop(TopAnchor, -6);

            Canvas.SetLeft(HorizontalWidthAnchor, _horizontalLength / 2);
            Canvas.SetTop(HorizontalWidthAnchor, this.Height - 6);

            Canvas.SetLeft(VerticalWidthAnchor, this.Width - 6);
            Canvas.SetTop(VerticalWidthAnchor, _verticalLength / 2);

            double centerX = this.Width / 2;
            Canvas.SetLeft(RotateAnchor, centerX - 6);
            Canvas.SetTop(RotateAnchor, -30);

            RotateAnchorLine.X1 = centerX;
            RotateAnchorLine.Y1 = 0;
            RotateAnchorLine.X2 = centerX;
            RotateAnchorLine.Y2 = -24;
        }

        private void UpdateRotation()
        {
            _rotateTransform.Angle = _rotationAngle;
            RotationText.Text = $"{_rotationAngle:F0}°";
        }

        #endregion

        #region 拖动、调整大小、旋转功能

        private void OnControlMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (IsClickOnAnchor(e.OriginalSource)) return;

            if (e.LeftButton == MouseButtonState.Pressed && !_isResizing && !_isRotating)
            {
                PipeSelected?.Invoke(this, this.PipeId);
                this.IsSelected = true;

                _isDraggingPipe = true;
                var parent = this.Parent as Canvas;
                if (parent != null)
                {
                    _dragStartPoint = e.GetPosition(parent);
                    _originalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
                    if (double.IsNaN(_originalPosition.X)) _originalPosition.X = 0;
                    if (double.IsNaN(_originalPosition.Y)) _originalPosition.Y = 0;
                }

                // ✅ 拖拽时禁用缓存，降低质量
                this.CacheMode = null;
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);

                this.CaptureMouse();
                this.Opacity = 0.7;

                PipeDragStarted?.Invoke(this, new ElbowPipeDragEventArgs
                {
                    PipeId = PipeId,
                    StartPosition = _originalPosition
                });
                e.Handled = true;
            }
        }

        private void OnControlMouseMove(object sender, MouseEventArgs e)
        {
            if (_isDraggingPipe && e.LeftButton == MouseButtonState.Pressed)
            {
                var parent = this.Parent as Canvas;
                if (parent != null)
                {
                    var currentPoint = e.GetPosition(parent);
                    double deltaX = currentPoint.X - _dragStartPoint.X;
                    double deltaY = currentPoint.Y - _dragStartPoint.Y;
                    double newLeft = Math.Max(0, Math.Min(parent.ActualWidth - this.ActualWidth, _originalPosition.X + deltaX));
                    double newTop = Math.Max(0, Math.Min(parent.ActualHeight - this.ActualHeight, _originalPosition.Y + deltaY));
                    Canvas.SetLeft(this, newLeft);
                    Canvas.SetTop(this, newTop);
                    PipeDragging?.Invoke(this, new ElbowPipeDragEventArgs { PipeId = PipeId, CurrentPosition = new Point(newLeft, newTop) });
                }
                e.Handled = true;
            }
            else if (_isResizing && _resizingAnchor.HasValue)
            {
                HandleResize(e);
                e.Handled = true;
            }
            else if (_isRotating)
            {
                HandleRotate(e);
                e.Handled = true;
            }
        }

        private void OnControlMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDraggingPipe) { CompletePipeDrag(); e.Handled = true; }
            else if (_isResizing) { CompleteResize(); e.Handled = true; }
            else if (_isRotating) { CompleteRotate(); e.Handled = true; }
        }

        private void OnControlMouseLeave(object sender, MouseEventArgs e)
        {
            if (_isDraggingPipe) CompletePipeDrag();
            else if (_isResizing) CompleteResize();
            else if (_isRotating) CompleteRotate();
        }

        private void CompletePipeDrag()
        {
            if (!_isDraggingPipe) return;

            var finalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));

            PipeDragCompleted?.Invoke(this, new ElbowPipeDragEventArgs
            {
                PipeId = PipeId,
                StartPosition = _originalPosition,
                CurrentPosition = finalPosition
            });

            this.Opacity = 1.0;
            _isDraggingPipe = false;

            // ✅ 拖拽结束后恢复缓存和高质量
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            this.CacheMode = new BitmapCache
            {
                RenderAtScale = 1.0,
                SnapsToDevicePixels = true,
                EnableClearType = false
            };

            this.ReleaseMouseCapture();
        }


        private bool IsClickOnAnchor(object source)
        {
            return source == LeftAnchor || source == TopAnchor ||
                   source == HorizontalWidthAnchor || source == VerticalWidthAnchor ||
                   source == RotateAnchor;
        }

        #region 锚点调整大小

        private void OnAnchorMouseDown(ElbowAnchorPosition position, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _isResizing = true;
                _resizingAnchor = position;

                var parent = this.Parent as Canvas;
                if (parent != null)
                {
                    _dragStartPoint = e.GetPosition(parent);
                    _originalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
                    _originalSize = new ElbowPipeSize
                    {
                        HorizontalLength = _horizontalLength,
                        VerticalLength = _verticalLength,
                        PipeWidth = _pipeWidth
                    };
                    if (double.IsNaN(_originalPosition.X)) _originalPosition.X = 0;
                    if (double.IsNaN(_originalPosition.Y)) _originalPosition.Y = 0;

                    _fixedPoint = CalculateElbowFixedPoint(position);

                    Debug.WriteLine($"开始调整弯管大小: 锚点={position}, 固定点=({_fixedPoint.X:F1}, {_fixedPoint.Y:F1})");
                }

                // ✅ 调整大小时禁用缓存
                this.CacheMode = null;
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);

                this.CaptureMouse();

                PipeResizeStarted?.Invoke(this, new ElbowPipeResizeEventArgs
                {
                    PipeId = PipeId,
                    AnchorPosition = position,
                    OriginalSize = _originalSize
                });
                e.Handled = true;
            }
        }

        private Point CalculateElbowFixedPoint(ElbowAnchorPosition draggedAnchor)
        {
            var parent = this.Parent as Canvas;
            if (parent == null) return new Point();

            var pipeLeft = Canvas.GetLeft(this);
            var pipeTop = Canvas.GetTop(this);
            if (double.IsNaN(pipeLeft)) pipeLeft = 0;
            if (double.IsNaN(pipeTop)) pipeTop = 0;

            Point center = new Point(
                pipeLeft + this.ActualWidth / 2,
                pipeTop + this.ActualHeight / 2
            );

            double radians = _rotationAngle * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);

            Point localFixed = new Point();

            switch (draggedAnchor)
            {
                case ElbowAnchorPosition.Left:
                    localFixed = new Point(
                        this.Width / 2 - 10 - _pipeWidth / 2,
                        this.Height / 2 - 10 - _pipeWidth / 2
                    );
                    break;

                case ElbowAnchorPosition.Top:
                    localFixed = new Point(
                        this.Width / 2 - 10 - _pipeWidth / 2,
                        this.Height / 2 - 10 - _pipeWidth / 2
                    );
                    break;

                case ElbowAnchorPosition.HorizontalWidth:
                case ElbowAnchorPosition.VerticalWidth:
                    localFixed = new Point(0, 0);
                    break;
            }

            Point worldFixed = new Point(
                localFixed.X * cos - localFixed.Y * sin,
                localFixed.X * sin + localFixed.Y * cos
            );

            return new Point(
                center.X + worldFixed.X,
                center.Y + worldFixed.Y
            );
        }

        private void HandleResize(MouseEventArgs e)
        {
            if (!_resizingAnchor.HasValue) return;
            var parent = this.Parent as Canvas;
            if (parent == null) return;

            var currentPoint = e.GetPosition(parent);

            switch (_resizingAnchor.Value)
            {
                case ElbowAnchorPosition.Left:
                    ResizeHorizontalArmFromLeft(currentPoint);
                    break;

                case ElbowAnchorPosition.Top:
                    ResizeVerticalArmFromTop(currentPoint);
                    break;

                case ElbowAnchorPosition.HorizontalWidth:
                case ElbowAnchorPosition.VerticalWidth:
                    ResizePipeWidthFromAnchor(currentPoint);
                    break;
            }

            PipeResizing?.Invoke(this, new ElbowPipeResizeEventArgs
            {
                PipeId = PipeId,
                AnchorPosition = _resizingAnchor.Value,
                CurrentSize = new ElbowPipeSize
                {
                    HorizontalLength = _horizontalLength,
                    VerticalLength = _verticalLength,
                    PipeWidth = _pipeWidth
                }
            });
        }

        private void ResizeHorizontalArmFromLeft(Point currentMousePosition)
        {
            double deltaX = currentMousePosition.X - _fixedPoint.X;
            double deltaY = currentMousePosition.Y - _fixedPoint.Y;

            double radians = _rotationAngle * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);

            double projectedLength = deltaX * cos + deltaY * sin;

            double newLength = Math.Abs(projectedLength);
            newLength = Math.Max(MinArmLength, Math.Min(MaxArmLength, newLength));

            _horizontalLength = newLength;

            RecalculatePositionForElbow();

            UpdatePipeGeometry();

            Debug.WriteLine($"调整水平臂: {newLength:F1}px");
        }

        private void ResizeVerticalArmFromTop(Point currentMousePosition)
        {
            double deltaX = currentMousePosition.X - _fixedPoint.X;
            double deltaY = currentMousePosition.Y - _fixedPoint.Y;

            double radians = _rotationAngle * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);

            double projectedLength = -deltaX * sin + deltaY * cos;

            double newLength = Math.Abs(projectedLength);
            newLength = Math.Max(MinArmLength, Math.Min(MaxArmLength, newLength));

            _verticalLength = newLength;

            RecalculatePositionForElbow();

            UpdatePipeGeometry();

            Debug.WriteLine($"调整垂直臂: {newLength:F1}px");
        }

        private void ResizePipeWidthFromAnchor(Point currentMousePosition)
        {
            double deltaX = currentMousePosition.X - _dragStartPoint.X;
            double deltaY = currentMousePosition.Y - _dragStartPoint.Y;

            double delta = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
            if (deltaX < 0 || deltaY < 0) delta = -delta;

            double newWidth = _originalSize.PipeWidth + delta * 0.5;
            newWidth = Math.Max(MinPipeWidth, Math.Min(MaxPipeWidth, newWidth));

            double widthRatio = newWidth / _originalSize.PipeWidth;

            double newHorizontalLength = _originalSize.HorizontalLength * widthRatio;
            double newVerticalLength = _originalSize.VerticalLength * widthRatio;

            newHorizontalLength = Math.Max(MinArmLength, Math.Min(MaxArmLength, newHorizontalLength));
            newVerticalLength = Math.Max(MinArmLength, Math.Min(MaxArmLength, newVerticalLength));

            _pipeWidth = newWidth;
            _horizontalLength = newHorizontalLength;
            _verticalLength = newVerticalLength;

            UpdatePipeGeometry();

            Debug.WriteLine($"协调调整 - 宽度: {newWidth:F1}px, 水平: {newHorizontalLength:F1}px, 垂直: {newVerticalLength:F1}px, 比例: {widthRatio:F2}");
        }

        private void RecalculatePositionForElbow()
        {
            double radians = _rotationAngle * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);

            Point elbowLocalOffset = new Point(
                this.Width / 2 - 10 - _pipeWidth / 2,
                this.Height / 2 - 10 - _pipeWidth / 2
            );

            Point elbowWorldOffset = new Point(
                elbowLocalOffset.X * cos - elbowLocalOffset.Y * sin,
                elbowLocalOffset.X * sin + elbowLocalOffset.Y * cos
            );

            Point newCenter = new Point(
                _fixedPoint.X - elbowWorldOffset.X,
                _fixedPoint.Y - elbowWorldOffset.Y
            );

            this.Width = _horizontalLength + 20;
            this.Height = _verticalLength + 20;

            Canvas.SetLeft(this, newCenter.X - this.Width / 2);
            Canvas.SetTop(this, newCenter.Y - this.Height / 2);
        }

        private void CompleteResize()
        {
            if (!_isResizing) return;

            PipeResizeCompleted?.Invoke(this, new ElbowPipeResizeEventArgs
            {
                PipeId = PipeId,
                AnchorPosition = _resizingAnchor.Value,
                OriginalSize = _originalSize,
                CurrentSize = new ElbowPipeSize
                {
                    HorizontalLength = _horizontalLength,
                    VerticalLength = _verticalLength,
                    PipeWidth = _pipeWidth
                }
            });

            _isResizing = false;
            _resizingAnchor = null;

            // ✅ 调整大小结束后恢复缓存
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            this.CacheMode = new BitmapCache
            {
                RenderAtScale = 1.0,
                SnapsToDevicePixels = true,
                EnableClearType = false
            };

            this.ReleaseMouseCapture();
        }


        #endregion

        private void OnRotateAnchorMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _isRotating = true;
                _originalRotation = _rotationAngle;

                var parent = this.Parent as Canvas;
                if (parent != null)
                    _dragStartPoint = e.GetPosition(parent);

                // ✅ 旋转时禁用缓存（关键优化）
                this.CacheMode = null;
                RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.LowQuality);

                this.CaptureMouse();

                PipeRotateStarted?.Invoke(this, new ElbowPipeRotateEventArgs
                {
                    PipeId = PipeId,
                    OriginalAngle = _originalRotation
                });
                e.Handled = true;
            }
        }

        private void HandleRotate(MouseEventArgs e)
        {
            var parent = this.Parent as Canvas;
            if (parent == null) return;
            var currentPoint = e.GetPosition(parent);
            var pipeLeft = Canvas.GetLeft(this);
            var pipeTop = Canvas.GetTop(this);
            if (double.IsNaN(pipeLeft)) pipeLeft = 0;
            if (double.IsNaN(pipeTop)) pipeTop = 0;

            Point center = new Point(pipeLeft + this.ActualWidth / 2, pipeTop + this.ActualHeight / 2);
            double startAngle = Math.Atan2(_dragStartPoint.Y - center.Y, _dragStartPoint.X - center.X) * 180 / Math.PI;
            double currentAngle = Math.Atan2(currentPoint.Y - center.Y, currentPoint.X - center.X) * 180 / Math.PI;
            double deltaAngle = currentAngle - startAngle;
            double newAngle = _originalRotation + deltaAngle;

            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
                newAngle = Math.Round(newAngle / 15) * 15;

            RotationAngle = newAngle;
            ShowRotationLabel();
            PipeRotating?.Invoke(this, new ElbowPipeRotateEventArgs { PipeId = PipeId, CurrentAngle = _rotationAngle });
        }

        private void CompleteRotate()
        {
            if (!_isRotating) return;

            PipeRotateCompleted?.Invoke(this, new ElbowPipeRotateEventArgs
            {
                PipeId = PipeId,
                OriginalAngle = _originalRotation,
                CurrentAngle = _rotationAngle
            });

            _isRotating = false;

            // ✅ 旋转结束后恢复缓存（关键优化）
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            this.CacheMode = new BitmapCache
            {
                RenderAtScale = 1.0,
                SnapsToDevicePixels = true,
                EnableClearType = false
            };

            this.ReleaseMouseCapture();
        }

        #endregion

        public void SetSelected(bool selected)
        {
            IsSelected = selected;
        }

        #region IPipeSnapPoints 实现

        public List<PipeSnapPoint> GetSnapPoints()
        {
            var snapPoints = new List<PipeSnapPoint>();

            var parent = this.Parent as Canvas;
            if (parent == null) return snapPoints;

            var pipeLeft = Canvas.GetLeft(this);
            var pipeTop = Canvas.GetTop(this);
            if (double.IsNaN(pipeLeft)) pipeLeft = 0;
            if (double.IsNaN(pipeTop)) pipeTop = 0;

            Point center = new Point(
                pipeLeft + this.ActualWidth / 2,
                pipeTop + this.ActualHeight / 2
            );

            double radians = _rotationAngle * Math.PI / 180.0;

            // ✅ 左侧法兰中心位置：Canvas.Left=0.35, 法兰宽度=2.54, 中心X=0.35+1.27=1.62
            double leftFlangeX = FlangeOffset + FlangeWidth / 2 - this.ActualWidth / 2;
            double leftFlangeY = Canvas.GetTop(LeftFlangeGroup) + LeftFlangeGroup.Height / 2 - this.ActualHeight / 2;

            Point leftFlangeLocal = new Point(leftFlangeX, leftFlangeY);
            Point leftFlangeWorld = RotatePoint(leftFlangeLocal, radians);

            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(
                    center.X + leftFlangeWorld.X,
                    center.Y + leftFlangeWorld.Y
                ),
                Direction = GetSnapDirectionFromAngle(radians + Math.PI),
                Description = $"弯管水平法兰 (水平:{_horizontalLength:F0})"
            });

            // ✅ 顶部法兰中心位置：Canvas.Top=0.35, 法兰高度=2.54, 中心Y=0.35+1.27=1.62
            double topFlangeX = Canvas.GetLeft(TopFlangeGroup) + TopFlangeGroup.Width / 2 - this.ActualWidth / 2;
            double topFlangeY = FlangeOffset + FlangeWidth / 2 - this.ActualHeight / 2;

            Point topFlangeLocal = new Point(topFlangeX, topFlangeY);
            Point topFlangeWorld = RotatePoint(topFlangeLocal, radians);

            snapPoints.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(
                    center.X + topFlangeWorld.X,
                    center.Y + topFlangeWorld.Y
                ),
                Direction = GetSnapDirectionFromAngle(radians - Math.PI / 2),
                Description = $"弯管垂直法兰 (垂直:{_verticalLength:F0})"
            });

            return snapPoints;
        }

        private Point RotatePoint(Point localPoint, double radians)
        {
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);

            return new Point(
                localPoint.X * cos - localPoint.Y * sin,
                localPoint.X * sin + localPoint.Y * cos
            );
        }

        private SnapDirection GetSnapDirectionFromAngle(double radians)
        {
            double degrees = (radians * 180 / Math.PI + 360) % 360;

            if (degrees >= 315 || degrees < 45) return SnapDirection.Right;
            if (degrees >= 45 && degrees < 135) return SnapDirection.Down;
            if (degrees >= 135 && degrees < 225) return SnapDirection.Left;
            return SnapDirection.Up;
        }

        #endregion
    }

    #region 事件参数类

    public class ElbowPipeDragEventArgs : EventArgs
    {
        public string PipeId { get; set; }
        public Point StartPosition { get; set; }
        public Point CurrentPosition { get; set; }
    }

    public class ElbowPipeResizeEventArgs : EventArgs
    {
        public string PipeId { get; set; }
        public ElbowAnchorPosition AnchorPosition { get; set; }
        public ElbowPipeSize OriginalSize { get; set; }
        public ElbowPipeSize CurrentSize { get; set; }
    }

    public class ElbowPipeRotateEventArgs : EventArgs
    {
        public string PipeId { get; set; }
        public double OriginalAngle { get; set; }
        public double CurrentAngle { get; set; }
    }

    public class ElbowPipeEventArgs : EventArgs
    {
        public string PipeId { get; set; }
    }

    public struct ElbowPipeSize
    {
        public double HorizontalLength { get; set; }
        public double VerticalLength { get; set; }
        public double PipeWidth { get; set; }
    }

    public enum ElbowAnchorPosition
    {
        Left,
        Top,
        HorizontalWidth,
        VerticalWidth
    }

    #endregion
}