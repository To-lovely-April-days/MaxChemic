using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using MySql.Data.MySqlClient;

namespace MaxChemical.Mcp.History;

/// <summary>
/// MySQL 只读访问层。
///
/// ── 只读是分层保证的,不能只靠一层 ──────────────────────────────────
/// 1. **数据库账号本身应当是只读的**(只 GRANT SELECT)。这是唯一真正可靠的一层,
///    部署文档里写成硬性要求。下面几层是「账号配错了也别出事」的兜底。
/// 2. 本类只提供 ExecuteQueryAsync,没有任何执行非查询语句的入口。
/// 3. 受限 SQL 工具的入参过 GuardReadOnly:必须以 SELECT/WITH 开头、
///    禁止多语句、禁止注释、禁止写类关键字、强制 LIMIT。
/// 4. 行数与超时都有上限,防止一条查询把模型上下文撑爆或把库拖死。
/// </summary>
public sealed class Database
{
    private readonly AppConfig _config;

    public Database(AppConfig config) => _config = config;

    public int MaxRows => _config.MaxRows;

    /// <summary>是否开放受限只读 SQL 工具。关掉后 query_sql 根本不会出现在 tools/list 里。</summary>
    public bool EnableRawSql => _config.EnableRawSql;

    public sealed record ProbeResult(bool Ok, int TableCount, string Error);

    /// <summary>启动时探活。失败不抛,让调用方决定怎么处理。</summary>
    public async Task<ProbeResult> ProbeAsync()
    {
        try
        {
            await using var conn = new MySqlConnection(_config.ConnectionString);
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand(
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE()", conn);
            cmd.CommandTimeout = _config.CommandTimeoutSeconds;
            object? n = await cmd.ExecuteScalarAsync();
            return new ProbeResult(true, Convert.ToInt32(n ?? 0), "");
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, 0, ex.Message);
        }
    }

    /// <summary>
    /// 执行一条参数化查询,把结果渲染成 Markdown 表格。
    ///
    /// 直接返回 Markdown 而不是 JSON 是有意的:模型读表格比读嵌套 JSON 省 token,
    /// 而且人把结果转发到微信/日报里时不用再加工。
    /// </summary>
    public async Task<string> QueryToMarkdownAsync(
        string sql, IReadOnlyDictionary<string, object?>? parameters = null, int? maxRows = null)
    {
        int limit = maxRows ?? _config.MaxRows;

        await using var conn = new MySqlConnection(_config.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.CommandTimeout = _config.CommandTimeoutSeconds;
        if (parameters != null)
        {
            foreach (var (k, v) in parameters)
                cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        }

        // 不用 SequentialAccess:那个模式下同一列先 IsDBNull 再 GetValue 会踩到「不能回退」的限制,
        // 而我们这里的行都很小,顺序访问省下的那点内存不值得担这个风险。
        await using var reader = await cmd.ExecuteReaderAsync();
        return await RenderAsync(reader, limit);
    }

    private static async Task<string> RenderAsync(System.Data.Common.DbDataReader reader, int limit)
    {
        int columnCount = reader.FieldCount;
        if (columnCount == 0) return "（查询没有返回任何列）";

        var headers = new string[columnCount];
        for (int i = 0; i < columnCount; i++) headers[i] = reader.GetName(i);

        var rows = new List<string[]>();
        bool truncated = false;

        while (await reader.ReadAsync())
        {
            if (rows.Count >= limit) { truncated = true; break; }

            var row = new string[columnCount];
            for (int i = 0; i < columnCount; i++)
                row[i] = FormatCell(reader.IsDBNull(i) ? null : reader.GetValue(i));
            rows.Add(row);
        }

        if (rows.Count == 0) return "（没有匹配的记录）";

        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", headers)).AppendLine(" |");
        sb.Append("|").Append(string.Concat(Enumerable.Repeat("---|", columnCount))).AppendLine();
        foreach (var row in rows)
            sb.Append("| ").Append(string.Join(" | ", row)).AppendLine(" |");

        sb.AppendLine();
        sb.Append($"共 {rows.Count} 行");
        if (truncated)
            sb.Append($"（**已截断到 {limit} 行**，如需更多请缩小时间范围或加筛选条件）");

        return sb.ToString();
    }

    /// <summary>
    /// 单元格格式化。三件事:
    /// 竖线要转义(否则会把 Markdown 表格结构撑破)、换行压成空格、超长文本截断。
    /// 最后一条尤其重要 —— input_parameters / output_parameters 是 TEXT 字段,
    /// 一条可能有好几 KB,几十行就能把模型上下文吃满。
    /// </summary>
    private static string FormatCell(object? value)
    {
        if (value is null) return "";

        string text = value switch
        {
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss"),
            bool b => b ? "是" : "否",
            byte[] bytes => $"(二进制 {bytes.Length} 字节)",
            double d => d.ToString("0.####"),
            float f => f.ToString("0.####"),
            decimal m => m.ToString("0.####"),
            _ => value.ToString() ?? ""
        };

        text = text.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|").Trim();
        const int max = 200;
        if (text.Length > max) text = text[..max] + "…";
        return text;
    }

    // ── 只读守卫 ────────────────────────────────────────────────────────

    private static readonly Regex ForbiddenKeywords = new(
        @"\b(insert|update|delete|drop|alter|create|truncate|grant|revoke|replace|call|load|outfile|dumpfile|into|set|lock|unlock|rename|handler|prepare|execute|do|kill|flush|reset|shutdown)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HasLimit = new(@"\blimit\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 校验并规范化一条外来 SQL。返回 null 表示通过,否则返回拒绝理由。
    /// </summary>
    public string? GuardReadOnly(string sql, out string normalized)
    {
        normalized = (sql ?? "").Trim().TrimEnd(';').Trim();

        if (normalized.Length == 0) return "SQL 不能为空";

        // 注释直接拒 —— 注释是绕过关键字检查最常用的手法,而正常查询用不上
        if (normalized.Contains("--") || normalized.Contains("/*") || normalized.Contains('#'))
            return "出于安全考虑,SQL 中不允许出现注释（--、/*、#）";

        // 多语句直接拒。分号已在上面 TrimEnd 掉,这里还有说明中间夹了语句
        if (normalized.Contains(';'))
            return "只允许单条语句,不能用分号拼接多条";

        string head = normalized.TrimStart();
        if (!head.StartsWith("select", StringComparison.OrdinalIgnoreCase) &&
            !head.StartsWith("with", StringComparison.OrdinalIgnoreCase))
            return "只允许 SELECT 或 WITH 开头的只读查询";

        var hit = ForbiddenKeywords.Match(normalized);
        if (hit.Success)
            return $"SQL 中出现了不允许的关键字「{hit.Value}」。本接口只提供只读查询。";

        // 没写 LIMIT 就替它加一个,别让一条 SELECT * 把整张表拉回来
        if (!HasLimit.IsMatch(normalized))
            normalized += $" LIMIT {_config.MaxRows}";

        return null;
    }
}

// 渲染直接吃 System.Data.Common.DbDataReader —— MySqlCommand.ExecuteReaderAsync()
// 返回的就是它,不用再包一层适配器,将来换数据库驱动这段也不用动。
