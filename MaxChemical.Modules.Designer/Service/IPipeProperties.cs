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
    }
}