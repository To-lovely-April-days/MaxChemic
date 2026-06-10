using System;
using System.Collections.Generic;

namespace MaxChemical.Modules.DOE.Tests
{
    /// <summary>
    /// 已知解析函数 — 与 Python 端 test_doe_accuracy.py 中的函数完全一致。
    /// 用于在 C# 端生成 ground truth 数据，验证 C#↔Python 桥接的数值准确性。
    /// 
    /// 产率:   y1 = 75 + 10T + 5P + 3C - 4T² - 2P² + 2TP
    /// 选择性: y2 = 95 - 2T² - 1.5P² + 0.5C - 0.8TP
    /// 成本:   y3 = 50 + 15T + 10P + 20C - 5TC
    /// 
    /// 其中 T, P, C 是标准化因子 [-1, 1]
    /// </summary>
    public static class KnownFunctions
    {
        public static readonly Dictionary<string, double[]> FactorBounds = new()
        {
            ["温度"] = new[] { 80.0, 160.0 },
            ["压力"] = new[] { 10.0, 30.0 },
            ["催化剂"] = new[] { 1.0, 5.0 }
        };

        private static double Standardize(double actual, double low, double high)
        {
            double center = (low + high) / 2.0;
            double half = (high - low) / 2.0;
            return (actual - center) / half;
        }

        public static double TrueYield(double temp, double pressure, double catalyst)
        {
            double T = Standardize(temp, 80, 160);
            double P = Standardize(pressure, 10, 30);
            double C = Standardize(catalyst, 1, 5);
            return 75 + 10 * T + 5 * P + 3 * C - 4 * T * T - 2 * P * P + 2 * T * P;
        }

        public static double TrueSelectivity(double temp, double pressure, double catalyst)
        {
            double T = Standardize(temp, 80, 160);
            double P = Standardize(pressure, 10, 30);
            double C = Standardize(catalyst, 1, 5);
            return 95 - 2 * T * T - 1.5 * P * P + 0.5 * C - 0.8 * T * P;
        }

        public static double TrueCost(double temp, double pressure, double catalyst)
        {
            double T = Standardize(temp, 80, 160);
            double P = Standardize(pressure, 10, 30);
            double C = Standardize(catalyst, 1, 5);
            return 50 + 15 * T + 10 * P + 20 * C - 5 * T * C;
        }

        public static Dictionary<string, double> EvaluateAll(double temp, double pressure, double catalyst)
        {
            return new Dictionary<string, double>
            {
                ["产率"] = TrueYield(temp, pressure, catalyst),
                ["选择性"] = TrueSelectivity(temp, pressure, catalyst),
                ["成本"] = TrueCost(temp, pressure, catalyst)
            };
        }

        /// <summary>
        /// 生成标准训练数据集（15 组，无噪声）
        /// </summary>
        public static List<(Dictionary<string, double> Factors, Dictionary<string, double> Responses)> GenerateTrainingData()
        {
            var points = new (double T, double P, double C)[]
            {
                (80, 10, 1.0), (160, 30, 5.0), (120, 20, 3.0),
                (100, 15, 2.0), (140, 25, 4.0), (80, 30, 3.0),
                (160, 10, 2.0), (120, 20, 5.0), (100, 25, 1.0),
                (140, 15, 3.0), (90, 20, 4.0), (130, 10, 3.0),
                (110, 25, 2.0), (150, 20, 1.0), (120, 15, 4.0),
            };

            var data = new List<(Dictionary<string, double>, Dictionary<string, double>)>();
            foreach (var (T, P, C) in points)
            {
                var factors = new Dictionary<string, double>
                {
                    ["温度"] = T,
                    ["压力"] = P,
                    ["催化剂"] = C
                };
                var responses = EvaluateAll(T, P, C);
                data.Add((factors, responses));
            }
            return data;
        }
    }
}