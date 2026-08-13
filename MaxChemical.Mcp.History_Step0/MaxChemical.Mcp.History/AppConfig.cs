using System.Text.Json;

namespace MaxChemical.Mcp.History;

/// <summary>
/// 运行配置。按优先级从三处取:命令行 &gt; 环境变量 &gt; appsettings.json。
///
/// 之所以支持环境变量:WorkBuddy 的 MCP 配置里可以给子进程设 env,
/// 这样连接串不用落在磁盘上的配置文件里,也方便一台机器上跑多套。
/// </summary>
public sealed class AppConfig
{
    public string ConnectionString { get; init; } = "";

    /// <summary>单次查询最多返回多少行。防止一条 SELECT 把几十万行塞进模型上下文。</summary>
    public int MaxRows { get; init; } = 200;

    /// <summary>单次查询超时(秒)。</summary>
    public int CommandTimeoutSeconds { get; init; } = 15;

    /// <summary>是否开放受限只读 SQL 工具。默认开;客户不放心可以关掉。</summary>
    public bool EnableRawSql { get; init; } = true;

    public static AppConfig Load(string[] args)
    {
        string? conn = null;
        int maxRows = 200, timeout = 15;
        bool rawSql = true;

        // 1) appsettings.json(与 exe 同目录)
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;
                if (root.TryGetProperty("ConnectionString", out var c)) conn = c.GetString();
                if (root.TryGetProperty("MaxRows", out var m) && m.TryGetInt32(out var mv)) maxRows = mv;
                if (root.TryGetProperty("CommandTimeoutSeconds", out var t) && t.TryGetInt32(out var tv)) timeout = tv;
                if (root.TryGetProperty("EnableRawSql", out var r)) rawSql = r.GetBoolean();
            }
        }
        catch (Exception ex)
        {
            Program.Log($"读取 appsettings.json 失败,忽略:{ex.Message}");
        }

        // 2) 环境变量覆盖
        conn = Environment.GetEnvironmentVariable("MAXCHEMICAL_MCP_CONNSTR") ?? conn;
        if (int.TryParse(Environment.GetEnvironmentVariable("MAXCHEMICAL_MCP_MAXROWS"), out var envRows))
            maxRows = envRows;
        if (bool.TryParse(Environment.GetEnvironmentVariable("MAXCHEMICAL_MCP_RAWSQL"), out var envRaw))
            rawSql = envRaw;

        // 3) 命令行覆盖(最高)
        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--conn": conn = args[i + 1]; break;
                case "--max-rows": if (int.TryParse(args[i + 1], out var a)) maxRows = a; break;
                case "--timeout": if (int.TryParse(args[i + 1], out var b)) timeout = b; break;
                case "--raw-sql": if (bool.TryParse(args[i + 1], out var c2)) rawSql = c2; break;
            }
        }

        if (string.IsNullOrWhiteSpace(conn))
        {
            // 兜底只是为了让程序能起来并给出一条能看懂的报错,不是推荐配置。
            // 注意这里**故意不带默认口令** —— 主程序里那个硬编码的 root/123456 不该在这里复制一遍。
            // AllowPublicKeyRetrieval 不能少:MySQL 8.0 默认 caching_sha2_password,
            // 客户端要先取 RSA 公钥才能加密口令,缺了它连不上而且报错看不出原因。
            conn = "Server=localhost;Database=maxchemical;CharSet=utf8mb4;SslMode=None;AllowPublicKeyRetrieval=True;";
            Program.Log("未配置连接串,使用无凭据的兜底值。请在 appsettings.json 或 MAXCHEMICAL_MCP_CONNSTR 里配置只读账号。");
        }

        return new AppConfig
        {
            ConnectionString = conn!,
            MaxRows = Math.Clamp(maxRows, 1, 5000),
            CommandTimeoutSeconds = Math.Clamp(timeout, 1, 300),
            EnableRawSql = rawSql
        };
    }

    /// <summary>给日志用的目标描述。**必须隐藏口令** —— 日志会被贴进工单。</summary>
    public string DescribeTarget()
    {
        var parts = ConnectionString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var safe = parts.Where(p =>
        {
            string k = p.Split('=')[0].Trim().ToLowerInvariant();
            return k is "server" or "host" or "database" or "port" or "uid" or "user id";
        });
        return string.Join(";", safe);
    }
}
