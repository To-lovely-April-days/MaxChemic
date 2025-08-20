using System;
using System.Configuration;
using MySql.Data.MySqlClient;
using MaxChemical.Logging;

namespace MaxChemical.Data.Services
{
    public class DatabaseConfigService : IDatabaseConfigService
    {
        private readonly ILogService? _logger;
        private readonly string _connectionString;

        public DatabaseConfigService(ILogService _logger)
        {
            _logger = _logger.ForContext<DatabaseConfigService>();

            // 从配置文件读取连接字符串，或使用默认配置
            _connectionString = ConfigurationManager.ConnectionStrings["MaxChemicalDB"]?.ConnectionString
                ?? "Server=localhost;Database=maxchemical;Uid=root;Pwd=123456;CharSet=utf8mb4;SslMode=None;AllowUserVariables=True;";

            _logger.LogDebug("数据库连接字符串已初始化");
        }

        public string GetConnectionString()
        {
            return _connectionString;
        }

        public bool TestConnection()
        {
            try
            {
                using var connection = new MySqlConnection(_connectionString);
                connection.Open();
                _logger.LogDebug("数据库连接测试成功");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "数据库连接测试失败");
                return false;
            }
        }
    }
}
