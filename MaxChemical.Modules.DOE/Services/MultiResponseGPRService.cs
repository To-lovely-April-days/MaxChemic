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
    ///  新增: 多响应 GPR 管理器实现
    /// 
    /// 架构说明:
    ///   - _primaryService: 主响应使用已注入的 IGPRModelService 单例（即原来的 GPRModelService）
    ///     所有现有代码（停止条件、ModelAnalysis 页面等）通过 IGPRModelService 接口仍然只看到主响应模型，完全兼容。
    ///   - _secondaryModels: 非主响应各自有一个轻量级的 Python GPR 模型实例
    ///     这些模型直接通过 pythonnet 调用 doe_gpr.py，不经过 GPRModelService 包装
    ///     这样做的原因:
    ///       a. 避免为每个响应注册一个 GPRModelService 单例（Prism DI 不支持 keyed 注册）
    ///       b. 次要模型不需要完整的 GPRModelService 功能（不需要 FindOptimal、ShouldStop 等）
    ///       c. 持久化使用独立的 GPRModelState 记录，FactorSignature 加上响应名后缀区分
    /// 
    /// 持久化策略:
    ///   主模型: FlowId + FactorSignature（与原来一致）
    ///   次要模型: FlowId + FactorSignature + "::" + ResponseName
    ///   例: 主响应"转化率" → sig = "Temperature,Pressure,Catalyst"
    ///       次要"选择性"   → sig = "Temperature,Pressure,Catalyst::选择性"
    ///       次要"成本"     → sig = "Temperature,Pressure,Catalyst::成本"
    /// </summary>

        /// <summary>
        /// 多响应 GPR 管理器 — 【已软禁用】
        /// 所有方法改为空实现,AnyModelActive/AllModelsActive 永远返回 false,
        /// 外层 DesirabilityService 等调用方的 GPR 分支自动跳过。
        /// </summary>
        public class MultiResponseGPRService : IMultiResponseGPRService
        {
            private readonly IGPRModelService _primaryService;
            private readonly IDOERepository _repository;
            private readonly ILogService _logger;

            public MultiResponseGPRService(
                IGPRModelService primaryService,
                IDOERepository repository,
                ILogService logger)
            {
                _primaryService = primaryService ?? throw new ArgumentNullException(nameof(primaryService));
                _repository = repository ?? throw new ArgumentNullException(nameof(repository));
                _logger = logger?.ForContext<MultiResponseGPRService>() ?? throw new ArgumentNullException(nameof(logger));
            }

            // ══════════════ 状态属性 ══════════════

            public List<string> ResponseNames => new List<string>();

            public bool AnyModelActive => false;

            public bool AllModelsActive => false;

            // ══════════════ 初始化 ══════════════

            public Task InitializeAsync(string flowId, List<DOEFactor> factors, List<DOEResponse> responses, string? projectId = null)
            {
                /* GPR 禁用: no-op */
                return Task.CompletedTask;
            }

            // ══════════════ 数据追加 ══════════════

            public void AppendAllResponses(Dictionary<string, object> factorValues,
                                           Dictionary<string, double> responseValues,
                                           string source = "measured",
                                           string batchName = "",
                                           string timestamp = "")
            {
                /* GPR 禁用: no-op */
            }

            // ══════════════ 训练 ══════════════

            public Task<Dictionary<string, GPRTrainResult>> TrainAllAsync()
            {
                /* GPR 禁用 */
                return Task.FromResult(new Dictionary<string, GPRTrainResult>());
            }

            // ══════════════ 多响应预测 ══════════════

            public Dictionary<string, GPRPrediction> PredictAll(Dictionary<string, object> factorValues)
            {
                /* GPR 禁用 */
                return new Dictionary<string, GPRPrediction>();
            }

            public Dictionary<string, GPRPrediction> PredictAllInsideGIL(Dictionary<string, object> factorValues)
            {
                /* GPR 禁用 */
                return new Dictionary<string, GPRPrediction>();
            }

            // ══════════════ 持久化 ══════════════

            public Task SaveAllAsync(string flowId)
            {
                /* GPR 禁用: no-op */
                return Task.CompletedTask;
            }

            // ══════════════ 状态查询 ══════════════

            public List<ResponseModelStatus> GetModelStatuses()
            {
                /* GPR 禁用 */
                return new List<ResponseModelStatus>();
            }

            // ══════════════ 原实现保留注释,恢复时删 /* */ ══════════════
            /*
            [原来所有的字段、辅助方法和实现放这里]
            */
        }
    }
