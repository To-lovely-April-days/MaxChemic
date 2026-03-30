using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MaxChemical.Logging;
using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Models;
using Newtonsoft.Json;
using Python.Runtime;

namespace MaxChemical.Modules.DOE.Services
{
    /// <summary>
    /// DOE 实验设计服务 — 调用 Python doe_engine.py 生成参数矩阵
    /// 注意: pythonnet GIL 必须在主线程获取，不能放在 Task.Run 中
    /// </summary>
    public class DOEDesignService : IDOEDesignService
    {
        private readonly IDOERepository _repository;
        private readonly ILogService _logger;
        private dynamic? _pythonDesigner;
        private dynamic? _pythonImporter;
        private bool _isPythonInitialized;

        public DOEDesignService(IDOERepository repository, ILogService logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _logger = logger?.ForContext<DOEDesignService>() ?? throw new ArgumentNullException(nameof(logger));
            // 不在构造函数初始化 Python，延迟到首次调用时
        }

        private void EnsurePythonReady()
        {
            if (_isPythonInitialized && _pythonDesigner != null) return;

            try
            {
                var envManager = MaxChemical.Modules.Designer.Services.PythonEnvironmentManager.Instance;
                if (!envManager.IsInitialized)
                {
                    var success = envManager.Initialize();
                    if (!success)
                        throw new InvalidOperationException("Python 环境初始化失败");
                }

                using (Py.GIL())
                {
                    dynamic doeEngine = Py.Import("doe_engine");
                    _pythonDesigner = doeEngine.create_designer();
                    _pythonImporter = doeEngine.create_importer();
                }

                _isPythonInitialized = true;
                _logger.LogInformation("DOE 设计服务 Python 模块初始化成功");
            }
            catch (Exception ex)
            {
                _isPythonInitialized = false;
                _logger.LogError(ex, "DOE 设计服务 Python 模块初始化失败");
                throw;
            }
        }

        // ══════════════ 参数矩阵生成 ══════════════

        public Task<DOEDesignMatrix> GenerateFullFactorialAsync(List<DOEFactor> factors)
        {
            EnsurePythonReady();

            var factorsJson = BuildFactorsJsonWithLevels(factors);
            _logger.LogInformation("生成全因子设计: {FactorCount} 个因子", factors.Count);

            using (Py.GIL())
            {
                string matrixJson = _pythonDesigner!.full_factorial(factorsJson).ToString();
                var result = ParseDesignMatrix(matrixJson, factors);
                return Task.FromResult(result);
            }
        }

        public Task<DOEDesignMatrix> GenerateFractionalFactorialAsync(List<DOEFactor> factors, int resolution)
        {
            EnsurePythonReady();

            var factorsJson = BuildFactorsJsonWithBounds(factors);
            _logger.LogInformation("生成部分因子设计: {FactorCount} 个因子, 分辨度 {Resolution}", factors.Count, resolution);

            using (Py.GIL())
            {
                string matrixJson = _pythonDesigner!.fractional_factorial(factorsJson, resolution).ToString();
                var result = ParseDesignMatrix(matrixJson, factors);
                return Task.FromResult(result);
            }
        }

        public Task<DOEDesignMatrix> GenerateTaguchiAsync(List<DOEFactor> factors, string tableType)
        {
            EnsurePythonReady();

            var factorsJson = BuildFactorsJsonWithLevels(factors);
            _logger.LogInformation("生成正交设计: {FactorCount} 个因子, 表类型 {TableType}", factors.Count, tableType);

            using (Py.GIL())
            {
                string matrixJson = _pythonDesigner!.taguchi(factorsJson, tableType).ToString();
                var result = ParseDesignMatrix(matrixJson, factors);
                return Task.FromResult(result);
            }
        }

        public Task<DOEDesignMatrix> ImportCustomMatrixAsync(string excelPath, List<DOEFactor> factors)
        {
            EnsurePythonReady();

            var factorNames = factors.Select(f => f.FactorName).ToList();
            var factorNamesJson = JsonConvert.SerializeObject(factorNames);
            var emptyResponsesJson = "[]";

            _logger.LogInformation("导入自定义参数矩阵: {Path}", excelPath);

            using (Py.GIL())
            {
                string dataJson = _pythonImporter!.import_excel(excelPath, factorNamesJson, emptyResponsesJson).ToString();
                var records = JsonConvert.DeserializeObject<List<ImportedRecord>>(dataJson) ?? new();

                var matrix = new DOEDesignMatrix
                {
                    FactorNames = factorNames,
                    Rows = records.Select(r => r.Factors).ToList()
                };
                return Task.FromResult(matrix);
            }
        }

        // ══════════════ 历史数据校验与导入 ══════════════

        public Task<DataValidationResult> ValidateImportedDataAsync(
            string excelPath, List<DOEFactor> factors, List<DOEResponse> responses)
        {
            EnsurePythonReady();

            var factorNamesJson = JsonConvert.SerializeObject(factors.Select(f => f.FactorName));
            var responseNamesJson = JsonConvert.SerializeObject(responses.Select(r => r.ResponseName));

            using (Py.GIL())
            {
                string resultJson = _pythonImporter!.validate_excel(
                    excelPath, factorNamesJson, responseNamesJson).ToString();

                var pyResult = JsonConvert.DeserializeObject<PythonValidationResult>(resultJson);

                var result = new DataValidationResult
                {
                    IsValid = pyResult?.IsValid ?? false,
                    Errors = pyResult?.Errors ?? new List<string>(),
                    Warnings = pyResult?.Warnings ?? new List<string>(),
                    ValidRowCount = pyResult?.ValidRowCount ?? 0
                };
                return Task.FromResult(result);
            }
        }

        public Task<List<DOERunRecord>> ImportHistoricalDataAsync(
            string excelPath, string batchId, List<DOEFactor> factors, List<DOEResponse> responses)
        {
            EnsurePythonReady();

            var factorNamesJson = JsonConvert.SerializeObject(factors.Select(f => f.FactorName));
            var responseNamesJson = JsonConvert.SerializeObject(responses.Select(r => r.ResponseName));

            using (Py.GIL())
            {
                string dataJson = _pythonImporter!.import_excel(
                    excelPath, factorNamesJson, responseNamesJson).ToString();

                var records = JsonConvert.DeserializeObject<List<ImportedRecord>>(dataJson) ?? new();

                var existingRuns = _repository.GetRunsAsync(batchId).GetAwaiter().GetResult();
                int startIndex = existingRuns.Count > 0 ? existingRuns.Max(r => r.RunIndex) + 1 : 0;

                var runRecords = new List<DOERunRecord>();
                for (int i = 0; i < records.Count; i++)
                {
                    var rec = records[i];
                    runRecords.Add(new DOERunRecord
                    {
                        BatchId = batchId,
                        RunIndex = startIndex + i,
                        FactorValuesJson = JsonConvert.SerializeObject(rec.Factors),
                        ResponseValuesJson = JsonConvert.SerializeObject(rec.Responses),
                        DataSource = DOEDataSource.Imported,
                        Status = DOERunStatus.Completed,
                        StartTime = DateTime.Now,
                        EndTime = DateTime.Now
                    });
                }

                _logger.LogInformation("导入 {Count} 条历史数据到批次 {BatchId}", runRecords.Count, batchId);
                return Task.FromResult(runRecords);
            }
        }

        // ══════════════ 辅助方法 ══════════════

        private static string BuildFactorsJsonWithLevels(List<DOEFactor> factors)
        {
            var factorDefs = factors.Select(f => new
            {
                name = f.FactorName,
                levels = f.GetLevels().ToList()
            });
            return JsonConvert.SerializeObject(factorDefs);
        }

        private static string BuildFactorsJsonWithBounds(List<DOEFactor> factors)
        {
            var factorDefs = factors.Select(f => new
            {
                name = f.FactorName,
                lower = f.LowerBound,
                upper = f.UpperBound
            });
            return JsonConvert.SerializeObject(factorDefs);
        }

        private static DOEDesignMatrix ParseDesignMatrix(string matrixJson, List<DOEFactor> factors)
        {
            var rows = JsonConvert.DeserializeObject<List<Dictionary<string, double>>>(matrixJson)
                       ?? new List<Dictionary<string, double>>();
            return new DOEDesignMatrix
            {
                FactorNames = factors.Select(f => f.FactorName).ToList(),
                Rows = rows
            };
        }

        private class ImportedRecord
        {
            [JsonProperty("factors")]
            public Dictionary<string, double> Factors { get; set; } = new();

            [JsonProperty("responses")]
            public Dictionary<string, double> Responses { get; set; } = new();
        }

        private class PythonValidationResult
        {
            [JsonProperty("is_valid")] public bool IsValid { get; set; }
            [JsonProperty("errors")] public List<string> Errors { get; set; } = new();
            [JsonProperty("warnings")] public List<string> Warnings { get; set; } = new();
            [JsonProperty("valid_row_count")] public int ValidRowCount { get; set; }
        }
    }
}