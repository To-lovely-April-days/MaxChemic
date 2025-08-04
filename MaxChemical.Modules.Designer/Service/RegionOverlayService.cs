using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MaxChemical.Core;
using MaxChemical.Modules.Designer.Services;

namespace MaxChemical.Modules.Designer.Service
{
    public class RegionOverlayService : IRegionOverlayService
    {
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

                // 🔥 关键：完全重新绘制区域覆盖层内容
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

        // 🔥 新增：绘制区域内容（线条和标签）
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
                    Margin = new Thickness(4 / currentScale)
                };

                _regionOverlay.Children.Add(textBlock);
                Canvas.SetLeft(textBlock, actualLeft + 4 / currentScale);
                Canvas.SetTop(textBlock, actualTop + 2 / currentScale);
            }
        }

        // 🔥 新增：重新绘制区域覆盖层内容（拖动时调用）
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
                string leftKey = $"V_{region.Left:F1}";
                if (!addedBoundaries.Contains(leftKey))
                {
                    AddIndependentRegionDragArea(
                        region.Left, region.Top, dragAreaWidth, region.Height,
                        true, region, RegionBoundary.Left, layout);
                    addedBoundaries.Add(leftKey);
                }

                // 右边界
                string rightKey = $"V_{region.Right:F1}";
                if (!addedBoundaries.Contains(rightKey))
                {
                    AddIndependentRegionDragArea(
                        region.Right, region.Top, dragAreaWidth, region.Height,
                        true, region, RegionBoundary.Right, layout);
                    addedBoundaries.Add(rightKey);
                }

                // 上边界
                string topKey = $"H_{region.Top:F1}";
                if (!addedBoundaries.Contains(topKey))
                {
                    AddIndependentRegionDragArea(
                        region.Left, region.Top, region.Width, dragAreaWidth,
                        false, region, RegionBoundary.Top, layout);
                    addedBoundaries.Add(topKey);
                }

                // 下边界
                string bottomKey = $"H_{region.Bottom:F1}";
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
                        // 🔥 恢复连接线正常模式
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
                    // 改进：基于相对位置重新计算设备位置
                    double newX = info.Region.Left + info.RelativeX * info.Region.Width;
                    double newY = info.Region.Top + info.RelativeY * info.Region.Height;

                    // 改进：确保设备完全在区域内
                    double deviceWidth = info.DeviceSize.Width;
                    double deviceHeight = info.DeviceSize.Height;

                    // 边界检查，确保设备不超出区域
                    double maxX = Math.Max(info.Region.Left, info.Region.Right - deviceWidth);
                    double maxY = Math.Max(info.Region.Top, info.Region.Bottom - deviceHeight);

                    newX = Math.Max(info.Region.Left, Math.Min(maxX, newX));
                    newY = Math.Max(info.Region.Top, Math.Min(maxY, newY));

                    // 调试信息
                    Debug.WriteLine($"更新设备位置: {element.GetType().Name}");
                    Debug.WriteLine($"  区域: ({info.Region.Left:F1}, {info.Region.Top:F1}) - ({info.Region.Right:F1}, {info.Region.Bottom:F1})");
                    Debug.WriteLine($"  相对位置: ({info.RelativeX:F3}, {info.RelativeY:F3})");
                    Debug.WriteLine($"  新位置: ({newX:F1}, {newY:F1})");

                    Canvas.SetLeft(element, newX);
                    Canvas.SetTop(element, newY);

                    // 强制更新布局
                    element.UpdateLayout();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"更新设备位置失败: {ex.Message}");
                    // 回退到原始位置
                    Canvas.SetLeft(element, info.OriginalPosition.X);
                    Canvas.SetTop(element, info.OriginalPosition.Y);
                }
            }
        }

        private void UpdateAllConnectionsRealtime()
        {
            try
            {
                // 🔥 直接使用注入的服务
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
                    else
                    {
                        Debug.WriteLine($"  非Border元素: {element.GetType().Name}");
                        otherCount++;
                    }
                }

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

                _deviceRegionMap[element] = new DeviceRegionInfo
                {
                    Region = region,
                    RelativeX = relativeX,
                    RelativeY = relativeY,
                    OriginalPosition = elementPos,
                    DeviceSize = elementSize
                };

                Debug.WriteLine($"    成功记录到 _deviceRegionMap");
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
            RegionType.Feed => "进料",
            RegionType.PreHeat => "预热/预冷",
            RegionType.Reaction => "反应",
            RegionType.Quench => "淬灭",
            RegionType.PostProcess => "后处理",
            _ => "未定义"
        };

        #endregion
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
    }

    #endregion
}