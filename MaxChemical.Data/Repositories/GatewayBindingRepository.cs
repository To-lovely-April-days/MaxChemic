using MaxChemical.Data.Models;
using MaxChemical.Data.Services;
using MaxChemical.Logging;
using PetaPoco;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace MaxChemical.Data.Repositories
{
    public class GatewayBindingRepository : IGatewayBindingRepository
    {
        private readonly IDatabaseConnectionFactory _connectionFactory;
        private readonly ILogService _logger;

        private const string CreateTableSql = @"
CREATE TABLE IF NOT EXISTS `gateway_channel_bindings` (
    `id` INT PRIMARY KEY AUTO_INCREMENT,
    `gateway_mac` VARCHAR(32) NOT NULL,
    `gateway_ip` VARCHAR(32) NOT NULL,
    `channel_index` INT NOT NULL,
    `channel_local_port` INT NOT NULL,
    `device_type_name` VARCHAR(255) NOT NULL,
    `device_instance_id` VARCHAR(64) NOT NULL,
    `device_display_name` VARCHAR(255) DEFAULT NULL,
    `created_time` DATETIME NOT NULL,
    `updated_time` DATETIME NOT NULL,
    UNIQUE KEY `uk_gateway_channel` (`gateway_mac`, `channel_index`),
    UNIQUE KEY `uk_device_instance` (`device_type_name`, `device_instance_id`),
    INDEX `idx_gateway_mac` (`gateway_mac`),
    INDEX `idx_device_instance_id` (`device_instance_id`)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;";

        public GatewayBindingRepository(IDatabaseConnectionFactory connectionFactory, ILogService logger)
        {
            _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _logger = logger?.ForContext<GatewayBindingRepository>() ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task InitializeAsync()
        {
            await _connectionFactory.ExecuteAsync(async database =>
            {
                await database.ExecuteAsync(CreateTableSql);
                _logger.LogInformation("gateway_channel_bindings 表初始化完成");
            });
        }

        public async Task<List<GatewayChannelBinding>> GetByGatewayAsync(string gatewayMac)
        {
            var normalized = NormalizeMac(gatewayMac);
            return await _connectionFactory.ExecuteAsync(async database =>
            {
                return await database.FetchAsync<GatewayChannelBinding>(
                    "WHERE gateway_mac = @0 ORDER BY channel_index", normalized);
            });
        }

        public async Task<GatewayChannelBinding?> GetChannelBindingAsync(string gatewayMac, int channelIndex)
        {
            var normalized = NormalizeMac(gatewayMac);
            return await _connectionFactory.ExecuteAsync(async database =>
            {
                return await database.SingleOrDefaultAsync<GatewayChannelBinding>(
                    "WHERE gateway_mac = @0 AND channel_index = @1", normalized, channelIndex);
            });
        }

        public async Task<GatewayChannelBinding?> GetByDeviceAsync(string deviceTypeName, string deviceInstanceId)
        {
            return await _connectionFactory.ExecuteAsync(async database =>
            {
                return await database.SingleOrDefaultAsync<GatewayChannelBinding>(
                    "WHERE device_type_name = @0 AND device_instance_id = @1",
                    deviceTypeName, deviceInstanceId);
            });
        }

        public async Task<List<GatewayChannelBinding>> GetAllAsync()
        {
            return await _connectionFactory.ExecuteAsync(async database =>
            {
                return await database.FetchAsync<GatewayChannelBinding>(
                    "ORDER BY gateway_mac, channel_index");
            });
        }

        public async Task<bool> UpsertAsync(GatewayChannelBinding binding)
        {
            binding.GatewayMac = NormalizeMac(binding.GatewayMac);
            binding.UpdatedTime = DateTime.Now;

            return await _connectionFactory.ExecuteAsync(async database =>
            {
                using var transaction = database.GetTransaction();
                try
                {
                    // 1) 删掉该通道的旧绑定
                    await database.ExecuteAsync(
                        "DELETE FROM gateway_channel_bindings WHERE gateway_mac = @0 AND channel_index = @1",
                        binding.GatewayMac, binding.ChannelIndex);

                    // 2) 删掉该设备实例的旧绑定 (双向唯一,设备可能之前绑在别的通道)
                    await database.ExecuteAsync(
                        "DELETE FROM gateway_channel_bindings WHERE device_type_name = @0 AND device_instance_id = @1",
                        binding.DeviceTypeName, binding.DeviceInstanceId);

                    // 3) 插入新记录
                    if (binding.CreatedTime == default)
                        binding.CreatedTime = DateTime.Now;
                    await database.InsertAsync(binding);

                    transaction.Complete();
                    _logger.LogInformation("绑定成功: 网关 {Mac} 通道 {Ch} → {Device} {Id}",
                        binding.GatewayMac, binding.ChannelIndex,
                        binding.DeviceTypeName, binding.DeviceInstanceId);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "绑定写入失败");
                    return false;
                }
            });
        }

        public async Task<bool> DeleteByChannelAsync(string gatewayMac, int channelIndex)
        {
            var normalized = NormalizeMac(gatewayMac);
            return await _connectionFactory.ExecuteAsync(async database =>
            {
                var n = await database.ExecuteAsync(
                    "DELETE FROM gateway_channel_bindings WHERE gateway_mac = @0 AND channel_index = @1",
                    normalized, channelIndex);
                _logger.LogInformation("解绑通道: {Mac}/{Ch}, 影响行数 {N}", normalized, channelIndex, n);
                return n > 0;
            });
        }

        public async Task<bool> DeleteByDeviceAsync(string deviceTypeName, string deviceInstanceId)
        {
            return await _connectionFactory.ExecuteAsync(async database =>
            {
                var n = await database.ExecuteAsync(
                    "DELETE FROM gateway_channel_bindings WHERE device_type_name = @0 AND device_instance_id = @1",
                    deviceTypeName, deviceInstanceId);
                return n > 0;
            });
        }

        /// <summary>把 "28:89:D8:A4:B2:15" 标准化为 "2889D8A4B215" 大写</summary>
        private static string NormalizeMac(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return "";
            return mac.Replace(":", "").Replace("-", "").Replace(" ", "").ToUpperInvariant();
        }
    }
}