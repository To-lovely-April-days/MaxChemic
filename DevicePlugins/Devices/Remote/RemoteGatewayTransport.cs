using System;
using System.Threading;
using System.Threading.Tasks;
using MaxChemical.Core;
using MaxChemical.Logging;

namespace DevicePlugins.Devices.Remote
{
    /// <summary>
    /// 自建云服务器模式下的字节通道，实现 <see cref="IDeviceTransport"/>。
    /// 按设备配置的「DTU序列号」，经 SignalR 隧道把 Modbus 字节发到云端 DTU 网关，
    /// 上层 Modbus 驱动(BuildReadCmd/CRC/解析)零改动复用。
    ///
    /// 写命令(SendAsync)在此集中做"写+回读校验"：下发写帧→回读同寄存器→比对，
    /// 一致才算成功(幂等、有限重试、最终失败返回 false 让上层报警)。
    /// 所有走 RemoteServer 的字节级驱动因此自动获得写校验，无需各自实现。
    /// </summary>
    public class RemoteGatewayTransport : IDeviceTransport
    {
        private const int WriteVerifyRetries = 3;
        private const int RetryDelayMs = 200;

        private readonly string _serialNo;
        private readonly ILogService? _logger;

        public RemoteGatewayTransport(string serialNo)
        {
            _serialNo = serialNo;
            _logger = LogManager.GetLogger<RemoteGatewayTransport>();
        }

        private int TimeoutMs => Math.Max(2000, DeviceConnectionConfig.RemoteServerSettings.RequestTimeoutMs);

        public async Task<int> SendAndReceiveAsync(byte[] sendBuffer, byte[] recvBuffer, int recvTimeoutMs, CancellationToken ct)
        {
            var resp = await RemoteGatewayClient.Instance
                .SendAndReceiveAsync(_serialNo, sendBuffer, recvTimeoutMs, ct)
                .ConfigureAwait(false);

            if (resp == null || resp.Length == 0)
            {
                throw new TimeoutException($"云服务器未返回 DTU[{_serialNo}] 的应答（超时或离线）");
            }

            int len = Math.Min(resp.Length, recvBuffer.Length);
            Array.Copy(resp, 0, recvBuffer, 0, len);
            return len;
        }

        /// <summary>
        /// 写命令：寄存器写(FC06/10)走"写+回读校验"；其它(线圈等不可回读的)退回普通下发。
        /// </summary>
        public async Task<bool> SendAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            byte[] frame = (offset == 0 && count == buffer.Length)
                ? buffer
                : Slice(buffer, offset, count);

            // 仅寄存器写可回读校验
            if (ModbusFrameUtil.TryParseWrite(frame, out byte slave, out _, out ushort register, out ushort regCount, out byte[] writtenData))
            {
                byte[] readFrame = ModbusFrameUtil.BuildReadHolding(slave, register, regCount);

                for (int attempt = 1; attempt <= WriteVerifyRetries; attempt++)
                {
                    try
                    {
                        var resp = await RemoteGatewayClient.Instance
                            .WriteThenReadAsync(_serialNo, frame, readFrame, TimeoutMs, ct)
                            .ConfigureAwait(false);

                        if (resp != null && resp.Length > 0
                            && ModbusFrameUtil.TryExtractReadData(resp, out var readData)
                            && ModbusFrameUtil.BytesEqual(readData, writtenData))
                        {
                            return true;
                        }

                        _logger?.LogWarning("写后回读不一致 (第 {Attempt}/{Total} 次): DTU={Serial} reg=0x{Reg:X4}",
                            attempt, WriteVerifyRetries, _serialNo, register);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning("写+回读失败 (第 {Attempt}/{Total} 次): DTU={Serial} {Error}",
                            attempt, WriteVerifyRetries, _serialNo, ex.Message);
                    }

                    if (attempt < WriteVerifyRetries)
                        await Task.Delay(RetryDelayMs, ct).ConfigureAwait(false);
                }

                _logger?.LogError("写后回读校验最终失败: DTU={Serial} reg=0x{Reg:X4}", _serialNo, register);
                return false;
            }

            // 非寄存器写(如线圈)：无法可靠回读，普通下发
            return await RemoteGatewayClient.Instance.SendAsync(_serialNo, frame, ct).ConfigureAwait(false);
        }

        private static byte[] Slice(byte[] buffer, int offset, int count)
        {
            var data = new byte[count];
            Array.Copy(buffer, offset, data, 0, count);
            return data;
        }

        public void Dispose()
        {
            // 隧道由 RemoteGatewayClient 单例统一管理，单次 transport 不关闭
        }
    }
}
