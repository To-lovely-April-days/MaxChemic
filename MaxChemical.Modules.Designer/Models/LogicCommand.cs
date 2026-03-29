// Models/LogicCommand.cs
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace MaxChemical.Modules.Designer.Models
{
    /// <summary>
    /// 逻辑命令类型
    /// </summary>
    public enum LogicCommandType
    {
        Wait,
        If,
        Loop,
        WaitIf,
        Parallel,
        SetVariable,
        Unknown
    }

    /// <summary>
    /// 比较运算符
    /// </summary>
    public enum ComparisonOperator
    {
        [Description("等于")]
        Equal,
        [Description("不等于")]
        NotEqual,
        [Description("大于")]
        GreaterThan,
        [Description("大于等于")]
        GreaterThanOrEqual,
        [Description("小于")]
        LessThan,
        [Description("小于等于")]
        LessThanOrEqual,
        [Description("包含")]
        Contains,
        [Description("不包含")]
        NotContains
    }

    /// <summary>
    /// 时间单位
    /// </summary>
    public enum TimeUnit
    {
        [Description("毫秒")]
        Milliseconds,
        [Description("秒")]
        Seconds,
        [Description("分钟")]
        Minutes,
        [Description("小时")]
        Hours
    }
    /// <summary>
    /// 执行模式
    /// </summary>
    /// <summary>
    /// 执行模式
    /// </summary>
    public enum ExecutionMode
    {
        [Description("同步执行")]
        Synchronous,
        [Description("异步执行")]
        Asynchronous
    }
    /// <summary>
    /// 循环类型
    /// </summary>
    public enum LoopType
    {
        [Description("固定次数")]
        FixedCount=0,
        [Description("条件循环")]
        Conditional=1,
        [Description("无限循环")]
        Infinite=2
    }

    /// <summary>
    /// 变量类型
    /// </summary>
    public enum VariableType
    {
        [Description("字符串")]
        String,
        [Description("整数")]
        Integer,
        Number,
        [Description("小数")]
        Double,
        [Description("布尔")]
        Boolean,
        [Description("日期时间")]
        DateTime
    }

    /// <summary>
    /// 并行等待策略
    /// </summary>
    public enum ParallelWaitStrategy
    {
        [Description("等待所有")]
        WaitAll,
        [Description("等待任意一个")]
        WaitAny,
        [Description("不等待")]
        NoWait
    }

    /// <summary>
    /// 逻辑命令基类
    /// </summary>
    public class LogicCommand
    {
        public string Name { get; set; }
        public string HelpText { get; set; }
        public string ImageLocation { get; set; }
        public LogicCommandType CommandType { get; set; }
        public Dictionary<string, object> Properties { get; set; }

        public LogicCommand()
        {
            Properties = new Dictionary<string, object>();
        }

        public LogicCommand(string name, string helpText, string imageLocation, LogicCommandType commandType) : this()
        {
            Name = name;
            HelpText = helpText;
            ImageLocation = imageLocation;
            CommandType = commandType;
        }

        public T GetProperty<T>(string key, T defaultValue = default(T))
        {
            if (Properties.TryGetValue(key, out var value) && value is T)
            {
                return (T)value;
            }
            return defaultValue;
        }

        public void SetProperty(string key, object value)
        {
            Properties[key] = value;
        }


    }

    /// <summary>
    /// 通用等待命令属性
    /// </summary>
    public class WaitCommandProperties
    {
        // 基础等待属性
        public double WaitTime { get; set; } = 5.0;
        public TimeUnit TimeUnit { get; set; } = TimeUnit.Seconds;

        // 条件等待属性
        public string ConditionLeftVariable { get; set; } = "";
        public ComparisonOperator ConditionOperator { get; set; } = ComparisonOperator.Equal;
        public string ConditionRightValue { get; set; } = "";
        public double TimeoutSeconds { get; set; } = 30.0;
        public double CheckIntervalSeconds { get; set; } = 1.0;
        public bool IsInfiniteTimeout { get; set; } = false; // 是否无限超时

        // 执行模式（只有两种）
        public ExecutionMode ExecutionMode { get; set; } = ExecutionMode.Synchronous;

        // 通用属性
        public string Description { get; set; } = "等待指定时间";

        public bool IsWarning { get; set; } = false;
    }




    /// <summary>
    /// 条件判断命令属性
    /// </summary>
    public class IfCommandProperties
    {
        public string Bezel_lessIconPath= "pack://siteoforigin:,,,/Resources/CommadIcon/Bezel-lessIF.png";
        public string LeftOperand { get; set; } = "A";
        public ComparisonOperator Operator { get; set; } = ComparisonOperator.Equal;
        public string RightOperand { get; set; } = "B";
        public string Description { get; set; } = "条件判断";
    }

    /// <summary>
    /// 循环命令属性
    /// </summary>
    public class LoopCommandProperties
    {
        public string Bezel_lessIconPath = "pack://siteoforigin:,,,/Resources/CommadIcon/Bezel-lessLoop.png";
        public LoopType LoopType { get; set; } = LoopType.FixedCount;
        public int LoopCount { get; set; } = 3;
        public string LeftOperand { get; set; } = "A";
        public ComparisonOperator Operator { get; set; } = ComparisonOperator.Equal;
        public string RightOperand { get; set; } = "B";
        public string Description { get; set; } = "循环执行";
        public int WaitTime { get; set; } = 10;

        /// <summary>
        /// Do-While 开关：true = do-while (先执行后判断), false = while (先判断后执行)
        /// 仅在条件循环时有效
        /// </summary>
        public bool IsDoWhile { get; set; } = false;
    }
    /// <summary>
    /// 并行命令属性
    /// </summary>
    public class ParallelCommandProperties
    {
        public string Bezel_lessIconPath = "pack://siteoforigin:,,,/Resources/CommadIcon/Bezel-lessParallel.png";
        public int MaxParallelCount { get; set; } = 4;
        public ParallelWaitStrategy WaitStrategy { get; set; } = ParallelWaitStrategy.NoWait;
        public double TimeoutSeconds { get; set; } = 60.0;
        public string Description { get; set; } = "并行执行";
    }

    /// <summary>
    /// 设置变量命令属性
    /// </summary>
    public class SetVariableCommandProperties
    {
        public string VariableName { get; set; } = "";
        public string VariableValue { get; set; } = "";
        public VariableType VariableType { get; set; } = VariableType.String;
        public string Description { get; set; } = "设置变量";
    }
}