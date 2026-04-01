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
    /// ★ 新增: Desirability 多响应优化服务
    /// 调用 Python doe_desirability.py 实现 Derringer-Suich 方法
    /// </summary>
    public class DesirabilityService : IDesirabilityService
    {
        private readonly IDOERepository _repository;
        private readonly IGPRModelService _gprService;
        private readonly ILogService _logger;
        private dynamic? _pythonEngine;
        private bool _isPythonInitialized;

        // 当前配置缓存
        private List<DesirabilityResponseConfig> _currentConfigs = new();
        private List<DOEFactor> _currentFactors = new();

        public DesirabilityService(IDOERepository repository, IGPRModelService gprService, ILogService logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _gprService = gprService ?? throw new ArgumentNullException(nameof(gprService));
            _logger = logger?.ForContext<DesirabilityService>() ?? throw new ArgumentNullException(nameof(logger));
        }

        private void EnsurePythonReady()
        {
            if (_isPythonInitialized && _pythonEngine != null) return;
            try
            {
                var envManager = MaxChemical.Modules.Designer.Services.PythonEnvironmentManager.Instance;
                if (!envManager.IsInitialized && !envManager.Initialize())
                    throw new InvalidOperationException("Python 环境初始化失败");

                using (Py.GIL())
                {
                    dynamic doeDesirability = Py.Import("doe_desirability");
                    _pythonEngine = doeDesirability.create_engine();
                }
                _isPythonInitialized = true;
                _logger.LogInformation("★ Desirability 服务初始化成功");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "★ Desirability 服务初始化失败");
                throw;
            }
        }

        /// <summary>
        /// 配置多响应优化参数并初始化 Python 引擎
        /// </summary>
        public Task ConfigureAsync(string batchId, List<DesirabilityResponseConfig> configs, List<DOEFactor> factors)
        {
            EnsurePythonReady();

            _currentConfigs = configs;
            _currentFactors = factors;

            // 构建 Python 侧配置 JSON
            var configsList = configs.Select(c => new
            {
                name = c.ResponseName,
                goal = c.Goal.ToString().ToLower(),
                lower = c.Lower,
                upper = c.Upper,
                target = c.Target,
                weight = c.Weight,
                importance = c.Importance,
                shape = c.Shape,
                shape_lower = c.ShapeLower,
                shape_upper = c.ShapeUpper
            }).ToList();

            var factorNames = factors.Select(f => f.FactorName).ToList();
            var bounds = factors.ToDictionary(f => f.FactorName, f => new[] { f.LowerBound, f.UpperBound });

            var configsJson = JsonConvert.SerializeObject(configsList);
            var factorNamesJson = JsonConvert.SerializeObject(factorNames);
            var boundsJson = JsonConvert.SerializeObject(bounds);

            using (Py.GIL())
            {
                _pythonEngine!.configure(configsJson, factorNamesJson, boundsJson);
            }

            _logger.LogInformation("★ Desirability 已配置: {Count} 个响应变量", configs.Count);
            return Task.CompletedTask;
        }

        /// <summary>
        /// 计算给定预测值的综合 Desirability
        /// </summary>
        public Task<DesirabilityResult> ComputeDesirabilityAsync(Dictionary<string, double> predictions)
        {
            EnsurePythonReady();

            var predsJson = JsonConvert.SerializeObject(predictions);

            using (Py.GIL())
            {
                string resultJson = _pythonEngine!.composite_desirability(predsJson).ToString();
                var pyResult = JsonConvert.DeserializeObject<PyDesirabilityResult>(resultJson)!;

                var result = new DesirabilityResult
                {
                    CompositeD = pyResult.CompositeD,
                    Success = true,
                    IndividualD = new()
                };

                if (pyResult.IndividualD != null)
                {
                    foreach (var kv in pyResult.IndividualD)
                    {
                        var config = _currentConfigs.FirstOrDefault(c => c.ResponseName == kv.Key);
                        result.IndividualD.Add(new IndividualDesirability
                        {
                            ResponseName = kv.Key,
                            PredictedValue = kv.Value.Value,
                            D = kv.Value.D,
                            Goal = config?.Goal ?? DesirabilityGoal.Maximize
                        });
                    }
                }

                return Task.FromResult(result);
            }
        }

        /// <summary>
        /// 搜索最优因子组合
        /// 内部将 GPR 多响应预测包装成 Python 回调函数
        /// </summary>
        public Task<DesirabilityResult> OptimizeAsync()
        {
            EnsurePythonReady();

            if (!_gprService.IsActive)
            {
                return Task.FromResult(new DesirabilityResult
                {
                    Success = false,
                    ErrorMessage = "GPR 模型未激活，无法进行多响应优化"
                });
            }

            _logger.LogInformation("★ Desirability 优化: 开始搜索最优因子组合");

            using (Py.GIL())
            {
                // 构建预测回调: factors_json → predictions_json
                // 注意: 当前 GPR 是单响应模型，多响应需要分别预测
                // 这里简化处理: 用 GPR 预测主响应，其他响应取已有数据的均值
                // TODO: 完整多响应 GPR 需要为每个响应维护独立模型

                // 使用 Python 的 optimize 方法，传入 lambda 预测函数
                Func<string, string> predictor = (factorsJson) =>
                {
                    try
                    {
                        var factors = JsonConvert.DeserializeObject<Dictionary<string, double>>(factorsJson) ?? new();
                        var gprPred = _gprService.Predict(factors);

                        // 构建多响应预测（主响应用 GPR，其他响应也尝试用 GPR）
                        var predictions = new Dictionary<string, double>();
                        foreach (var config in _currentConfigs)
                        {
                            // 简化: 所有响应都用同一个 GPR 的预测值
                            // 实际场景中应为每个响应维护独立的 GPR 模型
                            predictions[config.ResponseName] = gprPred.Mean;
                        }

                        return JsonConvert.SerializeObject(predictions);
                    }
                    catch
                    {
                        var fallback = _currentConfigs.ToDictionary(c => c.ResponseName, c => (c.Lower + c.Upper) / 2.0);
                        return JsonConvert.SerializeObject(fallback);
                    }
                };

                // 将 C# 委托转为 Python callable
                dynamic pyPredictor = predictor.ToPython();
                string resultJson = _pythonEngine!.optimize(pyPredictor).ToString();

                var pyResult = JsonConvert.DeserializeObject<PyOptimizeResult>(resultJson)!;

                var result = new DesirabilityResult
                {
                    OptimalFactors = pyResult.OptimalFactors ?? new(),
                    CompositeD = pyResult.CompositeD,
                    Success = pyResult.Success,
                    ErrorMessage = pyResult.Error,
                    IndividualD = new()
                };

                if (pyResult.IndividualD != null)
                {
                    foreach (var kv in pyResult.IndividualD)
                    {
                        var config = _currentConfigs.FirstOrDefault(c => c.ResponseName == kv.Key);
                        result.IndividualD.Add(new IndividualDesirability
                        {
                            ResponseName = kv.Key,
                            PredictedValue = kv.Value.Value,
                            D = kv.Value.D,
                            Goal = config?.Goal ?? DesirabilityGoal.Maximize
                        });
                    }
                }

                _logger.LogInformation("★ Desirability 优化完成: D={D}, 成功={Success}", result.CompositeD, result.Success);
                return Task.FromResult(result);
            }
        }

        /// <summary>
        /// 获取 Profiler 图数据
        /// </summary>
        public Task<string> GetProfileDataAsync(int gridSize = 50)
        {
            EnsurePythonReady();

            using (Py.GIL())
            {
                Func<string, string> predictor = (factorsJson) =>
                {
                    try
                    {
                        var factors = JsonConvert.DeserializeObject<Dictionary<string, double>>(factorsJson) ?? new();
                        var gprPred = _gprService.Predict(factors);
                        var predictions = _currentConfigs.ToDictionary(c => c.ResponseName, c => gprPred.Mean);
                        return JsonConvert.SerializeObject(predictions);
                    }
                    catch
                    {
                        var fallback = _currentConfigs.ToDictionary(c => c.ResponseName, c => (c.Lower + c.Upper) / 2.0);
                        return JsonConvert.SerializeObject(fallback);
                    }
                };

                dynamic pyPredictor = predictor.ToPython();
                string resultJson = _pythonEngine!.profile_plot_data(pyPredictor, gridSize).ToString();
                return Task.FromResult(resultJson);
            }
        }

        /// <summary>
        /// 保存配置到数据库
        /// </summary>
        public async Task SaveConfigAsync(string batchId, List<DesirabilityResponseConfig> configs)
        {
            var configJson = JsonConvert.SerializeObject(configs);
            await _repository.SaveDesirabilityConfigAsync(batchId, configJson);
            _logger.LogInformation("★ Desirability 配置已保存: batchId={BatchId}", batchId);
        }

        /// <summary>
        /// 从数据库加载配置
        /// </summary>
        public async Task<List<DesirabilityResponseConfig>> LoadConfigAsync(string batchId)
        {
            var configJson = await _repository.GetDesirabilityConfigJsonAsync(batchId);
            if (string.IsNullOrEmpty(configJson))
                return new List<DesirabilityResponseConfig>();

            return JsonConvert.DeserializeObject<List<DesirabilityResponseConfig>>(configJson)
                   ?? new List<DesirabilityResponseConfig>();
        }

        // ── Python 结果反序列化类 ──

        private class PyDesirabilityResult
        {
            [JsonProperty("composite_d")] public double CompositeD { get; set; }
            [JsonProperty("individual_d")] public Dictionary<string, PyIndividualD>? IndividualD { get; set; }
        }

        private class PyIndividualD
        {
            [JsonProperty("value")] public double Value { get; set; }
            [JsonProperty("d")] public double D { get; set; }
            [JsonProperty("goal")] public string? Goal { get; set; }
        }

        private class PyOptimizeResult
        {
            [JsonProperty("optimal_factors")] public Dictionary<string, double>? OptimalFactors { get; set; }
            [JsonProperty("composite_d")] public double CompositeD { get; set; }
            [JsonProperty("individual_d")] public Dictionary<string, PyIndividualD>? IndividualD { get; set; }
            [JsonProperty("success")] public bool Success { get; set; }
            [JsonProperty("error")] public string? Error { get; set; }
        }
    }
}