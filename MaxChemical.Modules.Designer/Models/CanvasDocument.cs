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
    [Serializable]
    public class CanvasDocument
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "新建画布";
        public DateTime CreatedTime { get; set; } = DateTime.Now;
        public DateTime LastModifiedTime { get; set; } = DateTime.Now;
        public RegionLayout RegionLayout { get; set; }
        public List<CanvasDeviceData> Devices { get; set; } = new List<CanvasDeviceData>();
        public List<ConnectionData> Connections { get; set; } = new List<ConnectionData>();

        /// <summary>
        ///  新增：管道数据
        /// </summary>
        public List<CanvasPipeData> Pipes { get; set; } = new List<CanvasPipeData>();

        public double CanvasWidth { get; set; } = 1200;
        public double CanvasHeight { get; set; } = 800;
    }

    /// <summary>
    /// 画布设备数据
    /// </summary>
    [Serializable]
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

        // 设备缩放
        public double ScaleX { get; set; } = 1.0;
        public double ScaleY { get; set; } = 1.0;
        //  新增：控件旋转角度
        public double RotationAngle { get; set; } = 0.0;
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// 连接数据
    /// </summary>
    [Serializable]
    public class ConnectionData
    {
        public string ConnectionId { get; set; }
        public string StartDeviceId { get; set; }
        public string EndDeviceId { get; set; }
        public string StartDirection { get; set; }
        public string EndDirection { get; set; }
        public List<Point> PathPoints { get; set; } = new List<Point>();
    }

    /// <summary>
    ///  新增：画布管道数据（用于序列化保存）
    /// </summary>
    [Serializable]
    public class CanvasPipeData
    {
        /// <summary>
        /// 管道唯一标识符
        /// </summary>
        public string PipeId { get; set; }

        /// <summary>
        /// 管道类型："Straight" / "Elbow" / "ZShape"
        /// </summary>
        public string PipeType { get; set; }

        /// <summary>
        /// X 坐标（左上角）
        /// </summary>
        public double X { get; set; }

        /// <summary>
        /// Y 坐标（左上角）
        /// </summary>
        public double Y { get; set; }

        /// <summary>
        /// Z-Index 层级
        /// </summary>
        public int ZIndex { get; set; }

        /// <summary>
        /// 管道宽度（所有类型通用）
        /// </summary>
        public double PipeWidth { get; set; }

        /// <summary>
        /// 旋转角度（所有类型通用）
        /// </summary>
        public double RotationAngle { get; set; }

        // ============ 直管道专用属性 ============
        /// <summary>
        /// 直管道长度
        /// </summary>
        public double? PipeLength { get; set; }

        // ============ 弯管道专用属性 ============
        /// <summary>
        /// 弯管道水平长度
        /// </summary>
        public double? HorizontalLength { get; set; }

        /// <summary>
        /// 弯管道垂直长度
        /// </summary>
        public double? VerticalLength { get; set; }

        // ============ Z形管道专用属性 ============
        /// <summary>
        /// Z形管道顶部水平长度
        /// </summary>
        public double? TopHorizontalLength { get; set; }

        /// <summary>
        /// Z形管道底部水平长度
        /// </summary>
        public double? BottomHorizontalLength { get; set; }

        /// <summary>
        /// Z形管道垂直偏移
        /// </summary>
        public double? VerticalOffset { get; set; }
        // T型管道专用属性
        public double? MainPipeLength { get; set; }
        public double? BranchPosition { get; set; }
        public double? BranchPipeLength { get; set; }
        public override string ToString()
        {
            return $"Pipe[{PipeType}] ID:{PipeId} at ({X:F0},{Y:F0})";
        }
        #region 管道外观属性（保存/加载用）

        /// <summary>管体颜色 (HEX, e.g. "#727171")</summary>
        public string PipeColorHex { get; set; }

        /// <summary>法兰颜色 (HEX)</summary>
        public string FlangeColorHex { get; set; }

        /// <summary>流体颜色 (HEX)</summary>
        public string FluidColorHex { get; set; }

        /// <summary>流体不透明度 (0.0~1.0)</summary>
        public double FluidOpacity { get; set; } = 0.85;

        /// <summary>是否显示流体</summary>
        public bool IsFluidVisible { get; set; }

        /// <summary>是否流动</summary>
        public bool IsFlowing { get; set; }

        /// <summary>流动方向 ("LeftToRight" / "RightToLeft")</summary>
        public string FlowDirectionStr { get; set; } = "LeftToRight";

        /// <summary>流动速度倍率</summary>
        public double FlowSpeed { get; set; } = 1.0;

        #endregion

    }
}