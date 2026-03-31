using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MaxChemical.Modules.DOE.Models;

namespace MaxChemical.Modules.DOE.Services
{
    // ═══════════════════════════════════════════════════════
    // IDOEDesignService — 实验设计服务
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// DOE 实验设计服务 — 调用 Python doe_engine.pyd 生成参数矩阵
    /// </summary>
    public interface IDOEDesignService
    {
        /// <summary>
        /// 生成全因子设计矩阵
        /// </summary>
        Task<DOEDesignMatrix> GenerateFullFactorialAsync(List<DOEFactor> factors);

        /// <summary>
        /// 生成部分因子设计矩阵
        /// </summary>
        Task<DOEDesignMatrix> GenerateFractionalFactorialAsync(List<DOEFactor> factors, int resolution);

        /// <summary>
        /// 生成正交设计矩阵（Taguchi）
        /// </summary>
        Task<DOEDesignMatrix> GenerateTaguchiAsync(List<DOEFactor> factors, string tableType);

        /// <summary>
        /// 导入自定义参数矩阵（Excel）
        /// </summary>
        Task<DOEDesignMatrix> ImportCustomMatrixAsync(string excelPath, List<DOEFactor> factors);

        /// <summary>
        /// 校验导入的历史数据 Excel
        /// </summary>
        Task<DataValidationResult> ValidateImportedDataAsync(string excelPath, List<DOEFactor> factors, List<DOEResponse> responses);

        /// <summary>
        /// 导入历史实验数据
        /// </summary>
        Task<List<DOERunRecord>> ImportHistoricalDataAsync(string excelPath, string batchId, List<DOEFactor> factors, List<DOEResponse> responses);


    }

    /// <summary>
    /// 数据校验结果
    /// </summary>
    public class DataValidationResult
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public int ValidRowCount { get; set; }
    }

    // ═══════════════════════════════════════════════════════
    // IDOEExecutionService — 批量执行服务
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// DOE 批量执行服务 — 编排多组实验的执行循环
    /// </summary>
    public interface IDOEExecutionService
    {
        /// <summary>
        /// 当前批次执行状态
        /// </summary>
        DOEBatchStatus BatchState { get; }

        /// <summary>
        /// 当前正在执行的组索引（-1 表示未开始）
        /// </summary>
        int CurrentRunIndex { get; }

        /// <summary>
        /// 启动批量执行
        /// </summary>
        Task StartBatchAsync(string batchId, CancellationToken cancellationToken = default);

        /// <summary>
        /// 暂停批量执行
        /// </summary>
        void PauseBatch();

        /// <summary>
        /// 恢复批量执行
        /// </summary>
        void ResumeBatch();

        /// <summary>
        /// 终止批量执行
        /// </summary>
        Task AbortBatchAsync();

        /// <summary>
        /// 提交某组实验的响应值（弹框录入后调用）
        /// </summary>
        void SubmitRunResponse(int runIndex, Dictionary<string, double> responseValues);

        /// <summary>
        /// 单组实验完成事件
        /// </summary>
        event EventHandler<DOERunCompletedEventArgs>? RunCompleted;

        /// <summary>
        /// 整个批次完成事件
        /// </summary>
        event EventHandler<DOEBatchCompletedEventArgs>? BatchCompleted;

        /// <summary>
        /// 进度变化事件
        /// </summary>
        event EventHandler<DOEProgressEventArgs>? ProgressChanged;

        /// <summary>
        /// 暂停真正生效时触发（当前组执行完毕，循环已停下）
        /// </summary>
        event EventHandler? PauseConfirmed;
    }

    public class DOERunCompletedEventArgs : EventArgs
    {
        public int RunIndex { get; set; }
        public Dictionary<string, double> FactorValues { get; set; } = new();
        public Dictionary<string, double> ResponseValues { get; set; } = new();
        public bool IsSuccessful { get; set; }
    }

    public class DOEBatchCompletedEventArgs : EventArgs
    {
        public string BatchId { get; set; } = string.Empty;
        public DOEBatchStatus FinalStatus { get; set; }
        public int TotalRuns { get; set; }
        public int CompletedRuns { get; set; }
        public string? StopReason { get; set; }
    }

    public class DOEProgressEventArgs : EventArgs
    {
        public int CurrentRun { get; set; }
        public int TotalRuns { get; set; }
        public double ProgressPercent => TotalRuns > 0 ? (double)CurrentRun / TotalRuns * 100 : 0;
        public string StatusMessage { get; set; } = string.Empty;
    }

    // ═══════════════════════════════════════════════════════
    // IGPRModelService — GPR 预测模型服务
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// GPR 模型服务 — 管理高斯过程回归模型的生命周期，调用 doe_gpr.pyd
    /// </summary>
    public interface IGPRModelService
    {
        /// <summary>
        /// 初始化模型（新建或从数据库恢复）
        /// </summary>
        Task InitializeModelAsync(string flowId, List<DOEFactor> factors);

        /// <summary>
        /// 获取内部 Python GPR 模型对象（供 SurfacePlotter 使用）
        /// </summary>
        dynamic? GetPythonModel();
        /// <summary>
        /// 训练/重新拟合模型
        /// </summary>
        Task<GPRTrainResult> TrainModelAsync();

        /// <summary>
        /// 预测单组因子值的响应
        /// </summary>
        GPRPrediction Predict(Dictionary<string, double> factorValues);

        /// <summary>
        /// 批量预测
        /// </summary>
        List<GPRPrediction> PredictBatch(List<Dictionary<string, double>> factorValuesList);

        /// <summary>
        /// 搜索最优因子组合
        /// </summary>
        GPROptimalResult FindOptimal();

        /// <summary>
        /// 判断是否应该提前停止
        /// </summary>
        GPRStopDecision ShouldStop(List<Dictionary<string, double>> remainingRuns, double bestObserved);

        /// <summary>
        /// 获取参数敏感性排序
        /// </summary>
        Dictionary<string, double> GetSensitivity();

        /// <summary>
        /// 保存模型状态到数据库
        /// </summary>
        Task SaveStateAsync(string flowId);

        /// <summary>
        /// 从数据库恢复模型状态
        /// </summary>
        Task<bool> LoadStateAsync(string flowId);

        /// <summary>
        /// 重置模型（清空回到冷启动状态）
        /// </summary>
        Task ResetModelAsync(string flowId, bool keepData);

        /// <summary>
        /// 模型是否已激活
        /// </summary>
        bool IsActive { get; }

        /// <summary>
        /// 当前数据量
        /// </summary>
        int DataCount { get; }
        Task SaveInitialStateAsync(string flowId);

        // 接口
        void AppendData(Dictionary<string, double> factorValues, double responseValue,
                        string source = "measured", string batchName = "", string timestamp = "");
    }

    public class GPRTrainResult
    {
        public bool IsActive { get; set; }
        public double RSquared { get; set; }
        public double RMSE { get; set; }
        public Dictionary<string, double> Lengthscales { get; set; } = new();
        public int DataCount { get; set; }
    }

    public class GPRPrediction
    {
        public double Mean { get; set; }
        public double Std { get; set; }
        public double LowerBound => Mean - 2 * Std;
        public double UpperBound => Mean + 2 * Std;
    }

    public class GPROptimalResult
    {
        public Dictionary<string, double> OptimalFactors { get; set; } = new();
        public double PredictedResponse { get; set; }
        public double PredictionStd { get; set; }
        public double Confidence => 1.0 - PredictionStd;
    }

    public class GPRStopDecision
    {
        public bool ShouldStop { get; set; }
        public string Reason { get; set; } = string.Empty;
        public double BestPredicted { get; set; }
        public double BestPredictedStd { get; set; }
    }

    // ═══════════════════════════════════════════════════════
    // IDOEAnalysisService — 数据分析服务
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// DOE 数据分析服务 — 调用 Python doe_engine.pyd 进行统计分析和图表数据生成
    /// </summary>
    public interface IDOEAnalysisService
    {
        Task<string> GetMainEffectsAsync(string batchId);
        Task<string> GetInteractionEffectsAsync(string batchId);
        Task<string> GetParetoChartAsync(string batchId);
        Task<string> GetResponseSurfaceDataAsync(string batchId, string factor1, string factor2);
        Task<string> GetContourDataAsync(string batchId, string factor1, string factor2);
        Task<byte[]> GetResponseSurfaceImageAsync(string batchId, string factor1, string factor2);
        /// <summary>
        /// 用 GPR 模型直接生成响应曲面图（不依赖批次）
        /// </summary>
        Task<byte[]> GetResponseSurfaceImageFromGPRAsync(string factor1, string factor2);
        Task<string> GetResidualAnalysisAsync(string batchId);
        Task<string> GetActualVsPredictedAsync(string batchId);
        Task<string> GetAnovaTableAsync(string batchId);
        Task<string> GetRegressionSummaryAsync(string batchId);
        Task<string> GetGPRConfidenceBandAsync(string flowId);
        Task<string> GetAcquisitionSurfaceAsync(string flowId, string factor1, string factor2);
        Task<string> GetModelEvolutionAsync(string flowId);
    }
}