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
    /// 管道吸附服务实现（QuadTree 优化版 - 仅收集 IPipeSnapPoints）
    /// </summary>
    public class PipeSnapService : IPipeSnapService
    {
        private const double SnapDistance = 15.0;
        private const double SnapHighlightDistance = 25.0;
        private const double QuadTreeQueryRadius = 50.0;

        private Canvas _canvas;
        private readonly List<Ellipse> _snapIndicators = new List<Ellipse>();

        //  四叉树（空间分区）
        private QuadTree<SnapPoint> _snapPointQuadTree;
        private Rect _canvasBounds;
        private bool _quadTreeNeedsRebuild = true;

        //  缓存计时器（定期重建四叉树）
        private System.Windows.Threading.DispatcherTimer _rebuildTimer;
        private DateTime _lastRebuildTime = DateTime.MinValue;
        private const int RebuildIntervalMs = 500;

        /// <summary>
        /// 初始化吸附服务
        /// </summary>
        public void Initialize(Canvas canvas)
        {
            _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));

            _canvasBounds = new Rect(0, 0, 10000, 10000);
            _snapPointQuadTree = new QuadTree<SnapPoint>(_canvasBounds);

            _rebuildTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(RebuildIntervalMs)
            };
            _rebuildTimer.Tick += (s, e) =>
            {
                _rebuildTimer.Stop();
                RebuildQuadTree();
            };

            Debug.WriteLine(" 吸附服务已初始化（QuadTree优化 - 仅收集 IPipeSnapPoints）");
        }

        #region QuadTree 管理

        /// <summary>
        /// 标记需要重建四叉树
        /// </summary>
        public void MarkQuadTreeDirty()
        {
            _quadTreeNeedsRebuild = true;

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
                return;
            }
            _lastRebuildTime = now;

            var sw = Stopwatch.StartNew();

            _snapPointQuadTree.Clear();

            var snapPoints = new List<SnapPoint>();

            //  只收集实现了 IPipeSnapPoints 接口的元素
            CollectSnapPointsFromInterface(snapPoints, null);

            foreach (var point in snapPoints)
            {
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
            Debug.WriteLine($" QuadTree 重建完成: {snapPoints.Count} 个吸附点，耗时 {sw.ElapsedMilliseconds}ms");
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

            EnsureQuadTreeUpdated();

            var pipeEndPoints = GetPipeEndPoints(pipeElement, currentCenter);
            if (pipeEndPoints == null || pipeEndPoints.Count == 0)
                return null;

            var solution = new PipeSnapSolution();

            foreach (var endPoint in pipeEndPoints)
            {
                var nearbySnapPoints = _snapPointQuadTree.QueryRadius(
                    endPoint.Position,
                    QuadTreeQueryRadius
                );

                if (!string.IsNullOrEmpty(excludePipeId))
                {
                    nearbySnapPoints = nearbySnapPoints
                        .Where(p => p.TargetId != excludePipeId)
                        .ToList();
                }

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

            // 使用距离最近的吸附点作为主要参考
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

            EnsureQuadTreeUpdated();

            var nearbySnapPoints = _snapPointQuadTree.QueryRadius(
                currentPosition,
                QuadTreeQueryRadius
            );

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

        #region 收集吸附点（ 只从 IPipeSnapPoints 收集）

        /// <summary>
        ///  只收集实现了 IPipeSnapPoints 接口的元素的吸附点
        /// </summary>
        private void CollectSnapPointsFromInterface(List<SnapPoint> snapPoints, string excludePipeId)
        {
            if (_canvas == null)
                return;

            // 遍历画布上所有实现了 IPipeSnapPoints 接口的元素
            var snapProviders = _canvas.Children
                .OfType<IPipeSnapPoints>()
                .ToList();

            int deviceCount = 0;
            int pipeCount = 0;

            foreach (var provider in snapProviders)
            {
                // 获取元素的 ID（用于排除）
                string elementId = null;

                if (provider is StraightPipeControl straightPipe)
                {
                    elementId = straightPipe.PipeId;
                    pipeCount++;
                }
                else if (provider is ElbowPipeControl elbowPipe)
                {
                    elementId = elbowPipe.PipeId;
                    pipeCount++;
                }
                else if (provider is ZShapePipeControl zPipe)
                {
                    elementId = zPipe.PipeId;
                    pipeCount++;
                }
                else if (provider is ConicalFlaskControl flaskControl)
                {
                    elementId = flaskControl.FlaskId;
                    deviceCount++;
                }
                else
                {
                    //  对于其他类型的设备，尝试通过反射获取 ID
                    // 假设设备有 DeviceId 或类似属性
                    var idProperty = provider.GetType().GetProperty("DeviceId")
                                  ?? provider.GetType().GetProperty("Id")
                                  ?? provider.GetType().GetProperty("ElementId");

                    if (idProperty != null)
                    {
                        elementId = idProperty.GetValue(provider) as string;
                        deviceCount++;
                    }
                    else
                    {
                        // 如果没有 ID 属性，生成一个临时 ID
                        elementId = $"Unknown_{provider.GetHashCode()}";
                        deviceCount++;
                    }
                }

                // 如果是被排除的元素，跳过
                if (elementId == excludePipeId)
                    continue;

                try
                {
                    // 获取该元素提供的吸附点
                    var providerSnapPoints = provider.GetSnapPoints();

                    if (providerSnapPoints == null || providerSnapPoints.Count == 0)
                        continue;

                    foreach (var point in providerSnapPoints)
                    {
                        snapPoints.Add(new SnapPoint
                        {
                            Position = point.WorldPosition,
                            Type = SnapPointType.PipeEnd, // 统一使用 PipeEnd 类型
                            TargetId = elementId,
                            Direction = ConvertSnapDirection(point.Direction)
                        });
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($" 获取元素 {elementId} 的吸附点时出错: {ex.Message}");
                }
            }

            Debug.WriteLine($"收集吸附点: {deviceCount} 个设备, {pipeCount} 个管道, 共 {snapPoints.Count} 个吸附点");
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

        #region 吸附指示器

        /// <summary>
        /// 显示单个吸附指示器
        /// </summary>
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

        /// <summary>
        /// 显示多个吸附指示器
        /// </summary>
        public void ShowSnapIndicators(List<EndPointSnapInfo> snapInfos)
        {
            HideSnapIndicator();

            if (snapInfos == null || snapInfos.Count == 0)
                return;

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

        /// <summary>
        /// 创建吸附指示器
        /// </summary>
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

        /// <summary>
        /// 隐藏吸附指示器
        /// </summary>
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

        /// <summary>
        /// 计算两点之间的距离
        /// </summary>
        private double CalculateDistance(Point p1, Point p2)
        {
            var dx = p1.X - p2.X;
            var dy = p1.Y - p2.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        #endregion

        #region 清理资源

        /// <summary>
        /// 清理资源
        /// </summary>
        public void Cleanup()
        {
            _rebuildTimer?.Stop();
            _rebuildTimer = null;

            _snapPointQuadTree?.Clear();
            _snapPointQuadTree = null;

            foreach (var indicator in _snapIndicators)
            {
                if (indicator != null && _canvas != null)
                {
                    _canvas.Children.Remove(indicator);
                }
            }
            _snapIndicators.Clear();

            Debug.WriteLine(" 吸附服务资源已清理");
        }

        #endregion
    }
}