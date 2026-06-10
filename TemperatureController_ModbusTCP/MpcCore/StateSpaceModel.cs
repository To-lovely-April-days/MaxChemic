namespace MicroReactor_ModbusTCP.MpcCore;

/// <summary>
/// FOPDT 模型的离散状态空间表示
///
/// 连续 FOPDT:  τ·dy/dt + y = K·u(t-L)
/// 离散化(采样周期 Ts):
///   y(k+1) = a·y(k) + K·(1-a)·u(k-d)
///   其中:
///     a = exp(-Ts/τ)
///     d = round(L/Ts)        滞后步数(整数)
///
/// 状态向量 x = [y(k), u(k-1), u(k-2), ..., u(k-d)]^T   长度 = 1+d
///
/// 状态空间形式:
///   x(k+1) = A·x(k) + B·u(k)
///   y(k)   = C·x(k)
/// </summary>
public class StateSpaceModel
{
    public double K { get; }
    public double Tau { get; }
    public double L { get; }
    public double Ts { get; }
    public double A_scalar { get; }    // exp(-Ts/τ)
    public double B_scalar { get; }    // K·(1-a)
    public int Delay { get; }          // round(L/Ts)
    public int StateDim { get; }       // 1 + Delay
    public double[,] A { get; }
    public double[] B { get; }
    public double[] C { get; }
    public StateSpaceModel(double K, double tau, double L, double Ts)
    {
        if (tau <= 0) throw new ArgumentException("tau must be > 0");
        if (Ts <= 0) throw new ArgumentException("Ts must be > 0");
        if (L < 0) throw new ArgumentException("L must be >= 0");
        this.K = K;
        this.Tau = tau;
        this.L = L;
        this.Ts = Ts;
        A_scalar = Math.Exp(-Ts / tau);
        B_scalar = K * (1.0 - A_scalar);
        Delay = (int)Math.Round(L / Ts);
        StateDim = 1 + Math.Max(Delay, 1);
        A = new double[StateDim, StateDim];
        B = new double[StateDim];
        C = new double[StateDim];
        // y(k+1) = a*y(k) + B_scalar * u(k-d)
        A[0, 0] = A_scalar;
        if (Delay > 0)
        {
            A[0, Delay] = B_scalar;
        }
        else
        {
            // 无滞后: y(k+1) = a*y(k) + B_scalar * u(k)
            B[0] = B_scalar;
        }
        // u 队列移位: u(k-1) <- u(k), u(k-2) <- u(k-1), ...
        for (int i = 1; i < StateDim - 1; i++)
        {
            A[i + 1, i] = 1.0;
        }
        if (StateDim > 1) B[1] = 1.0;  // u(k) 进入队列首位
        C[0] = 1.0;
    }
    /// <summary>从初始状态 x0 模拟 N 步,给定输入序列 u_seq</summary>
    public double[] Simulate(double[] x0, double[] u_seq)
    {
        if (x0.Length != StateDim) throw new ArgumentException($"x0 length must be {StateDim}");
        var y_out = new double[u_seq.Length];
        var x = (double[])x0.Clone();
        for (int k = 0; k < u_seq.Length; k++)
        {
            // y_pred = C * x
            y_out[k] = x[0];
            // x_next = A*x + B*u
            var x_next = new double[StateDim];
            for (int i = 0; i < StateDim; i++)
            {
                double sum = 0;
                for (int j = 0; j < StateDim; j++)
                    sum += A[i, j] * x[j];
                x_next[i] = sum + B[i] * u_seq[k];
            }
            x = x_next;
        }
        return y_out;
    }
    public override string ToString() =>
        $"FOPDT(K={K:F3}, τ={Tau:F1}s, L={L:F1}s) → SS(Ts={Ts}s, d={Delay}, dim={StateDim})";
}
