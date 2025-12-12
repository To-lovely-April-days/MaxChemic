using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using MaxChemical.Modules.Designer.Models;



namespace MaxChemical.Modules.Designer.Service
{

    /// <summary>
    /// 管道吸附服务接口
    /// </summary>
    public interface IPipeSnapService
    {
        /// <summary>
        /// 初始化吸附服务
        /// </summary>
        void Initialize(Canvas canvas);

        /// <summary>
        /// 查找最近的吸附点（单点检测，保留用于兼容）
        /// </summary>
        SnapResult FindNearestSnapPoint(Point currentPosition, string excludePipeId = null);

        /// <summary>
        /// 查找管道的最佳吸附方案（双端点检测）
        /// </summary>
        PipeSnapSolution FindBestSnapSolution(
            UIElement pipeElement,
            Point currentCenter,
            string excludePipeId = null);

        /// <summary>
        /// 显示单个吸附指示器
        /// </summary>
        void ShowSnapIndicator(Point position);

        /// <summary>
        /// 显示多个吸附指示器
        /// </summary>
        void ShowSnapIndicators(List<EndPointSnapInfo> snapInfos);

        /// <summary>
        /// 隐藏吸附指示器
        /// </summary>
        void HideSnapIndicator();

        /// <summary>
        /// 清理资源
        /// </summary>
        void Cleanup();
    }

    #region 辅助类和枚举定义

    /// <summary>
    /// 吸附点类型
    /// </summary>
    public enum SnapPointType
    {
        DeviceLeft,
        DeviceRight,
        DeviceTop,
        DeviceBottom,
        PipeEnd
    }

   

    /// <summary>
    /// 管道端点类型
    /// </summary>
    public enum PipeEndPointType
    {
        Generic,           //  新增：通用端点类型
        StraightLeft,
        StraightRight,
        ElbowLeft,
        ElbowTop,
        ZShapeLeftTop,
        ZShapeRightBottom
    }

    /// <summary>
    /// 吸附点信息
    /// </summary>
    public class SnapPoint
    {
        public Point Position { get; set; }
        public SnapPointType Type { get; set; }
        public string TargetId { get; set; }
        public ConnectionDirection Direction { get; set; }
    }

    /// <summary>
    /// 吸附结果
    /// </summary>
    public class SnapResult
    {
        public SnapPoint SnapPoint { get; set; }
        public double Distance { get; set; }
        public bool ShouldSnap { get; set; }
    }

    /// <summary>
    /// 管道端点信息
    /// </summary>
    public class PipeEndPoint
    {
        public PipeEndPointType Type { get; set; }
        public Point Position { get; set; }
        public ConnectionDirection Direction { get; set; }
    }

    /// <summary>
    /// 端点吸附信息
    /// </summary>
    public class EndPointSnapInfo
    {
        public PipeEndPointType EndPointType { get; set; }
        public Point OriginalPosition { get; set; }
        public SnapPoint SnapTarget { get; set; }
        public double Distance { get; set; }
    }

    /// <summary>
    /// 管道吸附方案
    /// </summary>
    public class PipeSnapSolution
    {
        public bool HasSnap { get; set; }
        public Point NewCenterPosition { get; set; }
        public List<EndPointSnapInfo> EndPointSnaps { get; set; } = new List<EndPointSnapInfo>();
    }

    #endregion
}
