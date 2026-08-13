using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MaxChemical.Mcp.History;

public sealed record ToolResult(string Text, bool IsError = false);

/// <summary>
/// MCP 工具表。
///
/// ── 设计取舍 ────────────────────────────────────────────────────────
/// **所有工具最终都落到「参数化 SQL → Markdown 表格」这一条路上。**
/// 没有为每张表写 DTO 和映射代码,原因是:实验表的结构已核对过(见 Maxchemical.sql),
/// 但 16 张 DOE 表的列名只能从代码里的字符串反推,不敢写死。
/// 统一走 SELECT + 动态渲染,少一整类「列名拼错、上线才发现」的问题。
///
/// **表名/列名一律不接受外部拼接。** SQL 参数只能绑值不能绑标识符,
/// 凡是需要标识符的地方(describe_schema 的表名)都过 IsSafeIdentifier 白名单校验。
/// </summary>
public sealed class ToolRegistry
{
    private readonly Database _db;
    private readonly List<ToolDef> _tools = new();

    private sealed record ToolDef(
        string Name,
        string Description,
        JsonObject InputSchema,
        Func<JsonObject?, Task<ToolResult>> Handler);

    public ToolRegistry(Database db)
    {
        _db = db;
        Register();
    }

    public JsonArray DescribeAll()
    {
        var arr = new JsonArray();
        foreach (var t in _tools)
        {
            arr.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = t.InputSchema.DeepClone()
            });
        }
        return arr;
    }

    public async Task<ToolResult> CallAsync(string name, JsonObject? args)
    {
        var tool = _tools.FirstOrDefault(t => t.Name == name);
        if (tool is null)
            return new ToolResult($"没有名为「{name}」的工具。可用工具:{string.Join("、", _tools.Select(t => t.Name))}", true);

        try
        {
            return await tool.Handler(args);
        }
        catch (MySql.Data.MySqlClient.MySqlException ex)
        {
            // 把数据库错误原样回给模型,它能据此改查询;但不要把连接串泄出去
            return new ToolResult($"数据库查询失败:{ex.Message}", true);
        }
        catch (Exception ex)
        {
            Program.Log($"工具 {name} 异常:{ex}");
            return new ToolResult($"工具执行失败:{ex.Message}", true);
        }
    }

    // ── 参数读取小工具 ──────────────────────────────────────────────

    private static string? Str(JsonObject? o, string key)
    {
        var v = o?[key];
        if (v is null) return null;
        var s = v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : v.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static int Int(JsonObject? o, string key, int fallback)
    {
        var v = o?[key];
        if (v is null) return fallback;
        try
        {
            return v.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.Number => v.GetValue<int>(),
                System.Text.Json.JsonValueKind.String => int.TryParse(v.GetValue<string>(), out var n) ? n : fallback,
                _ => fallback
            };
        }
        catch { return fallback; }
    }

    private static bool? Bool(JsonObject? o, string key)
    {
        var v = o?[key];
        if (v is null) return null;
        try
        {
            return v.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.String => bool.TryParse(v.GetValue<string>(), out var b) ? b : null,
                _ => null
            };
        }
        catch { return null; }
    }

    private static readonly Regex SafeIdentifier = new(@"^[A-Za-z0-9_]{1,64}$", RegexOptions.Compiled);
    private static bool IsSafeIdentifier(string s) => SafeIdentifier.IsMatch(s);

    private static JsonObject Schema(params (string Name, string Type, string Desc, bool Required)[] props)
    {
        var p = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, type, desc, req) in props)
        {
            p[name] = new JsonObject { ["type"] = type, ["description"] = desc };
            if (req) required.Add(name);
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = p };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    // ── 工具注册 ────────────────────────────────────────────────────

    private void Register()
    {
        // ══════════ 1. 看有哪些表和列 ══════════
        _tools.Add(new ToolDef(
            "describe_schema",
            "列出数据库里的表和字段。不确定某个信息存在哪张表时先调这个。" +
            "不传 table 时返回所有表及其行数概览;传了 table 就返回该表的完整字段清单。",
            Schema(("table", "string", "表名。留空则列出所有表。", false)),
            async args =>
            {
                string? table = Str(args, "table");

                if (table is null)
                {
                    string sql = @"
SELECT table_name AS 表名, table_rows AS 估算行数, table_comment AS 说明
FROM information_schema.tables
WHERE table_schema = DATABASE()
ORDER BY table_name";
                    string md = await _db.QueryToMarkdownAsync(sql, null, 500);
                    return new ToolResult(
                        "当前数据库的表：\n\n" + md +
                        "\n\n注：估算行数来自 information_schema，是近似值，不是精确计数。" +
                        "要看某张表的字段，再调一次本工具并传 table 参数。");
                }

                if (!IsSafeIdentifier(table))
                    return new ToolResult($"表名「{table}」含有非法字符。表名只能是字母、数字和下划线。", true);

                string colSql = @"
SELECT column_name AS 字段, column_type AS 类型, is_nullable AS 可空,
       column_default AS 默认值, column_comment AS 说明
FROM information_schema.columns
WHERE table_schema = DATABASE() AND table_name = @t
ORDER BY ordinal_position";
                string cols = await _db.QueryToMarkdownAsync(colSql, new Dictionary<string, object?> { ["@t"] = table }, 300);
                return new ToolResult($"表 `{table}` 的字段：\n\n{cols}");
            }));

        // ══════════ 2. 实验记录清单 ══════════
        _tools.Add(new ToolDef(
            "list_experiments",
            "查实验记录清单（experiment_records 表）。可按关键词、最近天数、是否成功、是否排除模拟模式筛选。" +
            "这是回答「最近做了哪些实验」「上周那批跑成功没有」这类问题的入口。",
            Schema(
                ("keyword", "string", "在实验名称、流程名称、描述、标签里模糊匹配。", false),
                ("days", "integer", "只看最近 N 天。不传则不限时间。", false),
                ("only_successful", "boolean", "true=只看成功的，false=只看失败的，不传=都要。", false),
                ("exclude_simulation", "boolean", "true=排除模拟模式的实验，只看实机数据。建议默认传 true。", false),
                ("limit", "integer", "最多返回多少条，默认 50。", false)),
            async args =>
            {
                var where = new List<string>();
                var p = new Dictionary<string, object?>();

                string? kw = Str(args, "keyword");
                if (kw != null)
                {
                    where.Add("(experiment_name LIKE @kw OR flow_name LIKE @kw OR description LIKE @kw OR tags LIKE @kw)");
                    p["@kw"] = $"%{kw}%";
                }

                int days = Int(args, "days", 0);
                if (days > 0)
                {
                    where.Add("start_time >= DATE_SUB(NOW(), INTERVAL @days DAY)");
                    p["@days"] = days;
                }

                var ok = Bool(args, "only_successful");
                if (ok.HasValue) where.Add(ok.Value ? "is_successful = 1" : "is_successful = 0");

                if (Bool(args, "exclude_simulation") == true) where.Add("simulation_mode = 0");

                string sql = $@"
SELECT experiment_id AS 实验编号, experiment_name AS 实验名称, flow_name AS 流程,
       start_time AS 开始时间, duration_seconds AS 耗时秒, is_successful AS 成功,
       completed_nodes AS 已完成步, total_nodes AS 总步数,
       simulation_mode AS 模拟模式, operator_name AS 操作者, error_message AS 错误
FROM experiment_records
{(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
ORDER BY start_time DESC";

                string md = await _db.QueryToMarkdownAsync(sql, p, Int(args, "limit", 50));
                return new ToolResult(md);
            }));

        // ══════════ 3. 单次实验详情 ══════════
        _tools.Add(new ToolDef(
            "get_experiment",
            "看一次实验的完整情况：主记录 + 逐步节点执行明细。" +
            "实验失败时用它定位是哪一步、哪台设备、什么命令出的问题。",
            Schema(
                ("experiment_id", "string", "实验编号，来自 list_experiments 的「实验编号」列。", true),
                ("include_variables", "boolean", "是否同时返回该实验记录的变量值，默认 true。", false)),
            async args =>
            {
                string? id = Str(args, "experiment_id");
                if (id is null) return new ToolResult("必须提供 experiment_id。", true);

                var p = new Dictionary<string, object?> { ["@id"] = id };
                var sb = new StringBuilder();

                sb.AppendLine("## 实验主记录\n");
                sb.AppendLine(await _db.QueryToMarkdownAsync(@"
SELECT experiment_id AS 实验编号, experiment_name AS 实验名称, flow_name AS 流程,
       start_time AS 开始, end_time AS 结束, duration_seconds AS 耗时秒,
       is_successful AS 成功, completed_nodes AS 已完成步, total_nodes AS 总步数,
       simulation_mode AS 模拟模式, operator_name AS 操作者,
       description AS 描述, tags AS 标签, error_message AS 错误
FROM experiment_records WHERE experiment_id = @id", p, 1));

                sb.AppendLine("\n## 节点执行明细\n");
                sb.AppendLine(await _db.QueryToMarkdownAsync(@"
SELECT execution_order AS 序, node_name AS 节点, node_type AS 类型,
       device_name AS 设备, command_name AS 命令,
       start_time AS 开始, duration_ms AS 耗时毫秒, is_successful AS 成功,
       error_message AS 错误
FROM experiment_node_records WHERE experiment_id = @id
ORDER BY execution_order, start_time", p, 300));

                if (Bool(args, "include_variables") != false)
                {
                    sb.AppendLine("\n## 变量记录\n");
                    sb.AppendLine(await _db.QueryToMarkdownAsync(@"
SELECT variable_name AS 变量, variable_value AS 值, variable_type AS 类型,
       source_device_id AS 来源设备, timestamp AS 时间
FROM experiment_variables WHERE experiment_id = @id
ORDER BY timestamp", p, 200));
                }

                return new ToolResult(sb.ToString());
            }));

        // ══════════ 4. 设备时序数据 ══════════
        _tools.Add(new ToolDef(
            "get_experiment_data",
            "查一次实验里设备采集的数据（experiment_device_data 表）。\n" +
            "**默认返回统计摘要而不是原始点**——一次实验一台设备可能有上万个采样点，" +
            "全量拉回来既没法读也会撑爆上下文。要看趋势用 mode=summary，" +
            "确实要原始点再用 mode=raw 并配合 device_name / parameter_name 缩小范围。",
            Schema(
                ("experiment_id", "string", "实验编号。", true),
                ("mode", "string", "summary（默认，按设备+参数给出点数/最小/最大/均值）或 raw（原始采样点）。", false),
                ("device_name", "string", "只看某台设备，模糊匹配。", false),
                ("parameter_name", "string", "只看某个参数，模糊匹配。", false),
                ("limit", "integer", "raw 模式下最多返回多少个点，默认 200。", false)),
            async args =>
            {
                string? id = Str(args, "experiment_id");
                if (id is null) return new ToolResult("必须提供 experiment_id。", true);

                var where = new List<string> { "experiment_id = @id" };
                var p = new Dictionary<string, object?> { ["@id"] = id };

                string? dev = Str(args, "device_name");
                if (dev != null) { where.Add("device_name LIKE @dev"); p["@dev"] = $"%{dev}%"; }

                string? par = Str(args, "parameter_name");
                if (par != null) { where.Add("parameter_name LIKE @par"); p["@par"] = $"%{par}%"; }

                string whereSql = string.Join(" AND ", where);
                bool raw = string.Equals(Str(args, "mode"), "raw", StringComparison.OrdinalIgnoreCase);

                if (!raw)
                {
                    // parameter_value 是 TEXT,统计前先转数值;转不动的(字符串型参数)只报点数
                    string sql = $@"
SELECT device_name AS 设备, parameter_name AS 参数, unit AS 单位,
       COUNT(*) AS 采样点数,
       MIN(timestamp) AS 起, MAX(timestamp) AS 止,
       ROUND(MIN(CAST(parameter_value AS DECIMAL(20,6))), 4) AS 最小,
       ROUND(MAX(CAST(parameter_value AS DECIMAL(20,6))), 4) AS 最大,
       ROUND(AVG(CAST(parameter_value AS DECIMAL(20,6))), 4) AS 平均
FROM experiment_device_data
WHERE {whereSql} AND data_direction = 'OUTPUT'
GROUP BY device_name, parameter_name, unit
ORDER BY device_name, parameter_name";
                    string md = await _db.QueryToMarkdownAsync(sql, p, 200);
                    return new ToolResult(
                        md + "\n\n注：最小/最大/平均是把 parameter_value 按数值解析后算的，" +
                        "文本型参数（如状态描述）这三列会是空或 0，看采样点数即可。" +
                        "要看具体波形请用 mode=raw。");
                }

                string rawSql = $@"
SELECT timestamp AS 时间, device_name AS 设备, parameter_name AS 参数,
       parameter_value AS 值, unit AS 单位, node_name AS 所属节点
FROM experiment_device_data
WHERE {whereSql} AND data_direction = 'OUTPUT'
ORDER BY timestamp";
                return new ToolResult(await _db.QueryToMarkdownAsync(rawSql, p, Int(args, "limit", 200)));
            }));

        // ══════════ 5. 聚合统计 ══════════
        _tools.Add(new ToolDef(
            "summarize_experiments",
            "按天 / 按流程 / 按操作者聚合实验记录，给出次数、成功率、平均耗时。" +
            "适合回答「这个月做了多少次」「哪个流程失败率最高」这类问题，也适合生成周报。",
            Schema(
                ("group_by", "string", "day（按天）、flow（按流程名）、operator（按操作者）。默认 day。", false),
                ("days", "integer", "统计最近 N 天，默认 30。", false),
                ("exclude_simulation", "boolean", "是否排除模拟模式，默认 true。", false)),
            async args =>
            {
                // 分组字段绝不从入参拼接,只能在这三个写死的选项里选
                string groupBy = (Str(args, "group_by") ?? "day").ToLowerInvariant();
                (string expr, string label) = groupBy switch
                {
                    "flow" => ("flow_name", "流程"),
                    "operator" => ("COALESCE(operator_name, '(未记录)')", "操作者"),
                    _ => ("DATE(start_time)", "日期")
                };

                var where = new List<string> { "start_time >= DATE_SUB(NOW(), INTERVAL @days DAY)" };
                var p = new Dictionary<string, object?> { ["@days"] = Math.Clamp(Int(args, "days", 30), 1, 3650) };
                if (Bool(args, "exclude_simulation") != false) where.Add("simulation_mode = 0");

                string sql = $@"
SELECT {expr} AS `{label}`,
       COUNT(*) AS 实验次数,
       SUM(is_successful) AS 成功次数,
       ROUND(100 * SUM(is_successful) / COUNT(*), 1) AS 成功率百分比,
       ROUND(AVG(duration_seconds), 1) AS 平均耗时秒,
       ROUND(MAX(duration_seconds), 1) AS 最长耗时秒
FROM experiment_records
WHERE {string.Join(" AND ", where)}
GROUP BY {expr}
ORDER BY {expr} DESC";

                return new ToolResult(await _db.QueryToMarkdownAsync(sql, p, 200));
            }));

        // ══════════ 6. DOE 批次清单 ══════════
        _tools.Add(new ToolDef(
            "list_doe_batches",
            "查 DOE（试验设计）批次清单。回答「昨晚那批 DOE 跑完没有」「最近做了哪些优化批次」。",
            Schema(
                ("keyword", "string", "在批次名称、流程名里模糊匹配。", false),
                ("status", "string", "按状态筛选（具体取值先用本工具不带筛选跑一次看有哪些）。", false),
                ("limit", "integer", "最多返回多少条，默认 50。", false)),
            async args =>
            {
                var where = new List<string>();
                var p = new Dictionary<string, object?>();

                string? kw = Str(args, "keyword");
                if (kw != null) { where.Add("(batch_name LIKE @kw OR flow_name LIKE @kw)"); p["@kw"] = $"%{kw}%"; }

                string? st = Str(args, "status");
                if (st != null) { where.Add("status = @st"); p["@st"] = st; }

                // 用 SELECT * :DOE 表的列在代码里是动态映射的,写死列名风险更大
                string sql = $@"
SELECT * FROM doe_batches
{(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
ORDER BY created_time DESC";

                return new ToolResult(await _db.QueryToMarkdownAsync(sql, p, Int(args, "limit", 50)));
            }));

        // ══════════ 7. DOE 批次详情 ══════════
        _tools.Add(new ToolDef(
            "get_doe_batch",
            "看一个 DOE 批次的详情：批次信息 + 因子设置 + 响应定义 + 各组运行结果。" +
            "回答「这批的最优条件是什么」「哪几组还没跑」。",
            Schema(("batch_id", "string", "批次编号，来自 list_doe_batches。", true)),
            async args =>
            {
                string? id = Str(args, "batch_id");
                if (id is null) return new ToolResult("必须提供 batch_id。", true);

                var p = new Dictionary<string, object?> { ["@id"] = id };
                var sb = new StringBuilder();

                // 逐段查,某张表不存在或列名对不上时只影响那一段,不会整条命令失败
                async Task Section(string title, string sql, int rows)
                {
                    sb.AppendLine($"\n## {title}\n");
                    try { sb.AppendLine(await _db.QueryToMarkdownAsync(sql, p, rows)); }
                    catch (Exception ex) { sb.AppendLine($"（这一段查询失败：{ex.Message}）"); }
                }

                await Section("批次信息", "SELECT * FROM doe_batches WHERE batch_id = @id", 1);
                await Section("因子", "SELECT * FROM doe_factors WHERE batch_id = @id ORDER BY sort_order", 50);
                await Section("响应", "SELECT * FROM doe_responses WHERE batch_id = @id ORDER BY sort_order", 50);
                await Section("各组运行", "SELECT * FROM doe_runs WHERE batch_id = @id ORDER BY run_index", 300);

                return new ToolResult(sb.ToString());
            }));

        // ══════════ 8. 受限只读 SQL ══════════
        // 配置里关掉时**根本不注册**,而不是注册了再在里面拒绝 ——
        // 后者模型会一直看见这个工具、反复尝试、反复被拒,白烧 token。
        if (_db.EnableRawSql)
        {
            _tools.Add(new ToolDef(
                "query_sql",
                "执行一条**只读** SQL 查询。上面那些工具覆盖不到的问题用这个。\n" +
                "限制：只允许 SELECT / WITH 开头；不允许分号拼接多条；不允许注释；" +
                "不允许任何写类关键字；没写 LIMIT 会自动加上。\n" +
                "不知道有哪些表和列时先调 describe_schema。",
                Schema(
                    ("sql", "string", "要执行的 SELECT 语句。", true),
                    ("limit", "integer", "最多返回多少行，默认取服务端配置。", false)),
                async args =>
                {
                    string? sql = Str(args, "sql");
                    if (sql is null) return new ToolResult("必须提供 sql。", true);

                    string? reject = _db.GuardReadOnly(sql, out string normalized);
                    if (reject != null)
                    {
                        Program.Log($"query_sql 被拒:{reject} — 原文:{sql}");
                        return new ToolResult($"这条 SQL 被拒绝了：{reject}", true);
                    }

                    Program.Log($"query_sql 执行:{normalized}");
                    string md = await _db.QueryToMarkdownAsync(normalized, null, Int(args, "limit", _db.MaxRows));
                    return new ToolResult($"```sql\n{normalized}\n```\n\n{md}");
                }));
        }
    }
}
