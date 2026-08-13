using System;
using System.Collections.Generic;
using System.Linq;

namespace MaxChemical.Modules.DOE.Services
{
    /// <summary>动力学拟合的一个数据点:稳态 PFR 的 (停留时间, 温度, 转化率)。</summary>
    public sealed class KineticsPoint
    {
        public double Tau { get; set; }        // min
        public double? TempC { get; set; }     // ℃;无温度因子时为 null(单温拟合,不出 Ea)
        public double X { get; set; }          // 转化率 0~1
        public int RunIndex { get; set; }
    }

    /// <summary>同温度组的拟合结果。</summary>
    public sealed class KineticsTGroup
    {
        public double TempC { get; set; }
        public double K { get; set; }          // 一级/拟一级: 1/min;二级: L/(mol·min)
        public int N { get; set; }
        public double R2 { get; set; }
        public int DistinctTau { get; set; }
    }

    /// <summary>动力学拟合总结果(引擎是裁判:Tier 由硬规则给出,AI 只解读)。</summary>
    public sealed class KineticsFitResult
    {
        public string Template { get; set; } = "first_order";
        public List<KineticsTGroup> Groups { get; } = new();
        public double? EaKJmol { get; set; }
        public double? EaSeKJmol { get; set; }     // 标准误(组数=2 时无法估计,为 null)
        public double? LnA { get; set; }
        public double? ArrheniusR2 { get; set; }
        public double GlobalR2 { get; set; }
        public double Rmse { get; set; }
        public int N { get; set; }
        public double TauMin { get; set; }
        public double TauMax { get; set; }
        public double? TMinC { get; set; }
        public double? TMaxC { get; set; }
        public List<string> Notes { get; } = new();

        /// <summary>0=可靠 1=谨慎(给数但降级) 2=拒绝(不给预测)。</summary>
        public int Tier { get; set; }
    }

    /// <summary>
    /// 单反应稳态 PFR 动力学拟合(机理层 V1,纯函数无依赖):
    ///   first_order / pseudo_first_order : X = 1 − exp(−kτ)
    ///   second_order(A+B 等摩尔或 2A)   : X = k·C_A0·τ / (1 + k·C_A0·τ)
    /// 逐温度组用黄金分割在 ln k 空间做一维最小二乘,多温度组再回归 Arrhenius 得 Ea。
    /// 假设:等温、稳态、体积流量恒定;这些假设写进 Notes,由上层如实转告用户。
    /// </summary>
    public static class KineticsFit
    {
        public const double GasConstant = 8.314e-3; // kJ/(mol·K)

        public static double ModelX(string template, double k, double tau, double cA0)
        {
            if (tau <= 0 || k <= 0) return 0;
            if (template == "second_order")
            {
                double kt = k * cA0 * tau;
                return kt / (1 + kt);
            }
            return 1 - Math.Exp(-k * tau);
        }

        /// <summary>由目标转化率反解所需停留时间(min)。</summary>
        public static double SolveTauForX(string template, double k, double x, double cA0)
        {
            x = Math.Min(Math.Max(x, 1e-6), 0.999999);
            if (template == "second_order")
                return x / (k * cA0 * (1 - x));
            return -Math.Log(1 - x) / k;
        }

        /// <summary>由 Arrhenius 参数计算任意温度下的 k。</summary>
        public static double KAtTemp(double lnA, double eaKJmol, double tempC)
            => Math.Exp(lnA - eaKJmol / (GasConstant * (tempC + 273.15)));

        public static KineticsFitResult Fit(string template, IReadOnlyList<KineticsPoint> pts, double cA0)
        {
            var r = new KineticsFitResult { Template = template };
            var data = (pts ?? Array.Empty<KineticsPoint>() as IReadOnlyList<KineticsPoint>)
                .Where(p => p.Tau > 0 && p.X >= 0).ToList();
            r.N = data.Count;

            if (data.Count < 4)
            {
                r.Tier = 2;
                r.Notes.Add($"有效数据只有 {data.Count} 组(至少 4 组),不足以拟合动力学。");
                return r;
            }
            if (template == "second_order" && cA0 <= 0)
            {
                r.Tier = 2;
                r.Notes.Add("二级模板必须提供限量物起始浓度 C_A0(mol/L)。");
                return r;
            }

            // 转化率越界检查:X>1 说明响应不是转化率语义(或选择性>1 的记录口径问题)
            int over = data.Count(p => p.X > 1.05);
            if (over > 0)
                r.Notes.Add($"{over} 组转化率大于 1,响应口径可能不是转化率/分数收率,结果需谨慎。");
            foreach (var p in data) p.X = Math.Min(p.X, 0.999); // 数学上限,避免 ln(0)

            r.TauMin = data.Min(p => p.Tau);
            r.TauMax = data.Max(p => p.Tau);

            // ── 按温度分组(容差 1℃;无温度因子 → 单组) ──
            var groups = data.GroupBy(p => p.TempC.HasValue ? Math.Round(p.TempC.Value) : double.NaN)
                             .OrderBy(g => g.Key).ToList();
            if (data.Any(p => p.TempC.HasValue))
            {
                r.TMinC = data.Where(p => p.TempC.HasValue).Min(p => p.TempC.Value);
                r.TMaxC = data.Where(p => p.TempC.HasValue).Max(p => p.TempC.Value);
            }

            double xMean = data.Average(p => p.X);
            foreach (var g in groups)
            {
                var list = g.ToList();
                double k = FitK(template, list, cA0);
                double sse = list.Sum(p => Sq(p.X - ModelX(template, k, p.Tau, cA0)));
                double sst = list.Sum(p => Sq(p.X - list.Average(q => q.X)));
                r.Groups.Add(new KineticsTGroup
                {
                    TempC = double.IsNaN(g.Key) ? double.NaN : g.Key,
                    K = k,
                    N = list.Count,
                    R2 = sst > 1e-12 ? 1 - sse / sst : 1,
                    DistinctTau = list.Select(p => Math.Round(p.Tau, 3)).Distinct().Count()
                });
            }
            double globalSse = 0, globalSst = data.Sum(p => Sq(p.X - xMean));
            foreach (var g in r.Groups)
            {
                var list = groups.First(x2 => (double.IsNaN(g.TempC) && double.IsNaN(x2.Key)) || Math.Abs(x2.Key - g.TempC) < 0.01).ToList();
                globalSse += list.Sum(p => Sq(p.X - ModelX(template, g.K, p.Tau, cA0)));
            }
            r.GlobalR2 = globalSst > 1e-12 ? 1 - globalSse / globalSst : 1;
            r.Rmse = Math.Sqrt(globalSse / data.Count);

            // τ 变化不足:同温组里 τ 全一样,等于没有动力学信息
            if (r.Groups.All(g => g.DistinctTau < 3))
                r.Notes.Add("各温度组内停留时间的取值不足 3 个,曲率信息很弱,k 的可靠性有限。");

            // ── Arrhenius(≥2 个有温度的组且温度跨度 ≥10℃) ──
            var tg = r.Groups.Where(g => !double.IsNaN(g.TempC) && g.K > 0).ToList();
            if (tg.Count >= 2 && (r.TMaxC - r.TMinC) >= 10)
            {
                // ln k = lnA − Ea/R · (1/T)
                var xs = tg.Select(g => 1.0 / (g.TempC + 273.15)).ToList();
                var ys = tg.Select(g => Math.Log(g.K)).ToList();
                double mx = xs.Average(), my = ys.Average();
                double sxx = xs.Sum(v => Sq(v - mx));
                double sxy = xs.Zip(ys, (a, b) => (a - mx) * (b - my)).Sum();
                if (sxx > 1e-18)
                {
                    double slope = sxy / sxx;
                    r.EaKJmol = -slope * GasConstant;
                    r.LnA = my - slope * mx;
                    double ssRes = xs.Zip(ys, (a, b) => Sq(b - (my + slope * (a - mx)))).Sum();
                    double ssTot = ys.Sum(v => Sq(v - my));
                    r.ArrheniusR2 = ssTot > 1e-12 ? 1 - ssRes / ssTot : 1;
                    if (tg.Count > 2)
                    {
                        double se = Math.Sqrt(ssRes / (tg.Count - 2) / sxx) * GasConstant;
                        r.EaSeKJmol = se;
                    }
                    else
                    {
                        r.Notes.Add("只有 2 个温度水平,活化能没有自由度估计误差,建议补第 3 个温度。");
                    }
                    if (r.EaKJmol < 0)
                        r.Notes.Add("拟出的活化能为负:随温度升高转化率下降,可能存在副反应/分解或数据口径问题,机理假设存疑。");
                }

                // k 随温度非单调(高温组反而变慢):Arrhenius 前提崩坏,比线性度数字更该说人话
                bool nonMono = false;
                for (int i = 1; i < tg.Count; i++)
                    if (tg[i].K < tg[i - 1].K * 0.999) nonMono = true;
                if (nonMono)
                    r.Notes.Add("k 随温度非单调(更高温度下反应反而变慢):可能高温失活、分解或传质受限,活化能与跨温度外推不可用,建议核查高温组数据。");
            }
            else if (tg.Count >= 1 && r.Groups.Count > 1)
            {
                r.Notes.Add("温度水平不足或跨度小于 10℃,只给各温度下的 k,不拟合活化能。");
            }

            // ── 质量闸门(硬规则) ──
            if (r.GlobalR2 < 0.6)
            {
                r.Tier = 2;
                r.Notes.Add($"拟合 R² = {r.GlobalR2:F3},低于 0.6:该机理模板解释不了数据,拒绝据此外推。可能原因:反应级数不对、非等温、响应不是转化率口径。");
            }
            else if (r.GlobalR2 < 0.85 || (r.ArrheniusR2.HasValue && r.ArrheniusR2 < 0.9) || r.EaKJmol < 0)
            {
                r.Tier = 1;
                if (r.GlobalR2 < 0.85) r.Notes.Add($"拟合 R² = {r.GlobalR2:F3},机理描述数据的能力一般,预测仅供参考。");
                if (r.ArrheniusR2.HasValue && r.ArrheniusR2 < 0.9) r.Notes.Add($"Arrhenius 线性度 R² = {r.ArrheniusR2:F3} 偏低,Ea 参考价值有限。");
            }
            else
            {
                r.Tier = 0;
            }

            // 附加压级:k(T) 非单调 → 至少降为谨慎;τ 无变化(各组单点反解 k)时
            // 全局 R² 只反映温度结构、不反映动力学曲率,同样不许给「可靠」
            if (r.Notes.Any(n => n.Contains("非单调")) && r.Tier == 0) r.Tier = 1;
            if (r.Groups.All(g => g.DistinctTau < 3) && r.Tier == 0) r.Tier = 1;

            r.Notes.Add("假设:等温稳态 PFR、体积流量恒定" +
                        (template == "pseudo_first_order" ? "、过量组分浓度视为常数(拟一级)" : "") +
                        (template == "second_order" ? "、两股等摩尔进料(A+B)" : "") + "。");
            return r;
        }

        private static double FitK(string template, List<KineticsPoint> pts, double cA0)
        {
            double Sse(double lnk)
            {
                double k = Math.Exp(lnk);
                return pts.Sum(p => Sq(p.X - ModelX(template, k, p.Tau, cA0)));
            }

            // 关键:SSE 对 ln k 在大 k 端是平台(转化率全部饱和到 1,误差不再变化),
            // 盲目黄金分割的两个探针一旦同落平台,打平后的收缩会把真正的谷挤出区间
            // (合成数据回收测试实测踩中:高温组 k 被拟到 1e6)。
            // 因此先粗网格扫描锁定谷的位置,再在邻域内黄金分割精修。
            double lo = Math.Log(1e-6), hi = Math.Log(1e6);
            const int grid = 96;
            double step = (hi - lo) / grid;
            double bestLn = lo, bestF = double.MaxValue;
            for (int i = 0; i <= grid; i++)
            {
                double v = lo + i * step;
                double f = Sse(v);
                if (f < bestF) { bestF = f; bestLn = v; }
            }

            const double gr = 0.6180339887;
            double a = bestLn - step, b = bestLn + step;
            double c = b - gr * (b - a), d = a + gr * (b - a);
            double fc = Sse(c), fd = Sse(d);
            for (int i = 0; i < 200 && (b - a) > 1e-9; i++)
            {
                if (fc < fd) { b = d; d = c; fd = fc; c = b - gr * (b - a); fc = Sse(c); }
                else { a = c; c = d; fc = fd; d = a + gr * (b - a); fd = Sse(d); }
            }
            return Math.Exp((a + b) / 2);
        }

        private static double Sq(double v) => v * v;
    }
}
