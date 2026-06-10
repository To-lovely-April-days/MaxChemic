using System.IO;
using System.Text.Json;

namespace MicroReactor_ModbusTCP.MpcCore;

/// <summary>
/// 模型库:存储不同流量、不同温区下的 FOPDT 参数,支持流量插值与升降温分离。
/// </summary>
public class MpcModelBank
{
    public class ModelEntry
    {
        public double Flow { get; set; }
        public string Step { get; set; } = "";
        public double K { get; set; }
        public double Tau { get; set; }
        public double L { get; set; }
        public double R2 { get; set; }
        public double CmdCurr { get; set; }
        public bool IsCooldown =>
            Step.Contains("80→60") || Step.Contains("60→40") ||
            Step.Contains("40→25") || Step.Contains("→25") || Step.Contains("80→25");
    }

    public List<ModelEntry> Heating { get; } = new();
    public List<ModelEntry> Cooling { get; } = new();

    // 升降温迟滞:±2℃ 死区内保持上次判断,避免边界抖动。Reset 时清零。
    private bool _lastCooling = false;
    public void ResetCoolingState() => _lastCooling = false;

    public static MpcModelBank LoadFromJson(string filePath)
    {
        var bank = new MpcModelBank();
        var json = File.ReadAllText(filePath);
        using var doc = JsonDocument.Parse(json);
        var models = doc.RootElement.GetProperty("models");
        foreach (var m in models.EnumerateArray())
        {
            var entry = new ModelEntry
            {
                Flow = m.GetProperty("flow").GetDouble(),
                Step = m.GetProperty("step").GetString() ?? "",
                K = m.GetProperty("K").GetDouble(),
                Tau = m.GetProperty("tau").GetDouble(),
                L = m.GetProperty("L").GetDouble(),
                R2 = m.GetProperty("r2").GetDouble(),
                CmdCurr = m.GetProperty("cmd_curr").GetDouble()
            };
            if (entry.IsCooldown) bank.Cooling.Add(entry);
            else bank.Heating.Add(entry);
        }
        return bank;
    }

    /// <summary>
    /// 内置默认模型库。
    /// 升温:2026-05-13 阶跃实验 scipy FOPDT 拟合(R² ≥ 0.983)。
    /// 降温:2026-05-19 修正,K 取与升温一致,τ 为工程估计(升温 τ×~1.3),分温区。
    ///       ⚠ 降温 τ/L 待干净的 80→60→40→25 分段阶跃实验正式辨识替换。
    /// </summary>
    public static MpcModelBank CreateDefault()
    {
        var bank = new MpcModelBank();

        var heatingData = new (double flow, string step, double K, double tau, double L)[]
        {
        (20, "25→40", 0.966,  87.9, 44.0),
        (20, "40→60", 0.966, 112.5, 46.5),
        (20, "60→80", 0.975, 110.8, 47.0),
        (40, "25→40", 0.967,  70.8, 36.1),
        (40, "40→60", 0.975, 101.9, 41.2),
        (40, "60→80", 0.984,  97.1, 45.4),
        (59, "25→40", 0.926,  64.9, 37.4),
        (59, "40→60", 0.953,  91.4, 41.9),
        (59, "60→80", 0.970,  89.0, 46.2),
        };

        var coolingData = new (double flow, string step, double K, double tau, double L)[]
        {
        (20, "80→60", 0.975, 145.0, 50.0),
        (20, "60→40", 0.966, 145.0, 50.0),
        (20, "40→25", 0.966, 115.0, 45.0),
        (40, "80→60", 0.984, 125.0, 45.0),
        (40, "60→40", 0.975, 130.0, 45.0),
        (40, "40→25", 0.967,  90.0, 40.0),
        (59, "80→60", 0.970, 115.0, 50.0),
        (59, "60→40", 0.953, 115.0, 45.0),
        (59, "40→25", 0.926,  85.0, 40.0),
        };

        foreach (var (flow, step, K, tau, L) in heatingData)
            bank.Heating.Add(new ModelEntry { Flow = flow, Step = step, K = K, Tau = tau, L = L, R2 = 0.99 });
        foreach (var (flow, step, K, tau, L) in coolingData)
            bank.Cooling.Add(new ModelEntry { Flow = flow, Step = step, K = K, Tau = tau, L = L, R2 = 0.90 });

        return bank;
    }

    /// <summary>
    /// 按流量、当前温度、目标温度返回 FOPDT 模型。
    /// 升降温分离(±2℃ 迟滞)→ 温区匹配 → 流量线性插值。
    /// </summary>
    public StateSpaceModel GetModel(double flow, double currentTemp, double targetTemp, double Ts)
    {
        double diff = targetTemp - currentTemp;
        bool cooling;
        if (diff > 2.0) cooling = false;
        else if (diff < -2.0) cooling = true;
        else cooling = _lastCooling;
        _lastCooling = cooling;

        var pool = cooling ? Cooling : Heating;
        if (pool.Count == 0)
            throw new InvalidOperationException(cooling ? "Cooling 模型为空" : "Heating 模型为空");

        ModelEntry[] zonedEntries;
        if (cooling)
        {
            string zone = currentTemp >= 60 ? "80→60" : currentTemp >= 40 ? "60→40" : "40→25";
            zonedEntries = pool.Where(e => e.Step == zone).ToArray();
            if (zonedEntries.Length == 0) zonedEntries = pool.ToArray();
        }
        else
        {
            string zone = currentTemp < 40 ? "25→40" : currentTemp < 60 ? "40→60" : "60→80";
            zonedEntries = pool.Where(e => e.Step == zone).ToArray();
            if (zonedEntries.Length == 0) zonedEntries = pool.ToArray();
        }

        var sorted = zonedEntries.OrderBy(e => e.Flow).ToArray();
        if (flow <= sorted[0].Flow) return ToSS(sorted[0], Ts);
        if (flow >= sorted[^1].Flow) return ToSS(sorted[^1], Ts);
        for (int i = 0; i < sorted.Length - 1; i++)
        {
            var lo = sorted[i];
            var hi = sorted[i + 1];
            if (flow >= lo.Flow && flow <= hi.Flow)
            {
                double t = (flow - lo.Flow) / (hi.Flow - lo.Flow);
                double K = lo.K + t * (hi.K - lo.K);
                double tau = lo.Tau + t * (hi.Tau - lo.Tau);
                double L = lo.L + t * (hi.L - lo.L);
                return new StateSpaceModel(K, tau, L, Ts);
            }
        }
        return ToSS(sorted[0], Ts);
    }

    private static StateSpaceModel ToSS(ModelEntry e, double Ts) =>
        new StateSpaceModel(e.K, e.Tau, e.L, Ts);
}
