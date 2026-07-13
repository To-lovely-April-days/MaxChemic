using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MaxChemical.Modules.Designer.Views;
using MaxChemical.Modules.Designer.Views.Controls.Service;

namespace MaxChemical.Modules.Designer.Service
{
    /// <summary>
    /// 把工艺设计器的画布(渲染后的可视化树)导出成矢量 SVG。
    /// 关键点:遍历"渲染结果"而非绘制逻辑 —— 设备/管路的几何都是 code-behind 算好填进
    /// Path/Rectangle/… 的,这里直接读取并转成等价 SVG 元素,一次覆盖所有设备与以后的新设备。
    ///
    /// 用法(在 ProcessDesignerView 里,DesignCanvas 就是承载设备+管路的 Canvas):
    ///   canvas.UpdateLayout();
    ///   string svg = CanvasSvgExporter.ExportToSvg(canvas, canvas.ActualWidth, canvas.ActualHeight,
    ///                    fe => TryResolveDeviceId(fe));   // deviceIdResolver 可选
    ///
    /// 云端拿到这段 SVG 直接内联渲染 = 桌面原样(矢量、可缩放),再叠实时数值/流体动画/点选。
    /// </summary>
    public static class CanvasSvgExporter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string ExportToSvg(FrameworkElement root, double width, double height,
                                         Func<FrameworkElement, string?>? deviceIdResolver = null)
        {
            root.UpdateLayout();
            var defs = new StringBuilder();
            var body = new StringBuilder();
            int gradId = 0;
            WalkChildren(root, root, body, defs, ref gradId, deviceIdResolver, insideDevice: false, flowDir: null, cardName: null);

            var sb = new StringBuilder();
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ")
              .Append(F(width)).Append(' ').Append(F(height)).Append("\" width=\"")
              .Append(F(width)).Append("\" height=\"").Append(F(height)).Append("\">");
            if (defs.Length > 0) sb.Append("<defs>").Append(defs).Append("</defs>");
            sb.Append(body);
            sb.Append("</svg>");
            return sb.ToString();
        }

        // ── 递归遍历可视化树 ──
        private static void WalkChildren(DependencyObject node, Visual root, StringBuilder body,
                                         StringBuilder defs, ref int gid,
                                         Func<FrameworkElement, string?>? resolver, bool insideDevice,
                                         string? flowDir, string? cardName)
        {
            int n = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is not FrameworkElement fe) { WalkChildren(child, root, body, defs, ref gid, resolver, insideDevice, flowDir, cardName); continue; }

                if (fe.Visibility != Visibility.Visible) continue;
                if (fe.Opacity <= 0.001) continue;                 // 锚点/选中框等隐形元素

                // 管路流向:遇到管道控件(实现 IPipeProperties),记下它这一段的 PipeFlowDir,
                // 往下传给子树里的流体色块(命名含 Fluid),导出成 data-flow-dir → 云端据此精确定向。
                string? segFlowDir = flowDir;
                if (fe is IPipeProperties pipe) segFlowDir = pipe.PipeFlowDir.ToString();

                // 监控卡片:记下卡片显示的设备名,传给子树里的数值文本(Tag=mc-value),
                // 导出成 data-val="设备名|指标" → 云端据此精确绑定实时值(不再靠坐标猜测)。
                string? curCard = cardName;
                if (fe is MonitoringCard mc) curCard = mc.DeviceName;

                // 设备分组:遇到设备控件,给整棵子树套一个带 data-device-id 的组(云端据此绑实时值)
                string? devId = (!insideDevice && resolver != null) ? resolver(fe) : null;
                bool openedGroup = false;
                if (devId != null)
                {
                    body.Append("<g data-device-id=\"").Append(Esc(devId)).Append("\">");
                    openedGroup = true;
                }

                if (fe is Shape shape) EmitShape(shape, root, body, defs, ref gid, segFlowDir);
                else if (fe is TextBlock tb) EmitText(tb, root, body, curCard);
                else if (fe is Border bd)
                {
                    // 设备宿主边框(Tag=设备Id、DarkBlue、圆角)是编辑器外框/选中框,不属于工艺图,
                    // 不绘制它自己,只作为 data-device-id 分组 + 子树定位锚点。
                    if (devId == null) EmitBorder(bd, root, body, defs, ref gid, curCard);
                    WalkChildren(bd, root, body, defs, ref gid, resolver, insideDevice || devId != null, segFlowDir, curCard);
                }
                else if (fe is Image img) EmitImage(img, root, body);
                else WalkChildren(fe, root, body, defs, ref gid, resolver, insideDevice || devId != null, segFlowDir, curCard);

                if (fe is Shape || fe is TextBlock || fe is Image) { /* 叶子,无需递归 */ }

                if (openedGroup) body.Append("</g>");
            }
        }

        // ── 元素 → root 的仿射矩阵(3 点法,兼容任意 GeneralTransform)──
        private static Matrix MatrixToRoot(Visual el, Visual root)
        {
            try
            {
                var gt = el.TransformToVisual(root);
                Point o = gt.Transform(new Point(0, 0));
                Point x = gt.Transform(new Point(1, 0));
                Point y = gt.Transform(new Point(0, 1));
                return new Matrix(x.X - o.X, x.Y - o.Y, y.X - o.X, y.Y - o.Y, o.X, o.Y);
            }
            catch { return Matrix.Identity; }
        }

        private static string MatrixAttr(Matrix m) =>
            $" transform=\"matrix({F(m.M11)} {F(m.M12)} {F(m.M21)} {F(m.M22)} {F(m.OffsetX)} {F(m.OffsetY)})\"";

        // ── 各类图元 ──
        private static void EmitShape(Shape s, Visual root, StringBuilder body, StringBuilder defs, ref int gid, string? flowDir)
        {
            string tf = MatrixAttr(MatrixToRoot(s, root));
            string fill = BrushToSvg(s.Fill, "fill", defs, ref gid, s.Opacity);
            string stroke = BrushToSvg(s.Stroke, "stroke", defs, ref gid, s.Opacity);
            string sw = s.StrokeThickness > 0 && s.Stroke != null ? $" stroke-width=\"{F(s.StrokeThickness)}\"" : "";
            string dash = DashAttr(s.StrokeDashArray);
            // 默认流动样式(赛博流光)用 CyberMainStream/CyberParticleStream/CyberSparkStream 这几条描边虚线 Path 表现流动;
            // 桌面的 Storyboard 不随 SVG 导出,这里给它们打 data-flow-stroke(带流向),云端补 stroke-dashoffset 动画。
            string streamAttr = (!string.IsNullOrEmpty(s.Name) && s.Name.Contains("Cyber") && s.Name.Contains("Stream"))
                ? $" data-flow-stroke=\"{Esc(flowDir ?? "")}\"" : "";

            switch (s)
            {
                case Rectangle r:
                    // 管路流体色块(命名含 Fluid)带上所属管段的流向,云端 animateFlow 据此定向流动虚线。
                    string flowAttr = (flowDir != null && !string.IsNullOrEmpty(r.Name) && r.Name.Contains("Fluid"))
                        ? $" data-flow-dir=\"{Esc(flowDir)}\"" : "";
                    body.Append("<rect x=\"0\" y=\"0\" width=\"").Append(F(r.ActualWidth > 0 ? r.ActualWidth : r.Width))
                        .Append("\" height=\"").Append(F(r.ActualHeight > 0 ? r.ActualHeight : r.Height)).Append('"');
                    if (r.RadiusX > 0) body.Append(" rx=\"").Append(F(r.RadiusX)).Append('"');
                    if (r.RadiusY > 0) body.Append(" ry=\"").Append(F(r.RadiusY)).Append('"');
                    body.Append(flowAttr).Append(fill).Append(stroke).Append(sw).Append(dash).Append(tf).Append("/>");
                    break;
                case Ellipse e:
                    double ew = e.ActualWidth > 0 ? e.ActualWidth : e.Width, eh = e.ActualHeight > 0 ? e.ActualHeight : e.Height;
                    body.Append("<ellipse cx=\"").Append(F(ew / 2)).Append("\" cy=\"").Append(F(eh / 2))
                        .Append("\" rx=\"").Append(F(ew / 2)).Append("\" ry=\"").Append(F(eh / 2)).Append('"')
                        .Append(fill).Append(stroke).Append(sw).Append(dash).Append(tf).Append("/>");
                    break;
                case Line ln:
                    body.Append("<line x1=\"").Append(F(ln.X1)).Append("\" y1=\"").Append(F(ln.Y1))
                        .Append("\" x2=\"").Append(F(ln.X2)).Append("\" y2=\"").Append(F(ln.Y2)).Append('"')
                        .Append(stroke).Append(sw).Append(dash).Append(" stroke-linecap=\"round\"").Append(tf).Append("/>");
                    break;
                case Polygon pg:
                    body.Append("<polygon points=\"").Append(Points(pg.Points)).Append('"')
                        .Append(fill).Append(stroke).Append(sw).Append(dash).Append(tf).Append("/>");
                    break;
                case Polyline pl:
                    body.Append("<polyline points=\"").Append(Points(pl.Points)).Append('"')
                        .Append(fill).Append(stroke).Append(sw).Append(dash).Append(tf).Append("/>");
                    break;
                case System.Windows.Shapes.Path p when p.Data != null:
                    body.Append("<path d=\"").Append(GeometryToPath(p.Data)).Append('"')
                        .Append(streamAttr).Append(fill).Append(stroke).Append(sw).Append(dash)
                        .Append(" stroke-linejoin=\"round\" stroke-linecap=\"round\"").Append(tf).Append("/>");
                    break;
            }
        }

        private static void EmitBorder(Border b, Visual root, StringBuilder body, StringBuilder defs, ref int gid, string? cardName = null)
        {
            string tf = MatrixAttr(MatrixToRoot(b, root));
            double w = b.ActualWidth, h = b.ActualHeight;
            if (w <= 0 || h <= 0) return;
            string fill = BrushToSvg(b.Background, "fill", defs, ref gid, b.Opacity);
            double rx = b.CornerRadius.TopLeft;
            string stroke = BrushToSvg(b.BorderBrush, "stroke", defs, ref gid, b.Opacity);
            string sw = b.BorderThickness.Left > 0 && b.BorderBrush != null ? $" stroke-width=\"{F(b.BorderThickness.Left)}\"" : "";
            // 监控卡片内的矩形(外框/单元格/分隔)带上 data-cardname,云端据此对整张卡片做重排(值溢出时右移右侧元素+加宽外框)
            string card = cardName != null ? $" data-cardname=\"{Esc(cardName)}\"" : "";
            body.Append("<rect x=\"0\" y=\"0\" width=\"").Append(F(w)).Append("\" height=\"").Append(F(h)).Append('"');
            if (rx > 0) body.Append(" rx=\"").Append(F(rx)).Append('"');
            body.Append(card).Append(fill).Append(stroke).Append(sw).Append(tf).Append("/>");
        }

        private static void EmitText(TextBlock tb, Visual root, StringBuilder body, string? cardName)
        {
            if (string.IsNullOrEmpty(tb.Text)) return;
            var m = MatrixToRoot(tb, root);
            double fs = tb.FontSize > 0 ? tb.FontSize : 12;
            // WPF 文本以左上为基准;SVG text 以基线,y 近似下移 0.8em
            string tf = $" transform=\"matrix({F(m.M11)} {F(m.M12)} {F(m.M21)} {F(m.M22)} {F(m.OffsetX)} {F(m.OffsetY)})\"";
            string fill = tb.Foreground is SolidColorBrush scb ? $" fill=\"{Hex(scb.Color)}\" fill-opacity=\"{F(scb.Color.A / 255.0 * tb.Opacity)}\"" : " fill=\"#000\"";
            string style = tb.FontStyle == FontStyles.Italic ? " font-style=\"italic\"" : "";
            string weight = tb.FontWeight.ToOpenTypeWeight() >= 600 ? " font-weight=\"600\"" : "";
            // 监控卡片数值文本(Tag=mc-value):带上精确 key "设备名|指标" + 单元格可用宽度 data-w,
            // 云端据此绑定实时值,并在数字变宽时按框宽自动缩字号(大小跟随监控框)。
            string dataVal = "";
            if (cardName != null && tb.Tag is string t && t == "mc-value" && tb.DataContext is MonitorParameter mp
                && !string.IsNullOrEmpty(mp.Name))
            {
                dataVal = $" data-val=\"{Esc(cardName + "|" + mp.Name)}\"";
                double cellW = FindCellWidth(tb);
                if (cellW > 1) dataVal += $" data-w=\"{F(cellW)}\"";
            }
            // 卡片内所有文本(名字/数值)带 data-cardname,重排时随卡片一起右移
            string card = cardName != null ? $" data-cardname=\"{Esc(cardName)}\"" : "";
            body.Append("<text x=\"0\" y=\"").Append(F(fs * 0.82)).Append("\" font-size=\"").Append(F(fs)).Append('"')
                .Append(" font-family=\"").Append(Esc(tb.FontFamily?.Source ?? "sans-serif")).Append('"')
                .Append(fill).Append(style).Append(weight).Append(dataVal).Append(card).Append(tf).Append('>').Append(Esc(tb.Text)).Append("</text>");
        }

        /// <summary>数值文本所在单元格的可用宽度(最近的 Border 内宽);供云端按框宽缩字号。</summary>
        private static double FindCellWidth(DependencyObject el)
        {
            var p = VisualTreeHelper.GetParent(el);
            while (p != null)
            {
                if (p is Border b && b.ActualWidth > 0)
                    return b.ActualWidth - (b.Padding.Left + b.Padding.Right);
                p = VisualTreeHelper.GetParent(p);
            }
            return 0;
        }

        private static void EmitImage(Image img, Visual root, StringBuilder body)
        {
            // 把图片编码成 base64 data-URI 内联进 SVG,云端 <image> 直接显示(不再是灰色占位)。
            double w = img.ActualWidth > 0 ? img.ActualWidth : img.Width;
            double h = img.ActualHeight > 0 ? img.ActualHeight : img.Height;
            if (w <= 0 || h <= 0) return;
            string tf = MatrixAttr(MatrixToRoot(img, root));
            string? dataUri = TryEncodeImage(img, w, h);
            if (dataUri != null)
            {
                body.Append("<image x=\"0\" y=\"0\" width=\"").Append(F(w)).Append("\" height=\"").Append(F(h))
                    .Append("\" preserveAspectRatio=\"none\" href=\"").Append(dataUri).Append('"')
                    .Append(tf).Append("/>");
            }
            else
            {
                // 编码失败兜底:灰色占位,至少保留位置与尺寸
                body.Append("<rect x=\"0\" y=\"0\" width=\"").Append(F(w)).Append("\" height=\"")
                    .Append(F(h)).Append("\" fill=\"#eee\"").Append(tf).Append("/>");
            }
        }

        /// <summary>把 Image 的内容编码成 PNG base64 data-URI。位图源直接编码;其它源(矢量/DrawingImage)渲染成位图。</summary>
        private static string? TryEncodeImage(Image img, double w, double h)
        {
            try
            {
                BitmapSource? bmp = img.Source as BitmapSource;
                if (bmp == null)
                {
                    if (img.Source == null) return null;
                    // 非位图源:按当前布局渲染成位图(不改动 img 的布局)
                    int pw = Math.Max(1, (int)Math.Ceiling(w));
                    int ph = Math.Max(1, (int)Math.Ceiling(h));
                    var rtb = new RenderTargetBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(img);
                    bmp = rtb;
                }
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
            }
            catch
            {
                return null;
            }
        }

        // ── 画刷 → SVG(纯色 / 线性 / 径向渐变)──
        private static string BrushToSvg(Brush? brush, string attr, StringBuilder defs, ref int gid, double elemOpacity)
        {
            if (brush == null) return attr == "fill" ? " fill=\"none\"" : "";
            if (brush is SolidColorBrush s)
            {
                double op = (s.Color.A / 255.0) * s.Opacity * elemOpacity;
                return $" {attr}=\"{Hex(s.Color)}\" {attr}-opacity=\"{F(op)}\"";
            }
            if (brush is LinearGradientBrush lg)
            {
                string id = "g" + (gid++);
                defs.Append("<linearGradient id=\"").Append(id).Append("\" gradientUnits=\"objectBoundingBox\" x1=\"")
                    .Append(F(lg.StartPoint.X)).Append("\" y1=\"").Append(F(lg.StartPoint.Y))
                    .Append("\" x2=\"").Append(F(lg.EndPoint.X)).Append("\" y2=\"").Append(F(lg.EndPoint.Y)).Append("\">");
                AppendStops(lg.GradientStops, defs);
                defs.Append("</linearGradient>");
                return $" {attr}=\"url(#{id})\" {attr}-opacity=\"{F(brush.Opacity * elemOpacity)}\"";
            }
            if (brush is RadialGradientBrush rg)
            {
                string id = "g" + (gid++);
                defs.Append("<radialGradient id=\"").Append(id).Append("\" gradientUnits=\"objectBoundingBox\" cx=\"")
                    .Append(F(rg.Center.X)).Append("\" cy=\"").Append(F(rg.Center.Y))
                    .Append("\" r=\"").Append(F(Math.Max(rg.RadiusX, rg.RadiusY))).Append("\">");
                AppendStops(rg.GradientStops, defs);
                defs.Append("</radialGradient>");
                return $" {attr}=\"url(#{id})\" {attr}-opacity=\"{F(brush.Opacity * elemOpacity)}\"";
            }
            return attr == "fill" ? " fill=\"#ccc\"" : "";
        }

        private static void AppendStops(GradientStopCollection stops, StringBuilder defs)
        {
            foreach (var gs in stops)
                defs.Append("<stop offset=\"").Append(F(gs.Offset)).Append("\" stop-color=\"").Append(Hex(gs.Color))
                    .Append("\" stop-opacity=\"").Append(F(gs.Color.A / 255.0)).Append("\"/>");
        }

        // ── Geometry → SVG path(展平为折线,兼容任意曲线/圆弧,稳)──
        private static string GeometryToPath(Geometry geo)
        {
            var sb = new StringBuilder();
            PathGeometry pg = geo.GetFlattenedPathGeometry(0.15, ToleranceType.Absolute);
            foreach (var fig in pg.Figures)
            {
                sb.Append('M').Append(F(fig.StartPoint.X)).Append(' ').Append(F(fig.StartPoint.Y));
                foreach (var seg in fig.Segments)
                    if (seg is PolyLineSegment poly)
                        foreach (var pt in poly.Points) sb.Append('L').Append(F(pt.X)).Append(' ').Append(F(pt.Y));
                    else if (seg is LineSegment ls) sb.Append('L').Append(F(ls.Point.X)).Append(' ').Append(F(ls.Point.Y));
                if (fig.IsClosed) sb.Append('Z');
            }
            return sb.ToString();
        }

        // ── 辅助 ──
        private static string DashAttr(DoubleCollection? d)
        {
            if (d == null || d.Count == 0) return "";
            var sb = new StringBuilder(" stroke-dasharray=\"");
            for (int i = 0; i < d.Count; i++) { if (i > 0) sb.Append(','); sb.Append(F(d[i])); }
            return sb.Append('"').ToString();
        }

        private static string Points(PointCollection pts)
        {
            var sb = new StringBuilder();
            foreach (var p in pts) sb.Append(F(p.X)).Append(',').Append(F(p.Y)).Append(' ');
            return sb.ToString().Trim();
        }

        private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        private static string F(double v) => Math.Round(v, 3).ToString(Inv);
        private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        /// <summary>
        /// 尽力从设备控件解析设备Id(供 data-device-id)。优先 Tag,其次 DataContext 上的 DeviceId/Id 属性。
        /// 如你的设备控件用别的方式挂 Id,把这段改成对应取法即可。
        /// </summary>
        public static string? TryResolveDeviceId(FrameworkElement fe)
        {
            // 画布上每个设备被放进一个宿主 Border,Tag=设备Id(见 ProcessDesignerView.CreateDeviceControl)。
            // 这才是设备Id的真正落点,也是分组的最佳锚点。
            if (fe is Border host && host.Tag is string hid && !string.IsNullOrEmpty(hid))
                return hid;

            // 兜底:设备可视化控件本身(命名约定:*Control,且在 Designer 的设备控件命名空间)
            var t = fe.GetType();
            if (!t.Name.EndsWith("Control")) return null;
            if (t.Namespace == null || !t.Namespace.Contains("Views.Controls")) return null;
            if (t.Name.Contains("Pipe") || t.Name.Contains("Node") || t.Name.Contains("Toolbox")) return null;

            if (fe.Tag is string tag && !string.IsNullOrEmpty(tag)) return tag;
            object? dc = fe.DataContext;
            foreach (var name in new[] { "DeviceId", "Id" })
            {
                var p = dc?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                var v = p?.GetValue(dc)?.ToString();
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return null;
        }
    }
}
