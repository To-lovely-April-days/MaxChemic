using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MaxChemical.Modules.Designer.Models
{
    /// <summary>
    /// 多 Y 轴图表构建器(取代旧的 NormalizedChartBuilder)。
    /// 
    /// 核心特性:
    ///   1. 真实物理量直接显示(不再归一化)。
    ///   2. 最多 3 个 Y 轴(温度/压力/流量),只显示有参数的轴,动态增减。
    ///   3. 同类参数共用同一 Y 轴,量程取并集 + "最小有效跨度"保护。
    ///   4. 配色遵循 MaxChemical 品牌色板(#A41626 主红 + #1A1A1A 近黑 + 暖白底)。
    ///   5. 线条样式保留原有 OxyPlot 配置:实线 1.5px + 实心圆 marker 2.5 + 白色细描边。
    ///   6. 鼠标悬停同步显示所有参数当前值(由 ViewModel 监听 TrackerChanged 实现)。
    /// </summary>
    public static class MultiAxisChartBuilder
    {
        // ═══ 品牌色板(与 BrandColors.xaml 保持一致) ═══
        private static readonly OxyColor BrandPrimary = OxyColor.Parse("#A41626"); // 品牌主红
        private static readonly OxyColor BrandDark = OxyColor.Parse("#1A1A1A");    // 近黑

        private static readonly OxyColor TextSecondary = OxyColor.Parse("#5F5E5A");
        private static readonly OxyColor TextTertiary = OxyColor.Parse("#888780");
        private static readonly OxyColor BorderDefault = OxyColor.Parse("#EAEAEA");
        private static readonly OxyColor BorderSubtle = OxyColor.Parse("#F1EFE8");

        // 各轴的标题色(与对应曲线主色调一致,便于工程师视觉关联)
        private static readonly OxyColor AxisColorPressure = OxyColor.Parse("#A41626");    // 压力 → 品牌主红
        private static readonly OxyColor AxisColorTemperature = OxyColor.Parse("#854F0B"); // 温度 → 暖琥珀深色
        private static readonly OxyColor AxisColorFlow = OxyColor.Parse("#0F6E56");        // 流量 → 深绿(从品牌状态色板取)

        /// <summary>
        /// 调色板:每条曲线分配一种颜色。
        /// 顺序优先使用品牌色,然后是协调的暖色 + 深色,避免出现"彩虹色"工业感。
        /// </summary>
        public static readonly OxyColor[] LineColors = new[]
        {
            OxyColor.Parse("#A41626"), // 品牌主红
            OxyColor.Parse("#BA7517"), // 暖琥珀
            OxyColor.Parse("#1A1A1A"), // 近黑
            OxyColor.Parse("#0F6E56"), // 深绿
            OxyColor.Parse("#534AB7"), // 深紫
            OxyColor.Parse("#185FA5"), // 深蓝
            OxyColor.Parse("#993556"), // 暗粉
            OxyColor.Parse("#5F5E5A"), // 中灰
            OxyColor.Parse("#3B6D11"), // 暗绿
            OxyColor.Parse("#7C3AED"), // 紫
            OxyColor.Parse("#0891B2"), // 青
            OxyColor.Parse("#A32D2D"), // 深红
        };

        /// <summary>
        /// 一个 Series 输入(配合 BuildModel 使用)。
        /// </summary>
        public class SeriesInput
        {
            public string DisplayName { get; set; } = "";
            public string Unit { get; set; } = "";
            public AxisType AxisType { get; set; } = AxisType.Unclassified;
            public OxyColor LineColor { get; set; } = OxyColors.Black;
            public IEnumerable<(DateTime Timestamp, double Value)> Points { get; set; }
        }

        /// <summary>
        /// 每个 Y 轴的 Key 字符串(用于 Series.YAxisKey 绑定)。
        /// </summary>
        private static string GetAxisKey(AxisType axisType) => $"Y_{axisType}";

        /// <summary>
        /// 创建空白 PlotModel(只有 X 轴,Y 轴根据后续添加的 Series 动态创建)。
        /// </summary>
        public static PlotModel CreateEmptyModel()
        {
            var model = new PlotModel
            {
                PlotAreaBorderColor = BorderDefault,
                PlotAreaBorderThickness = new OxyThickness(0.5),
                Padding = new OxyThickness(8, 8, 12, 8),
                PlotMargins = new OxyThickness(double.NaN, 32, double.NaN, 32),
                Background = OxyColors.Transparent,
                TextColor = TextSecondary,
            };

            // X 轴:时间
            model.Axes.Add(new DateTimeAxis
            {
                Position = AxisPosition.Bottom,
                StringFormat = "HH:mm:ss",
                FontSize = 10,
                TextColor = TextTertiary,
                TicklineColor = BorderDefault,
                MajorGridlineStyle = LineStyle.Solid,
                MajorGridlineColor = BorderSubtle,
                MajorGridlineThickness = 0.5,
                MinorGridlineStyle = LineStyle.None,
                AxislineStyle = LineStyle.Solid,
                AxislineColor = BorderDefault,
                AxislineThickness = 0.5,
                IntervalType = DateTimeIntervalType.Auto,
                IsPanEnabled = true,
                IsZoomEnabled = true,
            });

            return model;
        }

        /// <summary>
        /// 一次性构建完整模型:根据传入的多个 SeriesInput,自动创建所需的 Y 轴和 LineSeries。
        /// </summary>
        /// <param name="inputs">所有要绘制的曲线。Unclassified 类型会被忽略。</param>
        public static PlotModel BuildModel(IEnumerable<SeriesInput> inputs)
        {
            var model = CreateEmptyModel();
            if (inputs == null) return model;

            var validInputs = inputs.Where(i => i?.AxisType != AxisType.Unclassified).ToList();
            if (validInputs.Count == 0) return model;

            // ═══ 第 1 步:按 AxisType 分组,确定要创建哪些 Y 轴 ═══
            var groupsByAxis = validInputs
                .GroupBy(i => i.AxisType)
                .OrderBy(g => (int)g.Key) // 让轴的位置稳定:温度=1, 压力=2, 流量=3
                .ToList();

            // ═══ 第 2 步:为每个轴类型创建一个 LinearAxis ═══
            // 位置规划(只显示有参数的轴):
            //   - 1 个轴 → 全部放左侧
            //   - 2 个轴 → 第一个左、第二个右
            //   - 3 个轴 → 第一个左外、第二个左内、第三个右
            var presentAxisTypes = groupsByAxis.Select(g => g.Key).ToList();
            for (int i = 0; i < presentAxisTypes.Count; i++)
            {
                var axisType = presentAxisTypes[i];
                var groupItems = groupsByAxis.First(g => g.Key == axisType).ToList();
                var axis = CreateYAxisForGroup(axisType, groupItems, position: i, totalCount: presentAxisTypes.Count);
                model.Axes.Add(axis);
            }

            // ═══ 第 3 步:为每个输入创建 LineSeries 并绑定到对应 Y 轴 ═══
            foreach (var input in validInputs)
            {
                var series = CreateLineSeries(input);
                model.Series.Add(series);
            }

            return model;
        }

        /// <summary>
        /// 为某个轴类型(组)创建一个 LinearAxis,量程根据组内所有参数数据自适应计算。
        /// </summary>
        private static LinearAxis CreateYAxisForGroup(
            AxisType axisType,
            List<SeriesInput> groupItems,
            int position,
            int totalCount)
        {
            // 计算组内所有数据的合并量程(取并集 + 最小有效跨度保护)
            var (minR, maxR) = ComputeAxisRange(groupItems);

            // 决定轴的位置(Left / Right)和 PositionTier(同侧多轴时的偏移层级)
            AxisPosition axisPos;
            int positionTier = 0;
            if (totalCount <= 1)
            {
                axisPos = AxisPosition.Left;
            }
            else if (totalCount == 2)
            {
                axisPos = position == 0 ? AxisPosition.Left : AxisPosition.Right;
            }
            else
            {
                // 3 个轴:第一个左外(tier=1)、第二个左内(tier=0)、第三个右
                if (position == 0) { axisPos = AxisPosition.Left; positionTier = 1; }
                else if (position == 1) { axisPos = AxisPosition.Left; positionTier = 0; }
                else { axisPos = AxisPosition.Right; positionTier = 0; }
            }

            // 轴标题:温度 (℃) / 压力 (MPa) 等
            var unit = groupItems.FirstOrDefault()?.Unit ?? ParameterAxisClassifier.GetDefaultUnit(axisType);
            var title = $"{ParameterAxisClassifier.GetAxisTitle(axisType)} ({unit})";

            // 轴的颜色与轴上第一条曲线的颜色族一致
            var axisColor = GetAxisColor(axisType);

            return new LinearAxis
            {
                Key = GetAxisKey(axisType),
                Position = axisPos,
                PositionTier = positionTier,
                Title = title,
                TitleColor = axisColor,
                TitleFontSize = 10,
                TitleFontWeight = OxyPlot.FontWeights.Bold, // OxyPlot.FontWeights.Bold = 700.0
                Minimum = minR,
                Maximum = maxR,
                FontSize = 10,
                TextColor = axisColor,
                TicklineColor = BorderDefault,
                MajorGridlineStyle = position == 0 ? LineStyle.Solid : LineStyle.None,
                MajorGridlineColor = BorderSubtle,
                MajorGridlineThickness = 0.5,
                MinorGridlineStyle = LineStyle.None,
                AxislineStyle = LineStyle.Solid,
                AxislineColor = BorderDefault,
                AxislineThickness = 0.5,
                StringFormat = GetStringFormatForAxis(axisType, minR, maxR),
                IsPanEnabled = true,
                IsZoomEnabled = true,
                AxisTitleDistance = 4,
            };
        }

        /// <summary>
        /// 计算一组参数的 Y 轴量程。
        /// 逻辑:取所有数据的全局 min/max,然后用"最小有效跨度"撑开稳定值,
        /// 最后两端各留 20% 边距让曲线不贴边。
        /// </summary>
        private static (double minR, double maxR) ComputeAxisRange(List<SeriesInput> groupItems)
        {
            // 收集所有数据点的值
            var allValues = groupItems
                .Where(i => i.Points != null)
                .SelectMany(i => i.Points.Select(p => p.Value))
                .Where(v => !double.IsNaN(v) && !double.IsInfinity(v))
                .ToList();

            if (allValues.Count == 0)
            {
                // 没有数据,给个对称区间避免画图崩溃
                return (-1, 1);
            }

            double dataMin = allValues.Min();
            double dataMax = allValues.Max();
            double dataRange = dataMax - dataMin;
            double absMax = Math.Max(Math.Abs(dataMin), Math.Abs(dataMax));

            // 最小有效跨度:绝对值的 5%,至少 0.1。防止稳定值微抖动被放大成方波。
            double minMeaningfulRange = Math.Max(absMax * 0.05, 0.1);

            double minR, maxR;
            if (dataRange < minMeaningfulRange)
            {
                // 数据基本稳定,围绕中心撑开有意义的窗口
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

            return (minR, maxR);
        }

        /// <summary>
        /// 根据轴类型和量程范围决定数值格式。
        /// </summary>
        private static string GetStringFormatForAxis(AxisType axisType, double minR, double maxR)
        {
            double range = Math.Abs(maxR - minR);
            // 范围很小用更高精度
            if (range < 1) return "0.00";
            if (range < 10) return "0.0";
            return "0";
        }

        private static OxyColor GetAxisColor(AxisType axisType)
        {
            switch (axisType)
            {
                case AxisType.Pressure: return AxisColorPressure;
                case AxisType.Temperature: return AxisColorTemperature;
                case AxisType.Flow: return AxisColorFlow;
                default: return TextSecondary;
            }
        }

        /// <summary>
        /// 创建一条 LineSeries(保留原 OxyPlot 风格:实线 1.5px + 实心圆 marker 2.5 + 白色细描边)。
        /// </summary>
        private static LineSeries CreateLineSeries(SeriesInput input)
        {
            // Marker 用 0.85 透明度(217/255)的同色填充,白色细描边
            var markerFill = OxyColor.FromArgb(217, input.LineColor.R, input.LineColor.G, input.LineColor.B);

            var pointsList = input.Points?.ToList() ?? new List<(DateTime, double)>();

            var series = new LineSeries
            {
                Title = input.DisplayName,
                Color = input.LineColor,
                StrokeThickness = 1.5,
                LineStyle = LineStyle.Solid,
                CanTrackerInterpolatePoints = false,
                YAxisKey = GetAxisKey(input.AxisType),

                // ★ 保留原有的 marker 风格
                MarkerType = pointsList.Count > 200 ? MarkerType.None : MarkerType.Circle,
                MarkerSize = 2.5,
                MarkerFill = markerFill,
                MarkerStroke = OxyColors.White,
                MarkerStrokeThickness = 0.8,

                // Tracker 文本格式占位(由 ViewModel 用 TrackerChanged 事件自定义内容)
                TrackerFormatString = "{0}\n时间: {2:HH:mm:ss}\n值: {4:F2}",
            };

            foreach (var (ts, val) in pointsList)
            {
                if (double.IsNaN(val) || double.IsInfinity(val)) continue;
                series.Points.Add(new DataPoint(DateTimeAxis.ToDouble(ts), val));
            }

            // 把单位塞到 Tag 里,方便 Tracker 显示
            series.Tag = new SeriesMeta
            {
                Unit = input.Unit ?? "",
                AxisType = input.AxisType,
                DisplayName = input.DisplayName,
            };

            return series;
        }

        /// <summary>
        /// 重置所有 Y 轴量程为自适应值(用户点"重置量程"按钮时调用)。
        /// </summary>
        public static void ResetAllAxisRanges(PlotModel model, IEnumerable<SeriesInput> inputs)
        {
            if (model == null || inputs == null) return;

            var groups = inputs
                .Where(i => i?.AxisType != AxisType.Unclassified)
                .GroupBy(i => i.AxisType);

            foreach (var grp in groups)
            {
                var axisKey = GetAxisKey(grp.Key);
                var axis = model.Axes.FirstOrDefault(a => a.Key == axisKey) as LinearAxis;
                if (axis == null) continue;

                var (minR, maxR) = ComputeAxisRange(grp.ToList());
                axis.Reset();        // 清掉用户滚轮缩放产生的 ActualMinimum/Maximum
                axis.Minimum = minR;
                axis.Maximum = maxR;
            }

            model.InvalidatePlot(false);
        }

        /// <summary>
        /// Series.Tag 的元数据,用于 Tracker 自定义文本。
        /// </summary>
        public class SeriesMeta
        {
            public string Unit { get; set; } = "";
            public AxisType AxisType { get; set; }
            public string DisplayName { get; set; } = "";
        }
    }
}