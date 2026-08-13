using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MaxChemical.Mcp.Bridge;

/// <summary>
/// MaxChemical 实时接口桥(MCP Server)。
///
/// ── 这是什么 ────────────────────────────────────────────────────────
/// 一个瘦壳进程:对上以 MCP 协议(stdio)接 AI 客户端,对下用命名管道接
/// MaxChemical 主程序,把小桐的工具转出去。**本进程不含任何业务逻辑**,
/// 工具清单、参数 schema、执行结果全部来自主程序。
///
///   AI 客户端 ──stdio/JSON-RPC──▶ 本进程 ──命名管道──▶ MaxChemical 主程序
///
/// 与 MaxChemical.Mcp.History 的分工:
///   · History 查**历史**(直连 MySQL 只读账号,不需要主程序在运行)
///   · Bridge  查**实时**(设备状态、流程进度、当前画布,必须主程序在运行)
/// 两个都挂上,模型才既能回答"上周那批做了几次",又能回答"现在几度"。
///
/// ── 当前是只读的 ────────────────────────────────────────────────────
/// 放行哪些工具由主程序侧的白名单决定(McpToolPolicy),本进程不做也不能做
/// 权限判断——把策略放在被保护的一侧,是这个设计唯一说得通的位置。
/// 设备控制、流程启停、数据入库目前一律不开放,调用会拿到一句说明。
///
/// ── stdout 只能有 JSON-RPC ──────────────────────────────────────────
/// 与 History 同一条铁律:任何一句调试输出都会让客户端解析失败并断开,
/// 而它给的报错通常只是一句没有信息量的 "server disconnected"。
/// 所有日志一律走 stderr(见 Log),stdout 只由 Send 写。
/// </summary>
public static class Program
{
    private static readonly string[] SupportedProtocolVersions =
    {
        "2025-06-18", "2025-03-26", "2024-11-05"
    };

    private const string DefaultProtocolVersion = "2024-11-05";
    private const string ServerName = "maxchemical-bridge";
    private const string ServerVersion = "0.1.0";

    /// <summary>主程序侧管道协议版本。不一致时给出可读提示,而不是行为诡异。</summary>
    private const int ExpectedPipeProtocol = 1;

    private static StreamWriter _stdout = null!;
    private static PipeClient _pipe = null!;

    /// <summary>
    /// 最近一次成功取到的工具清单。主程序临时不可用时用它兜底,
    /// 免得客户端把工具清单刷成空、之后再也不重试。
    /// </summary>
    private static JsonArray? _cachedTools;

    public static async Task<int> Main(string[] args)
    {
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        _stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = false
        };

        var config = AppConfig.Load(args);
        _pipe = new PipeClient(config.PipeName, config.ConnectTimeoutMs);

        Log($"{ServerName} v{ServerVersion} 启动");
        Log($"目标管道:{config.PipeName}(连接超时 {config.ConnectTimeoutMs} ms)");

        // 启动时探一次,但**探不通也不退出**——MCP 客户端遇到子进程立刻退出
        // 只会显示一句无信息量的 disconnected。主程序后开也能自动接上。
        await ProbeAsync();

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

    private static async Task ProbeAsync()
    {
        try
        {
            var hello = await _pipe.RequestAsync("hello");
            if (hello["ok"]?.GetValue<bool>() == true)
            {
                int proto = hello["protocolVersion"]?.GetValue<int>() ?? 0;
                string mode = hello["mode"]?.GetValue<string>() ?? "?";
                int count = hello["toolCount"]?.GetValue<int>() ?? 0;

                Log($"主程序已连接:管道协议 v{proto},模式 {mode},可用工具 {count} 项");

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
            Log("这不影响本进程启动;主程序起来之后,下一次工具调用会自动接上。");
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
                            "这是 MaxChemical 化工实验平台的**实时**接口,数据直接来自正在运行的主程序:" +
                            "画布上的设备与实时参数、流程运行状态、DOE 批次进度、模型预测与动力学推算。" +
                            "当前为只读模式:可以查询和分析,不能控制设备、启停流程或写入数据——" +
                            "需要这些操作时,请让用户在 MaxChemical 主程序里完成。" +
                            "若工具返回连不上主程序,说明 MaxChemical 没有运行,请如实告知用户,不要臆测数值。" +
                            "查历史实验记录请用 maxchemical-history 接口(如果它也挂上了)。"
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

    private static async Task<JsonArray> ListToolsAsync()
    {
        try
        {
            var resp = await _pipe.RequestAsync("tools/list");
            if (resp["ok"]?.GetValue<bool>() != true)
            {
                Log("主程序拒绝了 tools/list:" + (resp["error"]?.GetValue<string>() ?? "(无错误信息)"));
                return _cachedTools?.DeepClone().AsArray() ?? new JsonArray();
            }

            var tools = resp["tools"]?.AsArray() ?? new JsonArray();

            // 主程序侧带了 displayName(给人看的名字),MCP 的 tool 对象里没有这个字段,
            // 剥掉以免客户端按未知字段报错
            var cleaned = new JsonArray();
            foreach (var t in tools)
            {
                if (t is not JsonObject o) continue;
                cleaned.Add(new JsonObject
                {
                    ["name"] = o["name"]?.DeepClone(),
                    ["description"] = o["description"]?.DeepClone(),
                    ["inputSchema"] = o["inputSchema"]?.DeepClone()
                                      ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
                });
            }

            _cachedTools = cleaned.DeepClone().AsArray();
            Log($"tools/list:返回 {cleaned.Count} 项");
            return cleaned;
        }
        catch (McpBridgeException ex)
        {
            Log("tools/list 失败:" + ex.Message);
            // 有缓存就用缓存:客户端多半只在启动时列一次,这里返回空会让整个接口看起来是空的
            if (_cachedTools is not null)
            {
                Log("已用上一次的工具清单兜底。");
                return _cachedTools.DeepClone().AsArray();
            }
            return new JsonArray();
        }
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
            text = ex.Message;
            isError = true;
        }

        // 工具执行失败用 isError 而不是 JSON-RPC error:
        // 前者模型看得到内容并能自己纠正,后者对模型是不可见的传输层故障
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
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>所有日志走 stderr。写 stdout 会毒掉 JSON-RPC 流。</summary>
    internal static void Log(string message)
        => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
}
