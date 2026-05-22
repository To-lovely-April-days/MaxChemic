namespace MicroReactor_ModbusTCP.MpcCore;

/// <summary>MPC 整定参数</summary>
public class MpcParameters
{
    public double Ts { get; set; } = 10.0;
    public int Np { get; set; } = 40;
    public int Nc { get; set; } = 5;
    public double Q { get; set; } = 1.0;

    /// <summary>R 基础值(会随流量+距离自适应调整)</summary>
    public double RBase { get; set; } = 0.8;

    public double UMin { get; set; } = 5.0;
    public double UMax { get; set; } = 90.0;
    public double RateMax { get; set; } = 5.0;     // ℃/min

    public double DobGain { get; set; } = 0.08;
    public double TauRefFactorBase { get; set; } = 1.6;

    /// <summary>
    /// 根据流量 + 当前误差自适应 R
    /// 接近 SP 时 R 不能放大(会导致不敢调命令产生稳态偏差)。
    /// 防超调靠"参考轨迹放慢"和"Δu限幅",不靠加大 R。
    /// </summary>
    public double GetAdaptiveR(double flow, double error)
    {
        double r_base;
        if (flow < 30) r_base = 0.5;
        else if (flow < 50) r_base = 0.3;
        else r_base = 0.4;

        double absErr = Math.Abs(error);
        if (absErr > 15) r_base *= 3.0;
        else if (absErr > 5) r_base *= 1.5;
        else if (absErr < 2.0) r_base *= 4.0;
        return r_base;
    }

    public double GetAdaptiveTauRef(double tau, double error)
    {
        double absErr = Math.Abs(error);
        if (absErr < 3) return tau * 2.5;
        if (absErr < 8) return tau * 2.0;
        return tau * 1.5;
    }

    /// <summary>
    /// 默认参数 —— 与 MpcWorkbench 验证版严格一致。
    /// Ts=10, Np=50, Nc=3, RateMax=2.5, DobGain=0.05, TauRefFactorBase=1.8
    /// </summary>
    public static MpcParameters Default() => new MpcParameters
    {
        Ts = 10, Np = 50, Nc = 3,
        Q = 1.0, RBase = 0.8,
        RateMax = 2.5,
        UMin = 5, UMax = 90,
        DobGain = 0.05,
        TauRefFactorBase = 1.8,
    };
}
