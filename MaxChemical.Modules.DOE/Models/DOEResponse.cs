namespace MaxChemical.Modules.DOE.Models
{
    /// <summary>
    /// DOE 响应变量定义
    /// </summary>
    public class DOEResponse
    {
        public int Id { get; set; }
        public string BatchId { get; set; } = string.Empty;
        public string ResponseName { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public DOECollectionMethod CollectionMethod { get; set; } = DOECollectionMethod.Manual;
        public int SortOrder { get; set; }
        /// <summary>
        /// 模型中包含项的最高阶数（部分因子/全因子分析用）。
        /// null 表示未设置过，使用默认值 2。
        /// </summary>
        public int? ModelOrder { get; set; }
    }

    /// <summary>
    /// 响应值采集方式
    /// </summary>
    public enum DOECollectionMethod
    {
        /// <summary>
        /// 弹框手动录入
        /// </summary>
        Manual,

        /// <summary>
        /// 设备自动读取（预留）
        /// </summary>
        Auto
    }
}
