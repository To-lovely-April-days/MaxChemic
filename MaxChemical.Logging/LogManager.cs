using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Serilog.Events;
using Serilog;

namespace MaxChemical.Logging
{
    public static class LogManager
    {
        private static bool _isInitialized = false;
        private static readonly object _lock = new object();

        public static void Initialize(string? logDirectory = null, LogEventLevel minimumLevel = LogEventLevel.Information)
        {
            lock (_lock)
            {
                if (_isInitialized) return;

                logDirectory ??= Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");

                // 确保日志目录存在
                if (!Directory.Exists(logDirectory))
                {
                    Directory.CreateDirectory(logDirectory);
                }

                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Is(minimumLevel)
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                    .MinimumLevel.Override("System", LogEventLevel.Warning)
                    .Enrich.FromLogContext()
                    .WriteTo.Console(
                        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                    .WriteTo.File(
                        path: Path.Combine(logDirectory, "maxchemical-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 30,
                        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
                    .CreateLogger();

                _isInitialized = true;
            }
        }

        public static ILogService GetLogger<T>()
        {
            EnsureInitialized();
            return new LogService(Log.ForContext<T>());
        }

        public static ILogService GetLogger(string contextName)
        {
            EnsureInitialized();
            return new LogService(Log.ForContext("SourceContext", contextName));
        }

        public static void Shutdown()
        {
            Log.CloseAndFlush();
        }

        private static void EnsureInitialized()
        {
            if (!_isInitialized)
            {
                Initialize(); // 使用默认配置初始化
            }
        }
    }
}
