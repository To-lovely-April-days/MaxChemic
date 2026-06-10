using System;
using System.IO;
using System.Threading.Tasks;

namespace MaxChemical.GatewayConfig.Proxy
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            var logPath = Path.Combine(Path.GetTempPath(), "GatewayConfigProxy.log");
            void Log(string msg)
            {
                var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
                Console.Error.WriteLine(line);
                try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
            }

            Log($"=== Proxy 启动 ===  Is64BitProcess={Environment.Is64BitProcess}");

            if (Environment.Is64BitProcess)
            {
                Log("[FATAL] must run as x86 process");
                return 2;
            }

            SdkExecutor executor;
            try
            {
                executor = new SdkExecutor();
                Log("ZLDM_Init 成功");
            }
            catch (Exception ex)
            {
                Log($"[FATAL] SdkExecutor 构造失败: {ex.Message}");
                return 3;
            }

            using (executor)
            using (var server = new PipeServer(executor))
            {
                Console.CancelKeyPress += (s, e) => { e.Cancel = true; server.Dispose(); };
                try
                {
                    Log("PipeServer 启动,等待连接...");
                    await server.RunAsync().ConfigureAwait(false);
                    return 0;
                }
                catch (Exception ex)
                {
                    Log($"[FATAL] PipeServer: {ex}");
                    return 1;
                }
            }
        }
    }
}
