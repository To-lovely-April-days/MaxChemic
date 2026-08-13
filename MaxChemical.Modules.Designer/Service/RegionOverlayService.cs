using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MaxChemical.Core;
using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.Designer.Services;
using Prism.Events;

namespace MaxChemical.Modules.Designer.Service
{
    public class RegionOverlayService : IRegionOverlayService
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly ILocalizationService _localizationService;

        private Canvas _regionOverlay;
        private readonly List<Rectangle> _invisibleDragAreas = new List<Rectangle>();
        private bool _isDraggingRegionBorder = false;
        private bool _isDraggingVerticalBorder = false;
        private Point _dragStartPoint;
        private IndependentRegion _currentAdjustingIndependentRegion;
        private RegionBoundary _currentAdjustingBoundary;
        private readonly Dictionary<UIElement, DeviceRegionInfo> _deviceRegionMap = new Dictionary<UIElement, DeviceRegionInfo>();
        private readonly Dictionary<UIElement, Point> _originalDevicePositions = new Dictionary<UIElement, Point>();
        private List<double> _originalColumnWidths = new List<double>();
        private List<double> _originalRowHeights = new List<double>();

        // 当前上下文
        private object _currentDocument;
        private Canvas _currentCanvas;
        private RegionLayout _currentLayout;
        private double _currentScale = 1.0;
        private Dictionary<string, Border> _variablePanels = new Dictionary<string, Border>();
        private List<object> _connections = new List<object>();

        public event Action<RegionLayout> RegionLayoutChanged;
        public event Action<Size> CanvasSizeChanged;
        private  IConnectionService _connectionService; //添加连接服务字段


        public RegionOverlayService(IEventAggregator eventAggregator, ILocalizationService localizationService)
        {
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _localizationService = localizationService ?? throw new ArgumentNullException(nameof(localizationService));

            _eventAggregator.GetEvent<LanguageChangedEvent>()?.Subscribe(LanguageChangedHandler);
        }


        // 设置当前上下文
        public void SetCurrentContext(object currentDocument, Canvas designCanvas, Dictionary<string, Border> variablePanels = null, List<object> connections = null, IConnectionService connectionService = null)
        {
            Debug.WriteLine("=== 设置当前上下文 ===");

            _currentDocument = currentDocument;
            _currentCanvas = designCanvas;

            if (variablePanels != null)
            {
                _variablePanels = variablePanels;
                Debug.WriteLine($"设置变量面板: {_variablePanels.Count} 个");
            }

            if (connections != null)
            {
                _connections = connections;
                Debug.WriteLine($"设置连接: {_connections.Count} 个");
            }

            // 关键：如果传入了新的连接服务，使用它
            if (connectionService != null)
            {
                _connectionService = connectionService;
                Debug.WriteLine("使用传入的连接服务");
            }

            Debug.WriteLine($"上下文设置完成:");
            Debug.WriteLine($"  Document: {_currentDocument != null}");
            Debug.WriteLine($"  Canvas: {_currentCanvas != null}");
            Debug.WriteLine($"  VariablePanels: {_variablePanels?.Count ?? 0}");
            Debug.WriteLine($"  Connections: {_connections?.Count ?? 0}");
            Debug.WriteLine($"  ConnectionService: {_connectionService != null}");

            // 调试连接服务中的连接数量
            if (_connectionService != null)
            {
                Debug.WriteLine($"  ConnectionService.Connections: {_connectionService.Connections?.Count ?? 0}");
            }
        }

        public void UpdateRegionOverlay(Canvas canvas, RegionLayout layout, double currentScale = 1.0)
        {
            try
            {
                if (canvas == null || layout == null) return;

                _currentCanvas = canvas;
                _currentLayout = layout;
                _currentScale = currentScale;

                ClearRegionOverlay(canvas);

                // 确保使用独立区域模式
                if (!layout.UseIndependentRegions)
                {
                    layout.ConvertToIndependentRegions();
                }

                // 自动合并相邻区域
                layout.AutoMergeIndependentRegions();

                var totalSize = layout.GetTotalSizeFromIndependentRegions();
                double canvasWidth = totalSize.Width;
                double canvasHeight = totalSize.Height;

                // 更新画布尺寸
                canvas.Width = canvasWidth;
                canvas.Height = canvasHeight;

                // 创建区域覆盖层
                _regionOverlay = new Canvas
                {
                    Width = canvasWidth,
                    Height = canvasHeight,
                    IsHitTestVisible = false
                };

                Panel.SetZIndex(_regionOverlay, 2);
                canvas.Children.Insert(0, _regionOverlay);

                // 绘制区域内容
                DrawRegionContent(layout, currentScale);

                // 添加拖动区域
                AddSmartDragAreas(canvasWidth, canvasHeight, layout);

                // 通知画布尺寸变更
                CanvasSizeChanged?.Invoke(new Size(canvasWidth, canvasHeight));
            }
            catch (Exception ex)
            {
                // 忽略异常
            }
        }

        public void ClearRegionOverlay(Canvas canvas)
        {
            try
            {
                if (canvas == null) return;

                // 清理拖动区域
                ClearDragAreas();

                // 移除区域覆盖层
                if (_regionOverlay != null)
                {
                    canvas.Children.Remove(_regionOverlay);
                    _regionOverlay = null;
                }
            }
            catch (Exception ex)
            {
                // 忽略异常
            }
        }

        public void StartDragOperation(Canvas canvas, RegionLayout layout)
        {
            RecordOriginalState();
        }

        public void UpdateDuringDrag(Canvas canvas, RegionLayout layout, double currentScale = 1.0)
        {
            if (_regionOverlay == null || layout == null) return;

            try
            {
                var totalSize = layout.GetTotalSizeFromIndependentRegions();

                // 更新画布尺寸
                canvas.Width = totalSize.Width;
                canvas.Height = totalSize.Height;

                //  关键：完全重新绘制区域覆盖层内容
                RedrawRegionOverlayContent(layout, currentScale);

                // 通知画布尺寸变更
                CanvasSizeChanged?.Invoke(new Size(totalSize.Width, totalSize.Height));
            }
            catch (Exception ex)
            {
                // 忽略异常
            }
        }

        public void EndDragOperation(Canvas canvas, RegionLayout layout)
        {
            try
            {
                _isDraggingRegionBorder = false;
                _currentAdjustingIndependentRegion = null;

                // 完全重新绘制
                UpdateRegionOverlay(canvas, layout, _currentScale);

                RegionLayoutChanged?.Invoke(layout);
            }
            catch (Exception ex)
            {
                // 忽略异常
            }
        }

        //  新增：绘制区域内容（线条和标签）
        private void DrawRegionContent(RegionLayout layout, double currentScale)
        {
            double gapSize = 8.0;
            double cornerRadius = 10;
            double strokeThickness = 1 / currentScale;
            Brush lineBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
            Brush textBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

            foreach (var region in layout.IndependentRegions)
            {
                double actualLeft = region.Left + gapSize / 2;
                double actualTop = region.Top + gapSize / 2;
                double actualWidth = region.Width - gapSize;
                double actualHeight = region.Height - gapSize;

                if (actualWidth > gapSize && actualHeight > gapSize)
                {
                    // 绘制区域边框
                    var regionPath = CreateRoundedRectPath(actualLeft, actualTop, actualWidth, actualHeight, cornerRadius);
                    regionPath.Fill = Brushes.Transparent;
                    regionPath.Stroke = lineBrush;
                    regionPath.StrokeThickness = strokeThickness;
                    regionPath.StrokeDashArray = new DoubleCollection { 4 * strokeThickness, 3 * strokeThickness };

                    _regionOverlay.Children.Add(regionPath);
                }

                // 添加区域标签
                var textBlock = new TextBlock
                {
                    Text = RegionTypeToCN(region.Type),
                    Foreground = textBrush,
                    FontSize = 11 / currentScale,
                    FontWeight = FontWeights.Normal,
                    Margin = new Thickness(4 / currentScale),
                    Tag = region.Type // 新增：用于语言切换
                };

                _regionOverlay.Children.Add(textBlock);
                Canvas.SetLeft(textBlock, actualLeft + 4 / currentScale);
                Canvas.SetTop(textBlock, actualTop + 2 / currentScale);
            }
        }

        //  新增：重新绘制区域覆盖层内容（拖动时调用）
        private void RedrawRegionOverlayContent(RegionLayout layout, double currentScale)
        {
            if (_regionOverlay == null) return;

            // 清除现有的绘制内容，但保留拖动区域
            var itemsToRemove = _regionOverlay.Children.OfType<UIElement>()
                .Where(child => !(child is Rectangle) || !_invisibleDragAreas.Contains(child as Rectangle))
                .ToList();

            foreach (var item in itemsToRemove)
            {
                _regionOverlay.Children.Remove(item);
            }

            // 更新覆盖层尺寸
            var totalSize = layout.GetTotalSizeFromIndependentRegions();
            _regionOverlay.Width = totalSize.Width;
            _regionOverlay.Height = totalSize.Height;

            // 重新绘制区域内容
            DrawRegionContent(layout, currentScale);
        }
        // 严格限制左右相邻和上下相邻的区域公用一个拖拽矩形
        private void AddSmartDragAreas(double canvasWidth, double canvasHeight, RegionLayout layout)
        {
            double dragAreaWidth = 8;

            if (!layout.UseIndependentRegions)
            {
                layout.ConvertToIndependentRegions();
            }

            HashSet<string> addedBoundaries = new HashSet<string>();

            foreach (var region in layout.IndependentRegions)
            {
                // 左边界
                string leftKey = $"V_{region.Left:F1}_{region.Top:F1}_{region.Bottom:F1}";
                if (!addedBoundaries.Contains(leftKey))
                {
                    AddIndependentRegionDragArea(
                        region.Left, region.Top, dragAreaWidth, region.Height,
                        true, region, RegionBoundary.Left, layout);
                    addedBoundaries.Add(leftKey);
                }

                // 右边界
                string rightKey = $"V_{region.Right:F1}_{region.Top:F1}_{region.Bottom:F1}";
                if (!addedBoundaries.Contains(rightKey))
                {
                    AddIndependentRegionDragArea(
                        region.Right, region.Top, dragAreaWidth, region.Height,
                        true, region, RegionBoundary.Right, layout);
                    addedBoundaries.Add(rightKey);
                }

                // 上边界
                string topKey = $"H_{region.Top:F1}_{region.Left:F1}_{region.Right:F1}";
                if (!addedBoundaries.Contains(topKey))
                {
                    AddIndependentRegionDragArea(
                        region.Left, region.Top, region.Width, dragAreaWidth,
                        false, region, RegionBoundary.Top, layout);
                    addedBoundaries.Add(topKey);
                }

                // 下边界
                string bottomKey = $"H_{region.Bottom:F1}_{region.Left:F1}_{region.Right:F1}";
                if (!addedBoundaries.Contains(bottomKey))
                {
                    AddIndependentRegionDragArea(
                        region.Left, region.Bottom, region.Width, dragAreaWidth,
                        false, region, RegionBoundary.Bottom, layout);
                    addedBoundaries.Add(bottomKey);
                }
            }
        }

        private void AddIndependentRegionDragArea(double x, double y, double width, double height,
            bool isVertical, IndependentRegion region, RegionBoundary boundary, RegionLayout layout)
        {
            Rectangle dragArea = new Rectangle
            {
                Width = width,
                Height = height,
                Fill = Brushes.Transparent,
                Cursor = isVertical ? Cursors.SizeWE : Cursors.SizeNS,
                Tag = new RegionLineInfo
                {
                    IsVertical = isVertical,
                    Boundary = boundary,
                    AffectedIndependentRegion = region,
                    BoundaryPosition = isVertical ? x : y,
                    Layout = layout
                }
            };

            Canvas.SetLeft(dragArea, x - (isVertical ? width / 2 : 0));
            Canvas.SetTop(dragArea, y - (isVertical ? 0 : height / 2));
            Panel.SetZIndex(dragArea, 100);

            dragArea.MouseEnter += DragArea_MouseEnter;
            dragArea.MouseLeave += DragArea_MouseLeave;
            dragArea.MouseLeftButtonDown += DragArea_MouseLeftButtonDown;

            _currentCanvas.Children.Add(dragArea);
            _invisibleDragAreas.Add(dragArea);
        }

        #region 拖动事件处理

        private void DragArea_MouseEnter(object sender, MouseEventArgs e)
        {
            //注释掉高亮效果
            //if (sender is Rectangle dragArea && !_isDraggingRegionBorder)
            //{
            //    dragArea.Fill = new SolidColorBrush(Color.FromArgb(30, 255, 0, 0));
            //}
        }

        private void DragArea_MouseLeave(object sender, MouseEventArgs e)
        {
            //注释掉高亮效果
            //if (sender is Rectangle dragArea && !_isDraggingRegionBorder)
            //{
            //    dragArea.Fill = Brushes.Transparent;
            //}
        }

        private void DragArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Rectangle dragArea && dragArea.Tag is RegionLineInfo lineInfo)
            {
                _currentAdjustingIndependentRegion = lineInfo.AffectedIndependentRegion;
                _currentAdjustingBoundary = lineInfo.Boundary;

                RecordOriginalState();

                _isDraggingRegionBorder = true;
                _isDraggingVerticalBorder = lineInfo.IsVertical;
                _dragStartPoint = e.GetPosition(_currentCanvas);

                dragArea.CaptureMouse();
                //如果你也不想要拖动时的红色显示，也可以注释掉这行
                //dragArea.Fill = new SolidColorBrush(Color.FromArgb(60, 255, 0, 0));

                dragArea.MouseMove += DragArea_MouseMove;
                dragArea.MouseLeftButtonUp += DragArea_MouseLeftButtonUp;

                e.Handled = true;
            }
        }

        private void DragArea_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingRegionBorder || _currentAdjustingIndependentRegion == null)
                return;

            Point currentPoint = e.GetPosition(_currentCanvas);
            var lineInfo = (sender as Rectangle)?.Tag as RegionLineInfo;
            if (lineInfo?.Layout == null) return;

            if (_isDraggingVerticalBorder)
            {
                double deltaX = currentPoint.X - _dragStartPoint.X;
                _currentAdjustingIndependentRegion.AdjustBoundaryWithSync(
                    _currentAdjustingBoundary, deltaX, lineInfo.Layout.IndependentRegions);
            }
            else
            {
                double deltaY = currentPoint.Y - _dragStartPoint.Y;
                _currentAdjustingIndependentRegion.AdjustBoundaryWithSync(
                    _currentAdjustingBoundary, deltaY, lineInfo.Layout.IndependentRegions);
            }

            //  关键：更新顺序很重要
            // 1. 先更新画布尺寸
            UpdateCanvasSizeFromIndependentRegions(lineInfo.Layout);

            // 2. 再更新区域显示
            UpdateDuringDrag(_currentCanvas, lineInfo.Layout, _currentScale);

            // 3. 然后更新设备位置
            UpdateDevicePositionsDuringDrag();

            // 4. 最后更新连接线
            UpdateAllConnectionsRealtime();

            _dragStartPoint = currentPoint;
            e.Handled = true;
        }

        private void DragArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Rectangle dragArea)
            {
                dragArea.ReleaseMouseCapture();
                dragArea.Fill = Brushes.Transparent;

                dragArea.MouseMove -= DragArea_MouseMove;
                dragArea.MouseLeftButtonUp -= DragArea_MouseLeftButtonUp;

                if (_isDraggingRegionBorder)
                {
                    _isDraggingRegionBorder = false;

                    var lineInfo = dragArea.Tag as RegionLineInfo;
                    if (lineInfo?.Layout != null)
                    {
                        //  恢复连接线正常模式
                        if (_connectionService != null)
                        {
                            _connectionService.SetDragMode(false);

                            // 使用完整算法重新计算所有连接线
                            foreach (var connection in _connectionService.Connections.Values)
                            {
                                _connectionService.UpdateConnectionPath(connection);
                            }

                            Debug.WriteLine("恢复连接线完整模式，重新计算路径");
                        }

                        // 完全重新绘制区域覆盖层和拖动区域
                        UpdateRegionOverlay(_currentCanvas, lineInfo.Layout, _currentScale);

                        // 更新画布尺寸
                        UpdateCanvasSizeFromIndependentRegions(lineInfo.Layout);

                        // 最终更新设备位置
                        UpdateDevicePositionsDuringDrag();

                        // 通知布局已更改
                        RegionLayoutChanged?.Invoke(lineInfo.Layout);
                    }
                }

                // 重置状态
                _currentAdjustingIndependentRegion = null;
                _currentAdjustingBoundary = RegionBoundary.Left;
            }

            e.Handled = true;
        }

        #endregion
        #region 管道相关辅助方法

        /// <summary>
        /// 判断元素是否为管道控件
        /// </summary>
        private bool IsPipeControl(UIElement element)
        {
            if (element == null) return false;
            string typeName = element.GetType().Name;
            return typeName == "StraightPipeControl"
                || typeName == "ElbowPipeControl"
                || typeName == "ZShapePipeControl"
                || typeName == "TShapePipeControl";
        }

        /// <summary>
        /// 获取管道类型名(简化)
        /// </summary>
        private string GetPipeType(UIElement element)
        {
            if (element == null) return null;
            string typeName = element.GetType().Name;
            switch (typeName)
            {
                case "StraightPipeControl": return "Straight";
                case "ElbowPipeControl": return "Elbow";
                case "ZShapePipeControl": return "ZShape";
                case "TShapePipeControl": return "TShape";
                default: return null;
            }
        }

        /// <summary>
        /// 判断管道是否为横向(0° 或 180°,容差 5°)
        /// 只有横管在区域水平缩放时才会调整"水平长度"属性
        /// </summary>
        private bool IsHorizontalPipe(UIElement element)
        {
            if (!IsPipeControl(element)) return false;

            double angle = GetPropertyValue<double>(element, "RotationAngle", 0);
            angle = ((angle % 360) + 360) % 360;

            return (angle < 5 || angle > 355) || (angle > 175 && angle < 185);
        }

        /// <summary>
        /// 反射读取属性值(失败返回默认值)
        /// </summary>
        private T GetPropertyValue<T>(UIElement element, string propertyName, T defaultValue)
        {
            try
            {
                var prop = element.GetType().GetProperty(propertyName);
                if (prop != null)
                {
                    object val = prop.GetValue(element);
                    if (val is T typed) return typed;
                }
            }
            catch { }
            return defaultValue;
        }

        /// <summary>
        /// 反射写入属性值
        /// </summary>
        private void SetPropertyValue<T>(UIElement element, string propertyName, T value)
        {
            try
            {
                var prop = element.GetType().GetProperty(propertyName);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(element, value);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"设置属性 {propertyName} 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 根据缩放比例,调整指定管道的"水平长度"属性
        /// 各管道控件 setter 内部会自动夹紧到合法范围,不需要外部判断
        /// </summary>
        private void ApplyHorizontalScaleToPipe(UIElement element, DeviceRegionInfo info, double widthRatio)
        {
            switch (info.PipeType)
            {
                case "Straight":
                    SetPropertyValue(element, "PipeLength",
                        info.OriginalPipeLength * widthRatio);
                    break;

                case "Elbow":
                    SetPropertyValue(element, "HorizontalLength",
                        info.OriginalHorizontalLength * widthRatio);
                    break;

                case "ZShape":
                    SetPropertyValue(element, "TopHorizontalLength",
                        info.OriginalTopHorizontalLength * widthRatio);
                    SetPropertyValue(element, "BottomHorizontalLength",
                        info.OriginalBottomHorizontalLength * widthRatio);
                    break;

                case "TShape":
                    SetPropertyValue(element, "MainPipeLength",
                        info.OriginalMainPipeLength * widthRatio);
                    break;
            }
        }

        #endregion
        #region 辅助方法

        private void ClearDragAreas()
        {
            foreach (var dragArea in _invisibleDragAreas)
            {
                dragArea.MouseEnter -= DragArea_MouseEnter;
                dragArea.MouseLeave -= DragArea_MouseLeave;
                dragArea.MouseLeftButtonDown -= DragArea_MouseLeftButtonDown;

                if (dragArea.IsMouseCaptured)
                {
                    dragArea.ReleaseMouseCapture();
                }

                _currentCanvas?.Children.Remove(dragArea);
            }
            _invisibleDragAreas.Clear();
        }

        private void UpdateCanvasSizeFromIndependentRegions(RegionLayout layout)
        {
            if (layout.UseIndependentRegions)
            {
                var totalSize = layout.GetTotalSizeFromIndependentRegions();
                _currentCanvas.Width = totalSize.Width;
                _currentCanvas.Height = totalSize.Height;
                _currentCanvas.UpdateLayout();
            }
        }

        private void UpdateDevicePositionsDuringDrag()
        {
            foreach (var kvp in _deviceRegionMap)
            {
                UIElement element = kvp.Key;
                DeviceRegionInfo info = kvp.Value;

                try
                {
                    //  第一步:对横管,先按区域宽度比例缩放"水平长度"属性
                    if (info.CanScaleHorizontal && info.OriginalRegionWidth > 0)
                    {
                        double widthRatio = info.Region.Width / info.OriginalRegionWidth;
                        ApplyHorizontalScaleToPipe(element, info, widthRatio);

                        // 必须强制刷新,让管道的 ActualWidth 真实更新
                        element.UpdateLayout();
                    }

                    //  第二步:重新获取当前尺寸(管道缩了之后尺寸变了)
                    Size currentSize = info.CanScaleHorizontal
                        ? GetElementSize(element)
                        : info.DeviceSize;

                    //  第三步:按相对位置算新位置(原有逻辑)
                    double newX = info.Region.Left + info.RelativeX * info.Region.Width;
                    double newY = info.Region.Top + info.RelativeY * info.Region.Height;

                    // 边界夹紧
                    double maxX = Math.Max(info.Region.Left, info.Region.Right - currentSize.Width);
                    double maxY = Math.Max(info.Region.Top, info.Region.Bottom - currentSize.Height);

                    newX = Math.Max(info.Region.Left, Math.Min(maxX, newX));
                    newY = Math.Max(info.Region.Top, Math.Min(maxY, newY));

                    Canvas.SetLeft(element, newX);
                    Canvas.SetTop(element, newY);

                    element.UpdateLayout();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"更新元素位置失败 ({element.GetType().Name}): {ex.Message}");
                    Canvas.SetLeft(element, info.OriginalPosition.X);
                    Canvas.SetTop(element, info.OriginalPosition.Y);
                }
            }
        }

        private void UpdateAllConnectionsRealtime()
        {
            try
            {
                //  直接使用注入的服务
                if (_connectionService != null)
                {
                    // 启用拖拽模式以提高性能
                    _connectionService.SetDragMode(true);

                    // 更新所有连接线
                    foreach (var connection in _connectionService.Connections.Values)
                    {
                        _connectionService.UpdateConnectionPath(connection);
                    }

                    Debug.WriteLine($"实时更新了 {_connectionService.Connections.Count} 条连接线");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"更新连接线失败: {ex.Message}");
            }
        }


        private void RecordOriginalState()
        {
            Debug.WriteLine("=== 开始记录原始状态 ===");

            // 直接使用 _currentLayout 而不是从 _currentDocument 获取
            if (_currentLayout == null)
            {
                Debug.WriteLine("_currentLayout 为 null");
                return;
            }

            var layout = _currentLayout;
            Debug.WriteLine($"使用当前布局，独立区域数量: {layout.IndependentRegions?.Count ?? 0}");

            if (layout.UseIndependentRegions)
            {
                foreach (var region in layout.IndependentRegions)
                {
                    region.OriginalLeft = region.Left;
                    region.OriginalTop = region.Top;
                    region.OriginalRight = region.Right;
                    region.OriginalBottom = region.Bottom;

                    Debug.WriteLine($"区域: {region.Type}, 位置: ({region.Left:F1}, {region.Top:F1}) - ({region.Right:F1}, {region.Bottom:F1})");
                }
            }

            _originalColumnWidths = new List<double>(layout.ColumnWidths);
            _originalRowHeights = new List<double>(layout.RowHeights);

            _deviceRegionMap.Clear();
            _originalDevicePositions.Clear();

            Debug.WriteLine($"画布子元素数量: {_currentCanvas?.Children.Count ?? 0}");

            // 详细的设备扫描
            if (_currentCanvas != null)
            {
                int deviceCount = 0;
                int panelCount = 0;
                int otherCount = 0;

                int pipeCount = 0;  //  新增：管道计数

                foreach (UIElement element in _currentCanvas.Children)
                {
                    Debug.WriteLine($"检查元素: {element.GetType().Name}");

                    if (element is Border border)
                    {
                        string tag = border.Tag?.ToString() ?? "null";
                        Debug.WriteLine($"  Border Tag: {tag}");

                        bool isDevice = IsDevice(border);
                        bool isPanel = _variablePanels.Values.Contains(border);
                        bool isOverlay = ReferenceEquals(border, _regionOverlay);

                        Debug.WriteLine($"  IsDevice: {isDevice}, IsPanel: {isPanel}, IsOverlay: {isOverlay}");

                        if (isDevice)
                        {
                            deviceCount++;
                            RecordElementPosition(element, layout);
                        }
                        else if (isPanel)
                        {
                            panelCount++;
                            RecordElementPosition(element, layout);
                        }
                        else
                        {
                            otherCount++;
                        }
                    }
                    // 新增：识别所有管道控件,让它们也跟随区域移动
                    else if (IsPipeControl(element))
                    {
                        pipeCount++;
                        Debug.WriteLine($"  识别为管道控件: {element.GetType().Name}");
                        RecordElementPosition(element, layout);
                    }
                    else
                    {
                        Debug.WriteLine($"  非Border元素: {element.GetType().Name}");
                        otherCount++;
                    }
                }

                Debug.WriteLine($"统计结果 - 设备: {deviceCount}, 面板: {panelCount}, 管道: {pipeCount}, 其他: {otherCount}");

                Debug.WriteLine($"统计结果 - 设备: {deviceCount}, 面板: {panelCount}, 其他: {otherCount}");
            }

            Debug.WriteLine($"最终 _deviceRegionMap 数量: {_deviceRegionMap.Count}");
            Debug.WriteLine("=== 记录原始状态完成 ===");
        }
     
        // 新增：统一的元素位置记录方法
        private void RecordElementPosition(UIElement element, RegionLayout layout)
        {
            Debug.WriteLine($"  开始记录元素位置: {element.GetType().Name}");

            double leftValue = Canvas.GetLeft(element);
            double topValue = Canvas.GetTop(element);

            Debug.WriteLine($"    Canvas位置原始值: Left={leftValue}, Top={topValue}");

            Point elementPos = new Point(
                double.IsNaN(leftValue) ? 0 : leftValue,
                double.IsNaN(topValue) ? 0 : topValue
            );

            Size elementSize = GetElementSize(element);

            Debug.WriteLine($"    处理后位置: ({elementPos.X:F1}, {elementPos.Y:F1})");
            Debug.WriteLine($"    元素尺寸: {elementSize.Width:F1} x {elementSize.Height:F1}");

            var region = FindDeviceRegion(elementPos, elementSize, layout);
            if (region != null)
            {
                Debug.WriteLine($"    找到所在区域: {region.Type}");
                Debug.WriteLine($"    区域范围: ({region.Left:F1}, {region.Top:F1}) - ({region.Right:F1}, {region.Bottom:F1})");

                double relativeX = 0;
                double relativeY = 0;

                if (region.Width > 0)
                {
                    relativeX = (elementPos.X - region.Left) / region.Width;
                }

                if (region.Height > 0)
                {
                    relativeY = (elementPos.Y - region.Top) / region.Height;
                }

                relativeX = Math.Max(0, Math.Min(1, relativeX));
                relativeY = Math.Max(0, Math.Min(1, relativeY));

                Debug.WriteLine($"    相对位置: ({relativeX:F3}, {relativeY:F3})");

                //  构造 DeviceRegionInfo,如果是管道则额外记录长度参数
                var info = new DeviceRegionInfo
                {
                    Region = region,
                    RelativeX = relativeX,
                    RelativeY = relativeY,
                    OriginalPosition = elementPos,
                    DeviceSize = elementSize,
                    PipeType = GetPipeType(element),
                    CanScaleHorizontal = IsHorizontalPipe(element),
                    OriginalRegionWidth = region.Width
                };

                // 记录各类管道的原始长度参数
                if (info.CanScaleHorizontal)
                {
                    switch (info.PipeType)
                    {
                        case "Straight":
                            info.OriginalPipeLength = GetPropertyValue<double>(element, "PipeLength", 0);
                            break;
                        case "Elbow":
                            info.OriginalHorizontalLength = GetPropertyValue<double>(element, "HorizontalLength", 0);
                            break;
                        case "ZShape":
                            info.OriginalTopHorizontalLength = GetPropertyValue<double>(element, "TopHorizontalLength", 0);
                            info.OriginalBottomHorizontalLength = GetPropertyValue<double>(element, "BottomHorizontalLength", 0);
                            break;
                        case "TShape":
                            info.OriginalMainPipeLength = GetPropertyValue<double>(element, "MainPipeLength", 0);
                            break;
                    }
                    Debug.WriteLine($"    记录横管原始长度信息: 类型={info.PipeType}, 区域宽={info.OriginalRegionWidth:F1}");
                }

                _deviceRegionMap[element] = info;
                Debug.WriteLine($"    成功记录到 _deviceRegionMap (PipeType={info.PipeType}, CanScale={info.CanScaleHorizontal})");
                
            }
            else
            {
                Debug.WriteLine($"    未找到对应的区域");

                // 调试：列出所有可用区域
                Debug.WriteLine($"    可用区域列表:");
                if (layout.UseIndependentRegions && layout.IndependentRegions != null)
                {
                    foreach (var r in layout.IndependentRegions)
                    {
                        Debug.WriteLine($"      {r.Type}: ({r.Left:F1}, {r.Top:F1}) - ({r.Right:F1}, {r.Bottom:F1})");
                    }
                }
            }

            _originalDevicePositions[element] = elementPos;
        }

        private bool IsDeviceOrPanel(UIElement element)
        {
            if (element is Border border)
            {
                // 排除区域覆盖层
                if (ReferenceEquals(border, _regionOverlay)) return false;

                // 包含设备和参数面板
                return IsDevice(border) || _variablePanels.Values.Contains(border);
            }
            return false;
        }
        private bool IsDevice(Border border)
        {
            if (border == null)
            {
                Debug.WriteLine("    IsDevice: border 为 null");
                return false;
            }

            bool isVariablePanel = _variablePanels.Values.Contains(border);
            bool isOverlay = ReferenceEquals(border, _regionOverlay);
            bool hasTag = border.Tag != null && !string.IsNullOrEmpty(border.Tag.ToString());

            Debug.WriteLine($"    IsDevice检查: hasTag={hasTag}, isVariablePanel={isVariablePanel}, isOverlay={isOverlay}");

            // 修改判断逻辑：有Tag且不是变量面板且不是覆盖层
            bool result = hasTag && !isVariablePanel && !isOverlay;

            Debug.WriteLine($"    IsDevice结果: {result}");
            return result;
        }

        private Size GetElementSize(UIElement element)
        {
            if (element is FrameworkElement fe)
            {
                double width = double.IsNaN(fe.Width) ? fe.ActualWidth : fe.Width;
                double height = double.IsNaN(fe.Height) ? fe.ActualHeight : fe.Height;

                if (width <= 0) width = 100;
                if (height <= 0) height = 100;

                return new Size(width, height);
            }
            return new Size(100, 100);
        }

        private IndependentRegion FindDeviceRegion(Point devicePos, Size deviceSize, RegionLayout layout)
        {
            if (!layout.UseIndependentRegions) return null;

            Point deviceCenter = new Point(
                devicePos.X + deviceSize.Width / 2,
                devicePos.Y + deviceSize.Height / 2
            );

            return layout.IndependentRegions.FirstOrDefault(region => region.Contains(deviceCenter));
        }

        private Path CreateRoundedRectPath(double x, double y, double w, double h, double r)
        {
            PathFigure f = new() { StartPoint = new Point(x + r, y) };
            f.Segments.Add(new LineSegment(new Point(x + w - r, y), true));
            f.Segments.Add(new QuadraticBezierSegment(new Point(x + w, y), new Point(x + w, y + r), true));
            f.Segments.Add(new LineSegment(new Point(x + w, y + h - r), true));
            f.Segments.Add(new QuadraticBezierSegment(new Point(x + w, y + h), new Point(x + w - r, y + h), true));
            f.Segments.Add(new LineSegment(new Point(x + r, y + h), true));
            f.Segments.Add(new QuadraticBezierSegment(new Point(x, y + h), new Point(x, y + h - r), true));
            f.Segments.Add(new LineSegment(new Point(x, y + r), true));
            f.Segments.Add(new QuadraticBezierSegment(new Point(x, y), new Point(x + r, y), true));
            f.IsClosed = true;
            return new Path { Data = new PathGeometry { Figures = { f } } };
        }

        private static string RegionTypeToCN(RegionType t) => t switch
        {
            RegionType.Feed => CultureInfo.CurrentUICulture.Name == "en-US" ? "Feed" : "进料",
            RegionType.PreHeat => CultureInfo.CurrentUICulture.Name == "en-US" ? "PreHeat" : "预热/预冷",
            RegionType.Reaction => CultureInfo.CurrentUICulture.Name == "en-US" ? "Reaction" : "反应",
            RegionType.Quench => CultureInfo.CurrentUICulture.Name == "en-US" ? "Quench" : "淬灭",
            RegionType.PostProcess => CultureInfo.CurrentUICulture.Name == "en-US" ? "PostProcess" : "后处理",
            _ => CultureInfo.CurrentUICulture.Name == "en-US" ? "Undefined" : "未定义"
        };

        #endregion

        /// <summary>
        /// 新增：更新画布布局区域标题的语言
        /// </summary>
        private void LanguageChangedHandler(string s)
        {
            if (_regionOverlay == null || _regionOverlay.Children.Count < 1)
            {
                return;
            }
            var query = _regionOverlay.Children.OfType<TextBlock>();
            foreach (var item in query)
            {
                item.Text = _localizationService.GetString($"RegionSetting_RegionType_{item.Tag}");
            }
        }
    }

    #region 辅助类

    public class RegionLineInfo
    {
        public bool IsVertical { get; set; }
        public RegionBoundary Boundary { get; set; }
        public IndependentRegion AffectedIndependentRegion { get; set; }
        public double BoundaryPosition { get; set; }
        public RegionLayout Layout { get; set; }
    }

    public class DeviceRegionInfo
    {
        public IndependentRegion Region { get; set; }
        public double RelativeX { get; set; }
        public double RelativeY { get; set; }
        public Point OriginalPosition { get; set; }
        public Size DeviceSize { get; set; }

        // 新增:管道伸缩相关
        public string PipeType { get; set; }            // null=非管道, "Straight"/"Elbow"/"ZShape"/"TShape"
        public bool CanScaleHorizontal { get; set; }    // 是否为横管(0°/180°),只有横管才缩长度
        public double OriginalRegionWidth { get; set; } // 记录时区域的原始宽度(基准)

        // 各类管道的原始长度参数(只用得到的会赋值)
        public double OriginalPipeLength { get; set; }          // Straight: PipeLength
        public double OriginalHorizontalLength { get; set; }    // Elbow: HorizontalLength
        public double OriginalTopHorizontalLength { get; set; } // Z: TopHorizontalLength
        public double OriginalBottomHorizontalLength { get; set; } // Z: BottomHorizontalLength
        public double OriginalMainPipeLength { get; set; }      // T: MainPipeLength
    }

    #endregion
}