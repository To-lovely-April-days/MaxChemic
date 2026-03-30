using MaxChemical.Infrastructure.DOE;

namespace MaxChemical.Modules.DOE.Models
{
    /// <summary>
    /// DOE 因子定义
    /// </summary>
    public class DOEFactor
    {
        public int Id { get; set; }
        public string BatchId { get; set; } = string.Empty;
        public string FactorName { get; set; } = string.Empty;
        public FactorSourceType FactorSource { get; set; } = FactorSourceType.ParameterOverride;
        public string? SourceNodeId { get; set; }
        public string? SourceParamName { get; set; }
        public double LowerBound { get; set; }
        public double UpperBound { get; set; }
        public int LevelCount { get; set; } = 3;
        public DOEFactorType FactorType { get; set; } = DOEFactorType.Continuous;
        public int SortOrder { get; set; }

        /// <summary>
        /// 计算步长
        /// </summary>
        public double StepSize => LevelCount > 1 ? (UpperBound - LowerBound) / (LevelCount - 1) : 0;

        /// <summary>
        /// 计算中心点
        /// </summary>
        public double CenterPoint => (UpperBound + LowerBound) / 2.0;

        /// <summary>
        /// 生成水平值列表
        /// </summary>
        public double[] GetLevels()
        {
            if (LevelCount <= 1) return new[] { CenterPoint };
            var levels = new double[LevelCount];
            var step = StepSize;
            for (int i = 0; i < LevelCount; i++)
            {
                levels[i] = LowerBound + step * i;
            }
            return levels;
        }
    }

    /// <summary>
    /// 因子类型
    /// </summary>
    public enum DOEFactorType
    {
        Continuous,
        Discrete
    }
}
