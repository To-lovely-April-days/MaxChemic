using System.Globalization;
using System.Text;
using System.Text.Json;
using DevicePlugins.Devices;
using MaxChemical.Infrastructure.Services;

namespace MaxChemical.Shell.Services.Agent.Experiment
{
    /// <summary>设备蓝图的只读快照，避免把设备库中的共享实例交给计划生成或校验逻辑修改。</summary>
    internal sealed class ExperimentDeviceBlueprint
    {
        public string LibraryName { get; init; } = "";
        public string DeviceType { get; init; } = "";
        public string Category { get; init; } = "";
        public string ReadError { get; init; } = "";
        public List<ExperimentCommandBlueprint> Commands { get; init; } = new();
    }

    internal sealed class ExperimentCommandBlueprint
    {
        public string Name { get; init; } = "";
        public string HelpText { get; init; } = "";
        public List<ExperimentParameterRule> InputParameters { get; init; } = new();
    }

    /// <summary>
    /// 命令输入参数的规则快照。生成阶段校验蓝图、执行阶段校验画布实例时都转换为该结构，
    /// 因而两道门禁采用完全相同的名称、类型、量程和字符串选项语义。
    /// </summary>
    internal sealed class ExperimentParameterRule
    {
        public string Name { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Unit { get; init; } = "";
        public string HelpText { get; init; } = "";
        public double MinValue { get; init; }
        public double MaxValue { get; init; }
        public int MaxLength { get; init; } = int.MaxValue;
        public bool IsReadOnly { get; init; }
        public bool IsRequired { get; init; }
        public List<string> Options { get; init; } = new();

        public static ExperimentParameterRule FromLive(ParameterBase parameter)
        {
            var rule = new ExperimentParameterRule
            {
                Name = parameter.Name ?? "",
                Kind = parameter.GetType().Name,
                Unit = parameter.Unit ?? "",
                HelpText = parameter.HelpText ?? "",
                IsReadOnly = parameter.IsReadOnly,
                IsRequired = parameter.IsRequired
            };

            switch (parameter)
            {
                case NumberParameter number:
                    return new ExperimentParameterRule
                    {
                        Name = rule.Name,
                        Kind = rule.Kind,
                        Unit = rule.Unit,
                        HelpText = rule.HelpText,
                        IsReadOnly = rule.IsReadOnly,
                        IsRequired = rule.IsRequired,
                        MinValue = number.MinValue,
                        MaxValue = number.MaxValue
                    };
                case StringParameter text:
                    return new ExperimentParameterRule
                    {
                        Name = rule.Name,
                        Kind = rule.Kind,
                        Unit = rule.Unit,
                        HelpText = rule.HelpText,
                        IsReadOnly = rule.IsReadOnly,
                        IsRequired = rule.IsRequired,
                        MaxLength = text.MaxLength,
                        Options = text.Options?.ToList() ?? new List<string>()
                    };
                case EnumParameter enumeration:
                    return new ExperimentParameterRule
                    {
                        Name = rule.Name,
                        Kind = rule.Kind,
                        Unit = rule.Unit,
                        HelpText = rule.HelpText,
                        IsReadOnly = rule.IsReadOnly,
                        IsRequired = rule.IsRequired,
                        Options = enumeration.EnumType == null
                            ? new List<string>()
                            : Enum.GetNames(enumeration.EnumType).ToList()
                    };
                default:
                    return rule;
            }
        }

        public bool Validate(JsonElement value, out string error)
        {
            if (IsReadOnly)
            {
                error = $"参数 {Name} 是只读参数，不能作为命令覆盖值";
                return false;
            }

            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                error = $"参数 {Name} 不能为 null";
                return false;
            }

            switch (Kind)
            {
                case nameof(NumberParameter):
                    if (!TryGetNumber(value, out double number))
                    {
                        error = $"参数 {Name} 必须是有限数值";
                        return false;
                    }
                    if (number < MinValue || number > MaxValue)
                    {
                        error = $"参数 {Name}={number.ToString(CultureInfo.InvariantCulture)} 超出范围 " +
                                $"[{MinValue.ToString(CultureInfo.InvariantCulture)}, {MaxValue.ToString(CultureInfo.InvariantCulture)}]" +
                                (string.IsNullOrWhiteSpace(Unit) ? "" : " " + Unit);
                        return false;
                    }
                    break;

                case nameof(StringParameter):
                    if (value.ValueKind != JsonValueKind.String)
                    {
                        error = $"参数 {Name} 必须是字符串";
                        return false;
                    }
                    string text = value.GetString() ?? "";
                    if (text.Length > MaxLength)
                    {
                        error = $"参数 {Name} 超过最大长度 {MaxLength}";
                        return false;
                    }
                    if (Options.Count > 0 && !Options.Contains(text, StringComparer.Ordinal))
                    {
                        error = $"参数 {Name} 的值「{text}」不在允许选项中:{string.Join("、", Options)}";
                        return false;
                    }
                    break;

                case nameof(BooleanParameter):
                    if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        error = $"参数 {Name} 必须是布尔值";
                        return false;
                    }
                    break;

                case nameof(EnumParameter):
                    if (value.ValueKind != JsonValueKind.String ||
                        !Options.Contains(value.GetString() ?? "", StringComparer.Ordinal))
                    {
                        error = $"参数 {Name} 必须是枚举选项之一:{string.Join("、", Options)}";
                        return false;
                    }
                    break;

                default:
                    if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        error = $"参数 {Name} 只支持标量值";
                        return false;
                    }
                    break;
            }

            error = "";
            return true;
        }

        public string ToPromptText()
        {
            var metadata = new List<string> { Kind };
            if (Kind == nameof(NumberParameter))
            {
                metadata.Add("范围=" + MinValue.ToString("R", CultureInfo.InvariantCulture) + ".." +
                             MaxValue.ToString("R", CultureInfo.InvariantCulture));
            }
            if (!string.IsNullOrWhiteSpace(Unit)) metadata.Add("单位=" + Unit);
            if (Options.Count > 0) metadata.Add("选项=" + string.Join("/", Options.Take(12)));
            if (MaxLength != int.MaxValue) metadata.Add("最大长度=" + MaxLength);
            if (IsRequired) metadata.Add("必填");
            if (IsReadOnly) metadata.Add("只读");
            if (!string.IsNullOrWhiteSpace(HelpText)) metadata.Add("说明=" + HelpText);
            return Name + "(" + string.Join(";", metadata) + ")";
        }

        private static bool TryGetNumber(JsonElement value, out double number)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number))
                return double.IsFinite(number);

            if (value.ValueKind == JsonValueKind.String &&
                double.TryParse(value.GetString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out number))
                return double.IsFinite(number);

            number = 0;
            return false;
        }
    }

    internal sealed class ExperimentDeviceCatalog
    {
        private const int MaxCatalogChars = 36000;
        private const int MaxDeviceEntryChars = 6000;
        private const int MaxCommandsPerDevice = 24;
        private const int MaxParametersPerCommand = 12;

        public List<ExperimentDeviceBlueprint> Devices { get; private init; } = new();
        public string PromptText { get; private init; } = "";

        public ExperimentDeviceBlueprint Find(string libraryName) => Devices.FirstOrDefault(device =>
            string.Equals(device.LibraryName, libraryName, StringComparison.Ordinal));

        public static ExperimentDeviceCatalog Capture(IDeviceManager manager)
        {
            if (manager == null) throw new ArgumentNullException(nameof(manager));

            var blueprints = new List<ExperimentDeviceBlueprint>();
            foreach (var device in manager.GetAllDevices() ?? Enumerable.Empty<IDevice>())
            {
                if (device == null || string.IsNullOrWhiteSpace(device.Name)) continue;
                blueprints.Add(CaptureDevice(device));
            }

            string prompt = BuildPrompt(blueprints);
            return new ExperimentDeviceCatalog { Devices = blueprints, PromptText = prompt };
        }

        private static ExperimentDeviceBlueprint CaptureDevice(IDevice device)
        {
            try
            {
                var commands = new List<ExperimentCommandBlueprint>();
                foreach (var command in device.Commands ?? new List<DeviceCommand>())
                {
                    if (command == null || string.IsNullOrWhiteSpace(command.Name)) continue;
                    var parameters = (command.InputParameters?.Variables ?? new System.Collections.ObjectModel.ObservableCollection<ParameterBase>())
                        .Where(parameter => parameter != null)
                        .Select(ExperimentParameterRule.FromLive)
                        .ToList();
                    commands.Add(new ExperimentCommandBlueprint
                    {
                        Name = command.Name,
                        HelpText = command.HelpText ?? "",
                        InputParameters = parameters
                    });
                }

                return new ExperimentDeviceBlueprint
                {
                    LibraryName = device.Name,
                    DeviceType = device.GetType().Name,
                    Category = device.Category ?? "",
                    Commands = commands
                };
            }
            catch (Exception ex)
            {
                // 读不完整的蓝图仍保留名称用于报错，但不提供可能误导模型的部分命令目录。
                return new ExperimentDeviceBlueprint
                {
                    LibraryName = device.Name ?? "",
                    DeviceType = device.GetType().Name,
                    Category = device.Category ?? "",
                    ReadError = ex.Message
                };
            }
        }

        private static string BuildPrompt(IReadOnlyList<ExperimentDeviceBlueprint> devices)
        {
            var output = new StringBuilder();
            output.AppendLine("以下是当前真实左侧设备库目录。名称、命令名、参数名均须逐字使用；带“读取失败”的设备不可选。");
            int included = 0;

            foreach (var device in devices.OrderBy(item => item.LibraryName, StringComparer.OrdinalIgnoreCase))
            {
                string entry = BuildDeviceEntry(device);
                if (output.Length + entry.Length > MaxCatalogChars) break;
                output.Append(entry);
                included++;
            }

            if (included < devices.Count)
            {
                output.AppendLine("目录已达到长度上限，后续设备未向模型展开，不能凭名称猜测其命令或参数。未展开设备:" +
                    string.Join("、", devices.OrderBy(item => item.LibraryName, StringComparer.OrdinalIgnoreCase)
                        .Skip(included).Select(item => item.LibraryName)));
            }
            if (devices.Count == 0) output.AppendLine("(设备库为空)");
            return output.ToString();
        }

        private static string BuildDeviceEntry(ExperimentDeviceBlueprint device)
        {
            var entry = new StringBuilder();
            entry.AppendLine($"\n设备库名: {device.LibraryName} | 驱动类型: {device.DeviceType} | 分类: {device.Category}");
            if (!string.IsNullOrWhiteSpace(device.ReadError))
            {
                entry.AppendLine("  读取失败，不可选择: " + Truncate(device.ReadError, 240));
                return entry.ToString();
            }

            int commandCount = 0;
            foreach (var command in device.Commands.Take(MaxCommandsPerDevice))
            {
                var line = new StringBuilder("  命令: " + command.Name);
                if (!string.IsNullOrWhiteSpace(command.HelpText))
                    line.Append(" | 说明: ").Append(Truncate(command.HelpText, 180));
                line.AppendLine();
                line.Append("    输入参数: ");
                var parameters = command.InputParameters.Take(MaxParametersPerCommand)
                    .Select(parameter => Truncate(parameter.ToPromptText(), 360)).ToList();
                line.Append(parameters.Count == 0 ? "(无)" : string.Join("; ", parameters));
                if (command.InputParameters.Count > MaxParametersPerCommand)
                    line.Append("; …其余参数未展开");
                line.AppendLine();

                if (entry.Length + line.Length > MaxDeviceEntryChars)
                {
                    entry.AppendLine("  …该设备后续命令因目录长度上限未展开");
                    return entry.ToString();
                }
                entry.Append(line);
                commandCount++;
            }

            if (commandCount == 0) entry.AppendLine("  (无可自动控制命令，可作为展示型设备)");
            if (device.Commands.Count > commandCount)
                entry.AppendLine($"  …另有 {device.Commands.Count - commandCount} 个命令未展开");
            return entry.ToString();
        }

        private static string Truncate(string text, int maxChars)
        {
            string normalized = (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return normalized.Length <= maxChars ? normalized : normalized.Substring(0, maxChars) + "…";
        }
    }
}
