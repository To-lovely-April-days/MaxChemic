using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MaxChemical.Modules.DOE.Models;
using MySql.Data.MySqlClient;

namespace MaxChemical.Modules.DOE.Data
{
    /// <summary>
    /// DOERepository 扩展 — 智能决策引擎（AutoPilot）数据访问
    /// </summary>
    public partial class DOERepository
    {
        // ══════════════ AutoPilotConfig ══════════════

        /// <summary>
        /// 获取项目的智能决策配置。如果不存在则返回 null。
        /// </summary>
        public async Task<AutoPilotConfig?> GetAutoPilotConfigAsync(string projectId)
        {
            const string sql = "SELECT * FROM doe_autopilot_config WHERE project_id = @projectId";
            using var conn = CreateConnection();
            await conn.OpenAsync();
            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@projectId", projectId);
            using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;

            return new AutoPilotConfig
            {
                Id = GetInt(reader, "id"),
                ProjectId = GetStr(reader, "project_id"),
                Enabled = GetInt(reader, "enabled") == 1,
                AlphaScreen = GetDbl(reader, "alpha_screen"),
                R2Min = GetDbl(reader, "r2_min"),
                R2Good = GetDbl(reader, "r2_good"),
                AlphaLOF = GetDbl(reader, "alpha_lof"),
                R2ImprovementMin = GetDbl(reader, "r2_improvement_min"),
                DTarget = GetDbl(reader, "d_target"),
                ConfirmationReps = GetInt(reader, "confirmation_reps"),
                MaxRounds = GetInt(reader, "max_rounds"),
                MaxTotalRuns = GetInt(reader, "max_total_runs"),
                RangeExpansionRatio = GetDbl(reader, "range_expansion_ratio"),
                MaxConsecutiveSameAction = GetInt(reader, "max_consecutive_same_action")
            };
        }

        /// <summary>
        /// 获取项目的智能决策配置。如果不存在则自动创建默认配置并返回。
        /// </summary>
        public async Task<AutoPilotConfig> GetOrCreateAutoPilotConfigAsync(string projectId)
        {
            var config = await GetAutoPilotConfigAsync(projectId);
            if (config != null) return config;

            config = AutoPilotConfig.CreateDefault(projectId);
            await SaveAutoPilotConfigAsync(config);
            return config;
        }

        /// <summary>
        /// 保存（插入或更新）智能决策配置
        /// </summary>
        public async Task SaveAutoPilotConfigAsync(AutoPilotConfig config)
        {
            const string sql = @"
                INSERT INTO doe_autopilot_config 
                (project_id, enabled, alpha_screen, r2_min, r2_good, alpha_lof, r2_improvement_min,
                 d_target, confirmation_reps, max_rounds, max_total_runs, 
                 range_expansion_ratio, max_consecutive_same_action)
                VALUES 
                (@projectId, @enabled, @alphaScreen, @r2Min, @r2Good, @alphaLof, @r2ImpMin,
                 @dTarget, @confirmReps, @maxRounds, @maxTotalRuns,
                 @rangeExpRatio, @maxConsecSame)
                ON DUPLICATE KEY UPDATE
                    enabled = @enabled,
                    alpha_screen = @alphaScreen,
                    r2_min = @r2Min,
                    r2_good = @r2Good,
                    alpha_lof = @alphaLof,
                    r2_improvement_min = @r2ImpMin,
                    d_target = @dTarget,
                    confirmation_reps = @confirmReps,
                    max_rounds = @maxRounds,
                    max_total_runs = @maxTotalRuns,
                    range_expansion_ratio = @rangeExpRatio,
                    max_consecutive_same_action = @maxConsecSame";

            using var conn = CreateConnection();
            await conn.OpenAsync();
            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@projectId", config.ProjectId);
            cmd.Parameters.AddWithValue("@enabled", config.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("@alphaScreen", config.AlphaScreen);
            cmd.Parameters.AddWithValue("@r2Min", config.R2Min);
            cmd.Parameters.AddWithValue("@r2Good", config.R2Good);
            cmd.Parameters.AddWithValue("@alphaLof", config.AlphaLOF);
            cmd.Parameters.AddWithValue("@r2ImpMin", config.R2ImprovementMin);
            cmd.Parameters.AddWithValue("@dTarget", config.DTarget);
            cmd.Parameters.AddWithValue("@confirmReps", config.ConfirmationReps);
            cmd.Parameters.AddWithValue("@maxRounds", config.MaxRounds);
            cmd.Parameters.AddWithValue("@maxTotalRuns", config.MaxTotalRuns);
            cmd.Parameters.AddWithValue("@rangeExpRatio", config.RangeExpansionRatio);
            cmd.Parameters.AddWithValue("@maxConsecSame", config.MaxConsecutiveSameAction);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// 更新智能决策开关状态
        /// </summary>
        public async Task SetAutoPilotEnabledAsync(string projectId, bool enabled)
        {
            // 确保配置存在
            await GetOrCreateAutoPilotConfigAsync(projectId);

            const string sql = "UPDATE doe_autopilot_config SET enabled = @enabled WHERE project_id = @projectId";
            using var conn = CreateConnection();
            await conn.OpenAsync();
            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@projectId", projectId);
            cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
            await cmd.ExecuteNonQueryAsync();
        }

        // ══════════════ AutoPilotLog ══════════════

        /// <summary>
        /// 保存一条决策日志
        /// </summary>
        public async Task SaveAutoPilotLogAsync(AutoPilotLog log)
        {
            const string sql = @"
                INSERT INTO doe_autopilot_log 
                (project_id, batch_id, round_number, timestamp,
                 phase_at_decision, input_summary_json,
                 decision_type, decision_detail_json, reason,
                 next_phase, next_design_method, next_factor_count, estimated_runs, next_batch_id,
                 user_override, user_override_decision, user_override_note,
                 safeguard_triggered, safeguard_rule)
                VALUES 
                (@projectId, @batchId, @round, @ts,
                 @phase, @inputJson,
                 @decType, @decDetailJson, @reason,
                 @nextPhase, @nextMethod, @nextFactors, @estRuns, @nextBatchId,
                 @userOverride, @userOverrideDec, @userOverrideNote,
                 @safeguard, @safeguardRule)";

            using var conn = CreateConnection();
            await conn.OpenAsync();
            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@projectId", log.ProjectId);
            cmd.Parameters.AddWithValue("@batchId", log.BatchId);
            cmd.Parameters.AddWithValue("@round", log.RoundNumber);
            cmd.Parameters.AddWithValue("@ts", log.Timestamp);
            cmd.Parameters.AddWithValue("@phase", log.PhaseAtDecision.ToString());
            cmd.Parameters.AddWithValue("@inputJson", log.InputSummaryJson);
            cmd.Parameters.AddWithValue("@decType", log.DecisionType.ToString());
            cmd.Parameters.AddWithValue("@decDetailJson", log.DecisionDetailJson);
            cmd.Parameters.AddWithValue("@reason", log.Reason);
            cmd.Parameters.AddWithValue("@nextPhase", log.NextPhase.ToString());
            cmd.Parameters.AddWithValue("@nextMethod", log.NextDesignMethod.ToString());
            cmd.Parameters.AddWithValue("@nextFactors", log.NextFactorCount);
            cmd.Parameters.AddWithValue("@estRuns", log.EstimatedRuns);
            cmd.Parameters.AddWithValue("@nextBatchId", (object?)log.NextBatchId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@userOverride", log.UserOverride ? 1 : 0);
            cmd.Parameters.AddWithValue("@userOverrideDec", (object?)log.UserOverrideDecision?.ToString() ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@userOverrideNote", (object?)log.UserOverrideNote ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@safeguard", log.SafeguardTriggered ? 1 : 0);
            cmd.Parameters.AddWithValue("@safeguardRule", (object?)log.SafeguardRule ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// 获取项目的所有决策日志（按轮次排序）
        /// </summary>
        public async Task<List<AutoPilotLog>> GetAutoPilotLogsAsync(string projectId)
        {
            const string sql = "SELECT * FROM doe_autopilot_log WHERE project_id = @projectId ORDER BY round_number ASC";
            using var conn = CreateConnection();
            await conn.OpenAsync();
            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@projectId", projectId);
            using var reader = await cmd.ExecuteReaderAsync();

            var logs = new List<AutoPilotLog>();
            while (await reader.ReadAsync())
            {
                logs.Add(MapAutoPilotLog(reader));
            }
            return logs;
        }

        /// <summary>
        /// 获取项目最近 N 条决策日志（用于防护规则检查）
        /// </summary>
        public async Task<List<AutoPilotLog>> GetRecentAutoPilotLogsAsync(string projectId, int count = 5)
        {
            const string sql = @"
                SELECT * FROM doe_autopilot_log 
                WHERE project_id = @projectId 
                ORDER BY round_number DESC 
                LIMIT @count";
            using var conn = CreateConnection();
            await conn.OpenAsync();
            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@projectId", projectId);
            cmd.Parameters.AddWithValue("@count", count);
            using var reader = await cmd.ExecuteReaderAsync();

            var logs = new List<AutoPilotLog>();
            while (await reader.ReadAsync())
            {
                logs.Add(MapAutoPilotLog(reader));
            }
            return logs;
        }

        /// <summary>
        /// 更新日志的用户覆盖信息
        /// </summary>
        public async Task UpdateAutoPilotLogOverrideAsync(int logId,
            NextStepRecommendation userDecision, string? note)
        {
            const string sql = @"
                UPDATE doe_autopilot_log 
                SET user_override = 1, 
                    user_override_decision = @dec, 
                    user_override_note = @note
                WHERE id = @id";
            using var conn = CreateConnection();
            await conn.OpenAsync();
            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", logId);
            cmd.Parameters.AddWithValue("@dec", userDecision.ToString());
            cmd.Parameters.AddWithValue("@note", (object?)note ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// 更新日志的 NextBatchId（自动创建批次后回填）
        /// </summary>
        public async Task UpdateAutoPilotLogNextBatchAsync(int logId, string nextBatchId)
        {
            const string sql = "UPDATE doe_autopilot_log SET next_batch_id = @batchId WHERE id = @id";
            using var conn = CreateConnection();
            await conn.OpenAsync();
            using var cmd = new MySqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@id", logId);
            cmd.Parameters.AddWithValue("@batchId", nextBatchId);
            await cmd.ExecuteNonQueryAsync();
        }

        // ══════════════ 映射方法 ══════════════

        private static AutoPilotLog MapAutoPilotLog(System.Data.Common.DbDataReader r)
        {
            return new AutoPilotLog
            {
                Id = GetInt(r, "id"),
                ProjectId = GetStr(r, "project_id"),
                BatchId = GetStr(r, "batch_id"),
                RoundNumber = GetInt(r, "round_number"),
                Timestamp = GetDt(r, "timestamp"),
                PhaseAtDecision = Enum.TryParse<DOEProjectPhase>(GetStr(r, "phase_at_decision"), out var phase)
                    ? phase : DOEProjectPhase.Screening,
                InputSummaryJson = GetStr(r, "input_summary_json"),
                DecisionType = Enum.TryParse<NextStepRecommendation>(GetStr(r, "decision_type"), out var dec)
                    ? dec : NextStepRecommendation.UserDecision,
                DecisionDetailJson = GetStr(r, "decision_detail_json"),
                Reason = GetStr(r, "reason"),
                NextPhase = Enum.TryParse<DOEProjectPhase>(GetStrN(r, "next_phase"), out var np)
                    ? np : DOEProjectPhase.Screening,
                NextDesignMethod = Enum.TryParse<DOEDesignMethod>(GetStrN(r, "next_design_method"), out var nm)
                    ? nm : DOEDesignMethod.FullFactorial,
                NextFactorCount = IsNull(r, "next_factor_count") ? 0 : GetInt(r, "next_factor_count"),
                EstimatedRuns = IsNull(r, "estimated_runs") ? 0 : GetInt(r, "estimated_runs"),
                NextBatchId = GetStrN(r, "next_batch_id"),
                UserOverride = GetInt(r, "user_override") == 1,
                UserOverrideDecision = Enum.TryParse<NextStepRecommendation>(
                    GetStrN(r, "user_override_decision"), out var uod) ? uod : null,
                UserOverrideNote = GetStrN(r, "user_override_note"),
                SafeguardTriggered = GetInt(r, "safeguard_triggered") == 1,
                SafeguardRule = GetStrN(r, "safeguard_rule")
            };
        }
    }
}