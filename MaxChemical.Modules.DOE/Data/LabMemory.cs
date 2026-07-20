using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;

namespace MaxChemical.Modules.DOE.Data
{
    /// <summary>
    /// 实验室历史检索的一条命中:项目或独立批次(未挂项目的批次)。
    /// 供小桐回答「以前做过某某实验吗」「上次类似反应的最优条件」这类跨项目记忆问题。
    /// </summary>
    public sealed class LabHistoryHit
    {
        /// <summary>项目 / 独立批次。</summary>
        public string Kind { get; set; } = "项目";
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public string Phase { get; set; } = "";
        public string Objective { get; set; } = "";
        public int Rounds { get; set; }
        public int TotalExperiments { get; set; }
        public double? BestResponseValue { get; set; }
        public string? BestFactorsJson { get; set; }
        public DateTime UpdatedTime { get; set; }
        /// <summary>命中说明(名称/目标/因子/响应/批次名)。</summary>
        public string MatchedOn { get; set; } = "";
    }

    public partial interface IDOERepository
    {
        /// <summary>
        /// 按关键词检索全部历史项目与独立批次(名称、目标描述、因子名、响应名、批次名)。
        /// 多个关键词为 AND 关系;每个关键词在各列间为 OR。返回按最近更新排序。
        /// </summary>
        Task<List<LabHistoryHit>> SearchLabHistoryAsync(List<string> terms, int limit);
    }

    public partial class DOERepository
    {
        /// <summary>LIKE 通配符转义:关键词里的 % _ \ 按字面匹配。</summary>
        private static string EscapeLike(string term)
            => term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        public async Task<List<LabHistoryHit>> SearchLabHistoryAsync(List<string> terms, int limit)
        {
            var hits = new List<LabHistoryHit>();
            terms = (terms ?? new List<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct()
                .Take(5)
                .ToList();
            if (terms.Count == 0) return hits;
            if (limit <= 0) limit = 8;
            if (limit > 20) limit = 20;

            using var conn = CreateConnection();
            await conn.OpenAsync();

            // ── 项目:名称/目标/项目因子/批次名/响应名 任一列命中即算该词命中 ──
            var projWhere = new StringBuilder();
            for (int i = 0; i < terms.Count; i++)
            {
                if (i > 0) projWhere.Append(" AND ");
                projWhere.Append($@"(p.project_name LIKE @t{i}
                    OR p.objective_description LIKE @t{i}
                    OR EXISTS(SELECT 1 FROM doe_project_factors f WHERE f.project_id = p.project_id AND f.factor_name LIKE @t{i})
                    OR EXISTS(SELECT 1 FROM doe_batches b WHERE b.project_id = p.project_id AND b.batch_name LIKE @t{i})
                    OR EXISTS(SELECT 1 FROM doe_batches b2 JOIN doe_responses r ON r.batch_id = b2.batch_id
                              WHERE b2.project_id = p.project_id AND r.response_name LIKE @t{i}))");
            }

            string projSql = $@"
                SELECT p.project_id, p.project_name, p.status, p.current_phase, p.objective_description,
                       p.completed_rounds, p.total_experiments, p.best_response_value, p.best_factors_json, p.updated_time
                FROM doe_projects p
                WHERE {projWhere}
                ORDER BY p.updated_time DESC
                LIMIT {limit}";

            using (var cmd = new MySqlCommand(projSql, conn))
            {
                for (int i = 0; i < terms.Count; i++)
                    cmd.Parameters.AddWithValue($"@t{i}", "%" + EscapeLike(terms[i]) + "%");

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    hits.Add(new LabHistoryHit
                    {
                        Kind = "项目",
                        Id = reader.IsDBNull(0) ? "" : reader.GetString(0),
                        Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        Status = reader.IsDBNull(2) ? "" : reader.GetString(2),
                        Phase = reader.IsDBNull(3) ? "" : reader.GetString(3),
                        Objective = reader.IsDBNull(4) ? "" : reader.GetString(4),
                        Rounds = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                        TotalExperiments = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                        BestResponseValue = reader.IsDBNull(7) ? (double?)null : reader.GetDouble(7),
                        BestFactorsJson = reader.IsDBNull(8) ? null : reader.GetString(8),
                        UpdatedTime = reader.IsDBNull(9) ? DateTime.MinValue : reader.GetDateTime(9)
                    });
                }
            }

            // ── 独立批次(未挂项目):批次名/因子名/响应名 ──
            var batchWhere = new StringBuilder();
            for (int i = 0; i < terms.Count; i++)
            {
                if (i > 0) batchWhere.Append(" AND ");
                batchWhere.Append($@"(b.batch_name LIKE @t{i}
                    OR EXISTS(SELECT 1 FROM doe_factors f WHERE f.batch_id = b.batch_id AND f.factor_name LIKE @t{i})
                    OR EXISTS(SELECT 1 FROM doe_responses r WHERE r.batch_id = b.batch_id AND r.response_name LIKE @t{i}))");
            }

            string batchSql = $@"
                SELECT b.batch_id, b.batch_name, b.status, b.design_method, b.updated_time,
                       (SELECT COUNT(*) FROM doe_runs r WHERE r.batch_id = b.batch_id) AS total_runs,
                       (SELECT COUNT(*) FROM doe_runs r2 WHERE r2.batch_id = b.batch_id AND r2.status = 'Completed') AS completed_runs
                FROM doe_batches b
                WHERE b.project_id IS NULL AND {batchWhere}
                ORDER BY b.updated_time DESC
                LIMIT {limit}";

            using (var cmd = new MySqlCommand(batchSql, conn))
            {
                for (int i = 0; i < terms.Count; i++)
                    cmd.Parameters.AddWithValue($"@t{i}", "%" + EscapeLike(terms[i]) + "%");

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    int total = reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetValue(5));
                    int done = reader.IsDBNull(6) ? 0 : Convert.ToInt32(reader.GetValue(6));
                    hits.Add(new LabHistoryHit
                    {
                        Kind = "独立批次",
                        Id = reader.IsDBNull(0) ? "" : reader.GetString(0),
                        Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                        // 批次没有项目阶段,把状态与完成度放进同一格,设计方法不冒充阶段
                        Status = (reader.IsDBNull(2) ? "" : reader.GetString(2)) + $",完成 {done}/{total} 组",
                        Phase = "",
                        Rounds = 0,
                        TotalExperiments = done,
                        BestResponseValue = null,
                        UpdatedTime = reader.IsDBNull(4) ? DateTime.MinValue : reader.GetDateTime(4)
                    });
                }
            }

            // 补全命中说明(名称/目标直接判断,其余归为因子/响应名等检索列)
            foreach (var h in hits)
            {
                var where = new List<string>();
                foreach (var t in terms)
                {
                    if (h.Name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) where.Add($"名称含「{t}」");
                    else if (h.Kind == "项目" && (h.Objective ?? "").IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) where.Add($"目标含「{t}」");
                    else where.Add(h.Kind == "项目" ? $"因子/响应/批次名含「{t}」" : $"因子/响应名含「{t}」");
                }
                h.MatchedOn = string.Join(",", where);
            }

            return hits
                .OrderByDescending(h => h.UpdatedTime)
                .Take(limit)
                .ToList();
        }
    }
}
