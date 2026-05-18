using MaxChemical.Data.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MaxChemical.Data.Repositories
{
    /// <summary>
    /// 网关通道 ↔ 设备 绑定关系仓储。
    /// </summary>
    public interface IGatewayBindingRepository
    {
        /// <summary>首次使用前调用,创建表(幂等)</summary>
        Task InitializeAsync();

        /// <summary>查询某台网关的所有通道绑定</summary>
        Task<List<GatewayChannelBinding>> GetByGatewayAsync(string gatewayMac);

        /// <summary>查询某个特定通道的绑定 (没绑则返回 null)</summary>
        Task<GatewayChannelBinding?> GetChannelBindingAsync(string gatewayMac, int channelIndex);

        /// <summary>查询某个设备实例的绑定 (用于"已绑定"标记)</summary>
        Task<GatewayChannelBinding?> GetByDeviceAsync(string deviceTypeName, string deviceInstanceId);

        /// <summary>查询所有绑定 (设备驱动启动时用这个建路由表)</summary>
        Task<List<GatewayChannelBinding>> GetAllAsync();

        /// <summary>新增或更新一条绑定 (通道为主键,会先删旧的)</summary>
        Task<bool> UpsertAsync(GatewayChannelBinding binding);

        /// <summary>删除通道绑定</summary>
        Task<bool> DeleteByChannelAsync(string gatewayMac, int channelIndex);

        /// <summary>删除设备绑定 (按设备实例)</summary>
        Task<bool> DeleteByDeviceAsync(string deviceTypeName, string deviceInstanceId);
    }
}