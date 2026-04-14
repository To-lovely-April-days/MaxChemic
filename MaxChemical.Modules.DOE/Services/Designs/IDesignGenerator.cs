using System.Collections.Generic;
using MaxChemical.Modules.DOE.Models;

namespace MaxChemical.Modules.DOE.Services.Designs
{
    /// <summary>
    /// 设计生成器统一接口 —— 每种设计方法实现一个。
    ///
    /// 约定:
    ///   1. 生成器只负责生成"基础矩阵"(连续因子 + 基础类别因子展开)
    ///   2. 仿行/区组/随机化由 DesignPipeline 统一处理
    ///   3. 中心点由各生成器自己根据 Minitab 规则加 (不同方法加法不同)
    /// </summary>
    public interface IDesignGenerator<TOptions> where TOptions : DesignOptions
    {
        /// <summary>
        /// 生成设计矩阵。
        /// </summary>
        /// <param name="factors">因子列表 (连续 + 类别)</param>
        /// <param name="options">方法特定选项</param>
        /// <returns>完整设计矩阵,已包含中心点/仿行/区组/随机化</returns>
        DOEDesignMatrix Generate(List<DOEFactor> factors, TOptions options);

        /// <summary>
        /// 校验参数是否合法。返回 null 表示合法,否则返回错误描述。
        /// </summary>
        string? Validate(List<DOEFactor> factors, TOptions options);
    }
}
