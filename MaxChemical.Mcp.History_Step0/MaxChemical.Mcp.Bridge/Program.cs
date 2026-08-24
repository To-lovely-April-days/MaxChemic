using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using History = MaxChemical.Mcp.History;

namespace MaxChemical.Mcp.Bridge;

/// <summary>
/// MaxChemical 统一 MCP Server(对外一个入口,对内两条链路)。
///
/// ── 架构 ────────────────────────────────────────────────────────────
///                        ┌── 实时工具(22 项)──命名管道──▶ MaxChemical 主程序
/// AI 客户端 ──stdio──▶ 本进程 ┤
///                        └── 历史工具(8 项)───直连 MySQL(只读账号)
///
/// 对外只暴露一个 MCP Server;内部两条链路完全独立:
///   · 实时工具:纯协议转发,清单与执行都来自主程序(经 McpToolPolicy 白名单),
///     主程序没开时返回一句可读的说明;
///   · 历史工具:引用 MaxChemical.Mcp.History 的 ToolRegistry 在本进程内直接执行,
///     主程序开不开都能用。
/// 两边互不依赖:数据库连不上不影响实时查询,主程序没开不影响历史查询。
///
/// (MaxChemical.Mcp.History.exe 自身仍可独立运行,想分开挂就分开挂——
///  本进程只是引用了它的核心类,没有改变它。)
///
/// ── stdout 只能有 JSON-RPC ──────────────────────────────────────────
/// 任何一句调试输出都会让客户端解析失败并断开,而报错通常只是一句
/// 没有信息量的 "server disconnected"。所有日志一律走 stderr(见 Log),
/// stdout 只由 Send 写。
/// </summary>
public static class Program
{
    private static readonly string[] SupportedProtocolVersions =
    {
        "2025-06-18", "2025-03-26", "2024-11-05"
    };

    private const string DefaultProtocolVersion = "2024-11-05";
    private const string ServerName = "maxchemical";
    private const string ServerVersion = "0.2.0";

    /// <summary>主程序侧管道协议版本。不一致时给出可读提示,而不是行为诡异。</summary>
    private const int ExpectedPipeProtocol = 1;

    private static StreamWriter _stdout = null!;
    private static PipeClient _pipe = null!;

    /// <summary>历史工具注册表(直连 MySQL,本进程内执行)。</summary>
    private static History.ToolRegistry _historyTools = null!;

    /// <summary>历史工具名集合:tools/call 按它路由——命中走本地,其余走管道。</summary>
    private static readonly HashSet<string> _historyToolNames = new(StringComparer.Ordinal);

    /// <summary>
    /// 最近一次成功取到的实时工具清单。主程序临时不可用时用它兜底,
    /// 免得客户端把工具清单刷成空、之后再也不重试。(历史工具不需要缓存,
    /// 它们在本进程内,永远列得出来。)
    /// </summary>
    private static JsonArray? _cachedPipeTools;

    public static async Task<int> Main(string[] args)
    {
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        _stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = false
        };

        var config = AppConfig.Load(args);
        _pipe = new PipeClient(config.PipeName, config.ConnectTimeoutMs);

        // 历史链路:History.AppConfig 读同一目录的 appsettings.json(ConnectionString 等键)
        // 与环境变量 MAXCHEMICAL_MCP_CONNSTR,优先级与它独立运行时完全一致
        var historyConfig = History.AppConfig.Load(args);
        _historyTools = new History.ToolRegistry(new History.Database(historyConfig));
        foreach (var t in _historyTools.DescribeAll())
        {
            string? n = (t as JsonObject)?["name"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(n)) _historyToolNames.Add(n);
        }

        Log($"{ServerName} v{ServerVersion} 启动(统一入口:实时经管道 + 历史直连库)");
        Log($"实时链路:管道 {config.PipeName}(连接超时 {config.ConnectTimeoutMs} ms)");
        Log($"历史链路:{historyConfig.DescribeTarget()},工具 {_historyToolNames.Count} 项");

        // 探一次实时链路,但**探不通也不退出**——MCP 客户端遇到子进程立刻退出
        // 只会显示一句无信息量的 disconnected。历史链路的连通性留到首次调用时
        // 由 ToolRegistry 给出模型可读的错误,这里不发真实查询。
        await ProbePipeAsync();

        string? line;
        while ((line = await stdin.ReadLineAsync()) != null)
        {
            if (line.Length == 0) continue;

            JsonNode? request;
            try
            {
                request = JsonNode.Parse(line);
            }
            catch (JsonException ex)
            {
                Log($"收到无法解析的消息:{ex.Message}");
                SendError(null, -32700, "Parse error");
                continue;
            }
            if (request is null) continue;

            try
            {
                await HandleAsync(request);
            }
            catch (Exception ex)
            {
                Log($"处理消息时异常:{ex}");
                SendError(request["id"]?.DeepClone(), -32603, "Internal error: " + ex.Message);
            }
        }

        Log("stdin 已关闭,退出");
        _pipe.Dispose();
        return 0;
    }

    private static async Task ProbePipeAsync()
    {
        try
        {
            var hello = await _pipe.RequestAsync("hello");
            if (hello["ok"]?.GetValue<bool>() == true)
            {
                int proto = hello["protocolVersion"]?.GetValue<int>() ?? 0;
                string mode = hello["mode"]?.GetValue<string>() ?? "?";
                int count = hello["toolCount"]?.GetValue<int>() ?? 0;

                Log($"主程序已连接:管道协议 v{proto},模式 {mode},实时工具 {count} 项");

                if (proto != ExpectedPipeProtocol)
                    Log($"警告:管道协议版本不一致(本程序期望 v{ExpectedPipeProtocol},主程序为 v{proto})," +
                        "请确认两侧是同一次发布的产物。");
            }
            else
            {
                Log("主程序应答异常:" + (hello["error"]?.GetValue<string>() ?? "(无错误信息)"));
            }
        }
        catch (McpBridgeException ex)
        {
            Log("主程序暂时连不上:" + ex.Message);
            Log("实时工具暂不可用;主程序起来之后,下一次调用会自动接上。历史工具不受影响。");
        }
    }

    private static async Task HandleAsync(JsonNode request)
    {
        string? method = request["method"]?.GetValue<string>();
        JsonNode? id = request["id"]?.DeepClone();
        JsonNode? prms = request["params"];

        bool isNotification = request["id"] is null;

        switch (method)
        {
            case "initialize":
            {
                string? asked = prms?["protocolVersion"]?.GetValue<string>();
                string version = asked is not null && SupportedProtocolVersions.Contains(asked)
                    ? asked
                    : DefaultProtocolVersion;

                Log($"initialize:客户端请求 {asked ?? "(未指定)"},应答 {version}");

                Send(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["result"] = new JsonObject
                    {
                        ["protocolVersion"] = version,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = ServerName,
                            ["version"] = ServerVersion
                        },
                        ["instructions"] =
                            "这是 MaxChemical 化工实验平台的统一接口,工具分两类:" +
                            "【实时类】设备状态与实时读数、流程状态、DOE 进度、统计分析、模型预测、动力学、设计预览——" +
                            "数据来自正在运行的主程序;工具返回「连不上主程序」时说明 MaxChemical 没有运行,请如实告知用户,不要臆测数值。" +
                            "【历史类】describe_schema、list_experiments、get_experiment、get_experiment_data、" +
                            "summarize_experiments、list_doe_batches、get_doe_batch、query_sql——" +
                            "直连实验历史数据库,主程序开不开都能用;问「上周做了什么、成功率多少、以前的数据」优先用它们。" +
                            "当前为只读模式:可以查询和分析,不能控制设备、启停流程或写入数据——" +
                            "需要这些操作时,请让用户在 MaxChemical 主程序里完成。"
                    }
                });
                break;
            }

            case "notifications/initialized":
            case "initialized":
                break;

            case "ping":
                if (!isNotification)
                    Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JsonObject() });
                break;

            case "tools/list":
                Send(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["result"] = new JsonObject { ["tools"] = await ListToolsAsync() }
                });
                break;

            case "tools/call":
                await CallToolAsync(id, prms);
                break;

            default:
                if (!isNotification)
                    SendError(id, -32601, $"未实现的方法:{method}");
                break;
        }
    }

    /// <summary>合并清单:历史工具(本地,永远可列)+ 实时工具(管道,取不到用缓存兜底)。</summary>
    private static async Task<JsonArray> ListToolsAsync()
    {
        var merged = new JsonArray();

        // ① 历史工具:本进程内,永远列得出来
        foreach (var t in _historyTools.DescribeAll())
        {
            if (t is not JsonObject o) continue;
            merged.Add(new JsonObject
            {
                ["name"] = o["name"]?.DeepClone(),
                ["description"] = o["description"]?.DeepClone(),
                ["inputSchema"] = o["inputSchema"]?.DeepClone()
                                  ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
            });
        }

        // ② 实时工具:经管道取,失败用上次成功的清单兜底
        JsonArray pipeTools;
        try
        {
            var resp = await _pipe.RequestAsync("tools/list");
            if (resp["ok"]?.GetValue<bool>() == true)
            {
                pipeTools = resp["tools"]?.AsArray() ?? new JsonArray();
                _cachedPipeTools = pipeTools.DeepClone().AsArray();
            }
            else
            {
                Log("主程序拒绝了 tools/list:" + (resp["error"]?.GetValue<string>() ?? "(无错误信息)"));
                pipeTools = _cachedPipeTools?.DeepClone().AsArray() ?? new JsonArray();
            }
        }
        catch (McpBridgeException ex)
        {
            Log("实时工具清单获取失败:" + ex.Message);
            pipeTools = _cachedPipeTools?.DeepClone().AsArray() ?? new JsonArray();
            if (pipeTools.Count > 0) Log("已用上一次的实时工具清单兜底。");
        }

        int historyCount = merged.Count;
        foreach (var t in pipeTools)
        {
            if (t is not JsonObject o) continue;
            string? name = o["name"]?.GetValue<string>();

            // 撞名防线:两侧工具名今天不重叠,但主程序将来加工具时谁也拦不住起个同名。
            // 撞了以历史侧为准(它先进清单),只丢一条告警,不让整个清单坏掉。
            if (name != null && _historyToolNames.Contains(name))
            {
                Log($"警告:实时工具「{name}」与历史工具同名,已跳过实时侧(请改名)。");
                continue;
            }

            merged.Add(new JsonObject
            {
                ["name"] = o["name"]?.DeepClone(),
                ["description"] = o["description"]?.DeepClone(),
                ["inputSchema"] = o["inputSchema"]?.DeepClone()
                                  ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
            });
        }

        Log($"tools/list:返回 {merged.Count} 项(历史 {historyCount} + 实时 {merged.Count - historyCount})");
        return merged;
    }

    private static async Task CallToolAsync(JsonNode? id, JsonNode? prms)
    {
        string? name = prms?["name"]?.GetValue<string>();
        JsonNode? args = prms?["arguments"];

        if (string.IsNullOrEmpty(name))
        {
            SendError(id, -32602, "缺少 params.name");
            return;
        }

        Log($"tools/call {name} {args?.ToJsonString()}");

        string text;
        bool isError;

        // ── 路由:历史工具本地执行,其余经管道转发 ──
        if (_historyToolNames.Contains(name))
        {
            var result = await _historyTools.CallAsync(name, args as JsonObject);
            text = result.Text;
            isError = result.IsError;
        }
        else
        {
            try
            {
                var resp = await _pipe.RequestAsync("tools/call", req =>
                {
                    req["name"] = name;
                    req["arguments"] = args?.DeepClone() ?? new JsonObject();
                });

                if (resp["ok"]?.GetValue<bool>() == true)
                {
                    text = resp["content"]?.GetValue<string>() ?? "(主程序返回了空内容)";
                    isError = resp["isError"]?.GetValue<bool>() ?? false;
                }
                else
                {
                    text = "主程序拒绝了本次调用:" + (resp["error"]?.GetValue<string>() ?? "(无错误信息)");
                    isError = true;
                }
            }
            catch (McpBridgeException ex)
            {
                text = ex.Message + "(这是实时类工具,需要主程序运行;查历史实验请改用 list_experiments 等历史类工具。)";
                isError = true;
            }
        }

        // 工具执行失败用 isError 而不是 JSON-RPC error:
        // 前者模型看得到错误内容并能自己纠正,后者对模型是不可见的传输层故障
        Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                ["isError"] = isError
            }
        });
    }

    private static void SendError(JsonNode? id, int code, string message)
    {
        Send(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        });
    }

    /// <summary>唯一允许写 stdout 的地方。一条消息一行,写完立刻 flush。</summary>
    private static void Send(JsonNode message)
    {
        lock (_stdout)
        {
            _stdout.Write(message.ToJsonString(JsonOpts));
            _stdout.Write('\n');
            _stdout.Flush();
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        // 中文不转义,日志与排障时肉眼可读
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>所有日志走 stderr。写 stdout 会毒掉 JSON-RPC 流。</summary>
    internal static void Log(string message)
        => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
}
