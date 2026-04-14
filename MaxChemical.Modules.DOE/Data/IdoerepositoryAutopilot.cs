using System.Collections.Generic;
using System.Threading.Tasks;
using MaxChemical.Modules.DOE.Models;

namespace MaxChemical.Modules.DOE.Data
{
    /// <summary>
    /// IDOERepository 扩展 — AutoPilot 相关方法
    /// 
    /// ★ 将以下方法添加到现有 IDOERepository 接口中
    /// 
    /// 同时需要修正两处调用名:
    ///   1. AutoPilotService 中 _repository.GetOlsResultAsync → 改为 _repository.GetOlsResultJsonAsync
    ///   2. AutoPilotService 中 _repository.GetRoundSummaryAsync(projectId, batchId) → 改为 _repository.GetRoundSummaryByBatchAsync(batchId)
    /// </summary>
    public partial interface IDOERepository
    {
        // ══════════════ AutoPilotConfig ══════════════

        /// <summary>获取项目的智能决策配置（不存在返回 null）</summary>
        Task<AutoPilotConfig?> GetAutoPilotConfigAsync(string projectId);

        /// <summary>获取或创建项目的智能决策配置（不存在则创建默认值）</summary>
        Task<AutoPilotConfig> GetOrCreateAutoPilotConfigAsync(string projectId);

        /// <summary>保存（插入或更新）智能决策配置</summary>
        Task SaveAutoPilotConfigAsync(AutoPilotConfig config);

        /// <summary>更新智能决策开关状态</summary>
        Task SetAutoPilotEnabledAsync(string projectId, bool enabled);

        // ══════════════ AutoPilotLog ══════════════

        /// <summary>保存一条决策日志</summary>
        Task SaveAutoPilotLogAsync(AutoPilotLog log);

        /// <summary>获取项目的所有决策日志（按轮次排序）</summary>
        Task<List<AutoPilotLog>> GetAutoPilotLogsAsync(string projectId);

        /// <summary>获取项目最近 N 条决策日志</summary>
        Task<List<AutoPilotLog>> GetRecentAutoPilotLogsAsync(string projectId, int count = 5);

        /// <summary>更新日志的用户覆盖信息</summary>
        Task UpdateAutoPilotLogOverrideAsync(int logId, NextStepRecommendation userDecision, string? note);

        /// <summary>更新日志的 NextBatchId</summary>
        Task UpdateAutoPilotLogNextBatchAsync(int logId, string nextBatchId);

    
    }
}