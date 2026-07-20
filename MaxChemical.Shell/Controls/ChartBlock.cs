using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace MaxChemical.Shell.Controls
{
    /// <summary>
    /// 轻量图表控件(小桐对话内嵌,配色取自 ThemeColors.xaml 品牌色板):
    /// 支持折线(line)、柱状(bar)、横向条形(hbar,用于 Pareto)、散点(scatter,可带对角参考线)。
    /// 带网格、坐标轴、轴标题、参考线、显著标记与高亮点;输入为 JSON 规格,
    /// 解析失败静默降级,绝不抛异常。
    /// 规格字段:type/title/xLabel/yLabel/x(类别标签)/xNumeric(散点数值X)/
    /// series[{name,values}]/refLine(参考线)/highlightIndex(高亮点)/
    /// flags(hbar 逐条显著标记)/diagonal(散点对角线)。
    /// </summary>
    public sealed class ChartBlock : FrameworkElement
    {
        // 与 Resources/ThemeColors.xaml 保持一致
        private static readonly Brush BrTitle = Freeze("#1A1A1A");
        private static readonly Brush BrAxis = Freeze("#5F5E5A");
        private static readonly Brush BrGrid = Freeze("#EAEAEA");
        private static readonly Brush BrRef = Freeze("#BA7517");
        private static readonly Brush BrHighlight = Freeze("#A41626");
        private static readonly Brush BrMuted = Freeze("#D3D1C7");
        private static readonly Brush[] SeriesBrushes =
        {
            Freeze("#A41626"), Freeze("#1A1A1A"), Freeze("#BA7517"), Freeze("#1D9E75")
        };

        private static readonly Typeface Face = new(
            new FontFamily("Segoe UI, Microsoft YaHei UI"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private static readonly Typeface FaceBold = new(
            new FontFamily("Segoe UI, Microsoft YaHei UI"),
            FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        private static Brush Freeze(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            b.Freeze();
            return b;
        }

        public static readonly DependencyProperty ChartJsonProperty = DependencyProperty.Register(
            nameof(ChartJson), typeof(string), typeof(ChartBlock),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender,
                (d, e) => ((ChartBlock)d).OnJsonChanged()));

        public string ChartJson
        {
            get => (string)GetValue(ChartJsonProperty);
            set => SetValue(ChartJsonProperty, value);
        }

        private Spec _spec;

        private void OnJsonChanged()
        {
            _spec = Parse(ChartJson);
            InvalidateMeasure();
            InvalidateVisual();
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double w = double.IsInfinity(availableSize.Width) ? 540 : Math.Min(availableSize.Width, 560);
            w = Math.Max(w, 320);

            // hbar 高度随条目数自适应,其余固定
            double h = 288;
            if (_spec != null && _spec.Type == "hbar")
                h = Math.Min(Math.Max(84 + _spec.X.Count * 27, 200), 440);
            return new Size(w, h);
        }

        // ── 解析后的规格 ──
        private sealed class Spec
        {
            public string Type = "line";
            public string Title = "";
            public string XLabel = "";
            public string YLabel = "";
            public List<string> X = new();
            public List<double> XNumeric = new();
            public List<(string Name, List<double?> Values)> Series = new();
            public List<bool> Flags = new();
            public double? RefLine;
            public int HighlightIndex = -1;
            public bool Diagonal;
        }

        private static Spec Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var s = new Spec();
                if (root.TryGetProperty("type", out var t)) s.Type = t.GetString() ?? "line";
                if (root.TryGetProperty("title", out var ti)) s.Title = ti.GetString() ?? "";
                if (root.TryGetProperty("xLabel", out var xl)) s.XLabel = xl.GetString() ?? "";
                if (root.TryGetProperty("yLabel", out var yl)) s.YLabel = yl.GetString() ?? "";
                if (root.TryGetProperty("refLine", out var rl) && rl.ValueKind == JsonValueKind.Number)
                    s.RefLine = rl.GetDouble();
                if (root.TryGetProperty("highlightIndex", out var hi) && hi.ValueKind == JsonValueKind.Number)
                    s.HighlightIndex = hi.GetInt32();
                if (root.TryGetProperty("diagonal", out var dg))
                    s.Diagonal = dg.ValueKind == JsonValueKind.True;
                if (root.TryGetProperty("x", out var xs) && xs.ValueKind == JsonValueKind.Array)
                    foreach (var e in xs.EnumerateArray())
                        s.X.Add(e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString());
                if (root.TryGetProperty("xNumeric", out var xn) && xn.ValueKind == JsonValueKind.Array)
                    foreach (var e in xn.EnumerateArray())
                        if (e.ValueKind == JsonValueKind.Number) s.XNumeric.Add(e.GetDouble());
                if (root.TryGetProperty("flags", out var fl) && fl.ValueKind == JsonValueKind.Array)
                    foreach (var e in fl.EnumerateArray())
                        s.Flags.Add(e.ValueKind == JsonValueKind.True);
                if (root.TryGetProperty("series", out var ss) && ss.ValueKind == JsonValueKind.Array)
                {
                    foreach (var se in ss.EnumerateArray())
                    {
                        string name = se.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var vals = new List<double?>();
                        if (se.TryGetProperty("values", out var vv) && vv.ValueKind == JsonValueKind.Array)
                            foreach (var v in vv.EnumerateArray())
                                vals.Add(v.ValueKind == JsonValueKind.Number ? v.GetDouble() : (double?)null);
                        s.Series.Add((name, vals));
                    }
                }
                if (s.Series.Count == 0) return null;
                if (s.X.Count == 0 && s.XNumeric.Count == 0) return null;
                return s;
            }
            catch { return null; }
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_spec == null) _spec = Parse(ChartJson);
            double W = ActualWidth > 0 ? ActualWidth : 540, H = ActualHeight > 0 ? ActualHeight : 288;
            if (_spec == null)
            {
                dc.DrawText(Text("图表数据无效", 11, BrAxis), new Point(8, 8));
                return;
            }

            try
            {
                if (_spec.Type == "hbar") RenderHBar(dc, _spec, W, H);
                else if (_spec.Type == "scatter" && _spec.XNumeric.Count > 0) RenderScatter(dc, _spec, W, H);
                else RenderCategorical(dc, _spec, W, H);
            }
            catch { /* 任何绘制异常都不允许影响聊天窗 */ }
        }

        // ══════════════ 类别轴:折线 / 柱状 ══════════════

        private void RenderCategorical(DrawingContext dc, Spec s, double W, double H)
        {
            double top = DrawTitle(dc, s, W);
            double bottomPad = string.IsNullOrEmpty(s.XLabel) ? 26 : 40;
            var plot = new Rect(50, top, Math.Max(W - 50 - 14, 40), Math.Max(H - top - bottomPad, 40));

            var all = s.Series.SelectMany(se => se.Values).Where(v => v.HasValue).Select(v => v.Value).ToList();
            if (s.RefLine.HasValue) all.Add(s.RefLine.Value);
            if (all.Count == 0) return;
            var (vMin, vMax) = NiceRange(all, s.Type == "bar");

            double YOf(double v) => plot.Bottom - (v - vMin) / (vMax - vMin) * plot.Height;
            DrawValueAxis(dc, plot, vMin, vMax, YOf, s.YLabel);

            int n = s.X.Count;
            double XOf(int idx) => n <= 1 ? plot.Left + plot.Width / 2
                                          : plot.Left + plot.Width * idx / (n - 1);

            // X 轴标签(自动抽稀)
            int step = Math.Max(1, (int)Math.Ceiling(n / Math.Max(plot.Width / 36, 1)));
            for (int i = 0; i < n; i += step)
            {
                var ft = Text(s.X[i], 9.5, BrAxis);
                double cx = s.Type == "bar" ? plot.Left + plot.Width * (i + 0.5) / n : XOf(i);
                dc.DrawText(ft, new Point(cx - ft.Width / 2, plot.Bottom + 4));
            }
            DrawXAxisTitle(dc, s.XLabel, plot, H);
            DrawRefLine(dc, s, plot, YOf, horizontal: true);

            for (int si = 0; si < s.Series.Count && si < SeriesBrushes.Length; si++)
            {
                var brush = SeriesBrushes[si];
                var vals = s.Series[si].Values;

                if (s.Type == "bar")
                {
                    double slot = plot.Width / n;
                    double barW = Math.Max(Math.Min(slot * 0.55, 44), 6);
                    for (int i = 0; i < n && i < vals.Count; i++)
                    {
                        if (!vals[i].HasValue) continue;
                        double cx = plot.Left + slot * (i + 0.5);
                        double y = YOf(vals[i].Value), y0 = YOf(Math.Max(vMin, 0));
                        var rect = new Rect(cx - barW / 2, Math.Min(y, y0), barW, Math.Max(Math.Abs(y0 - y), 1));
                        var b = i == s.HighlightIndex ? BrHighlight : brush;
                        dc.DrawRoundedRectangle(b, null, rect, 2, 2);
                        // 柱顶数值
                        if (n <= 12)
                        {
                            var fv = Text(FormatVal(vals[i].Value), 9, BrAxis);
                            dc.DrawText(fv, new Point(cx - fv.Width / 2, Math.Min(y, y0) - fv.Height - 1));
                        }
                    }
                }
                else
                {
                    var pen = new Pen(brush, 1.8) { LineJoin = PenLineJoin.Round };
                    Point? prev = null;
                    for (int i = 0; i < n && i < vals.Count; i++)
                    {
                        if (!vals[i].HasValue) { prev = null; continue; }
                        var p = new Point(XOf(i), YOf(vals[i].Value));
                        if (s.Type == "line" && prev.HasValue) dc.DrawLine(pen, prev.Value, p);
                        prev = p;
                    }
                    for (int i = 0; i < n && i < vals.Count; i++)
                    {
                        if (!vals[i].HasValue) continue;
                        var p = new Point(XOf(i), YOf(vals[i].Value));
                        bool hl = i == s.HighlightIndex;
                        dc.DrawEllipse(hl ? BrHighlight : brush, null, p, hl ? 4.5 : 3, hl ? 4.5 : 3);
                        // 点数值(点少时全标,多时只标高亮)
                        if (hl || n <= 10)
                        {
                            var fv = Text(FormatVal(vals[i].Value), hl ? 10 : 9, hl ? BrHighlight : BrAxis, bold: hl);
                            dc.DrawText(fv, new Point(
                                Math.Min(Math.Max(p.X - fv.Width / 2, plot.Left), plot.Right - fv.Width),
                                Math.Max(p.Y - fv.Height - 5, plot.Top)));
                        }
                    }
                }
            }

            DrawLegend(dc, s, plot);
            dc.DrawLine(new Pen(BrGrid, 1), new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
        }

        // ══════════════ 横向条形(Pareto:标准化效应) ══════════════

        private void RenderHBar(DrawingContext dc, Spec s, double W, double H)
        {
            double top = DrawTitle(dc, s, W);
            var vals = s.Series[0].Values;
            int n = Math.Min(s.X.Count, vals.Count);
            if (n == 0) return;

            // 左侧留出最长类别名宽度
            double labelW = 0;
            for (int i = 0; i < n; i++)
                labelW = Math.Max(labelW, Text(s.X[i], 10, BrAxis).Width);
            labelW = Math.Min(labelW + 10, W * 0.4);

            double bottomPad = string.IsNullOrEmpty(s.XLabel) ? 24 : 38;
            var plot = new Rect(labelW + 8, top, Math.Max(W - labelW - 8 - 14, 40), Math.Max(H - top - bottomPad, 40));

            var all = vals.Where(v => v.HasValue).Select(v => v.Value).ToList();
            if (s.RefLine.HasValue) all.Add(s.RefLine.Value);
            if (all.Count == 0) return;
            double vMax = Math.Max(all.Max() * 1.12, 1e-9);
            double XOfV(double v) => plot.Left + v / vMax * plot.Width;

            // 垂直网格 + 刻度
            var gridPen = new Pen(BrGrid, 1);
            for (int i = 0; i <= 4; i++)
            {
                double v = vMax * i / 4;
                double x = XOfV(v);
                dc.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                var ft = Text(FormatVal(v), 9.5, BrAxis);
                dc.DrawText(ft, new Point(x - ft.Width / 2, plot.Bottom + 4));
            }
            DrawXAxisTitle(dc, s.XLabel, plot, H);

            double slot = plot.Height / n;
            double barH = Math.Max(Math.Min(slot * 0.62, 22), 6);
            for (int i = 0; i < n; i++)
            {
                double cy = plot.Top + slot * (i + 0.5);
                var lab = Text(s.X[i], 10, BrAxis);
                dc.DrawText(lab, new Point(plot.Left - 8 - lab.Width, cy - lab.Height / 2));

                if (!vals[i].HasValue) continue;
                bool flagged = i < s.Flags.Count && s.Flags[i];
                var rect = new Rect(plot.Left, cy - barH / 2, Math.Max(XOfV(vals[i].Value) - plot.Left, 1), barH);
                dc.DrawRoundedRectangle(flagged ? BrHighlight : BrMuted, null, rect, 2, 2);

                var fv = Text(FormatVal(vals[i].Value), 9, flagged ? BrHighlight : BrAxis, bold: flagged);
                dc.DrawText(fv, new Point(rect.Right + 4, cy - fv.Height / 2));
            }

            // 显著性临界线(垂直虚线)
            if (s.RefLine.HasValue)
            {
                var refPen = new Pen(BrRef, 1.3) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
                double x = XOfV(s.RefLine.Value);
                dc.DrawLine(refPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                var ft = Text(FormatVal(s.RefLine.Value), 9.5, BrRef, bold: true);
                dc.DrawText(ft, new Point(Math.Min(x + 3, plot.Right - ft.Width), plot.Top - 2));
            }

            dc.DrawLine(new Pen(BrGrid, 1), new Point(plot.Left, plot.Top), new Point(plot.Left, plot.Bottom));
        }

        // ══════════════ 数值轴散点(实际 vs 预测) ══════════════

        private void RenderScatter(DrawingContext dc, Spec s, double W, double H)
        {
            double top = DrawTitle(dc, s, W);
            double bottomPad = string.IsNullOrEmpty(s.XLabel) ? 26 : 40;
            var plot = new Rect(50, top, Math.Max(W - 50 - 14, 40), Math.Max(H - top - bottomPad, 40));

            var vals = s.Series[0].Values;
            int n = Math.Min(s.XNumeric.Count, vals.Count);
            var ys = new List<double>();
            var xs = new List<double>();
            var origIdx = new List<int>();
            for (int i = 0; i < n; i++)
                if (vals[i].HasValue) { xs.Add(s.XNumeric[i]); ys.Add(vals[i].Value); origIdx.Add(i); }
            if (xs.Count == 0) return;

            // 定标:仅画 y=x 对角线时(实际vs预测,两轴同单位)才共用范围;
            // 通用散点(如 τ vs 转化率)两轴量纲不同,必须各自独立定标,否则数据被压扁
            double vMin, vMax, xMin, xMax;
            if (s.Diagonal)
            {
                var domain = new List<double>(xs);
                domain.AddRange(ys);
                (vMin, vMax) = NiceRange(domain, false);
                (xMin, xMax) = (vMin, vMax);
            }
            else
            {
                (vMin, vMax) = NiceRange(ys, false);
                (xMin, xMax) = NiceRange(xs, false);
            }

            double XOfV(double v) => plot.Left + (v - xMin) / (xMax - xMin) * plot.Width;
            double YOfV(double v) => plot.Bottom - (v - vMin) / (vMax - vMin) * plot.Height;

            DrawValueAxis(dc, plot, vMin, vMax, YOfV, s.YLabel);
            // X 轴刻度
            for (int i = 0; i <= 4; i++)
            {
                double v = xMin + (xMax - xMin) * i / 4;
                var ft = Text(FormatVal(v), 9.5, BrAxis);
                dc.DrawText(ft, new Point(XOfV(v) - ft.Width / 2, plot.Bottom + 4));
            }
            DrawXAxisTitle(dc, s.XLabel, plot, H);

            // 对角线 y=x(虚线):点越贴近说明预测越准
            if (s.Diagonal)
            {
                var diagPen = new Pen(BrRef, 1.2) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
                dc.DrawLine(diagPen,
                    new Point(XOfV(vMin), YOfV(vMin)),
                    new Point(XOfV(vMax), YOfV(vMax)));
            }

            for (int i = 0; i < xs.Count; i++)
            {
                bool hl = origIdx[i] == s.HighlightIndex;
                var p = new Point(XOfV(xs[i]), YOfV(ys[i]));
                dc.DrawEllipse(hl ? BrHighlight : SeriesBrushes[0], null, p, hl ? 4.5 : 3.2, hl ? 4.5 : 3.2);
                if (hl)
                {
                    var fv = Text(FormatVal(ys[i]), 10, BrHighlight, bold: true);
                    dc.DrawText(fv, new Point(
                        Math.Min(Math.Max(p.X - fv.Width / 2, plot.Left), plot.Right - fv.Width),
                        Math.Max(p.Y - fv.Height - 6, plot.Top)));
                }
            }

            dc.DrawLine(new Pen(BrGrid, 1), new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));
        }

        // ══════════════ 公共绘制 ══════════════

        private double DrawTitle(DrawingContext dc, Spec s, double W)
        {
            double top = 8;
            if (!string.IsNullOrEmpty(s.Title))
            {
                var ft = Text(s.Title, 12.5, BrTitle, bold: true);
                dc.DrawText(ft, new Point(Math.Max((W - ft.Width) / 2, 4), top));
                top += ft.Height + 8;
            }
            return top;
        }

        private void DrawValueAxis(DrawingContext dc, Rect plot, double vMin, double vMax,
            Func<double, double> yOf, string yLabel)
        {
            var gridPen = new Pen(BrGrid, 1);
            for (int i = 0; i <= 4; i++)
            {
                double v = vMin + (vMax - vMin) * i / 4;
                double y = yOf(v);
                dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
                var ft = Text(FormatVal(v), 9.5, BrAxis);
                dc.DrawText(ft, new Point(plot.Left - ft.Width - 5, y - ft.Height / 2));
            }
            if (!string.IsNullOrEmpty(yLabel))
                dc.DrawText(Text(yLabel, 9.5, BrAxis), new Point(2, plot.Top - 14));
        }

        private void DrawXAxisTitle(DrawingContext dc, string xLabel, Rect plot, double H)
        {
            if (string.IsNullOrEmpty(xLabel)) return;
            var ft = Text(xLabel, 9.5, BrAxis);
            dc.DrawText(ft, new Point(plot.Left + (plot.Width - ft.Width) / 2, H - ft.Height - 2));
        }

        private void DrawRefLine(DrawingContext dc, Spec s, Rect plot, Func<double, double> yOf, bool horizontal)
        {
            if (!s.RefLine.HasValue) return;
            var refPen = new Pen(BrRef, 1.2) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
            double y = yOf(s.RefLine.Value);
            dc.DrawLine(refPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var ft = Text(FormatVal(s.RefLine.Value), 9.5, BrRef);
            dc.DrawText(ft, new Point(plot.Right - ft.Width, y - ft.Height - 1));
        }

        private void DrawLegend(DrawingContext dc, Spec s, Rect plot)
        {
            if (s.Series.Count <= 1) return;
            double lx = plot.Right, ly = plot.Top + 2;
            for (int si = s.Series.Count - 1; si >= 0 && si < SeriesBrushes.Length; si--)
            {
                var ft = Text(s.Series[si].Name, 9.5, BrAxis);
                lx -= ft.Width;
                dc.DrawText(ft, new Point(lx, ly));
                lx -= 12;
                dc.DrawEllipse(SeriesBrushes[si], null, new Point(lx + 4, ly + ft.Height / 2), 3.5, 3.5);
                lx -= 14;
            }
        }

        private static (double Min, double Max) NiceRange(List<double> all, bool barFromZero)
        {
            double vMin = all.Min(), vMax = all.Max();
            if (Math.Abs(vMax - vMin) < 1e-12) { vMax += 1; vMin -= 1; }
            double pad = (vMax - vMin) * 0.10;
            vMin -= pad; vMax += pad;
            if (barFromZero && vMin > 0) vMin = 0;
            return (vMin, vMax);
        }

        private static string FormatVal(double v)
        {
            double a = Math.Abs(v);
            if (a >= 1000) return v.ToString("0");
            if (a >= 10) return v.ToString("0.#");
            if (a >= 0.01 || a == 0) return v.ToString("0.##");
            return v.ToString("0.####");
        }

        private FormattedText Text(string s, double size, Brush brush, bool bold = false)
            => new(s ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                   bold ? FaceBold : Face, size, brush,
                   VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }
}
