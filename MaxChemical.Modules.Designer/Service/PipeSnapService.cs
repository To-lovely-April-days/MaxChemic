using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Views.Controls;

namespace MaxChemical.Modules.Designer.Service
{
    /// <summary>
    /// 管道吸附服务实现（QuadTree 优化版）
    /// </summary>
    public class PipeSnapService : IPipeSnapService
    {
        private const double SnapDistance = 15.0;
        private const double SnapHighlightDistance = 25.0;
        private const double QuadTreeQueryRadius = 50.0; // ✅ QuadTree 查询半径

        private Canvas _canvas;
        private readonly List<Ellipse> _snapIndicators = new List<Ellipse>();

        // ✅ 四叉树（空间分区）
        private QuadTree<SnapPoint> _snapPointQuadTree;
        private Rect _canvasBounds;
        private bool _quadTreeNeedsRebuild = true;

        // ✅ 缓存计时器（定期重建四叉树）
        private System.Windows.Threading.DispatcherTimer _rebuildTimer;
        private DateTime _lastRebuildTime = DateTime.MinValue;
        private const int RebuildIntervalMs = 500; // 500ms 内最多重建一次

        /// <summary>
        /// 初始化吸附服务
        /// </summary>
        public void Initialize(Canvas canvas)
        {
            _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));

            // 初始化画布边界
            _canvasBounds = new Rect(0, 0, 10000, 10000); // 默认边界
            _snapPointQuadTree = new QuadTree<SnapPoint>(_canvasBounds);

            // ✅ 初始化重建计时器
            _rebuildTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(RebuildIntervalMs)
            };
            _rebuildTimer.Tick += (s, e) =>
            {
                _rebuildTimer.Stop();
                RebuildQuadTree();
            };

            Debug.WriteLine("吸附服务已初始化（QuadTree优化）");
        }

        #region QuadTree 管理

        /// <summary>
        /// 标记需要重建四叉树
        /// </summary>
        public void MarkQuadTreeDirty()
        {
            _quadTreeNeedsRebuild = true;

            // ✅ 启动延迟重建（避免频繁重建）
            if (!_rebuildTimer.IsEnabled)
            {
                _rebuildTimer.Start();
            }
        }

        /// <summary>
        /// 重建四叉树
        /// </summary>
        private void RebuildQuadTree()
        {
            var now = DateTime.Now;
            if ((now - _lastRebuildTime).TotalMilliseconds < RebuildIntervalMs)
            {
                return; // 限制重建频率
            }
            _lastRebuildTime = now;

            var sw = Stopwatch.StartNew();

            _snapPointQuadTree.Clear();

            var snapPoints = new List<SnapPoint>();
            CollectDeviceSnapPoints(snapPoints);
            CollectPipeSnapPoints(snapPoints, null);

            foreach (var point in snapPoints)
            {
                // 为每个吸附点创建一个小的边界框
                var bounds = new Rect(
                    point.Position.X - 5,
                    point.Position.Y - 5,
                    10,
                    10
                );
                _snapPointQuadTree.Insert(bounds, point);
            }

            _quadTreeNeedsRebuild = false;
            sw.Stop();
            Debug.WriteLine($"✅ QuadTree 重建完成: {snapPoints.Count} 个吸附点，耗时 {sw.ElapsedMilliseconds}ms");
        }

        /// <summary>
        /// 确保四叉树是最新的
        /// </summary>
        private void EnsureQuadTreeUpdated()
        {
            if (_quadTreeNeedsRebuild)
            {
                RebuildQuadTree();
            }
        }

        #endregion

        #region 双端点吸附检测（QuadTree 优化）

        /// <summary>
        /// 为管道查找最佳吸附方案（使用 QuadTree）
        /// </summary>
        public PipeSnapSolution FindBestSnapSolution(
            UIElement pipeElement,
            Point currentCenter,
            string excludePipeId = null)
        {
            if (_canvas == null || pipeElement == null)
                return null;

            // ✅ 确保四叉树已更新
            EnsureQuadTreeUpdated();

            // 获取管道的端点
            var pipeEndPoints = GetPipeEndPoints(pipeElement, currentCenter);
            if (pipeEndPoints == null || pipeEndPoints.Count == 0)
                return null;

            var solution = new PipeSnapSolution();

            // ✅ 为每个端点使用 QuadTree 查询附近的吸附点
            foreach (var endPoint in pipeEndPoints)
            {
                var nearbySnapPoints = _snapPointQuadTree.QueryRadius(
                    endPoint.Position,
                    QuadTreeQueryRadius
                );

                // 过滤掉被排除的管道
                if (!string.IsNullOrEmpty(excludePipeId))
                {
                    nearbySnapPoints = nearbySnapPoints
                        .Where(p => p.TargetId != excludePipeId)
                        .ToList();
                }

                // 在附近的点中查找最佳吸附点
                var nearestSnap = FindClosestSnapPointForEndPoint(
                    endPoint.Position,
                    nearbySnapPoints
                );

                if (nearestSnap != null && nearestSnap.ShouldSnap)
                {
                    solution.EndPointSnaps.Add(new EndPointSnapInfo
                    {
                        EndPointType = endPoint.Type,
                        OriginalPosition = endPoint.Position,
                        SnapTarget = nearestSnap.SnapPoint,
                        Distance = nearestSnap.Distance
                    });
                }
            }

            // 计算吸附后的中心位置
            if (solution.EndPointSnaps.Count > 0)
            {
                solution.HasSnap = true;
                solution.NewCenterPosition = CalculateNewCenterPosition(
                    pipeElement,
                    currentCenter,
                    solution.EndPointSnaps
                );
            }

            return solution;
        }

        /// <summary>
        /// 获取管道的端点位置
        /// </summary>
        private List<PipeEndPoint> GetPipeEndPoints(UIElement pipeElement, Point currentCenter)
        {
            var endPoints = new List<PipeEndPoint>();

            if (pipeElement is IPipeSnapPoints snapPointsProvider)
            {
                var snapPoints = snapPointsProvider.GetSnapPoints();

                foreach (var snapPoint in snapPoints)
                {
                    endPoints.Add(new PipeEndPoint
                    {
                        Type = PipeEndPointType.Generic,
                        Position = snapPoint.WorldPosition,
                        Direction = ConvertSnapDirection(snapPoint.Direction)
                    });
                }
            }

            return endPoints;
        }

        /// <summary>
        /// 为单个端点查找最近的吸附点（从预筛选的列表中）
        /// </summary>
        private SnapResult FindClosestSnapPointForEndPoint(
            Point endPointPosition,
            List<SnapPoint> nearbySnapPoints)
        {
            if (nearbySnapPoints == null || nearbySnapPoints.Count == 0)
                return null;

            SnapPoint nearestPoint = null;
            double minDistance = double.MaxValue;

            foreach (var point in nearbySnapPoints)
            {
                var distance = CalculateDistance(endPointPosition, point.Position);

                if (distance < minDistance)
                {
                    minDistance = distance;
                    nearestPoint = point;
                }
            }

            if (nearestPoint != null && minDistance <= SnapDistance)
            {
                return new SnapResult
                {
                    SnapPoint = nearestPoint,
                    Distance = minDistance,
                    ShouldSnap = true
                };
            }

            if (nearestPoint != null && minDistance <= SnapHighlightDistance)
            {
                return new SnapResult
                {
                    SnapPoint = nearestPoint,
                    Distance = minDistance,
                    ShouldSnap = false
                };
            }

            return null;
        }

        /// <summary>
        /// 计算吸附后的新中心位置
        /// </summary>
        private Point CalculateNewCenterPosition(
            UIElement pipeElement,
            Point currentCenter,
            List<EndPointSnapInfo> snapInfos)
        {
            if (snapInfos.Count == 0)
                return currentCenter;

            var primarySnap = snapInfos.OrderBy(s => s.Distance).First();

            var offset = new Point(
                primarySnap.SnapTarget.Position.X - primarySnap.OriginalPosition.X,
                primarySnap.SnapTarget.Position.Y - primarySnap.OriginalPosition.Y
            );

            return new Point(
                currentCenter.X + offset.X,
                currentCenter.Y + offset.Y
            );
        }

        #endregion

        #region 单点吸附检测（QuadTree 优化）

        /// <summary>
        /// 查找最近的吸附点（使用 QuadTree）
        /// </summary>
        public SnapResult FindNearestSnapPoint(Point currentPosition, string excludePipeId = null)
        {
            if (_canvas == null)
                return null;

            // ✅ 确保四叉树已更新
            EnsureQuadTreeUpdated();

            // ✅ 使用 QuadTree 查询附近的吸附点
            var nearbySnapPoints = _snapPointQuadTree.QueryRadius(
                currentPosition,
                QuadTreeQueryRadius
            );

            // 过滤排除的管道
            if (!string.IsNullOrEmpty(excludePipeId))
            {
                nearbySnapPoints = nearbySnapPoints
                    .Where(p => p.TargetId != excludePipeId)
                    .ToList();
            }

            return FindClosestSnapPoint(currentPosition, nearbySnapPoints);
        }

        /// <summary>
        /// 从预筛选列表中查找最近的吸附点
        /// </summary>
        private SnapResult FindClosestSnapPoint(Point currentPosition, List<SnapPoint> snapPoints)
        {
            if (snapPoints == null || snapPoints.Count == 0)
                return null;

            SnapPoint nearestPoint = null;
            double minDistance = double.MaxValue;

            foreach (var point in snapPoints)
            {
                var distance = CalculateDistance(currentPosition, point.Position);

                if (distance < minDistance)
                {
                    minDistance = distance;
                    nearestPoint = point;
                }
            }

            if (nearestPoint != null && minDistance <= SnapDistance)
            {
                return new SnapResult
                {
                    SnapPoint = nearestPoint,
                    Distance = minDistance,
                    ShouldSnap = true
                };
            }

            if (nearestPoint != null && minDistance <= SnapHighlightDistance)
            {
                return new SnapResult
                {
                    SnapPoint = nearestPoint,
                    Distance = minDistance,
                    ShouldSnap = false
                };
            }

            return null;
        }

        #endregion

        #region 收集吸附点（保持原有逻辑）

        /// <summary>
        /// 收集设备的吸附点
        /// </summary>
        private void CollectDeviceSnapPoints(List<SnapPoint> snapPoints)
        {
            var deviceElements = _canvas.Children
                .OfType<Border>()
                .Where(b => b.Tag is string deviceId && !string.IsNullOrEmpty(deviceId))
                .ToList();

            foreach (var device in deviceElements)
            {
                var deviceId = device.Tag as string;
                snapPoints.AddRange(GetDeviceConnectionPoints(device, deviceId));
            }
        }

        /// <summary>
        /// 获取设备的连接点
        /// </summary>
        private List<SnapPoint> GetDeviceConnectionPoints(Border device, string deviceId)
        {
            var points = new List<SnapPoint>();

            var left = Canvas.GetLeft(device);
            var top = Canvas.GetTop(device);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;

            var width = device.ActualWidth;
            var height = device.ActualHeight;

            points.Add(new SnapPoint
            {
                Position = new Point(left, top + height / 2),
                Type = SnapPointType.DeviceLeft,
                TargetId = deviceId,
                Direction = ConnectionDirection.Left
            });

            points.Add(new SnapPoint
            {
                Position = new Point(left + width, top + height / 2),
                Type = SnapPointType.DeviceRight,
                TargetId = deviceId,
                Direction = ConnectionDirection.Right
            });

            points.Add(new SnapPoint
            {
                Position = new Point(left + width / 2, top),
                Type = SnapPointType.DeviceTop,
                TargetId = deviceId,
                Direction = ConnectionDirection.Top
            });

            points.Add(new SnapPoint
            {
                Position = new Point(left + width / 2, top + height),
                Type = SnapPointType.DeviceBottom,
                TargetId = deviceId,
                Direction = ConnectionDirection.Bottom
            });

            return points;
        }

        /// <summary>
        /// 收集管道的吸附点
        /// </summary>
        private void CollectPipeSnapPoints(List<SnapPoint> snapPoints, string excludePipeId)
        {
            var pipeElements = _canvas.Children
                .OfType<IPipeSnapPoints>()
                .ToList();

            foreach (var pipe in pipeElements)
            {
                string pipeId = null;
                if (pipe is StraightPipeControl straightPipe)
                    pipeId = straightPipe.PipeId;
                else if (pipe is ElbowPipeControl elbowPipe)
                    pipeId = elbowPipe.PipeId;
                else if (pipe is ZShapePipeControl zPipe)
                    pipeId = zPipe.PipeId;

                if (pipeId == excludePipeId)
                    continue;

                var pipeSnapPoints = pipe.GetSnapPoints();

                foreach (var point in pipeSnapPoints)
                {
                    snapPoints.Add(new SnapPoint
                    {
                        Position = point.WorldPosition,
                        Type = SnapPointType.PipeEnd,
                        TargetId = pipeId,
                        Direction = ConvertSnapDirection(point.Direction)
                    });
                }
            }
        }

        /// <summary>
        /// 转换吸附方向
        /// </summary>
        private ConnectionDirection ConvertSnapDirection(SnapDirection snapDirection)
        {
            switch (snapDirection)
            {
                case SnapDirection.Left: return ConnectionDirection.Left;
                case SnapDirection.Right: return ConnectionDirection.Right;
                case SnapDirection.Up: return ConnectionDirection.Top;
                case SnapDirection.Down: return ConnectionDirection.Bottom;
                default: return ConnectionDirection.Right;
            }
        }

        #endregion

        #region 吸附指示器（保持原有逻辑）

        public void ShowSnapIndicator(Point position)
        {
            Ellipse indicator;
            if (_snapIndicators.Count > 0)
            {
                indicator = _snapIndicators[0];
            }
            else
            {
                indicator = CreateSnapIndicator();
                _snapIndicators.Add(indicator);
            }

            Canvas.SetLeft(indicator, position.X - 6);
            Canvas.SetTop(indicator, position.Y - 6);
            indicator.Visibility = Visibility.Visible;
        }

        public void ShowSnapIndicators(List<EndPointSnapInfo> snapInfos)
        {
            HideSnapIndicator();

            for (int i = 0; i < snapInfos.Count; i++)
            {
                Ellipse indicator;

                if (i < _snapIndicators.Count)
                {
                    indicator = _snapIndicators[i];
                }
                else
                {
                    indicator = CreateSnapIndicator();
                    _snapIndicators.Add(indicator);
                }

                var position = snapInfos[i].SnapTarget.Position;
                Canvas.SetLeft(indicator, position.X - 6);
                Canvas.SetTop(indicator, position.Y - 6);
                indicator.Visibility = Visibility.Visible;
            }
        }

        private Ellipse CreateSnapIndicator()
        {
            var indicator = new Ellipse
            {
                Width = 12,
                Height = 12,
                Fill = new SolidColorBrush(Color.FromArgb(150, 255, 165, 0)),
                Stroke = Brushes.White,
                StrokeThickness = 2,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };

            indicator.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Orange,
                BlurRadius = 8,
                ShadowDepth = 0,
                Opacity = 0.8
            };

            Panel.SetZIndex(indicator, 10000);
            _canvas?.Children.Add(indicator);

            return indicator;
        }

        public void HideSnapIndicator()
        {
            foreach (var indicator in _snapIndicators)
            {
                if (indicator != null)
                {
                    indicator.Visibility = Visibility.Collapsed;
                }
            }
        }

        #endregion

        #region 辅助方法

        private double CalculateDistance(Point p1, Point p2)
        {
            var dx = p1.X - p2.X;
            var dy = p1.Y - p2.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        #endregion

        /// <summary>
        /// 清理资源
        /// </summary>
        public void Cleanup()
        {
            _rebuildTimer?.Stop();
            _snapPointQuadTree?.Clear();

            foreach (var indicator in _snapIndicators)
            {
                if (indicator != null && _canvas != null)
                {
                    _canvas.Children.Remove(indicator);
                }
            }
            _snapIndicators.Clear();

            Debug.WriteLine("吸附服务资源已清理");
        }
    }
}