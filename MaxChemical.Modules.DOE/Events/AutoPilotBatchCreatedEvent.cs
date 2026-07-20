using Prism.Events;

namespace MaxChemical.Modules.DOE.Events
{
    /// <summary>
    /// 智能决策(AutoPilot)创建下一轮批次后的广播。
    /// 自动建轮此前是静默的:值守与小桐都不知道新批次的存在,
    /// 用户问下一轮时专员只能空对空。发此事件让值守接管播报与托管延续。
    /// </summary>
    public class AutoPilotBatchCreatedEvent : PubSubEvent<AutoPilotBatchCreatedPayload> { }

    public class AutoPilotBatchCreatedPayload
    {
        public string ProjectId { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;

        /// <summary>触发决策的已完成批次(上一轮)。</summary>
        public string SourceBatchId { get; set; } = string.Empty;

        /// <summary>新创建的下一轮批次。</summary>
        public string NextBatchId { get; set; } = string.Empty;

        public int NextRoundNumber { get; set; }
        public string DesignMethodDisplay { get; set; } = string.Empty;

        /// <summary>下一轮实验组数(建轮后实查;查询失败为 0,展示时略过)。</summary>
        public int RunCount { get; set; }

        /// <summary>true=智能决策全自动创建;false=用户在原生决策弹窗采纳推荐后创建。</summary>
        public bool AutoTriggered { get; set; }
    }
}
