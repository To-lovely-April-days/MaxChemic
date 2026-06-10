namespace MicroReactor_ModbusTCP.MpcCore;

/// <summary>
/// 扰动观测器 (Disturbance Observer, DOB)
/// 用一阶低通把"实测 y - 模型预测 y"作为等效扰动 ŵ 跟踪起来。
/// MPC 把 ŵ 加到每步预测,稳态误差自动归零(等价积分作用,但更稳)。
/// </summary>
public class DisturbanceObserver
{
    public double Gain { get; set; }
    public double Estimate { get; private set; }
    public double LastPredictionError { get; private set; }
    public DisturbanceObserver(double gain = 0.08)
    {
        if (gain <= 0 || gain >= 1)
            throw new ArgumentException("DOB gain must be in (0, 1)");
        Gain = gain;
        Estimate = 0;
    }
    public void Update(double y_measured, double y_predicted)
    {
        LastPredictionError = y_measured - y_predicted;
        Estimate += Gain * LastPredictionError;
        if (Estimate > 20) Estimate = 20;
        if (Estimate < -20) Estimate = -20;
    }
    public void Reset() { Estimate = 0; LastPredictionError = 0; }
}
