namespace MaxChemical.Core
{
    /// <summary>
    /// 区域类型枚举
    /// </summary>
    public enum RegionType
    {
        /// <summary>
        /// 无特定区域
        /// </summary>
        None,

        /// <summary>
        /// 进料区域
        /// </summary>
        Feed,

        /// <summary>
        /// 预热区域
        /// </summary>
        PreHeat,

        /// <summary>
        /// 反应区域
        /// </summary>
        Reaction,

        /// <summary>
        /// 淬火区域
        /// </summary>
        Quench,

        /// <summary>
        /// 后处理区域
        /// </summary>
        PostProcess
    }
}