using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MaxChemical.Logging;
using MaxChemical.Data.Models;
using PetaPoco;

namespace MaxChemical.Data.Services
{
    public class CustomVariableDatabaseService : ICustomVariablePersistenceService
    {
        private readonly ILogService _logger;
        private readonly IDatabaseConfigService _dbConfig;
        private readonly Database _database;

        public CustomVariableDatabaseService(ILogService logService, IDatabaseConfigService dbConfig)
        {
            _logger = logService.ForContext<CustomVariableDatabaseService>();
            _dbConfig = dbConfig ?? throw new ArgumentNullException(nameof(dbConfig));

            // 初始化PetaPoco数据库连接
            _database = new Database(_dbConfig.GetConnectionString(), "MySql.Data.MySqlClient");

            _logger.LogDebug("自定义变量数据库服务已初始化");
        }

        public async Task InitializeDatabaseAsync()
        {
            try
            {
                _logger.LogInformation("开始初始化数据库表结构...");

                // 检查并创建自定义变量表
                var createTableSql = @"
                CREATE TABLE IF NOT EXISTS `custom_variables` (
                    `id` INT PRIMARY KEY AUTO_INCREMENT,
                    `name` VARCHAR(255) NOT NULL UNIQUE,
                    `value` TEXT,
                    `type` VARCHAR(50) NOT NULL DEFAULT 'String',
                    `created_time` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    `last_modified_time` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
                    `is_from_execution_context` BOOLEAN NOT NULL DEFAULT FALSE,
                    `description` TEXT,
                    `category` VARCHAR(100) DEFAULT 'User',
                    INDEX `idx_name` (`name`),
                    INDEX `idx_category` (`category`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";

                await _database.ExecuteAsync(createTableSql);

                // 检查并创建历史记录表
                var createHistoryTableSql = @"
                CREATE TABLE IF NOT EXISTS `custom_variable_history` (
                    `id` INT PRIMARY KEY AUTO_INCREMENT,
                    `variable_id` INT NOT NULL,
                    `old_value` TEXT,
                    `new_value` TEXT,
                    `operation_type` ENUM('CREATE', 'UPDATE', 'DELETE') NOT NULL,
                    `operation_time` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    `operation_user` VARCHAR(255),
                    INDEX `idx_variable_id` (`variable_id`),
                    INDEX `idx_operation_time` (`operation_time`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";

                await _database.ExecuteAsync(createHistoryTableSql);

                _logger.LogInformation("数据库表结构初始化完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "初始化数据库表结构失败");
                throw;
            }
        }

        public async Task<List<CustomVariableData>> LoadCustomVariablesAsync()
        {
            try
            {
                var entities = await _database.FetchAsync<CustomVariableEntity>(
                    "SELECT * FROM custom_variables ORDER BY name");

                var variables = entities.Select(ConvertToBusinessModel).ToList();

                _logger.LogInformation("成功加载 {Count} 个自定义变量", variables.Count);
                return variables;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载自定义变量失败");
                return new List<CustomVariableData>();
            }
        }

        public async Task<CustomVariableData> GetCustomVariableAsync(string name)
        {
            try
            {
                var entity = await _database.FirstOrDefaultAsync<CustomVariableEntity>(
                    "SELECT * FROM custom_variables WHERE name = @0", name);

                if (entity != null)
                {
                    return ConvertToBusinessModel(entity);
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "获取自定义变量失败: {Name}", name);
                return null;
            }
        }

        public async Task<int> SaveCustomVariableAsync(CustomVariableData variable)
        {
            try
            {
                var entity = ConvertToEntity(variable);
                entity.CreatedTime = DateTime.Now;
                entity.LastModifiedTime = DateTime.Now;

                var id = await _database.InsertAsync(entity);
                int newId = Convert.ToInt32(id);
                variable.Id = newId;

                // 记录历史
                await RecordHistoryAsync(newId, null, SerializeValue(variable.Value), "CREATE");

                _logger.LogInformation("成功保存自定义变量: {Name} = {Value}, ID: {Id}", variable.Name, variable.Value, newId);
                return newId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保存自定义变量失败: {Name}", variable.Name);
                throw;
            }
        }

        public async Task<bool> UpdateCustomVariableAsync(CustomVariableData variable)
        {
            try
            {
                if (variable.Id <= 0)
                {
                    _logger.LogWarning("更新失败: 缺少ID for {Name}", variable.Name);
                    return false;
                }

                // 获取旧值用于历史记录
                var oldEntity = await _database.FirstOrDefaultAsync<CustomVariableEntity>(
                    "SELECT * FROM custom_variables WHERE id = @0", variable.Id);

                if (oldEntity == null)
                {
                    _logger.LogWarning("找不到要更新的变量: ID={Id}", variable.Id);
                    return false;
                }

                // 只更新需要更新的字段，保持创建时间不变
                var updateSql = @"
            UPDATE custom_variables 
            SET name = @0, 
                value = @1, 
                type = @2, 
                last_modified_time = @3,
                is_from_execution_context = @4,
                description = @5,
                category = @6
            WHERE id = @7";

                var rowsAffected = await _database.ExecuteAsync(updateSql,
                    variable.Name,
                    SerializeValue(variable.Value),
                    variable.Type,
                    DateTime.Now,
                    variable.IsFromExecutionContext,
                    variable.Description,
                    variable.Category ?? "User",
                    variable.Id);

                if (rowsAffected > 0)
                {
                    // 记录历史
                    await RecordHistoryAsync(variable.Id, oldEntity?.Value, SerializeValue(variable.Value), "UPDATE");

                    _logger.LogInformation("成功更新自定义变量: {Name} = {Value} (ID: {Id})", variable.Name, variable.Value, variable.Id);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更新自定义变量失败: {Name} (ID: {Id})", variable.Name, variable.Id);
                return false;
            }
        }

        public async Task<bool> DeleteCustomVariableAsync(string name)
        {
            try
            {
                // 获取变量ID用于历史记录和删除
                var entity = await _database.FirstOrDefaultAsync<CustomVariableEntity>(
                    "SELECT * FROM custom_variables WHERE name = @0", name);

                if (entity == null) return false;

                // 记录历史
                await RecordHistoryAsync(entity.Id, entity.Value, null, "DELETE");

                // 物理删除历史记录
                await _database.ExecuteAsync(
                    "DELETE FROM custom_variable_history WHERE variable_id = @0", entity.Id);

                // 物理删除主记录
                var rowsAffected = await _database.ExecuteAsync(
                    "DELETE FROM custom_variables WHERE name = @0", name);

                if (rowsAffected > 0)
                {
                    _logger.LogInformation("成功物理删除自定义变量: {Name}", name);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "物理删除自定义变量失败: {Name}", name);
                return false;
            }
        }

        public async Task<bool> DeleteCustomVariableAsync(int id)
        {
            try
            {
                // 获取变量信息用于历史记录
                var entity = await _database.FirstOrDefaultAsync<CustomVariableEntity>(
                    "SELECT * FROM custom_variables WHERE id = @0", id);

                if (entity == null) return false;

                // 记录历史
                await RecordHistoryAsync(id, entity.Value, null, "DELETE");

                // 物理删除历史记录
                await _database.ExecuteAsync(
                    "DELETE FROM custom_variable_history WHERE variable_id = @0", id);

                // 物理删除主记录
                var rowsAffected = await _database.ExecuteAsync(
                    "DELETE FROM custom_variables WHERE id = @0", id);

                if (rowsAffected > 0)
                {
                    _logger.LogInformation("成功物理删除自定义变量: ID={Id}", id);
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "物理删除自定义变量失败: ID={Id}", id);
                return false;
            }
        }

        public async Task SaveCustomVariablesAsync(IEnumerable<CustomVariableData> variables)
        {
            try
            {
                using var transaction = _database.GetTransaction();

                foreach (var variable in variables)
                {
                    if (variable.Id > 0)
                    {
                        await UpdateCustomVariableAsync(variable);
                    }
                    else
                    {
                        await SaveCustomVariableAsync(variable);
                    }
                }

                transaction.Complete();
                _logger.LogInformation("批量保存自定义变量完成");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "批量保存自定义变量失败");
                throw;
            }
        }

        public async Task<List<CustomVariableData>> GetVariablesByCategoryAsync(string category)
        {
            try
            {
                var entities = await _database.FetchAsync<CustomVariableEntity>(
                    "SELECT * FROM custom_variables WHERE category = @0 ORDER BY name", category);

                return entities.Select(ConvertToBusinessModel).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "按分类获取自定义变量失败: {Category}", category);
                return new List<CustomVariableData>();
            }
        }

        public async Task<bool> VariableExistsAsync(string name)
        {
            try
            {
                var count = await _database.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM custom_variables WHERE name = @0", name);
                return count > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检查变量是否存在失败: {Name}", name);
                return false;
            }
        }

        private async Task RecordHistoryAsync(int variableId, string oldValue, string newValue, string operationType)
        {
            try
            {
                var history = new CustomVariableHistoryEntity
                {
                    VariableId = variableId,
                    OldValue = oldValue,
                    NewValue = newValue,
                    OperationType = operationType,
                    OperationTime = DateTime.Now,
                    OperationUser = Environment.UserName
                };

                await _database.InsertAsync(history);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "记录变量历史失败，但不影响主操作");
            }
        }

        private CustomVariableData ConvertToBusinessModel(CustomVariableEntity entity)
        {
            return new CustomVariableData
            {
                Id = entity.Id,
                Name = entity.Name,
                Value = DeserializeValue(entity.Value, entity.Type),
                Type = entity.Type,
                CreatedTime = entity.CreatedTime,
                LastModifiedTime = entity.LastModifiedTime,
                IsFromExecutionContext = entity.IsFromExecutionContext,
                Description = entity.Description,
                Category = entity.Category ?? "User"
            };
        }

        private CustomVariableEntity ConvertToEntity(CustomVariableData data)
        {
            return new CustomVariableEntity
            {
                Id = data.Id,
                Name = data.Name,
                Value = SerializeValue(data.Value),
                Type = data.Type,
                CreatedTime = data.CreatedTime,
                LastModifiedTime = data.LastModifiedTime,
                IsFromExecutionContext = data.IsFromExecutionContext,
                Description = data.Description,
                Category = data.Category ?? "User"
            };
        }

        private string SerializeValue(object value)
        {
            if (value == null) return null;

            try
            {
                return JsonSerializer.Serialize(value, new JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                });
            }
            catch
            {
                return value.ToString();
            }
        }

        private object DeserializeValue(string jsonValue, string type)
        {
            if (string.IsNullOrEmpty(jsonValue)) return null;

            try
            {
                return type switch
                {
                    "String" => JsonSerializer.Deserialize<string>(jsonValue),
                    "Int32" => JsonSerializer.Deserialize<int>(jsonValue),
                    "Double" => JsonSerializer.Deserialize<double>(jsonValue),
                    "Boolean" => JsonSerializer.Deserialize<bool>(jsonValue),
                    "DateTime" => JsonSerializer.Deserialize<DateTime>(jsonValue),
                    _ => JsonSerializer.Deserialize<object>(jsonValue)
                };
            }
            catch
            {
                // 如果反序列化失败，返回原始字符串值
                return jsonValue;
            }
        }

        public void Dispose()
        {
            _database?.Dispose();
        }
    }
}