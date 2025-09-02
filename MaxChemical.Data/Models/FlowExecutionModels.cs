// MaxChemical.Data/Models/FlowExecutionModels.cs - 独立的流程执行模型
using System;
using System.Collections.Generic;
using System.Linq;

namespace MaxChemical.Data.Models
{
    // 设备参数基类（独立于Designer模块）
    public abstract class DeviceParameterBase
    {
        public string Name { get; set; } = string.Empty;
        public object? Value { get; set; }
        public abstract string GetParameterType();
    }

    // 数字参数
    public class DeviceNumberParameter : DeviceParameterBase
    {
        public double NumericValue => Convert.ToDouble(Value ?? 0);
        public override string GetParameterType() => "Number";
    }

    // 字符串参数
    public class DeviceStringParameter : DeviceParameterBase
    {
        public string StringValue => Value?.ToString() ?? string.Empty;
        public override string GetParameterType() => "String";
    }

    // 布尔参数
    public class DeviceBooleanParameter : DeviceParameterBase
    {
        public bool BooleanValue => Convert.ToBoolean(Value ?? false);
        public override string GetParameterType() => "Boolean";
    }

    // 设备参数集合
    public class DeviceParameterCollection
    {
        public List<DeviceParameterBase> Parameters { get; set; } = new();

        public void AddParameter(string name, object? value, string type)
        {
            DeviceParameterBase parameter = type.ToLower() switch
            {
                "number" or "double" or "int" or "float" => new DeviceNumberParameter { Name = name, Value = value },
                "bool" or "boolean" => new DeviceBooleanParameter { Name = name, Value = value },
                _ => new DeviceStringParameter { Name = name, Value = value }
            };

            Parameters.Add(parameter);
        }

        public T? GetParameter<T>(string name) where T : DeviceParameterBase
        {
            return Parameters.Find(p => p.Name == name && p is T) as T;
        }

        public bool HasParameters => Parameters.Any();
    }

    // 节点执行信息（独立于Designer模块）
    public class NodeExecutionInfo
    {
        public string NodeId { get; set; } = string.Empty;
        public string NodeName { get; set; } = string.Empty;
        public string NodeType { get; set; } = string.Empty;
        public string? DeviceId { get; set; }
        public string? DeviceName { get; set; }
        public string? CommandName { get; set; }
        public DeviceParameterCollection? InputParameters { get; set; }
        public DeviceParameterCollection? OutputParameters { get; set; }
    }
}