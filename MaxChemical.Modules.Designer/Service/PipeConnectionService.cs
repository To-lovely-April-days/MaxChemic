// Services/PipeConnectionService.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using MaxChemical.Modules.Designer.Views.Controls;
using MaxChemical.Modules.Designer.Models;

namespace MaxChemical.Modules.Designer.Service
{
    public interface IPipeConnectionService
    {
        // 管道字典
        Dictionary<string, PipeConnectionInfo> Pipes { get; }

        // 添加管道到画布
        void AddPipeToCanvas(StraightPipeControl pipe, Point position);

        // 移除管道
        void RemovePipe(string pipeId);

        // 检查锚点连接
        bool TryConnectToNearestAnchor(string pipeId, PipeAnchorPosition anchorPosition, Point currentPosition);

        // 断开连接
        void DisconnectPipeAnchor(string pipeId, PipeAnchorPosition anchorPosition);

        // 获取画布
        Func<Canvas> GetCanvasFunc { get; set; }

        // 查找设备元素
        Func<string, FrameworkElement> FindDeviceElementFunc { get; set; }

        /// <summary>
        /// 添加管道到画布(支持所有管道类型)
        /// </summary>
        void AddPipeToCanvas(UIElement pipe, Point position);
    }

    public class PipeConnectionService : IPipeConnectionService
    {
        private const double SnapDistance = 20.0; // 吸附距离

        public Dictionary<string, PipeConnectionInfo> Pipes { get; } = new Dictionary<string, PipeConnectionInfo>();

        public Func<Canvas> GetCanvasFunc { get; set; }
        public Func<string, FrameworkElement> FindDeviceElementFunc { get; set; }

        public void AddPipeToCanvas(StraightPipeControl pipe, Point position)
        {
            var canvas = GetCanvasFunc?.Invoke();
            if (canvas == null)
            {
                Debug.WriteLine("画布未找到，无法添加管道");
                return;
            }

            // 设置管道位置
            Canvas.SetLeft(pipe, position.X - pipe.Width / 2);
            Canvas.SetTop(pipe, position.Y - pipe.Height / 2);
            Canvas.SetZIndex(pipe, 10);

            // 添加到画布
            canvas.Children.Add(pipe);

            // 创建连接信息
            var pipeInfo = new PipeConnectionInfo
            {
                PipeId = pipe.PipeId,
                PipeControl = pipe,
                LeftConnection = null,
                RightConnection = null
            };

            Pipes[pipe.PipeId] = pipeInfo;

         

            Debug.WriteLine($"管道 {pipe.PipeId} 已添加到画布，位置: ({position.X:F0}, {position.Y:F0})");
        }

        public void RemovePipe(string pipeId)
        {
            if (!Pipes.TryGetValue(pipeId, out var pipeInfo))
                return;

            var canvas = GetCanvasFunc?.Invoke();
            if (canvas != null && pipeInfo.PipeControl != null)
            {
                // 断开所有连接
                DisconnectPipeAnchor(pipeId, PipeAnchorPosition.Left);
                DisconnectPipeAnchor(pipeId, PipeAnchorPosition.Right);

               

                // 从画布移除
                canvas.Children.Remove(pipeInfo.PipeControl);
            }

            // 从字典移除
            Pipes.Remove(pipeId);

            Debug.WriteLine($"管道 {pipeId} 已移除");
        }

        public bool TryConnectToNearestAnchor(string pipeId, PipeAnchorPosition pipeAnchor, Point currentPosition)
        {
            if (!Pipes.TryGetValue(pipeId, out var pipeInfo))
                return false;

            // 1. 检查设备锚点
            var deviceAnchor = FindNearestDeviceAnchor(currentPosition);
            if (deviceAnchor != null)
            {
                ConnectPipeToDevice(pipeId, pipeAnchor, deviceAnchor);
                return true;
            }

            // 2. 检查其他管道锚点
            var otherPipeAnchor = FindNearestPipeAnchor(pipeId, currentPosition);
            if (otherPipeAnchor != null)
            {
                ConnectPipeToPipe(pipeId, pipeAnchor, otherPipeAnchor.Item1, otherPipeAnchor.Item2);
                return true;
            }

            return false;
        }

        private DeviceAnchorInfo FindNearestDeviceAnchor(Point position)
        {
            var canvas = GetCanvasFunc?.Invoke();
            if (canvas == null) return null;

            DeviceAnchorInfo nearest = null;
            double minDistance = SnapDistance;

            // 遍历所有设备元素
            foreach (var child in canvas.Children.OfType<FrameworkElement>())
            {
                if (child.Tag is string deviceId && !string.IsNullOrEmpty(deviceId))
                {
                    // 获取设备的所有锚点
                    var anchors = GetDeviceAnchors(child, deviceId);

                    foreach (var anchor in anchors)
                    {
                        // 检查锚点是否已被占用
                        if (IsDeviceAnchorOccupied(deviceId, anchor.Direction))
                            continue;

                        double distance = CalculateDistance(position, anchor.Position);
                        if (distance < minDistance)
                        {
                            minDistance = distance;
                            nearest = anchor;
                        }
                    }
                }
            }

            return nearest;
        }

        private Tuple<string, PipeAnchorPosition> FindNearestPipeAnchor(string currentPipeId, Point position)
        {
            Tuple<string, PipeAnchorPosition> nearest = null;
            double minDistance = SnapDistance;

            foreach (var kvp in Pipes)
            {
                if (kvp.Key == currentPipeId)
                    continue;

                var pipeInfo = kvp.Value;

                // 检查左锚点
                if (pipeInfo.LeftConnection == null)
                {
                    var leftPos = pipeInfo.PipeControl.GetAnchorPosition(PipeAnchorPosition.Left);
                    double distance = CalculateDistance(position, leftPos);
                    if (distance < minDistance)
                    {
                        minDistance = distance;
                        nearest = Tuple.Create(kvp.Key, PipeAnchorPosition.Left);
                    }
                }

                // 检查右锚点
                if (pipeInfo.RightConnection == null)
                {
                    var rightPos = pipeInfo.PipeControl.GetAnchorPosition(PipeAnchorPosition.Right);
                    double distance = CalculateDistance(position, rightPos);
                    if (distance < minDistance)
                    {
                        minDistance = distance;
                        nearest = Tuple.Create(kvp.Key, PipeAnchorPosition.Right);
                    }
                }
            }

            return nearest;
        }

        private void ConnectPipeToDevice(string pipeId, PipeAnchorPosition pipeAnchor, DeviceAnchorInfo deviceAnchor)
        {
            if (!Pipes.TryGetValue(pipeId, out var pipeInfo))
                return;

            var connection = new AnchorConnection
            {
                TargetType = AnchorTargetType.Device,
                DeviceId = deviceAnchor.DeviceId,
                DeviceDirection = deviceAnchor.Direction
            };

            if (pipeAnchor == PipeAnchorPosition.Left)
            {
                pipeInfo.LeftConnection = connection;
            }
            else
            {
                pipeInfo.RightConnection = connection;
            }

            Debug.WriteLine($"管道 {pipeId} 的 {pipeAnchor} 锚点连接到设备 {deviceAnchor.DeviceId} 的 {deviceAnchor.Direction} 锚点");

            // 吸附到锚点位置
            SnapPipeToAnchor(pipeInfo.PipeControl, pipeAnchor, deviceAnchor.Position);
        }

        private void ConnectPipeToPipe(string pipeId, PipeAnchorPosition pipeAnchor, string targetPipeId, PipeAnchorPosition targetAnchor)
        {
            if (!Pipes.TryGetValue(pipeId, out var pipeInfo))
                return;

            if (!Pipes.TryGetValue(targetPipeId, out var targetPipeInfo))
                return;

            // 创建双向连接
            var connection1 = new AnchorConnection
            {
                TargetType = AnchorTargetType.Pipe,
                TargetPipeId = targetPipeId,
                TargetPipeAnchor = targetAnchor
            };

            var connection2 = new AnchorConnection
            {
                TargetType = AnchorTargetType.Pipe,
                TargetPipeId = pipeId,
                TargetPipeAnchor = pipeAnchor
            };

            if (pipeAnchor == PipeAnchorPosition.Left)
            {
                pipeInfo.LeftConnection = connection1;
            }
            else
            {
                pipeInfo.RightConnection = connection1;
            }

            if (targetAnchor == PipeAnchorPosition.Left)
            {
                targetPipeInfo.LeftConnection = connection2;
            }
            else
            {
                targetPipeInfo.RightConnection = connection2;
            }

            Debug.WriteLine($"管道 {pipeId}:{pipeAnchor} 连接到管道 {targetPipeId}:{targetAnchor}");

            // 吸附到目标管道锚点
            var targetPosition = targetPipeInfo.PipeControl.GetAnchorPosition(targetAnchor);
            SnapPipeToAnchor(pipeInfo.PipeControl, pipeAnchor, targetPosition);
        }

        public void DisconnectPipeAnchor(string pipeId, PipeAnchorPosition anchorPosition)
        {
            if (!Pipes.TryGetValue(pipeId, out var pipeInfo))
                return;

            AnchorConnection connection = null;

            if (anchorPosition == PipeAnchorPosition.Left)
            {
                connection = pipeInfo.LeftConnection;
                pipeInfo.LeftConnection = null;
            }
            else
            {
                connection = pipeInfo.RightConnection;
                pipeInfo.RightConnection = null;
            }

            if (connection == null)
                return;

            // 如果连接的是另一个管道，也断开对方的连接
            if (connection.TargetType == AnchorTargetType.Pipe && !string.IsNullOrEmpty(connection.TargetPipeId))
            {
                if (Pipes.TryGetValue(connection.TargetPipeId, out var targetPipeInfo))
                {
                    if (connection.TargetPipeAnchor == PipeAnchorPosition.Left)
                    {
                        targetPipeInfo.LeftConnection = null;
                    }
                    else
                    {
                        targetPipeInfo.RightConnection = null;
                    }
                }
            }

            Debug.WriteLine($"管道 {pipeId} 的 {anchorPosition} 锚点已断开连接");
        }

        private void SnapPipeToAnchor(StraightPipeControl pipe, PipeAnchorPosition pipeAnchor, Point targetPosition)
        {
            var canvas = GetCanvasFunc?.Invoke();
            if (canvas == null) return;

            var currentLeft = Canvas.GetLeft(pipe);
            var currentTop = Canvas.GetTop(pipe);

            double offsetX, offsetY;

            if (pipeAnchor == PipeAnchorPosition.Left)
            {
                // 左锚点对齐
                offsetX = 6;
                offsetY = 20;
            }
            else
            {
                // 右锚点对齐
                offsetX = 94;
                offsetY = 20;
            }

            var newLeft = targetPosition.X - offsetX;
            var newTop = targetPosition.Y - offsetY;

            Canvas.SetLeft(pipe, newLeft);
            Canvas.SetTop(pipe, newTop);
        }

        private List<DeviceAnchorInfo> GetDeviceAnchors(FrameworkElement deviceElement, string deviceId)
        {
            var anchors = new List<DeviceAnchorInfo>();

            var left = Canvas.GetLeft(deviceElement);
            var top = Canvas.GetTop(deviceElement);
            var width = deviceElement.ActualWidth;
            var height = deviceElement.ActualHeight;

            // 四个方向的锚点
            anchors.Add(new DeviceAnchorInfo
            {
                DeviceId = deviceId,
                Direction = ConnectionDirection.Left,
                Position = new Point(left, top + height / 2)
            });

            anchors.Add(new DeviceAnchorInfo
            {
                DeviceId = deviceId,
                Direction = ConnectionDirection.Right,
                Position = new Point(left + width, top + height / 2)
            });

            anchors.Add(new DeviceAnchorInfo
            {
                DeviceId = deviceId,
                Direction = ConnectionDirection.Top,
                Position = new Point(left + width / 2, top)
            });

            anchors.Add(new DeviceAnchorInfo
            {
                DeviceId = deviceId,
                Direction = ConnectionDirection.Bottom,
                Position = new Point(left + width / 2, top + height)
            });

            return anchors;
        }

        private bool IsDeviceAnchorOccupied(string deviceId, ConnectionDirection direction)
        {
            // 检查是否有管道连接到该设备锚点
            return Pipes.Values.Any(p =>
                (p.LeftConnection?.TargetType == AnchorTargetType.Device &&
                 p.LeftConnection?.DeviceId == deviceId &&
                 p.LeftConnection?.DeviceDirection == direction) ||
                (p.RightConnection?.TargetType == AnchorTargetType.Device &&
                 p.RightConnection?.DeviceId == deviceId &&
                 p.RightConnection?.DeviceDirection == direction));
        }

        private double CalculateDistance(Point p1, Point p2)
        {
            double dx = p1.X - p2.X;
            double dy = p1.Y - p2.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }
        public void AddPipeToCanvas(UIElement pipe, Point position)
        {
            var canvas = GetCanvasFunc?.Invoke();
            if (canvas == null)
            {
                Debug.WriteLine("画布获取失败");
                return;
            }

            // 根据管道类型计算位置偏移
            double offsetX = 0, offsetY = 0;

            if (pipe is StraightPipeControl straightPipe)
            {
                offsetX = straightPipe.Width / 2;
                offsetY = straightPipe.Height / 2;
            }
            else if (pipe is ElbowPipeControl elbowPipe)
            {
                offsetX = elbowPipe.Width / 2;
                offsetY = elbowPipe.Height / 2;
            }

            // 设置位置
            Canvas.SetLeft(pipe, position.X - offsetX);
            Canvas.SetTop(pipe, position.Y - offsetY);

            // 添加到画布
            canvas.Children.Add(pipe);

            Debug.WriteLine($"管道已添加到画布: 位置({position.X:F0}, {position.Y:F0})");
        }

    }

    // 辅助类
    public class PipeConnectionInfo
    {
        public string PipeId { get; set; }
        public StraightPipeControl PipeControl { get; set; }
        public AnchorConnection LeftConnection { get; set; }
        public AnchorConnection RightConnection { get; set; }
    }

    public class AnchorConnection
    {
        public AnchorTargetType TargetType { get; set; }

        // 连接到设备时使用
        public string DeviceId { get; set; }
        public ConnectionDirection DeviceDirection { get; set; }

        // 连接到管道时使用
        public string TargetPipeId { get; set; }
        public PipeAnchorPosition TargetPipeAnchor { get; set; }
    }

    public enum AnchorTargetType
    {
        Device,
        Pipe
    }

    public class DeviceAnchorInfo
    {
        public string DeviceId { get; set; }
        public ConnectionDirection Direction { get; set; }
        public Point Position { get; set; }
    }

    
}