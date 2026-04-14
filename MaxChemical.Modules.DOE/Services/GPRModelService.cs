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
    /// GPR 模型服务 — 【已软禁用】
    /// 
    /// 所有方法改为空实现,IsActive 永远返回 false,
    /// 外层 if (IsActive) 分支自动跳过,无需修改调用方。
    /// 原实现用 /* */ 注释保留,恢复时删注释即可。
    /// </summary>
    public class GPRModelService : IGPRModelService
    {
        private readonly IDOERepository _repository;
        private readonly ILogService _logger;
        // GPR 禁用: 以下字段保留但不再使用
        // private dynamic? _pythonModel;
        // private bool _isPythonInitialized;
        // private string _currentFlowId = "";
        // private string _currentSignature = "";
        // private string _currentModelName = "";
        // private string? _currentProjectId;

        public GPRModelService(IDOERepository repository, ILogService logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _logger = logger?.ForContext<GPRModelService>() ?? throw new ArgumentNullException(nameof(logger));
        }

        // ══════════════ 状态属性(永远返回禁用值) ══════════════

        public bool IsActive => false;

        public int DataCount => 0;

        public void SetProjectId(string? projectId)
        {
            /* GPR 禁用: no-op */
        }

        // ══════════════ 初始化 ══════════════

        public Task InitializeModelAsync(string flowId, List<DOEFactor> factors)
        {
            /* GPR 禁用: no-op */
            return Task.CompletedTask;
        }

        public dynamic? GetPythonModel()
        {
            /* GPR 禁用 */
            return null;
        }

        // ══════════════ 数据追加 ══════════════

        public void AppendData(Dictionary<string, object> factorValues, double responseValue,
                        string source = "measured", string batchName = "", string timestamp = "")
        {
            /* GPR 禁用: no-op */
        }

        public void AppendData(Dictionary<string, double> factorValues, double responseValue,
                        string source = "measured", string batchName = "", string timestamp = "")
        {
            /* GPR 禁用: no-op */
        }

        // ══════════════ 训练 ══════════════

        public Task<GPRTrainResult> TrainModelAsync()
        {
            /* GPR 禁用 */
            return Task.FromResult(new GPRTrainResult
            {
                IsActive = false,
                DataCount = 0,
                Lengthscales = new Dictionary<string, double>()
            });
        }

        // ══════════════ 预测 ══════════════

        public GPRPrediction Predict(Dictionary<string, object> factorValues)
        {
            /* GPR 禁用 */
            return new GPRPrediction { Mean = 0, Std = 0 };
        }

        public GPRPrediction Predict(Dictionary<string, double> factorValues)
        {
            /* GPR 禁用 */
            return new GPRPrediction { Mean = 0, Std = 0 };
        }

        public List<GPRPrediction> PredictBatch(List<Dictionary<string, object>> factorValuesList)
        {
            /* GPR 禁用 */
            return factorValuesList.Select(_ => new GPRPrediction { Mean = 0, Std = 0 }).ToList();
        }

        public List<GPRPrediction> PredictBatch(List<Dictionary<string, double>> factorValuesList)
        {
            /* GPR 禁用 */
            return factorValuesList.Select(_ => new GPRPrediction { Mean = 0, Std = 0 }).ToList();
        }

        // ══════════════ 最优搜索 ══════════════

        public GPROptimalResult FindOptimal(bool maximize = true)
        {
            /* GPR 禁用 */
            return new GPROptimalResult
            {
                OptimalFactors = new Dictionary<string, double>(),
                PredictedResponse = 0,
                PredictionStd = 0,
                Direction = maximize ? "maximize" : "minimize"
            };
        }

        // ══════════════ 停止判断 ══════════════

        public GPRStopDecision ShouldStop(List<Dictionary<string, object>> remainingRuns, double bestObserved,
                                          bool maximize = true, double eiThreshold = 0.01, double minRunsRatio = 1.5)
        {
            /* GPR 禁用: 永远不触发模型停止 */
            return new GPRStopDecision { ShouldStop = false, Reason = "GPR 已禁用" };
        }

        public GPRStopDecision ShouldStop(List<Dictionary<string, double>> remainingRuns, double bestObserved,
                                          bool maximize = true, double eiThreshold = 0.01, double minRunsRatio = 1.5)
        {
            /* GPR 禁用: 永远不触发模型停止 */
            return new GPRStopDecision { ShouldStop = false, Reason = "GPR 已禁用" };
        }

        // ══════════════ 敏感性 ══════════════

        public Dictionary<string, double> GetSensitivity()
        {
            /* GPR 禁用 */
            return new Dictionary<string, double>();
        }

        // ══════════════ 持久化 ══════════════

        public Task SaveStateAsync(string flowId)
        {
            /* GPR 禁用: no-op */
            return Task.CompletedTask;
        }

        public Task SaveInitialStateAsync(string flowId)
        {
            /* GPR 禁用: no-op */
            return Task.CompletedTask;
        }

        public Task<bool> LoadStateAsync(string flowId)
        {
            /* GPR 禁用 */
            return Task.FromResult(false);
        }

        public Task ResetModelAsync(string flowId, bool keepData)
        {
            /* GPR 禁用: no-op */
            return Task.CompletedTask;
        }

        // ══════════════ 原实现保留在下面的注释块中,需要恢复时删掉 /* 和 */ 即可 ══════════════
        /*
        [把原来所有的方法实现、EnsurePythonEnv、EnsureModelReady、GetPythonProperty、
         PythonTrainResult/PythonPrediction/PythonOptimalResult/PythonStopDecision 嵌套类
        */
    }
}