using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;

namespace DevicePlugins.Devices
{
    /// <summary>
    /// 串口下拉项工具:把驱动里写死的 COM1~COM7 换成本机实际存在的串口。
    ///
    /// 两个使用时机:
    ///   1) 驱动构造时 —— <see cref="CreateOptions"/>,给出装载那一刻的串口列表;
    ///   2) 打开设备属性对话框时 —— <see cref="RefreshDeviceOptions"/>,重新扫描,
    ///      这样 USB 转 485 是在程序启动之后才插上的也能刷出来。
    ///
    /// 两条硬约束(StringParameter.ValidateValue 决定的):
    ///   · Options 非空时,Value 必须在 Options 里,否则命令执行前的参数校验直接失败。
    ///     所以已选中的值即使当下机器上没有(换电脑打开别人的存档),也必须留在列表里。
    ///   · 一个口都扫不到时不能把 Options 清空,否则下拉变空、原值落不进去。
    /// </summary>
    public static class SerialPortOptions
    {
        /// <summary>被认作"串口选择"的参数名。各驱动历史命名不统一,这里全兜住。</summary>
        private static readonly string[] SerialParameterNames =
        {
            "串口号", "串口", "端口号", "COM Port", "COMPort", "COM口"
        };

        /// <summary>兜底列表:机器上一个串口都没有时用,免得下拉框是空的。</summary>
        private static readonly string[] Fallback =
        {
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8"
        };

        /// <summary>
        /// 扫描本机串口。按口号数值排序,让 COM2 排在 COM10 前面
        /// (直接按字符串排会得到 COM1, COM10, COM2 这种顺序)。
        /// </summary>
        public static List<string> Scan()
        {
            try
            {
                return SerialPort.GetPortNames()
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(PortNumber)
                    .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                // 没有串口驱动/权限不足时 GetPortNames 会抛,不能让驱动构造挂掉
                return new List<string>();
            }
        }

        /// <summary>
        /// 供驱动构造函数直接用:扫到什么给什么,一个都没有就给兜底列表。
        /// 用法:new StringParameter("串口号", ...) { Options = SerialPortOptions.CreateOptions() }
        /// </summary>
        public static ObservableCollection<string> CreateOptions()
        {
            var ports = Scan();
            return new ObservableCollection<string>(ports.Count > 0 ? ports : Fallback);
        }

        /// <summary>
        /// 驱动构造用的另一个重载:同时决定默认选中哪个口。
        /// 返回值是建议的默认值(第一个可用串口;一个都没有时返回 preferred 或 "COM1")。
        /// </summary>
        public static string DefaultPort(string preferred = "COM1")
        {
            var ports = Scan();
            if (ports.Count == 0) return preferred;
            // 首选口还在就用首选,否则退到第一个实际存在的
            return ports.Any(p => string.Equals(p, preferred, StringComparison.OrdinalIgnoreCase))
                ? preferred
                : ports[0];
        }

        /// <summary>
        /// 重新扫描并刷新设备上所有串口参数的下拉项。宿主在打开设备属性对话框时调用。
        /// 扫不到任何串口时原样保留,不动。
        /// </summary>
        public static void RefreshDeviceOptions(IDevice device)
        {
            var variables = device?.Parameters?.Variables;
            if (variables == null) return;

            var ports = Scan();
            if (ports.Count == 0) return;   // 没扫到就别动,保留原有选项

            foreach (var variable in variables)
            {
                if (variable is not StringParameter parameter) continue;
                if (!SerialParameterNames.Contains(parameter.Name, StringComparer.OrdinalIgnoreCase)) continue;

                ApplyOptions(parameter, ports);
            }
        }

        private static void ApplyOptions(StringParameter parameter, List<string> ports)
        {
            // 原样保留当前值(Options.Contains 是区分大小写的序数比较,不能改写它)
            var current = parameter.Value?.ToString();

            var list = new List<string>(ports);
            if (!string.IsNullOrWhiteSpace(current) && !list.Contains(current))
            {
                // 存档里的口这台机器上没有 —— 仍要留着,否则参数校验会不过
                list.Add(current);
            }

            if (parameter.Options.SequenceEqual(list, StringComparer.Ordinal)) return;

            // 原地增删而不是换新集合:ComboBox 绑的是这个集合实例
            parameter.Options.Clear();
            foreach (var port in list) parameter.Options.Add(port);

            // Clear() 可能让绑定把 SelectedItem 打成 null 并回写 Value,这里补回来
            if (!string.IsNullOrWhiteSpace(current))
            {
                if (!Equals(parameter.Value, current)) parameter.Value = current;
            }
            else if (parameter.Options.Count > 0)
            {
                parameter.Value = parameter.Options[0];
            }
        }

        /// <summary>"COM10" → 10;取不到数字的排到最后。</summary>
        private static int PortNumber(string name)
        {
            var digits = new string(name.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out var n) ? n : int.MaxValue;
        }
    }
}
