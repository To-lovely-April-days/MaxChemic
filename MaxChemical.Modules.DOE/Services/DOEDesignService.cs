using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MaxChemical.Logging;
using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.Services.Designs;
using Newtonsoft.Json;
using OfficeOpenXml;

namespace MaxChemical.Modules.DOE.Services
{
    /// <summary>
    /// DOE 实验设计服务 —— 纯 C# 原生实现,已移除所有 Python 调用。
    ///
    /// 所有设计方法通过 Services/Designs/ 下的 IDesignGenerator 实现。
    /// 设计质量评估通过 DesignQualityEvaluator (MathNet.Numerics)。
    /// Excel 导入通过 EPPlus。
    ///
    /// ★ 重写说明:
    ///   - 旧的 Python 调用 (_pythonDesigner / _pythonImporter / Py.GIL) 全部删除
    ///   - 旧的接口签名全部保留, 内部转发到新的 generator
    ///   - 新增 GenerateAsync(factors, options) 统一入口
    /// </summary>
    public partial class DOEDesignService : IDOEDesignService
    {
        private readonly IDOERepository _repository;
        private readonly ILogService _logger;

        // 各个生成器 (单例, 无状态)
        private readonly FullFactorialGenerator _fullFactorial = new();
        private readonly FractionalFactorialGenerator _fractional = new();
        private readonly PlackettBurmanGenerator _plackettBurman = new();
        private readonly CCDGenerator _ccd = new();
        private readonly BoxBehnkenGenerator _boxBehnken = new();
        private readonly DOptimalGenerator _dOptimal = new();
        private readonly TaguchiGenerator _taguchi = new();

        public DOEDesignService(IDOERepository repository, ILogService logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _logger = logger?.ForContext<DOEDesignService>() ?? throw new ArgumentNullException(nameof(logger));
        }

        // ══════════════════════════════════════════════════════
        // 统一入口 (新)
        // ══════════════════════════════════════════════════════

        public Task<DOEDesignMatrix> GenerateAsync(List<DOEFactor> factors, DesignOptions options)
        {
            _logger.LogInformation("生成设计: {Type}, {FactorCount} 个因子",
                options.GetType().Name, factors.Count);

            DOEDesignMatrix matrix = options switch
            {
                FullFactorialOptions f => _fullFactorial.Generate(factors, f),
                FractionalFactorialOptions f => _fractional.Generate(factors, f),
                PlackettBurmanOptions f => _plackettBurman.Generate(factors, f),
                CCDOptions f => _ccd.Generate(factors, f),
                BoxBehnkenOptions f => _boxBehnken.Generate(factors, f),
                DOptimalOptions f => _dOptimal.Generate(factors, f),
                TaguchiOptions f => _taguchi.Generate(factors, f),
                _ => throw new ArgumentException($"不支持的选项类型: {options.GetType().Name}")
            };

            _logger.LogInformation("设计生成完成: {RunCount} 组实验", matrix.RunCount);
            return Task.FromResult(matrix);
        }

        // ══════════════════════════════════════════════════════
        // 旧签名 (保留向后兼容, 内部转发到新 generator)
        // ══════════════════════════════════════════════════════

        public Task<DOEDesignMatrix> GenerateFullFactorialAsync(List<DOEFactor> factors)
        {
            var opts = new FullFactorialOptions
            {
                LevelCount = factors.FirstOrDefault(f => f.IsContinuous)?.LevelCount ?? 2,
                CenterPointCount = 0
            };
            return GenerateAsync(factors, opts);
        }

        public Task<DOEDesignMatrix> GenerateFractionalFactorialAsync(List<DOEFactor> factors, int resolution)
        {
            // 从分辨度推断合适的 runs: 选最小满足该分辨度的 runs
            int k = factors.Count;
            var entries = FractionalFactorialTables.GetFeasibleRuns(k);
            int chosen = entries
                .Where(e => e.resolution >= resolution || e.resolution == 99)
                .OrderBy(e => e.runs)
                .FirstOrDefault().runs;

            if (chosen == 0)
                throw new ArgumentException($"k={k} 无法满足 Res {resolution}");

            var opts = new FractionalFactorialOptions
            {
                RunCount = chosen,
                CenterPointCount = 0
            };
            return GenerateAsync(factors, opts);
        }

        public Task<DOEDesignMatrix> GeneratePlackettBurmanAsync(List<DOEFactor> factors)
        {
            var opts = new PlackettBurmanOptions
            {
                RunCount = PlackettBurmanTables.RecommendRunCount(factors.Count),
                CenterPointCount = 0
            };
            return GenerateAsync(factors, opts);
        }

        public Task<DOEDesignMatrix> GenerateCCDAsync(List<DOEFactor> factors, string alphaType = "rotatable", int centerCount = -1)
        {
            var opts = new CCDOptions
            {
                AlphaType = alphaType,
                CubeCenterCount = centerCount
            };
            return GenerateAsync(factors, opts);
        }

        public Task<DOEDesignMatrix> GenerateBoxBehnkenAsync(List<DOEFactor> factors, int centerCount = -1)
        {
            var opts = new BoxBehnkenOptions
            {
                BbdCenterCount = centerCount
            };
            return GenerateAsync(factors, opts);
        }

        public Task<DOEDesignMatrix> GenerateDOptimalAsync(List<DOEFactor> factors, int numRuns = -1, string modelType = "quadratic")
        {
            int n = numRuns;
            if (n <= 0)
            {
                // 默认: quadratic 模型参数数 + 4
                int k = factors.Count;
                n = 1 + 2 * k + k * (k - 1) / 2 + 4;
            }
            var opts = new DOptimalOptions
            {
                RunCount = n,
                ModelType = modelType,
                Restarts = 20,
                MaxIterations = 100
            };
            return GenerateAsync(factors, opts);
        }

        public Task<DOEDesignMatrix> GenerateTaguchiAsync(List<DOEFactor> factors, string tableType)
        {
            var opts = new TaguchiOptions
            {
                LArray = tableType ?? "auto"
            };
            return GenerateAsync(factors, opts);
        }

        // ══════════════════════════════════════════════════════
        // 设计质量评估 (C# 实现)
        // ══════════════════════════════════════════════════════

        public Task<DOEDesignQuality> GetDesignQualityAsync(
            List<DOEFactor> factors, DOEDesignMatrix matrix, string modelType = "quadratic")
        {
            _logger.LogInformation("评估设计质量: {FactorCount} 因子, {RunCount} 组, 模型={Model}",
                factors.Count, matrix.RunCount, modelType);
            var quality = DesignQualityEvaluator.Evaluate(factors, matrix, modelType);
            _logger.LogInformation("设计质量: D-eff={DEff:F4}, A-eff={AEff:F4}, G-eff={GEff:F4}",
                quality.DEfficiency, quality.AEfficiency, quality.GEfficiency);
            return Task.FromResult(quality);
        }

        // ══════════════════════════════════════════════════════
        // Excel 导入 (EPPlus 实现, 替代 Python import_excel)
        // ══════════════════════════════════════════════════════

        public Task<DOEDesignMatrix> ImportCustomMatrixAsync(string excelPath, List<DOEFactor> factors)
        {
            _logger.LogInformation("导入自定义参数矩阵: {Path}", excelPath);
            var rows = ReadExcelRows(excelPath, factors.Select(f => f.FactorName).ToList(),
                                     new List<string>(), out _);

            var categoricalNames = new HashSet<string>(factors.Where(f => f.IsCategorical).Select(f => f.FactorName));
            var normalized = rows.Select(r =>
            {
                var dict = new Dictionary<string, object>();
                foreach (var kv in r)
                {
                    if (categoricalNames.Contains(kv.Key))
                        dict[kv.Key] = kv.Value?.ToString() ?? "";
                    else if (kv.Value is double d) dict[kv.Key] = d;
                    else if (double.TryParse(kv.Value?.ToString(), out var parsed)) dict[kv.Key] = parsed;
                    else dict[kv.Key] = 0.0;
                }
                return dict;
            }).ToList();

            var matrix = new DOEDesignMatrix
            {
                FactorNames = factors.Select(f => f.FactorName).ToList(),
                Rows = normalized
            };
            return Task.FromResult(matrix);
        }

        public Task<DataValidationResult> ValidateImportedDataAsync(
            string excelPath, List<DOEFactor> factors, List<DOEResponse> responses)
        {
            var result = new DataValidationResult { IsValid = true };
            try
            {
                var rows = ReadExcelRows(
                    excelPath,
                    factors.Select(f => f.FactorName).ToList(),
                    responses.Select(r => r.ResponseName).ToList(),
                    out var warnings);
                result.ValidRowCount = rows.Count;
                result.Warnings = warnings;
                if (rows.Count == 0)
                {
                    result.IsValid = false;
                    result.Errors.Add("Excel 中未找到有效数据行");
                }
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.Errors.Add($"读取 Excel 失败: {ex.Message}");
            }
            return Task.FromResult(result);
        }

        public Task<List<DOERunRecord>> ImportHistoricalDataAsync(
            string excelPath, string batchId, List<DOEFactor> factors, List<DOEResponse> responses)
        {
            var factorNames = factors.Select(f => f.FactorName).ToList();
            var responseNames = responses.Select(r => r.ResponseName).ToList();
            var rows = ReadExcelRows(excelPath, factorNames, responseNames, out _);

            var categoricalNames = new HashSet<string>(factors.Where(f => f.IsCategorical).Select(f => f.FactorName));

            var runs = rows.Select((r, idx) =>
            {
                var factorsDict = new Dictionary<string, object>();
                var responsesDict = new Dictionary<string, double>();
                foreach (var n in factorNames)
                {
                    if (!r.TryGetValue(n, out var v)) continue;
                    if (categoricalNames.Contains(n))
                        factorsDict[n] = v?.ToString() ?? "";
                    else if (v is double d) factorsDict[n] = d;
                    else if (double.TryParse(v?.ToString(), out var p)) factorsDict[n] = p;
                }
                foreach (var n in responseNames)
                {
                    if (r.TryGetValue(n, out var v) && v is double d) responsesDict[n] = d;
                    else if (r.TryGetValue(n, out var v2) && double.TryParse(v2?.ToString(), out var p)) responsesDict[n] = p;
                }

                return new DOERunRecord
                {
                    BatchId = batchId,
                    RunIndex = 10000 + idx,
                    FactorValuesJson = JsonConvert.SerializeObject(factorsDict),
                    ResponseValuesJson = JsonConvert.SerializeObject(responsesDict),
                    DataSource = DOEDataSource.Imported,
                    Status = DOERunStatus.Completed,
                    StartTime = DateTime.Now,
                    EndTime = DateTime.Now
                };
            }).ToList();
            return Task.FromResult(runs);
        }

        // ══════════════════════════════════════════════════════
        // Excel 读取辅助 (EPPlus)
        // ══════════════════════════════════════════════════════

        private List<Dictionary<string, object>> ReadExcelRows(
            string path, List<string> factorNames, List<string> responseNames,
            out List<string> warnings)
        {
            warnings = new List<string>();
            var result = new List<Dictionary<string, object>>();
            if (!File.Exists(path))
                throw new FileNotFoundException("Excel 文件不存在", path);

            ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;
            using var package = new ExcelPackage(new FileInfo(path));
            var ws = package.Workbook.Worksheets.FirstOrDefault();
            if (ws == null || ws.Dimension == null) return result;

            int nRows = ws.Dimension.End.Row;
            int nCols = ws.Dimension.End.Column;

            // 第一行为列头
            var header = new Dictionary<string, int>();
            for (int c = 1; c <= nCols; c++)
            {
                var name = ws.Cells[1, c].Text?.Trim() ?? "";
                if (!string.IsNullOrEmpty(name)) header[name] = c;
            }

            // 检查缺失列
            var allNames = factorNames.Concat(responseNames).ToList();
            foreach (var n in allNames)
                if (!header.ContainsKey(n))
                    warnings.Add($"列 '{n}' 在 Excel 中未找到");

            for (int r = 2; r <= nRows; r++)
            {
                var row = new Dictionary<string, object>();
                bool hasAny = false;
                foreach (var n in allNames)
                {
                    if (!header.TryGetValue(n, out var col)) continue;
                    var cell = ws.Cells[r, col];
                    var raw = cell.Value;
                    if (raw == null) continue;
                    if (raw is double d) row[n] = d;
                    else if (double.TryParse(raw.ToString(), out var p)) row[n] = p;
                    else row[n] = raw.ToString() ?? "";
                    hasAny = true;
                }
                if (hasAny) result.Add(row);
            }
            return result;
        }
    }
}
