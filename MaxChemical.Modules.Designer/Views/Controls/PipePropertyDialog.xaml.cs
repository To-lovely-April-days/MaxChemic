using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.Designer.Views.Controls.Service;
using Prism.Ioc;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace MaxChemical.Modules.Designer.Views.Controls
{
    /// <summary>
    /// 管道属性对话框 — 清爽极简风 + #C41E3A 主题色
    /// 实时预览置顶 + 选项卡切换（管道外观 / 流体设置 / 流动控制）
    /// 流体设置选项卡支持「使用默认流动样式（赛博风）」勾选
    /// </summary>
    public partial class PipePropertyDialog : UserControl
    {
        #region 主题色常量

        private static readonly Color Accent = Color.FromRgb(0xC4, 0x1E, 0x3A);
        private static readonly SolidColorBrush AccentBrush = new SolidColorBrush(Accent);
        private static readonly SolidColorBrush TransparentBrush = Brushes.Transparent;

        #endregion

        #region 事件

        public event EventHandler CloseRequested;
        public event EventHandler<bool> DialogResult;

        #endregion

        #region 字段

        private IPipeProperties _targetPipe;

        // 原始值（取消时恢复）
        private Color _originalPipeColor;
        private Color _originalFlangeColor;
        private Color _originalFluidColor;
        private double _originalFluidOpacity;
        private bool _originalIsFluidVisible;
        private bool _originalIsFlowing;
        private PipeFlowDirection _originalFlowDir;
        private double _originalFlowSpeed;
        private bool _originalUseDefaultStyle;   // ★ 新增

        // 当前选中色块
        private Border _selectedPipeColorSwatch;
        private Border _selectedFlangeColorSwatch;
        private Border _selectedFluidColorSwatch;

        // 预览元素
        private Rectangle _previewPipeBackground;
        private Rectangle _previewFluidBg;
        private Rectangle _previewFluidPattern;
        private Rectangle _previewPipeGradient;
        private Rectangle _previewHighlight;
        private Rectangle _previewLeftFlange;
        private Rectangle _previewRightFlange;
        private TextBlock _previewFlowArrow;

        // 预览动画
        private TranslateTransform _previewFlowTransform;
        private DrawingBrush _previewPatternBrush;

        private bool _isInitializing = true;

        #endregion

        #region 颜色预设

        private static readonly (string Name, Color Color)[] PipeColors = new[]
        {
            ("默认灰", Color.FromRgb(0x72, 0x71, 0x71)),
            ("深灰",   Color.FromRgb(0x4A, 0x4A, 0x54)),
            ("银色",   Color.FromRgb(0xA0, 0xA0, 0xA0)),
            ("暖灰",   Color.FromRgb(0x8A, 0x7E, 0x6E)),
            ("蓝灰",   Color.FromRgb(0x3D, 0x5A, 0x80)),
            ("锈棕",   Color.FromRgb(0x6B, 0x4C, 0x3B)),
            ("深蓝",   Color.FromRgb(0x2E, 0x40, 0x57)),
            ("墨绿",   Color.FromRgb(0x2D, 0x5A, 0x45)),
            ("铜色",   Color.FromRgb(0xB8, 0x73, 0x33)),
            ("黑色",   Color.FromRgb(0x1E, 0x1E, 0x1E)),
        };

        private static readonly (string Name, Color Color)[] FlangeColors = new[]
        {
            ("默认银", Color.FromRgb(0xD9, 0xD9, 0xD9)),
            ("浅灰",   Color.FromRgb(0xC0, 0xC0, 0xC0)),
            ("暖银",   Color.FromRgb(0xD0, 0xC8, 0xB8)),
            ("深银",   Color.FromRgb(0x9A, 0x9A, 0xA2)),
            ("白色",   Color.FromRgb(0xF0, 0xF0, 0xF0)),
            ("金色",   Color.FromRgb(0xD4, 0xAF, 0x37)),
            ("铜色",   Color.FromRgb(0xCD, 0x7F, 0x32)),
            ("黑色",   Color.FromRgb(0x3A, 0x3A, 0x3A)),
        };

        private static readonly (string Name, Color Color)[] FluidColors = new[]
        {
            ("水蓝",   Color.FromRgb(0x1E, 0xC1, 0xF4)),
            ("化学绿", Color.FromRgb(0x40, 0xD8, 0x90)),
            ("酸黄",   Color.FromRgb(0xF4, 0xC5, 0x42)),
            ("高温红", Color.FromRgb(0xFF, 0x6B, 0x6B)),
            ("橙色",   Color.FromRgb(0xFF, 0x95, 0x00)),
            ("紫色",   Color.FromRgb(0xA8, 0x55, 0xF7)),
            ("惰性灰", Color.FromRgb(0x6B, 0x72, 0x80)),
            ("深蓝",   Color.FromRgb(0x22, 0x63, 0xEB)),
            ("粉色",   Color.FromRgb(0xEC, 0x48, 0x99)),
            ("白色",   Color.FromRgb(0xE8, 0xE8, 0xE8)),
        };

        #endregion

        private readonly ILocalizationService _localization;

        public PipePropertyDialog()
        {
            InitializeComponent();
            _localization = ContainerLocator.Container.Resolve<ILocalizationService>() ?? throw new NullReferenceException();

            BuildColorPalettes();
            BuildPreviewPipe();
            UpdateLocalizationText();
        }

        #region 公共方法

        public void SetTargetPipe(IPipeProperties pipe)
        {
            _isInitializing = true;
            _targetPipe = pipe;

            _originalPipeColor = pipe.PipeColor;
            _originalFlangeColor = pipe.FlangeColor;
            _originalFluidColor = pipe.FluidColor;
            _originalFluidOpacity = pipe.FluidOpacity;
            _originalIsFluidVisible = pipe.IsFluidVisible;
            _originalIsFlowing = pipe.IsFlowing;
            _originalFlowDir = pipe.PipeFlowDir;
            _originalFlowSpeed = pipe.FlowSpeed;
            _originalUseDefaultStyle = pipe.UseDefaultStyle;   // ★

            SyncUIFromPipe();
            _isInitializing = false;
            UpdatePreview();
        }

        #endregion

        #region 选项卡切换

        private void OnTabChanged(object sender, RoutedEventArgs e)
        {
            if (PanelAppearance == null) return;

            PanelAppearance.Visibility = TabAppearance.IsChecked == true
                ? Visibility.Visible : Visibility.Collapsed;
            PanelFluid.Visibility = TabFluid.IsChecked == true
                ? Visibility.Visible : Visibility.Collapsed;
            PanelFlow.Visibility = TabFlow.IsChecked == true
                ? Visibility.Visible : Visibility.Collapsed;
        }

        #endregion

        #region 构建颜色面板

        private void BuildColorPalettes()
        {
            BuildPalette(PipeColorPalette, PipeColors, OnPipeColorSelected);
            BuildPalette(FlangeColorPalette, FlangeColors, OnFlangeColorSelected);
            BuildPalette(FluidColorPalette, FluidColors, OnFluidColorSelected);
        }

        private void BuildPalette(WrapPanel panel, (string Name, Color Color)[] colors,
                                   Action<Border, Color> onSelected)
        {
            foreach (var (name, color) in colors)
            {
                var swatch = new Border
                {
                    Width = 32,
                    Height = 32,
                    CornerRadius = new CornerRadius(8),
                    Margin = new Thickness(3),
                    Cursor = Cursors.Hand,
                    BorderThickness = new Thickness(2.5),
                    BorderBrush = TransparentBrush,
                    Background = new SolidColorBrush(color),
                    ToolTip = name,
                    Tag = color,
                    RenderTransform = new ScaleTransform(1, 1),
                    RenderTransformOrigin = new Point(0.5, 0.5)
                };

                swatch.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 3,
                    ShadowDepth = 1,
                    Opacity = 0.08
                };

                swatch.MouseDown += (s, e) =>
                {
                    if (s is Border b && b.Tag is Color c)
                    {
                        onSelected(b, c);
                        e.Handled = true;
                    }
                };

                swatch.MouseEnter += (s, e) =>
                {
                    if (s is Border b)
                    {
                        b.Effect = new System.Windows.Media.Effects.DropShadowEffect
                        {
                            BlurRadius = 8,
                            ShadowDepth = 2,
                            Opacity = 0.12
                        };
                    }
                };

                swatch.MouseLeave += (s, e) =>
                {
                    if (s is Border b)
                    {
                        b.Effect = new System.Windows.Media.Effects.DropShadowEffect
                        {
                            BlurRadius = 3,
                            ShadowDepth = 1,
                            Opacity = 0.08
                        };
                    }
                };

                panel.Children.Add(swatch);
            }
        }

        private void HighlightSwatch(WrapPanel panel, ref Border selectedRef, Border newSwatch)
        {
            if (selectedRef != null)
            {
                selectedRef.BorderBrush = TransparentBrush;
                AnimateScale(selectedRef, 1.0);
            }

            newSwatch.BorderBrush = AccentBrush;
            AnimateScale(newSwatch, 1.12);
            selectedRef = newSwatch;
        }

        private void AnimateScale(Border target, double scale)
        {
            if (target.RenderTransform is ScaleTransform st)
            {
                var anim = new DoubleAnimation(scale, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
            }
        }

        private void AutoHighlightSwatch(WrapPanel panel, ref Border selectedRef, Color currentColor)
        {
            foreach (Border child in panel.Children)
            {
                if (child.Tag is Color c && c == currentColor)
                {
                    HighlightSwatch(panel, ref selectedRef, child);
                    return;
                }
            }

            if (selectedRef != null)
            {
                selectedRef.BorderBrush = TransparentBrush;
                AnimateScale(selectedRef, 1.0);
            }
            selectedRef = null;
        }

        #endregion

        #region 色块点击处理

        private void OnPipeColorSelected(Border swatch, Color color)
        {
            HighlightSwatch(PipeColorPalette, ref _selectedPipeColorSwatch, swatch);
            if (_targetPipe != null) _targetPipe.PipeColor = color;
            UpdatePreview();
        }

        private void OnFlangeColorSelected(Border swatch, Color color)
        {
            HighlightSwatch(FlangeColorPalette, ref _selectedFlangeColorSwatch, swatch);
            if (_targetPipe != null) _targetPipe.FlangeColor = color;
            UpdatePreview();
        }

        private void OnFluidColorSelected(Border swatch, Color color)
        {
            HighlightSwatch(FluidColorPalette, ref _selectedFluidColorSwatch, swatch);
            if (_targetPipe != null) _targetPipe.FluidColor = color;
            UpdatePreview();
        }

        #endregion

        #region UI 事件处理

        /// <summary>
        /// ★ 默认流动样式勾选切换
        /// </summary>
        private void OnUseDefaultStyleChanged(object sender, RoutedEventArgs e)
        {
            if (_isInitializing || _targetPipe == null) return;

            bool useDefault = ChkUseDefaultStyle.IsChecked == true;
            _targetPipe.UseDefaultStyle = useDefault;

            UpdateLockedFieldsUI(useDefault);
            UpdatePreview();
        }

        /// <summary>
        /// 根据是否使用默认样式，灰显/隐藏部分控件
        /// </summary>
        private void UpdateLockedFieldsUI(bool useDefault)
        {
            // 1. 管道外观面板：勾选默认样式时灰显（颜色被赛博样式接管）
            if (PipeColorPalette != null)
            {
                PipeColorPalette.IsEnabled = !useDefault;
                PipeColorPalette.Opacity = useDefault ? 0.4 : 1.0;
            }
            if (FlangeColorPalette != null)
            {
                FlangeColorPalette.IsEnabled = !useDefault;
                FlangeColorPalette.Opacity = useDefault ? 0.4 : 1.0;
            }
            if (AppearanceLockedHint != null)
            {
                AppearanceLockedHint.Visibility = useDefault ? Visibility.Visible : Visibility.Collapsed;
            }

            // 2. 透明度滑块：勾选默认样式时灰显（不透明度由赛博样式自带辉光层次接管）
            if (OpacityRow != null)
            {
                OpacityRow.IsEnabled = !useDefault;
                OpacityRow.Opacity = useDefault ? 0.4 : 1.0;
            }

            // 3. 流体颜色：保持可用（用户可改赛博流光颜色）
            // 4. 流动控制（方向/流速）：保持可用
        }

        private void OnFluidVisibleChanged(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            bool visible = ToggleFluidVisible.IsChecked == true;
            if (_targetPipe != null) _targetPipe.IsFluidVisible = visible;

            FluidSettingsPanel.IsEnabled = visible;
            FluidSettingsPanel.Opacity = visible ? 1.0 : 0.35;

            UpdateFlowPanelEnabled();
            UpdatePreview();
        }

        private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isInitializing) return;

            double val = Math.Round(SliderOpacity.Value, 1);
            TxtOpacity.Text = $"{(int)(val * 100)}%";
            if (_targetPipe != null) _targetPipe.FluidOpacity = val;
            UpdatePreview();
        }

        private void OnFlowingChanged(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            bool flowing = ToggleFlowing.IsChecked == true;
            if (_targetPipe != null) _targetPipe.IsFlowing = flowing;

            UpdateFlowPanelEnabled();
            UpdatePreview();
        }

        private void OnFlowDirectionChanged(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;

            var dir = RbLeftToRight.IsChecked == true
                ? PipeFlowDirection.LeftToRight
                : PipeFlowDirection.RightToLeft;

            if (_targetPipe != null) _targetPipe.PipeFlowDir = dir;
            UpdatePreview();
        }

        private void OnSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isInitializing) return;

            double val = Math.Round(SliderSpeed.Value, 1);
            TxtSpeed.Text = $"{val:F1}x";
            if (_targetPipe != null) _targetPipe.FlowSpeed = val;
            UpdatePreview();
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            RestoreOriginalValues();
            CloseRequested?.Invoke(this, EventArgs.Empty);
            DialogResult?.Invoke(this, false);
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            RestoreOriginalValues();
            CloseRequested?.Invoke(this, EventArgs.Empty);
            DialogResult?.Invoke(this, false);
        }

        private void OnApplyClick(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            DialogResult?.Invoke(this, true);
        }

        private void UpdateFlowPanelEnabled()
        {
            if (FlowControlPanel == null) return;

            bool fluidVisible = ToggleFluidVisible?.IsChecked == true;
            bool flowing = ToggleFlowing?.IsChecked == true;
            bool enabled = fluidVisible && flowing;

            FlowControlPanel.IsEnabled = enabled;
            FlowControlPanel.Opacity = enabled ? 1.0 : 0.35;
        }

        #endregion

        #region 同步 UI ↔ 管道

        private void SyncUIFromPipe()
        {
            if (_targetPipe == null) return;

            AutoHighlightSwatch(PipeColorPalette, ref _selectedPipeColorSwatch, _targetPipe.PipeColor);
            AutoHighlightSwatch(FlangeColorPalette, ref _selectedFlangeColorSwatch, _targetPipe.FlangeColor);
            AutoHighlightSwatch(FluidColorPalette, ref _selectedFluidColorSwatch, _targetPipe.FluidColor);

            ToggleFluidVisible.IsChecked = _targetPipe.IsFluidVisible;
            ToggleFlowing.IsChecked = _targetPipe.IsFlowing;

            // ★ 同步默认样式开关 + 灰显
            ChkUseDefaultStyle.IsChecked = _targetPipe.UseDefaultStyle;
            UpdateLockedFieldsUI(_targetPipe.UseDefaultStyle);

            bool fluidVisible = _targetPipe.IsFluidVisible;
            FluidSettingsPanel.IsEnabled = fluidVisible;
            FluidSettingsPanel.Opacity = fluidVisible ? 1.0 : 0.35;

            UpdateFlowPanelEnabled();

            SliderOpacity.Value = _targetPipe.FluidOpacity;
            TxtOpacity.Text = $"{(int)(_targetPipe.FluidOpacity * 100)}%";

            SliderSpeed.Value = _targetPipe.FlowSpeed;
            TxtSpeed.Text = $"{_targetPipe.FlowSpeed:F1}x";

            RbLeftToRight.IsChecked = _targetPipe.PipeFlowDir == PipeFlowDirection.LeftToRight;
            RbRightToLeft.IsChecked = _targetPipe.PipeFlowDir == PipeFlowDirection.RightToLeft;
        }

        private void RestoreOriginalValues()
        {
            if (_targetPipe == null) return;

            _targetPipe.PipeColor = _originalPipeColor;
            _targetPipe.FlangeColor = _originalFlangeColor;
            _targetPipe.FluidColor = _originalFluidColor;
            _targetPipe.FluidOpacity = _originalFluidOpacity;
            _targetPipe.IsFluidVisible = _originalIsFluidVisible;
            _targetPipe.IsFlowing = _originalIsFlowing;
            _targetPipe.PipeFlowDir = _originalFlowDir;
            _targetPipe.FlowSpeed = _originalFlowSpeed;
            _targetPipe.UseDefaultStyle = _originalUseDefaultStyle;   // ★

            Debug.WriteLine("管道属性已恢复为原始值");
        }

        #endregion

        #region 预览管道

        private void BuildPreviewPipe()
        {
            double canvasW = 440;
            double canvasH = 56;
            double pipeW = 340;
            double pipeH = 22;
            double flangeW = 8;
            double flangeH = pipeH + 14;
            double startX = (canvasW - pipeW - flangeW * 2) / 2;
            double pipeY = (canvasH - pipeH) / 2;
            double flangeY = (canvasH - flangeH) / 2;

            _previewPipeBackground = new Rectangle
            {
                Width = pipeW,
                Height = pipeH,
                Fill = Brushes.White,
                RadiusX = 1,
                RadiusY = 1
            };
            Canvas.SetLeft(_previewPipeBackground, startX + flangeW);
            Canvas.SetTop(_previewPipeBackground, pipeY);
            PreviewCanvas.Children.Add(_previewPipeBackground);

            _previewFluidBg = new Rectangle
            {
                Width = pipeW,
                Height = pipeH,
                Visibility = Visibility.Collapsed,
                Opacity = 0.5
            };
            Canvas.SetLeft(_previewFluidBg, startX + flangeW);
            Canvas.SetTop(_previewFluidBg, pipeY);
            PreviewCanvas.Children.Add(_previewFluidBg);

            _previewFluidPattern = new Rectangle
            {
                Width = pipeW,
                Height = pipeH,
                Visibility = Visibility.Collapsed
            };
            Canvas.SetLeft(_previewFluidPattern, startX + flangeW);
            Canvas.SetTop(_previewFluidPattern, pipeY);
            PreviewCanvas.Children.Add(_previewFluidPattern);

            _previewPipeGradient = new Rectangle
            {
                Width = pipeW,
                Height = pipeH,
                RadiusX = 1,
                RadiusY = 1
            };
            Canvas.SetLeft(_previewPipeGradient, startX + flangeW);
            Canvas.SetTop(_previewPipeGradient, pipeY);
            PreviewCanvas.Children.Add(_previewPipeGradient);

            _previewHighlight = new Rectangle
            {
                Width = pipeW - 20,
                Height = pipeH * 0.35,
                Fill = new LinearGradientBrush(
                    Color.FromArgb(64, 255, 255, 255),
                    Color.FromArgb(0, 255, 255, 255),
                    new Point(0, 0), new Point(0, 1)),
                IsHitTestVisible = false
            };
            Canvas.SetLeft(_previewHighlight, startX + flangeW + 10);
            Canvas.SetTop(_previewHighlight, pipeY);
            PreviewCanvas.Children.Add(_previewHighlight);

            _previewLeftFlange = new Rectangle
            {
                Width = flangeW,
                Height = flangeH,
                RadiusX = 2,
                RadiusY = 2
            };
            Canvas.SetLeft(_previewLeftFlange, startX);
            Canvas.SetTop(_previewLeftFlange, flangeY);
            PreviewCanvas.Children.Add(_previewLeftFlange);

            _previewRightFlange = new Rectangle
            {
                Width = flangeW,
                Height = flangeH,
                RadiusX = 2,
                RadiusY = 2
            };
            Canvas.SetLeft(_previewRightFlange, startX + flangeW + pipeW);
            Canvas.SetTop(_previewRightFlange, flangeY);
            PreviewCanvas.Children.Add(_previewRightFlange);

            _previewFlowArrow = new TextBlock
            {
                FontSize = 10,
                Foreground = AccentBrush,
                Opacity = 0.6,
                TextAlignment = TextAlignment.Center,
                Width = canvasW,
                Visibility = Visibility.Collapsed
            };
            Canvas.SetLeft(_previewFlowArrow, 0);
            Canvas.SetTop(_previewFlowArrow, canvasH - 14);
            PreviewCanvas.Children.Add(_previewFlowArrow);

            _previewFlowTransform = new TranslateTransform(0, 0);
        }

        private void UpdatePreview()
        {
            if (_targetPipe == null) return;

            var pipeC = _targetPipe.PipeColor;
            var flangeC = _targetPipe.FlangeColor;
            var fluidC = _targetPipe.FluidColor;

            // ★ 默认样式时,管壁用赛博金属色覆盖 PipeColor
            if (_targetPipe.UseDefaultStyle)
            {
                var metalGrad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                metalGrad.GradientStops.Add(new GradientStop(Color.FromRgb(0x1A, 0x20, 0x30), 0));
                metalGrad.GradientStops.Add(new GradientStop(Color.FromRgb(0x4A, 0x5A, 0x7A), 0.2));
                metalGrad.GradientStops.Add(new GradientStop(Color.FromRgb(0x8A, 0xA0, 0xC0), 0.5));
                metalGrad.GradientStops.Add(new GradientStop(Color.FromRgb(0x4A, 0x5A, 0x7A), 0.8));
                metalGrad.GradientStops.Add(new GradientStop(Color.FromRgb(0x1A, 0x20, 0x30), 1));
                _previewPipeGradient.Fill = metalGrad;
            }
            else
            {
                var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                grad.GradientStops.Add(new GradientStop(pipeC, 0));
                grad.GradientStops.Add(new GradientStop(Color.FromArgb(0x59, pipeC.R, pipeC.G, pipeC.B), 0.26));
                grad.GradientStops.Add(new GradientStop(Color.FromArgb(0x1A, pipeC.R, pipeC.G, pipeC.B), 0.42));
                grad.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.50));
                grad.GradientStops.Add(new GradientStop(Color.FromArgb(0x1A, pipeC.R, pipeC.G, pipeC.B), 0.58));
                grad.GradientStops.Add(new GradientStop(Color.FromArgb(0x59, pipeC.R, pipeC.G, pipeC.B), 0.72));
                grad.GradientStops.Add(new GradientStop(pipeC, 1));
                _previewPipeGradient.Fill = grad;
            }

            // 法兰渐变
            var flangeGrad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            flangeGrad.GradientStops.Add(new GradientStop(LightenColor(flangeC, 30), 0));
            flangeGrad.GradientStops.Add(new GradientStop(flangeC, 0.5));
            flangeGrad.GradientStops.Add(new GradientStop(DarkenColor(flangeC, 20), 1));

            _previewLeftFlange.Fill = flangeGrad.Clone();
            _previewLeftFlange.Stroke = new SolidColorBrush(Color.FromArgb(128, pipeC.R, pipeC.G, pipeC.B));
            _previewLeftFlange.StrokeThickness = 0.8;

            _previewRightFlange.Fill = flangeGrad;
            _previewRightFlange.Stroke = new SolidColorBrush(Color.FromArgb(128, pipeC.R, pipeC.G, pipeC.B));
            _previewRightFlange.StrokeThickness = 0.8;

            // 流体（不论默认样式与否，预览都用气泡纹理简化展示）
            bool fluidVis = _targetPipe.IsFluidVisible;
            _previewFluidBg.Visibility = fluidVis ? Visibility.Visible : Visibility.Collapsed;
            _previewFluidPattern.Visibility = fluidVis ? Visibility.Visible : Visibility.Collapsed;

            if (fluidVis)
            {
                _previewFluidBg.Fill = new SolidColorBrush(fluidC);
                _previewFluidBg.Opacity = _targetPipe.UseDefaultStyle ? 0.85 : _targetPipe.FluidOpacity;
                BuildPreviewBubblePattern(fluidC, pipeC);
            }

            bool showArrow = _targetPipe.IsFlowing && _targetPipe.IsFluidVisible;
            _previewFlowArrow.Visibility = showArrow ? Visibility.Visible : Visibility.Collapsed;
            if (showArrow)
            {
                _previewFlowArrow.Text = _targetPipe.PipeFlowDir == PipeFlowDirection.LeftToRight
                    ? string.Format("▸▸▸  {0}  ▸▸▸", _localization.GetString("PipeProperty_Direction", "流动方向"))
                    : string.Format("◂◂◂  {0}  ◂◂◂", _localization.GetString("PipeProperty_Direction", "流动方向"));
            }

            UpdatePreviewFlowAnimation();
        }

        private void BuildPreviewBubblePattern(Color fluidC, Color pipeC)
        {
            var group = new DrawingGroup();

            var bubbles = new[]
            {
                (10.0, 6.0, 4.0),  (30.0, 14.0, 3.0), (50.0, 8.0, 5.0),
                (65.0, 16.0, 3.0), (20.0, 18.0, 2.5), (45.0, 4.0, 3.0),
            };

            foreach (var (x, y, r) in bubbles)
            {
                var radial = new RadialGradientBrush
                {
                    GradientOrigin = new Point(0.3, 0.3),
                    Center = new Point(0.5, 0.5)
                };
                radial.GradientStops.Add(new GradientStop(Color.FromArgb(180, 255, 255, 255), 0));
                radial.GradientStops.Add(new GradientStop(Color.FromArgb(13, 255, 255, 255), 1));

                group.Children.Add(new GeometryDrawing(
                    radial, null,
                    new EllipseGeometry(new Point(x, y), r, r)));
            }

            _previewFlowTransform = new TranslateTransform(0, 0);

            _previewPatternBrush = new DrawingBrush(group)
            {
                TileMode = TileMode.Tile,
                Viewbox = new Rect(0, 0, 80, 22),
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 80, 22),
                ViewportUnits = BrushMappingMode.Absolute,
                Transform = _previewFlowTransform
            };

            _previewFluidPattern.Fill = _previewPatternBrush;
        }

        private void UpdatePreviewFlowAnimation()
        {
            if (_previewFlowTransform == null) return;

            _previewFlowTransform.BeginAnimation(TranslateTransform.XProperty, null);

            if (_targetPipe == null) return;

            if (_targetPipe.IsFlowing && _targetPipe.IsFluidVisible)
            {
                double tileW = 80;
                double to = _targetPipe.PipeFlowDir == PipeFlowDirection.LeftToRight ? tileW : -tileW;
                double durationMs = 1000.0 / Math.Max(0.1, _targetPipe.FlowSpeed);

                var anim = new DoubleAnimation
                {
                    From = 0,
                    To = to,
                    Duration = TimeSpan.FromMilliseconds(durationMs),
                    RepeatBehavior = RepeatBehavior.Forever
                };
                _previewFlowTransform.BeginAnimation(TranslateTransform.XProperty, anim);
            }
            else
            {
                _previewFlowTransform.X = 0;
            }
        }

        #endregion

        #region 辅助

        private static Color LightenColor(Color c, byte amount)
        {
            return Color.FromRgb(
                (byte)Math.Min(255, c.R + amount),
                (byte)Math.Min(255, c.G + amount),
                (byte)Math.Min(255, c.B + amount));
        }

        private static Color DarkenColor(Color c, byte amount)
        {
            return Color.FromRgb(
                (byte)Math.Max(0, c.R - amount),
                (byte)Math.Max(0, c.G - amount),
                (byte)Math.Max(0, c.B - amount));
        }

        #endregion

        #region 本地化

        private void UpdateLocalizationText()
        {
            TbTitleLabel.Text = _localization.GetString("PipeProperty_Title", "管道属性设置");
            TbPreviewLabel.Text = _localization.GetString("PipeProperty_Preview", "实时预览");
            TbAppearanceLabel.Text = _localization.GetString("PipeProperty_Appearance", "管道外观");
            TbSettingLabel.Text = _localization.GetString("PipeProperty_Setting", "流体设置");
            TbControlLabel.Text = _localization.GetString("PipeProperty_Control", "流体控制");
            TbBodyColorLabel.Text = _localization.GetString("PipeProperty_BodyColor", "管体颜色");
            TbFlangeColorLabel.Text = _localization.GetString("PipeProperty_FlangeColor", "法兰颜色");
            TbShowFlowLabel.Text = _localization.GetString("PipeProperty_ShowFlow", "显示流体");
            TbFlowColorLabel.Text = _localization.GetString("PipeProperty_FlowColor", "流体颜色");
            TbTransparencyLabel.Text = _localization.GetString("PipeProperty_Transparency", "透明度");
            TbFlowStatusLabel.Text = _localization.GetString("PipeProperty_Status", "流动状态");
            TbFlowDirectionLabel.Text = _localization.GetString("PipeProperty_Direction", "流动方向");
            RbLeftToRight.Content = _localization.GetString("PipeProperty_PositiveDir", "→  正向（左→右）");
            RbRightToLeft.Content = _localization.GetString("PipeProperty_NagtiveDir", "←  反向（右→左）");
            TbFlowVelocityLabel.Text = _localization.GetString("PipeProperty_Velocity", "流速");
            BtnCancel.Content = _localization.GetString("PipeProperty_Cancel", "取消");
            BtnApply.Content = _localization.GetString("PipeProperty_Confirm", "确定");
            ChkUseDefaultStyle.Content = _localization.GetString("PipeProperty_UseDefStyle", "使用默认流动样式（赛博风 · 推荐）");
            TbUseDefStyleText.Text = _localization.GetString("PipeProperty_DefStyleTxt", "金属管壁 + 流光带 + 粒子辉光的科技效果。流体颜色、方向、流速仍可调整。");
        }

        #endregion
    }
}
