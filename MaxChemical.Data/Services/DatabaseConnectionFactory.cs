using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MaxChemical.Logging;
using PetaPoco;

namespace MaxChemical.Data.Services
{
    public class DatabaseConnectionFactory : IDatabaseConnectionFactory
    {
        private readonly IDatabaseConfigService _dbConfig;
        private readonly ILogService _logger;

        public DatabaseConnectionFactory(IDatabaseConfigService dbConfig, ILogService logger)
        {
            _dbConfig = dbConfig ?? throw new ArgumentNullException(nameof(dbConfig));
            _logger = logger?.ForContext<DatabaseConnectionFactory>() ?? throw new ArgumentNullException(nameof(logger));
        }

        public IDatabase CreateConnection()
        {
            return new Database(_dbConfig.GetConnectionString(), "MySql.Data.MySqlClient");
        }

        // 封装数据库操作，自动处理连接管理
        public async Task<T> ExecuteAsync<T>(Func<IDatabase, Task<T>> operation)
        {
            try
            {
                using var database = CreateConnection();
                return await operation(database);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "数据库操作执行失败");
                throw;
            }
        }

        public async Task ExecuteAsync(Func<IDatabase, Task> operation)
        {
            try
            {
                using var database = CreateConnection();
                await operation(database);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "数据库操作执行失败");
                throw;
            }
        }
    }
}
