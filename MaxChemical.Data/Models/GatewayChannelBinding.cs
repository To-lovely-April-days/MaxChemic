using PetaPoco;
using System;

namespace MaxChemical.Data.Models
{
    /// <summary>
    /// 网关通道 ↔ 设备 绑定关系。
    /// 双向唯一:
    ///   (gateway_mac, channel_index) 一个通道最多绑 1 个设备
    ///   (device_type_name, device_instance_id) 一个设备实例最多绑到 1 个通道
    /// </summary>
    [TableName("gateway_channel_bindings")]
    [PrimaryKey("id", AutoIncrement = true)]
    public class GatewayChannelBinding
    {
        [Column("id")]
        public int Id { get; set; }

        /// <summary>网关身份标识 — MAC 字符串 (12 字符 Hex,无分隔符,如 "2889D8A4B215")</summary>
        [Column("gateway_mac")]
        public string GatewayMac { get; set; } = "";

        /// <summary>当前 IP — 仅供显示和路由,可能因 DHCP 变化</summary>
        [Column("gateway_ip")]
        public string GatewayIp { get; set; } = "";

        /// <summary>通道索引 0..3 — 对应 RS-232/485 (1)..(4)</summary>
        [Column("channel_index")]
        public int ChannelIndex { get; set; }

        /// <summary>通道 TCP 监听端口 (从 SDK 读出的 PARAM_DEV_LOCAL_PORT)</summary>
        [Column("channel_local_port")]
        public int ChannelLocalPort { get; set; }

        /// <summary>设备类型完整类名,如 "SiphonInfusionPump_ModbusRTU"</summary>
        [Column("device_type_name")]
        public string DeviceTypeName { get; set; } = "";

        /// <summary>设备实例 ID — 用户给设备起的名字,如 "SIP0001"</summary>
        [Column("device_instance_id")]
        public string DeviceInstanceId { get; set; } = "";

        /// <summary>设备显示名称 — 如 "平流输液泵"</summary>
        [Column("device_display_name")]
        public string DeviceDisplayName { get; set; } = "";

        [Column("created_time")]
        public DateTime CreatedTime { get; set; }

        [Column("updated_time")]
        public DateTime UpdatedTime { get; set; }
    }
}