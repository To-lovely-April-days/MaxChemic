using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MaxChemical.Mcp.Bridge;

/// <summary>
/// 到主程序命名管道的客户端。
///
/// ── 连接策略:按需连接,不常驻 ──────────────────────────────────
/// MCP 客户端(WorkBuddy / Codex / …)通常在自己启动时就把本进程拉起来,
/// 那时候 MaxChemical 主程序很可能还没开。所以本类**不在构造时连接**,
/// 而是每次调用前确保连接可用,断了就重连。
///
/// 这样带来的行为是:先开 AI 客户端、后开主程序,不需要重启客户端,
/// 主程序起来之后下一次工具调用就能通——现场用起来这一点很重要。
/// </summary>
public sealed class PipeClient : IDisposable
{
    private readonly string _pipeName;
    private readonly int _connectTimeoutMs;

    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _nextId;

    /// <summary>同一时刻只允许一次问答:管道上是严格的一问一答。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public PipeClient(string pipeName, int connectTimeoutMs)
    {
        _pipeName = pipeName;
        _connectTimeoutMs = connectTimeoutMs;
    }

    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>
    /// 发一次请求并等应答。连接不上时抛 <see cref="McpBridgeException"/>,
    /// 消息是给人看的(会原样出现在 AI 客户端的工具结果里)。
    /// </summary>
    public async Task<JsonObject> RequestAsync(string method, Action<JsonObject>? fill = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // 第一次失败就重连一次再试:管道可能因主程序重启而失效,
            // 这种情况下让用户重开 AI 客户端太蠢了
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await EnsureConnectedAsync().ConfigureAwait(false);

                var req = new JsonObject
                {
                    ["id"] = (++_nextId).ToString(),
                    ["method"] = method
                };
                fill?.Invoke(req);

                try
                {
                    await _writer!.WriteLineAsync(req.ToJsonString(JsonOpts)).ConfigureAwait(false);
                    string? line = await _reader!.ReadLineAsync().ConfigureAwait(false);

                    if (line is null)
                    {
                        // 对端关闭了连接
                        Disconnect();
                        if (attempt == 0) continue;
                        throw new McpBridgeException("主程序在应答前关闭了连接,请确认 MaxChemical 仍在运行。");
                    }

                    var node = JsonNode.Parse(line);
                    if (node is not JsonObject obj)
                        throw new McpBridgeException("主程序返回了无法解析的应答。");

                    return obj;
                }
                catch (IOException)
                {
                    Disconnect();
                    if (attempt == 0) continue;
                    throw new McpBridgeException("与主程序的管道连接中断,请确认 MaxChemical 仍在运行。");
                }
            }

            throw new McpBridgeException("与主程序通信失败。");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureConnectedAsync()
    {
        if (IsConnected) return;

        Disconnect();

        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(_connectTimeoutMs).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            pipe.Dispose();
            throw new McpBridgeException(
                $"连不上 MaxChemical 主程序(管道 {_pipeName})。" +
                "请确认:① 主程序正在运行;② 主程序的 appsettings.json 里 MaxChemicalMcp:Mode 不是 disabled;" +
                "③ 本程序与主程序运行在同一台机器、同一个 Windows 账户下。");
        }
        catch (Exception ex)
        {
            pipe.Dispose();
            throw new McpBridgeException($"连接主程序失败:{ex.Message}");
        }

        var enc = new UTF8Encoding(false);
        _pipe = pipe;
        _reader = new StreamReader(pipe, enc, false, 1024, leaveOpen: true);
        _writer = new StreamWriter(pipe, enc, 1024, leaveOpen: true) { AutoFlush = true };

        Program.Log($"已连接主程序管道 {_pipeName}");
    }

    private void Disconnect()
    {
        try { _reader?.Dispose(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _reader = null;
        _writer = null;
        _pipe = null;
    }

    public void Dispose()
    {
        Disconnect();
        try { _gate.Dispose(); } catch { }
    }
}

/// <summary>桥接层的可读错误。消息会原样呈现给模型,所以要说人话。</summary>
public sealed class McpBridgeException : Exception
{
    public McpBridgeException(string message) : base(message) { }
}
