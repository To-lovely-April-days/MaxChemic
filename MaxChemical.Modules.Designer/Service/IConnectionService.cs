// Services/IConnectionService.cs
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using MaxChemical.Modules.Designer.Models;

namespace MaxChemical.Modules.Designer.Services
{
    public interface IConnectionService
    {
        // 连接管理
        void StartConnection(string startDeviceId, ConnectionDirection direction);
        void CompleteConnection(string endDeviceId, ConnectionDirection direction);
        void CancelConnection();
        bool IsConnectionMode { get; }

        // 连接操作
        ConnectionInfo CreateConnection(string startDeviceId, string endDeviceId,
            ConnectionDirection startDirection, ConnectionDirection endDirection);
        void UpdateConnection(ConnectionInfo connection);
        void RemoveConnection(string connectionId);
        void RemoveSelectedConnections();

        // 路径计算
        void UpdateConnectionPath(ConnectionInfo connection);

        // 连接点管理
        List<ConnectionPoint> GetDeviceConnectionPoints(string deviceId);
        void ShowConnectionPoints(string deviceId);
        void HideAllConnectionPoints();
        void ShowAllConnectionPoints();

        // 连接集合
        Dictionary<string, ConnectionInfo> Connections { get; }

        // 画布访问委托
        Func<Canvas> GetCanvasFunc { get; set; }
        Func<string, FrameworkElement> FindDeviceElementFunc { get; set; }

        // 事件
        event System.Action<ConnectionInfo> ConnectionCreated;
        event System.Action<string> ConnectionDeleted;

        // 添加鼠标跟踪方法
        void UpdateMousePosition(Point mousePosition);
        // 添加显示所有设备连接点的委托
        Action ShowAllDeviceConnectionPointsAction { get; set; }

        void SetDragMode(bool isDragMode);
    }
}