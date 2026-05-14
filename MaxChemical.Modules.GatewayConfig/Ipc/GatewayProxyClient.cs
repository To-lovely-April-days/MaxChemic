using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MaxChemical.Modules.GatewayConfig.Ipc
{
    /// <summary>
    /// 主进程侧的 IPC 客户端。负责拉起 32 位代理子进程并维护命名管道连接。
    /// </summary>
    public class GatewayProxyClient : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private readonly string _proxyExePath;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcResponse>> _pending
            = new ConcurrentDictionary<string, TaskCompletionSource<IpcResponse>>();
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        private Process _proxyProcess;
        private NamedPipeClientStream _pipe;
        private StreamReader _reader;
        private StreamWriter _writer;
        private Task _readLoop;
        private long _correlationCounter;

        public event Action<IpcNotification> NotificationReceived;
        public event Action<string> ProxyExited;

        public bool IsConnected => _pipe != null && _pipe.IsConnected;

        public GatewayProxyClient(string proxyExePath)
        {
            _proxyExePath = proxyExePath;
        }

        public async Task ConnectAsync(int startupTimeoutMs = 5000)
        {
            if (_proxyProcess == null || _proxyProcess.HasExited)
            {
                if (!File.Exists(_proxyExePath))
                    throw new FileNotFoundException("找不到代理进程", _proxyExePath);

                _proxyProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = _proxyExePath,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        WorkingDirectory = Path.GetDirectoryName(_proxyExePath),
                    },
                    EnableRaisingEvents = true,
                };
                _proxyProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Debug.WriteLine($"[Proxy stderr] {e.Data}");
                };
                _proxyProcess.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        Debug.WriteLine($"[Proxy stdout] {e.Data}");
                };
                _proxyProcess.Exited += (s, e) => ProxyExited?.Invoke($"代理进程已退出 ExitCode={_proxyProcess.ExitCode}");
                _proxyProcess.Start();
                _proxyProcess.BeginErrorReadLine();
                _proxyProcess.BeginOutputReadLine();
            }

            _pipe = new NamedPipeClientStream(".", IpcProtocol.PipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await _pipe.ConnectAsync(startupTimeoutMs).ConfigureAwait(false);

            _writer = new StreamWriter(_pipe) { AutoFlush = true, NewLine = "\n" };
            _reader = new StreamReader(_pipe);

            _readLoop = Task.Run(ReadLoopAsync);

            var handshake = await SendRequestAsync(IpcProtocol.Op.Handshake, null).ConfigureAwait(false);
            if (!handshake.Success)
                throw new InvalidOperationException("代理握手失败: " + handshake.ErrorMessage);
        }

        public async Task ShutdownAsync()
        {
            try
            {
                if (IsConnected)
                {
                    await SendRequestAsync(IpcProtocol.Op.Shutdown, null, timeoutMs: 1000).ConfigureAwait(false);
                }
            }
            catch { }

            _cts.Cancel();
            try { _pipe?.Close(); } catch { }
            try { _proxyProcess?.WaitForExit(1500); } catch { }
            try { if (_proxyProcess != null && !_proxyProcess.HasExited) _proxyProcess.Kill(); } catch { }
        }

        public void Dispose()
        {
            try { ShutdownAsync().GetAwaiter().GetResult(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _pipe?.Dispose(); }   catch { }
            try { _proxyProcess?.Dispose(); } catch { }
            _writeLock.Dispose();
            _cts.Dispose();
        }

        public async Task<IpcResponse> SendRequestAsync(
            string op,
            Dictionary<string, object> payload,
            int timeoutMs = 60000)
        {
            var corrId = Interlocked.Increment(ref _correlationCounter).ToString();
            var req = new IpcRequest
            {
                Op = op,
                CorrelationId = corrId,
                Payload = payload ?? new Dictionary<string, object>(),
            };
            var tcs = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[corrId] = tcs;

            try
            {
                var json = JsonSerializer.Serialize(req, JsonOpts);
                await _writeLock.WaitAsync().ConfigureAwait(false);
                try { await _writer.WriteLineAsync(json).ConfigureAwait(false); }
                finally { _writeLock.Release(); }

                using var timeoutCts = new CancellationTokenSource(timeoutMs);
                using var reg = timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token));
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                _pending.TryRemove(corrId, out _);
            }
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var line = await _reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;

                    var doc = JsonDocument.Parse(line);
                    var msgType = doc.RootElement.GetProperty("msgType").GetString();

                    if (msgType == "response")
                    {
                        var resp = JsonSerializer.Deserialize<IpcResponse>(line, JsonOpts);
                        if (resp != null && _pending.TryRemove(resp.CorrelationId, out var tcs))
                            tcs.TrySetResult(resp);
                    }
                    else if (msgType == "notification")
                    {
                        var noti = JsonSerializer.Deserialize<IpcNotification>(line, JsonOpts);
                        if (noti != null)
                        {
                            try { NotificationReceived?.Invoke(noti); } catch { }
                        }
                    }
                }
            }
            catch
            {
                foreach (var kv in _pending)
                    kv.Value.TrySetException(new IOException("代理连接已断开"));
            }
        }
    }
}
