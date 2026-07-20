using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MaxChemical.Infrastructure.DOE;
using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.Services;

namespace MaxChemical.Shell.Services.Agent.Tools
{
    /// <summary>
    /// 开放实验接入的文件缓存:解析后的表格按令牌暂存,映射确认与预览生成之间不重读文件。
    /// 与 DoePreviewStore 同一纪律:少量、限时。
    /// </summary>
    internal static class ImportFileStore
    {
        internal sealed class ParsedTable
        {
            public string FilePath;
            public List<string> Headers;
            public List<string[]> Rows;
            /// <summary>每个数据行在文件里的物理行号(空行/全空行被跳过后,报错要指回用户在文件里看得到的行)。</summary>
            public List<int> RowLines;
            public DateTime CreatedAt;
        }

        private static readonly object Gate = new();
        private static readonly Dictionary<string, ParsedTable> Tables = new();

        public static string Put(ParsedTable t)
        {
            lock (Gate)
            {
                foreach (var k in Tables.Where(kv => (DateTime.UtcNow - kv.Value.CreatedAt).TotalMinutes > 30)
                             .Select(kv => kv.Key).ToList())
                    Tables.Remove(k);
                while (Tables.Count >= 3)
                    Tables.Remove(Tables.OrderBy(kv => kv.Value.CreatedAt).First().Key);

                string id = Guid.NewGuid().ToString("N").Substring(0, 8);
                t.CreatedAt = DateTime.UtcNow;
                Tables[id] = t;
                return id;
            }
        }

        /// <summary>取但不删:映射可能要来回改几次,预览生成成功前令牌保持有效。</summary>
        public static ParsedTable Get(string id)
        {
            lock (Gate)
            {
                return id != null && Tables.TryGetValue(id, out var t) ? t : null;
            }
        }
    }

    /// <summary>
    /// 开放实验接入·第一步:读取用户的实验数据文件(CSV/TXT),返回列结构与数据预览。
    /// 不猜映射——列里哪个是因子、哪个是响应,由后续对话与用户确认后交给 create_import_preview。
    /// </summary>
    public sealed class InspectExperimentFileTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public InspectExperimentFileTool(AgentToolContext ctx) => _ctx = ctx;

        static InspectExperimentFileTool()
        {
            // 中文 Excel 另存的 CSV 常是 GBK 编码,net8 需注册 CodePages 提供程序才解得开
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
        }

        public string Name => "inspect_experiment_file";
        public string DisplayName => "读取实验数据文件";
        public string Description => @"开放实验接入的第一步:读取用户自己的实验数据文件(CSV/TXT,Excel 请先另存为 CSV),返回列清单、每列的类型与取值范围、空缺统计和前几行预览。
不传 file_path 时会弹出文件选择框让用户挑文件(用户取消则如实返回)。两种用途:①历史数据导入(带结果的行,喂给记忆/预测/动力学);②自带方案执行(只有条件没有结果的行,建成可执行批次接着跑);同一文件里两种行可以混合(做了一半的实验接着做)。
拿到列清单后,与用户确认哪些列是因子、哪些列是响应(名字含收率/转化率/纯度等的通常是响应,但必须用户确认,不要自作主张),再调 create_import_preview。只读。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""file_path"":{""type"":""string"",""description"":""数据文件完整路径;省略则弹文件选择框让用户挑""}
  }
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "读取实验数据文件";

        public Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            string path = AgentToolContext.GetString(args, "file_path");

            if (string.IsNullOrWhiteSpace(path))
            {
                string picked = null;
                try
                {
                    AgentToolContext.RunOnUi(() =>
                    {
                        var dlg = new Microsoft.Win32.OpenFileDialog
                        {
                            Title = "选择实验数据文件(小桐导入)",
                            Filter = "实验数据 (*.csv;*.txt)|*.csv;*.txt|所有文件 (*.*)|*.*",
                            CheckFileExists = true
                        };
                        if (dlg.ShowDialog() == true) picked = dlg.FileName;
                    });
                }
                catch (Exception ex)
                {
                    return Task.FromResult(AgentToolResult.Fail("文件选择框打开失败:" + ex.Message));
                }
                if (string.IsNullOrEmpty(picked))
                    return Task.FromResult(AgentToolResult.Ok("用户取消了文件选择。不要重复弹框,询问用户是否要提供文件路径或稍后再导入。"));
                path = picked;
            }

            if (!File.Exists(path))
                return Task.FromResult(AgentToolResult.Fail($"文件不存在:{path}"));

            List<string> headers;
            List<string[]> rows;
            List<int> rowLines;
            try
            {
                (headers, rows, rowLines) = ParseDelimitedFile(path);
            }
            catch (Exception ex)
            {
                return Task.FromResult(AgentToolResult.Fail("文件解析失败:" + ex.Message +
                    "。请确认是 CSV/TXT 文本表格(Excel 请先另存为 CSV)。"));
            }

            if (headers.Count < 2)
                return Task.FromResult(AgentToolResult.Fail("表头至少要 2 列(至少 1 个因子 + 1 个响应)。"));
            if (rows.Count == 0)
                return Task.FromResult(AgentToolResult.Fail("表头之下没有数据行。"));
            if (rows.Count > 200)
                return Task.FromResult(AgentToolResult.Fail($"数据 {rows.Count} 行超过上限 200 行,请拆分文件后分批导入。"));

            string token = ImportFileStore.Put(new ImportFileStore.ParsedTable
            {
                FilePath = path,
                Headers = headers,
                Rows = rows,
                RowLines = rowLines
            });

            var sb = new StringBuilder();
            sb.AppendLine($"文件已读取(file_token:{token}):{Path.GetFileName(path)},{headers.Count} 列 × {rows.Count} 行数据。");
            sb.AppendLine();
            sb.AppendLine("| 列名 | 类型 | 非空/总行 | 数值范围 | 备注 |");
            sb.AppendLine("| --- | --- | --- | --- | --- |");
            for (int c = 0; c < headers.Count; c++)
            {
                int nonEmpty = 0, numeric = 0;
                double min = double.MaxValue, max = double.MinValue;
                foreach (var r in rows)
                {
                    string cell = c < r.Length ? r[c]?.Trim() : "";
                    if (string.IsNullOrEmpty(cell)) continue;
                    nonEmpty++;
                    if (TryParseCell(cell, out double v))
                    {
                        numeric++;
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                }
                string type = numeric == 0 ? "文本" : (numeric == nonEmpty ? "数值" : "混合");
                string range = numeric > 0 ? $"{min:0.####} ~ {max:0.####}" : "-";
                string note = LooksLikeResponse(headers[c]) ? "名称疑似响应列(须用户确认)" :
                              nonEmpty < rows.Count ? $"空缺 {rows.Count - nonEmpty} 行" : "";
                sb.AppendLine($"| {headers[c]} | {type} | {nonEmpty}/{rows.Count} | {range} | {note} |");
            }
            sb.AppendLine();
            sb.AppendLine("数据预览(前 8 行):");
            sb.AppendLine("| " + string.Join(" | ", headers.Take(10)) + " |");
            sb.AppendLine("| " + string.Join(" | ", headers.Take(10).Select(_ => "---")) + " |");
            foreach (var r in rows.Take(8))
                sb.AppendLine("| " + string.Join(" | ", headers.Take(10).Select((_, c) => c < r.Length ? r[c] : "")) + " |");
            if (headers.Count > 10) sb.AppendLine($"(列较多,预览只显示前 10 列,共 {headers.Count} 列)");
            sb.AppendLine();
            sb.Append("下一步:与用户确认列映射——哪些列是因子、哪些列是响应(带单位)。响应列全空的行会按「待执行」处理(自带方案执行);" +
                      "带响应值的行按「已完成历史数据」导入。确认后调用 create_import_preview(传 file_token 与映射);" +
                      "若待执行行要在本平台自动跑,因子还需绑定流程参数(list_factor_candidates 取ID)或走化学家坐标系 transform。");
            return Task.FromResult(AgentToolResult.Ok(sb.ToString()));
        }

        /// <summary>列名是否疑似响应(仅提示用,决定权在用户)。</summary>
        internal static bool LooksLikeResponse(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string[] keys = { "收率", "转化率", "纯度", "选择性", "产率", "响应", "yield", "conversion", "purity", "selectivity" };
            return keys.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool TryParseCell(string cell, out double v)
        {
            bool ok = double.TryParse(cell, System.Globalization.NumberStyles.Float,
                          System.Globalization.CultureInfo.InvariantCulture, out v)
                      || double.TryParse(cell, out v);
            // numpy/MATLAB 用 nan/Infinity 表示缺失值,double.TryParse 会放行;
            // 非有限值入库会在 JSON 序列化和因子边界计算处崩溃,必须在源头拒绝
            return ok && double.IsFinite(v);
        }

        /// <summary>
        /// 解析分隔文本:编码 UTF-8 严格尝试失败则回退 GB18030(中文 Excel 的 CSV 常见);
        /// 分隔符按表头行在逗号/分号/制表符中取列数最多者;支持双引号包裹与转义。
        /// 记录切分是引号感知的(Excel 单元格内换行在引号里,不能按物理行切);
        /// 全空行(Excel 尾部残留的 ",,," 行)跳过;数据行携带物理行号供报错定位。
        /// </summary>
        internal static (List<string> Headers, List<string[]> Rows, List<int> RowLines) ParseDelimitedFile(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            string text;
            try
            {
                text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                text = Encoding.GetEncoding("GB18030").GetString(bytes);
            }
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);

            // 分隔符要先于记录切分确定:记录切分按「字段起始处的引号才算包裹」判定,
            // 而字段起始的判定本身依赖分隔符。每个候选各自做一遍引号感知切分再比列数——
            // 按物理行探测会被表头首格的引号内换行截断,进而误判分隔符
            char delimiter = new[] { ',', ';', '\t' }
                .OrderByDescending(d => ProbeColumnCount(text, d))
                .First();

            var lines = SplitRecords(text, delimiter).Where(rec => !string.IsNullOrWhiteSpace(rec.Text)).ToList();
            if (lines.Count == 0) throw new InvalidOperationException("文件为空");

            var headers = SplitLine(lines[0].Text, delimiter)
                .Select(h => h.Trim().Replace('\n', ' ')).ToList();
            var dup = headers.Where(h => h.Length > 0).GroupBy(h => h).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) throw new InvalidOperationException($"表头存在重复列名「{dup.Key}」,请先在文件里改成唯一名称");
            if (headers.Any(string.IsNullOrEmpty)) throw new InvalidOperationException("表头存在空列名,请补全后重试");

            var rows = new List<string[]>();
            var rowLines = new List<int>();
            for (int i = 1; i < lines.Count; i++)
            {
                var cells = SplitLine(lines[i].Text, delimiter);
                // 单元格数多于表头且溢出部分有内容:多半是数值/文本里的未加引号分隔符(如 1,250),
                // 静默截断会让整行列错位、错的数据当成对的入库,必须拒绝
                if (cells.Count > headers.Count && cells.Skip(headers.Count).Any(x => !string.IsNullOrWhiteSpace(x)))
                    throw new InvalidOperationException(
                        $"文件第 {lines[i].Line} 行有 {cells.Count} 个单元格,多于表头的 {headers.Count} 列" +
                        "(常见原因:数值或文本里含未加引号的分隔符,如「1,250」),请修正该行或给单元格加引号");
                while (cells.Count < headers.Count) cells.Add("");
                var row = cells.Take(headers.Count)
                    .Select(x => x.Trim().Replace('\n', ' ')).ToArray();
                if (row.All(string.IsNullOrEmpty)) continue;
                rows.Add(row);
                rowLines.Add(lines[i].Line);
            }
            return (headers, rows, rowLines);
        }

        /// <summary>用候选分隔符探测列数:取首个非空记录做引号感知切分;切分失败(如引号未闭合)记 0 列。</summary>
        private static int ProbeColumnCount(string text, char candidate)
        {
            try
            {
                var first = SplitRecords(text, candidate).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Text));
                return first.Text == null ? 0 : SplitLine(first.Text, candidate).Count;
            }
            catch (InvalidOperationException) { return 0; }
        }

        /// <summary>
        /// 引号感知地把全文切成记录:只有字段起始处的引号才算包裹引号(与 Excel/python-csv 一致,
        /// 字段中途的游离引号按字面处理——否则一个错引号会把后面整个文件吞进一个单元格);
        /// 包裹引号内的换行是单元格内容(Excel 的 Alt+Enter),不当作行分隔;
        /// 引号到文件末尾仍未闭合按错误抛出(静默吞行就是数据丢失)。
        /// 返回每条记录的起始物理行号(1 起),跨行记录占用的行数也计入后续行号。
        /// </summary>
        private static List<(int Line, string Text)> SplitRecords(string text, char delimiter)
        {
            var records = new List<(int, string)>();
            var sb = new StringBuilder();
            bool inQuotes = false, atFieldStart = true;
            int line = 1, recordStartLine = 1, quoteOpenLine = 1;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append("\"\""); i++; }
                        else { inQuotes = false; sb.Append(ch); }
                    }
                    else if (ch == '\n' || ch == '\r')
                    {
                        if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                        line++;
                        sb.Append('\n');
                    }
                    else sb.Append(ch);
                }
                else if (ch == '"' && atFieldStart)
                {
                    inQuotes = true;
                    quoteOpenLine = line;
                    atFieldStart = false;
                    sb.Append(ch);
                }
                else if (ch == '\n' || ch == '\r')
                {
                    if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    line++;
                    records.Add((recordStartLine, sb.ToString()));
                    sb.Clear();
                    recordStartLine = line;
                    atFieldStart = true;
                }
                else
                {
                    atFieldStart = ch == delimiter;
                    sb.Append(ch);
                }
            }
            if (inQuotes)
                throw new InvalidOperationException(
                    $"文件第 {quoteOpenLine} 行存在未闭合的引号,其后的内容都会被并进同一个单元格。" +
                    "请补全引号或删除多余的引号后重试");
            records.Add((recordStartLine, sb.ToString()));
            return records;
        }

        /// <summary>
        /// 单条记录按分隔符切成单元格。引号规则必须与 SplitRecords 一致:
        /// 仅字段起始处的引号算包裹,字段中途的游离引号按字面保留。
        /// </summary>
        private static List<string> SplitLine(string line, char delimiter)
        {
            var cells = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false, atFieldStart = true;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else sb.Append(ch);
                }
                else if (ch == '"' && atFieldStart)
                {
                    inQuotes = true;
                    atFieldStart = false;
                }
                else if (ch == delimiter)
                {
                    cells.Add(sb.ToString());
                    sb.Clear();
                    atFieldStart = true;
                }
                else
                {
                    atFieldStart = false;
                    sb.Append(ch);
                }
            }
            cells.Add(sb.ToString());
            return cells;
        }
    }

    /// <summary>
    /// 开放实验接入·第二步:按确认过的列映射生成导入预览(存 DoePreviewStore),
    /// 用户点头后走统一的 create_doe_batch 入库——查重、AI 标签、项目挂载、化学家坐标系全部复用同一条通道。
    /// </summary>
    public sealed class CreateImportPreviewTool : IAgentTool
    {
        private readonly AgentToolContext _ctx;
        public CreateImportPreviewTool(AgentToolContext ctx) => _ctx = ctx;

        public string Name => "create_import_preview";
        public string DisplayName => "生成导入预览";
        public string Description => @"开放实验接入的第二步:按与用户确认过的列映射,把 inspect_experiment_file 读到的表格生成导入预览。
带响应值的行按「已完成历史数据」导入(喂记忆/分析/预测/动力学);响应列全空的行按「待执行」建组(自带方案,可接着在本平台执行——要自动执行需给因子 bind_parameter_id 或提供化学家坐标系 transform,τ/eq 列名与 transform 一致时不要绑定)。
响应列部分填写的行会被拒绝(说不清做没做完),请让用户补全或清空。预览确认后用 create_doe_batch(preview_id) 入库。";
        public string ParametersSchema => @"{
  ""type"":""object"",
  ""properties"":{
    ""file_token"":{""type"":""string"",""description"":""inspect_experiment_file 返回的令牌""},
    ""batch_name"":{""type"":""string"",""description"":""项目/批次名称:必须用户确认,入库自动加 [AI] 标签""},
    ""factor_columns"":{""type"":""array"",""minItems"":1,""items"":{
      ""type"":""object"",""properties"":{
        ""column"":{""type"":""string"",""description"":""文件里的因子列名(与表头完全一致)""},
        ""bind_parameter_id"":{""type"":""string"",""description"":""要在本平台执行时绑定的流程参数ID(list_factor_candidates);纯历史导入可省;τ/eq 派生因子不要绑""}},
      ""required"":[""column""]}},
    ""response_columns"":{""type"":""array"",""minItems"":1,""items"":{
      ""type"":""object"",""properties"":{
        ""column"":{""type"":""string"",""description"":""文件里的响应列名(与表头完全一致)""},
        ""unit"":{""type"":""string""}},
      ""required"":[""column""]}},
    ""transform"":{""type"":""object"",""description"":""化学家坐标系配置(可选,与 preview_doe_design 同构):文件里的 τ/eq 列要在本平台反算执行时使用""}
  },
  ""required"":[""file_token"",""batch_name"",""factor_columns"",""response_columns""]
}";
        public bool RequiresConfirmation => false;
        public string DescribeAction(JsonElement args) => "生成实验数据导入预览";

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args)
        {
            var table = ImportFileStore.Get(AgentToolContext.GetString(args, "file_token"));
            if (table == null)
                return AgentToolResult.Fail("file_token 无效或已过期(30 分钟),请重新调用 inspect_experiment_file 读取文件。");

            string batchName = AgentToolContext.GetString(args, "batch_name");
            if (string.IsNullOrWhiteSpace(batchName))
                return AgentToolResult.Fail("batch_name 必填,且必须是用户确认过的名称。");
            try
            {
                if (await _ctx.Resolve<IDOERepository>().ProjectNameExistsAsync(batchName).ConfigureAwait(false))
                    return AgentToolResult.Fail($"项目名「{batchName}」已存在(不允许重复),请让用户换一个名称。");
            }
            catch { }

            // ── 列映射解析 ──
            var factorCols = ReadColumnList(args, "factor_columns", out var factorBinds, out var fcErr);
            if (fcErr != null) return AgentToolResult.Fail(fcErr);
            var respCols = ReadColumnList(args, "response_columns", out _, out var rcErr);
            if (rcErr != null) return AgentToolResult.Fail(rcErr);
            var respUnits = ReadUnits(args);
            if (factorCols == null || factorCols.Count == 0) return AgentToolResult.Fail("factor_columns 至少要 1 列。");
            if (respCols == null || respCols.Count == 0) return AgentToolResult.Fail("response_columns 至少要 1 列。");

            foreach (var c in factorCols.Concat(respCols))
                if (!table.Headers.Contains(c))
                    return AgentToolResult.Fail($"列「{c}」不在文件表头里。现有列:{string.Join("、", table.Headers)}");
            var overlap = factorCols.Intersect(respCols).ToList();
            if (overlap.Count > 0)
                return AgentToolResult.Fail($"列 {string.Join("、", overlap)} 同时被指定为因子和响应,请修正映射。");
            if (factorCols.Distinct().Count() != factorCols.Count || respCols.Distinct().Count() != respCols.Count)
                return AgentToolResult.Fail("factor_columns / response_columns 里存在重复列,请修正。");

            List<FactorCandidate> candidates = new();
            try { candidates = _ctx.Resolve<IFlowParameterProvider>().GetFactorCandidates() ?? new(); }
            catch { }

            int ColIdx(string name) => table.Headers.IndexOf(name);

            // ── 逐行读取:因子必须全数值;响应列全填=已完成行,全空=待执行行,半填=拒绝 ──
            var matrix = new DOEDesignMatrix { FactorNames = factorCols.ToList() };
            var rowResponses = new List<Dictionary<string, double>>();
            var badRows = new List<string>();
            int completedRows = 0, pendingRows = 0;
            for (int r = 0; r < table.Rows.Count; r++)
            {
                // 报错指回文件里的物理行号:全空行被解析器跳过后,数据行序号和文件行号会错开
                int fileLine = (table.RowLines != null && r < table.RowLines.Count) ? table.RowLines[r] : r + 2;
                var raw = table.Rows[r];
                bool rowBad = false;
                var frow = new Dictionary<string, object>();
                foreach (var fc in factorCols)
                {
                    string cell = raw[ColIdx(fc)];
                    if (!InspectExperimentFileTool.TryParseCell(cell, out double v))
                    {
                        badRows.Add($"文件第 {fileLine} 行因子「{fc}」值「{cell}」不是有效数值");
                        rowBad = true;
                        break;
                    }
                    frow[fc] = v;
                }

                if (!rowBad)
                {
                    int filled = 0;
                    var resp = new Dictionary<string, double>();
                    foreach (var rc in respCols)
                    {
                        string cell = raw[ColIdx(rc)];
                        if (string.IsNullOrEmpty(cell)) continue;
                        if (!InspectExperimentFileTool.TryParseCell(cell, out double v))
                        {
                            badRows.Add($"文件第 {fileLine} 行响应「{rc}」值「{cell}」不是有效数值");
                            rowBad = true;
                            break;
                        }
                        resp[rc] = v;
                        filled++;
                    }
                    if (!rowBad && filled > 0 && filled < respCols.Count)
                    {
                        badRows.Add($"文件第 {fileLine} 行响应只填了 {filled}/{respCols.Count} 列(说不清做没做完),请补全或清空该行响应");
                        rowBad = true;
                    }
                    if (!rowBad)
                    {
                        matrix.Rows.Add(frow);
                        rowResponses.Add(filled == respCols.Count ? resp : null);
                        if (filled == respCols.Count) completedRows++; else pendingRows++;
                    }
                }

                if (rowBad && badRows.Count >= 6) break;
            }
            if (badRows.Count > 0)
                return AgentToolResult.Fail("数据行存在问题,未生成预览:\n" + string.Join("\n", badRows.Take(6)) +
                    (badRows.Count >= 6 ? "\n(仅列出前几处,请全文件核查)" : "") +
                    "\n请让用户修正文件后重新 inspect_experiment_file。");
            if (matrix.Rows.Count == 0)
                return AgentToolResult.Fail("没有可导入的有效数据行。");

            // ── 因子表:范围取自数据,绑定按映射 ──
            var factors = new List<DOEFactor>();
            var bindingReport = new List<string>();
            int boundCount = 0;
            for (int i = 0; i < factorCols.Count; i++)
            {
                string fc = factorCols[i];
                var vals = matrix.Rows.Select(row => Convert.ToDouble(row[fc])).ToList();
                // 手工设计通道(DoeArgs.ParseFactors)强制上下限严格递增,导入通道必须守同一条线:
                // 零宽度范围的因子在回归里不可估计,下一轮设计还会退化成同一水平
                if (vals.Min() >= vals.Max())
                    return AgentToolResult.Fail(
                        $"因子「{fc}」在所有有效数据行中取值恒定({vals[0]:0.####}),不能作为 DOE 因子。" +
                        "请把该列从 factor_columns 中移除、当作固定实验条件处理;若确要研究该因子,请补充含不同水平的数据后重新导入。");
                var f = new DOEFactor
                {
                    FactorName = fc,
                    FactorType = DOEFactorType.Continuous,
                    LowerBound = vals.Min(),
                    UpperBound = vals.Max(),
                    LevelCount = Math.Max(2, vals.Distinct().Count()),
                    SortOrder = i
                };
                string bind = factorBinds.TryGetValue(fc, out var b) ? b : null;
                if (!string.IsNullOrWhiteSpace(bind))
                {
                    var cand = candidates.FirstOrDefault(x => x.ParameterId == bind);
                    if (cand == null)
                        return AgentToolResult.Fail($"因子「{fc}」的 bind_parameter_id({bind})在当前流程中不存在,请用 list_factor_candidates 核对。");
                    f.FactorSource = FactorSourceType.ParameterOverride;
                    f.SourceNodeId = cand.NodeId;
                    f.SourceParamName = cand.ParameterName;
                    boundCount++;
                    bindingReport.Add($"{fc}:已绑定 {cand.DisplayName}");
                }
                else bindingReport.Add($"{fc}:未绑定(历史导入不需要;要执行需绑定或走 transform)");
                factors.Add(f);
            }

            // ── 化学家坐标系(可选):τ/eq 列在本平台执行时反算泵速 ──
            string terr = DoeArgs.ParseTransform(args, candidates, factors, out var transformCfg);
            if (terr != null) return AgentToolResult.Fail(terr);
            string ssLocateNote = null;
            if (transformCfg?.SteadyState != null && transformCfg.SteadyState.Enabled)
            {
                string ssErr = DoeArgs.ResolveSteadyStateNode(_ctx, transformCfg.SteadyState, out ssLocateNote);
                if (ssErr != null) return AgentToolResult.Fail(ssErr);
            }

            // 有待执行行且带 transform 时,逐行反算校验(与常规预览同一道闸;纯历史导入不必卡量程)
            string dualNote = null;
            if (transformCfg != null && pendingRows > 0)
            {
                var pendMatrix = new DOEDesignMatrix { FactorNames = matrix.FactorNames };
                for (int i = 0; i < matrix.Rows.Count; i++)
                    if (rowResponses[i] == null) pendMatrix.Rows.Add(matrix.Rows[i]);
                DoeArgs.DualSpaceMatrixToMarkdown(pendMatrix, transformCfg, out var violations, maxRows: 1);
                if (violations.Count > 0)
                    return AgentToolResult.Fail(
                        $"待执行行反算泵速有 {violations.Count} 处超量程/无解:\n" + string.Join("\n", violations.Take(5)) +
                        "\n请与用户确认这些行是否仍要执行,或调整常数/量程。");
                dualNote = $"待执行的 {pendingRows} 行已通过泵速反算与量程校验。";
            }

            var responses = respCols.Select((rc, i) => new DOEResponse
            {
                ResponseName = rc,
                Unit = respUnits.TryGetValue(rc, out var u) ? u : "",
                SortOrder = i
            }).ToList();

            string previewId = DoePreviewStore.Put(new DoePreviewStore.Entry
            {
                BatchName = batchName,
                Method = DOEDesignMethod.Custom,
                Factors = factors,
                Responses = responses,
                BindingReport = bindingReport,
                BoundCount = boundCount,
                Matrix = matrix,
                TransformConfigJson = transformCfg != null ? FactorTransform.ToJson(transformCfg) : null,
                RowResponses = rowResponses
            });

            var sb = new StringBuilder();
            sb.AppendLine($"导入预览已生成(preview_id:{previewId},尚未入库):");
            sb.AppendLine($"批次「{batchName}」:{factors.Count} 因子 × {responses.Count} 响应,共 {matrix.Rows.Count} 行——" +
                          $"已完成 {completedRows} 行(导入为历史数据),待执行 {pendingRows} 行" +
                          (pendingRows > 0 ? "(入库后可接着在本平台执行)" : "") + "。");
            sb.AppendLine("绑定:" + string.Join(";", bindingReport));
            if (dualNote != null) sb.AppendLine(dualNote);
            if (ssLocateNote != null) sb.AppendLine(ssLocateNote);
            sb.AppendLine();
            sb.AppendLine("| 因子 | 数据范围 | 水平数 |");
            sb.AppendLine("| --- | --- | --- |");
            foreach (var f in factors)
                sb.AppendLine($"| {f.FactorName} | {f.LowerBound:0.####} ~ {f.UpperBound:0.####} | {f.LevelCount} |");
            sb.AppendLine();
            sb.Append($"请把统计与因子表展示给用户,确认两件事:①项目名称拟用「{batchName}」是否认可;②是否入库。" +
                      $"都确认后调用 create_doe_batch 并传 preview_id={previewId}(带结果的行入库即标记为导入来源、可直接分析)。");
            return AgentToolResult.Ok(sb.ToString());
        }

        private static List<string> ReadColumnList(JsonElement args, string key,
            out Dictionary<string, string> binds, out string error)
        {
            binds = new Dictionary<string, string>();
            error = null;
            if (!args.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            var cols = new List<string>();
            int idx = 0;
            foreach (var el in arr.EnumerateArray())
            {
                idx++;
                // 格式坏的映射项不能静默跳过:少映射一列,该列数据会无声消失,
                // 少映射一个响应列还会把「只填了这列的已完成行」错分类成待执行
                string col = el.ValueKind == JsonValueKind.Object ? AgentToolContext.GetString(el, "column") : null;
                if (string.IsNullOrWhiteSpace(col))
                {
                    error = $"{key} 第 {idx} 项缺少非空的 column 字段(收到:{el.GetRawText()}),请修正列映射后重试。";
                    return null;
                }
                cols.Add(col.Trim());
                string bind = AgentToolContext.GetString(el, "bind_parameter_id");
                if (!string.IsNullOrWhiteSpace(bind)) binds[col.Trim()] = bind.Trim();
            }
            return cols;
        }

        private static Dictionary<string, string> ReadUnits(JsonElement args)
        {
            var units = new Dictionary<string, string>();
            if (args.TryGetProperty("response_columns", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var el in arr.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    string col = AgentToolContext.GetString(el, "column");
                    string unit = AgentToolContext.GetString(el, "unit");
                    if (!string.IsNullOrWhiteSpace(col) && !string.IsNullOrWhiteSpace(unit))
                        units[col.Trim()] = unit.Trim();
                }
            return units;
        }
    }
}
