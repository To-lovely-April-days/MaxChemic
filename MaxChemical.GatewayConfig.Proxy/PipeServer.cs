using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MaxChemical.Modules.GatewayConfig.Ipc;

namespace MaxChemical.GatewayConfig.Proxy
{
    /// <summary>
    /// Named Pipe 服务端,接收 IPC 消息并派发。
    /// 单连接模型,主进程崩溃后允许重连。
    /// </summary>
    internal class PipeServer : IDisposable
    {
        private readonly SdkExecutor _executor;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly object _writeLock = new object();
        private NamedPipeServerStream _pipe;
        private StreamWriter _writer;
        private StreamReader _reader;

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };

        public PipeServer(SdkExecutor executor)
        {
            _executor = executor;
        }

        public async Task RunAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    _pipe = new NamedPipeServerStream(
                        IpcProtocol.PipeName,
                        PipeDirection.InOut,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await _pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);

                    _writer = new StreamWriter(_pipe) { AutoFlush = true, NewLine = "\n" };
                    _reader = new StreamReader(_pipe);

                    await PumpAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Proxy] pipe loop error: {ex.Message}");
                    await Task.Delay(500).ConfigureAwait(false);
                }
                finally
                {
                    try { _writer?.Dispose(); } catch { }
                    try { _reader?.Dispose(); } catch { }
                    try { _pipe?.Dispose(); }   catch { }
                }
            }
        }

        private async Task PumpAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && _pipe.IsConnected)
            {
                string line = await _reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) return;

                IpcRequest req;
                try { req = JsonSerializer.Deserialize<IpcRequest>(line, JsonOpts); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Proxy] bad request: {ex.Message}");
                    continue;
                }
                if (req == null) continue;
                _ = Task.Run(() => HandleRequestAsync(req, ct), ct);
            }
        }

        private async Task HandleRequestAsync(IpcRequest req, CancellationToken ct)
        {
            var resp = new IpcResponse { Op = req.Op, CorrelationId = req.CorrelationId };
            try
            {
                switch (req.Op)
                {
                    case IpcProtocol.Op.Handshake:
                        resp.Payload["version"] = IpcProtocol.ProtocolVersion;
                        resp.Success = true;
                        break;

                    case IpcProtocol.Op.SearchDevices:
                    {
                        int timeoutMs = GetInt(req.Payload, "timeoutMs", 3000);
                        var devices = await _executor.SearchDevicesAsync(
                            timeoutMs,
                            count => SendNotification(IpcProtocol.NotifyOp.SearchProgress,
                                new Dictionary<string, object> { ["count"] = count }),
                            ct).ConfigureAwait(false);
                        resp.Payload["devices"] = devices;
                        resp.Success = true;
                        break;
                    }

                    case IpcProtocol.Op.ReadParams:
                    {
                        string devId = GetStr(req.Payload, "deviceId");
                        var snapshot = await _executor.ReadParamsAsync(devId, ct).ConfigureAwait(false);
                        resp.Payload["device"] = snapshot;
                        resp.Success = snapshot != null;
                        if (!resp.Success) resp.ErrorMessage = "device not found";
                        break;
                    }

                    case IpcProtocol.Op.WriteParams:
                    {
                        string devId = GetStr(req.Payload, "deviceId");
                        var changes = ExtractChanges(req.Payload);
                        await _executor.WriteParamsAsync(devId, changes, ct).ConfigureAwait(false);
                        resp.Success = true;
                        break;
                    }

                    case IpcProtocol.Op.SetDefault:
                    {
                        string devId = GetStr(req.Payload, "deviceId");
                        await _executor.SetDefaultAsync(devId, ct).ConfigureAwait(false);
                        resp.Success = true;
                        break;
                    }

                    case IpcProtocol.Op.PollDevice:
                    {
                        string devId = GetStr(req.Payload, "deviceId");
                        int intervalMs = GetInt(req.Payload, "intervalMs", 1500);
                        int timeoutMs = GetInt(req.Payload, "timeoutMs", 30000);
                        var found = await _executor.PollDeviceAsync(devId, intervalMs, timeoutMs,
                            (attempt, isFound) => SendNotification(IpcProtocol.NotifyOp.PollProgress,
                                new Dictionary<string, object>
                                {
                                    ["attempt"] = attempt,
                                    ["found"] = isFound,
                                    ["deviceId"] = devId,
                                }),
                            ct).ConfigureAwait(false);
                        resp.Payload["found"] = found;
                        resp.Success = true;
                        break;
                    }

                    case IpcProtocol.Op.Shutdown:
                        resp.Success = true;
                        SendMessage(resp);
                        _cts.Cancel();
                        return;

                    default:
                        resp.Success = false;
                        resp.ErrorMessage = "unknown op: " + req.Op;
                        break;
                }
            }
            catch (Exception ex)
            {
                resp.Success = false;
                resp.ErrorMessage = ex.Message;
            }
            SendMessage(resp);
        }

        private void SendNotification(string op, Dictionary<string, object> payload)
        {
            SendMessage(new IpcNotification { Op = op, Payload = payload });
        }

        private void SendMessage(IpcMessage msg)
        {
            try
            {
                var json = JsonSerializer.Serialize(msg, msg.GetType(), JsonOpts);
                lock (_writeLock)
                {
                    _writer?.WriteLine(json);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Proxy] send fail: {ex.Message}");
            }
        }

        private static int GetInt(Dictionary<string, object> p, string key, int dflt = 0)
        {
            if (p == null || !p.TryGetValue(key, out var v)) return dflt;
            if (v is JsonElement je && je.ValueKind == JsonValueKind.Number)
                return je.GetInt32();
            return Convert.ToInt32(v);
        }

        private static string GetStr(Dictionary<string, object> p, string key)
        {
            if (p == null || !p.TryGetValue(key, out var v)) return "";
            if (v is JsonElement je) return je.GetString() ?? "";
            return v?.ToString() ?? "";
        }

        private static Dictionary<string, object> ExtractChanges(Dictionary<string, object> p)
        {
            var result = new Dictionary<string, object>();
            if (!p.TryGetValue("changes", out var raw)) return result;
            var je = (JsonElement)raw;
            foreach (var prop in je.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number)
                    result[prop.Name] = prop.Value.GetInt32();
                else if (prop.Value.ValueKind == JsonValueKind.String)
                    result[prop.Name] = prop.Value.GetString();
            }
            return result;
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _pipe?.Dispose(); }   catch { }
            _cts.Dispose();
        }
    }
}
