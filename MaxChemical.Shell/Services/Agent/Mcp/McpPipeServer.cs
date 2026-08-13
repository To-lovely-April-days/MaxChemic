using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MaxChemical.Logging;

namespace MaxChemical.Shell.Services.Agent.Mcp
{
    /// <summary>
    /// 主程序内的命名管道服务:把小桐的工具注册表(经白名单过滤)暴露给同机的
    /// <c>MaxChemical.Mcp.Bridge.exe</c>,再由它以 MCP 协议对接外部 AI 客户端。
    ///
    /// ── 为什么要中间这一跳 ──────────────────────────────────────────
    /// MCP 的 stdio 传输要求客户端**启动一个子进程**并接管它的标准输入输出。
    /// 而设备连接、画布状态、流程引擎全都活在主程序这个 WPF 进程里,拿不出来。
    /// 所以:客户端 ──stdio──▶ Bridge.exe ──命名管道──▶ 主程序(本类)。
    /// Bridge 是个瘦壳,不含任何业务逻辑,主程序没开时它也能起来并给出说得清的错误。
    ///
    /// ── 协议 ────────────────────────────────────────────────────────
    /// 换行分隔的 JSON,一问一答,不做流式。请求:
    ///   {"id":"1","method":"hello"}
    ///   {"id":"2","method":"tools/list"}
    ///   {"id":"3","method":"tools/call","name":"list_devices","arguments":{}}
    /// 应答:
    ///   {"id":"1","ok":true,...}
    ///   {"id":"3","ok":true,"content":"...","isError":false}
    ///   {"id":"9","ok":false,"error":"..."}
    ///
    /// 刻意不复用 MCP 的 JSON-RPC 报文格式:管道这一段是内部私有协议,
    /// 保持简单,MCP 的协议细节(能力协商、协议版本回退)全部留在 Bridge 里。
    ///
    /// ── 边界 ────────────────────────────────────────────────────────
    /// · 只监听本机命名管道,不开任何网络端口。
    /// · 工具集经 <see cref="McpToolPolicy"/> 白名单过滤,当前只放只读。
    /// · 工具调用串行执行(见 _gate),与小桐自身的单轮串行语义保持一致。
    /// </summary>
    public sealed class McpPipeServer : IDisposable
    {
        /// <summary>默认管道名。现场若要多实例并存,改配置 MaxChemicalMcp:PipeName。</summary>
        public const string DefaultPipeName = "MaxChemical.Mcp.v1";

        /// <summary>本服务实现的管道协议版本。Bridge 侧会核对,不匹配时给出可读提示。</summary>
        public const int ProtocolVersion = 1;

        /// <summary>同时保持的监听实例数:允许几个客户端同时挂着。</summary>
        private const int MaxConcurrentClients = 4;

        private readonly ILogService _logger = LogManager.GetLogger<McpPipeServer>();

        /// <summary>工具执行串行化。设备与画布状态不是为并发访问设计的。</summary>
        private readonly SemaphoreSlim _gate = new(1, 1);

        private CancellationTokenSource _cts;
        private XiaoTongAgent _agent;
        private McpToolPolicy _policy;
        private string _pipeName = DefaultPipeName;
        private bool _started;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            // 中文不转义,日志与排障时肉眼可读
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// 启动监听。策略为 disabled 时直接返回(不建管道)。
        /// 重复调用无副作用。
        /// </summary>
        public void Start(XiaoTongAgent agent, McpToolPolicy policy, string pipeName = null)
        {
            if (_started) return;
            if (agent == null) throw new ArgumentNullException(nameof(agent));
            if (policy == null) throw new ArgumentNullException(nameof(policy));

            _agent = agent;
            _policy = policy;
            if (!string.IsNullOrWhiteSpace(pipeName)) _pipeName = pipeName.Trim();

            if (!_policy.IsEnabled)
            {
                _logger.LogInformation("外部 MCP 管道服务未启动(模式 disabled)");
                return;
            }

            var exposed = _policy.Filter(_agent.Tools);
            var stale = _policy.FindStaleEntries(_agent.Tools);
            if (stale.Count > 0)
            {
                // 白名单里写了但注册表没有——多半是改名或拼错,静默失效最难查
                _logger.LogWarning("MCP 白名单中有 " + stale.Count + " 项在工具注册表中不存在,已忽略:"
                                   + string.Join("、", stale));
            }

            _started = true;
            _cts = new CancellationTokenSource();

            for (int i = 0; i < MaxConcurrentClients; i++)
                _ = Task.Run(() => ListenLoopAsync(_cts.Token));

            _logger.LogInformation($"外部 MCP 管道服务已启动:管道 {_pipeName},模式 {_policy.Mode}," +
                                   $"放行 {exposed.Count}/{_agent.Tools.Count} 项工具");
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;
            try { _cts?.Cancel(); } catch { }
            _logger.LogInformation("外部 MCP 管道服务已停止");
        }

        public void Dispose()
        {
            Stop();
            try { _cts?.Dispose(); } catch { }
            try { _gate.Dispose(); } catch { }
        }

        // ── 监听循环 ────────────────────────────────────────────────

        private async Task ListenLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(
                        _pipeName,
                        PipeDirection.InOut,
                        MaxConcurrentClients,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    await ServeAsync(pipe, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 单个连接崩掉不能让监听循环停摆;歇一下避免异常风暴打满日志
                    _logger.LogWarning("MCP 管道连接异常:" + ex.Message);
                    try { await Task.Delay(500, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
                finally
                {
                    try { pipe?.Dispose(); } catch { }
                }
            }
        }

        private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
        {
            var enc = new UTF8Encoding(false);
            using var reader = new StreamReader(pipe, enc, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(pipe, enc, 1024, leaveOpen: true) { AutoFlush = true };

            while (!ct.IsCancellationRequested && pipe.IsConnected)
            {
                string line;
                try { line = await reader.ReadLineAsync().ConfigureAwait(false); }
                catch (IOException) { break; }   // 客户端断开

                if (line == null) break;
                if (line.Length == 0) continue;

                JsonNode request;
                try { request = JsonNode.Parse(line); }
                catch (JsonException ex)
                {
                    await WriteAsync(writer, Error(null, "报文无法解析:" + ex.Message)).ConfigureAwait(false);
                    continue;
                }
                if (request == null) continue;

                JsonObject response;
                try
                {
                    response = await HandleAsync(request).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError("MCP 请求处理异常:" + ex);
                    response = Error(TryGetId(request), "内部错误:" + ex.Message);
                }

                await WriteAsync(writer, response).ConfigureAwait(false);
            }
        }

        private static async Task WriteAsync(StreamWriter writer, JsonObject payload)
        {
            try
            {
                await writer.WriteLineAsync(payload.ToJsonString(JsonOpts)).ConfigureAwait(false);
            }
            catch (IOException) { /* 客户端已断开,交由外层结束本次会话 */ }
        }

        // ── 请求分发 ────────────────────────────────────────────────

        private async Task<JsonObject> HandleAsync(JsonNode request)
        {
            string id = TryGetId(request);
            string method = request["method"]?.GetValue<string>();

            switch (method)
            {
                case "hello":
                    return new JsonObject
                    {
                        ["id"] = id,
                        ["ok"] = true,
                        ["protocolVersion"] = ProtocolVersion,
                        ["mode"] = _policy.Mode,
                        ["toolCount"] = _policy.Filter(_agent.Tools).Count,
                        ["agentConfigured"] = _agent.IsConfigured
                    };

                case "ping":
                    return new JsonObject { ["id"] = id, ["ok"] = true };

                case "tools/list":
                    return new JsonObject
                    {
                        ["id"] = id,
                        ["ok"] = true,
                        ["tools"] = DescribeTools()
                    };

                case "tools/call":
                    return await CallToolAsync(id, request).ConfigureAwait(false);

                default:
                    return Error(id, $"未实现的方法:{method}");
            }
        }

        private JsonArray DescribeTools()
        {
            var arr = new JsonArray();
            foreach (var tool in _policy.Filter(_agent.Tools))
            {
                JsonNode schema;
                try
                {
                    schema = JsonNode.Parse(tool.ParametersSchema)
                             ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
                }
                catch (Exception ex)
                {
                    // 某个工具的 schema 字面量写坏了不该连累其余工具
                    _logger.LogWarning($"工具 {tool.Name} 的参数 schema 解析失败,已用空对象代替:{ex.Message}");
                    schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
                }

                arr.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["displayName"] = tool.DisplayName,
                    ["description"] = tool.Description,
                    ["inputSchema"] = schema
                });
            }
            return arr;
        }

        private async Task<JsonObject> CallToolAsync(string id, JsonNode request)
        {
            string name = request["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
                return Error(id, "缺少 name");

            if (!_policy.IsAllowed(name, out string denyReason))
            {
                // 拒绝要留痕:这是排查"外部客户端想干什么"的唯一线索
                _logger.LogWarning($"MCP 拒绝调用 {name}:{denyReason}");
                return new JsonObject
                {
                    ["id"] = id,
                    ["ok"] = true,
                    ["isError"] = true,
                    ["content"] = $"工具「{name}」未对外部客户端开放:{denyReason}。" +
                                  "当前外部接口为只读模式,设备控制与写入请在 MaxChemical 主程序内操作。"
                };
            }

            var tool = _agent.Tools.FirstOrDefault(t => t.Name == name);
            if (tool == null)
                return Error(id, $"工具 {name} 不存在");

            JsonElement args;
            try
            {
                string argJson = request["arguments"]?.ToJsonString() ?? "{}";
                using var doc = JsonDocument.Parse(argJson);
                args = doc.RootElement.Clone();   // doc 出作用域即释放,必须 Clone
            }
            catch (JsonException ex)
            {
                return Error(id, "arguments 解析失败:" + ex.Message);
            }

            _logger.LogInformation($"MCP 调用 {name} {request["arguments"]?.ToJsonString()}");

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                AgentToolResult result = await tool.ExecuteAsync(args).ConfigureAwait(false);
                return new JsonObject
                {
                    ["id"] = id,
                    ["ok"] = true,
                    ["isError"] = !result.Success,
                    ["content"] = result.ToLlmText()
                };
            }
            catch (Exception ex)
            {
                // 工具自身抛异常时,回 isError 而不是协议层错误——
                // 前者模型看得到内容并能自己纠正,后者对模型是不可见的传输故障
                _logger.LogError($"MCP 工具 {name} 执行异常:{ex}");
                return new JsonObject
                {
                    ["id"] = id,
                    ["ok"] = true,
                    ["isError"] = true,
                    ["content"] = "工具执行失败:" + ex.Message
                };
            }
            finally
            {
                _gate.Release();
            }
        }

        // ── 小工具 ──────────────────────────────────────────────────

        private static string TryGetId(JsonNode request)
        {
            try { return request?["id"]?.ToString(); }
            catch { return null; }
        }

        private static JsonObject Error(string id, string message) => new()
        {
            ["id"] = id,
            ["ok"] = false,
            ["error"] = message
        };
    }
}
