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
    public partial class StraightPipeControl : UserControl
    {
        // 事件定义
        public event EventHandler<PipeDragEventArgs> PipeDragStarted;
        public event EventHandler<PipeDragEventArgs> PipeDragging;
        public event EventHandler<PipeDragEventArgs> PipeDragCompleted;
        public event EventHandler<PipeResizeEventArgs> PipeResizeStarted;
        public event EventHandler<PipeResizeEventArgs> PipeResizing;
        public event EventHandler<PipeResizeEventArgs> PipeResizeCompleted;
        public event EventHandler<PipeRotateEventArgs> PipeRotateStarted;
        public event EventHandler<PipeRotateEventArgs> PipeRotating;
        public event EventHandler<PipeRotateEventArgs> PipeRotateCompleted;

        public string PipeId { get; set; }
        public bool IsSelected { get; set; }

        private bool _isDraggingPipe = false;
        private bool _isResizing = false;
        private bool _isRotating = false;
        private PipeAnchorPosition? _resizingAnchor = null;
        private Point _dragStartPoint;
        private Point _originalPosition;
        private Size _originalSize;
        private double _originalRotation = 0;
        private Point _fixedPoint;

        private const double MinPipeLength = 50;
        private const double MaxPipeLength = 500;
        private const double MinPipeWidth = 8;
        private const double MaxPipeWidth = 100;

        private double _pipeLength = 80;
        private double _pipeWidth = 15;
        private double _rotationAngle = 0;

        private RotateTransform _rotateTransform;
        private TransformGroup _transformGroup;
        private DispatcherTimer _rotationLabelTimer;

        public double PipeLength
        {
            get => _pipeLength;
            set
            {
                _pipeLength = Math.Max(MinPipeLength, Math.Min(MaxPipeLength, value));
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

        public StraightPipeControl()
        {
            InitializeComponent();
            PipeId = Guid.NewGuid().ToString();
            this.DataContext = this;

            _rotateTransform = new RotateTransform(0);
            _transformGroup = new TransformGroup();
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
        }

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
            RightAnchor.Opacity = 0.6;
            TopAnchor.Opacity = 0.6;
            BottomAnchor.Opacity = 0.6;
            RotateAnchor.Opacity = 0.6;
            RotateAnchorLine.Opacity = 0.6;
        }

        private void HideAllAnchors()
        {
            LeftAnchor.Opacity = 0;
            RightAnchor.Opacity = 0;
            TopAnchor.Opacity = 0;
            BottomAnchor.Opacity = 0;
            RotateAnchor.Opacity = 0;
            RotateAnchorLine.Opacity = 0;
        }

        #endregion

        private void SetupAnchorEvents()
        {
            LeftAnchor.MouseEnter += OnAnchorMouseEnter;
            LeftAnchor.MouseLeave += OnAnchorMouseLeave;
            LeftAnchor.MouseDown += (s, e) => OnAnchorMouseDown(PipeAnchorPosition.Left, e);

            RightAnchor.MouseEnter += OnAnchorMouseEnter;
            RightAnchor.MouseLeave += OnAnchorMouseLeave;
            RightAnchor.MouseDown += (s, e) => OnAnchorMouseDown(PipeAnchorPosition.Right, e);

            TopAnchor.MouseEnter += OnAnchorMouseEnter;
            TopAnchor.MouseLeave += OnAnchorMouseLeave;
            TopAnchor.MouseDown += (s, e) => OnAnchorMouseDown(PipeAnchorPosition.Top, e);

            BottomAnchor.MouseEnter += OnAnchorMouseEnter;
            BottomAnchor.MouseLeave += OnAnchorMouseLeave;
            BottomAnchor.MouseDown += (s, e) => OnAnchorMouseDown(PipeAnchorPosition.Bottom, e);

            RotateAnchor.MouseEnter += OnRotateAnchorMouseEnter;
            RotateAnchor.MouseLeave += OnRotateAnchorMouseLeave;
            RotateAnchor.MouseDown += OnRotateAnchorMouseDown;
        }

        #region 管道几何更新

        private void UpdatePipeGeometry()
        {
            this.Width = _pipeLength + 16;
            this.Height = _pipeWidth + 30;

            MainPipe.Width = _pipeLength;
            MainPipe.Height = _pipeWidth;
            Canvas.SetLeft(MainPipe, 8);
            Canvas.SetTop(MainPipe, 15);

            TopEdgeLine.X1 = 8;
            TopEdgeLine.X2 = 8 + _pipeLength;
            TopEdgeLine.Y1 = 15;
            TopEdgeLine.Y2 = 15;

            BottomEdgeLine.X1 = 8;
            BottomEdgeLine.X2 = 8 + _pipeLength;
            BottomEdgeLine.Y1 = 15 + _pipeWidth;
            BottomEdgeLine.Y2 = 15 + _pipeWidth;

            TopHighlight.Width = _pipeLength - 2;
            Canvas.SetLeft(TopHighlight, 9);
            Canvas.SetTop(TopHighlight, 16);

            BottomShadow.Width = _pipeLength - 2;
            Canvas.SetLeft(BottomShadow, 9);
            Canvas.SetTop(BottomShadow, 15 + _pipeWidth - 2);

            UpdateFlanges();
            UpdateConnections();

            SelectionBorder.Width = this.Width;
            SelectionBorder.Height = _pipeWidth + 4;

            UpdateAnchorPositions();
            UpdateRotationLabelPosition();
        }

        private void UpdateFlanges()
        {
            double flangeHeight = _pipeWidth + 8;
            double flangeTop = (this.Height - flangeHeight) / 2;

            Canvas.SetLeft(LeftFlangeGroup, 2);
            Canvas.SetTop(LeftFlangeGroup, flangeTop);
            LeftFlangeBase.Height = flangeHeight;
            LeftFlangeGradient.Height = flangeHeight;
            LeftFlangeBorder.Height = flangeHeight;
            UpdateFlangeBolts(LeftBolt1, LeftBolt2, LeftBolt3, flangeHeight);

            Canvas.SetLeft(RightFlangeGroup, this.Width - 8);
            Canvas.SetTop(RightFlangeGroup, flangeTop);
            RightFlangeBase.Height = flangeHeight;
            RightFlangeGradient.Height = flangeHeight;
            RightFlangeBorder.Height = flangeHeight;
            UpdateFlangeBolts(RightBolt1, RightBolt2, RightBolt3, flangeHeight);
        }

        private void UpdateFlangeBolts(Ellipse bolt1, Ellipse bolt2, Ellipse bolt3, double flangeHeight)
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

        private void UpdateConnections()
        {
            LeftConnection.Height = _pipeWidth;
            Canvas.SetLeft(LeftConnection, 7);
            Canvas.SetTop(LeftConnection, 15);

            RightConnection.Height = _pipeWidth;
            Canvas.SetLeft(RightConnection, 8 + _pipeLength - 1);
            Canvas.SetTop(RightConnection, 15);
        }

        private void UpdateAnchorPositions()
        {
            double centerY = this.Height / 2;
            double centerX = this.Width / 2;

            Canvas.SetLeft(LeftAnchor, -6);
            Canvas.SetTop(LeftAnchor, centerY - 6);

            Canvas.SetLeft(RightAnchor, this.Width - 6);
            Canvas.SetTop(RightAnchor, centerY - 6);

            Canvas.SetLeft(TopAnchor, centerX - 6);
            Canvas.SetTop(TopAnchor, 4);

            Canvas.SetLeft(BottomAnchor, centerX - 6);
            Canvas.SetTop(BottomAnchor, this.Height - 16);

            Canvas.SetLeft(RotateAnchor, centerX - 6);
            Canvas.SetTop(RotateAnchor, -30);

            RotateAnchorLine.X1 = centerX;
            RotateAnchorLine.Y1 = 0;
            RotateAnchorLine.X2 = centerX;
            RotateAnchorLine.Y2 = -24;
        }

        private void UpdateRotationLabelPosition()
        {
            double centerX = this.Width / 2;
            Canvas.SetLeft(RotationLabel, centerX - 20);
            Canvas.SetTop(RotationLabel, -50);
        }

        private void UpdateRotation()
        {
            _rotateTransform.Angle = _rotationAngle;
            RotationText.Text = $"{_rotationAngle:F0}°";
        }

        #endregion

        #region 整个管道拖动

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
                PipeDragStarted?.Invoke(this, new PipeDragEventArgs { PipeId = PipeId, StartPosition = _originalPosition });
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
                    PipeDragging?.Invoke(this, new PipeDragEventArgs { PipeId = PipeId, CurrentPosition = new Point(newLeft, newTop) });
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
            PipeDragCompleted?.Invoke(this, new PipeDragEventArgs { PipeId = PipeId, StartPosition = _originalPosition, CurrentPosition = finalPosition });
            this.Opacity = 1.0;
            _isDraggingPipe = false;
            this.ReleaseMouseCapture();
        }

        private bool IsClickOnAnchor(object source)
        {
            return source == LeftAnchor || source == RightAnchor ||
                   source == TopAnchor || source == BottomAnchor ||
                   source == RotateAnchor;
        }

        #endregion

        #region 锚点调整大小

        private void OnAnchorMouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Ellipse anchor) anchor.Opacity = 1.0;
        }

        private void OnAnchorMouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Ellipse anchor && !_isResizing) anchor.Opacity = 0.6;
        }

        private void OnAnchorMouseDown(PipeAnchorPosition position, MouseButtonEventArgs e)
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
                    _originalSize = new Size(_pipeLength, _pipeWidth);
                    if (double.IsNaN(_originalPosition.X)) _originalPosition.X = 0;
                    if (double.IsNaN(_originalPosition.Y)) _originalPosition.Y = 0;

                    _fixedPoint = CalculateFixedPoint(position);
                    Debug.WriteLine($"开始调整大小: 锚点={position}, 固定点=({_fixedPoint.X:F1}, {_fixedPoint.Y:F1})");
                }
                this.CaptureMouse();
                PipeResizeStarted?.Invoke(this, new PipeResizeEventArgs
                {
                    PipeId = PipeId,
                    AnchorPosition = position,
                    OriginalSize = _originalSize
                });
                e.Handled = true;
            }
        }

        private Point CalculateFixedPoint(PipeAnchorPosition draggedAnchor)
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
                case PipeAnchorPosition.Left:
                    localFixed = new Point(_pipeLength / 2, 0);
                    break;
                case PipeAnchorPosition.Right:
                    localFixed = new Point(-_pipeLength / 2, 0);
                    break;
                case PipeAnchorPosition.Top:
                    localFixed = new Point(0, _pipeWidth / 2);
                    break;
                case PipeAnchorPosition.Bottom:
                    localFixed = new Point(0, -_pipeWidth / 2);
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
                case PipeAnchorPosition.Left:
                case PipeAnchorPosition.Right:
                    ResizeLengthFromAnchor(currentPoint);
                    break;

                case PipeAnchorPosition.Top:
                case PipeAnchorPosition.Bottom:
                    ResizeWidthFromAnchor(currentPoint);
                    break;
            }

            PipeResizing?.Invoke(this, new PipeResizeEventArgs
            {
                PipeId = PipeId,
                AnchorPosition = _resizingAnchor.Value,
                CurrentSize = new Size(_pipeLength, _pipeWidth)
            });
        }

        private void ResizeLengthFromAnchor(Point currentMousePosition)
        {
            // 计算固定点到鼠标位置的向量
            double deltaX = currentMousePosition.X - _fixedPoint.X;
            double deltaY = currentMousePosition.Y - _fixedPoint.Y;

            // 获取当前旋转角度的弧度值
            double radians = _rotationAngle * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);

            // 将鼠标移动投影到管道的轴向（沿着管道方向）
            // 这样可以只改变长度，而不改变角度
            double projectedLength = deltaX * cos + deltaY * sin;

            // 如果拖动的是左侧锚点，方向相反
            if (_resizingAnchor == PipeAnchorPosition.Left)
            {
                projectedLength = -projectedLength;
            }

            // 新长度是投影距离的绝对值
            double newLength = Math.Abs(projectedLength);

            // 限制在最小和最大长度之间
            newLength = Math.Max(MinPipeLength, Math.Min(MaxPipeLength, newLength));

            // 只更新长度，不改变旋转角度
            _pipeLength = newLength;

            // 重新计算控件的中心位置，确保固定点保持不动
            // 固定点在局部坐标系中的位置
            double fixedLocalX = (_resizingAnchor == PipeAnchorPosition.Right)
                ? -_pipeLength / 2  // 右侧锚点拖动时，左端是固定点
                : _pipeLength / 2;   // 左侧锚点拖动时，右端是固定点

            // 将固定点的局部坐标转换为世界坐标偏移
            Point fixedOffset = new Point(
                fixedLocalX * cos,
                fixedLocalX * sin
            );

            // 计算新的中心点位置
            Point newCenter = new Point(
                _fixedPoint.X - fixedOffset.X,
                _fixedPoint.Y - fixedOffset.Y
            );

            // 更新控件位置
            Canvas.SetLeft(this, newCenter.X - this.ActualWidth / 2);
            Canvas.SetTop(this, newCenter.Y - this.ActualHeight / 2);

            // 更新几何形状
            UpdatePipeGeometry();

            Debug.WriteLine($"调整长度: {_pipeLength:F1}px, 角度保持: {_rotationAngle:F1}°");
        }

        private void ResizeWidthFromAnchor(Point currentMousePosition)
        {
            double deltaX = currentMousePosition.X - _fixedPoint.X;
            double deltaY = currentMousePosition.Y - _fixedPoint.Y;

            double radians = _rotationAngle * Math.PI / 180.0;
            double cos = Math.Cos(radians);
            double sin = Math.Sin(radians);

            double perpDelta = -deltaX * sin + deltaY * cos;

            double newWidth = Math.Abs(perpDelta);
            newWidth = Math.Max(MinPipeWidth, Math.Min(MaxPipeWidth, newWidth));

            _pipeWidth = newWidth;

            double fixedLocalY = (_resizingAnchor == PipeAnchorPosition.Bottom) ? -_pipeWidth / 2 : _pipeWidth / 2;

            Point fixedOffset = new Point(
                -fixedLocalY * sin,
                fixedLocalY * cos
            );

            Point newCenter = new Point(
                _fixedPoint.X - fixedOffset.X,
                _fixedPoint.Y - fixedOffset.Y
            );

            Canvas.SetLeft(this, newCenter.X - this.ActualWidth / 2);
            Canvas.SetTop(this, newCenter.Y - this.ActualHeight / 2);

            UpdatePipeGeometry();

            Debug.WriteLine($"调整宽度: {newWidth:F1}px");
        }

        private void CompleteResize()
        {
            if (!_isResizing) return;
            PipeResizeCompleted?.Invoke(this, new PipeResizeEventArgs
            {
                PipeId = PipeId,
                AnchorPosition = _resizingAnchor.Value,
                OriginalSize = _originalSize,
                CurrentSize = new Size(_pipeLength, _pipeWidth)
            });
            Debug.WriteLine($"调整大小完成: 长度={_pipeLength:F0}px, 宽度={_pipeWidth:F0}px");
            _isResizing = false;
            _resizingAnchor = null;
            this.ReleaseMouseCapture();
        }

        #endregion

        #region 旋转功能

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
                PipeRotateStarted?.Invoke(this, new PipeRotateEventArgs { PipeId = PipeId, OriginalAngle = _originalRotation });
                ShowRotationLabel();
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
            PipeRotating?.Invoke(this, new PipeRotateEventArgs { PipeId = PipeId, CurrentAngle = _rotationAngle });
        }

        private void CompleteRotate()
        {
            if (!_isRotating) return;
            PipeRotateCompleted?.Invoke(this, new PipeRotateEventArgs
            {
                PipeId = PipeId,
                OriginalAngle = _originalRotation,
                CurrentAngle = _rotationAngle
            });
            _isRotating = false;
            this.ReleaseMouseCapture();
        }

        private void ShowRotationLabel()
        {
            RotationLabel.Visibility = Visibility.Visible;
            _rotationLabelTimer.Stop();
            _rotationLabelTimer.Start();
        }

        #endregion

        #region 选择状态

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

        #endregion

        #region 辅助方法

        public Point GetAnchorPosition(PipeAnchorPosition position)
        {
            var parent = this.Parent as Canvas;
            if (parent == null) return new Point();

            var pipeLeft = Canvas.GetLeft(this);
            var pipeTop = Canvas.GetTop(this);
            if (double.IsNaN(pipeLeft)) pipeLeft = 0;
            if (double.IsNaN(pipeTop)) pipeTop = 0;

            switch (position)
            {
                case PipeAnchorPosition.Left:
                    return new Point(pipeLeft + 8, pipeTop + this.Height / 2);
                case PipeAnchorPosition.Right:
                    return new Point(pipeLeft + this.Width - 8, pipeTop + this.Height / 2);
                case PipeAnchorPosition.Top:
                    return new Point(pipeLeft + this.Width / 2, pipeTop + 15);
                case PipeAnchorPosition.Bottom:
                    return new Point(pipeLeft + this.Width / 2, pipeTop + this.Height - 15);
                default:
                    return new Point();
            }
        }

        #endregion
    }

    #region 事件参数类

    public class PipeDragEventArgs : EventArgs
    {
        public string PipeId { get; set; }
        public Point StartPosition { get; set; }
        public Point CurrentPosition { get; set; }
    }

    public class PipeResizeEventArgs : EventArgs
    {
        public string PipeId { get; set; }
        public PipeAnchorPosition AnchorPosition { get; set; }
        public Size OriginalSize { get; set; }
        public Size CurrentSize { get; set; }
    }

    public class PipeRotateEventArgs : EventArgs
    {
        public string PipeId { get; set; }
        public double OriginalAngle { get; set; }
        public double CurrentAngle { get; set; }
    }

    public enum PipeAnchorPosition
    {
        Left,
        Right,
        Top,
        Bottom
    }

    #endregion
}
