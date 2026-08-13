using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MaxChemical.Mcp.History;

/// <summary>
/// MaxChemical 实验历史 MCP Server（只读）。
///
/// ── 这是什么 ────────────────────────────────────────────────────────
/// 一个独立的控制台程序,通过 MCP 协议把 MaxChemical 的实验历史数据库暴露给
/// AI Agent（腾讯 WorkBuddy / Claude Desktop / 任何支持 MCP 的客户端）。
///
/// **它完全不碰 MaxChemical 主程序。** 只连数据库,只读。主程序开没开都不影响。
/// 这是「分阶段接入 Agent」的第一步:先把链路跑通、把外围问题趟平,
/// 再去动那个绑死在 WPF 上的实时控制面。
///
/// ── 传输方式:stdio ──────────────────────────────────────────────────
/// MCP 的 stdio 传输就是「标准输入输出上的换行分隔 JSON-RPC 2.0」。
/// 客户端负责启动本进程,一条消息一行,不能有内嵌换行。
///
/// **最容易踩的坑:stdout 里只能有 JSON-RPC,一个字节的杂音都不行。**
/// 任何一句 Console.WriteLine 的调试输出都会让客户端解析失败并断开连接,
/// 而报错信息通常只是"server disconnected",完全看不出原因。
/// 所以本程序所有日志一律走 stderr(见 Log 方法),stdout 只由 Send 写。
///
/// ── 为什么手写 JSON-RPC 而不用 MCP SDK ──────────────────────────────
/// 我们需要的协议面很小:initialize / tools/list / tools/call / ping,
/// 四个方法两百行就够。手写换来的是:
///   · 零额外依赖(不引入 Hosting/DI 全家桶)
///   · 不受 SDK 版本漂移影响
///   · 将来主程序那侧要换成命名管道时,这套骨架能直接搬过去
/// </summary>
public static class Program
{
    /// <summary>本服务器实现的协议版本。客户端请求的版本若在此列表内就回它请求的那个。</summary>
    private static readonly string[] SupportedProtocolVersions =
    {
        "2025-06-18", "2025-03-26", "2024-11-05"
    };

    private const string DefaultProtocolVersion = "2024-11-05";
    private const string ServerName = "maxchemical-history";
    private const string ServerVersion = "0.1.0";

    private static StreamWriter _stdout = null!;
    private static Database _db = null!;
    private static ToolRegistry _tools = null!;

    public static async Task<int> Main(string[] args)
    {
        // 显式接管标准流,不用 Console.In/Out ——
        // 直接设 Console.OutputEncoding 在 stdin/stdout 被重定向时可能抛异常,
        // 而 MCP 场景下它们一定是被重定向的。
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        _stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = false
        };

        var config = AppConfig.Load(args);
        _db = new Database(config);
        _tools = new ToolRegistry(_db);

        Log($"{ServerName} v{ServerVersion} 启动");
        Log($"数据库: {config.DescribeTarget()}");

        // 启动时探一次库,但**探不通也不退出** ——
        // MCP 客户端遇到「子进程立刻退出」只会显示一句无信息量的 disconnected,
        // 让工具调用时返回一条说得清的错误,比在这里死掉有用得多。
        var probe = await _db.ProbeAsync();
        Log(probe.Ok ? $"数据库连通,发现 {probe.TableCount} 张表" : $"数据库暂时连不上:{probe.Error}");

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
                // 单条消息处理失败不能让整个 server 停摆
                Log($"处理消息时异常:{ex}");
                SendError(request["id"]?.DeepClone(), -32603, "Internal error: " + ex.Message);
            }
        }

        Log("stdin 已关闭,退出");
        return 0;
    }

    private static async Task HandleAsync(JsonNode request)
    {
        string? method = request["method"]?.GetValue<string>();
        JsonNode? id = request["id"]?.DeepClone();
        JsonNode? prms = request["params"];

        // id 为 null 表示这是通知(notification),按协议不能有响应
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
                        // 只声明 tools 能力。没有 resources/prompts,不要谎报。
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = ServerName,
                            ["version"] = ServerVersion
                        },
                        ["instructions"] =
                            "这是 MaxChemical 化工实验平台的历史数据查询接口,只读。" +
                            "可以查实验记录、节点执行明细、设备时序数据、DOE 批次与运行结果。" +
                            "不知道有哪些表/列时,先调用 describe_schema。" +
                            "本接口不能控制任何设备,也不能修改数据。"
                    }
                });
                break;
            }

            case "notifications/initialized":
            case "initialized":
                // 通知,不回
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
                    ["result"] = new JsonObject { ["tools"] = _tools.DescribeAll() }
                });
                break;

            case "tools/call":
            {
                string? name = prms?["name"]?.GetValue<string>();
                JsonNode? argsNode = prms?["arguments"];

                if (string.IsNullOrEmpty(name))
                {
                    SendError(id, -32602, "缺少 params.name");
                    break;
                }

                Log($"tools/call {name} {argsNode?.ToJsonString()}");
                var result = await _tools.CallAsync(name, argsNode as JsonObject);

                // 工具执行失败用 isError 而不是 JSON-RPC error ——
                // 前者模型看得到错误内容并能自己纠正,后者对模型是不可见的传输层故障。
                Send(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["result"] = new JsonObject
                    {
                        ["content"] = new JsonArray(
                            new JsonObject { ["type"] = "text", ["text"] = result.Text }),
                        ["isError"] = result.IsError
                    }
                });
                break;
            }

            default:
                if (!isNotification)
                    SendError(id, -32601, $"未实现的方法:{method}");
                break;
        }
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
        // 中文不转义,日志里看得懂;JSON 规范允许直接放 UTF-8
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>所有日志走 stderr。写 stdout 会毒掉 JSON-RPC 流。</summary>
    internal static void Log(string message)
        => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
}
