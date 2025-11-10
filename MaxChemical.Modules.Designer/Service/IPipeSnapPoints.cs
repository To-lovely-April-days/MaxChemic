// Interfaces/IPipeSnapPoints.cs
using System.Collections.Generic;
using System.Windows;

namespace MaxChemical.Modules.Designer.Service
{
    /// <summary>
    /// 管道吸附点接口 - 管道组件实现此接口来提供自己的吸附点
    /// </summary>
    public interface IPipeSnapPoints
    {
        /// <summary>
        /// 获取管道的所有吸附点（世界坐标）
        /// </summary>
        List<PipeSnapPoint> GetSnapPoints();
    }

    /// <summary>
    /// 管道吸附点
    /// </summary>
    public class PipeSnapPoint
    {
        /// <summary>
        /// 吸附点在世界坐标系中的位置
        /// </summary>
        public Point WorldPosition { get; set; }

        /// <summary>
        /// 吸附点的连接方向
        /// </summary>
        public SnapDirection Direction { get; set; }

        /// <summary>
        /// 吸附点类型描述（用于调试）
        /// </summary>
        public string Description { get; set; }
    }

    /// <summary>
    /// 吸附方向
    /// </summary>
    public enum SnapDirection
    {
        Left,      // 向左连接
        Right,     // 向右连接
        Up,        // 向上连接
        Down       // 向下连接
    }
}