using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;

namespace MicroReactor_ModbusTCP.MpcCore;

/// <summary>
/// 二次规划求解器:min 0.5 x'Hx + f'x  s.t. Ax ≤ b
/// 对偶激活集法,适用 H 正定 + 小规模约束的 MPC 场景。
/// </summary>
public class QpSolver
{
    public int MaxIterations { get; set; } = 100;
    public double Tolerance { get; set; } = 1e-8;
    public bool Converged { get; private set; }
    public int IterationsUsed { get; private set; }
    public string Status { get; private set; } = "";

    public double[] Solve(double[,] H, double[] f, double[,]? A, double[]? b)
    {
        int n = f.Length;
        var H_m = DenseMatrix.OfArray(H);
        var f_v = DenseVector.OfArray(f);

        Vector<double> x;
        try { x = H_m.Solve(-f_v); }
        catch (Exception ex)
        {
            Converged = false;
            Status = "无约束求解失败: " + ex.Message;
            return Enumerable.Repeat(0.0, n).ToArray();
        }

        if (A == null || b == null || A.GetLength(0) == 0)
        {
            Converged = true; Status = "无约束求解"; IterationsUsed = 0;
            return x.ToArray();
        }

        int m = b.Length;
        var A_m = DenseMatrix.OfArray(A);
        var b_v = DenseVector.OfArray(b);

        var activeSet = new List<int>();
        double[] lambda = new double[m];

        for (int iter = 0; iter < MaxIterations; iter++)
        {
            IterationsUsed = iter;

            var residual = A_m * x - b_v;
            int worstIdx = -1;
            double worstViolation = Tolerance;
            for (int i = 0; i < m; i++)
            {
                if (activeSet.Contains(i)) continue;
                if (residual[i] > worstViolation)
                {
                    worstViolation = residual[i];
                    worstIdx = i;
                }
            }

            if (worstIdx < 0)
            {
                int removeIdx = -1;
                double mostNeg = -Tolerance;
                foreach (int i in activeSet)
                {
                    if (lambda[i] < mostNeg) { mostNeg = lambda[i]; removeIdx = i; }
                }
                if (removeIdx < 0)
                {
                    Converged = true; Status = "收敛";
                    return x.ToArray();
                }
                activeSet.Remove(removeIdx);
                lambda[removeIdx] = 0;
            }
            else
            {
                activeSet.Add(worstIdx);
            }

            int k = activeSet.Count;
            var Aa = DenseMatrix.Create(k, n, 0.0);
            var ba = DenseVector.Create(k, 0.0);
            for (int j = 0; j < k; j++)
            {
                int idx = activeSet[j];
                for (int col = 0; col < n; col++) Aa[j, col] = A_m[idx, col];
                ba[j] = b_v[idx];
            }

            int N = n + k;
            var KKT = DenseMatrix.Create(N, N, 0.0);
            var rhs = DenseVector.Create(N, 0.0);
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++) KKT[i, j] = H_m[i, j];
                rhs[i] = -f_v[i];
            }
            for (int j = 0; j < k; j++)
            {
                for (int i = 0; i < n; i++)
                {
                    KKT[i, n + j] = Aa[j, i];
                    KKT[n + j, i] = Aa[j, i];
                }
                rhs[n + j] = ba[j];
            }

            try
            {
                var sol = KKT.Solve(rhs);
                x = DenseVector.OfArray(sol.SubVector(0, n).ToArray());
                Array.Clear(lambda, 0, lambda.Length);
                for (int j = 0; j < k; j++) lambda[activeSet[j]] = sol[n + j];
            }
            catch (Exception ex)
            {
                Converged = false;
                Status = $"KKT 求解失败 (iter {iter}): {ex.Message}";
                return x.ToArray();
            }
        }

        Converged = false;
        Status = $"达到最大迭代 {MaxIterations}";
        return x.ToArray();
    }
}
