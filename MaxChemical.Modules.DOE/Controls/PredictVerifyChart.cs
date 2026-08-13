using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using MaxChemical.Modules.DOE.Events;

namespace MaxChemical.Modules.DOE.Controls
{
    /// <summary>
    /// 先猜后验图:每组一根「开跑前预测区间」竖棒(±2σ 预测区间,含观测噪声),
    /// 实测回来后画点——区间内蓝点、95% 区间外琥珀点、超 3σ 红点带圈。
    /// 区间逐组收窄本身就是给用户看的「模型在学习」的证据,不需要额外解释。
    /// 轻量 FrameworkElement,整体重绘,数据来自 DOEPredictVerifyPayload.Entries。
    /// </summary>
    public sealed class PredictVerifyChart : FrameworkElement
    {
        public static readonly DependencyProperty EntriesProperty = DependencyProperty.Register(
            nameof(Entries), typeof(IList<PredictVerifyEntry>), typeof(PredictVerifyChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public IList<PredictVerifyEntry> Entries
        {
            get => (IList<PredictVerifyEntry>)GetValue(EntriesProperty);
            set => SetValue(EntriesProperty, value);
        }

        // 与执行看板同一套浅色系
        private static readonly Brush BrInk = Freeze(new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51)));
        private static readonly Brush BrMuted = Freeze(new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF)));
        private static readonly Pen PenGrid = FreezePen(Color.FromRgb(0xE2, 0xE5, 0xEB), 1);
        private static readonly Pen PenBar = FreezePen(Color.FromRgb(0xB4, 0xB2, 0xA9), 2);
        private static readonly Pen PenBarCur = FreezePen(Color.FromRgb(0xD9, 0x77, 0x06), 3);
        private static readonly Brush BrCur = Freeze(new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)));
        private static readonly Brush BrHit = Freeze(new SolidColorBrush(Color.FromRgb(0x2A, 0x78, 0xD6)));
        private static readonly Brush BrMiss = Freeze(new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)));
        private static readonly Brush BrAlarm = Freeze(new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)));
        private static readonly Pen PenAlarm = FreezePen(Color.FromRgb(0xDC, 0x26, 0x26), 2);

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }
        private static Pen FreezePen(Color c, double w)
        {
            var p = new Pen(new SolidColorBrush(c), w);
            p.Freeze();
            return p;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            if (w < 60 || h < 40) return;

            var entries = Entries?.Where(e => e != null).OrderBy(e => e.RunIndex).ToList();
            if (entries == null || entries.Count == 0) return;

            // ── y 轴范围:预测区间与实测的并集,留 8% 边距 ──
            var ys = new List<double>();
            foreach (var e in entries)
            {
                if (e.Measured.HasValue) ys.Add(e.Measured.Value);
                if (e.Predicted.HasValue && e.HalfWidth2.HasValue)
                {
                    ys.Add(e.Predicted.Value + e.HalfWidth2.Value);
                    ys.Add(e.Predicted.Value - e.HalfWidth2.Value);
                }
            }
            if (ys.Count == 0) return;
            double yMin = ys.Min(), yMax = ys.Max();
            if (yMax - yMin < 1e-9) { yMax += 1; yMin -= 1; }
            double pad = (yMax - yMin) * 0.10;
            yMin -= pad; yMax += pad;

            double left = 42, right = w - 8, top = 8, bottom = h - 20;
            if (right - left < 30 || bottom - top < 20) return;

            double PxX(int idx) => left + 12 + (entries.Count == 1 ? 0 : (double)idx / (entries.Count - 1) * (right - left - 24));
            double PxY(double v) => bottom - (v - yMin) / (yMax - yMin) * (bottom - top);

            // ── 网格与 y 标签(4 档) ──
            for (int t = 0; t <= 3; t++)
            {
                double v = yMin + (yMax - yMin) * t / 3.0;
                double y = PxY(v);
                dc.DrawLine(PenGrid, new Point(left, y), new Point(right, y));
                DrawText(dc, Fmt(v), 10, BrMuted, left - 4, y - 6, TextAlignment.Right);
            }

            // ── x 标签(最多 6 个) ──
            int step = Math.Max(1, (int)Math.Ceiling(entries.Count / 6.0));
            for (int i = 0; i < entries.Count; i += step)
            {
                int no = entries[i].DisplayNo > 0 ? entries[i].DisplayNo : entries[i].RunIndex;
                DrawText(dc, $"#{no}", 9.5, BrMuted, PxX(i), bottom + 4, TextAlignment.Center);
            }

            // ── 逐组:区间棒 + 实测点 ──
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                double x = PxX(i);

                if (e.Predicted.HasValue && e.HalfWidth2.HasValue)
                {
                    double hi = PxY(e.Predicted.Value + e.HalfWidth2.Value);
                    double lo = PxY(e.Predicted.Value - e.HalfWidth2.Value);
                    var pen = e.IsCurrent ? PenBarCur : PenBar;
                    dc.DrawLine(pen, new Point(x, hi), new Point(x, lo));
                    dc.DrawLine(pen, new Point(x - 4, hi), new Point(x + 4, hi));
                    dc.DrawLine(pen, new Point(x - 4, lo), new Point(x + 4, lo));
                    if (e.IsCurrent)
                        DrawText(dc, $"{Fmt(e.Predicted.Value)} ± {Fmt(e.HalfWidth2.Value)}", 10, BrCur,
                            x, Math.Max(0, hi - 14), TextAlignment.Center);
                }

                if (e.Measured.HasValue)
                {
                    double y = PxY(e.Measured.Value);
                    if (e.IsAlarm3)
                    {
                        dc.DrawEllipse(BrAlarm, null, new Point(x, y), 5, 5);
                        dc.DrawEllipse(null, PenAlarm, new Point(x, y), 9, 9);
                    }
                    else if (e.IsMiss2)
                    {
                        dc.DrawEllipse(BrMiss, null, new Point(x, y), 4.5, 4.5);
                    }
                    else
                    {
                        dc.DrawEllipse(BrHit, null, new Point(x, y), 4, 4);
                    }
                }
            }
        }

        private void DrawText(DrawingContext dc, string text, double size, Brush brush,
            double x, double y, TextAlignment align)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI, Microsoft YaHei UI"), size, brush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            double ox = align == TextAlignment.Center ? x - ft.Width / 2
                      : align == TextAlignment.Right ? x - ft.Width : x;
            dc.DrawText(ft, new Point(ox, y));
        }

        private static string Fmt(double v)
        {
            double a = Math.Abs(v);
            if (a >= 100) return v.ToString("0");
            if (a >= 1) return v.ToString("0.#");
            return v.ToString("0.###");
        }
    }
}
