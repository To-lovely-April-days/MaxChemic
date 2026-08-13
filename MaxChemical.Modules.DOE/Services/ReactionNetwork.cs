using System;
using System.Collections.Generic;
using System.Linq;

namespace MaxChemical.Modules.DOE.Services
{
    /// <summary>网络拟合的一个观测点:t 时刻某角色物种的浓度。</summary>
    public sealed class NetworkPoint
    {
        public double T { get; set; }        // min
        public int Role { get; set; }        // 角色下标(模板物种顺序)
        public double C { get; set; }        // mol/L
    }

    public sealed class NetworkFitResult
    {
        public string Template { get; set; } = "";
        public double[] K { get; set; } = Array.Empty<double>();
        public double GlobalR2 { get; set; }
        public Dictionary<int, double> R2ByRole { get; } = new();
        public int N { get; set; }
        public double TMax { get; set; }
        public List<string> Notes { get; } = new();
        public int Tier { get; set; }   // 0 可靠 / 1 谨慎 / 2 拒绝
    }

    /// <summary>
    /// 多步反应网络拟合(机理层 V2,纯函数无依赖):
    /// 内置机理模板 + RK4 定步长积分 + ln k 空间 Nelder-Mead 多起点最小二乘。
    /// 假设等温、恒容;t 在流动化学下即停留时间。质量分级是硬规则,AI 只解读。
    /// </summary>
    public static class ReactionNetwork
    {
        /// <summary>模板的角色物种名(顺序即状态向量顺序)。</summary>
        public static string[] Roles(string template) => template switch
        {
            "consecutive" => new[] { "A", "B", "C" },          // A→B→C
            "parallel" => new[] { "A", "B", "C" },             // A→B, A→C
            "a_plus_b" => new[] { "A", "B", "C" },             // A+B→C
            "abc_over" => new[] { "A", "B", "C", "D" },        // A+B→C→D(过反应)
            "reversible" => new[] { "A", "B" },                // A⇌B
            _ => Array.Empty<string>()
        };

        public static int ParamCount(string template) => template == "a_plus_b" ? 1 : 2;

        public static string Describe(string template) => template switch
        {
            "consecutive" => "连串 A→B→C(k1,k2)",
            "parallel" => "平行 A→B 与 A→C(k1,k2)",
            "a_plus_b" => "二级 A+B→C(k1)",
            "abc_over" => "A+B→C→D 过反应(k1,k2)",
            "reversible" => "可逆 A⇌B(k正,k逆)",
            _ => template
        };

        private static void Rhs(string template, double[] y, double[] k, double[] dy)
        {
            switch (template)
            {
                case "consecutive":
                    dy[0] = -k[0] * y[0];
                    dy[1] = k[0] * y[0] - k[1] * y[1];
                    dy[2] = k[1] * y[1];
                    break;
                case "parallel":
                    dy[0] = -(k[0] + k[1]) * y[0];
                    dy[1] = k[0] * y[0];
                    dy[2] = k[1] * y[0];
                    break;
                case "a_plus_b":
                {
                    double r = k[0] * y[0] * y[1];
                    dy[0] = -r; dy[1] = -r; dy[2] = r;
                    break;
                }
                case "abc_over":
                {
                    double r1 = k[0] * y[0] * y[1];
                    double r2 = k[1] * y[2];
                    dy[0] = -r1; dy[1] = -r1; dy[2] = r1 - r2; dy[3] = r2;
                    break;
                }
                case "reversible":
                    dy[0] = -k[0] * y[0] + k[1] * y[1];
                    dy[1] = k[0] * y[0] - k[1] * y[1];
                    break;
            }
        }

        /// <summary>RK4 定步长积分,返回等距网格轨迹(含 t=0)。</summary>
        public static List<(double T, double[] Y)> Simulate(string template, double[] k, double[] y0, double tEnd, int steps = 2000)
        {
            var traj = new List<(double, double[])>(steps + 1);
            int n = y0.Length;
            var y = (double[])y0.Clone();
            traj.Add((0, (double[])y.Clone()));
            double h = tEnd / steps;
            var k1 = new double[n]; var k2 = new double[n]; var k3 = new double[n]; var k4 = new double[n]; var tmp = new double[n];
            for (int i = 1; i <= steps; i++)
            {
                Rhs(template, y, k, k1);
                for (int j = 0; j < n; j++) tmp[j] = y[j] + h / 2 * k1[j];
                Rhs(template, tmp, k, k2);
                for (int j = 0; j < n; j++) tmp[j] = y[j] + h / 2 * k2[j];
                Rhs(template, tmp, k, k3);
                for (int j = 0; j < n; j++) tmp[j] = y[j] + h * k3[j];
                Rhs(template, tmp, k, k4);
                for (int j = 0; j < n; j++)
                    y[j] = Math.Max(0, y[j] + h / 6 * (k1[j] + 2 * k2[j] + 2 * k3[j] + k4[j]));
                traj.Add((i * h, (double[])y.Clone()));
            }
            return traj;
        }

        /// <summary>网格轨迹上线性插值取某时刻状态。</summary>
        public static double[] Interp(List<(double T, double[] Y)> traj, double t)
        {
            if (t <= 0) return traj[0].Y;
            double tEnd = traj[traj.Count - 1].T;
            if (t >= tEnd) return traj[traj.Count - 1].Y;
            double h = tEnd / (traj.Count - 1);
            int i = Math.Min(traj.Count - 2, (int)(t / h));
            double f = (t - traj[i].T) / h;
            var a = traj[i].Y; var b = traj[i + 1].Y;
            var y = new double[a.Length];
            for (int j = 0; j < a.Length; j++) y[j] = a[j] + f * (b[j] - a[j]);
            return y;
        }

        public static NetworkFitResult Fit(string template, List<NetworkPoint> pts, double[] y0)
        {
            var r = new NetworkFitResult { Template = template };
            int dim = ParamCount(template);
            var roles = Roles(template);
            if (roles.Length == 0) { r.Tier = 2; r.Notes.Add($"未知模板 {template}。"); return r; }
            var data = (pts ?? new List<NetworkPoint>()).Where(p => p.T >= 0 && p.C >= 0 && p.Role >= 0 && p.Role < roles.Length).ToList();
            r.N = data.Count;
            if (data.Count < 2 + 2 * dim)
            {
                r.Tier = 2;
                r.Notes.Add($"有效取样点只有 {data.Count} 个(至少 {2 + 2 * dim} 个),不足以拟合 {dim} 个速率常数。");
                return r;
            }
            r.TMax = Math.Max(1e-6, data.Max(p => p.T));

            // 逐物种权重(1/max²)平衡浓度量级差异
            var wByRole = new Dictionary<int, double>();
            foreach (var g in data.GroupBy(p => p.Role))
            {
                double m = Math.Max(g.Max(p => p.C), 1e-9);
                wByRole[g.Key] = 1.0 / (m * m);
            }

            double Sse(double[] lnk)
            {
                var kk = lnk.Select(Math.Exp).ToArray();
                var traj = Simulate(template, kk, y0, r.TMax * 1.02);
                double s = 0;
                foreach (var p in data)
                {
                    double model = Interp(traj, p.T)[p.Role];
                    s += wByRole[p.Role] * (p.C - model) * (p.C - model);
                }
                return double.IsNaN(s) || double.IsInfinity(s) ? 1e30 : s;
            }

            // 多起点 Nelder-Mead(ln k 空间)
            double best = double.MaxValue;
            double[] bestLnk = null;
            foreach (double start in new[] { -2.0, 0.0, 2.0 })
            {
                var x0 = Enumerable.Repeat(start, dim).ToArray();
                var (x, f) = NelderMead(Sse, x0);
                if (f < best) { best = f; bestLnk = x; }
            }
            r.K = bestLnk.Select(Math.Exp).ToArray();

            // 拟合优度(未加权口径,便于解读)
            var finalTraj = Simulate(template, r.K, y0, r.TMax * 1.02);
            double sseRaw = 0, sstRaw = 0;
            foreach (var g in data.GroupBy(p => p.Role))
            {
                double mean = g.Average(p => p.C);
                double sse = g.Sum(p => Sq(p.C - Interp(finalTraj, p.T)[g.Key]));
                double sst = g.Sum(p => Sq(p.C - mean));
                sseRaw += sse; sstRaw += sst;
                r.R2ByRole[g.Key] = sst > 1e-12 ? 1 - sse / sst : 1;
            }
            r.GlobalR2 = sstRaw > 1e-12 ? 1 - sseRaw / sstRaw : 1;

            // 质量闸门
            if (r.GlobalR2 < 0.6)
            {
                r.Tier = 2;
                r.Notes.Add($"整体 R² = {r.GlobalR2:F3},低于 0.6:该机理网络解释不了数据,拒绝据此仿真。可能是机理不对、物种映射错、或非等温。");
            }
            else if (r.GlobalR2 < 0.85)
            {
                r.Tier = 1;
                r.Notes.Add($"整体 R² = {r.GlobalR2:F3},拟合一般,仿真结果仅供参考。");
            }
            int roleKinds = data.Select(p => p.Role).Distinct().Count();
            if (dim >= 2 && roleKinds < 2)
                r.Notes.Add("只有一个物种的数据却要拟两个速率常数,参数可辨识性差(k1/k2 可能互相补偿),建议补测中间体或产物浓度。");
            r.Notes.Add("假设:等温、恒容;流动化学下 t 即停留时间。");
            return r;
        }

        private static (double[] X, double F) NelderMead(Func<double[], double> f, double[] x0)
        {
            int n = x0.Length;
            var simplex = new List<double[]> { (double[])x0.Clone() };
            for (int i = 0; i < n; i++)
            {
                var v = (double[])x0.Clone();
                v[i] += 0.8;
                simplex.Add(v);
            }
            var fv = simplex.Select(f).ToList();

            for (int iter = 0; iter < 600; iter++)
            {
                var order = Enumerable.Range(0, simplex.Count).OrderBy(i => fv[i]).ToList();
                simplex = order.Select(i => simplex[i]).ToList();
                fv = order.Select(i => fv[i]).ToList();
                if (Math.Abs(fv[fv.Count - 1] - fv[0]) < 1e-12) break;

                var centroid = new double[n];
                for (int i = 0; i < simplex.Count - 1; i++)
                    for (int j = 0; j < n; j++) centroid[j] += simplex[i][j] / (simplex.Count - 1);

                double[] Combine(double coef)
                {
                    var v = new double[n];
                    for (int j = 0; j < n; j++) v[j] = centroid[j] + coef * (simplex[simplex.Count - 1][j] - centroid[j]);
                    return v;
                }

                var xr = Combine(-1.0); double fr = f(xr);
                if (fr < fv[0])
                {
                    var xe = Combine(-2.0); double fe = f(xe);
                    if (fe < fr) { simplex[simplex.Count - 1] = xe; fv[fv.Count - 1] = fe; }
                    else { simplex[simplex.Count - 1] = xr; fv[fv.Count - 1] = fr; }
                }
                else if (fr < fv[fv.Count - 2])
                {
                    simplex[simplex.Count - 1] = xr; fv[fv.Count - 1] = fr;
                }
                else
                {
                    var xc = Combine(0.5); double fc = f(xc);
                    if (fc < fv[fv.Count - 1]) { simplex[simplex.Count - 1] = xc; fv[fv.Count - 1] = fc; }
                    else
                    {
                        for (int i = 1; i < simplex.Count; i++)
                        {
                            for (int j = 0; j < n; j++) simplex[i][j] = simplex[0][j] + 0.5 * (simplex[i][j] - simplex[0][j]);
                            fv[i] = f(simplex[i]);
                        }
                    }
                }
            }
            int bi = fv.IndexOf(fv.Min());
            return (simplex[bi], fv[bi]);
        }

        private static double Sq(double v) => v * v;
    }
}
