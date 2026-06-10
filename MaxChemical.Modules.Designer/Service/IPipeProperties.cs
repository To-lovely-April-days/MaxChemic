using System.Windows.Media;

namespace MaxChemical.Modules.Designer.Views.Controls.Service
{
    /// <summary>
    /// 所有管道控件共享的外观 / 流体 / 流动属性接口
    /// </summary>
    public interface IPipeProperties
    {
        Color PipeColor { get; set; }
        Color FlangeColor { get; set; }
        Color FluidColor { get; set; }
        double FluidOpacity { get; set; }
        bool IsFluidVisible { get; set; }
        bool IsFlowing { get; set; }
        PipeFlowDirection PipeFlowDir { get; set; }
        double FlowSpeed { get; set; }

        /// <summary>
        /// 是否使用默认流动样式（赛博流光：金属管壁 + 流光带 + 粒子 + 辉光）
        /// 勾选时：纹理结构固定（金属壁、粒子、辉光、节奏），但主流光颜色仍跟随 FluidColor
        /// 用户可控：FluidColor / 方向 / 流速 / 流动开关
        /// 用户被锁定：PipeColor / FlangeColor / FluidOpacity（由默认样式接管）
        /// </summary>
        bool UseDefaultStyle { get; set; }
    }
}