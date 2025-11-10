// Services/PipeSnapService.cs
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
    /// 管道吸附服务实现
    /// </summary>
    public class PipeSnapService : IPipeSnapService
    {
        private const double SnapDistance = 15.0; // 吸附距离阈值(像素)
        private const double SnapHighlightDistance = 25.0; // 显示吸附提示的距离

        private Canvas _canvas;
        private readonly List<Ellipse> _snapIndicators = new List<Ellipse>(); // 支持多个指示器

        /// <summary>
        /// 初始化吸附服务
        /// </summary>
        public void Initialize(Canvas canvas)
        {
            _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
            Debug.WriteLine("吸附服务已初始化");
        }

        #region 双端点吸附检测（新方法）

        /// <summary>
        /// 为管道查找最佳吸附方案（双端点）
        /// </summary>
        public PipeSnapSolution FindBestSnapSolution(
            UIElement pipeElement,
            Point currentCenter,
            string excludePipeId = null)
        {
            if (_canvas == null || pipeElement == null)
                return null;

            var snapPoints = new List<SnapPoint>();

            // 收集所有吸附目标
            CollectDeviceSnapPoints(snapPoints);
            CollectPipeSnapPoints(snapPoints, excludePipeId);

            // 获取管道的两个端点
            var pipeEndPoints = GetPipeEndPoints(pipeElement, currentCenter);

            if (pipeEndPoints == null || pipeEndPoints.Count == 0)
                return null;

            // 为每个端点查找最佳吸附点
            var solution = new PipeSnapSolution();

            foreach (var endPoint in pipeEndPoints)
            {
                var nearestSnap = FindClosestSnapPointForEndPoint(
                    endPoint.Position,
                    snapPoints);

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

            // 计算吸附后的管道中心位置
            if (solution.EndPointSnaps.Count > 0)
            {
                solution.HasSnap = true;
                solution.NewCenterPosition = CalculateNewCenterPosition(
                    pipeElement,
                    currentCenter,
                    solution.EndPointSnaps);

                Debug.WriteLine($"找到 {solution.EndPointSnaps.Count} 个端点可以吸附");
            }

            return solution;
        }

        /// <summary>
        /// 获取管道的端点位置（使用组件自己提供的吸附点）
        /// </summary>
        private List<PipeEndPoint> GetPipeEndPoints(UIElement pipeElement, Point currentCenter)
        {
            var endPoints = new List<PipeEndPoint>();

            // ✅ 如果管道实现了 IPipeSnapPoints 接口，直接获取吸附点
            if (pipeElement is IPipeSnapPoints snapPointsProvider)
            {
                var snapPoints = snapPointsProvider.GetSnapPoints();

                foreach (var snapPoint in snapPoints)
                {
                    endPoints.Add(new PipeEndPoint
                    {
                        Type = PipeEndPointType.Generic, // 统一类型
                        Position = snapPoint.WorldPosition,
                        Direction = ConvertSnapDirection(snapPoint.Direction)
                    });
                }
            }

            return endPoints;
        }


        /// <summary>
        /// 为单个端点查找最近的吸附点
        /// </summary>
        private SnapResult FindClosestSnapPointForEndPoint(
            Point endPointPosition,
            List<SnapPoint> snapPoints)
        {
            SnapPoint nearestPoint = null;
            double minDistance = double.MaxValue;

            foreach (var point in snapPoints)
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

            // 使用第一个吸附点（优先级最高的，即距离最近的）
            var primarySnap = snapInfos.OrderBy(s => s.Distance).First();

            // 计算偏移量
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

        #region 单点吸附检测（保留用于兼容）

        /// <summary>
        /// 查找最近的吸附点
        /// </summary>
        public SnapResult FindNearestSnapPoint(Point currentPosition, string excludePipeId = null)
        {
            if (_canvas == null)
                return null;

            var snapPoints = new List<SnapPoint>();

            // 收集设备连接点
            CollectDeviceSnapPoints(snapPoints);

            // 收集其他管道的端点
            CollectPipeSnapPoints(snapPoints, excludePipeId);

            // 查找最近的吸附点
            return FindClosestSnapPoint(currentPosition, snapPoints);
        }

        /// <summary>
        /// 查找最近的吸附点
        /// </summary>
        private SnapResult FindClosestSnapPoint(Point currentPosition, List<SnapPoint> snapPoints)
        {
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

            // 只有在吸附距离内才返回结果
            if (nearestPoint != null && minDistance <= SnapDistance)
            {
                return new SnapResult
                {
                    SnapPoint = nearestPoint,
                    Distance = minDistance,
                    ShouldSnap = true
                };
            }

            // 在高亮距离内显示提示
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

        #region 收集吸附点

        /// <summary>
        /// 收集设备的吸附点
        /// </summary>
        private void CollectDeviceSnapPoints(List<SnapPoint> snapPoints)
        {
            // 查找所有设备元素
            var deviceElements = _canvas.Children
                .OfType<Border>()
                .Where(b => b.Tag is string deviceId && !string.IsNullOrEmpty(deviceId))
                .ToList();

            foreach (var device in deviceElements)
            {
                var deviceId = device.Tag as string;

                // 获取设备的四个方向连接点
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

            // 左侧连接点
            points.Add(new SnapPoint
            {
                Position = new Point(left, top + height / 2),
                Type = SnapPointType.DeviceLeft,
                TargetId = deviceId,
                Direction = ConnectionDirection.Left
            });

            // 右侧连接点
            points.Add(new SnapPoint
            {
                Position = new Point(left + width, top + height / 2),
                Type = SnapPointType.DeviceRight,
                TargetId = deviceId,
                Direction = ConnectionDirection.Right
            });

            // 顶部连接点
            points.Add(new SnapPoint
            {
                Position = new Point(left + width / 2, top),
                Type = SnapPointType.DeviceTop,
                TargetId = deviceId,
                Direction = ConnectionDirection.Top
            });

            // 底部连接点
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
            // 查找所有实现了 IPipeSnapPoints 接口的管道
            var pipeElements = _canvas.Children
                .OfType<IPipeSnapPoints>()
                .ToList();

            foreach (var pipe in pipeElements)
            {
                // 获取管道ID（需要转换回具体类型）
                string pipeId = null;
                if (pipe is StraightPipeControl straightPipe)
                    pipeId = straightPipe.PipeId;
                else if (pipe is ElbowPipeControl elbowPipe)
                    pipeId = elbowPipe.PipeId;
                else if (pipe is ZShapePipeControl zPipe)
                    pipeId = zPipe.PipeId;

                // 跳过被排除的管道
                if (pipeId == excludePipeId)
                    continue;

                // ✅ 获取管道自己提供的吸附点
                var pipeSnapPoints = pipe.GetSnapPoints();

                // 转换为通用的 SnapPoint 格式
                foreach (var point in pipeSnapPoints)
                {
                    snapPoints.Add(new SnapPoint
                    {
                        Position = point.WorldPosition,
                        Type = SnapPointType.PipeEnd,
                        TargetId = pipeId,
                        Direction = ConvertSnapDirection(point.Direction)
                    });

                    Debug.WriteLine($"收集到吸附点: {point.Description} at ({point.WorldPosition.X:F1}, {point.WorldPosition.Y:F1})");
                }
            }
        }
        /// <summary>
        /// 转换吸附方向枚举
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
            // 复用现有指示器或创建新的
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
            // 先隐藏所有现有指示器
            HideSnapIndicator();

            // 为每个吸附点创建或复用指示器
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
                Fill = new SolidColorBrush(Color.FromArgb(150, 255, 165, 0)), // 半透明橙色
                Stroke = Brushes.White,
                StrokeThickness = 2,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };

            // 添加发光效果
            indicator.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Orange,
                BlurRadius = 8,
                ShadowDepth = 0,
                Opacity = 0.8
            };

            Panel.SetZIndex(indicator, 10000); // 确保在最上层
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
        /// 根据角度获取连接方向
        /// </summary>
        private ConnectionDirection GetDirectionFromAngle(double radians)
        {
            var degrees = (radians * 180 / Math.PI + 360) % 360;

            if (degrees >= 315 || degrees < 45) return ConnectionDirection.Right;
            if (degrees >= 45 && degrees < 135) return ConnectionDirection.Bottom;
            if (degrees >= 135 && degrees < 225) return ConnectionDirection.Left;
            return ConnectionDirection.Top;
        }

        /// <summary>
        /// 计算两点间距离
        /// </summary>
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