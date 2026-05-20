using System;
using System.Runtime.InteropServices;

namespace MaxChemical.GatewayConfig.Proxy.Native
{
    /// <summary>
    /// ZLDevManage.dll (32-bit) 的 P/Invoke 包装。
    /// 调用约定:__stdcall;字符串使用 ANSI;返回值约定:int API 返回 1=成功,0=失败 (Win32 BOOL)。
    /// DeviceId 是 12 字符 Hex 字符串 (MAC 的 ASCII 形式),如 "284E40A50001"。
    /// </summary>
    internal static class ZLDevManageNative
    {
        private const string DllName = "ZLDevManage.dll";
        private const CallingConvention Conv = CallingConvention.StdCall;

        // ─── 参数类型常量 ───
        public const int PARAM_SET_TO_DEFAULT = 0;
        public const int PARAM_DEV_EXIST_IN_SUBNET = 1;
        public const int PARAM_DEV_LOCAL_IP = 2;
        public const int PARAM_DEV_LOCAL_PORT = 3;
        public const int PARAM_DEV_OUTER_IP = 4;
        public const int PARAM_ALL_SUBNET_ID = 5;
        public const int PARAM_SIMULATE_PORT = 6;

        public const int PARAM_WORK_MODE = 256;
        public const int PARAM_IP_MODE = 257;
        public const int PARAM_FLOW_CONTROL = 258;
        public const int PARAM_DEST_MODE = 259;
        public const int PARAM_APP_PROTOCOL = 260;
        public const int PARAM_STOP_BIT = 261;
        public const int PARAM_PARITY = 262;
        public const int PARAM_DATA_BITS = 263;
        public const int PARAM_BAUNDRATE = 264;
        public const int PARAM_DNS_SERVER_IP = 265;
        public const int PARAM_UDP_GROUP_IP = 266;
        public const int PARAM_NET_MASK = 268;
        public const int PARAM_GATEWAY = 269;
        public const int PARAM_RECONNECT_TIME = 270;
        public const int PARAM_KEEP_ALIVE_TIME = 271;
        public const int PARAM_WEB_PORT = 272;
        public const int PARAM_DEV_VER = 273;
        public const int PARAM_DEST_PORT = 275;
        public const int PARAM_DEV_NAME = 276;
        public const int PARAM_DEST_IP = 277;
        public const int PARAM_GAP_TIME = 278;
        public const int PARAM_PACKING_LEN = 279;
        public const int PARAM_LINK_STATUS = 280;
        public const int PARAM_RESOURCE_IP = 281;  // ← 新增备用:设备自身资源 IP
        public const int PARAM_RESOURCE_PORT = 282;  // ← 新增备用:设备自身资源端口

        // ─── 生命周期 ───
        [DllImport(DllName, CallingConvention = Conv)]
        public static extern int ZLDM_Init(int serverPort);

        [DllImport(DllName, CallingConvention = Conv)]
        public static extern void ZLDM_Exit();

        [DllImport(DllName, CallingConvention = Conv)]
        public static extern IntPtr ZLDM_GetVer();

        // ─── 设备搜索 ───
        [DllImport(DllName, CallingConvention = Conv)]
        public static extern int ZLDM_StartSearchDev();

        [DllImport(DllName, CallingConvention = Conv)]
        public static extern IntPtr ZLDM_GetDevID(int devIndex);

        // ─── 参数读取 ───
        [DllImport(DllName, CallingConvention = Conv, CharSet = CharSet.Ansi)]
        public static extern IntPtr ZLDM_GetDevParamString(
            [MarshalAs(UnmanagedType.LPStr)] string id, int paramType);

        [DllImport(DllName, CallingConvention = Conv, CharSet = CharSet.Ansi)]
        public static extern int ZLDM_GetDevParamInt(
            [MarshalAs(UnmanagedType.LPStr)] string id, int paramType);

        // ─── 参数写入 ───
        [DllImport(DllName, CallingConvention = Conv, CharSet = CharSet.Ansi)]
        public static extern int ZLDM_SetDevParamString(
            [MarshalAs(UnmanagedType.LPStr)] string id,
            [MarshalAs(UnmanagedType.LPStr)] string newParam,
            int paramType);

        [DllImport(DllName, CallingConvention = Conv, CharSet = CharSet.Ansi)]
        public static extern int ZLDM_SetDevParamInt(
            [MarshalAs(UnmanagedType.LPStr)] string id, int newParam, int paramType);

        [DllImport(DllName, CallingConvention = Conv, CharSet = CharSet.Ansi)]
        public static extern int ZLDM_SetDevParamExcute(
            [MarshalAs(UnmanagedType.LPStr)] string id);

        // ─── 工具:IntPtr → string (ANSI) ───
        public static string PtrToString(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero) return string.Empty;
            return Marshal.PtrToStringAnsi(ptr) ?? string.Empty;
        }
    }
}