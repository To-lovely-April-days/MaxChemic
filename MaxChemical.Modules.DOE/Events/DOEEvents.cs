using System.Collections.Generic;
using Prism.Events;
using MaxChemical.Modules.DOE.Models;

namespace MaxChemical.Modules.DOE.Events
{
    /// <summary>
    /// DOE 批次开始执行事件
    /// </summary>
    public class DOEBatchStartedEvent : PubSubEvent<string> { }

    /// <summary>
    /// DOE 单组实验完成事件（携带因子值 + 响应值）
    /// </summary>
    public class DOERunCompletedEvent : PubSubEvent<DOERunCompletedPayload> { }

    public class DOERunCompletedPayload
    {
        public string BatchId { get; set; } = string.Empty;
        public int RunIndex { get; set; }
        public string FactorValuesJson { get; set; } = "{}";
        public string ResponseValuesJson { get; set; } = "{}";
    }

    /// <summary>
    /// DOE 批次完成事件
    /// </summary>
    public class DOEBatchCompletedEvent : PubSubEvent<DOEBatchCompletedPayload> { }

    public class DOEBatchCompletedPayload
    {
        public string BatchId { get; set; } = string.Empty;
        public DOEBatchStatus FinalStatus { get; set; }
        public int CompletedRuns { get; set; }
        public string? StopReason { get; set; }
    }

    /// <summary>
    /// DOE 进度变化事件
    /// </summary>
    public class DOEProgressChangedEvent : PubSubEvent<DOEProgressPayload> { }

    public class DOEProgressPayload
    {
        public string BatchId { get; set; } = string.Empty;
        public int CurrentRun { get; set; }
        public int TotalRuns { get; set; }
        public string StatusMessage { get; set; } = string.Empty;

        /// <summary>★ 当前组的因子值 JSON（可为 null）</summary>
        public string? CurrentFactorValuesJson { get; set; }

        /// <summary>★ 先猜后验: 当前组的 RunIndex(记录序号,与 DOERunCompletedEvent 对齐;0=未提供)</summary>
        public int RunIndex { get; set; }
    }

    /// <summary>
    /// 单组结果已落库(响应值写入 DB 后发布,载荷复用 DOERunCompletedPayload)。
    /// 注意与 DOERunCompletedEvent 区分:后者是遗留的「弹框采集响应」触发事件,不可混用。
    /// </summary>
    public class DOERunResultRecordedEvent : PubSubEvent<DOERunCompletedPayload> { }

    /// <summary>
    /// 先猜后验(Predict-then-Verify)状态更新:每组开跑前发布模型预测区间,
    /// 实测回来后发布命中/越界判定,批次收官发布校准自检。
    /// 载荷是全量快照(Entries 为截至当前的完整序列),订阅方直接整体刷新即可。
    /// </summary>
    public class DOEPredictVerifyEvent : PubSubEvent<DOEPredictVerifyPayload> { }

    public class DOEPredictVerifyPayload
    {
        public string BatchId { get; set; } = string.Empty;
        public string ResponseName { get; set; } = string.Empty;
        public List<PredictVerifyEntry> Entries { get; set; } = new();

        /// <summary>当前状态一句话(开跑前预测/实测判定/收官校准)</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>实测偏离超 3σ 报警(2σ 只计入校准统计,不报警——报警器喊错一次就废了)</summary>
        public bool IsAlarm { get; set; }

        /// <summary>批次收官(此时 CalibrationText 有值)</summary>
        public bool IsCompleted { get; set; }

        /// <summary>有预测且已验证的组数</summary>
        public int JudgedCount { get; set; }

        /// <summary>落在 95% 预测区间内的组数(校准自检:命中率应接近 95%)</summary>
        public int HitCount { get; set; }

        /// <summary>收官校准结论(模型评价自己准不准)</summary>
        public string CalibrationText { get; set; } = "";
    }

    public class PredictVerifyEntry
    {
        public int RunIndex { get; set; }

        /// <summary>1 起的显示序号(RunIndex 在不同创建入口有 0 起/1 起两种约定,展示一律用它)</summary>
        public int DisplayNo { get; set; }

        /// <summary>开跑前模型预测均值 μ(前 3 组无模型时为 null)</summary>
        public double? Predicted { get; set; }

        /// <summary>95% 预测区间半宽 = 2·sqrt(σ²(x)+σn²)——含观测噪声,是「下一个观测」的区间,不是均值的区间</summary>
        public double? HalfWidth2 { get; set; }

        /// <summary>3σ 报警半宽 = 3·sqrt(σ²(x)+σn²)</summary>
        public double? HalfWidth3 { get; set; }

        /// <summary>实测值(未回来时 null)</summary>
        public double? Measured { get; set; }

        /// <summary>正在等实测的最新预测(界面高亮)</summary>
        public bool IsCurrent { get; set; }

        /// <summary>实测落在 95% 预测区间外(校准统计)</summary>
        public bool IsMiss2 { get; set; }

        /// <summary>实测偏离超 3σ(报警)</summary>
        public bool IsAlarm3 { get; set; }
    }

    /// <summary>
    /// 请求打开 DOE 模块事件（从菜单栏/工具栏触发）
    /// </summary>
    public class OpenDOEModuleEvent : PubSubEvent { }

    /// <summary>
    /// 请求执行指定 DOE 批次(payload=BatchId):
    /// DOE 主界面订阅后跳转到执行看板并加载该批次。供小桐等外部入口使用。
    /// </summary>
    public class DOEExecuteBatchRequestEvent : PubSubEvent<string> { }

    /// <summary>
    /// DOE 数据发生变化(外部入口新建/修改了批次):
    /// DOE 主界面订阅后刷新概览与历史列表,保证外部创建的批次即时可见。
    /// </summary>
    public class DOEDataChangedEvent : PubSubEvent { }

    /// <summary>
    /// 批次由小桐 Agent 托管执行(payload=BatchId):
    /// 该批次收官后的轮次决策由小桐在对话里解读与确认,
    /// DOE 主界面订阅后跳过原生 MessageBox 决策弹窗,避免双重询问。
    /// 未托管的批次(用户手动执行)决策弹窗行为不变。
    /// </summary>
    public class DOEAgentManagedBatchEvent : PubSubEvent<string> { }

    /// <summary>
    /// GPR 模型状态更新事件
    /// </summary>
    public class GPRModelUpdatedEvent : PubSubEvent<GPRModelUpdatedPayload> { }

    public class GPRModelUpdatedPayload
    {
        public string FlowId { get; set; } = string.Empty;
        public bool IsActive { get; set; }
        public double? RSquared { get; set; }
        public int DataCount { get; set; }
    }
}
