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

        public DatabaseConfigService(ILogService logger)
        {
            _logger = logger.ForContext<DatabaseConfigService>();

            // 从配置文件读取连接字符串，或使用默认配置
            // DatabaseConfigService.cs
            _connectionString = ConfigurationManager.ConnectionStrings["MaxChemicalDB"]?.ConnectionString
                ?? "Server=localhost;Database=maxchemical;Uid=root;Pwd=123456;" +
                   "CharSet=utf8mb4;SslMode=None;AllowUserVariables=True;" +
                   "AllowPublicKeyRetrieval=True;";  // ← 添加这个  MySQL 8.0+ 默认使用 caching_sha2_password 认证插件导致的，需要获取 RSA 公钥来加密密码，但连接字符串中没有允许此操作。

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
                _logger?.LogDebug("数据库连接测试成功");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "数据库连接测试失败");
                return false;
            }
        }
    }
}
