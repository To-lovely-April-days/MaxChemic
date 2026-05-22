using MathNet.Numerics.LinearAlgebra;
using MathNet.Numerics.LinearAlgebra.Double;

namespace MicroReactor_ModbusTCP.MpcCore;

/// <summary>
/// 串级 MPC 控制器(与 MpcWorkbench 验证版严格一致)
/// 外环 MPC 输出 jacket_SP,内环由循环器自带 PID 把夹套追到 jacket_SP。
/// 反馈:夹套实测;命令:夹套设定值;滞后:只算夹套→物料(~40s)。
///
/// 已验证配置(2026-05-19):
/// - 升温模型 τ/L 已修正;降温模型分温区
/// - SP_BOOST 在外层 Runner 已禁用
/// - Anti-Overshoot 刹车禁用(#if false)
/// - _trackingIntegral 半禁用(仅残值衰减)
/// </summary>
public class CascadeMpcController
{
    public MpcModelBank ModelBank { get; }
    public MpcParameters Parameters { get; private set; }
    public DisturbanceObserver Dob { get; }
    public ReferenceTrajectory RefTraj { get; private set; }
    public double RMultiplier { get; set; } = 1.0;

    private readonly QpSolver _solver = new();
    private readonly Queue<double> _jacketHistory = new();
    private double _jacketSpPrev = 25.0;
    private double _yPredictedNext = 25.0;
    private double _lastMeasuredJacket = 25.0;
    private double _jacketSlewLimitPerStep;

    private double _trackingIntegral = 0;
    private const double TRACKING_INTEGRAL_LIMIT = 15.0;

    public double LastSolveTimeMs { get; private set; }
    public bool LastSolveConverged { get; private set; }
    public string LastSolveStatus { get; private set; } = "";
    public double LastDisturbance => Dob.Estimate;
    public double LastReference => RefTraj.CurrentReference;
    public double LastJacketSp => _jacketSpPrev;

    public CascadeMpcController(MpcModelBank bank, MpcParameters parameters)
    {
        ModelBank = bank;
        Parameters = parameters;
        Dob = new DisturbanceObserver(parameters.DobGain);
        RefTraj = new ReferenceTrajectory(60.0 * parameters.TauRefFactorBase, parameters.Ts, 25.0);
        _jacketSlewLimitPerStep = parameters.RateMax * parameters.Ts / 60.0;
    }

    public void UpdateParameters(MpcParameters newParams)
    {
        Parameters = newParams;
        Dob.Gain = newParams.DobGain;
        RefTraj.Ts = newParams.Ts;
        _jacketSlewLimitPerStep = newParams.RateMax * newParams.Ts / 60.0;
    }

    public void Reset(double initialJacketSp, double initialY)
    {
        _jacketHistory.Clear();
        _jacketSpPrev = initialJacketSp;
        _yPredictedNext = initialY;
        _lastMeasuredJacket = initialJacketSp;
        _trackingIntegral = 0;
        Dob.Reset();
        RefTraj.Reset(initialY);
        ModelBank.ResetCoolingState();
    }

    public double Step(double materialSp, double materialT, double jacketT, double flow)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        double error = materialSp - materialT;
        if (Math.Abs(error) < 8.0)
            Dob.Update(materialT, _yPredictedNext);

        var model = ModelBank.GetModel(flow, materialT, materialSp, Parameters.Ts);
        int d = model.Delay;
        int n = model.StateDim;

        double tau_ref = Parameters.GetAdaptiveTauRef(model.Tau, error);
        RefTraj.TauRef = tau_ref;
        RefTraj.Step(materialSp);
        var refTraj = RefTraj.PredictTrajectory(Parameters.Np, materialSp);

        // tracking integral 半禁用:仅残值衰减,不注入新增量。
        _trackingIntegral *= 0.95;
        double total_disturbance = Dob.Estimate + _trackingIntegral;

        while (_jacketHistory.Count < d) _jacketHistory.Enqueue(jacketT);
        while (_jacketHistory.Count > d) _jacketHistory.Dequeue();

        var x = DenseVector.Create(n, 0);
        x[0] = materialT;
        var jacketArr = _jacketHistory.ToArray();
        for (int i = 1; i < n && i - 1 < jacketArr.Length; i++)
            x[i] = jacketArr[jacketArr.Length - i];

        double R_adaptive = Parameters.GetAdaptiveR(flow, error) * RMultiplier;
        var (H, f) = BuildQpCost(model, x, refTraj, _jacketSpPrev, total_disturbance, R_adaptive);
        var (A, b) = BuildQpConstraints(_jacketSpPrev, jacketT);

        var deltaJacket = _solver.Solve(H, f, A, b);
        LastSolveTimeMs = sw.Elapsed.TotalMilliseconds;
        LastSolveConverged = _solver.Converged;
        LastSolveStatus = _solver.Status;

        double dj0 = LastSolveConverged ? deltaJacket[0] : 0;
        dj0 = Math.Max(-_jacketSlewLimitPerStep, Math.Min(_jacketSlewLimitPerStep, dj0));

        // 三层死区:误差越小,Δu 阈值越大
        if (Math.Abs(error) < 0.3)
        {
            if (Math.Abs(dj0) < 0.5) dj0 = 0;
        }
        else if (Math.Abs(error) < 0.8)
        {
            if (Math.Abs(dj0) < 0.3) dj0 = 0;
        }

        // === Anti-Overshoot 刹车:禁用(2026-05-19)===
#if false
        double K_steady = model.K;
        double jacketSteadyState = materialSp / K_steady;
        double predictedMatSS = K_steady * jacketT;
        if (error > -3.0 && error < 10.0 && predictedMatSS > materialSp + 0.3)
        {
            double brakeTarget = jacketSteadyState;
            if (_jacketSpPrev + dj0 > brakeTarget)
            {
                dj0 = brakeTarget - _jacketSpPrev;
                dj0 = Math.Max(-3.0 * _jacketSlewLimitPerStep, dj0);
            }
        }
#endif
        double jacketSpNew = _jacketSpPrev + dj0;
        jacketSpNew = Math.Max(Parameters.UMin, Math.Min(Parameters.UMax, jacketSpNew));

        double jacket_eff_real = _jacketHistory.Count > 0 ? _jacketHistory.Peek() : jacketT;
        _yPredictedNext = model.A_scalar * materialT + model.B_scalar * jacket_eff_real + total_disturbance;

        _jacketHistory.Enqueue(jacketT);
        if (_jacketHistory.Count > d + 2) _jacketHistory.Dequeue();
        _jacketSpPrev = jacketSpNew;
        _lastMeasuredJacket = jacketT;

        return jacketSpNew;
    }

    public double TrackingIntegral => _trackingIntegral;

    private (double[,] H, double[] f) BuildQpCost(
        StateSpaceModel m, Vector<double> x0,
        double[] refTraj, double jacketSp_prev, double disturbance,
        double R_adaptive)
    {
        int Np = Parameters.Np, Nc = Parameters.Nc, n = m.StateDim;
        var A_m = DenseMatrix.OfArray(m.A);
        var B_v = DenseVector.OfArray(m.B);
        var C_v = DenseVector.OfArray(m.C);

        var Apow = new Matrix<double>[Np + 1];
        Apow[0] = DenseMatrix.CreateIdentity(n);
        for (int k = 1; k <= Np; k++) Apow[k] = Apow[k - 1] * A_m;

        var alpha = new double[Np];
        for (int k = 0; k < Np; k++)
        {
            double natural = C_v * (Apow[k + 1] * x0);
            double from_prev = 0;
            for (int i = 0; i <= k; i++)
                from_prev += C_v * (Apow[k - i] * B_v) * jacketSp_prev;
            double disturbance_effect = disturbance * (1 - Math.Pow(m.A_scalar, k + 1)) / (1 - m.A_scalar);
            alpha[k] = natural + from_prev + disturbance_effect;
        }

        var beta = new double[Np, Nc];
        for (int k = 0; k < Np; k++)
        {
            for (int j = 0; j < Nc && j <= k; j++)
            {
                double sum = 0;
                for (int i = j; i <= k; i++)
                    sum += C_v * (Apow[k - i] * B_v);
                beta[k, j] = sum;
            }
        }

        double Q = Parameters.Q, R = R_adaptive;
        var H = new double[Nc, Nc];
        var f = new double[Nc];
        for (int i = 0; i < Nc; i++)
        {
            for (int j = 0; j < Nc; j++)
            {
                double sum = 0;
                for (int k = 0; k < Np; k++)
                    sum += Q * beta[k, i] * beta[k, j];
                H[i, j] = 2 * sum;
            }
            H[i, i] += 2 * R;

            double fsum = 0;
            for (int k = 0; k < Np; k++)
                fsum += Q * beta[k, i] * (refTraj[k] - alpha[k]);
            f[i] = -2 * fsum;
        }
        return (H, f);
    }

    private (double[,] A, double[] b) BuildQpConstraints(double jacketSp_prev, double currentJacket)
    {
        int Nc = Parameters.Nc;
        double slewStep = _jacketSlewLimitPerStep;
        double uMin = Parameters.UMin, uMax = Parameters.UMax;

        int nCon = 4 * Nc;
        var A = new double[nCon, Nc];
        var b = new double[nCon];
        int row = 0;

        for (int j = 0; j < Nc; j++)
        {
            A[row, j] = 1; b[row] = slewStep; row++;
            A[row, j] = -1; b[row] = slewStep; row++;
        }
        for (int j = 0; j < Nc; j++)
        {
            for (int i = 0; i <= j; i++) A[row, i] = 1;
            b[row] = uMax - jacketSp_prev; row++;
            for (int i = 0; i <= j; i++) A[row, i] = -1;
            b[row] = jacketSp_prev - uMin; row++;
        }
        return (A, b);
    }
}
