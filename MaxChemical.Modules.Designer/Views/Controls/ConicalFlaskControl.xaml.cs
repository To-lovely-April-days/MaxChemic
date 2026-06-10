using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using MaxChemical.Modules.Designer.Service;

namespace MaxChemical.Modules.Designer.Views.Controls
{
    public partial class ConicalFlaskControl : UserControl, INotifyPropertyChanged, IPipeSnapPoints
    {
        #region 事件
        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler<FlaskDragEventArgs> FlaskDragStarted;
        public event EventHandler<FlaskDragEventArgs> FlaskDragging;
        public event EventHandler<FlaskDragEventArgs> FlaskDragCompleted;
        public event EventHandler<FlaskResizeEventArgs> FlaskResizeStarted;
        public event EventHandler<FlaskResizeEventArgs> FlaskResizing;
        public event EventHandler<FlaskResizeEventArgs> FlaskResizeCompleted;
        public event EventHandler<string> FlaskSelected;
        public event EventHandler<string> FlaskDeselected;
        #endregion

        #region 设计稿C精确比例常量
        // 从设计稿C提取: bodyBW=112, neckW=44, bodyH=137, 总宽120, 总高~200
        private const double BodyBottomWidthRatio = 0.933;  // 112/120
        private const double NeckToBodyRatio = 0.393;       // 44/112
        private const double BottomMarginRatio = 0.025;     // 5/200

        // 高度分配: bodyH=137, neckH=24, rimH=4, total=165
        private const double BodyHRatio = 0.830;   // 137/165
        private const double NeckHRatio = 0.145;    // 24/165
        private const double RimHRatio = 0.024;     // 4/165

        // 暗边
        private const double LeftShadowBotW = 0.107;   // 12/112
        private const double LeftShadowTopW = 0.182;    // 8/44
        private const double RightShadowBotW = 0.089;   // 10/112
        private const double RightShadowTopW = 0.136;    // 6/44

        // 高光
        private const double HlBotW = 0.080;    // 9/112
        private const double HlTopW = 0.091;    // 4/44
        private const double HlOffset = 0.018;  // 2/112

        // 底弧
        private const double BaseArcRyR = 0.0365;  // 5/137

        // 液面
        private const double SurfRyR = 0.0328;  // 4.5/137

        // 内壁偏移
        private const double IWOffset = 0.027;  // 3/112

        // 瓶颈暗边/高光
        private const double NkShadowW = 0.114;  // 5/44
        private const double NkHlW = 0.102;      // 4.5/44

        // 液体暗边
        private const double LiqEdgeBotL = 0.089;  // 10/112
        private const double LiqEdgeBotR = 0.071;  // 8/112

        // 瓶口pad
        private const double RimPadR = 0.036;  // 4/112

        // 尺寸限制
        private const double MinW = 40, MaxW = 150, MinH = 60, MaxH = 200;
        #endregion

        #region 字段
        public string FlaskId { get; set; }
        private bool _isDragging, _isResizing;
        private FlaskAnchorPosition? _resizingAnchor;
        private Point _dragStartPt, _origPos;
        private Size _origSize;
        private double _flaskW = 80, _flaskH = 120;
        private double _liquidLevel;
        private Color _liquidColor = Color.FromArgb(255, 93, 173, 226);
        private string _liquidName = "";
        private bool _waveEnabled = true, _waveRunning;
        private Storyboard _waveSb;
        private G _g;
        #endregion

        #region 几何
        private struct G
        {
            public double W, H, Cx;
            public double BBW, BBL, BBR;   // body bottom width/left/right
            public double NW, NL, NR;       // neck width/left/right
            public double RimY, RimH;
            public double NY, NH;           // neck y, height
            public double BT, BB, BH;      // body top/bottom/height
            public double ArcRy, SurfRy;
        }

        private G CalcG()
        {
            var g = new G { W = _flaskW, H = _flaskH, Cx = _flaskW / 2 };
            g.BBW = g.W * BodyBottomWidthRatio;
            g.BBL = (g.W - g.BBW) / 2;
            g.BBR = g.BBL + g.BBW;
            g.NW = g.BBW * NeckToBodyRatio;
            g.NL = (g.W - g.NW) / 2;
            g.NR = g.NL + g.NW;
            g.BB = g.H - g.H * BottomMarginRatio;
            double usable = g.BB;
            g.BH = usable * BodyHRatio;
            g.NH = usable * NeckHRatio;
            g.RimH = Math.Max(2, usable * RimHRatio);
            g.RimY = usable - g.BH - g.NH - g.RimH;
            g.NY = g.RimY + g.RimH;
            g.BT = g.NY + g.NH;
            g.ArcRy = Math.Max(2, g.BH * BaseArcRyR);
            g.SurfRy = Math.Max(2, g.BH * SurfRyR);
            return g;
        }

        private void BodyWidthAt(double pct, out double lx, out double w)
        {
            w = _g.NW + (_g.BBW - _g.NW) * pct;
            lx = (_g.W - w) / 2;
        }
        #endregion

        #region 属性
        public double FlaskWidth { get => _flaskW; set { if (Math.Abs(_flaskW - value) > 0.01) { _flaskW = Cl(value, MinW, MaxW); Redraw(); Fire(nameof(FlaskWidth)); } } }
        public double FlaskHeight { get => _flaskH; set { if (Math.Abs(_flaskH - value) > 0.01) { _flaskH = Cl(value, MinH, MaxH); Redraw(); Fire(nameof(FlaskHeight)); } } }
        public double LiquidLevel { get => _liquidLevel; set { var v = Cl(value, 0, 100); if (Math.Abs(_liquidLevel - v) > 0.01) { _liquidLevel = v; DrawLiquid(); Fire(nameof(LiquidLevel)); } } }
        public Color LiquidColor { get => _liquidColor; set { if (_liquidColor != value) { _liquidColor = value; DrawLiquid(); Fire(nameof(LiquidColor)); } } }
        public string LiquidName { get => _liquidName; set { if (_liquidName != value) { _liquidName = value ?? ""; DrawLabel(); Fire(nameof(LiquidName)); } } }
        public bool IsWaveAnimationEnabled { get => _waveEnabled; set { if (_waveEnabled != value) { _waveEnabled = value; if (value && _liquidLevel > 0) WaveOn(); else WaveOff(); Fire(nameof(IsWaveAnimationEnabled)); } } }

        public static readonly DependencyProperty IsSelectedProperty =
            DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(ConicalFlaskControl),
                new PropertyMetadata(false, (d, e) => ((ConicalFlaskControl)d).SelVis((bool)e.NewValue)));
        public bool IsSelected { get => (bool)GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }

        public static readonly DependencyProperty LiquidLevelAnimatedProperty =
            DependencyProperty.Register("LiquidLevelAnimated", typeof(double), typeof(ConicalFlaskControl),
                new PropertyMetadata(0.0, (d, e) => { var c = (ConicalFlaskControl)d; c._liquidLevel = (double)e.NewValue; c.DrawLiquid(); }));
        #endregion

        #region 构造
        public ConicalFlaskControl()
        {
            InitializeComponent();
            FlaskId = Guid.NewGuid().ToString();
            DataContext = this;
            if (!DesignerProperties.GetIsInDesignMode(this))
            {
                MouseEnter += (s, e) => { if (!_isResizing && !_isDragging) ShowA(); };
                MouseLeave += (s, e) => { if (!_isResizing && !IsSelected) HideA(); };
                MouseDown += MD; MouseMove += MM; MouseUp += MU; MouseLeave += ML;
                SetupA();
            }
            Loaded += (_, __) => { _waveSb = Resources["WaveAnimation"] as Storyboard; Redraw(); };
        }
        #endregion

        #region Redraw
        private void Redraw()
        {
            Width = _flaskW; Height = _flaskH;
            _g = CalcG();
            DrawBody(); DrawNeck(); DrawOutlines(); DrawScales(); DrawLiquid(); DrawLabel(); LayoutSel();
        }
        #endregion

        #region 1. 瓶身
        private void DrawBody()
        {
            var br = MakeLR();
            br.GradientStops.Add(new GradientStop(Color.FromArgb(128, 176, 168, 162), 0));
            br.GradientStops.Add(new GradientStop(Color.FromArgb(77, 208, 202, 198), 0.12));
            br.GradientStops.Add(new GradientStop(Color.FromArgb(38, 230, 226, 222), 0.30));
            br.GradientStops.Add(new GradientStop(Color.FromArgb(15, 248, 246, 244), 0.50));
            br.GradientStops.Add(new GradientStop(Color.FromArgb(38, 230, 226, 222), 0.70));
            br.GradientStops.Add(new GradientStop(Color.FromArgb(77, 208, 202, 198), 0.88));
            br.GradientStops.Add(new GradientStop(Color.FromArgb(128, 176, 168, 162), 1));
            br.Freeze();
            GlassBody.Data = Quad(_g.NL, _g.BT, _g.BBL, _g.BB, _g.BBR, _g.BB, _g.NR, _g.BT);
            GlassBody.Fill = br;

            double lsbw = _g.BBW * LeftShadowBotW, lstw = _g.NW * LeftShadowTopW;
            LeftEdgeShadow.Data = Quad(_g.NL, _g.BT, _g.BBL, _g.BB, _g.BBL + lsbw, _g.BB, _g.NL + lstw, _g.BT);
            double rsbw = _g.BBW * RightShadowBotW, rstw = _g.NW * RightShadowTopW;
            RightEdgeShadow.Data = Quad(_g.NR, _g.BT, _g.BBR, _g.BB, _g.BBR - rsbw, _g.BB, _g.NR - rstw, _g.BT);

            double ho = _g.BBW * HlOffset, hbw = _g.BBW * HlBotW, htw = _g.NW * HlTopW;
            double htl = _g.NL + ho, hbl = _g.BBL + ho + _g.BBW * 0.033;
            LeftHighlight.Data = Quad(htl, _g.BT + 2, hbl, _g.BB - 4, hbl + hbw, _g.BB - 4, htl + htw, _g.BT + 2);

            SecondaryHighlight.X1 = _g.BBL + _g.BBW * 0.32; SecondaryHighlight.Y1 = _g.BT + _g.BH * 0.045;
            SecondaryHighlight.X2 = _g.BBL + _g.BBW * 0.148; SecondaryHighlight.Y2 = _g.BT + _g.BH * 0.939;
        }
        #endregion

        #region 2. 瓶颈+瓶口
        private void DrawNeck()
        {
            var nkBr = MakeLR();
            nkBr.GradientStops.Add(new GradientStop(Color.FromArgb(115, 160, 148, 136), 0));
            nkBr.GradientStops.Add(new GradientStop(Color.FromArgb(56, 200, 192, 186), 0.30));
            nkBr.GradientStops.Add(new GradientStop(Color.FromArgb(26, 228, 224, 220), 0.50));
            nkBr.GradientStops.Add(new GradientStop(Color.FromArgb(56, 200, 192, 186), 0.70));
            nkBr.GradientStops.Add(new GradientStop(Color.FromArgb(115, 160, 148, 136), 1));
            nkBr.Freeze();
            SR(FlaskNeck, _g.NL, _g.NY, _g.NW, _g.NH); FlaskNeck.Fill = nkBr; FlaskNeck.Stroke = null;

            double nsw = _g.NW * NkShadowW;
            SR(NeckLeftShadow, _g.NL, _g.NY, nsw, _g.NH);
            SR(NeckRightShadow, _g.NR - nsw, _g.NY, nsw, _g.NH);
            SR(NeckHighlight, _g.NL + _g.NW * 0.045, _g.NY + 1, _g.NW * NkHlW, _g.NH - 2);

            // 4px rim
            var rmBr = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
            rmBr.GradientStops.Add(new GradientStop(Color.FromArgb(140, 224, 218, 212), 0));
            rmBr.GradientStops.Add(new GradientStop(Color.FromArgb(77, 168, 160, 152), 1));
            rmBr.Freeze();
            double rp = _g.BBW * RimPadR;
            SR(RimCap, _g.NL - rp, _g.RimY, _g.NW + rp * 2, _g.RimH);
            RimCap.Fill = rmBr;
            RimCap.Stroke = new SolidColorBrush(Color.FromArgb(128, 138, 154, 160));
            RimCap.StrokeThickness = 0.5;
            double rimW = _g.NW + rp * 2;
            SR(RimCapHighlight, _g.NL - rp + 3, _g.RimY + 0.5, rimW * 0.35, Math.Max(1, _g.RimH - 1));

            JointHatchContainer.Children.Clear(); // 无磨口纹理
        }
        #endregion

        #region 3. 轮廓线
        private void DrawOutlines()
        {
            OutlineLeft.X1 = _g.NL; OutlineLeft.Y1 = _g.BT; OutlineLeft.X2 = _g.BBL; OutlineLeft.Y2 = _g.BB;
            OutlineRight.X1 = _g.NR; OutlineRight.Y1 = _g.BT; OutlineRight.X2 = _g.BBR; OutlineRight.Y2 = _g.BB;
            double iw = _g.BBW * IWOffset;
            InnerWallLeft.X1 = _g.NL + iw; InnerWallLeft.Y1 = _g.BT + 1; InnerWallLeft.X2 = _g.BBL + iw; InnerWallLeft.Y2 = _g.BB - 1;
            InnerWallRight.X1 = _g.NR - iw; InnerWallRight.Y1 = _g.BT + 1; InnerWallRight.X2 = _g.BBR - iw; InnerWallRight.Y2 = _g.BB - 1;
        }
        #endregion

        #region 4. 刻度
        private void DrawScales()
        {
            double[] mp = { 0.811, 0.583, 0.409, 0.227 };
            Line[] ml = { MajorTick1, MajorTick2, MajorTick3, MajorTick4 };
            TextBlock[] mt = { ScaleText1, ScaleText2, ScaleText3, ScaleText4 };
            string[] ms = { "25", "50", "75", "100" };
            double tl = _g.BBW * 0.107;
            for (int i = 0; i < 4; i++)
            {
                double y = _g.BT + _g.BH * mp[i]; BodyWidthAt(mp[i], out double lx, out _);
                ml[i].X1 = lx; ml[i].Y1 = y; ml[i].X2 = lx + tl; ml[i].Y2 = y;
                mt[i].Text = ms[i]; Canvas.SetLeft(mt[i], lx - 16); Canvas.SetTop(mt[i], y - 4);
            }
            double[] np = { 0.697, 0.500, 0.318, 0.136 };
            Line[] nl = { MinorTick1, MinorTick2, MinorTick3, MinorTick4 };
            double ntl = tl * 0.62;
            for (int i = 0; i < 4; i++)
            {
                double y = _g.BT + _g.BH * np[i]; BodyWidthAt(np[i], out double lx, out _);
                nl[i].X1 = lx + 2; nl[i].Y1 = y; nl[i].X2 = lx + 2 + ntl; nl[i].Y2 = y;
            }
            BodyWidthAt(0.811, out double ulx, out _);
            Canvas.SetLeft(UnitLabel, ulx - 16); Canvas.SetTop(UnitLabel, _g.BT + _g.BH * 0.811 + 7);
        }
        #endregion

        #region 5. 液体+底弧
        private LinearGradientBrush BrLiq()
        {
            var c = _liquidColor; var dk = Dk(c, 0.3); var lt = Lt(c, 0.2);
            var b = MakeLR();
            b.GradientStops.Add(new GradientStop(Ca(dk, 115), 0));
            b.GradientStops.Add(new GradientStop(Ca(c, 77), 0.18));
            b.GradientStops.Add(new GradientStop(Ca(lt, 56), 0.42));
            b.GradientStops.Add(new GradientStop(Ca(lt, 51), 0.58));
            b.GradientStops.Add(new GradientStop(Ca(c, 77), 0.82));
            b.GradientStops.Add(new GradientStop(Ca(dk, 115), 1));
            b.Freeze(); return b;
        }
        private LinearGradientBrush BrSurf()
        {
            var c = _liquidColor; var dk = Dk(c, 0.15); var lt = Lt(c, 0.3);
            var b = MakeLR();
            b.GradientStops.Add(new GradientStop(Ca(dk, 89), 0));
            b.GradientStops.Add(new GradientStop(Ca(lt, 51), 0.30));
            b.GradientStops.Add(new GradientStop(Ca(lt, 36), 0.50));
            b.GradientStops.Add(new GradientStop(Ca(lt, 51), 0.70));
            b.GradientStops.Add(new GradientStop(Ca(dk, 89), 1));
            b.Freeze(); return b;
        }
        private LinearGradientBrush BrArc()
        {
            var c = _liquidColor; var dk = Dk(c, 0.08); var md = Dk(c, 0.03);
            var b = MakeLR();
            b.GradientStops.Add(new GradientStop(Ca(dk, 100), 0));
            b.GradientStops.Add(new GradientStop(Ca(md, 77), 0.25));
            b.GradientStops.Add(new GradientStop(Ca(c, 64), 0.50));
            b.GradientStops.Add(new GradientStop(Ca(md, 77), 0.75));
            b.GradientStops.Add(new GradientStop(Ca(dk, 100), 1));
            b.Freeze(); return b;
        }

        private void DrawBottomArc()
        {
            double rx = _g.BBW / 2, ry = _g.ArcRy;
            var geo = new PathGeometry();
            var fig = new PathFigure { StartPoint = new Point(_g.BBL, _g.BB) };
            fig.Segments.Add(new ArcSegment(new Point(_g.BBR, _g.BB), new Size(rx, ry), 0, false, SweepDirection.Counterclockwise, true));
            fig.IsClosed = true; geo.Figures.Add(fig);
            BottomArcFill.Data = geo; BottomArcStroke.Data = geo;

            if (_liquidLevel > 0)
            {
                BottomArcFill.Fill = BrArc();
                BottomArcStroke.Stroke = new SolidColorBrush(Color.FromArgb(77, 16, 104, 120));
                BottomArcStroke.Opacity = 0.3;
            }
            else
            {
                var eb = MakeLR();
                eb.GradientStops.Add(new GradientStop(Color.FromArgb(31, 144, 136, 128), 0));
                eb.GradientStops.Add(new GradientStop(Color.FromArgb(15, 168, 160, 152), 0.5));
                eb.GradientStops.Add(new GradientStop(Color.FromArgb(31, 144, 136, 128), 1));
                eb.Freeze();
                BottomArcFill.Fill = eb;
                BottomArcStroke.Stroke = new SolidColorBrush(Color.FromArgb(77, 90, 106, 116));
                BottomArcStroke.Opacity = 0.3;
            }
        }

        private void DrawLiquid()
        {
            DrawBottomArc();
            if (_liquidLevel <= 0)
            {
                LiquidFill.Visibility = LiquidLeftEdge.Visibility = LiquidRightEdge.Visibility = Visibility.Collapsed;
                LiquidSurface.Visibility = SurfaceHighlight.Visibility = MeniscusWave.Visibility = Visibility.Collapsed;
                WaveOff(); DrawLabel(); return;
            }
            LiquidFill.Visibility = LiquidLeftEdge.Visibility = LiquidRightEdge.Visibility = Visibility.Visible;
            LiquidSurface.Visibility = SurfaceHighlight.Visibility = MeniscusWave.Visibility = Visibility.Visible;

            double pct = 1.0 - (_liquidLevel / 100.0);
            double ly = _g.BT + _g.BH * pct;
            BodyWidthAt(pct, out double llx, out double lw);
            double lrx = llx + lw;

            LiquidFill.Data = Quad(llx, ly, _g.BBL + 1, _g.BB, _g.BBR - 1, _g.BB, lrx, ly);
            LiquidFill.Fill = BrLiq();

            double lebw = _g.BBW * LiqEdgeBotL, letw = lw * 0.07;
            LiquidLeftEdge.Data = Quad(llx, ly, _g.BBL + 1, _g.BB, _g.BBL + 1 + lebw, _g.BB, llx + letw, ly);
            double rebw = _g.BBW * LiqEdgeBotR, retw = lw * 0.05;
            LiquidRightEdge.Data = Quad(lrx, ly, _g.BBR - 1, _g.BB, _g.BBR - 1 - rebw, _g.BB, lrx - retw, ly);

            double sry = _g.SurfRy;
            Canvas.SetLeft(LiquidSurface, llx); Canvas.SetTop(LiquidSurface, ly - sry);
            LiquidSurface.Width = lw; LiquidSurface.Height = sry * 2;
            LiquidSurface.Fill = BrSurf();
            var sc = Dk(_liquidColor, 0.1);
            LiquidSurface.Stroke = new SolidColorBrush(Color.FromArgb(77, sc.R, sc.G, sc.B));
            LiquidSurface.StrokeThickness = 0.4;

            double hlRx = lw * 0.5 * 0.35, hlRy = sry * 0.45;
            Canvas.SetLeft(SurfaceHighlight, llx + lw * 0.18); Canvas.SetTop(SurfaceHighlight, ly - hlRy);
            SurfaceHighlight.Width = hlRx * 2; SurfaceHighlight.Height = hlRy * 2;

            Canvas.SetLeft(MeniscusWave, llx + 3); Canvas.SetTop(MeniscusWave, ly - sry * 0.6);
            MeniscusWave.Width = lw - 6; MeniscusWave.Height = sry * 1.2;
            MeniscusWave.Fill = new SolidColorBrush(Lt(_liquidColor, 0.4));

            if (_waveEnabled) WaveOn();
            DrawLabel();
        }
        #endregion

        #region 6. 标注
        private void DrawLabel()
        {
            if (string.IsNullOrWhiteSpace(_liquidName) || _liquidLevel <= 0)
            { LabelLine.Visibility = LabelText.Visibility = Visibility.Collapsed; return; }
            LabelLine.Visibility = LabelText.Visibility = Visibility.Visible;
            double pct = 1.0 - (_liquidLevel / 100.0);
            double ly = _g.BT + _g.BH * pct;
            BodyWidthAt(pct, out double llx, out double lw);
            double midY = (ly + _g.BB) / 2, ptY = ly + (midY - ly) * 0.4;
            double sx = llx + lw * 0.65, ex = _g.W + 8;
            LabelLine.X1 = sx; LabelLine.Y1 = ptY; LabelLine.X2 = ex; LabelLine.Y2 = ptY;
            LabelText.Text = _liquidName;
            Canvas.SetLeft(LabelText, ex + 3); Canvas.SetTop(LabelText, ptY - 5);
        }
        #endregion

        #region 7. 波动
        private void WaveOn() { if (_waveSb != null && !_waveRunning && _waveEnabled) { _waveSb.Begin(this, true); _waveRunning = true; } }
        private void WaveOff() { if (_waveSb != null && _waveRunning) { _waveSb.Stop(this); _waveRunning = false; } }
        #endregion

        #region 8. 选中&锚点
        private void LayoutSel()
        {
            SR2(SelectionBorder, -2, -2, _g.W + 4, _g.H + 4);
            SE(TopAnchor, _g.W / 2 - 5, -5); SE(BottomAnchor, _g.W / 2 - 5, _g.H - 5);
            SE(LeftAnchor, -5, _g.H / 2 - 5); SE(RightAnchor, _g.W - 5, _g.H / 2 - 5);
        }
        private void SelVis(bool s)
        {
            // ★ 旧的 SelectionBorder 由统一系统接管
            SelectionBorder.Visibility = Visibility.Collapsed;
            if (s) ShowA(); else if (!IsMouseOver && !_isResizing) HideA();
        }
        private void ShowA() { TopAnchor.Opacity = BottomAnchor.Opacity = LeftAnchor.Opacity = RightAnchor.Opacity = 0.6; }
        private void HideA() { TopAnchor.Opacity = BottomAnchor.Opacity = LeftAnchor.Opacity = RightAnchor.Opacity = 0; }
        #endregion

        #region 9. 鼠标交互
        private void SetupA()
        {
            foreach (var (a, p) in new (Ellipse, FlaskAnchorPosition)[]
            { (TopAnchor, FlaskAnchorPosition.Top), (BottomAnchor, FlaskAnchorPosition.Bottom),
              (LeftAnchor, FlaskAnchorPosition.Left), (RightAnchor, FlaskAnchorPosition.Right) })
            {
                a.MouseEnter += (s, _) => ((Ellipse)s).Opacity = 1;
                a.MouseLeave += (s, _) => { if (!_isResizing) ((Ellipse)s).Opacity = 0.6; };
                var cp = p; a.MouseDown += (s, e) => AD(cp, e);
            }
        }
        private bool IsAnc(object s) => s == TopAnchor || s == BottomAnchor || s == LeftAnchor || s == RightAnchor;

        private void MD(object s, MouseButtonEventArgs e)
        {
            if (IsAnc(e.OriginalSource)) return;
            if (e.LeftButton == MouseButtonState.Pressed && !_isResizing)
            {
                FlaskSelected?.Invoke(this, FlaskId); IsSelected = true; _isDragging = true;
                if (Parent is Canvas p) { _dragStartPt = e.GetPosition(p); _origPos = CPos(); }
                CaptureMouse(); Opacity = 0.7;
                FlaskDragStarted?.Invoke(this, new FlaskDragEventArgs { FlaskId = FlaskId, StartPosition = _origPos });
                e.Handled = true;
            }
        }
        private void MM(object s, MouseEventArgs e)
        {
            if (_isDragging && e.LeftButton == MouseButtonState.Pressed && Parent is Canvas p)
            {
                var c = e.GetPosition(p);
                double nl = Cl(_origPos.X + c.X - _dragStartPt.X, 0, p.ActualWidth - ActualWidth);
                double nt = Cl(_origPos.Y + c.Y - _dragStartPt.Y, 0, p.ActualHeight - ActualHeight);
                Canvas.SetLeft(this, nl); Canvas.SetTop(this, nt);
                FlaskDragging?.Invoke(this, new FlaskDragEventArgs { FlaskId = FlaskId, CurrentPosition = new Point(nl, nt) });
                e.Handled = true;
            }
            else if (_isResizing && _resizingAnchor.HasValue) { DR(e); e.Handled = true; }
        }
        private void MU(object s, MouseButtonEventArgs e) { if (_isDragging) ED(); else if (_isResizing) ER(); e.Handled = true; }
        private void ML(object s, MouseEventArgs e) { if (_isDragging) ED(); else if (_isResizing) ER(); }

        private void ED()
        {
            if (!_isDragging) return;
            FlaskDragCompleted?.Invoke(this, new FlaskDragEventArgs { FlaskId = FlaskId, StartPosition = _origPos, CurrentPosition = CPos() });
            Opacity = 1; _isDragging = false; ReleaseMouseCapture();
        }
        private void AD(FlaskAnchorPosition pos, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            _isResizing = true; _resizingAnchor = pos;
            if (Parent is Canvas p) { _dragStartPt = e.GetPosition(p); _origPos = CPos(); _origSize = new Size(_flaskW, _flaskH); }
            CaptureMouse();
            FlaskResizeStarted?.Invoke(this, new FlaskResizeEventArgs { FlaskId = FlaskId, AnchorPosition = pos, OriginalSize = _origSize });
            e.Handled = true;
        }
        private void DR(MouseEventArgs e)
        {
            if (!_resizingAnchor.HasValue || !(Parent is Canvas p)) return;
            var c = e.GetPosition(p); double dx = c.X - _dragStartPt.X, dy = c.Y - _dragStartPt.Y;
            switch (_resizingAnchor.Value)
            {
                case FlaskAnchorPosition.Top: var h = Cl(_origSize.Height - dy, MinH, MaxH); _flaskH = h; Canvas.SetTop(this, _origPos.Y - (h - _origSize.Height)); break;
                case FlaskAnchorPosition.Bottom: _flaskH = Cl(_origSize.Height + dy, MinH, MaxH); break;
                case FlaskAnchorPosition.Left: var w = Cl(_origSize.Width - dx, MinW, MaxW); _flaskW = w; Canvas.SetLeft(this, _origPos.X - (w - _origSize.Width)); break;
                case FlaskAnchorPosition.Right: _flaskW = Cl(_origSize.Width + dx, MinW, MaxW); break;
            }
            Redraw();
            FlaskResizing?.Invoke(this, new FlaskResizeEventArgs { FlaskId = FlaskId, AnchorPosition = _resizingAnchor.Value, CurrentSize = new Size(_flaskW, _flaskH) });
        }
        private void ER()
        {
            if (!_isResizing) return;
            FlaskResizeCompleted?.Invoke(this, new FlaskResizeEventArgs { FlaskId = FlaskId, AnchorPosition = _resizingAnchor.Value, OriginalSize = _origSize, CurrentSize = new Size(_flaskW, _flaskH) });
            _isResizing = false; _resizingAnchor = null; ReleaseMouseCapture();
        }
        #endregion

        #region IPipeSnapPoints
        public List<PipeSnapPoint> GetSnapPoints()
        {
            var pts = new List<PipeSnapPoint>();
            if (!(Parent is Canvas)) return pts;

            double fl = double.IsNaN(Canvas.GetLeft(this)) ? 0 : Canvas.GetLeft(this);
            double ft = double.IsNaN(Canvas.GetTop(this)) ? 0 : Canvas.GetTop(this);

            // ★ 唯一吸附点:液体高度的中间(液面到瓶底的中点)
            // 液面 y:_g.BT + _g.BH * (1 - _liquidLevel/100)
            // 瓶底 y:_g.BB (= _g.BT + _g.BH)
            // 中点 y = (液面 + 瓶底) / 2 = _g.BT + _g.BH * (1 - _liquidLevel/200)
            //   等价于:从瓶身顶起,占瓶身高度 (1 - _liquidLevel/200) 处

            // 液体为 0 时,默认放在瓶身腰部(50% 处),避免吸附点消失
            double pct = (_liquidLevel <= 0) ? 0.5 : (1.0 - _liquidLevel / 200.0);
            double midY = _g.BT + _g.BH * pct;

            pts.Add(new PipeSnapPoint
            {
                WorldPosition = new Point(fl + _g.Cx, ft + midY),
                Direction = SnapDirection.Up,
                Description = $"锥形瓶 (液位 {_liquidLevel:F0}%)"
            });

            return pts;
        }
        #endregion

        #region 公共方法
        public void SetLiquidLevel(double p, bool animated = true)
        {
            p = Cl(p, 0, 100);
            if (animated)
            {
                var a = new DoubleAnimation { From = _liquidLevel, To = p, Duration = TimeSpan.FromMilliseconds(600), EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut } };
                a.Completed += (_, __) => { _liquidLevel = p; Fire(nameof(LiquidLevel)); };
                BeginAnimation(LiquidLevelAnimatedProperty, a);
            }
            else LiquidLevel = p;
        }
        public void SetLiquidColor(string n) => LiquidColor = n?.ToLower() switch
        {
            "蓝色" or "blue" => Color.FromArgb(255, 93, 173, 226),
            "淡蓝色" => Color.FromArgb(255, 144, 202, 238),
            "绿色" or "green" => Color.FromArgb(255, 102, 187, 106),
            "红色" or "red" => Color.FromArgb(255, 239, 83, 80),
            "黄色" or "yellow" => Color.FromArgb(255, 255, 235, 59),
            "紫色" or "purple" => Color.FromArgb(255, 156, 39, 176),
            "橙色" or "orange" => Color.FromArgb(255, 255, 152, 0),
            "粉色" or "pink" => Color.FromArgb(255, 233, 130, 150),
            "青色" or "cyan" => Color.FromArgb(255, 0, 188, 212),
            "无色" => Color.FromArgb(80, 200, 220, 235),
            _ => Color.FromArgb(255, 93, 173, 226)
        };
        public void SetLiquidColor(byte r, byte g, byte b, byte a = 255) => LiquidColor = Color.FromArgb(a, r, g, b);
        public void SetSelected(bool s) => IsSelected = s;
        public (double level, Color color, string name) GetLiquidInfo() => (_liquidLevel, _liquidColor, _liquidName);
        public void ClearLiquid(bool animated = true) => SetLiquidLevel(0, animated);
        public void FillLiquid(bool animated = true) => SetLiquidLevel(100, animated);
        #endregion

        #region 辅助
        private static double Cl(double v, double mn, double mx) => Math.Max(mn, Math.Min(mx, v));
        private static Color Lt(Color c, double a) => Color.FromArgb(c.A, (byte)Math.Min(255, c.R + (255 - c.R) * a), (byte)Math.Min(255, c.G + (255 - c.G) * a), (byte)Math.Min(255, c.B + (255 - c.B) * a));
        private static Color Dk(Color c, double a) => Color.FromArgb(c.A, (byte)(c.R * (1 - a)), (byte)(c.G * (1 - a)), (byte)(c.B * (1 - a)));
        private static Color Ca(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
        private static LinearGradientBrush MakeLR() => new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        private static PathGeometry Quad(double x1, double y1, double x2, double y2, double x3, double y3, double x4, double y4)
        {
            var g = new PathGeometry(); var f = new PathFigure { StartPoint = new Point(x1, y1) };
            f.Segments.Add(new LineSegment(new Point(x2, y2), true));
            f.Segments.Add(new LineSegment(new Point(x3, y3), true));
            f.Segments.Add(new LineSegment(new Point(x4, y4), true));
            f.IsClosed = true; g.Figures.Add(f); return g;
        }
        private static void SR(Rectangle r, double x, double y, double w, double h) { Canvas.SetLeft(r, x); Canvas.SetTop(r, y); r.Width = Math.Max(0, w); r.Height = Math.Max(0, h); }
        private static void SR2(Rectangle r, double x, double y, double w, double h) { Canvas.SetLeft(r, x); Canvas.SetTop(r, y); r.Width = w; r.Height = h; }
        private static void SE(Ellipse e, double x, double y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); }
        private Point CPos() => new Point(double.IsNaN(Canvas.GetLeft(this)) ? 0 : Canvas.GetLeft(this), double.IsNaN(Canvas.GetTop(this)) ? 0 : Canvas.GetTop(this));
        private void Fire(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
        #endregion
    }

    public enum FlaskAnchorPosition { Top, Bottom, Left, Right }
    public class FlaskDragEventArgs : EventArgs { public string FlaskId { get; set; } public Point StartPosition { get; set; } public Point CurrentPosition { get; set; } }
    public class FlaskResizeEventArgs : EventArgs { public string FlaskId { get; set; } public FlaskAnchorPosition AnchorPosition { get; set; } public Size OriginalSize { get; set; } public Size CurrentSize { get; set; } }
}