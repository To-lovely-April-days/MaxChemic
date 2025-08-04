using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using MaxChemical.Core;

namespace MaxChemical.Modules.Designer.Models
{
    /// <summary>
    /// 画布文档模型
    /// </summary>
    public class CanvasDocument
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "新建画布";
        public DateTime CreatedTime { get; set; } = DateTime.Now;
        public DateTime LastModifiedTime { get; set; } = DateTime.Now;
        public RegionLayout RegionLayout { get; set; }
        public List<CanvasDeviceData> Devices { get; set; } = new List<CanvasDeviceData>();
        public List<ConnectionData> Connections { get; set; } = new List<ConnectionData>();
        public double CanvasWidth { get; set; } = 1200;
        public double CanvasHeight { get; set; } = 800;
    }

    /// <summary>
    /// 画布设备数据
    /// </summary>
    public class CanvasDeviceData
    {
        public string DeviceId { get; set; }
        public string DeviceName { get; set; }
        public string DeviceType { get; set; }
        public string Category { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        //设备缩放
        public double ScaleX { get; set; } = 1.0;
        public double ScaleY { get; set; } = 1.0;
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// 连接数据
    /// </summary>
    public class ConnectionData
    {
        public string ConnectionId { get; set; }
        public string StartDeviceId { get; set; }
        public string EndDeviceId { get; set; }
        public string StartDirection { get; set; }
        public string EndDirection { get; set; }
        public List<Point> PathPoints { get; set; } = new List<Point>();
    }
}
