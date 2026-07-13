using System;

namespace DevicePlugins.Devices.Remote
{
    /// <summary>
    /// RemoteServer(云透传)用的标准 Modbus RTU 帧小工具：
    /// CRC16、解析写帧、构造回读帧、提取读数据、字节比对。
    /// 仅服务于"写+回读校验"的集中实现，供 <see cref="RemoteGatewayTransport"/> 使用。
    /// </summary>
    internal static class ModbusFrameUtil
    {
        public static ushort Crc16(byte[] data, int offset, int length)
        {
            ushort crc = 0xFFFF;
            for (int i = offset; i < offset + length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    bool lsb = (crc & 1) != 0;
                    crc >>= 1;
                    if (lsb) crc ^= 0xA001;
                }
            }
            return crc;
        }

        /// <summary>给不含 CRC 的报文体追加 CRC(低字节在前)。</summary>
        public static byte[] AppendCrc(byte[] body)
        {
            ushort crc = Crc16(body, 0, body.Length);
            var result = new byte[body.Length + 2];
            Buffer.BlockCopy(body, 0, result, 0, body.Length);
            result[body.Length] = (byte)(crc & 0xFF);
            result[body.Length + 1] = (byte)((crc >> 8) & 0xFF);
            return result;
        }

        /// <summary>
        /// 解析写寄存器帧(FC 0x06 / 0x10)，取出从站、功能码、寄存器地址、寄存器数量、写入的数据字节。
        /// 仅支持寄存器写(可回读校验)；线圈/其它返回 false。
        /// </summary>
        public static bool TryParseWrite(byte[] f, out byte slave, out byte fc, out ushort register, out ushort regCount, out byte[] data)
        {
            slave = 0; fc = 0; register = 0; regCount = 0; data = Array.Empty<byte>();
            if (f == null || f.Length < 6) return false;
            slave = f[0];
            fc = f[1];
            register = (ushort)((f[2] << 8) | f[3]);

            if (fc == 0x10) // 写多个寄存器
            {
                if (f.Length < 9) return false;
                regCount = (ushort)((f[4] << 8) | f[5]);
                byte byteCount = f[6];
                if (f.Length < 7 + byteCount + 2) return false;
                data = new byte[byteCount];
                Array.Copy(f, 7, data, 0, byteCount);
                return true;
            }
            if (fc == 0x06) // 写单个寄存器
            {
                if (f.Length < 8) return false;
                regCount = 1;
                data = new byte[] { f[4], f[5] };
                return true;
            }
            return false;
        }

        /// <summary>构造读保持寄存器帧(FC 0x03) + CRC。</summary>
        public static byte[] BuildReadHolding(byte slave, ushort register, ushort regCount)
        {
            byte[] body =
            {
                slave, 0x03,
                (byte)(register >> 8), (byte)(register & 0xFF),
                (byte)(regCount >> 8), (byte)(regCount & 0xFF),
            };
            return AppendCrc(body);
        }

        /// <summary>从 FC 0x03 应答帧提取数据载荷(去掉站号/功能码/字节数/CRC)。</summary>
        public static bool TryExtractReadData(byte[] resp, out byte[] data)
        {
            data = Array.Empty<byte>();
            if (resp == null || resp.Length < 5) return false;
            if ((resp[1] & 0x80) != 0) return false; // 异常应答
            byte byteCount = resp[2];
            if (resp.Length < 3 + byteCount + 2) return false;
            data = new byte[byteCount];
            Array.Copy(resp, 3, data, 0, byteCount);
            return true;
        }

        public static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
