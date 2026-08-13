using System.Text.Json;

namespace MaxChemical.Mcp.Bridge;

/// <summary>
/// 桥接程序的配置。三个来源,优先级 **命令行 &gt; 环境变量 &gt; appsettings.json**。
///
/// 支持环境变量的原因和 Mcp.History 一样:各家 MCP 客户端的配置里都能给子进程设 env,
/// 这样现场不必去改磁盘上的配置文件,一处配置就能跑起来。
/// </summary>
public sealed class AppConfig
{
    /// <summary>主程序命名管道名。必须与主程序 MaxChemicalMcp:PipeName 一致。</summary>
    public string PipeName { get; private set; } = "MaxChemical.Mcp.v1";

    /// <summary>连接超时(毫秒)。默认给得比较短:连不上要快速返回一句说得清的话,而不是把客户端挂死。</summary>
    public int ConnectTimeoutMs { get; private set; } = 3000;

    public static AppConfig Load(string[] args)
    {
        var cfg = new AppConfig();

        // ① appsettings.json(与 exe 同目录)
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.TryGetProperty("PipeName", out var p) && p.ValueKind == JsonValueKind.String)
                    cfg.PipeName = p.GetString()!;
                if (root.TryGetProperty("ConnectTimeoutMs", out var t) && t.TryGetInt32(out int ms))
                    cfg.ConnectTimeoutMs = ms;
            }
        }
        catch (Exception ex)
        {
            // 配置文件坏了不该让进程起不来,用默认值继续并留一条 stderr
            Program.Log($"读取 appsettings.json 失败,使用默认配置:{ex.Message}");
        }

        // ② 环境变量
        string? envPipe = Environment.GetEnvironmentVariable("MAXCHEMICAL_MCP_PIPE");
        if (!string.IsNullOrWhiteSpace(envPipe)) cfg.PipeName = envPipe.Trim();

        string? envTimeout = Environment.GetEnvironmentVariable("MAXCHEMICAL_MCP_TIMEOUT_MS");
        if (int.TryParse(envTimeout, out int envMs)) cfg.ConnectTimeoutMs = envMs;

        // ③ 命令行
        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--pipe":
                    cfg.PipeName = args[i + 1];
                    break;
                case "--timeout-ms":
                    if (int.TryParse(args[i + 1], out int cliMs)) cfg.ConnectTimeoutMs = cliMs;
                    break;
            }
        }

        // 兜底:超时给一个合理区间,避免配成 0 导致永远连不上
        if (cfg.ConnectTimeoutMs < 200) cfg.ConnectTimeoutMs = 200;
        if (cfg.ConnectTimeoutMs > 60000) cfg.ConnectTimeoutMs = 60000;

        return cfg;
    }
}
