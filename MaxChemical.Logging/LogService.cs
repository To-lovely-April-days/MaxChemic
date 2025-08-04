using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Serilog;

namespace MaxChemical.Logging
{
    public class LogService : ILogService
    {
        private readonly ILogger _logger;

        public LogService() : this(Log.Logger)
        {
        }

        public LogService(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void LogTrace(string message, params object[] args)
        {
            _logger.Verbose(message, args);
        }

        public void LogDebug(string message, params object[] args)
        {
            _logger.Debug(message, args);
        }

        public void LogInformation(string message, params object[] args)
        {
            _logger.Information(message, args);
        }

        public void LogWarning(string message, params object[] args)
        {
            _logger.Warning(message, args);
        }

        public void LogError(string message, params object[] args)
        {
            _logger.Error(message, args);
        }

        public void LogError(Exception exception, string message, params object[] args)
        {
            _logger.Error(exception, message, args);
        }

        public void LogCritical(string message, params object[] args)
        {
            _logger.Fatal(message, args);
        }

        public void LogCritical(Exception exception, string message, params object[] args)
        {
            _logger.Fatal(exception, message, args);
        }

        public ILogService ForContext<T>()
        {
            return new LogService(_logger.ForContext<T>());
        }

        public ILogService ForContext(string contextName)
        {
            return new LogService(_logger.ForContext("SourceContext", contextName));
        }
    }
}
