using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MaxChemical.Modules.Designer.Models
{
    /// <summary>
    /// 参数归一化信息
    /// </summary>
    public class NormalizedParameterInfo
    {
        public string DisplayName { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string Unit { get; set; } = "";    // 单位（°C, MPa, rpm 等）
        public double MinRange { get; set; }  // 量程下限
        public double MaxRange { get; set; }  // 量程上限
        public double? WarningLow { get; set; }   // 警告下限（百分比，如 10）
        public double? WarningHigh { get; set; }  // 警告上限（百分比，如 90）
        public double? DangerLow { get; set; }    // 危险下限（百分比，如 0）
        public double? DangerHigh { get; set; }   // 危险上限（百分比，如 100）
        public OxyColor LineColor { get; set; } = OxyColors.Blue;
        public LineStyle LineStyle { get; set; } = LineStyle.Solid;
    }

    /// <summary>
    /// 构建归一化百分比图表（OxyPlot PlotModel）
    /// 所有参数按各自量程归一化到 0%~100%，统一 Y 轴
    /// 背景分五层色带：危险(红) / 警告(黄) / 安全(绿)
    /// </summary>
    public static class NormalizedChartBuilder
    {
        // ═══ 色带颜色 ═══
        private static readonly OxyColor DangerZone = OxyColor.FromArgb(30, 220, 38, 38);   // 红色半透明
        private static readonly OxyColor WarningZone = OxyColor.FromArgb(25, 217, 119, 6);  // 黄色半透明
        private static readonly OxyColor SafeZone = OxyColor.FromArgb(15, 5, 150, 105);     // 绿色半透明

        private static readonly OxyColor DangerLine = OxyColor.FromArgb(100, 220, 38, 38);
        private static readonly OxyColor WarningLine = OxyColor.FromArgb(80, 217, 119, 6);
        private static readonly OxyColor TargetLine = OxyColor.FromArgb(60, 5, 150, 105);

        private static readonly OxyColor GridLine = OxyColor.Parse("#F2F3F5");
        private static readonly OxyColor AxisText = OxyColor.Parse("#9CA3AF");
        private static readonly OxyColor Border = OxyColor.Parse("#E8EAED");

        /// <summary>
        /// 创建归一化图表模型
        /// </summary>
        public static PlotModel CreateModel()
        {
            var model = new PlotModel
            {
                PlotAreaBorderColor = Border,
                PlotAreaBorderThickness = new OxyThickness(0.5),
                Padding = new OxyThickness(4, 4, 8, 4),
                PlotMargins = new OxyThickness(36, 8, 8, 24),
            };

            // Y 轴：0% ~ 100%
            model.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                Minimum = -5,
                Maximum = 105,
                FontSize = 8,
                TextColor = AxisText,
                TicklineColor = Border,
                MajorStep = 25,
                MinorStep = 5,
                MajorGridlineStyle = LineStyle.None,
                MinorGridlineStyle = LineStyle.None,
                AxislineStyle = LineStyle.None,
                StringFormat = "0'%'",
                IsPanEnabled = false,
                IsZoomEnabled = false,
            });

            // X 轴：时间
            model.Axes.Add(new DateTimeAxis
            {
                Position = AxisPosition.Bottom,
                StringFormat = "HH:mm:ss",
                FontSize = 8,
                TextColor = AxisText,
                TicklineColor = Border,
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = GridLine,
                MinorGridlineStyle = LineStyle.None,
                AxislineStyle = LineStyle.None,
                IntervalType = DateTimeIntervalType.Auto,
            });

            // ═══ 背景色带（用 RectangleAnnotation） ═══

            // 上危险区 90~100%
            model.Annotations.Add(new RectangleAnnotation
            {
                MinimumY = 90,
                MaximumY = 105,
                Fill = DangerZone,
                Layer = AnnotationLayer.BelowSeries,
                StrokeThickness = 0,
            });

            // 上警告区 70~90%
            model.Annotations.Add(new RectangleAnnotation
            {
                MinimumY = 70,
                MaximumY = 90,
                Fill = WarningZone,
                Layer = AnnotationLayer.BelowSeries,
                StrokeThickness = 0,
            });

            // 安全区 30~70%
            model.Annotations.Add(new RectangleAnnotation
            {
                MinimumY = 30,
                MaximumY = 70,
                Fill = SafeZone,
                Layer = AnnotationLayer.BelowSeries,
                StrokeThickness = 0,
            });

            // 下警告区 10~30%
            model.Annotations.Add(new RectangleAnnotation
            {
                MinimumY = 10,
                MaximumY = 30,
                Fill = WarningZone,
                Layer = AnnotationLayer.BelowSeries,
                StrokeThickness = 0,
            });

            // 下危险区 0~10%
            model.Annotations.Add(new RectangleAnnotation
            {
                MinimumY = -5,
                MaximumY = 10,
                Fill = DangerZone,
                Layer = AnnotationLayer.BelowSeries,
                StrokeThickness = 0,
            });

            // ═══ 阈值线 ═══

            // 上危险线 90%
            model.Annotations.Add(new LineAnnotation
            {
                Type = LineAnnotationType.Horizontal,
                Y = 90,
                Color = DangerLine,
                StrokeThickness = 0.5,
                LineStyle = LineStyle.Dash,
                Text = "90% 上警告",
                TextColor = DangerLine,
                FontSize = 7,
                TextHorizontalAlignment = HorizontalAlignment.Right,
                Layer = AnnotationLayer.BelowSeries,
            });

            // 目标线 50%
            model.Annotations.Add(new LineAnnotation
            {
                Type = LineAnnotationType.Horizontal,
                Y = 50,
                Color = TargetLine,
                StrokeThickness = 0.3,
                LineStyle = LineStyle.Dot,
                Text = "50% 目标",
                TextColor = TargetLine,
                FontSize = 7,
                TextHorizontalAlignment = HorizontalAlignment.Right,
                Layer = AnnotationLayer.BelowSeries,
            });

            // 下危险线 10%
            model.Annotations.Add(new LineAnnotation
            {
                Type = LineAnnotationType.Horizontal,
                Y = 10,
                Color = DangerLine,
                StrokeThickness = 0.5,
                LineStyle = LineStyle.Dash,
                Text = "10% 下警告",
                TextColor = DangerLine,
                FontSize = 7,
                TextHorizontalAlignment = HorizontalAlignment.Right,
                Layer = AnnotationLayer.BelowSeries,
            });

            return model;
        }

        /// <summary>
        /// 归一化一个原始值到 0~100%
        /// </summary>
        public static double Normalize(double rawValue, double minRange, double maxRange)
        {
            if (Math.Abs(maxRange - minRange) < 1e-10) return 50;
            return ((rawValue - minRange) / (maxRange - minRange)) * 100.0;
        }

        /// <summary>
        /// 向模型添加一条归一化曲线（显示真实值 + 归一化%）
        /// </summary>
        public static LineSeries AddNormalizedSeries(
     PlotModel model,
     NormalizedParameterInfo info,
     IEnumerable<(DateTime timestamp, double value)> dataPoints)
        {
            var pointsList = dataPoints.ToList();
            if (pointsList.Count == 0) return null;

            // ═══ 自动量程(带"最小有效跨度"保护)═══
            double minR = info.MinRange;
            double maxR = info.MaxRange;
            bool useAutoRange = Math.Abs(maxR - minR) < 1e-6 || maxR <= minR;

            if (useAutoRange)
            {
                var values = pointsList.Select(p => p.value).ToList();
                double dataMin = values.Min();
                double dataMax = values.Max();
                double dataRange = dataMax - dataMin;
                double absMax = Math.Max(Math.Abs(dataMin), Math.Abs(dataMax));

                // 最小有效跨度:绝对值的 5%,至少 0.1
                double minMeaningfulRange = Math.Max(absMax * 0.05, 0.1);

                if (dataRange < minMeaningfulRange)
                {
                    // 数据稳定,人为撑开一个有意义的窗口
                    double center = (dataMin + dataMax) / 2.0;
                    minR = center - minMeaningfulRange;
                    maxR = center + minMeaningfulRange;
                }
                else
                {
                    // 数据有变化,留 20% 边距
                    minR = dataMin - dataRange * 0.2;
                    maxR = dataMax + dataRange * 0.2;
                }

                info.MinRange = minR;
                info.MaxRange = maxR;
            }

            var markerFill = OxyColor.FromArgb(200, info.LineColor.R, info.LineColor.G, info.LineColor.B);
            var series = new LineSeries
            {
                Title = info.DisplayName,
                Color = info.LineColor,
                StrokeThickness = 1.5,
                LineStyle = LineStyle.Solid,                                       // ★ 数据线一律实线
                CanTrackerInterpolatePoints = false,
                MarkerType = pointsList.Count > 80 ? MarkerType.None : MarkerType.Circle,
                MarkerSize = 2.5,
                MarkerFill = markerFill,
                MarkerStroke = OxyColors.White,
                MarkerStrokeThickness = 0.8,
            };

            var realValues = new List<double>();
            foreach (var (timestamp, value) in pointsList)
            {
                double normalized = Normalize(value, minR, maxR);
                // ★ 截断兜底,防止离群值画出垂直暴跳
                normalized = Math.Max(-20, Math.Min(120, normalized));

                series.Points.Add(new DataPoint(DateTimeAxis.ToDouble(timestamp), normalized));
                realValues.Add(value);
            }

            // ... Tracker 逻辑保持不变
            var displayName = info.DisplayName;
            var unit = info.Unit ?? "";
            var realValuesArray = realValues.ToArray();
            series.TrackerFormatString = $"{displayName}\n时间: {{2:HH:mm:ss}}\n归一化: {{4:F1}}%";

            model.Series.Add(series);

            model.TrackerChanged += (s, e) =>
            {
                if (e.HitResult?.Series != series) return;
                if (e.HitResult == null) return;

                int idx = (int)Math.Round(e.HitResult.Index);
                if (idx >= 0 && idx < realValuesArray.Length)
                {
                    double realVal = realValuesArray[idx];
                    double normVal = Normalize(realVal, minR, maxR);
                    normVal = Math.Max(-20, Math.Min(120, normVal));
                    var time = DateTimeAxis.ToDateTime(e.HitResult.DataPoint.X);

                    e.HitResult.Text = $"{displayName}\n" +
                                       $"时间: {time:HH:mm:ss}\n" +
                                       $"实际值: {realVal:F2} {unit}\n" +
                                       $"归一化: {normVal:F1}%";
                }
            };

            return series;
        }
        /// <summary>
        /// 预定义的曲线颜色（靛蓝主题色系）
        /// </summary>
        public static readonly OxyColor[] LineColors = new[]
        {
            OxyColor.Parse("#2D3A8C"), // 靛蓝
            OxyColor.Parse("#D97706"), // 琥珀
            OxyColor.Parse("#059669"), // 绿色
            OxyColor.Parse("#7C3AED"), // 紫色
            OxyColor.Parse("#2563EB"), // 蓝色
            OxyColor.Parse("#DB2777"), // 粉色
            OxyColor.Parse("#DC2626"), // 红色
            OxyColor.Parse("#65A30D"), // 黄绿
            OxyColor.Parse("#0891B2"), // 青色
            OxyColor.Parse("#9333EA"), // 深紫
            OxyColor.Parse("#EA580C"), // 橘色
            OxyColor.Parse("#0D9488"), // 深青
        };

        public static readonly LineStyle[] LineStyleCycle = new[]
        {
            LineStyle.Solid,
            LineStyle.Dash,
            LineStyle.Dot,
            LineStyle.DashDot,
            LineStyle.LongDash,
            LineStyle.LongDashDot,
        };
    }
}