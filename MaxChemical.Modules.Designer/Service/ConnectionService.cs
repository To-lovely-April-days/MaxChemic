using MaxChemical.Modules.Designer.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MaxChemical.Modules.Designer.Services
{
    /// <summary>
    /// 优化的连接服务 - 拖拽路径与最终路径保持一致
    /// </summary>
    public class ConnectionService : IConnectionService
    {
        #region 私有字段

        private readonly Dictionary<string, ConnectionInfo> _connections;
        private readonly Dictionary<string, List<ConnectionPoint>> _deviceConnectionPoints;
        private readonly Dictionary<string, List<FrameworkElement>> _deviceConnectionPointElements;
        private ConnectionInfo _pendingConnection;
        private readonly Color SlateGray = Color.FromRgb(112, 128, 144);
        private bool _isMonitorMode = false;

        // 🔥 拖拽模式标记
        private bool _isDragMode = false;

        // 🔥 性能优化缓存 - 拖拽时使用轻量级缓存
        private readonly Dictionary<string, List<Rect>> _obstacleCache = new Dictionary<string, List<Rect>>();
        private readonly Dictionary<string, PathGeometry> _pathCache = new Dictionary<string, PathGeometry>();
        private readonly Dictionary<string, List<Point>> _dragPathCache = new Dictionary<string, List<Point>>(); // 🔥 新增：拖拽路径缓存
        private DateTime _lastObstacleUpdate = DateTime.MinValue;
        private DateTime _lastDragObstacleUpdate = DateTime.MinValue; // 🔥 新增：拖拽障碍物更新时间
        private const int ObstacleCacheLifetime = 150; // 毫秒
        private const int DragObstacleCacheLifetime = 50; // 🔥 拖拽模式更短的缓存时间

        // 指引线相关字段
        private Path _guideLine;
        private Point _currentMousePosition;
        private bool _isTrackingMouse = false;

        // 防抖
        private DateTime _lastGuideUpdate = DateTime.MinValue;
        private const int GuideUpdateInterval = 32; // 降低到30FPS以提升性能

        #endregion

        #region 公共属性和事件

        public bool IsConnectionMode { get; private set; }
        public Dictionary<string, ConnectionInfo> Connections => _connections;

        // 画布访问委托
        public Func<Canvas> GetCanvasFunc { get; set; }
        public Func<string, FrameworkElement> FindDeviceElementFunc { get; set; }
        public event Action<ConnectionInfo> ConnectionCreated;
        public event Action<string> ConnectionDeleted;

        // 添加显示所有设备连接点的委托
        public Action ShowAllDeviceConnectionPointsAction { get; set; }

        #endregion

        #region 构造函数

        public ConnectionService()
        {
            _connections = new Dictionary<string, ConnectionInfo>();
            _deviceConnectionPoints = new Dictionary<string, List<ConnectionPoint>>();
            _deviceConnectionPointElements = new Dictionary<string, List<FrameworkElement>>();
        }

        #endregion

        #region 拖拽模式管理

        /// <summary>
        /// 设置拖拽模式
        /// </summary>
        public void SetDragMode(bool isDragMode)
        {
            if (_isDragMode != isDragMode)
            {
                _isDragMode = isDragMode;

                if (_isDragMode)
                {
                    // 🔥 进入拖拽模式时，清理普通缓存，准备拖拽缓存
                    _pathCache.Clear();
                    _dragPathCache.Clear();
                    LogDebug("进入拖拽模式，启用高速路径算法（与最终路径一致）");
                }
                else
                {
                    // 🔥 退出拖拽模式时，清理拖拽缓存，重新计算所有连接以确保精确度
                    _dragPathCache.Clear();
                    LogDebug("退出拖拽模式，恢复完整精度路径算法");
                    RefreshAllConnectionsAsync();
                }
            }
        }

        /// <summary>
        /// 异步刷新所有连接
        /// </summary>
        private void RefreshAllConnectionsAsync()
        {
            var connectionsToUpdate = _connections.Values.ToList();

            // 分批更新，避免阻塞UI
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                foreach (var connection in connectionsToUpdate)
                {
                    UpdateConnectionPath(connection);
                }
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// 条件日志输出
        /// </summary>
        private void LogDebug(string message)
        {
            if (!_isDragMode) // 只在非拖拽模式输出日志
            {
                Debug.WriteLine($"[ConnectionService] {message}");
            }
        }

        #endregion

        #region 画布和设备查找

        /// <summary>
        /// 获取画布的安全方法
        /// </summary>
        private Canvas GetCanvas()
        {
            var canvas = GetCanvasFunc?.Invoke();
            if (canvas == null)
            {
                LogDebug("警告：无法获取画布引用");
            }
            return canvas;
        }

        /// <summary>
        /// 查找设备元素
        /// </summary>
        private FrameworkElement FindDeviceElement(string deviceId)
        {
            if (FindDeviceElementFunc != null)
            {
                var element = FindDeviceElementFunc(deviceId);
                LogDebug($"通过委托查找设备 {deviceId}: {(element != null ? "找到" : "未找到")}");
                return element;
            }
            LogDebug($"设备查找委托未设置，无法查找设备: {deviceId}");
            return null;
        }

        #endregion

        #region 连接管理核心方法

        /// <summary>
        /// 开始连接
        /// </summary>
        public void StartConnection(string startDeviceId, ConnectionDirection direction)
        {
            IsConnectionMode = true;

            _pendingConnection = new ConnectionInfo
            {
                Id = Guid.NewGuid().ToString(),
                StartDeviceId = startDeviceId,
                DirectionMode = ConnectionDirectionMode.Auto
            };

            // 记录用户选择的起始连接方向
            _pendingConnection.StartAnchor = new ConnectionPoint
            {
                Direction = direction,
                DeviceId = startDeviceId
            };

            // 创建指引线
            CreateGuideLine();

            // 启用鼠标跟踪
            _isTrackingMouse = true;

            LogDebug($"开始连接模式，起始设备: {startDeviceId}, 固定方向: {direction}");
            ShowAllConnectionPoints();
            ShowAllDeviceConnectionPointsAction?.Invoke();
        }

        /// <summary>
        /// 完成连接
        /// </summary>
        public void CompleteConnection(string endDeviceId, ConnectionDirection direction)
        {
            if (_pendingConnection == null || _pendingConnection.StartDeviceId == endDeviceId)
            {
                CancelConnection();
                return;
            }

            var connection = CreateConnection(
                _pendingConnection.StartDeviceId,
                endDeviceId,
                _pendingConnection.StartAnchor.Direction,
                direction
            );

            if (connection != null)
            {
                _connections[connection.Id] = connection;
                UpdateConnectionPath(connection);

                // 触发事件，让 ProcessDesignerView 用命令记录
                ConnectionCreated?.Invoke(connection);
            }

            CancelConnection();
        }

        /// <summary>
        /// 取消连接
        /// </summary>
        public void CancelConnection()
        {
            IsConnectionMode = false;
            _isTrackingMouse = false;
            _pendingConnection = null;

            // 移除指引线
            RemoveGuideLine();

            HideAllConnectionPoints();
            LogDebug("连接模式已取消，指引线已移除");
        }

        /// <summary>
        /// 创建连接
        /// </summary>
        public ConnectionInfo CreateConnection(string startDeviceId, string endDeviceId,
            ConnectionDirection startDirection, ConnectionDirection endDirection)
        {
            var startDevice = FindDeviceElement(startDeviceId);
            var endDevice = FindDeviceElement(endDeviceId);

            if (startDevice == null || endDevice == null)
            {
                LogDebug($"无法找到设备元素: Start={startDevice != null}, End={endDevice != null}");
                return null;
            }

            var connection = new ConnectionInfo
            {
                Id = Guid.NewGuid().ToString(),
                StartDeviceId = startDeviceId,
                EndDeviceId = endDeviceId,
                StartDevice = startDevice,
                EndDevice = endDevice,
                DirectionMode = ConnectionDirectionMode.Auto
            };

            connection.StartAnchor = CreateConnectionPoint(startDeviceId, startDirection, startDevice);
            connection.EndAnchor = CreateConnectionPoint(endDeviceId, endDirection, endDevice);
            Connections[connection.Id] = connection;
            CreateConnectionVisuals(connection);

            return connection;
        }

        /// <summary>
        /// 更新连接
        /// </summary>
        public void UpdateConnection(ConnectionInfo connection)
        {
            UpdateConnectionPath(connection);
        }

        /// <summary>
        /// 移除连接
        /// </summary>
        public void RemoveConnection(string connectionId)
        {
            if (_connections.TryGetValue(connectionId, out var connection))
            {
                var canvas = GetCanvas();
                if (canvas != null)
                {
                    // 移除视觉元素
                    if (connection.ConnectionPath != null)
                        canvas.Children.Remove(connection.ConnectionPath);
                    if (connection.ArrowPath != null)
                        canvas.Children.Remove(connection.ArrowPath);
                    if (connection.ConnectionLabel != null)
                        canvas.Children.Remove(connection.ConnectionLabel);
                    if (connection.CornerControl != null)
                        canvas.Children.Remove(connection.CornerControl);
                }

                // 清理相关缓存
                CleanupConnectionCache(connectionId);

                _connections.Remove(connectionId);
                ConnectionDeleted?.Invoke(connectionId);
                LogDebug($"连接已删除: {connectionId}");
            }
        }

        /// <summary>
        /// 移除选中的连接
        /// </summary>
        public void RemoveSelectedConnections()
        {
            var selectedConnections = _connections.Values.Where(c => c.IsSelected).ToList();
            foreach (var connection in selectedConnections)
            {
                RemoveConnection(connection.Id);
            }
            LogDebug($"已删除 {selectedConnections.Count} 个选中的连接");
        }

        #endregion

        #region 鼠标位置更新

        /// <summary>
        /// 更新鼠标位置
        /// </summary>
        public void UpdateMousePosition(Point mousePosition)
        {
            _currentMousePosition = mousePosition;

            if (_isTrackingMouse && _pendingConnection != null)
            {
                var now = DateTime.Now;
                if ((now - _lastGuideUpdate).TotalMilliseconds >= GuideUpdateInterval)
                {
                    UpdateGuideLine();
                    _lastGuideUpdate = now;
                }
            }
        }

        #endregion

        #region 指引线功能 - 与实际路径完全一致

        /// <summary>
        /// 创建指引线 - 确保样式与连接线区分
        /// </summary>
        private void CreateGuideLine()
        {
            var canvas = GetCanvas();
            if (canvas == null) return;

            // 移除现有指引线
            RemoveGuideLine();

            // 创建新的指引线 - 使用虚线样式以区分实际连接线
            _guideLine = new Path
            {
                Stroke = new SolidColorBrush(Color.FromRgb(100, 149, 237)), // 使用蓝色以区分
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 8, 4 }, // 虚线样式
                Fill = Brushes.Transparent,
                IsHitTestVisible = false,
                Opacity = 0.8
            };

            canvas.Children.Add(_guideLine);
            Panel.SetZIndex(_guideLine, 950); // 确保在连接点下方，但在设备上方

            LogDebug("指引线已创建");
        }

        /// <summary>
        /// 移除指引线
        /// </summary>
        private void RemoveGuideLine()
        {
            if (_guideLine != null)
            {
                var canvas = GetCanvas();
                if (canvas != null)
                {
                    canvas.Children.Remove(_guideLine);
                }
                _guideLine = null;
                LogDebug("指引线已移除");
            }
        }

        /// <summary>
        /// 更新指引线 - 优化版本
        /// </summary>
        private void UpdateGuideLine()
        {
            if (_guideLine == null || _pendingConnection == null)
            {
                return;
            }

            var startDevice = FindDeviceElement(_pendingConnection.StartDeviceId);
            if (startDevice == null)
            {
                return;
            }

            try
            {
                // 🔥 关键：使用与实际连接线完全相同的路径计算逻辑
                PathGeometry pathGeometry = CalculateGuideLinePath(startDevice, _currentMousePosition);
                _guideLine.Data = pathGeometry;

                LogDebug($"指引线路径已更新，使用与实际连接线相同的算法");
            }
            catch (Exception ex)
            {
                LogDebug($"更新指引线失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 计算指引线路径 - 使用与实际连接线相同的算法
        /// </summary>
        private PathGeometry CalculateGuideLinePath(FrameworkElement startDevice, Point mousePosition)
        {
            try
            {
                // 获取起始设备的精确连接点位置（与实际连接线相同）
                Point startPoint = GetTransformedConnectionPointPosition(startDevice, _pendingConnection.StartAnchor.Direction);

                LogDebug($"指引线起点: ({startPoint.X:F1}, {startPoint.Y:F1}), 鼠标位置: ({mousePosition.X:F1}, {mousePosition.Y:F1})");

                // 🔥 关键：使用与实际连接线完全相同的路径算法
                if (_isDragMode)
                {
                    // 🔥 拖拽模式：使用高速但与最终路径一致的算法
                    return CalculateGuideLineDragMode(startPoint, mousePosition);
                }
                else
                {
                    // 正常模式：使用完整算法
                    return CalculateGuideLineNormal(startPoint, mousePosition);
                }
            }
            catch (Exception ex)
            {
                LogDebug($"计算指引线路径失败: {ex.Message}");
                // 回退到简单直线
                return CreateSimpleGuideLine(startDevice, mousePosition);
            }
        }

        /// <summary>
        /// 🔥 优化：拖拽模式的指引线路径 - 与最终路径保持一致但更高效
        /// </summary>
        private PathGeometry CalculateGuideLineDragMode(Point startPoint, Point mousePosition)
        {
            PathGeometry pathGeometry = new PathGeometry();
            PathFigure pathFigure = new PathFigure();
            pathFigure.StartPoint = startPoint;

            // 🔥 使用与实际连接线相同的方向和参数
            ConnectionDirection startDirection = _pendingConnection.StartAnchor.Direction;

            // 模拟终点方向（根据鼠标位置推测最合适的方向）
            ConnectionDirection simulatedEndDirection = CalculateOptimalEndDirection(startPoint, mousePosition, startDirection);

            try
            {
                // 🔥 关键：使用与完整算法相同的逻辑，但使用缓存和简化版障碍物检测
                List<Point> pathPoints = CalculateDragModePathPoints(startPoint, startDirection, mousePosition, simulatedEndDirection);

                // 🔥 应用与实际连接线相同的圆角处理（简化版）
                CreateOptimizedRoundedPath(pathFigure, startPoint, pathPoints, mousePosition, true); // true表示简化模式

                pathGeometry.Figures.Add(pathFigure);
                return pathGeometry;
            }
            catch (Exception ex)
            {
                LogDebug($"拖拽模式指引线计算失败: {ex.Message}，回退到简单模式");
                return CreateSimpleDragGuidePath(startPoint, mousePosition, startDirection);
            }
        }

        /// <summary>
        /// 🔥 新增：拖拽模式路径点计算 - 与最终算法一致但更高效
        /// </summary>
        private List<Point> CalculateDragModePathPoints(Point startPoint, ConnectionDirection startDir,
                                                       Point mousePosition, ConnectionDirection endDir)
        {
            // 🔥 生成拖拽缓存键
            var cacheKey = $"drag_{startPoint.X:F0},{startPoint.Y:F0}|{mousePosition.X:F0},{mousePosition.Y:F0}|{startDir}|{endDir}";

            // 🔥 检查拖拽缓存
            if (_dragPathCache.TryGetValue(cacheKey, out var cachedPath))
            {
                return cachedPath;
            }

            double extension = 20;

            Point startExt = GetExtensionPoint(startPoint, startDir, extension);
            Point endEntry = GetExtensionPoint(mousePosition, GetOppositeDirection(endDir), extension);

            LogDebug($"拖拽模式路径计算: start({startExt.X:F1}, {startExt.Y:F1}), end({endEntry.X:F1}, {endEntry.Y:F1})");

            // 🔥 使用快速但一致的障碍物检测
            List<Rect> obstacles = GetDragModeObstacles(startExt, endEntry);
            LogDebug($"拖拽模式检测到 {obstacles.Count} 个障碍物");

            // 🔥 使用与最终算法相同的路径调整，但参数简化
            Point adjustedStartExt = QuickAdjustPoint(startExt, startDir, obstacles);
            Point adjustedEndEntry = QuickAdjustPoint(endEntry, endDir, obstacles);

            if (ArePointsTooClose(adjustedStartExt, adjustedEndEntry, 10.0))
            {
                LogDebug("拖拽模式点过于接近，返回简单路径");
                var simplePath = new List<Point> { adjustedStartExt };

                // 🔥 缓存简单路径
                if (_dragPathCache.Count < 50) // 限制拖拽缓存大小
                {
                    _dragPathCache[cacheKey] = simplePath;
                }

                return simplePath;
            }

            List<Point> pathPoints = new List<Point>();
            pathPoints.Add(adjustedStartExt);

            // 🔥 使用与最终算法相同但简化的中间路径计算
            List<Point> middlePoints = CalculateDragMiddlePath(adjustedStartExt, adjustedEndEntry, obstacles);
            pathPoints.AddRange(middlePoints);

            pathPoints.Add(adjustedEndEntry);

            // 🔥 使用与最终算法相同的路径优化
            pathPoints = RemoveDuplicatePoints(pathPoints, 3.0);
            pathPoints = ToOrthogonalPath(pathPoints);

            LogDebug($"拖拽模式最终路径包含 {pathPoints.Count} 个点");

            // 🔥 缓存结果
            if (_dragPathCache.Count < 50)
            {
                _dragPathCache[cacheKey] = pathPoints;
            }

            return pathPoints;
        }

        /// <summary>
        /// 🔥 新增：拖拽模式快速障碍物检测
        /// </summary>
        private List<Rect> GetDragModeObstacles(Point start, Point end)
        {
            var now = DateTime.Now;
            var cacheKey = $"drag_obstacles_{start.X:F0}_{start.Y:F0}_{end.X:F0}_{end.Y:F0}";

            // 检查拖拽专用缓存
            if (_obstacleCache.TryGetValue(cacheKey, out var cachedObstacles) &&
                (now - _lastDragObstacleUpdate).TotalMilliseconds < DragObstacleCacheLifetime)
            {
                return cachedObstacles;
            }

            // 🔥 优化：更小的搜索范围用于拖拽
            List<Rect> obstacles = new List<Rect>();

            try
            {
                // 计算更紧凑的搜索区域
                double margin = 60; // 进一步减少搜索范围
                double minX = Math.Min(start.X, end.X) - margin;
                double minY = Math.Min(start.Y, end.Y) - margin;
                double maxX = Math.Max(start.X, end.X) + margin;
                double maxY = Math.Max(start.Y, end.Y) + margin;

                Rect pathBounds = new Rect(minX, minY, maxX - minX, maxY - minY);

                var canvas = GetCanvas();
                if (canvas != null)
                {
                    // 🔥 快速过滤：只检查主要障碍物
                    foreach (UIElement element in canvas.Children)
                    {
                        if (element is Border deviceBorder && IsDevice(deviceBorder))
                        {
                            Rect? iconRect = GetDeviceIconBounds(deviceBorder);
                            if (iconRect.HasValue && pathBounds.IntersectsWith(iconRect.Value))
                            {
                                // 🔥 减少扩展边距以提高性能
                                Rect expandedRect = new Rect(
                                    iconRect.Value.X - 10,
                                    iconRect.Value.Y - 10,
                                    iconRect.Value.Width + 20,
                                    iconRect.Value.Height + 20
                                );
                                obstacles.Add(expandedRect);
                            }
                        }
                    }
                }

                LogDebug($"拖拽模式障碍物检测: 找到 {obstacles.Count} 个障碍物");
            }
            catch (Exception ex)
            {
                LogDebug($"拖拽模式获取障碍物时出错: {ex.Message}");
            }

            // 更新拖拽缓存
            _obstacleCache[cacheKey] = obstacles;
            _lastDragObstacleUpdate = now;

            // 🔥 清理拖拽缓存（更激进的清理）
            if (_obstacleCache.Count > 20)
            {
                var oldKeys = _obstacleCache.Keys.Where(k => k.StartsWith("drag_")).Take(10).ToList();
                foreach (var key in oldKeys)
                {
                    _obstacleCache.Remove(key);
                }
            }

            return obstacles;
        }

        /// <summary>
        /// 🔥 新增：快速点调整（简化版）
        /// </summary>
        private Point QuickAdjustPoint(Point point, ConnectionDirection direction, List<Rect> obstacles)
        {
            Point adjustedPoint = point;

            // 🔥 只检查最明显的冲突
            foreach (var obstacle in obstacles.Take(3)) // 只检查前3个障碍物
            {
                double safetyMargin = 12.0; // 稍微减小安全边距
                Rect safeZone = new Rect(
                    obstacle.X - safetyMargin,
                    obstacle.Y - safetyMargin,
                    obstacle.Width + 2 * safetyMargin,
                    obstacle.Height + 2 * safetyMargin
                );

                if (safeZone.Contains(point))
                {
                    switch (direction)
                    {
                        case ConnectionDirection.Top:
                            adjustedPoint = new Point(point.X, obstacle.Top - safetyMargin);
                            break;
                        case ConnectionDirection.Bottom:
                            adjustedPoint = new Point(point.X, obstacle.Bottom + safetyMargin);
                            break;
                        case ConnectionDirection.Left:
                            adjustedPoint = new Point(obstacle.Left - safetyMargin, point.Y);
                            break;
                        case ConnectionDirection.Right:
                            adjustedPoint = new Point(obstacle.Right + safetyMargin, point.Y);
                            break;
                    }
                    break; // 只调整一次
                }
            }

            return adjustedPoint;
        }

        /// <summary>
        /// 🔥 新增：拖拽模式中间路径计算
        /// </summary>
        private List<Point> CalculateDragMiddlePath(Point startExt, Point endEntry, List<Rect> obstacles)
        {
            List<Point> middlePoints = new List<Point>();

            Point horizontalCorner = new Point(endEntry.X, startExt.Y);
            Point verticalCorner = new Point(startExt.X, endEntry.Y);

            // 🔥 快速路径检查（简化版）
            bool horizontalPathClear = !HasQuickObstacles(startExt, horizontalCorner, obstacles) &&
                                      !HasQuickObstacles(horizontalCorner, endEntry, obstacles);

            bool verticalPathClear = !HasQuickObstacles(startExt, verticalCorner, obstacles) &&
                                    !HasQuickObstacles(verticalCorner, endEntry, obstacles);

            if (horizontalPathClear && verticalPathClear)
            {
                // 选择更短的路径
                double horizontalDistance = Math.Abs(endEntry.X - startExt.X) + Math.Abs(endEntry.Y - startExt.Y);
                double verticalDistance = Math.Abs(startExt.X - endEntry.X) + Math.Abs(endEntry.Y - startExt.Y);

                if (horizontalDistance <= verticalDistance)
                {
                    middlePoints.Add(horizontalCorner);
                }
                else
                {
                    middlePoints.Add(verticalCorner);
                }
            }
            else if (horizontalPathClear)
            {
                middlePoints.Add(horizontalCorner);
            }
            else if (verticalPathClear)
            {
                middlePoints.Add(verticalCorner);
            }
            else
            {
                // 🔥 使用简化的绕行策略
                middlePoints.AddRange(CreateQuickBypassPath(startExt, endEntry, obstacles));
            }

            return middlePoints;
        }

        /// <summary>
        /// 🔥 新增：快速障碍物检查
        /// </summary>
        private bool HasQuickObstacles(Point start, Point end, List<Rect> obstacles)
        {
            // 🔥 快速边界框检查
            double minX = Math.Min(start.X, end.X) - 5;
            double maxX = Math.Max(start.X, end.X) + 5;
            double minY = Math.Min(start.Y, end.Y) - 5;
            double maxY = Math.Max(start.Y, end.Y) + 5;

            foreach (var obstacle in obstacles.Take(5)) // 只检查前5个障碍物
            {
                // 快速边界框排除
                if (obstacle.Right < minX || obstacle.Left > maxX ||
                    obstacle.Bottom < minY || obstacle.Top > maxY)
                    continue;

                // 简化的相交检查
                if (QuickLineIntersectsRect(start, end, obstacle))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// 🔥 新增：快速线矩形相交检查
        /// </summary>
        private bool QuickLineIntersectsRect(Point start, Point end, Rect rect)
        {
            // 🔥 简化版相交检查，只做基本判断
            if (rect.Contains(start) || rect.Contains(end))
                return true;

            // 简单的边界检查
            if ((start.X < rect.Left && end.X < rect.Left) ||
                (start.X > rect.Right && end.X > rect.Right) ||
                (start.Y < rect.Top && end.Y < rect.Top) ||
                (start.Y > rect.Bottom && end.Y > rect.Bottom))
                return false;

            return true; // 可能相交，保守估计
        }

        /// <summary>
        /// 🔥 新增：快速绕行路径
        /// </summary>
        private List<Point> CreateQuickBypassPath(Point start, Point end, List<Rect> obstacles)
        {
            if (obstacles.Count == 0)
                return new List<Point> { new Point(end.X, start.Y) };

            // 🔥 选择最简单的绕行方向
            var firstObstacle = obstacles[0];
            double margin = 30;

            if (Math.Abs(end.X - start.X) > Math.Abs(end.Y - start.Y))
            {
                // 水平距离更大，垂直绕行
                double bypassY = end.Y > start.Y ? firstObstacle.Bottom + margin : firstObstacle.Top - margin;
                return new List<Point>
                {
                    new Point(start.X, bypassY),
                    new Point(end.X, bypassY)
                };
            }
            else
            {
                // 垂直距离更大，水平绕行
                double bypassX = end.X > start.X ? firstObstacle.Right + margin : firstObstacle.Left - margin;
                return new List<Point>
                {
                    new Point(bypassX, start.Y),
                    new Point(bypassX, end.Y)
                };
            }
        }

        /// <summary>
        /// 🔥 新增：简化的拖拽指引线路径
        /// </summary>
        private PathGeometry CreateSimpleDragGuidePath(Point startPoint, Point mousePosition, ConnectionDirection startDirection)
        {
            PathGeometry pathGeometry = new PathGeometry();
            PathFigure pathFigure = new PathFigure();
            pathFigure.StartPoint = startPoint;

            // 添加延伸段
            double extension = 20;
            Point extendedStart = GetExtensionPoint(startPoint, startDirection, extension);
            pathFigure.Segments.Add(new LineSegment(extendedStart, true));

            // 计算转折点
            Point corner = CalculateSimpleDragCorner(extendedStart, mousePosition, startDirection);
            pathFigure.Segments.Add(new LineSegment(corner, true));

            // 最终段到鼠标位置
            pathFigure.Segments.Add(new LineSegment(mousePosition, true));

            pathGeometry.Figures.Add(pathFigure);
            return pathGeometry;
        }

        /// <summary>
        /// 正常模式的指引线路径 - 与完整连接线算法一致
        /// </summary>
        private PathGeometry CalculateGuideLineNormal(Point startPoint, Point mousePosition)
        {
            PathGeometry pathGeometry = new PathGeometry();
            PathFigure pathFigure = new PathFigure();
            pathFigure.StartPoint = startPoint;

            // 使用与实际连接线相同的参数和逻辑
            ConnectionDirection startDirection = _pendingConnection.StartAnchor.Direction;

            // 模拟终点方向（根据鼠标位置推测最合适的方向）
            ConnectionDirection simulatedEndDirection = CalculateOptimalEndDirection(startPoint, mousePosition, startDirection);

            LogDebug($"指引线方向: 起点={startDirection}, 模拟终点={simulatedEndDirection}");

            try
            {
                // 使用与 CreateOptimizedAutoPath 相同的逻辑
                List<Point> pathPoints = CalculateGuideLinePathPoints(startPoint, startDirection, mousePosition, simulatedEndDirection);

                // 应用与实际连接线相同的圆角处理
                CreateOptimizedRoundedPath(pathFigure, startPoint, pathPoints, mousePosition);

                pathGeometry.Figures.Add(pathFigure);
                return pathGeometry;
            }
            catch (Exception ex)
            {
                LogDebug($"正常模式指引线计算失败: {ex.Message}，回退到简单模式");
                return CalculateGuideLineDragMode(startPoint, mousePosition);
            }
        }

        /// <summary>
        /// 计算指引线的路径点 - 使用与实际连接线相同的算法
        /// </summary>
        private List<Point> CalculateGuideLinePathPoints(Point startPoint, ConnectionDirection startDir,
                                                        Point mousePosition, ConnectionDirection endDir)
        {
            double extension = 20;

            Point startExt = GetExtensionPoint(startPoint, startDir, extension);
            Point endEntry = GetExtensionPoint(mousePosition, GetOppositeDirection(endDir), extension);

            LogDebug($"指引线延伸点: start({startExt.X:F1}, {startExt.Y:F1}), end({endEntry.X:F1}, {endEntry.Y:F1})");

            // 使用与实际连接线相同的障碍物检测
            List<Rect> obstacles = GetOptimizedObstacles(startExt, endEntry);
            LogDebug($"指引线检测到 {obstacles.Count} 个障碍物");

            // 使用与实际连接线相同的路径调整
            Point adjustedStartExt = SmartAdjustPoint(startExt, startDir, obstacles, "指引线起点");
            Point adjustedEndEntry = SmartAdjustPoint(endEntry, endDir, obstacles, "指引线终点");

            if (ArePointsTooClose(adjustedStartExt, adjustedEndEntry, 10.0))
            {
                LogDebug("指引线点过于接近，返回简单路径");
                return new List<Point> { adjustedStartExt };
            }

            List<Point> pathPoints = new List<Point>();
            pathPoints.Add(adjustedStartExt);

            // 使用与实际连接线相同的中间路径计算
            List<Point> middlePoints = CalculateSimplifiedMiddlePath(adjustedStartExt, adjustedEndEntry, obstacles);
            pathPoints.AddRange(middlePoints);

            pathPoints.Add(adjustedEndEntry);

            // 使用与实际连接线相同的路径优化
            pathPoints = RemoveDuplicatePoints(pathPoints, 3.0);
            pathPoints = ToOrthogonalPath(pathPoints);

            LogDebug($"指引线最终路径包含 {pathPoints.Count} 个点");

            return pathPoints;
        }

        /// <summary>
        /// 根据鼠标位置和起始方向计算最优的终点方向
        /// </summary>
        private ConnectionDirection CalculateOptimalEndDirection(Point startPoint, Point mousePosition, ConnectionDirection startDirection)
        {
            // 计算鼠标相对于起点的方向
            double deltaX = mousePosition.X - startPoint.X;
            double deltaY = mousePosition.Y - startPoint.Y;

            // 根据起始方向和相对位置，选择最合适的终点方向
            switch (startDirection)
            {
                case ConnectionDirection.Right:
                    // 起点向右，终点优先选择左侧，其次根据Y方向
                    if (Math.Abs(deltaY) > Math.Abs(deltaX) * 0.5)
                    {
                        return deltaY > 0 ? ConnectionDirection.Top : ConnectionDirection.Bottom;
                    }
                    return ConnectionDirection.Left;

                case ConnectionDirection.Left:
                    // 起点向左，终点优先选择右侧，其次根据Y方向
                    if (Math.Abs(deltaY) > Math.Abs(deltaX) * 0.5)
                    {
                        return deltaY > 0 ? ConnectionDirection.Top : ConnectionDirection.Bottom;
                    }
                    return ConnectionDirection.Right;

                case ConnectionDirection.Bottom:
                    // 起点向下，终点优先选择上侧，其次根据X方向
                    if (Math.Abs(deltaX) > Math.Abs(deltaY) * 0.5)
                    {
                        return deltaX > 0 ? ConnectionDirection.Left : ConnectionDirection.Right;
                    }
                    return ConnectionDirection.Top;

                case ConnectionDirection.Top:
                    // 起点向上，终点优先选择下侧，其次根据X方向
                    if (Math.Abs(deltaX) > Math.Abs(deltaY) * 0.5)
                    {
                        return deltaX > 0 ? ConnectionDirection.Left : ConnectionDirection.Right;
                    }
                    return ConnectionDirection.Bottom;

                default:
                    // 默认情况，根据主要偏移方向选择
                    if (Math.Abs(deltaX) > Math.Abs(deltaY))
                    {
                        return deltaX > 0 ? ConnectionDirection.Left : ConnectionDirection.Right;
                    }
                    else
                    {
                        return deltaY > 0 ? ConnectionDirection.Top : ConnectionDirection.Bottom;
                    }
            }
        }

        /// <summary>
        /// 回退的简单指引线 - 保持与旧版本兼容
        /// </summary>
        private PathGeometry CreateSimpleGuideLine(FrameworkElement startDevice, Point mousePosition)
        {
            // 获取起始设备的位置和尺寸
            var deviceLeft = Canvas.GetLeft(startDevice);
            var deviceTop = Canvas.GetTop(startDevice);
            var deviceWidth = startDevice.ActualWidth > 0 ? startDevice.ActualWidth : 80;
            var deviceHeight = startDevice.ActualHeight > 0 ? startDevice.ActualHeight : 60;

            // 计算起始设备中心点
            Point deviceCenter = new Point(
                deviceLeft + deviceWidth / 2,
                deviceTop + deviceHeight / 2
            );

            // 使用用户选择的固定连接方向
            ConnectionDirection startDirection = _pendingConnection.StartAnchor.Direction;

            // 根据固定方向计算起始锚点位置
            Point startPoint = CalculateFixedAnchorPoint(deviceCenter, startDirection, deviceWidth, deviceHeight);

            // 创建简单的L形路径
            return CreateSimpleGuidePath(startPoint, mousePosition, startDirection);
        }

        /// <summary>
        /// 创建简单的L形指引线路径
        /// </summary>
        private PathGeometry CreateSimpleGuidePath(Point startPoint, Point mousePosition, ConnectionDirection startDirection)
        {
            PathGeometry pathGeometry = new PathGeometry();
            PathFigure pathFigure = new PathFigure();
            pathFigure.StartPoint = startPoint;

            // 添加简单的延伸段
            double extension = 20;
            Point extendedStart = GetExtensionPoint(startPoint, startDirection, extension);
            pathFigure.Segments.Add(new LineSegment(extendedStart, true));

            // 计算简单转折点
            Point cornerPoint = CalculateSimpleCorner(extendedStart, mousePosition, startDirection);
            if (!ArePointsTooClose(extendedStart, cornerPoint, 5))
            {
                pathFigure.Segments.Add(new LineSegment(cornerPoint, true));
            }

            // 最终段到鼠标位置
            pathFigure.Segments.Add(new LineSegment(mousePosition, true));

            pathGeometry.Figures.Add(pathFigure);
            return pathGeometry;
        }

        #endregion

        #region 指引线辅助方法

        /// <summary>
        /// 计算固定锚点位置 - 与实际连接点计算保持一致
        /// </summary>
        private Point CalculateFixedAnchorPoint(Point deviceCenter, ConnectionDirection direction, double deviceWidth, double deviceHeight)
        {
            // 名称区域高度
            double nameAreaHeight = 20;

            // 图标区域的实际高度
            double iconHeight = deviceHeight - nameAreaHeight;

            // 调整设备中心点，使其位于图标区域的中心
            Point iconCenter = new Point(
                deviceCenter.X,
                deviceCenter.Y - nameAreaHeight / 2  // 向上偏移名称区域的一半
            );

            switch (direction)
            {
                case ConnectionDirection.Top:
                    return new Point(iconCenter.X, iconCenter.Y - iconHeight / 2);

                case ConnectionDirection.Right:
                    return new Point(iconCenter.X + deviceWidth / 2, iconCenter.Y);

                case ConnectionDirection.Bottom:
                    return new Point(iconCenter.X, iconCenter.Y + iconHeight / 2);

                case ConnectionDirection.Left:
                    return new Point(iconCenter.X - deviceWidth / 2, iconCenter.Y);

                default:
                    return iconCenter;
            }
        }

        /// <summary>
        /// 计算简单转折点 - 与实际连接线算法保持一致
        /// </summary>
        private Point CalculateSimpleCorner(Point startPoint, Point endPoint, ConnectionDirection startDirection)
        {
            switch (startDirection)
            {
                case ConnectionDirection.Right:
                case ConnectionDirection.Left:
                    // 水平起始，垂直转折
                    return new Point(startPoint.X, endPoint.Y);

                case ConnectionDirection.Top:
                case ConnectionDirection.Bottom:
                    // 垂直起始，水平转折
                    return new Point(endPoint.X, startPoint.Y);

                default:
                    // 默认水平优先
                    return new Point(endPoint.X, startPoint.Y);
            }
        }

        #endregion

        #region 连接点管理

        /// <summary>
        /// 创建连接点
        /// </summary>
        private ConnectionPoint CreateConnectionPoint(string deviceId, ConnectionDirection direction, FrameworkElement device)
        {
            var connectionPoint = new ConnectionPoint
            {
                Id = Guid.NewGuid().ToString(),
                DeviceId = deviceId,
                Direction = direction,
                RelativePosition = GetConnectionPointPosition(device, direction)
            };

            var pointElement = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = Brushes.LightBlue,
                Stroke = Brushes.DarkBlue,
                StrokeThickness = 1,
                Tag = connectionPoint.Id,
                Cursor = Cursors.Hand
            };

            // 添加鼠标悬停效果
            pointElement.MouseEnter += (s, e) => {
                pointElement.Fill = Brushes.Yellow;
                pointElement.Width = 10;
                pointElement.Height = 10;
            };

            pointElement.MouseLeave += (s, e) => {
                pointElement.Fill = Brushes.LightBlue;
                pointElement.Width = 8;
                pointElement.Height = 8;
            };

            connectionPoint.VisualElement = pointElement;
            return connectionPoint;
        }

        /// <summary>
        /// 获取连接点位置
        /// </summary>
        private Point GetConnectionPointPosition(FrameworkElement device, ConnectionDirection direction)
        {
            double width = device.ActualWidth > 0 ? device.ActualWidth : 80;
            double height = device.ActualHeight > 0 ? device.ActualHeight : 60;

            // 名称区域高度（与CreateDeviceVisual中的设置保持一致）
            double nameAreaHeight = 20;

            // 图标区域的实际高度
            double iconHeight = height - nameAreaHeight;

            switch (direction)
            {
                case ConnectionDirection.Top:
                    // 图标顶部
                    return new Point(width / 2, 0);

                case ConnectionDirection.Right:
                    // 图标右侧中间
                    return new Point(width, iconHeight / 2);

                case ConnectionDirection.Bottom:
                    // 图标底部（名称上方）
                    return new Point(width / 2, iconHeight);

                case ConnectionDirection.Left:
                    // 图标左侧中间
                    return new Point(0, iconHeight / 2);

                default:
                    return new Point(width / 2, iconHeight / 2);
            }
        }

        /// <summary>
        /// 显示连接点
        /// </summary>
        public void ShowConnectionPoints(string deviceId)
        {
            var canvas = GetCanvas();
            var device = FindDeviceElement(deviceId);

            if (canvas == null)
            {
                LogDebug("无法获取画布，跳过显示连接点");
                return;
            }

            if (device == null)
            {
                LogDebug($"无法找到设备 {deviceId}，跳过显示连接点");
                return;
            }

            // 如果已经显示了连接点，先清除
            if (_deviceConnectionPointElements.ContainsKey(deviceId))
            {
                var oldElements = _deviceConnectionPointElements[deviceId];
                foreach (var element in oldElements)
                {
                    canvas.Children.Remove(element);
                }
                _deviceConnectionPointElements.Remove(deviceId);
                _deviceConnectionPoints.Remove(deviceId);
            }

            // 强制更新布局以确保变换生效
            device.UpdateLayout();

            // 创建连接点
            var connectionPoints = new List<ConnectionPoint>();
            var pointElements = new List<FrameworkElement>();

            var directions = new[] { ConnectionDirection.Top, ConnectionDirection.Right, ConnectionDirection.Bottom, ConnectionDirection.Left };

            foreach (var direction in directions)
            {
                var point = CreateConnectionPoint(deviceId, direction, device);
                connectionPoints.Add(point);

                // 关键修改：使用考虑缩放的位置计算
                var absolutePosition = GetTransformedConnectionPointPosition(device, direction);

                LogDebug($"设备 {deviceId} 方向 {direction} 连接点位置: ({absolutePosition.X:F1}, {absolutePosition.Y:F1})");

                // 设置连接点位置
                Canvas.SetLeft(point.VisualElement, absolutePosition.X - point.VisualElement.Width / 2);
                Canvas.SetTop(point.VisualElement, absolutePosition.Y - point.VisualElement.Height / 2);

                // 修改：添加点击事件，使连接点可点击
                point.VisualElement.MouseDown += (s, e) => {
                    if (e.LeftButton == MouseButtonState.Pressed)
                    {
                        OnConnectionPointClick(deviceId, direction, e);
                        e.Handled = true;
                    }
                };

                canvas.Children.Add(point.VisualElement);
                Panel.SetZIndex(point.VisualElement, 200);
                pointElements.Add(point.VisualElement);
            }

            _deviceConnectionPoints[deviceId] = connectionPoints;
            _deviceConnectionPointElements[deviceId] = pointElements;

            LogDebug($"已为设备 {deviceId} 显示 {directions.Length} 个连接点（已考虑缩放）");
        }

        /// <summary>
        /// 获取变换后的连接点位置
        /// </summary>
        private Point GetTransformedConnectionPointPosition(FrameworkElement device, ConnectionDirection direction)
        {
            try
            {
                // 获取设备的基本位置和尺寸
                var deviceLeft = Canvas.GetLeft(device);
                var deviceTop = Canvas.GetTop(device);
                var deviceWidth = device.ActualWidth > 0 ? device.ActualWidth : 80;
                var deviceHeight = device.ActualHeight > 0 ? device.ActualHeight : 60;

                if (double.IsNaN(deviceLeft)) deviceLeft = 0;
                if (double.IsNaN(deviceTop)) deviceTop = 0;

                // 计算设备中心点
                var deviceCenter = new Point(
                    deviceLeft + deviceWidth / 2,
                    deviceTop + deviceHeight / 2
                );

                // 获取缩放信息
                var scaleInfo = GetDeviceScaleInfo(device);

                LogDebug($"设备 {device.Tag} 缩放信息: ScaleX={scaleInfo.ScaleX:F2}, ScaleY={scaleInfo.ScaleY:F2}");

                // 计算缩放后的尺寸
                var scaledWidth = deviceWidth * scaleInfo.ScaleX;
                var scaledHeight = deviceHeight * scaleInfo.ScaleY;

                // 名称区域高度
                double nameAreaHeight = 20 * scaleInfo.ScaleY;
                // 图标区域的实际高度
                double scaledIconHeight = scaledHeight - nameAreaHeight;

                // 计算图标区域的中心点（考虑缩放）
                Point iconCenter = new Point(
                    deviceCenter.X,
                    deviceCenter.Y - nameAreaHeight / 2  // 向上偏移名称区域的一半
                );

                // 根据方向计算连接点位置
                Point connectionPoint;
                switch (direction)
                {
                    case ConnectionDirection.Top:
                        connectionPoint = new Point(iconCenter.X, iconCenter.Y - scaledIconHeight / 2);
                        break;

                    case ConnectionDirection.Right:
                        connectionPoint = new Point(iconCenter.X + scaledWidth / 2, iconCenter.Y);
                        break;

                    case ConnectionDirection.Bottom:
                        connectionPoint = new Point(iconCenter.X, iconCenter.Y + scaledIconHeight / 2);
                        break;

                    case ConnectionDirection.Left:
                        connectionPoint = new Point(iconCenter.X - scaledWidth / 2, iconCenter.Y);
                        break;

                    default:
                        connectionPoint = iconCenter;
                        break;
                }

                LogDebug($"方向 {direction} 连接点计算: 原始设备尺寸({deviceWidth:F1}x{deviceHeight:F1}) -> 缩放后({scaledWidth:F1}x{scaledHeight:F1}) -> 连接点({connectionPoint.X:F1}, {connectionPoint.Y:F1})");

                return connectionPoint;
            }
            catch (Exception ex)
            {
                LogDebug($"计算变换后连接点位置失败: {ex.Message}");

                // 回退到基本计算
                return GetBasicConnectionPointPosition(device, direction);
            }
        }

        /// <summary>
        /// 获取设备的缩放信息
        /// </summary>
        private (double ScaleX, double ScaleY) GetDeviceScaleInfo(FrameworkElement device)
        {
            try
            {
                // 检查是否有缩放变换
                if (device.RenderTransform is TransformGroup transformGroup)
                {
                    var scaleTransform = transformGroup.Children.OfType<ScaleTransform>().FirstOrDefault();
                    if (scaleTransform != null)
                    {
                        return (scaleTransform.ScaleX, scaleTransform.ScaleY);
                    }
                }
                else if (device.RenderTransform is ScaleTransform directScale)
                {
                    return (directScale.ScaleX, directScale.ScaleY);
                }

                // 没有缩放变换，返回默认值
                return (1.0, 1.0);
            }
            catch (Exception ex)
            {
                LogDebug($"获取设备缩放信息失败: {ex.Message}");
                return (1.0, 1.0);
            }
        }

        /// <summary>
        /// 基本连接点位置计算（回退方案）
        /// </summary>
        private Point GetBasicConnectionPointPosition(FrameworkElement device, ConnectionDirection direction)
        {
            var deviceLeft = Canvas.GetLeft(device);
            var deviceTop = Canvas.GetTop(device);
            var deviceWidth = device.ActualWidth > 0 ? device.ActualWidth : 80;
            var deviceHeight = device.ActualHeight > 0 ? device.ActualHeight : 60;

            if (double.IsNaN(deviceLeft)) deviceLeft = 0;
            if (double.IsNaN(deviceTop)) deviceTop = 0;

            var relativePosition = GetConnectionPointPosition(device, direction);

            return new Point(
                deviceLeft + relativePosition.X,
                deviceTop + relativePosition.Y
            );
        }

        /// <summary>
        /// 连接点点击处理
        /// </summary>
        private void OnConnectionPointClick(string deviceId, ConnectionDirection direction, MouseButtonEventArgs e)
        {
            LogDebug($"连接点被点击: 设备={deviceId}, 方向={direction}");

            if (IsConnectionMode)
            {
                CompleteConnection(deviceId, direction);
            }
            else
            {
                StartConnection(deviceId, direction);
            }
        }

        /// <summary>
        /// 隐藏连接点
        /// </summary>
        public void HideConnectionPoints(string deviceId)
        {
            var canvas = GetCanvas();
            if (canvas == null)
            {
                LogDebug("无法获取画布，跳过隐藏连接点");
                return;
            }

            if (_deviceConnectionPointElements.ContainsKey(deviceId))
            {
                var elements = _deviceConnectionPointElements[deviceId];
                foreach (var element in elements)
                {
                    canvas.Children.Remove(element);
                }
                _deviceConnectionPointElements.Remove(deviceId);
                _deviceConnectionPoints.Remove(deviceId);

                LogDebug($"已隐藏设备 {deviceId} 的连接点");
            }
        }

        /// <summary>
        /// 隐藏所有连接点
        /// </summary>
        public void HideAllConnectionPoints()
        {
            var canvas = GetCanvas();
            if (canvas == null)
            {
                LogDebug("无法获取画布，跳过隐藏连接点");
                return;
            }

            foreach (var elements in _deviceConnectionPointElements.Values)
            {
                foreach (var element in elements)
                {
                    canvas.Children.Remove(element);
                }
            }
            _deviceConnectionPoints.Clear();
            _deviceConnectionPointElements.Clear();

            LogDebug("已隐藏所有连接点");
        }

        /// <summary>
        /// 显示所有连接点
        /// </summary>
        public void ShowAllConnectionPoints()
        {
            ShowAllDeviceConnectionPointsAction?.Invoke();
        }

        /// <summary>
        /// 获取设备连接点
        /// </summary>
        public List<ConnectionPoint> GetDeviceConnectionPoints(string deviceId)
        {
            _deviceConnectionPoints.TryGetValue(deviceId, out var points);
            return points ?? new List<ConnectionPoint>();
        }

        #endregion

        #region 连接视觉元素管理

        /// <summary>
        /// 创建连接视觉元素
        /// </summary>
        private void CreateConnectionVisuals(ConnectionInfo connection)
        {
            var canvas = GetCanvas();
            if (canvas == null)
            {
                LogDebug("无法获取画布，跳过创建连接视觉元素");
                return;
            }

            connection.ConnectionPath = new Path
            {
                Stroke = new SolidColorBrush(SlateGray),
                StrokeThickness = 2,
                Fill = Brushes.Transparent,
                Tag = connection.Id
            };

            connection.ArrowPath = new Path
            {
                Fill = new SolidColorBrush(SlateGray),
                Stroke = new SolidColorBrush(SlateGray),
                Tag = connection.Id
            };

            connection.ConnectionColor = new SolidColorBrush(SlateGray);

            // 添加鼠标事件
            connection.ConnectionPath.MouseDown += (s, e) => OnConnectionMouseDown(connection, e);
            connection.ArrowPath.MouseDown += (s, e) => OnConnectionMouseDown(connection, e);

            canvas.Children.Add(connection.ConnectionPath);
            canvas.Children.Add(connection.ArrowPath);

            Panel.SetZIndex(connection.ConnectionPath, 100);
            Panel.SetZIndex(connection.ArrowPath, 101);

            LogDebug($"连接视觉元素已创建并添加到画布");
        }

        /// <summary>
        /// 连接鼠标事件处理
        /// </summary>
        private void OnConnectionMouseDown(ConnectionInfo connection, MouseButtonEventArgs e)
        {
            if (e.RightButton == MouseButtonState.Pressed)
            {
                // 右键点击，选择连接
                SetConnectionSelected(connection, !connection.IsSelected);
                e.Handled = true;
                LogDebug($"连接 {connection.Id} 选择状态: {connection.IsSelected}");
            }
            else if (e.LeftButton == MouseButtonState.Pressed)
            {
                // 左键点击，也可以选择连接
                SetConnectionSelected(connection, !connection.IsSelected);
                e.Handled = true;
                LogDebug($"连接 {connection.Id} 选择状态: {connection.IsSelected}");
            }
        }

        /// <summary>
        /// 设置连接选择状态
        /// </summary>
        private void SetConnectionSelected(ConnectionInfo connection, bool isSelected)
        {
            connection.IsSelected = isSelected;

            if (isSelected)
            {
                // 清除其他连接的选择状态
                foreach (var conn in _connections.Values)
                {
                    if (conn.Id != connection.Id)
                    {
                        conn.IsSelected = false;
                        UpdateConnectionAppearance(conn);
                    }
                }
            }

            UpdateConnectionAppearance(connection);
        }

        /// <summary>
        /// 更新连接外观
        /// </summary>
        private void UpdateConnectionAppearance(ConnectionInfo connection)
        {
            if (connection.ConnectionPath != null)
            {
                if (connection.IsSelected)
                {
                    connection.ConnectionPath.Stroke = Brushes.Orange;
                    connection.ConnectionPath.StrokeThickness = 2;
                }
                else
                {
                    connection.ConnectionPath.Stroke = connection.ConnectionColor ?? new SolidColorBrush(SlateGray);
                    connection.ConnectionPath.StrokeThickness = 2;
                }
            }

            if (connection.ArrowPath != null)
            {
                var brush = connection.IsSelected ? Brushes.Orange : (connection.ConnectionColor ?? new SolidColorBrush(SlateGray));
                connection.ArrowPath.Fill = brush;
                connection.ArrowPath.Stroke = brush;
                connection.ArrowPath.StrokeThickness = 1;
            }
        }

        #endregion

        #region 🔥 改进的路径计算 - 拖拽与最终路径完全一致

        /// <summary>
        /// 核心方法：根据模式选择算法
        /// </summary>
        public void UpdateConnectionPath(ConnectionInfo connection)
        {
            var canvas = GetCanvas();
            if (canvas == null) return;

            try
            {
                // 🔥 关键改进：拖拽时使用高速但一致的算法
                if (_isDragMode)
                {
                    UpdateConnectionPathDragMode(connection);
                    return;
                }

                // 正常时检查缓存
                var cacheKey = GeneratePathCacheKey(connection);
                if (_pathCache.TryGetValue(cacheKey, out var cachedPath))
                {
                    connection.ConnectionPath.Data = cachedPath;
                    UpdateArrowFromCache(connection);
                    return;
                }

                // 正常时使用完整算法
                UpdateConnectionPathNormal(connection);

                // 缓存结果（限制缓存大小）
                if (_pathCache.Count < 100)
                {
                    _pathCache[cacheKey] = connection.ConnectionPath.Data as PathGeometry;
                }
            }
            catch (Exception ex)
            {
                LogDebug($"更新连接路径失败: {ex.Message}");
                // 回退到简单直线连接
                CreateSimpleFallbackPath(connection);
            }
        }

        /// <summary>
        /// 🔥 改进：拖拽模式路径更新 - 与最终路径算法保持一致
        /// </summary>
        private void UpdateConnectionPathDragMode(ConnectionInfo connection)
        {
            try
            {
                // 🔥 使用与最终路径相同的连接点计算
                Point startPoint = GetTransformedConnectionPointPosition(connection.StartDevice, connection.StartAnchor.Direction);
                Point endPoint = GetTransformedConnectionPointPosition(connection.EndDevice, connection.EndAnchor.Direction);

                // 获取连接点的方向信息
                ConnectionDirection startDirection = connection.StartAnchor.Direction;
                ConnectionDirection endDirection = connection.EndAnchor.Direction;

                LogDebug($"拖拽模式连接方向: 起点={startDirection}, 终点={endDirection}");

                // 🔥 使用与最终路径相同的算法，但带性能优化
                List<Point> pathPoints = CalculateDragOptimizedPath(startPoint, startDirection, endPoint, endDirection, connection);

                // 创建路径几何
                PathGeometry pathGeometry = new PathGeometry();
                PathFigure pathFigure = new PathFigure();
                pathFigure.StartPoint = startPoint;

                // 计算箭头方向
                Vector arrowDirection = GetPerpendicularArrowDirection(endDirection);

                // 连接线终点调整
                double lineOffset = 10;
                Point adjustedEndPoint = new Point(
                    endPoint.X - arrowDirection.X * lineOffset,
                    endPoint.Y - arrowDirection.Y * lineOffset
                );

                // 🔥 应用与最终路径相同的圆角处理（简化版）
                CreateOptimizedRoundedPath(pathFigure, startPoint, pathPoints, adjustedEndPoint, true); // true表示拖拽简化模式

                pathGeometry.Figures.Add(pathFigure);

                // 更新连接线的路径
                connection.ConnectionPath.Data = pathGeometry;

                // 更新箭头
                UpdatePerpendicularArrow(connection, arrowDirection, endPoint);

                // 确保颜色正确应用
                EnsureConnectionColors(connection);

                LogDebug($"拖拽模式路径更新完成，路径点数: {pathPoints.Count}");
            }
            catch (Exception ex)
            {
                LogDebug($"拖拽模式路径更新失败: {ex.Message}");
                // 静默处理错误，避免拖拽中断
                CreateSimpleFallbackPath(connection);
            }
        }

        /// <summary>
        /// 🔥 新增：拖拽优化路径计算 - 与最终算法一致但更高效
        /// </summary>
        private List<Point> CalculateDragOptimizedPath(Point startPoint, ConnectionDirection startDir,
                                                      Point endPoint, ConnectionDirection endDir, ConnectionInfo connection)
        {
            // 🔥 生成拖拽缓存键
            var cacheKey = $"drag_{connection.Id}_{startPoint.X:F0},{startPoint.Y:F0}|{endPoint.X:F0},{endPoint.Y:F0}";

            // 检查拖拽缓存
            if (_dragPathCache.TryGetValue(cacheKey, out var cachedPath))
            {
                return cachedPath;
            }

            List<Point> pathPoints = new List<Point>();

            if (connection.IsControlPointCustomized)
            {
                pathPoints = CalculatePerpendicularPathWithControlPoint(startPoint, startDir, endPoint, endDir, connection.ControlPoint);
            }
            else
            {
                double extension = 20;

                switch (connection.DirectionMode)
                {
                    case ConnectionDirectionMode.HorizontalFirst:
                        pathPoints.AddRange(CreateHorizontalFirstPerpendicularPath(startPoint, startDir, endPoint, endDir, extension));
                        break;

                    case ConnectionDirectionMode.VerticalFirst:
                        pathPoints.AddRange(CreateVerticalFirstPerpendicularPath(startPoint, startDir, endPoint, endDir, extension));
                        break;

                    case ConnectionDirectionMode.Auto:
                    default:
                        // 🔥 使用拖拽优化的自动路径算法
                        pathPoints.AddRange(CreateDragOptimizedAutoPath(startPoint, startDir, endPoint, endDir, extension));
                        break;
                }
            }

            // 🔥 缓存拖拽路径结果
            if (_dragPathCache.Count < 30) // 限制拖拽缓存大小
            {
                _dragPathCache[cacheKey] = pathPoints;
            }

            return pathPoints;
        }

        /// <summary>
        /// 🔥 新增：拖拽优化的自动路径 - 与最终算法保持一致
        /// </summary>
        private List<Point> CreateDragOptimizedAutoPath(Point startPoint, ConnectionDirection startDir,
                                                       Point endPoint, ConnectionDirection endDir, double extension)
        {
            Point startExt = GetExtensionPoint(startPoint, startDir, extension);
            Point endEntryPoint = GetPerpendicularEntryPoint(endPoint, endDir, extension);

            LogDebug($"拖拽优化路径计算: start({startExt.X:F1}, {startExt.Y:F1}), end({endEntryPoint.X:F1}, {endEntryPoint.Y:F1})");

            // 🔥 使用拖拽模式的快速障碍物检测
            List<Rect> obstacles = GetDragModeObstacles(startExt, endEntryPoint);
            LogDebug($"拖拽模式获取到 {obstacles.Count} 个相关障碍物");

            // 🔥 使用快速但一致的路径调整
            Point adjustedStartExt = QuickAdjustPoint(startExt, startDir, obstacles);
            Point adjustedEndEntry = QuickAdjustPoint(endEntryPoint, endDir, obstacles);

            if (ArePointsTooClose(adjustedStartExt, adjustedEndEntry, 10.0))
            {
                LogDebug("拖拽模式调整后的点过于接近，返回简单路径");
                return new List<Point> { adjustedStartExt };
            }

            List<Point> pathPoints = new List<Point>();
            pathPoints.Add(adjustedStartExt);

            // 🔥 使用与最终算法一致的中间路径计算（简化版）
            List<Point> middlePoints = CalculateDragMiddlePath(adjustedStartExt, adjustedEndEntry, obstacles);
            pathPoints.AddRange(middlePoints);

            pathPoints.Add(adjustedEndEntry);

            // 🔥 使用与最终算法相同的路径优化
            pathPoints = RemoveDuplicatePoints(pathPoints, 3.0);
            pathPoints = ToOrthogonalPath(pathPoints);

            LogDebug($"=== 拖拽优化路径包含 {pathPoints.Count} 个点 ===");

            return pathPoints;
        }

        /// <summary>
        /// 生成路径缓存键
        /// </summary>
        private string GeneratePathCacheKey(ConnectionInfo connection)
        {
            var startPos = GetTransformedConnectionPointPosition(connection.StartDevice, connection.StartAnchor.Direction);
            var endPos = GetTransformedConnectionPointPosition(connection.EndDevice, connection.EndAnchor.Direction);

            return $"{startPos.X:F0},{startPos.Y:F0}|{endPos.X:F0},{endPos.Y:F0}|{connection.StartAnchor.Direction}|{connection.EndAnchor.Direction}";
        }

        /// <summary>
        /// 从缓存更新箭头
        /// </summary>
        private void UpdateArrowFromCache(ConnectionInfo connection)
        {
            try
            {
                var endPoint = GetTransformedConnectionPointPosition(connection.EndDevice, connection.EndAnchor.Direction);
                Vector arrowDirection = GetPerpendicularArrowDirection(connection.EndAnchor.Direction);
                double arrowAngle = Math.Atan2(arrowDirection.Y, arrowDirection.X) * 180 / Math.PI;
                PathGeometry arrowGeometry = CreateSimpleArrowGeometry(endPoint, arrowAngle, 14, 5);
                connection.ArrowPath.Data = arrowGeometry;
            }
            catch { }
        }

        /// <summary>
        /// 🔥 优化：拖拽模式简单转角计算
        /// </summary>
        private Point CalculateSimpleDragCorner(Point start, Point end, ConnectionDirection startDirection)
        {
            // 根据起始方向快速确定转折点
            switch (startDirection)
            {
                case ConnectionDirection.Right:
                case ConnectionDirection.Left:
                    return new Point(start.X, end.Y);
                case ConnectionDirection.Top:
                case ConnectionDirection.Bottom:
                    return new Point(end.X, start.Y);
                default:
                    return new Point(end.X, start.Y);
            }
        }

        /// <summary>
        /// 优化后的完整算法模式
        /// </summary>
        private void UpdateConnectionPathNormal(ConnectionInfo connection)
        {
            // 使用变换后的位置计算
            Point startPoint = GetTransformedConnectionPointPosition(connection.StartDevice, connection.StartAnchor.Direction);
            Point endPoint = GetTransformedConnectionPointPosition(connection.EndDevice, connection.EndAnchor.Direction);

            // 获取连接点的方向信息
            ConnectionDirection startDirection = connection.StartAnchor.Direction;
            ConnectionDirection endDirection = connection.EndAnchor.Direction;

            LogDebug($"连接方向: 起点={startDirection}, 终点={endDirection}");
            LogDebug($"变换后连接点: 起点({startPoint.X:F1}, {startPoint.Y:F1}), 终点({endPoint.X:F1}, {endPoint.Y:F1})");

            // 创建路径几何
            PathGeometry pathGeometry = new PathGeometry();
            PathFigure pathFigure = new PathFigure();
            pathFigure.StartPoint = startPoint;

            // 使用高效的路径计算
            List<Point> pathPoints = CalculateOptimizedPath(startPoint, startDirection, endPoint, endDirection, connection);

            // 计算箭头方向 - 基于终点方向
            Vector arrowDirection = GetPerpendicularArrowDirection(endDirection);

            // 连接线终点调整
            double lineOffset = 10;
            Point adjustedEndPoint = new Point(
                endPoint.X - arrowDirection.X * lineOffset,
                endPoint.Y - arrowDirection.Y * lineOffset
            );

            // 确保调整后的终点在路径的正确方向上
            if (pathPoints.Count > 0)
            {
                Point lastPathPoint = pathPoints.Last();

                if (Math.Abs(lastPathPoint.X - endPoint.X) < 1.0)
                {
                    adjustedEndPoint = new Point(endPoint.X, endPoint.Y - arrowDirection.Y * lineOffset);
                }
                else if (Math.Abs(lastPathPoint.Y - endPoint.Y) < 1.0)
                {
                    adjustedEndPoint = new Point(endPoint.X - arrowDirection.X * lineOffset, endPoint.Y);
                }
            }

            // 优化：简化的圆角处理
            CreateOptimizedRoundedPath(pathFigure, startPoint, pathPoints, adjustedEndPoint);

            pathGeometry.Figures.Add(pathFigure);

            // 更新连接线的路径
            connection.ConnectionPath.Data = pathGeometry;

            // 更新箭头 - 箭头指向设备
            UpdatePerpendicularArrow(connection, arrowDirection, endPoint);

            // 简化：只在必要时更新转角控制点
            if (connection.CornerControl != null)
            {
                UpdateRightAngleCornerControl(connection, pathPoints, startPoint, endPoint);
            }

            // 确保颜色正确应用
            EnsureConnectionColors(connection);

            // 简化：只在必要时更新标签和动画
            if (connection.ConnectionLabel != null && !connection.IsLabelPositionCustomized)
            {
                PositionConnectionLabel(connection);
            }
            if (_isMonitorMode && connection.IsAnimating)
            {
                UpdateAnimationPath(connection);
            }
        }

        #endregion

        #region 路径计算辅助方法

        /// <summary>
        /// 优化的路径计算
        /// </summary>
        private List<Point> CalculateOptimizedPath(Point startPoint, ConnectionDirection startDir,
                                                  Point endPoint, ConnectionDirection endDir, ConnectionInfo connection)
        {
            List<Point> pathPoints = new List<Point>();

            if (connection.IsControlPointCustomized)
            {
                return CalculatePerpendicularPathWithControlPoint(startPoint, startDir, endPoint, endDir, connection.ControlPoint);
            }

            double extension = 20;

            switch (connection.DirectionMode)
            {
                case ConnectionDirectionMode.HorizontalFirst:
                    pathPoints.AddRange(CreateHorizontalFirstPerpendicularPath(startPoint, startDir, endPoint, endDir, extension));
                    break;

                case ConnectionDirectionMode.VerticalFirst:
                    pathPoints.AddRange(CreateVerticalFirstPerpendicularPath(startPoint, startDir, endPoint, endDir, extension));
                    break;

                case ConnectionDirectionMode.Auto:
                default:
                    pathPoints.AddRange(CreateOptimizedAutoPath(startPoint, startDir, endPoint, endDir, extension));
                    break;
            }

            return pathPoints;
        }

        /// <summary>
        /// 优化的自动路径计算 - 减少复杂度
        /// </summary>
        private List<Point> CreateOptimizedAutoPath(Point startPoint, ConnectionDirection startDir,
                                                   Point endPoint, ConnectionDirection endDir, double extension)
        {
            Point startExt = GetExtensionPoint(startPoint, startDir, extension);
            Point endEntryPoint = GetPerpendicularEntryPoint(endPoint, endDir, extension);

            LogDebug($"优化路径计算: start({startExt.X:F1}, {startExt.Y:F1}), end({endEntryPoint.X:F1}, {endEntryPoint.Y:F1})");

            // 使用高效的障碍物检测
            List<Rect> obstacles = GetOptimizedObstacles(startExt, endEntryPoint);
            LogDebug($"获取到 {obstacles.Count} 个相关障碍物");

            // 简化：只进行基本的路径调整
            Point adjustedStartExt = SmartAdjustPoint(startExt, startDir, obstacles, "起点延伸");
            Point adjustedEndEntry = SmartAdjustPoint(endEntryPoint, endDir, obstacles, "终点进入");

            if (ArePointsTooClose(adjustedStartExt, adjustedEndEntry, 10.0))
            {
                LogDebug("调整后的点过于接近，返回简单路径");
                return new List<Point> { adjustedStartExt };
            }

            List<Point> pathPoints = new List<Point>();
            pathPoints.Add(adjustedStartExt);

            // 使用简化的中间路径计算
            List<Point> middlePoints = CalculateSimplifiedMiddlePath(adjustedStartExt, adjustedEndEntry, obstacles);
            pathPoints.AddRange(middlePoints);

            pathPoints.Add(adjustedEndEntry);

            // 优化：简化的点清理
            pathPoints = RemoveDuplicatePoints(pathPoints, 3.0); // 提高阈值
            pathPoints = ToOrthogonalPath(pathPoints);

            LogDebug($"=== 优化路径包含 {pathPoints.Count} 个点 ===");

            return pathPoints;
        }

        /// <summary>
        /// 高效的障碍物获取
        /// </summary>
        private List<Rect> GetOptimizedObstacles(Point start, Point end)
        {
            var now = DateTime.Now;
            var cacheKey = $"obstacles_{start.X:F0}_{start.Y:F0}_{end.X:F0}_{end.Y:F0}";

            // 检查缓存
            if (_obstacleCache.TryGetValue(cacheKey, out var cachedObstacles) &&
                (now - _lastObstacleUpdate).TotalMilliseconds < ObstacleCacheLifetime)
            {
                return cachedObstacles;
            }

            // 优化：只检查路径附近的障碍物
            List<Rect> obstacles = new List<Rect>();

            try
            {
                // 计算更紧凑的搜索区域
                double margin = 80; // 减少搜索范围
                double minX = Math.Min(start.X, end.X) - margin;
                double minY = Math.Min(start.Y, end.Y) - margin;
                double maxX = Math.Max(start.X, end.X) + margin;
                double maxY = Math.Max(start.Y, end.Y) + margin;

                Rect pathBounds = new Rect(minX, minY, maxX - minX, maxY - minY);

                var canvas = GetCanvas();
                if (canvas != null)
                {
                    // 优化：使用更高效的过滤
                    foreach (UIElement element in canvas.Children)
                    {
                        if (element is Border deviceBorder && IsDevice(deviceBorder))
                        {
                            Rect? iconRect = GetDeviceIconBounds(deviceBorder);
                            if (iconRect.HasValue && pathBounds.IntersectsWith(iconRect.Value))
                            {
                                // 减少扩展边距
                                Rect expandedRect = new Rect(
                                    iconRect.Value.X - 15,
                                    iconRect.Value.Y - 15,
                                    iconRect.Value.Width + 30,
                                    iconRect.Value.Height + 30
                                );
                                obstacles.Add(expandedRect);
                            }
                        }
                    }
                }

                LogDebug($"优化障碍物检测: 在路径区域内找到 {obstacles.Count} 个障碍物");
            }
            catch (Exception ex)
            {
                LogDebug($"获取障碍物时出错: {ex.Message}");
            }

            // 更新缓存
            _obstacleCache[cacheKey] = obstacles;
            _lastObstacleUpdate = now;

            // 清理旧缓存
            if (_obstacleCache.Count > 30) // 减少缓存大小
            {
                var oldKeys = _obstacleCache.Keys.Take(_obstacleCache.Count - 20).ToList();
                foreach (var key in oldKeys)
                {
                    _obstacleCache.Remove(key);
                }
            }

            return obstacles;
        }

        /// <summary>
        /// 简化的中间路径计算
        /// </summary>
        private List<Point> CalculateSimplifiedMiddlePath(Point startExt, Point endEntry, List<Rect> obstacles)
        {
            List<Point> middlePoints = new List<Point>();

            Point horizontalCorner = new Point(endEntry.X, startExt.Y);
            Point verticalCorner = new Point(startExt.X, endEntry.Y);

            // 优化：简化的路径检查
            bool horizontalPathClear = !HasObstaclesInPath(startExt, horizontalCorner, obstacles) &&
                                      !HasObstaclesInPath(horizontalCorner, endEntry, obstacles);

            bool verticalPathClear = !HasObstaclesInPath(startExt, verticalCorner, obstacles) &&
                                    !HasObstaclesInPath(verticalCorner, endEntry, obstacles);

            if (horizontalPathClear && verticalPathClear)
            {
                // 选择更短的路径
                double horizontalDistance = Math.Abs(endEntry.X - startExt.X) + Math.Abs(endEntry.Y - startExt.Y);
                double verticalDistance = Math.Abs(startExt.X - endEntry.X) + Math.Abs(endEntry.Y - startExt.Y);

                if (horizontalDistance <= verticalDistance)
                {
                    middlePoints.Add(horizontalCorner);
                }
                else
                {
                    middlePoints.Add(verticalCorner);
                }
            }
            else if (horizontalPathClear)
            {
                middlePoints.Add(horizontalCorner);
            }
            else if (verticalPathClear)
            {
                middlePoints.Add(verticalCorner);
            }
            else
            {
                // 优化：使用更简单的绕行策略
                middlePoints.AddRange(CreateSimpleBypassPath(startExt, endEntry, obstacles));
            }

            return middlePoints;
        }

        /// <summary>
        /// 简化的绕行路径
        /// </summary>
        private List<Point> CreateSimpleBypassPath(Point start, Point end, List<Rect> obstacles)
        {
            // 简单的绕行策略：向外扩展
            double margin = 40;

            if (obstacles.Count > 0)
            {
                var firstObstacle = obstacles[0];

                // 选择最简单的绕行方向
                if (Math.Abs(end.X - start.X) > Math.Abs(end.Y - start.Y))
                {
                    // 水平距离更大，垂直绕行
                    double bypassY = end.Y > start.Y ? firstObstacle.Bottom + margin : firstObstacle.Top - margin;
                    return new List<Point>
                    {
                        new Point(start.X, bypassY),
                        new Point(end.X, bypassY)
                    };
                }
                else
                {
                    // 垂直距离更大，水平绕行
                    double bypassX = end.X > start.X ? firstObstacle.Right + margin : firstObstacle.Left - margin;
                    return new List<Point>
                    {
                        new Point(bypassX, start.Y),
                        new Point(bypassX, end.Y)
                    };
                }
            }

            // 没有障碍物，返回简单L形路径
            return new List<Point> { new Point(end.X, start.Y) };
        }

        /// <summary>
        /// 🔥 优化的圆角路径创建 - 支持拖拽简化模式
        /// </summary>
        private void CreateOptimizedRoundedPath(PathFigure pathFigure, Point startPoint, List<Point> pathPoints, Point endPoint, bool simplifiedMode = false)
        {
            double cornerRadius = simplifiedMode ? 6.0 : 8.0; // 拖拽模式使用更小的圆角半径

            try
            {
                List<Point> allPoints = new List<Point> { startPoint };
                allPoints.AddRange(pathPoints);
                allPoints.Add(endPoint);

                if (allPoints.Count <= 2)
                {
                    if (allPoints.Count == 2)
                    {
                        pathFigure.Segments.Add(new LineSegment(allPoints[1], true));
                    }
                    return;
                }

                // 🔥 优化：拖拽模式进一步简化圆角处理
                for (int i = 1; i < allPoints.Count; i++)
                {
                    Point currentPoint = allPoints[i];

                    if (i == allPoints.Count - 1 || (simplifiedMode && allPoints.Count <= 4)) // 拖拽模式更保守
                    {
                        pathFigure.Segments.Add(new LineSegment(currentPoint, true));
                    }
                    else
                    {
                        Point prevPoint = allPoints[i - 1];
                        Point nextPoint = allPoints[i + 1];

                        Point approachPoint = CalculateCornerApproachPoint(prevPoint, currentPoint, cornerRadius);
                        pathFigure.Segments.Add(new LineSegment(approachPoint, true));

                        Point exitPoint = CalculateCornerExitPoint(currentPoint, nextPoint, cornerRadius);
                        pathFigure.Segments.Add(new QuadraticBezierSegment(currentPoint, exitPoint, true));
                    }
                }
            }
            catch (Exception ex)
            {
                LogDebug($"创建圆角路径失败: {ex.Message}，使用直角路径");
                CreateStraightCornerPath(pathFigure, pathPoints, endPoint);
            }
        }

        /// <summary>
        /// 简化的障碍物检查
        /// </summary>
        private bool HasObstaclesInPath(Point start, Point end, List<Rect> obstacles)
        {
            // 优化：快速边界框检查
            double minX = Math.Min(start.X, end.X);
            double maxX = Math.Max(start.X, end.X);
            double minY = Math.Min(start.Y, end.Y);
            double maxY = Math.Max(start.Y, end.Y);

            foreach (var obstacle in obstacles)
            {
                // 快速边界框排除
                if (obstacle.Right < minX || obstacle.Left > maxX ||
                    obstacle.Bottom < minY || obstacle.Top > maxY)
                    continue;

                // 详细检查
                if (LineIntersectsRect(start, end, obstacle))
                    return true;
            }

            return false;
        }

        #endregion

        #region 工具方法

        /// <summary>
        /// 获取相反方向
        /// </summary>
        private ConnectionDirection GetOppositeDirection(ConnectionDirection direction)
        {
            switch (direction)
            {
                case ConnectionDirection.Top: return ConnectionDirection.Bottom;
                case ConnectionDirection.Bottom: return ConnectionDirection.Top;
                case ConnectionDirection.Left: return ConnectionDirection.Right;
                case ConnectionDirection.Right: return ConnectionDirection.Left;
                default: return direction;
            }
        }

        /// <summary>
        /// 获取延伸点
        /// </summary>
        private Point GetExtensionPoint(Point basePoint, ConnectionDirection direction, double offset)
        {
            switch (direction)
            {
                case ConnectionDirection.Top:
                    return new Point(basePoint.X, basePoint.Y - offset);
                case ConnectionDirection.Bottom:
                    return new Point(basePoint.X, basePoint.Y + offset);
                case ConnectionDirection.Left:
                    return new Point(basePoint.X - offset, basePoint.Y);
                case ConnectionDirection.Right:
                    return new Point(basePoint.X + offset, basePoint.Y);
                default:
                    LogDebug($"未知的连接方向: {direction}");
                    return basePoint;
            }
        }

        private bool IsHorizontalDirection(ConnectionDirection dir)
        {
            return dir == ConnectionDirection.Left || dir == ConnectionDirection.Right;
        }

        private Point CalculateCornerApproachPoint(Point fromPoint, Point cornerPoint, double radius)
        {
            Vector direction = new Vector(cornerPoint.X - fromPoint.X, cornerPoint.Y - fromPoint.Y);
            double distance = direction.Length;

            if (distance == 0) return cornerPoint;

            double actualRadius = Math.Min(radius, distance * 0.4);
            direction.Normalize();

            return new Point(
                cornerPoint.X - direction.X * actualRadius,
                cornerPoint.Y - direction.Y * actualRadius
            );
        }

        private Point CalculateCornerExitPoint(Point cornerPoint, Point toPoint, double radius)
        {
            Vector direction = new Vector(toPoint.X - cornerPoint.X, toPoint.Y - cornerPoint.Y);
            double distance = direction.Length;

            if (distance == 0) return cornerPoint;

            double actualRadius = Math.Min(radius, distance * 0.4);
            direction.Normalize();

            return new Point(
                cornerPoint.X + direction.X * actualRadius,
                cornerPoint.Y + direction.Y * actualRadius
            );
        }

        private void CreateStraightCornerPath(PathFigure pathFigure, List<Point> pathPoints, Point endPoint)
        {
            PolyLineSegment polyLineSegment = new PolyLineSegment();

            foreach (var point in pathPoints)
            {
                polyLineSegment.Points.Add(point);
            }

            polyLineSegment.Points.Add(endPoint);
            pathFigure.Segments.Add(polyLineSegment);
        }

        private void UpdateRightAngleCornerControl(ConnectionInfo connection, List<Point> pathPoints, Point startPoint, Point endPoint)
        {
            if (connection.CornerControl == null) return;

            Point controlPosition;

            if (connection.IsControlPointCustomized)
            {
                controlPosition = connection.ControlPoint;
            }
            else if (pathPoints.Count >= 2)
            {
                controlPosition = pathPoints[1];
            }
            else if (pathPoints.Count == 1)
            {
                controlPosition = pathPoints[0];
            }
            else
            {
                controlPosition = new Point((startPoint.X + endPoint.X) / 2, (startPoint.Y + endPoint.Y) / 2);
            }

            var width = connection.CornerControl.Width > 0 ? connection.CornerControl.Width : connection.CornerControl.ActualWidth;
            var height = connection.CornerControl.Height > 0 ? connection.CornerControl.Height : connection.CornerControl.ActualHeight;

            if (width <= 0) width = 10;
            if (height <= 0) height = 10;

            Canvas.SetLeft(connection.CornerControl, controlPosition.X - width / 2);
            Canvas.SetTop(connection.CornerControl, controlPosition.Y - height / 2);
        }

        private List<Point> CreateHorizontalFirstPerpendicularPath(Point startPoint, ConnectionDirection startDir,
                                                                  Point endPoint, ConnectionDirection endDir, double extension)
        {
            List<Point> points = new List<Point>();

            Point startExt = GetExtensionPoint(startPoint, startDir, extension);
            points.Add(startExt);

            Point endEntryPoint = GetPerpendicularEntryPoint(endPoint, endDir, extension);

            Point horizontalTurn = new Point(endEntryPoint.X, startExt.Y);
            points.Add(horizontalTurn);

            points.Add(endEntryPoint);

            return points;
        }

        private List<Point> CreateVerticalFirstPerpendicularPath(Point startPoint, ConnectionDirection startDir,
                                                                Point endPoint, ConnectionDirection endDir, double extension)
        {
            List<Point> points = new List<Point>();

            Point startExt = GetExtensionPoint(startPoint, startDir, extension);
            points.Add(startExt);

            Point endEntryPoint = GetPerpendicularEntryPoint(endPoint, endDir, extension);

            Point verticalTurn = new Point(startExt.X, endEntryPoint.Y);
            points.Add(verticalTurn);

            points.Add(endEntryPoint);

            return points;
        }

        private Point SmartAdjustPoint(Point point, ConnectionDirection direction, List<Rect> obstacles, string pointName)
        {
            Point adjustedPoint = point;

            LogDebug($"调整{pointName}: 原始点({point.X:F1}, {point.Y:F1}), 方向: {direction}");

            foreach (var obstacle in obstacles)
            {
                double safetyMargin = 15.0;
                Rect safeZone = new Rect(
                    obstacle.X - safetyMargin,
                    obstacle.Y - safetyMargin,
                    obstacle.Width + 2 * safetyMargin,
                    obstacle.Height + 2 * safetyMargin
                );

                if (safeZone.Contains(point))
                {
                    LogDebug($"{pointName}在安全区域内，需要调整");

                    switch (direction)
                    {
                        case ConnectionDirection.Top:
                            adjustedPoint = new Point(point.X, obstacle.Top - safetyMargin);
                            break;

                        case ConnectionDirection.Bottom:
                            adjustedPoint = new Point(point.X, obstacle.Bottom + safetyMargin);
                            break;

                        case ConnectionDirection.Left:
                            adjustedPoint = new Point(obstacle.Left - safetyMargin, point.Y);
                            break;

                        case ConnectionDirection.Right:
                            adjustedPoint = new Point(obstacle.Right + safetyMargin, point.Y);
                            break;
                    }

                    break;
                }
            }

            return adjustedPoint;
        }

        private List<Point> RemoveDuplicatePoints(List<Point> points, double threshold = 2.0)
        {
            if (points.Count <= 1) return points;

            List<Point> cleanedPoints = new List<Point> { points[0] };

            for (int i = 1; i < points.Count; i++)
            {
                Point lastPoint = cleanedPoints.Last();
                Point currentPoint = points[i];

                if (!ArePointsTooClose(lastPoint, currentPoint, threshold))
                {
                    cleanedPoints.Add(currentPoint);
                }
            }

            return cleanedPoints;
        }

        private List<Point> ToOrthogonalPath(List<Point> rawPoints)
        {
            if (rawPoints.Count < 2)
            {
                return rawPoints;
            }

            var result = new List<Point> { rawPoints[0] };

            for (int i = 1; i < rawPoints.Count; i++)
            {
                var prev = result.Last();
                var curr = rawPoints[i];

                double deltaX = Math.Abs(curr.X - prev.X);
                double deltaY = Math.Abs(curr.Y - prev.Y);

                if (deltaX > 10.0 && deltaY > 10.0)
                {
                    Point cornerPoint;

                    if (deltaX >= deltaY)
                    {
                        cornerPoint = new Point(curr.X, prev.Y);
                    }
                    else
                    {
                        cornerPoint = new Point(prev.X, curr.Y);
                    }

                    if (!ArePointsTooClose(prev, cornerPoint, 5.0))
                    {
                        result.Add(cornerPoint);
                    }
                }

                if (!ArePointsTooClose(result.Last(), curr, 5.0))
                {
                    result.Add(curr);
                }
            }

            return result;
        }

        private bool ArePointsTooClose(Point p1, Point p2, double threshold = 1.0)
        {
            double distance = Math.Sqrt(Math.Pow(p1.X - p2.X, 2) + Math.Pow(p1.Y - p2.Y, 2));
            return distance < threshold;
        }

        private bool LineIntersectsRect(Point start, Point end, Rect rect)
        {
            double margin = 5.0;
            Rect expandedRect = new Rect(
                rect.X - margin,
                rect.Y - margin,
                rect.Width + 2 * margin,
                rect.Height + 2 * margin
            );

            bool startInRect = expandedRect.Contains(start);
            bool endInRect = expandedRect.Contains(end);

            if (startInRect || endInRect)
            {
                return true;
            }

            bool intersects = LineSegmentIntersectsRectangleBoundary(start, end, expandedRect);
            return intersects;
        }

        private bool LineSegmentIntersectsRectangleBoundary(Point start, Point end, Rect rect)
        {
            Point topLeft = new Point(rect.Left, rect.Top);
            Point topRight = new Point(rect.Right, rect.Top);
            Point bottomLeft = new Point(rect.Left, rect.Bottom);
            Point bottomRight = new Point(rect.Right, rect.Bottom);

            bool intersects =
                LineSegmentsIntersect(start, end, topLeft, topRight) ||
                LineSegmentsIntersect(start, end, topRight, bottomRight) ||
                LineSegmentsIntersect(start, end, bottomRight, bottomLeft) ||
                LineSegmentsIntersect(start, end, bottomLeft, topLeft);

            return intersects;
        }

        private bool LineSegmentsIntersect(Point p1, Point q1, Point p2, Point q2)
        {
            double d1 = Direction(p2, q2, p1);
            double d2 = Direction(p2, q2, q1);
            double d3 = Direction(p1, q1, p2);
            double d4 = Direction(p1, q1, q2);

            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
            {
                return true;
            }

            if (d1 == 0 && OnSegment(p2, p1, q2)) return true;
            if (d2 == 0 && OnSegment(p2, q1, q2)) return true;
            if (d3 == 0 && OnSegment(p1, p2, q1)) return true;
            if (d4 == 0 && OnSegment(p1, q2, q1)) return true;

            return false;
        }

        private double Direction(Point pi, Point pj, Point pk)
        {
            return (pk.X - pi.X) * (pj.Y - pi.Y) - (pj.X - pi.X) * (pk.Y - pi.Y);
        }

        private bool OnSegment(Point pi, Point pj, Point pk)
        {
            return Math.Min(pi.X, pk.X) <= pj.X && pj.X <= Math.Max(pi.X, pk.X) &&
                   Math.Min(pi.Y, pk.Y) <= pj.Y && pj.Y <= Math.Max(pi.Y, pk.Y);
        }

        private Point GetPerpendicularEntryPoint(Point deviceEdgePoint, ConnectionDirection edgeDirection, double extension)
        {
            switch (edgeDirection)
            {
                case ConnectionDirection.Top:
                    return new Point(deviceEdgePoint.X, deviceEdgePoint.Y - extension);
                case ConnectionDirection.Bottom:
                    return new Point(deviceEdgePoint.X, deviceEdgePoint.Y + extension);
                case ConnectionDirection.Left:
                    return new Point(deviceEdgePoint.X - extension, deviceEdgePoint.Y);
                case ConnectionDirection.Right:
                    return new Point(deviceEdgePoint.X + extension, deviceEdgePoint.Y);
                default:
                    LogDebug($"未知的连接方向: {edgeDirection}");
                    return deviceEdgePoint;
            }
        }

        private Vector GetPerpendicularArrowDirection(ConnectionDirection endDirection)
        {
            switch (endDirection)
            {
                case ConnectionDirection.Top:
                    return new Vector(0, 1);
                case ConnectionDirection.Bottom:
                    return new Vector(0, -1);
                case ConnectionDirection.Left:
                    return new Vector(1, 0);
                case ConnectionDirection.Right:
                    return new Vector(-1, 0);
                default:
                    return new Vector(0, 1);
            }
        }

        private void UpdatePerpendicularArrow(ConnectionInfo connection, Vector direction, Point arrowTipPoint)
        {
            if (connection.ArrowPath == null) return;

            double arrowAngle = Math.Atan2(direction.Y, direction.X) * 180 / Math.PI;
            Point arrowTip = arrowTipPoint;

            PathGeometry arrowGeometry = CreateSimpleArrowGeometry(arrowTip, arrowAngle, 14, 5);
            connection.ArrowPath.Data = arrowGeometry;

            Panel.SetZIndex(connection.ArrowPath, Panel.GetZIndex(connection.ConnectionPath) + 1);
        }

        private List<Point> CalculatePerpendicularPathWithControlPoint(Point startPoint, ConnectionDirection startDir,
                                                                      Point endPoint, ConnectionDirection endDir, Point controlPoint)
        {
            List<Point> points = new List<Point>();

            Point startExt = GetExtensionPoint(startPoint, startDir, 30);
            points.Add(startExt);

            Point endEntry = GetPerpendicularEntryPoint(endPoint, endDir, 30);

            if (IsHorizontalDirection(startDir))
            {
                points.Add(new Point(controlPoint.X, startExt.Y));
                points.Add(controlPoint);
                points.Add(new Point(controlPoint.X, endEntry.Y));
            }
            else
            {
                points.Add(new Point(startExt.X, controlPoint.Y));
                points.Add(controlPoint);
                points.Add(new Point(endEntry.X, controlPoint.Y));
            }

            points.Add(endEntry);

            return points;
        }

        // 辅助方法
        private bool IsDevice(Border deviceBorder)
        {
            return deviceBorder.Tag is string && !string.IsNullOrEmpty(deviceBorder.Tag.ToString());
        }

        private FrameworkElement FindDeviceIconElement(Border deviceBorder)
        {
            if (deviceBorder.Child is DockPanel dockPanel)
            {
                foreach (UIElement child in dockPanel.Children)
                {
                    if (child is Image)
                        return child as FrameworkElement;
                }
            }
            return null;
        }

        private Rect? GetDeviceIconBounds(Border deviceBorder)
        {
            try
            {
                FrameworkElement iconElement = FindDeviceIconElement(deviceBorder);
                if (iconElement == null)
                {
                    double deviceLeft1 = Canvas.GetLeft(deviceBorder);
                    double deviceTop1 = Canvas.GetTop(deviceBorder);
                    double deviceWidth = deviceBorder.ActualWidth;
                    double deviceHeight = deviceBorder.ActualHeight;

                    return new Rect(deviceLeft1, deviceTop1, deviceWidth, deviceHeight);
                }

                double deviceLeft = Canvas.GetLeft(deviceBorder);
                double deviceTop = Canvas.GetTop(deviceBorder);

                double iconWidth = iconElement.ActualWidth > 0 ? iconElement.ActualWidth : iconElement.Width;
                double iconHeight = iconElement.ActualHeight > 0 ? iconElement.ActualHeight : iconElement.Height;

                if (iconWidth <= 0) iconWidth = 80;
                if (iconHeight <= 0) iconHeight = 80;

                double iconLeft = deviceLeft + (deviceBorder.ActualWidth - iconWidth) / 2;
                double iconTop = deviceTop + (deviceBorder.ActualHeight - iconHeight) / 2;

                return new Rect(iconLeft, iconTop, iconWidth, iconHeight);
            }
            catch (Exception ex)
            {
                LogDebug($"获取设备图标边界时出错: {ex.Message}");
                return null;
            }
        }

        private PathGeometry CreateSimpleArrowGeometry(Point tip, double angle, double length, double width)
        {
            PathGeometry geometry = new PathGeometry();
            PathFigure figure = new PathFigure();
            figure.StartPoint = tip;

            double radians = angle * Math.PI / 180;

            Point p1 = new Point(
                tip.X - length * Math.Cos(radians) + width * Math.Sin(radians),
                tip.Y - length * Math.Sin(radians) - width * Math.Cos(radians)
            );

            Point p2 = new Point(
                tip.X - length * Math.Cos(radians) - width * Math.Sin(radians),
                tip.Y - length * Math.Sin(radians) + width * Math.Cos(radians)
            );

            figure.Segments.Add(new LineSegment(p1, true));
            figure.Segments.Add(new LineSegment(p2, true));
            figure.IsClosed = true;

            geometry.Figures.Add(figure);
            return geometry;
        }

        private void PositionConnectionLabel(ConnectionInfo connection)
        {
            if (connection.ConnectionLabel == null) return;

            var pathGeometry = connection.ConnectionPath.Data as PathGeometry;
            if (pathGeometry?.Figures.FirstOrDefault() is PathFigure figure)
            {
                var startPoint = figure.StartPoint;
                var segments = figure.Segments;

                if (segments.Count > 0)
                {
                    Point midPoint = startPoint;
                    Canvas.SetLeft(connection.ConnectionLabel, midPoint.X);
                    Canvas.SetTop(connection.ConnectionLabel, midPoint.Y);
                }
            }
        }

        private void UpdateAnimationPath(ConnectionInfo connection)
        {
            // 动画路径更新逻辑
        }

        /// <summary>
        /// 创建简单回退路径
        /// </summary>
        private void CreateSimpleFallbackPath(ConnectionInfo connection)
        {
            try
            {
                var startPoint = GetTransformedConnectionPointPosition(connection.StartDevice, connection.StartAnchor.Direction);
                var endPoint = GetTransformedConnectionPointPosition(connection.EndDevice, connection.EndAnchor.Direction);

                var geometry = new PathGeometry();
                var figure = new PathFigure { StartPoint = startPoint };
                figure.Segments.Add(new LineSegment(endPoint, true));
                geometry.Figures.Add(figure);

                connection.ConnectionPath.Data = geometry;

                // 简单箭头
                Vector arrowDirection = GetPerpendicularArrowDirection(connection.EndAnchor.Direction);
                double arrowAngle = Math.Atan2(arrowDirection.Y, arrowDirection.X) * 180 / Math.PI;
                connection.ArrowPath.Data = CreateSimpleArrowGeometry(endPoint, arrowAngle, 10, 4);
            }
            catch { }
        }

        /// <summary>
        /// 清理连接缓存
        /// </summary>
        private void CleanupConnectionCache(string connectionId)
        {
            // 🔥 清理普通缓存
            var keysToRemove = _pathCache.Keys.Where(key => key.Contains(connectionId)).ToList();
            foreach (var key in keysToRemove)
            {
                _pathCache.Remove(key);
            }

            // 🔥 清理拖拽缓存
            var dragKeysToRemove = _dragPathCache.Keys.Where(key => key.Contains(connectionId)).ToList();
            foreach (var key in dragKeysToRemove)
            {
                _dragPathCache.Remove(key);
            }
        }

        /// <summary>
        /// 简化的颜色应用
        /// </summary>
        private void EnsureConnectionColors(ConnectionInfo connection)
        {
            try
            {
                if (connection.ConnectionPath != null)
                {
                    connection.ConnectionPath.Stroke = connection.ConnectionColor ?? new SolidColorBrush(SlateGray);
                }

                if (connection.ArrowPath != null)
                {
                    var color = connection.ConnectionColor ?? new SolidColorBrush(SlateGray);
                    connection.ArrowPath.Fill = color;
                    connection.ArrowPath.Stroke = color;
                }
            }
            catch (Exception ex)
            {
                LogDebug($"应用连接线颜色时出错: {ex.Message}");
            }
        }

        #endregion
    }
}