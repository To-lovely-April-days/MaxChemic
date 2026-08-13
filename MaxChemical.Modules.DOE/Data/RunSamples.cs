using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;

namespace MaxChemical.Modules.DOE.Data
{
    /// <summary>
    /// 一条取样记录:某批次某组在某时刻某物种的浓度。
    /// 流动化学下 SampleTime 即停留时间(不同 τ 的出口取样共同构成浓度-时间曲线);
    /// 釜式则为真实反应时间。V2 机理层(反应网络拟合)的数据基座。
    /// </summary>
    public sealed class RunSample
    {
        public long Id { get; set; }
        public string BatchId { get; set; } = "";
        public int RunIndex { get; set; }
        public double SampleTime { get; set; }        // min
        public string Species { get; set; } = "";
        public double Concentration { get; set; }     // mol/L(unit 仅作标注)
        public string Unit { get; set; } = "mol/L";
        public string Source { get; set; } = "manual";
        public DateTime CreatedTime { get; set; }
    }

    public partial interface IDOERepository
    {
        /// <summary>保存取样点(overwrite=true 先清空该组已有记录);表不存在时抛带建表指引的异常。</summary>
        Task SaveRunSamplesAsync(string batchId, int runIndex, List<RunSample> samples, bool overwrite);

        /// <summary>读取批次全部取样点(runIndex 为 null 取全部组)。</summary>
        Task<List<RunSample>> GetRunSamplesAsync(string batchId, int? runIndex = null);
    }

    public partial class DOERepository
    {
        private const string SamplesMissingHint =
            "doe_run_samples 表不存在:请先在数据库执行建表 SQL(交付说明里提供),再录入取样数据。";

        public async Task SaveRunSamplesAsync(string batchId, int runIndex, List<RunSample> samples, bool overwrite)
        {
            if (samples == null || samples.Count == 0) return;
            try
            {
                using var conn = CreateConnection();
                await conn.OpenAsync();

                if (overwrite)
                {
                    using var del = new MySqlCommand(
                        "DELETE FROM doe_run_samples WHERE batch_id=@b AND run_index=@r", conn);
                    del.Parameters.AddWithValue("@b", batchId);
                    del.Parameters.AddWithValue("@r", runIndex);
                    await del.ExecuteNonQueryAsync();
                }

                const string sql = @"
                    INSERT INTO doe_run_samples
                    (batch_id, run_index, sample_time, species, concentration, unit, source)
                    VALUES (@b, @r, @t, @s, @c, @u, @src)";
                foreach (var s in samples)
                {
                    using var cmd = new MySqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@b", batchId);
                    cmd.Parameters.AddWithValue("@r", runIndex);
                    cmd.Parameters.AddWithValue("@t", s.SampleTime);
                    cmd.Parameters.AddWithValue("@s", s.Species ?? "");
                    cmd.Parameters.AddWithValue("@c", s.Concentration);
                    cmd.Parameters.AddWithValue("@u", string.IsNullOrEmpty(s.Unit) ? "mol/L" : s.Unit);
                    cmd.Parameters.AddWithValue("@src", string.IsNullOrEmpty(s.Source) ? "manual" : s.Source);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
            catch (MySqlException ex) when (ex.Message.Contains("doesn't exist") || ex.Number == 1146)
            {
                throw new InvalidOperationException(SamplesMissingHint);
            }
        }

        public async Task<List<RunSample>> GetRunSamplesAsync(string batchId, int? runIndex = null)
        {
            var list = new List<RunSample>();
            try
            {
                using var conn = CreateConnection();
                await conn.OpenAsync();
                string sql = "SELECT id,batch_id,run_index,sample_time,species,concentration,unit,source,created_time " +
                             "FROM doe_run_samples WHERE batch_id=@b" +
                             (runIndex.HasValue ? " AND run_index=@r" : "") +
                             " ORDER BY sample_time, species";
                using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@b", batchId);
                if (runIndex.HasValue) cmd.Parameters.AddWithValue("@r", runIndex.Value);
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    list.Add(new RunSample
                    {
                        Id = reader.GetInt64(0),
                        BatchId = reader.GetString(1),
                        RunIndex = reader.GetInt32(2),
                        SampleTime = reader.GetDouble(3),
                        Species = reader.GetString(4),
                        Concentration = reader.GetDouble(5),
                        Unit = reader.IsDBNull(6) ? "mol/L" : reader.GetString(6),
                        Source = reader.IsDBNull(7) ? "manual" : reader.GetString(7),
                        CreatedTime = reader.IsDBNull(8) ? DateTime.MinValue : reader.GetDateTime(8)
                    });
                }
            }
            catch (MySqlException ex) when (ex.Message.Contains("doesn't exist") || ex.Number == 1146)
            {
                throw new InvalidOperationException(SamplesMissingHint);
            }
            return list;
        }
    }
}
