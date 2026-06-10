using System.Collections.Generic;

namespace MaxChemical.Infrastructure.DOE
{
    /// <summary>
    /// 流程参数提供者接口 — DOE 模块通过此接口读取流程中可作为因子的参数列表，
    /// 以及在每组实验执行前向流程注入参数。Designer 侧实现。
    /// </summary>
    public interface IFlowParameterProvider
    {
        /// <summary>
        /// 获取当前流程的 ID
        /// </summary>
        string? GetCurrentFlowId();

        /// <summary>
        /// 获取当前流程的名称
        /// </summary>
        string? GetCurrentFlowName();

        /// <summary>
        /// 获取当前流程中所有可作为 DOE 因子的参数候选列表
        /// </summary>
        /// <returns>因子候选列表</returns>
        List<FactorCandidate> GetFactorCandidates();

        /// <summary>
        /// 向当前流程注入一组参数值（在每组 DOE 实验执行前调用）
        /// </summary>
        /// <param name="parameterValues">参数名 → 参数值 的映射</param>
        /// <returns>注入是否成功</returns>
        bool InjectParameters(Dictionary<string, object> parameterValues);
    }

    /// <summary>
    /// 因子候选项 — 表示流程中一个可作为 DOE 因子的参数
    /// </summary>
    public class FactorCandidate
    {
        /// <summary>
        /// 参数唯一标识（格式: {NodeId}.{ParamName} 或 custom.{VarName}）
        /// </summary>
        public string ParameterId { get; set; } = string.Empty;

        /// <summary>
        /// 参数显示名称
        /// </summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// 参数当前值
        /// </summary>
        public object? CurrentValue { get; set; }

        /// <summary>
        /// 参数来源类型
        /// </summary>
        public FactorSourceType SourceType { get; set; }

        /// <summary>
        /// 所属节点 ID（ParameterOverride 和 LogicParam 类型时有值）
        /// </summary>
        public string? NodeId { get; set; }

        /// <summary>
        /// 所属节点显示名称
        /// </summary>
        public string? NodeDisplayName { get; set; }

        /// <summary>
        /// 原始参数名称（节点内部的参数名）
        /// </summary>
        public string? ParameterName { get; set; }

        /// <summary>
        /// 参数数据类型
        /// </summary>
        public string DataType { get; set; } = "Double";
    }

    /// <summary>
    /// 因子来源类型
    /// </summary>
    public enum FactorSourceType
    {
        /// <summary>
        /// 设备命令节点的 ParameterOverrides
        /// </summary>
        ParameterOverride,

        /// <summary>
        /// 自定义变量（FlowExecutionContext 中的变量）
        /// </summary>
        CustomVariable,

        /// <summary>
        /// 逻辑节点参数（如循环次数、等待时间等）
        /// </summary>
        LogicParam
    }
}
