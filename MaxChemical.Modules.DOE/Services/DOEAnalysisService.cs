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
    /// DOE 数据分析服务 — 调用 Python doe_engine.py 的 DOEAnalyzer 和 DOESurfacePlotter。
    /// 注意: 所有 Python 调用不能放在 Task.Run 中，否则 GIL 死锁。
    /// </summary>
    public class DOEAnalysisService : IDOEAnalysisService
    {
        private readonly IDOERepository _repository;
        private readonly IGPRModelService _gprService;
        private readonly ILogService _logger;
        private dynamic? _analyzer;
        private dynamic? _plotter;
        private bool _isPythonInitialized;

        public DOEAnalysisService(IDOERepository repository, IGPRModelService gprService, ILogService logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _gprService = gprService ?? throw new ArgumentNullException(nameof(gprService));
            _logger = logger?.ForContext<DOEAnalysisService>() ?? throw new ArgumentNullException(nameof(logger));
        }

        private void EnsureReady()
        {
            if (_isPythonInitialized && _analyzer != null) return;

            try
            {
                var envManager = MaxChemical.Modules.Designer.Services.PythonEnvironmentManager.Instance;
                if (!envManager.IsInitialized && !envManager.Initialize())
                    throw new InvalidOperationException("Python 环境初始化失败");

                using (Py.GIL())
                {
                    dynamic doeEngine = Py.Import("doe_engine");
                    _analyzer = doeEngine.create_analyzer();
                    _plotter = doeEngine.create_surface_plotter();
                }

                _isPythonInitialized = true;
                _logger.LogInformation("DOE 分析服务初始化成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DOE 分析服务初始化失败");
                throw;
            }
        }

        /// <summary>
        /// 加载批次数据到 Python 分析器
        /// </summary>
        private async Task LoadBatchDataAsync(string batchId)
        {
            var batch = await _repository.GetBatchWithDetailsAsync(batchId);
            if (batch == null) throw new InvalidOperationException($"未找到批次: {batchId}");

            var completedRuns = batch.Runs
                .Where(r => r.Status == DOERunStatus.Completed)
                .OrderBy(r => r.RunIndex)
                .ToList();

            if (completedRuns.Count == 0)
                throw new InvalidOperationException("批次没有已完成的实验数据");

            var primaryResponse = batch.Responses.FirstOrDefault()?.ResponseName ?? "Response";

            var factorsList = completedRuns
                .Select(r => JsonConvert.DeserializeObject<Dictionary<string, double>>(r.FactorValuesJson) ?? new())
                .ToList();

            var responsesList = completedRuns
                .Select(r =>
                {
                    var dict = JsonConvert.DeserializeObject<Dictionary<string, double>>(r.ResponseValuesJson ?? "{}");
                    return dict != null && dict.TryGetValue(primaryResponse, out var val) ? val : 0.0;
                })
                .ToList();

            var factorsJson = JsonConvert.SerializeObject(factorsList);
            var responsesJson = JsonConvert.SerializeObject(responsesList);

            using (Py.GIL())
            {
                string loadResult = _analyzer!.load_data(factorsJson, responsesJson, primaryResponse).ToString();
                _logger.LogDebug("分析数据加载: {Result}", loadResult);

                var boundsJson = JsonConvert.SerializeObject(
                    batch.Factors.ToDictionary(f => f.FactorName, f => new[] { f.LowerBound, f.UpperBound }));
                _plotter!.set_analyzer(_analyzer, boundsJson);
            }

            _logger.LogInformation("分析数据加载完成: {BatchId}, {Count} 组", batchId, completedRuns.Count);
        }

        // ══════════════ 统计图表（全部去掉 Task.Run）══════════════

        public async Task<string> GetMainEffectsAsync(string batchId)
        {
            EnsureReady();
            await LoadBatchDataAsync(batchId);
            using (Py.GIL()) { return _analyzer!.main_effects().ToString(); }
        }

        public async Task<string> GetInteractionEffectsAsync(string batchId)
        {
            EnsureReady();
            await LoadBatchDataAsync(batchId);
            using (Py.GIL()) { return _analyzer!.interaction_effects().ToString(); }
        }

        public async Task<string> GetParetoChartAsync(string batchId)
        {
            EnsureReady();
            await LoadBatchDataAsync(batchId);
            using (Py.GIL()) { return _analyzer!.pareto_chart().ToString(); }
        }

        public async Task<string> GetAnovaTableAsync(string batchId)
        {
            EnsureReady();
            await LoadBatchDataAsync(batchId);
            using (Py.GIL()) { return _analyzer!.anova_table().ToString(); }
        }

        public async Task<string> GetRegressionSummaryAsync(string batchId)
        {
            EnsureReady();
            await LoadBatchDataAsync(batchId);
            using (Py.GIL()) { return _analyzer!.regression_summary().ToString(); }
        }

        public async Task<string> GetResidualAnalysisAsync(string batchId)
        {
            EnsureReady();
            await LoadBatchDataAsync(batchId);
            using (Py.GIL()) { return _analyzer!.residual_analysis().ToString(); }
        }

        public async Task<string> GetActualVsPredictedAsync(string batchId)
        {
            EnsureReady();
            await LoadBatchDataAsync(batchId);
            using (Py.GIL()) { return _analyzer!.actual_vs_predicted().ToString(); }
        }

        // ══════════════ 响应曲面 ══════════════

        public async Task<string> GetResponseSurfaceDataAsync(string batchId, string factor1, string factor2)
        {
            EnsureReady();
            await LoadBatchDataAsync(batchId);
            using (Py.GIL()) { return _plotter!.response_surface_data(factor1, factor2, 30).ToString(); }
        }

        public async Task<string> GetContourDataAsync(string batchId, string factor1, string factor2)
        {
            EnsureReady();
            await LoadBatchDataAsync(batchId);
            using (Py.GIL()) { return _plotter!.contour_data(factor1, factor2, 30).ToString(); }
        }

        public async Task<byte[]> GetResponseSurfaceImageAsync(string batchId, string factor1, string factor2)
        {
            EnsureReady();
            await LoadBatchDataAsync(batchId);

            using (Py.GIL())
            {
                string base64 = _plotter!.response_surface_image(factor1, factor2, 50, 800, 600).ToString();
                return string.IsNullOrEmpty(base64) ? Array.Empty<byte>() : Convert.FromBase64String(base64);
            }
        }

        // ══════════════ GPR 特有图表 ══════════════

        public Task<string> GetGPRConfidenceBandAsync(string flowId)
        {
            return Task.FromResult("{}");
        }

        public Task<string> GetAcquisitionSurfaceAsync(string flowId, string factor1, string factor2)
        {
            return Task.FromResult("{}");
        }

        public async Task<string> GetModelEvolutionAsync(string flowId)
        {
            try
            {
                var modelState = await _repository.GetGPRModelStateAsync(flowId);
                return modelState?.EvolutionHistoryJson ?? "[]";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取模型演进数据失败");
                return "[]";
            }
        }

        /// <summary>
        /// 用 GPR 模型直接生成响应曲面图（不依赖批次）。
        /// 走 DOESurfacePlotter.set_gpr_model() 路径。
        /// </summary>
        public async Task<byte[]> GetResponseSurfaceImageFromGPRAsync(string factor1, string factor2)
        {
            EnsureReady();

            var gprModel = _gprService.GetPythonModel();
            if (gprModel == null)
                return Array.Empty<byte>();

            using (Py.GIL())
            {
                // 从 GPR 模型自身获取因子名和范围
                string factorNamesJson = gprModel.get_factor_names().ToString();
                string boundsJson = gprModel.get_bounds().ToString();

                // 用 set_gpr_model 路径，不需要 analyzer
                _plotter!.set_gpr_model(gprModel, factorNamesJson, boundsJson);

                string base64 = _plotter.response_surface_image(factor1, factor2, 50, 800, 600).ToString();
                return string.IsNullOrEmpty(base64) ? Array.Empty<byte>() : Convert.FromBase64String(base64);
            }
        }

    }
}