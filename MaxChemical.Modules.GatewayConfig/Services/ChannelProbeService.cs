using DevicePlugins.Devices;
using MaxChemical.Data.Repositories;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.GatewayConfig.Models;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MaxChemical.Modules.GatewayConfig.Services
{
  
    /// <summary>
    /// 通道连通性探测服务 — 给已绑定的通道发一帧测试命令,
    /// 确认整条 (PC → 网关 → 设备) 链路是否打通。
    /// 探测结果写入 ChannelModel.ConnectionState,UI 据此渲染颜色。
    /// </summary>
    public interface IChannelProbeService
    {
        /// <summary>
        /// 探测单个通道。结果直接写入 channel.ConnectionState。
        /// 未绑定的通道返回 Unbound。
        /// </summary>
        Task ProbeChannelAsync(GatewayModel gateway, ChannelModel channel);

        /// <summary>
        /// 探测一台网关下所有已绑定通道,并发执行。
        /// </summary>
        Task ProbeGatewayAsync(GatewayModel gateway);
    }
    public class ChannelProbeService : IChannelProbeService
    {
        private readonly IDeviceManager _deviceManager;
        private readonly IGatewayBindingRepository _bindingRepository;

        public ChannelProbeService(
            IDeviceManager deviceManager,
            IGatewayBindingRepository bindingRepository)
        {
            _deviceManager = deviceManager;
            _bindingRepository = bindingRepository;
        }

        public async Task ProbeChannelAsync(GatewayModel gateway, ChannelModel channel)
        {
            if (channel == null) return;

            if (!channel.IsBound)
            {
                channel.ConnectionState = ChannelConnectionState.Unbound;
                return;
            }

            try
            {
                var mac = NormalizeMac(gateway.Mac);
                var binding = await _bindingRepository.GetChannelBindingAsync(mac, channel.Index);
                if (binding == null)
                {
                    channel.ConnectionState = ChannelConnectionState.Unbound;
                    return;
                }

                // 找到设备实例
                var device = _deviceManager.GetAllDevices()
                    .FirstOrDefault(d => d.GetType().Name == binding.DeviceTypeName);
                if (device == null)
                {
                    channel.ConnectionState = ChannelConnectionState.BoundOffline;
                    return;
                }

                // 临时改 "DeviceId" + "通信方式" 参数 — 完事恢复,
                // 这样不会影响画布上用户原本的配置
                var deviceIdParam = device.Parameters?.Variables
                    ?.FirstOrDefault(p => p.Name == "DeviceId") as StringParameter;
                var modeParam = device.Parameters?.Variables
                    ?.FirstOrDefault(p => p.Name == "通信方式") as StringParameter;

                var originalId = deviceIdParam?.Value;
                var originalMode = modeParam?.Value;

                try
                {
                    if (deviceIdParam != null) deviceIdParam.Value = binding.DeviceInstanceId;
                    if (modeParam != null) modeParam.Value = "ZLanGateway";

                    bool connected = false;
                    try
                    {
                        connected = await device.ConnectAsync();
                    }
                    finally
                    {
                        try { await device.DisconnectAsync(); } catch { }
                    }

                    channel.ConnectionState = connected
                        ? ChannelConnectionState.BoundOnline
                        : ChannelConnectionState.BoundOffline;
                }
                finally
                {
                    // 恢复用户原本的参数
                    if (deviceIdParam != null) deviceIdParam.Value = originalId;
                    if (modeParam != null) modeParam.Value = originalMode;
                }
            }
            catch
            {
                channel.ConnectionState = ChannelConnectionState.BoundOffline;
            }
        }

        public async Task ProbeGatewayAsync(GatewayModel gateway)
        {
            if (gateway == null) return;

            var tasks = gateway.Channels
                .Where(ch => ch != null && ch.IsBound)
                .Select(ch => ProbeChannelAsync(gateway, ch))
                .ToList();

            // 把未绑定的通道也明确标成 Unbound
            foreach (var ch in gateway.Channels)
            {
                if (ch != null && !ch.IsBound)
                    ch.ConnectionState = ChannelConnectionState.Unbound;
            }

            if (tasks.Count > 0)
                await Task.WhenAll(tasks);
        }

        private static string NormalizeMac(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return "";
            return mac.Replace(":", "").Replace("-", "").Replace(" ", "").ToUpperInvariant();
        }
    }
}