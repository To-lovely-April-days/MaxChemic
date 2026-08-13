using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MaxChemical.Logging;
using MaxChemical.Modules.DOE.Data;
using MaxChemical.Modules.DOE.Events;
using MaxChemical.Modules.DOE.Models;
using Prism.Events;

namespace MaxChemical.Modules.DOE.Services
{
    /// <summary>
    /// 先猜后验(Predict-then-Verify)追踪器:批次执行时,每组开跑前用已完成数据拟合模型、
    /// 对当前组给出预测区间;实测回来后判定命中/越界;收官时做校准自检。
    /// 纯事件驱动,不侵入执行器主循环;计算失败只降级为「无预测」,绝不影响执行。
    ///
    /// 三条铁律(设计约定,不许改松):
    ///  1. 预测区间含观测噪声:μ ± k·sqrt(σ²(x)+σn²)——是「下一个观测」的区间,不是均值的区间;
    ///     漏掉 σn 区间会系统性偏窄,命中率永远到不了 95%,图上一眼露馅。
    ///  2. 报警用 3σ(99.7%),2σ 只计入校准统计——2σ 报警 20 组里期望 1 次假警报,
    ///     报警器的可信度是一次性的,喊错一次就废了。
    ///  3. 前 3 组老实说「数据不足,模型还猜不了」——敢说不知道,比从第 1 组就硬猜可信十倍。
    /// </summary>
    public sealed class PredictVerifyTracker
    {
        private readonly IEventAggregator _ea;
        private readonly IDOERepository _repository;
        private readonly IDOEAnalysisService _analysis;
        private readonly ILogService _logger = LogManager.GetLogger<PredictVerifyTracker>();

        private readonly SemaphoreSlim _gate = new(1, 1);

        // ── 当前批次状态(单批次跟踪,与执行器的单批次语义一致) ──
        private string _batchId = "";
        private string _respName = "";
        private string _modelType = "linear";
        private readonly Dictionary<int, PredictVerifyEntry> _entries = new();
        // RunIndex → 1 起显示序号(向导批次 RunIndex 是 0 起、小桐/AutoPilot 是 1 起,展示一律归一)
        private readonly Dictionary<int, int> _displayNo = new();
        private int _hitCount;
        private int _judgedCount;
        private bool _calibrated;
        private bool _started;

        public PredictVerifyTracker(IEventAggregator ea, IDOERepository repository, IDOEAnalysisService analysis)
        {
            _ea = ea;
            _repository = repository;
            _analysis = analysis;
        }

        /// <summary>模块初始化时调用一次;订阅执行事件(常驻)。</summary>
        public void Start()
        {
            if (_started) return;
            _started = true;
            _ea.GetEvent<DOEBatchStartedEvent>().Subscribe(
                id => _ = RunSafeAsync(() => OnBatchStartedAsync(id)), ThreadOption.BackgroundThread, true);
            _ea.GetEvent<DOEProgressChangedEvent>().Subscribe(
                p => _ = RunSafeAsync(() => OnRunStartingAsync(p)), ThreadOption.BackgroundThread, true);
            _ea.GetEvent<DOERunResultRecordedEvent>().Subscribe(
                p => _ = RunSafeAsync(() => OnRunRecordedAsync(p)), ThreadOption.BackgroundThread, true);
            _ea.GetEvent<DOEBatchCompletedEvent>().Subscribe(
                p => _ = RunSafeAsync(() => OnBatchCompletedAsync(p)), ThreadOption.BackgroundThread, true);
        }

        /// <summary>RunIndex → 1 起显示序号(映射缺失时兜底为 RunIndex 原值)。</summary>
        private int No(int runIndex) => _displayNo.TryGetValue(runIndex, out var n) ? n : runIndex;

        private async Task RunSafeAsync(Func<Task> work)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try { await work().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning($"先猜后验计算失败(忽略,不影响执行): {ex.Message}"); }
            finally { _gate.Release(); }
        }

        // ══════════════ 批次开始:重置并锁定响应 ══════════════

        private async Task OnBatchStartedAsync(string batchId)
        {
            _batchId = batchId ?? "";
            _entries.Clear();
            _displayNo.Clear();
            _hitCount = 0;
            _judgedCount = 0;
            _calibrated = false;
            _respName = "";

            var batch = await _repository.GetBatchWithDetailsAsync(batchId).ConfigureAwait(false);
            if (batch == null) { _batchId = ""; return; }

            _respName = batch.Responses?.OrderBy(r => r.SortOrder).FirstOrDefault()?.ResponseName ?? "";
            if (string.IsNullOrEmpty(_respName)) { _batchId = ""; return; }

            int no = 1;
            foreach (var r in (batch.Runs ?? new List<DOERunRecord>()).OrderBy(r => r.RunIndex))
                _displayNo[r.RunIndex] = no++;

            _modelType = batch.DesignMethod is DOEDesignMethod.CCD or DOEDesignMethod.BoxBehnken
                or DOEDesignMethod.DOptimal or DOEDesignMethod.AugmentedDesign
                ? "quadratic" : "linear";

            // 续跑批次:历史会话完成的组当时没有事件,这里按实测回填画点——
            // 它们没有「开跑前预测」,只出蓝点、不计入校准(补猜历史等于作弊)
            int prefilled = 0;
            foreach (var r in (batch.Runs ?? new List<DOERunRecord>())
                     .Where(r => r.Status == DOERunStatus.Completed).OrderBy(r => r.RunIndex))
            {
                var vals = ParseNumbers(r.ResponseValuesJson);
                if (!vals.TryGetValue(_respName, out double m)) continue;
                _entries[r.RunIndex] = new PredictVerifyEntry
                {
                    RunIndex = r.RunIndex,
                    DisplayNo = No(r.RunIndex),
                    Measured = m
                };
                prefilled++;
            }

            Publish(prefilled > 0
                ? $"先猜后验已就绪(续跑批次,已回填 {prefilled} 组历史实测;历史组不补猜,只做建模数据):每组开跑前预测「{_respName}」,实测回来后验证。"
                : $"先猜后验已就绪:每组开跑前预测「{_respName}」,实测回来后验证(前 3 组数据不足,模型不猜)。", false, false);
        }

        // ══════════════ 组开跑:预测(或老实说猜不了) ══════════════

        private async Task OnRunStartingAsync(DOEProgressPayload p)
        {
            if (p == null || p.BatchId != _batchId || string.IsNullOrEmpty(_batchId)) return;
            if (p.RunIndex <= 0 || string.IsNullOrEmpty(p.CurrentFactorValuesJson)) return;
            if (_entries.ContainsKey(p.RunIndex)) return; // 重复事件

            foreach (var e in _entries.Values) e.IsCurrent = false;
            var entry = new PredictVerifyEntry { RunIndex = p.RunIndex, DisplayNo = No(p.RunIndex), IsCurrent = true };
            _entries[p.RunIndex] = entry;

            // 已完成且带本响应数据的组数
            var batch = await _repository.GetBatchWithDetailsAsync(_batchId).ConfigureAwait(false);
            int n = batch?.Runs?.Count(r =>
                r.Status == DOERunStatus.Completed &&
                ParseNumbers(r.ResponseValuesJson).ContainsKey(_respName)) ?? 0;

            // 防泄漏:本组若已经完成(仿真模式跑得比预测计算快),拟合数据已含本组结果,
            // 这时的「预测」是作弊——宁可不猜,也不给一个必然命中的假区间
            var self = batch?.Runs?.FirstOrDefault(r => r.RunIndex == p.RunIndex);
            if (self != null && self.Status == DOERunStatus.Completed)
            {
                entry.IsCurrent = false;
                Publish($"第 {No(p.RunIndex)} 组 · 该组结果已回来,来不及做开跑前预测(本组不猜,只计入建模数据)。", false, false);
                return;
            }

            if (n < 3)
            {
                Publish($"第 {No(p.RunIndex)} 组 · 数据不足,模型还猜不了(已完成 {n} 组,建模至少要 3 组)。", false, false);
                return;
            }

            try
            {
                // RSM 类批次前期二次模型参数比数据点还多,拟合必失败——降阶到线性照样能猜,
                // 区间会宽一点,但宽区间恰恰是「模型还没把握」的诚实表达
                string usedModel = _modelType;
                var ols = await _analysis.FitOlsAsync(_batchId, _respName, _modelType).ConfigureAwait(false);
                if ((ols?.Coefficients == null || ols.Coefficients.Count == 0) && _modelType != "linear")
                {
                    usedModel = "linear";
                    ols = await _analysis.FitOlsAsync(_batchId, _respName, "linear").ConfigureAwait(false);
                }
                if (ols?.ModelSummary == null || ols.Coefficients == null || ols.Coefficients.Count == 0)
                {
                    Publish($"第 {No(p.RunIndex)} 组 · 模型暂时建不起来(数据仍不足或共线),这组先不猜。", false, false);
                    return;
                }

                // 该组因子条件下的点预测:profiler 的 current_* 即固定条件下的 μ 与均值置信区间
                var fixedValues = ParseFixedValues(p.CurrentFactorValuesJson);
                string pj = await _analysis.GetPredictionProfilerAsync(_batchId, _respName, 5, fixedValues).ConfigureAwait(false);
                var pt = ParsePoint(pj);
                if (!pt.Ok)
                {
                    Publish($"第 {No(p.RunIndex)} 组 · 预测计算未成功,这组先不猜。", false, false);
                    return;
                }

                double tCrit = ols.TCrit > 0 ? ols.TCrit : 2.0;
                double sigmaN = ols.Sigma > 0 ? ols.Sigma : ols.ModelSummary.RMSE;   // 观测噪声 σn
                double seMean = tCrit > 0 ? (pt.CiHi - pt.Pred) / tCrit : 0;          // 均值标准误 σ(x)
                double piSigma = Math.Sqrt(sigmaN * sigmaN + seMean * seMean);        // 预测标准差(含噪声)

                entry.Predicted = pt.Pred;
                entry.HalfWidth2 = 2 * piSigma;
                entry.HalfWidth3 = 3 * piSigma;

                string modelNote = usedModel != _modelType ? "(数据尚少,暂用线性模型)" : "";
                Publish($"第 {No(p.RunIndex)} 组开跑前 · 用前 {n} 组数据预测:{_respName} = {Fmt(pt.Pred)} ± {Fmt(entry.HalfWidth2.Value)}(95% 预测区间,含观测噪声){modelNote} — 等实测回来验证。",
                    false, false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"先猜后验预测失败(第 {No(p.RunIndex)} 组,忽略): {ex.Message}");
                Publish($"第 {No(p.RunIndex)} 组 · 预测计算未成功,这组先不猜。", false, false);
            }
        }

        // ══════════════ 实测回来:验证 ══════════════

        private Task OnRunRecordedAsync(DOERunCompletedPayload p)
        {
            if (p == null || p.BatchId != _batchId || string.IsNullOrEmpty(_batchId)) return Task.CompletedTask;
            var vals = ParseNumbers(p.ResponseValuesJson);
            if (!vals.TryGetValue(_respName, out double measured)) return Task.CompletedTask;

            if (!_entries.TryGetValue(p.RunIndex, out var entry))
            {
                entry = new PredictVerifyEntry { RunIndex = p.RunIndex, DisplayNo = No(p.RunIndex) };
                _entries[p.RunIndex] = entry;
            }
            entry.Measured = measured;
            entry.IsCurrent = false;

            if (entry.Predicted == null || entry.HalfWidth2 == null)
            {
                Publish($"第 {No(p.RunIndex)} 组 · 实测 {_respName} = {Fmt(measured)}(该组无预测,已计入建模数据)。", false, false);
                return Task.CompletedTask;
            }

            double dev = Math.Abs(measured - entry.Predicted.Value);
            entry.IsMiss2 = dev > entry.HalfWidth2.Value;
            entry.IsAlarm3 = dev > entry.HalfWidth3.Value;
            _judgedCount++;
            if (!entry.IsMiss2) _hitCount++;

            if (entry.IsAlarm3)
            {
                Publish($"第 {No(p.RunIndex)} 组 · 实测 {Fmt(measured)},偏离预测 {Fmt(dev)}(超 3σ 报警线)→ 建议复核该组进料浓度、泵标定与原始数据,再决定是否重跑。",
                    true, false);
            }
            else if (entry.IsMiss2)
            {
                Publish($"第 {No(p.RunIndex)} 组 · 实测 {Fmt(measured)},落在 95% 区间外但未超 3σ——20 组里本就该有 1 组这样,不报警,计入校准统计。",
                    false, false);
            }
            else
            {
                Publish($"第 {No(p.RunIndex)} 组 · 实测 {Fmt(measured)},落在预测区间内 · 模型已吸收该点,下一组区间会更窄。",
                    false, false);
            }
            return Task.CompletedTask;
        }

        // ══════════════ 收官:校准自检(模型评价自己准不准) ══════════════

        private Task OnBatchCompletedAsync(DOEBatchCompletedPayload p)
        {
            if (p == null || p.BatchId != _batchId || string.IsNullOrEmpty(_batchId)) return Task.CompletedTask;
            // 中止批次不做校准(数据不完整,结论无意义);完成事件可能双发,只发一次
            if (_calibrated || p.FinalStatus == DOEBatchStatus.Aborted) return Task.CompletedTask;
            _calibrated = true;

            string calib;
            if (_judgedCount == 0)
            {
                calib = "本批没有可校准的预测(有预测且有实测的组数为 0)。";
            }
            else
            {
                double rate = (double)_hitCount / _judgedCount;
                string head = $"校准自检:{_hitCount}/{_judgedCount} 组落在 95% 预测区间内(命中率 {rate:P0},理论值 95%)——";
                if (_judgedCount < 5)
                    calib = head + "样本还少,校准结论仅供参考。";
                else if (rate < 0.8)
                    calib = head + "命中率明显偏低,模型高估了自己(区间偏窄),它给的预测要打折扣,建议检查异常组或补做重复实验。";
                else if (rate >= 1.0)
                    calib = head + "全部命中,区间可能画得偏宽,模型偏保守(不算错,但预测的参考价值打了折)。";
                else
                    calib = head + "校准良好,模型对自己的不确定性估计是诚实的,预测区间可以当真。";
            }

            int alarms = _entries.Values.Count(e => e.IsAlarm3);
            if (alarms > 0) calib += $" 另有 {alarms} 组超 3σ 已标记待复核。";

            Publish(calib, false, true);
            return Task.CompletedTask;
        }

        // ══════════════ 发布与工具 ══════════════

        private void Publish(string message, bool isAlarm, bool isCompleted)
        {
            try
            {
                _ea.GetEvent<DOEPredictVerifyEvent>().Publish(new DOEPredictVerifyPayload
                {
                    BatchId = _batchId,
                    ResponseName = _respName,
                    Entries = _entries.Values.OrderBy(e => e.RunIndex)
                        .Select(e => new PredictVerifyEntry
                        {
                            RunIndex = e.RunIndex,
                            DisplayNo = e.DisplayNo,
                            Predicted = e.Predicted,
                            HalfWidth2 = e.HalfWidth2,
                            HalfWidth3 = e.HalfWidth3,
                            Measured = e.Measured,
                            IsCurrent = e.IsCurrent,
                            IsMiss2 = e.IsMiss2,
                            IsAlarm3 = e.IsAlarm3
                        }).ToList(),
                    Message = message,
                    IsAlarm = isAlarm,
                    IsCompleted = isCompleted,
                    JudgedCount = _judgedCount,
                    HitCount = _hitCount,
                    CalibrationText = isCompleted ? message : ""
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"先猜后验事件发布失败: {ex.Message}");
            }
        }

        private static Dictionary<string, object> ParseFixedValues(string json)
        {
            var dict = new Dictionary<string, object>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number) dict[prop.Name] = prop.Value.GetDouble();
                    else if (prop.Value.ValueKind == JsonValueKind.String) dict[prop.Name] = prop.Value.GetString();
                }
            }
            catch { }
            return dict;
        }

        private static Dictionary<string, double> ParseNumbers(string json)
        {
            var dict = new Dictionary<string, double>();
            if (string.IsNullOrWhiteSpace(json)) return dict;
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject())
                    if (prop.Value.ValueKind == JsonValueKind.Number) dict[prop.Name] = prop.Value.GetDouble();
            }
            catch { }
            return dict;
        }

        private static (bool Ok, double Pred, double CiLo, double CiHi) ParsePoint(string profilerJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(profilerJson);
                var root = doc.RootElement;
                if (!root.TryGetProperty("current_predicted", out var p) || p.ValueKind != JsonValueKind.Number)
                    return (false, 0, 0, 0);
                double pred = p.GetDouble();
                double lo = root.TryGetProperty("current_ci_lower", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDouble() : pred;
                double hi = root.TryGetProperty("current_ci_upper", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetDouble() : pred;
                return (true, pred, lo, hi);
            }
            catch { return (false, 0, 0, 0); }
        }

        private static string Fmt(double v) => Math.Abs(v) >= 1000 ? v.ToString("F1") : v.ToString("0.###");
    }
}
