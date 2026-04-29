using System;
using System.Collections.Generic;

namespace MaxChemical.Modules.Designer.Models
{
    /// <summary>
    /// 监控图表的 Y 轴类型。
    /// 当前业务设定为 3 类:温度 / 压力 / 流量。
    /// 不属于这 3 类的参数视为 "Unclassified",前端会提示用户但不参与绘图。
    /// </summary>
    public enum AxisType
    {
        /// <summary>未分类(单位无法识别成温度/压力/流量)</summary>
        Unclassified = 0,

        /// <summary>温度轴(℃ / °C / °F / K)</summary>
        Temperature = 1,

        /// <summary>压力轴(MPa / kPa / bar / psi 等)</summary>
        Pressure = 2,

        /// <summary>流量轴(L/min / ml/min / m3/h 等)</summary>
        Flow = 3,
    }

    /// <summary>
    /// 根据"参数单位"自动识别所属 Y 轴类型的分类器。
    /// 大小写不敏感,容忍中英文以及常见全角/半角字符。
    /// </summary>
    public static class ParameterAxisClassifier
    {
        /// <summary>
        /// 把单位字符串规范化:去空格、转小写、把全角字符转半角等价。
        /// </summary>
        private static string Normalize(string unit)
        {
            if (string.IsNullOrWhiteSpace(unit)) return "";
            var s = unit.Trim().ToLowerInvariant();
            // 全角空格、特殊字符替换
            s = s.Replace("℃", "c").Replace("°c", "c")
                 .Replace("℉", "f").Replace("°f", "f")
                 .Replace(" ", "").Replace("\u00A0", "");
            return s;
        }

        /// <summary>
        /// 根据单位识别 AxisType。识别不出返回 Unclassified。
        /// </summary>
        public static AxisType Classify(string unit)
        {
            var s = Normalize(unit);
            if (s.Length == 0) return AxisType.Unclassified;

            // ═══ 温度 ═══
            // c, f, k(单字母), celsius, fahrenheit, kelvin
            if (s == "c" || s == "f" || s == "k" ||
                s.Contains("celsius") || s.Contains("fahrenheit") || s.Contains("kelvin"))
                return AxisType.Temperature;

            // ═══ 压力 ═══
            // mpa / kpa / pa / bar / psi / atm / torr / mmhg
            // 注意先判断 "mpa" / "kpa" 这种带前缀的,避免被 "pa" 短路
            if (s.Contains("mpa") || s.Contains("kpa") || s.Contains("hpa") ||
                s == "pa" ||
                s.Contains("bar") || s.Contains("psi") || s.Contains("atm") ||
                s.Contains("torr") || s.Contains("mmhg"))
                return AxisType.Pressure;

            // ═══ 流量 ═══
            // L/min, ml/min, m3/h, kg/h, g/min, sccm, slm, gpm 等
            // 包含斜杠的"x/y"形式,或常见缩写
            if (s.Contains("/min") || s.Contains("/h") || s.Contains("/s") ||
                s == "sccm" || s == "slm" || s == "gpm" || s == "lpm" ||
                s.Contains("m3") || s.Contains("m³"))
                return AxisType.Flow;

            return AxisType.Unclassified;
        }

        /// <summary>
        /// 获取轴的中文显示名(用于轴标题和提示信息)。
        /// </summary>
        public static string GetAxisTitle(AxisType axisType)
        {
            switch (axisType)
            {
                case AxisType.Temperature: return "温度";
                case AxisType.Pressure: return "压力";
                case AxisType.Flow: return "流量";
                default: return "未分类";
            }
        }

        /// <summary>
        /// 获取该轴类型的"代表单位"(展示在轴标题里)。
        /// 如果同一轴上有多个不同单位的参数,用第一个传入的 unit 作为代表。
        /// </summary>
        public static string GetDefaultUnit(AxisType axisType)
        {
            switch (axisType)
            {
                case AxisType.Temperature: return "℃";
                case AxisType.Pressure: return "MPa";
                case AxisType.Flow: return "L/min";
                default: return "";
            }
        }

        /// <summary>
        /// 获取轴归属徽章的字母标识(用于 UI)。
        /// </summary>
        public static string GetAxisBadge(AxisType axisType)
        {
            switch (axisType)
            {
                case AxisType.Temperature: return "T";
                case AxisType.Pressure: return "P";
                case AxisType.Flow: return "F";
                default: return "?";
            }
        }
    }
}