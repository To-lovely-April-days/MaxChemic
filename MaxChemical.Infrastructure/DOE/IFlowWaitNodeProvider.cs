using System.Collections.Generic;

namespace MaxChemical.Infrastructure.DOE
{
    /// <summary>
    /// 流程等待节点提供者:枚举当前流程里的固定时长 Wait 节点,供「按 τ 动态稳态等待」
    /// 挑选被驱动的保温节点。与因子候选分开——等待时长是流程控制量,不进因子候选清单。
    /// Designer 侧实现。
    /// </summary>
    public interface IFlowWaitNodeProvider
    {
        /// <summary>列出当前流程中所有固定时长 Wait 节点(排除条件等待);无流程/无节点时返回空列表。</summary>
        List<FlowWaitNodeInfo> GetFixedWaitNodes();
    }

    /// <summary>一个固定时长 Wait 节点的摘要。</summary>
    public class FlowWaitNodeInfo
    {
        /// <summary>节点ID(注入定位用,格式化为 {NodeId}.WaitTime)。</summary>
        public string NodeId { get; set; } = string.Empty;

        /// <summary>节点显示名。</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>当前配置的等待时长(按 TimeUnit 计)。</summary>
        public double CurrentWaitTime { get; set; }

        /// <summary>当前时间单位(Seconds/Minutes/…)。</summary>
        public string TimeUnit { get; set; } = "Seconds";

        /// <summary>是否勾选了「等待设备稳定」(仅展示,动态等待仍写 WaitTime)。</summary>
        public bool WaitForStability { get; set; }

        /// <summary>
        /// 是否勾选了「停留时间等待节点」标记(DOE 稳态保温):
        /// 动态等待的权威定位依据——流程可能有多个 Wait,靠人挑会挑错,语义钉在节点上。
        /// </summary>
        public bool IsResidenceTimeWait { get; set; }
    }
}
