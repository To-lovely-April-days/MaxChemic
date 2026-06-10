namespace MicroReactor_ModbusTCP.MpcCore;

/// <summary>
/// 参考轨迹生成器:r(k+1) = β·r(k) + (1-β)·SP,β = exp(-Ts/τ_ref)
/// 让 MPC 追平滑轨迹而非硬目标,避免命令冲击和超调。
/// </summary>
public class ReferenceTrajectory
{
    public double TauRef { get; set; }
    public double Ts { get; set; }
    public double CurrentReference { get; private set; }
    public double Target { get; set; }
    public double Beta => Math.Exp(-Ts / TauRef);
    public ReferenceTrajectory(double tau_ref, double Ts, double initial = 25.0)
    {
        TauRef = tau_ref;
        this.Ts = Ts;
        CurrentReference = initial;
        Target = initial;
    }
    public double Step(double setpoint)
    {
        Target = setpoint;
        double beta = Beta;
        CurrentReference = beta * CurrentReference + (1 - beta) * setpoint;
        return CurrentReference;
    }
    public double[] PredictTrajectory(int N, double setpoint)
    {
        double beta = Beta;
        var traj = new double[N];
        double r = CurrentReference;
        for (int i = 0; i < N; i++)
        {
            r = beta * r + (1 - beta) * setpoint;
            traj[i] = r;
        }
        return traj;
    }
    public void Reset(double initial = 25.0)
    {
        CurrentReference = initial;
        Target = initial;
    }
}
