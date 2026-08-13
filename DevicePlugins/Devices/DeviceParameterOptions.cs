using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;
using MaxChemical.Core;

namespace DevicePlugins.Devices
{
    /// <summary>
    /// 设备通用参数的下拉选项与整块参数的统一入口。
    ///
    /// 为什么要集中:七个 RTU 驱动里「串口号/波特率/校验位/数据位/停止位/通信方式」
    /// 是一字不差抄了七遍的。抄多了必然漂 —— 平流泵的「通信方式」就漏了 RemoteServer,
    /// 导致那台设备在界面上选不到云模式。
    ///
    /// 为什么校验位/停止位/通信方式要从枚举生成而不是手写字符串:
    /// 这些值最终是被 Enum.Parse 吃掉的(SerialPortTransport.OpenPort 里
    /// (Parity)Enum.Parse(typeof(Parity), config.Parity),既没有 try/catch
    /// 也没有 ignoreCase)。手写错一个字母不是下拉框难看,是连接时直接抛异常。
    /// 从枚举取名字,下拉里就只可能出现解析得掉的值,枚举加了新成员也自动跟上。
    ///
    /// 每个方法都返回新集合,不共享静态实例 —— Options 是可变的
    /// (SerialPortOptions.RefreshDeviceOptions 会原地增删串口项),
    /// 共享一份会让所有设备的下拉互相串台。
    /// </summary>
    public static class DeviceParameterOptions
    {
        /// <summary>波特率。没有对应枚举,按常用档位列;消费端是 int.Parse。</summary>
        public static readonly string[] BaudRateValues =
        {
            "4800", "9600", "19200", "38400", "57600", "115200", "230400"
        };

        /// <summary>数据位。SerialPort.DataBits 合法范围 5~8。</summary>
        public static readonly string[] DataBitsValues = { "5", "6", "7", "8" };

        public static ObservableCollection<string> BaudRates() => New(BaudRateValues);

        public static ObservableCollection<string> DataBits() => New(DataBitsValues);

        /// <summary>校验位:取自 System.IO.Ports.Parity(None/Odd/Even/Mark/Space)。</summary>
        public static ObservableCollection<string> Parities() => New(Enum.GetNames(typeof(Parity)));

        /// <summary>停止位:取自 System.IO.Ports.StopBits(None/One/Two/OnePointFive)。</summary>
        public static ObservableCollection<string> StopBitsOptions() => New(Enum.GetNames(typeof(StopBits)));

        /// <summary>通信方式:取自 MaxChemical.Core.CommunicationMode,新增传输方式后各驱动自动可选。</summary>
        public static ObservableCollection<string> CommunicationModes() => New(Enum.GetNames(typeof(CommunicationMode)));

        /// <summary>
        /// 一次性把 RTU 驱动都要的六个通信参数加进去,顺序与原来各驱动手写的一致:
        /// 串口号 → 波特率 → 校验位 → 数据位 → 停止位 → 通信方式。
        ///
        /// 串口号的候选项来自本机实际扫描(见 SerialPortOptions),
        /// 打开设备属性对话框时宿主还会再扫一次刷新。
        ///
        /// 默认值都可以按设备改,比如某些表默认 19200:
        ///   DeviceParameterOptions.AddSerialParameters(Parameters, baudRate: "19200");
        /// </summary>
        public static void AddSerialParameters(
            DeviceParameters parameters,
            string baudRate = "9600",
            string parity = "None",
            string dataBits = "8",
            string stopBits = "One",
            string communicationMode = "Direct",
            string preferredPort = "COM1")
        {
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));

            parameters.Variables.Add(new StringParameter("串口号", SerialPortOptions.DefaultPort(preferredPort))
            {
                Options = SerialPortOptions.CreateOptions()
            });

            parameters.Variables.Add(new StringParameter("波特率", baudRate)
            {
                Options = BaudRates()
            });

            parameters.Variables.Add(new StringParameter("校验位", parity)
            {
                Options = Parities()
            });

            parameters.Variables.Add(new StringParameter("数据位", dataBits)
            {
                Options = DataBits()
            });

            parameters.Variables.Add(new StringParameter("停止位", stopBits)
            {
                Options = StopBitsOptions()
            });

            parameters.Variables.Add(new StringParameter("通信方式", communicationMode, "通信方式")
            {
                Options = CommunicationModes()
            });
        }

        private static ObservableCollection<string> New(IEnumerable<string> values)
            => new ObservableCollection<string>(values.ToList());
    }
}
