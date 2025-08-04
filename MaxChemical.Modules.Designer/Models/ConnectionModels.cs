// Models/ConnectionModels.cs
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Controls;
using DevicePlugins.Devices;
using Prism.Mvvm;

namespace MaxChemical.Modules.Designer.Models
{
    // 连接方向枚举
    public enum ConnectionDirection
    {
        Top,
        Right,
        Bottom,
        Left
    }

    // 连接方向模式
    public enum ConnectionDirectionMode
    {
        Auto,
        HorizontalFirst,
        VerticalFirst
    }

    // 连接点模型
    public class ConnectionPoint : BindableBase
    {
        private string _id;
        private string _deviceId;
        private ConnectionDirection _direction;
        private Point _relativePosition;
        private bool _isConnected;
        private FrameworkElement _visualElement; // 修正：改名避免混淆

        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        public string DeviceId
        {
            get => _deviceId;
            set => SetProperty(ref _deviceId, value);
        }

        public ConnectionDirection Direction
        {
            get => _direction;
            set => SetProperty(ref _direction, value);
        }

        public Point RelativePosition
        {
            get => _relativePosition;
            set => SetProperty(ref _relativePosition, value);
        }

        public bool IsConnected
        {
            get => _isConnected;
            set => SetProperty(ref _isConnected, value);
        }

        public FrameworkElement VisualElement // 修正：改名
        {
            get => _visualElement;
            set => SetProperty(ref _visualElement, value);
        }
    }

    // 连接信息模型
    public class ConnectionInfo : BindableBase
    {
        private string _id;
        private string _startDeviceId;
        private string _endDeviceId;
        private FrameworkElement _startDevice;
        private FrameworkElement _endDevice;
        private ConnectionPoint _startAnchor;
        private ConnectionPoint _endAnchor;
        private Path _connectionPath;
        private Path _arrowPath;
        private TextBlock _connectionLabel;
        private FrameworkElement _cornerControl;
        private ConnectionDirectionMode _directionMode = ConnectionDirectionMode.Auto;
        private Point _controlPoint;
        private bool _isControlPointCustomized;
        private bool _isLabelPositionCustomized;
        private SolidColorBrush _connectionColor;
        private bool _isAnimating;
        private bool _isSelected;

        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        public string StartDeviceId
        {
            get => _startDeviceId;
            set => SetProperty(ref _startDeviceId, value);
        }

        public string EndDeviceId
        {
            get => _endDeviceId;
            set => SetProperty(ref _endDeviceId, value);
        }

        public FrameworkElement StartDevice
        {
            get => _startDevice;
            set => SetProperty(ref _startDevice, value);
        }

        public FrameworkElement EndDevice
        {
            get => _endDevice;
            set => SetProperty(ref _endDevice, value);
        }

        public ConnectionPoint StartAnchor
        {
            get => _startAnchor;
            set => SetProperty(ref _startAnchor, value);
        }

        public ConnectionPoint EndAnchor
        {
            get => _endAnchor;
            set => SetProperty(ref _endAnchor, value);
        }

        public Path ConnectionPath
        {
            get => _connectionPath;
            set => SetProperty(ref _connectionPath, value);
        }

        public Path ArrowPath
        {
            get => _arrowPath;
            set => SetProperty(ref _arrowPath, value);
        }

        public TextBlock ConnectionLabel
        {
            get => _connectionLabel;
            set => SetProperty(ref _connectionLabel, value);
        }

        public FrameworkElement CornerControl
        {
            get => _cornerControl;
            set => SetProperty(ref _cornerControl, value);
        }

        public ConnectionDirectionMode DirectionMode
        {
            get => _directionMode;
            set => SetProperty(ref _directionMode, value);
        }

        public Point ControlPoint
        {
            get => _controlPoint;
            set => SetProperty(ref _controlPoint, value);
        }

        public bool IsControlPointCustomized
        {
            get => _isControlPointCustomized;
            set => SetProperty(ref _isControlPointCustomized, value);
        }

        public bool IsLabelPositionCustomized
        {
            get => _isLabelPositionCustomized;
            set => SetProperty(ref _isLabelPositionCustomized, value);
        }

        public SolidColorBrush ConnectionColor
        {
            get => _connectionColor;
            set => SetProperty(ref _connectionColor, value);
        }

        public bool IsAnimating
        {
            get => _isAnimating;
            set => SetProperty(ref _isAnimating, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }
    }
}