// ElbowPipeControl.xaml.cs - 完整修复版

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MaxChemical.Modules.Designer.Views.Controls
{
    public partial class ElbowPipeControl : UserControl
    {
        // 事件定义
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

        public string PipeId { get; set; }
        public bool IsSelected { get; set; }

        private bool _isDraggingPipe = false;
        private bool _isResizing = false;
        private bool _isRotating = false;
        private ElbowAnchorPosition? _resizingAnchor = null;
        private Point _dragStartPoint;
        private Point _originalPosition;
        private ElbowPipeSize _originalSize;
        private double _originalRotation = 0;

        // ✅ 新增：记录调整大小时的固定点
        private Point _fixedPoint;

        private const double MinArmLength = 30;
        private const double MaxArmLength = 300;
        private const double MinPipeWidth = 8;
        private const double MaxPipeWidth = 60;

        private double _horizontalLength = 50;
        private double _verticalLength = 60;
        private double _pipeWidth = 20;
        private double _rotationAngle = 0;

        private RotateTransform _rotateTransform;
        private TransformGroup _transformGroup;
        private ScaleTransform _flipTransform;
        private DispatcherTimer _rotationLabelTimer;

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

                // ✅ 修复1: 整个控件的鼠标事件用于锚点显示/隐藏
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
        }

        #region ✅ 修复1: 锚点显示控制

        /// <summary>
        /// 鼠标进入管道区域，显示锚点
        /// </summary>
        private void OnPipeMouseEnter(object sender, MouseEventArgs e)
        {
            if (!_isResizing && !_isDraggingPipe && !_isRotating)
            {
                ShowAllAnchors();
            }
        }

        /// <summary>
        /// 鼠标离开管道区域，隐藏锚点
        /// </summary>
        private void OnPipeMouseLeave(object sender, MouseEventArgs e)
        {
            if (!_isResizing && !_isRotating && !IsSelected)
            {
                HideAllAnchors();
            }
        }

        /// <summary>
        /// 显示所有锚点
        /// </summary>
        private void ShowAllAnchors()
        {
            LeftAnchor.Opacity = 0.6;
            TopAnchor.Opacity = 0.6;
            HorizontalWidthAnchor.Opacity = 0.6;
            VerticalWidthAnchor.Opacity = 0.6;
            RotateAnchor.Opacity = 0.6;
            RotateAnchorLine.Opacity = 0.6;
        }

        /// <summary>
        /// 隐藏所有锚点
        /// </summary>
        private void HideAllAnchors()
        {
            LeftAnchor.Opacity = 0;
            TopAnchor.Opacity = 0;
            HorizontalWidthAnchor.Opacity = 0;
            VerticalWidthAnchor.Opacity = 0;
            RotateAnchor.Opacity = 0;
            RotateAnchorLine.Opacity = 0;
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
            Canvas.SetTop(RotationLabel, -30);
        }

        #endregion

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

        #region 管道几何更新

        private void UpdatePipeGeometry()
        {
            this.Width = _horizontalLength + 20;
            this.Height = _verticalLength + 20;

            double flangeSpace = 8;
            double elbowRadius = _pipeWidth;

            double horizontalCenterY = this.Height - 10 - _pipeWidth / 2;
            double verticalCenterX = this.Width - 10 - _pipeWidth / 2;

            double elbowCenterX = verticalCenterX;
            double elbowCenterY = horizontalCenterY;

            UpdateHorizontalPipe(horizontalCenterY, elbowCenterX, flangeSpace, elbowRadius);
            UpdateVerticalPipe(verticalCenterX, elbowCenterY, flangeSpace, elbowRadius);
            UpdateElbow(elbowCenterX, elbowCenterY, elbowRadius);
            UpdateFlanges(horizontalCenterY, verticalCenterX);
            UpdateConnections(horizontalCenterY, verticalCenterX);

            SelectionBorder.Width = this.Width;
            SelectionBorder.Height = this.Height;

            UpdateAnchorPositions(horizontalCenterY, verticalCenterX);
            UpdateRotationLabelPosition();
        }

        private void UpdateHorizontalPipe(double centerY, double elbowCenterX, double flangeSpace, double elbowRadius)
        {
            double pipeLeft = flangeSpace;
            double pipeWidth = Math.Max(0, elbowCenterX - elbowRadius - pipeLeft);
            double pipeTop = centerY - _pipeWidth / 2;

            HorizontalPipe.Width = pipeWidth;
            HorizontalPipe.Height = _pipeWidth;
            Canvas.SetLeft(HorizontalPipe, pipeLeft);
            Canvas.SetTop(HorizontalPipe, pipeTop);

            HorizontalTopEdge.X1 = pipeLeft;
            HorizontalTopEdge.X2 = pipeLeft + pipeWidth;
            HorizontalTopEdge.Y1 = pipeTop;
            HorizontalTopEdge.Y2 = pipeTop;

            HorizontalBottomEdge.X1 = pipeLeft;
            HorizontalBottomEdge.X2 = pipeLeft + pipeWidth;
            HorizontalBottomEdge.Y1 = pipeTop + _pipeWidth;
            HorizontalBottomEdge.Y2 = pipeTop + _pipeWidth;

            HorizontalHighlight.Width = Math.Max(0, pipeWidth - 2);
            Canvas.SetLeft(HorizontalHighlight, pipeLeft + 1);
            Canvas.SetTop(HorizontalHighlight, pipeTop + 1);
        }

        private void UpdateVerticalPipe(double centerX, double elbowCenterY, double flangeSpace, double elbowRadius)
        {
            double pipeTop = flangeSpace;
            double pipeHeight = Math.Max(0, elbowCenterY - elbowRadius - pipeTop);
            double pipeLeft = centerX - _pipeWidth / 2;

            VerticalPipe.Width = _pipeWidth;
            VerticalPipe.Height = pipeHeight;
            Canvas.SetLeft(VerticalPipe, pipeLeft);
            Canvas.SetTop(VerticalPipe, pipeTop);

            VerticalLeftEdge.X1 = pipeLeft;
            VerticalLeftEdge.X2 = pipeLeft;
            VerticalLeftEdge.Y1 = pipeTop;
            VerticalLeftEdge.Y2 = pipeTop + pipeHeight;

            VerticalRightEdge.X1 = pipeLeft + _pipeWidth;
            VerticalRightEdge.X2 = pipeLeft + _pipeWidth;
            VerticalRightEdge.Y1 = pipeTop;
            VerticalRightEdge.Y2 = pipeTop + pipeHeight;

            VerticalHighlight.Height = Math.Max(0, pipeHeight - 2);
            Canvas.SetLeft(VerticalHighlight, pipeLeft + 1);
            Canvas.SetTop(VerticalHighlight, pipeTop + 1);
        }

        private void UpdateElbow(double centerX, double centerY, double radius)
        {
            PathGeometry elbowGeometry = new PathGeometry();
            PathFigure figure = new PathFigure();

            Point outerStart = new Point(centerX - radius, centerY + _pipeWidth / 2);
            figure.StartPoint = outerStart;

            ArcSegment outerArc = new ArcSegment(
                new Point(centerX + _pipeWidth / 2, centerY - radius),
                new Size(radius + _pipeWidth / 2, radius + _pipeWidth / 2),
                0, false, SweepDirection.Counterclockwise, true);
            figure.Segments.Add(outerArc);

            LineSegment toInner = new LineSegment(
                new Point(centerX - _pipeWidth / 2, centerY - radius), true);
            figure.Segments.Add(toInner);

            ArcSegment innerArc = new ArcSegment(
                new Point(centerX - radius, centerY - _pipeWidth / 2),
                new Size(radius - _pipeWidth / 2, radius - _pipeWidth / 2),
                0, false, SweepDirection.Clockwise, true);
            figure.Segments.Add(innerArc);

            figure.IsClosed = true;
            elbowGeometry.Figures.Add(figure);
            ElbowPath.Data = elbowGeometry;

            PathGeometry highlightGeometry = new PathGeometry();
            PathFigure highlightFigure = new PathFigure();
            double highlightRadius = radius - _pipeWidth / 4;
            Point highlightStart = new Point(centerX - highlightRadius, centerY - _pipeWidth / 4);
            highlightFigure.StartPoint = highlightStart;

            ArcSegment highlightArc = new ArcSegment(
                new Point(centerX - _pipeWidth / 4, centerY - highlightRadius),
                new Size(highlightRadius, highlightRadius),
                0, false, SweepDirection.Counterclockwise, true);
            highlightFigure.Segments.Add(highlightArc);

            highlightGeometry.Figures.Add(highlightFigure);
            ElbowHighlight.Data = highlightGeometry;
        }

        private void UpdateFlanges(double horizontalY, double verticalX)
        {
            double flangeHeight = _pipeWidth + 8;
            double flangeWidth = _pipeWidth + 8;

            Canvas.SetLeft(LeftFlangeGroup, 2);
            Canvas.SetTop(LeftFlangeGroup, horizontalY - flangeHeight / 2);
            LeftFlangeGroup.Height = flangeHeight;
            LeftFlangeBase.Height = flangeHeight;
            LeftFlangeGradient.Height = flangeHeight;
            LeftFlangeBorder.Height = flangeHeight;

            UpdateFlangeBoltsVertical(LeftBolt1, LeftBolt2, LeftBolt3, flangeHeight);

            Canvas.SetLeft(TopFlangeGroup, verticalX - flangeWidth / 2);
            Canvas.SetTop(TopFlangeGroup, 2);
            TopFlangeGroup.Width = flangeWidth;
            TopFlangeBase.Width = flangeWidth;
            TopFlangeGradient.Width = flangeWidth;
            TopFlangeBorder.Width = flangeWidth;

            UpdateFlangeBoltsHorizontal(TopBolt1, TopBolt2, TopBolt3, flangeWidth);
        }

        private void UpdateFlangeBoltsVertical(Ellipse bolt1, Ellipse bolt2, Ellipse bolt3, double flangeHeight)
        {
            double boltSize = 2.5;
            double leftPosition = 1.75;
            double spacing = (flangeHeight - boltSize) / 4;

            Canvas.SetLeft(bolt1, leftPosition);
            Canvas.SetTop(bolt1, spacing);

            Canvas.SetLeft(bolt2, leftPosition);
            Canvas.SetTop(bolt2, spacing * 2 + boltSize / 2);

            Canvas.SetLeft(bolt3, leftPosition);
            Canvas.SetTop(bolt3, spacing * 3 + boltSize);
        }

        private void UpdateFlangeBoltsHorizontal(Ellipse bolt1, Ellipse bolt2, Ellipse bolt3, double flangeWidth)
        {
            double boltSize = 2.5;
            double topPosition = 1.75;
            double spacing = (flangeWidth - boltSize) / 4;

            Canvas.SetLeft(bolt1, spacing);
            Canvas.SetTop(bolt1, topPosition);

            Canvas.SetLeft(bolt2, spacing * 2 + boltSize / 2);
            Canvas.SetTop(bolt2, topPosition);

            Canvas.SetLeft(bolt3, spacing * 3 + boltSize);
            Canvas.SetTop(bolt3, topPosition);
        }

        private void UpdateConnections(double horizontalY, double verticalX)
        {
            LeftConnection.Height = _pipeWidth;
            Canvas.SetLeft(LeftConnection, 7);
            Canvas.SetTop(LeftConnection, horizontalY - _pipeWidth / 2);

            TopConnection.Width = _pipeWidth;
            Canvas.SetLeft(TopConnection, verticalX - _pipeWidth / 2);
            Canvas.SetTop(TopConnection, 7);
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
                _isDraggingPipe = true;
                var parent = this.Parent as Canvas;
                if (parent != null)
                {
                    _dragStartPoint = e.GetPosition(parent);
                    _originalPosition = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
                    if (double.IsNaN(_originalPosition.X)) _originalPosition.X = 0;
                    if (double.IsNaN(_originalPosition.Y)) _originalPosition.Y = 0;
                }
                this.CaptureMouse();
                this.Opacity = 0.7;
                PipeDragStarted?.Invoke(this, new ElbowPipeDragEventArgs { PipeId = PipeId, StartPosition = _originalPosition });
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
            PipeDragCompleted?.Invoke(this, new ElbowPipeDragEventArgs { PipeId = PipeId, StartPosition = _originalPosition, CurrentPosition = finalPosition });
            this.Opacity = 1.0;
            _isDraggingPipe = false;
            this.ReleaseMouseCapture();
        }

        private bool IsClickOnAnchor(object source)
        {
            return source == LeftAnchor || source == TopAnchor ||
                   source == HorizontalWidthAnchor || source == VerticalWidthAnchor ||
                   source == RotateAnchor;
        }

        private void OnAnchorMouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Ellipse anchor) anchor.Opacity = 1.0;
        }

        private void OnAnchorMouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Ellipse anchor && !_isResizing) anchor.Opacity = 0.6;
        }

        #region ✅ 修复2&3: 锚点调整大小（保持固定端不动 + 旋转后方向正确）

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

                    // ✅ 关键：计算固定点的世界坐标
                    _fixedPoint = CalculateElbowFixedPoint(position);

                    Debug.WriteLine($"开始调整弯管大小: 锚点={position}, 固定点=({_fixedPoint.X:F1}, {_fixedPoint.Y:F1})");
                }
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

        /// <summary>
        /// 计算弯管调整时的固定点（弯角的对角位置）
        /// </summary>
        private Point CalculateElbowFixedPoint(ElbowAnchorPosition draggedAnchor)
        {
            var parent = this.Parent as Canvas;
            if (parent == null) return new Point();

            var pipeLeft = Canvas.GetLeft(this);
            var pipeTop = Canvas.GetTop(this);
            if (double.IsNaN(pipeLeft)) pipeLeft = 0;
            if (double.IsNaN(pipeTop)) pipeTop = 0;

            // 弯管中心点（旋转中心）
            Point center = new Point(
                pipeLeft + this.ActualWidth / 2,
                pipeTop + this.ActualHeight / 2
            );

            double radians = _rotationAngle * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);

            // 弯管的关键点在本地坐标系中的位置
            // 本地坐标系：原点在控件中心，X轴向右，Y轴向下
            Point localFixed = new Point();

            switch (draggedAnchor)
            {
                case ElbowAnchorPosition.Left:
                    // 拖动左端（水平臂），固定点在弯角
                    localFixed = new Point(
                        this.Width / 2 - 10 - _pipeWidth / 2,
                        this.Height / 2 - 10 - _pipeWidth / 2
                    );
                    break;

                case ElbowAnchorPosition.Top:
                    // 拖动顶端（垂直臂），固定点在弯角
                    localFixed = new Point(
                        this.Width / 2 - 10 - _pipeWidth / 2,
                        this.Height / 2 - 10 - _pipeWidth / 2
                    );
                    break;

                case ElbowAnchorPosition.HorizontalWidth:
                case ElbowAnchorPosition.VerticalWidth:
                    // 调整宽度时，固定点在控件中心
                    localFixed = new Point(0, 0);
                    break;
            }

            // 旋转本地坐标到世界坐标
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

        /// <summary>
        /// 从左端调整水平臂长度
        /// </summary>
        private void ResizeHorizontalArmFromLeft(Point currentMousePosition)
        {
            // 计算鼠标到固定点的向量
            double deltaX = currentMousePosition.X - _fixedPoint.X;
            double deltaY = currentMousePosition.Y - _fixedPoint.Y;

            double radians = _rotationAngle * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);

            // 投影到水平臂方向（旋转后的X轴）
            double projectedLength = deltaX * cos + deltaY * sin;

            // 新的水平臂长度（取绝对值，因为可能反向）
            double newLength = Math.Abs(projectedLength);
            newLength = Math.Max(MinArmLength, Math.Min(MaxArmLength, newLength));

            _horizontalLength = newLength;

            // ✅ 关键：重新计算控件位置，使弯角固定
            RecalculatePositionForElbow();

            UpdatePipeGeometry();

            Debug.WriteLine($"调整水平臂: {newLength:F1}px");
        }

        /// <summary>
        /// 从顶端调整垂直臂长度
        /// </summary>
        private void ResizeVerticalArmFromTop(Point currentMousePosition)
        {
            double deltaX = currentMousePosition.X - _fixedPoint.X;
            double deltaY = currentMousePosition.Y - _fixedPoint.Y;

            double radians = _rotationAngle * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);

            // 投影到垂直臂方向（旋转后的Y轴）
            double projectedLength = -deltaX * sin + deltaY * cos;

            double newLength = Math.Abs(projectedLength);
            newLength = Math.Max(MinArmLength, Math.Min(MaxArmLength, newLength));

            _verticalLength = newLength;

            RecalculatePositionForElbow();

            UpdatePipeGeometry();

            Debug.WriteLine($"调整垂直臂: {newLength:F1}px");
        }

        /// <summary>
        /// 调整管道宽度
        /// </summary>
        private void ResizePipeWidthFromAnchor(Point currentMousePosition)
        {
            double deltaX = currentMousePosition.X - _dragStartPoint.X;
            double deltaY = currentMousePosition.Y - _dragStartPoint.Y;

            double delta = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
            if (deltaX < 0 || deltaY < 0) delta = -delta;

            double newWidth = _originalSize.PipeWidth + delta * 0.5;
            newWidth = Math.Max(MinPipeWidth, Math.Min(MaxPipeWidth, newWidth));

            // 计算宽度变化的比例
            double widthRatio = newWidth / _originalSize.PipeWidth;

            // 按比例调整水平和垂直臂的长度，保持整体协调
            double newHorizontalLength = _originalSize.HorizontalLength * widthRatio;
            double newVerticalLength = _originalSize.VerticalLength * widthRatio;

            // 确保长度在合理范围内
            newHorizontalLength = Math.Max(MinArmLength, Math.Min(MaxArmLength, newHorizontalLength));
            newVerticalLength = Math.Max(MinArmLength, Math.Min(MaxArmLength, newVerticalLength));

            _pipeWidth = newWidth;
            _horizontalLength = newHorizontalLength;
            _verticalLength = newVerticalLength;

            UpdatePipeGeometry();

            Debug.WriteLine($"协调调整 - 宽度: {newWidth:F1}px, 水平: {newHorizontalLength:F1}px, 垂直: {newVerticalLength:F1}px, 比例: {widthRatio:F2}");
        }

        /// <summary>
        /// 重新计算弯管位置，使弯角保持固定
        /// </summary>
        private void RecalculatePositionForElbow()
        {
            double radians = _rotationAngle * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);

            // 弯角在新尺寸下的本地坐标偏移
            Point elbowLocalOffset = new Point(
                this.Width / 2 - 10 - _pipeWidth / 2,
                this.Height / 2 - 10 - _pipeWidth / 2
            );

            // 旋转到世界坐标
            Point elbowWorldOffset = new Point(
                elbowLocalOffset.X * cos - elbowLocalOffset.Y * sin,
                elbowLocalOffset.X * sin + elbowLocalOffset.Y * cos
            );

            // 新的中心点 = 固定点 - 弯角偏移
            Point newCenter = new Point(
                _fixedPoint.X - elbowWorldOffset.X,
                _fixedPoint.Y - elbowWorldOffset.Y
            );

            // 更新后需要立即更新几何，以获取正确的 ActualWidth/Height
            this.Width = _horizontalLength + 20;
            this.Height = _verticalLength + 20;

            // 设置新位置（左上角）
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
            this.ReleaseMouseCapture();
        }

        #endregion

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

        private void OnRotateAnchorMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _isRotating = true;
                _originalRotation = _rotationAngle;
                var parent = this.Parent as Canvas;
                if (parent != null) _dragStartPoint = e.GetPosition(parent);
                this.CaptureMouse();
                PipeRotateStarted?.Invoke(this, new ElbowPipeRotateEventArgs { PipeId = PipeId, OriginalAngle = _originalRotation });
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
            this.ReleaseMouseCapture();
        }

        #endregion

        public void SetSelected(bool selected)
        {
            IsSelected = selected;
            if (selected)
            {
                ShowAllAnchors();
                SelectionBorder.Visibility = Visibility.Visible;
            }
            else
            {
                HideAllAnchors();
                SelectionBorder.Visibility = Visibility.Collapsed;
            }
        }
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