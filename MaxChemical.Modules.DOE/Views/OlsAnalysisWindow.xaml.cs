using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.DOE.Models;
using MaxChemical.Modules.DOE.ViewModels;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using Prism.Ioc;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MaxChemical.Modules.DOE.Views
{
    public partial class OlsAnalysisWindow : Window
    {
        // ═══ 刻画器拖拽状态 ═══
        private bool _isDraggingProfiler;
        private string? _draggingFactorName;
        private OxyPlot.Wpf.PlotView? _draggingPlotView;
        private bool _draggingIsCategorical;

        // ═══ 意愿行拖拽状态 ═══
        private bool _isDraggingDesirability;
        private string? _draggingDesirabilityFactorName;
        private OxyPlot.Wpf.PlotView? _draggingDesirabilityPlotView;

        public OlsAnalysisWindow()
        {
            this.Closed += (s, e) =>
            {
                if (DataContext is DOEModelAnalysisViewModel vm)
                {
                    vm.ClearProfilerPlots();
                }
            };
            InitializeComponent();
        }

        // ═══════════════════════════════════════════
        //  窗口控制
        // ═══════════════════════════════════════════

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
            => Close();

        // ═══════════════════════════════════════════
        //  Pareto 点击交互
        // ═══════════════════════════════════════════

        private void OlsPareto_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not DOEModelAnalysisViewModel vm) return;
            if (sender is not OxyPlot.Wpf.PlotView plotView) return;
            var model = plotView.Model;
            if (model == null) return;

            var pos = e.GetPosition(plotView);
            var barSeries = model.Series.OfType<BarSeries>().FirstOrDefault();
            if (barSeries == null) return;

            var result = barSeries.GetNearestPoint(new ScreenPoint(pos.X, pos.Y), false);
            if (result != null)
            {
                int index = (int)Math.Round(result.DataPoint.X);
                if (index >= 0)
                {
                    vm.ToggleParetoTerm(index);
                    e.Handled = true;
                }
            }
        }

        // ═══════════════════════════════════════════
        //  刻画器 Tab 自动加载
        // ═══════════════════════════════════════════

        private void TabProfiler_Checked(object sender, RoutedEventArgs e)
        {
            if (DataContext is DOEModelAnalysisViewModel vm && !vm.IsProfilerLoaded && vm.HasOlsResult)
                vm.LoadProfilerCommand?.Execute();
        }

        // ═══════════════════════════════════════════
        //  刻画器拖拽（从 DOEModelAnalysisView 完整迁移）
        // ═══════════════════════════════════════════

        private void Profiler_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not DOEModelAnalysisViewModel vm) return;
            if (sender is not OxyPlot.Wpf.PlotView plotView) return;
            if (!vm.IsProfilerLoaded) return;

            var factorName = plotView.Model?.Title;
            if (string.IsNullOrEmpty(factorName)) return;

            _isDraggingProfiler = true;
            _draggingFactorName = factorName;
            _draggingPlotView = plotView;
            _draggingIsCategorical = vm.IsProfilerFactorCategorical(factorName);
            plotView.CaptureMouse();

            // ★ 直接触发一次 Move 逻辑（不走 MoveCrosshair，让 VM 统一处理）
            Profiler_MouseMove(sender, e);
            e.Handled = true;
        }


        private void Profiler_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingProfiler || _draggingFactorName == null || _draggingPlotView == null) return;
            if (DataContext is not DOEModelAnalysisViewModel vm) return;

            if (_draggingIsCategorical)
            {
                var rawX = GetRawDataX(_draggingPlotView, e);
                if (!rawX.HasValue) return;
                var levels = vm.GetProfilerCategoryLevels(_draggingFactorName);
                if (levels == null || levels.Count == 0) return;
                int index = Math.Max(0, Math.Min(levels.Count - 1, (int)Math.Round(rawX.Value)));
                vm.UpdateProfilerLocal(_draggingFactorName, levels[index]);
            }
            else
            {
                var rawX = GetRawDataX(_draggingPlotView, e);
                if (!rawX.HasValue) return;
                var (fMin, fMax) = vm.GetProfilerFactorRange(_draggingFactorName);
                double newX = Math.Round(Math.Max(fMin, Math.Min(fMax, rawX.Value)), 4);
                vm.UpdateProfilerLocal(_draggingFactorName, newX);
            }
        }

        private void Profiler_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDraggingProfiler) return;
            _draggingPlotView?.ReleaseMouseCapture();
            _isDraggingProfiler = false;
            _draggingFactorName = null;
            _draggingPlotView = null;
        }

        /// <summary>移动十字线到鼠标位置（连续+类别因子统一处理）</summary>
        private void MoveCrosshair(OxyPlot.Wpf.PlotView plotView, string factorName,
            MouseEventArgs e, DOEModelAnalysisViewModel vm)
        {
            var model = plotView.Model;
            if (model == null) return;

            double? newX, newY;

            if (_draggingIsCategorical)
            {
                var rawX = GetRawDataX(plotView, e);
                if (!rawX.HasValue) return;
                var levels = vm.GetProfilerCategoryLevels(factorName);
                if (levels == null) return;
                int index = Math.Max(0, Math.Min(levels.Count - 1, (int)Math.Round(rawX.Value)));
                newX = index;
                newY = vm.GetProfilerCategoryYAtIndex(factorName, index);
            }
            else
            {
                newX = ClampXToRange(plotView, e, vm, factorName);
                if (!newX.HasValue) return;
                newY = vm.GetProfilerYAtX(factorName, newX.Value);
            }

            // 更新红色竖线
            var vline = model.Annotations
                .OfType<OxyPlot.Annotations.LineAnnotation>()
                .FirstOrDefault(a => a.Type == OxyPlot.Annotations.LineAnnotationType.Vertical
                                     && a.Color == OxyColors.Red);
            if (vline != null) vline.X = newX.Value;

            // 更新 Subtitle
            if (_draggingIsCategorical)
            {
                var levels = vm.GetProfilerCategoryLevels(factorName);
                int idx = (int)Math.Round(newX.Value);
                model.Subtitle = (levels != null && idx >= 0 && idx < levels.Count) ? levels[idx] : "?";
            }
            else
            {
                model.Subtitle = $"{newX.Value:F2}";
            }

            // 更新红色横线
            var hline = model.Annotations
                .OfType<OxyPlot.Annotations.LineAnnotation>()
                .FirstOrDefault(a => a.Type == OxyPlot.Annotations.LineAnnotationType.Horizontal);
            if (hline != null && newY.HasValue) hline.Y = newY.Value;

            // 更新预测值文本（含 CI）
            if (newY.HasValue)
            {
                string ciText = "";
                if (_draggingIsCategorical)
                {
                    var levels = vm.GetProfilerCategoryLevels(factorName);
                    int idx = (int)Math.Round(newX.Value);
                    var levelName = (levels != null && idx >= 0 && idx < levels.Count) ? levels[idx] : "?";
                    var (lo, hi) = vm.GetProfilerCategoryCIAtIndex(factorName, idx);
                    if (lo.HasValue && hi.HasValue) ciText = $"  95%CI [{lo.Value:F4}, {hi.Value:F4}]";
                    vm.ProfilerCurrentPredicted = $"{factorName}={levelName}  →  预测值={newY.Value:F4}{ciText}";
                }
                else
                {
                    var (lo, hi) = vm.GetProfilerCIAtX(factorName, newX.Value);
                    if (lo.HasValue && hi.HasValue) ciText = $"  95%CI [{lo.Value:F4}, {hi.Value:F4}]";
                    vm.ProfilerCurrentPredicted = $"{factorName}={newX.Value:F2}  →  预测值={newY.Value:F4}{ciText}";
                }
            }

            model.InvalidatePlot(false);
        }

        // ═══════════════════════════════════════════
        //  意愿行拖拽（从 DOEModelAnalysisView 完整迁移）
        // ═══════════════════════════════════════════

        private void DesirabilityProfiler_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not DOEModelAnalysisViewModel vm) return;
            if (sender is not OxyPlot.Wpf.PlotView plotView) return;

            var factorName = plotView.Model?.Title;
            if (string.IsNullOrEmpty(factorName)) return;

            if (factorName == "Desirability")
            {
                if (e.ClickCount == 2)
                    vm.SetDesirabilityCommand.Execute();
                return;
            }

            bool isCat = vm.IsProfilerFactorCategorical(factorName);
            if (isCat)
            {
                // ★ 类别因子: 点击切换水平，不做拖拽
                var rawX = GetRawDataX(plotView, e);
                if (rawX.HasValue)
                {
                    var levels = vm.GetProfilerCategoryLevels(factorName);
                    if (levels != null && levels.Count > 0)
                    {
                        int idx = Math.Max(0, Math.Min(levels.Count - 1, (int)Math.Round(rawX.Value)));
                        _ = vm.UpdateProfilerCategoryAsync(factorName, levels[idx]);
                    }
                }
                e.Handled = true;
                return;
            }

            // ★ 连续因子: 只记录拖拽状态，不立即更新位置
            _isDraggingDesirability = true;
            _draggingDesirabilityFactorName = factorName;
            _draggingDesirabilityPlotView = plotView;
            plotView.CaptureMouse();
            e.Handled = true;
        }

        private void DesirabilityProfiler_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingDesirability || _draggingDesirabilityFactorName == null) return;
            if (_draggingDesirabilityPlotView == null) return;
            if (DataContext is not DOEModelAnalysisViewModel vm) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;

            var rawX = GetRawDataX(_draggingDesirabilityPlotView, e);
            if (!rawX.HasValue) return;

            var (min, max) = vm.GetProfilerFactorRange(_draggingDesirabilityFactorName);
            if (Math.Abs(max - min) < 1e-10) return;  // ★ 范围无效则跳过

            double newX = Math.Round(Math.Max(min, Math.Min(max, rawX.Value)), 4);
            vm.UpdateProfilerLocal(_draggingDesirabilityFactorName, newX);
        }

        private void DesirabilityProfiler_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDraggingDesirability) return;
            _draggingDesirabilityPlotView?.ReleaseMouseCapture();
            _isDraggingDesirability = false;
            _draggingDesirabilityFactorName = null;
            _draggingDesirabilityPlotView = null;
        }

        // ═══════════════════════════════════════════
        //  意愿下拉菜单
        // ═══════════════════════════════════════════

        private void DesirabilityDropdown_Click(object sender, MouseButtonEventArgs e)
        {
            var vm = DataContext as DOEModelAnalysisViewModel;
            if (vm == null) return;

            var indigo = Color.FromRgb(0x2D, 0x3A, 0x8C);
            var indigoLt = Color.FromRgb(0xE8, 0xEA, 0xF6);
            var text = Color.FromRgb(0x37, 0x41, 0x51);
            var border = Color.FromRgb(0xE2, 0xE5, 0xEB);

            // ContextMenu 自定义模板（覆盖系统默认）
            var menuTemplate = new ControlTemplate(typeof(ContextMenu));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Colors.White));
            borderFactory.SetValue(Border.BorderBrushProperty, new SolidColorBrush(border));
            borderFactory.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            borderFactory.SetValue(Border.PaddingProperty, new Thickness(0, 6, 0, 6));
            borderFactory.SetValue(Border.SnapsToDevicePixelsProperty, true);

            var dropShadow = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 12,
                ShadowDepth = 3,
                Opacity = 0.08,
                Direction = 270
            };
            borderFactory.SetValue(Border.EffectProperty, dropShadow);

            var stackFactory = new FrameworkElementFactory(typeof(StackPanel));
            stackFactory.SetValue(StackPanel.IsItemsHostProperty, true);
            borderFactory.AppendChild(stackFactory);
            menuTemplate.VisualTree = borderFactory;

            // MenuItem 自定义模板
            var miTemplate = new ControlTemplate(typeof(MenuItem));
            var miBorder = new FrameworkElementFactory(typeof(Border));
            miBorder.SetValue(Border.PaddingProperty, new Thickness(16, 8, 24, 8));
            miBorder.SetValue(Border.BackgroundProperty, new SolidColorBrush(Colors.Transparent));
            miBorder.SetValue(Border.CursorProperty, Cursors.Hand);
            miBorder.Name = "MiBd";
            var miContent = new FrameworkElementFactory(typeof(ContentPresenter));
            miContent.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            miBorder.AppendChild(miContent);
            miTemplate.VisualTree = miBorder;

            var miHighlight = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            miHighlight.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(indigoLt), "MiBd"));
            miTemplate.Triggers.Add(miHighlight);

            var miStyle = new Style(typeof(MenuItem));
            miStyle.Setters.Add(new Setter(MenuItem.TemplateProperty, miTemplate));
            miStyle.Setters.Add(new Setter(MenuItem.ForegroundProperty, new SolidColorBrush(text)));
            miStyle.Setters.Add(new Setter(MenuItem.FontSizeProperty, 12.0));
            miStyle.Setters.Add(new Setter(MenuItem.FontFamilyProperty, new FontFamily("Segoe UI")));
            var miHoverFg = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            miHoverFg.Setters.Add(new Setter(MenuItem.ForegroundProperty, new SolidColorBrush(indigo)));
            miHoverFg.Setters.Add(new Setter(MenuItem.FontWeightProperty, System.Windows.FontWeights.Medium));
            miStyle.Triggers.Add(miHoverFg);

            var sepStyle = new Style(typeof(Separator));
            sepStyle.Setters.Add(new Setter(Separator.MarginProperty, new Thickness(12, 4, 12, 4)));
            sepStyle.Setters.Add(new Setter(Separator.HeightProperty, 1.0));
            sepStyle.Setters.Add(new Setter(Separator.BackgroundProperty, new SolidColorBrush(border)));
            var sepTemplate = new ControlTemplate(typeof(Separator));
            var sepBorder = new FrameworkElementFactory(typeof(Border));
            sepBorder.SetValue(Border.HeightProperty, 1.0);
            sepBorder.SetValue(Border.BackgroundProperty, new SolidColorBrush(border));
            sepTemplate.VisualTree = sepBorder;
            sepStyle.Setters.Add(new Setter(Separator.TemplateProperty, sepTemplate));

            var menu = new ContextMenu { Template = menuTemplate, HasDropShadow = false };

            MenuItem Mi(string header) => new MenuItem { Header = header, Style = miStyle };
            Separator Sep() => new Separator { Style = sepStyle };

            ILocalizationService localization = ContainerLocator.Container.Resolve<ILocalizationService>();
            if (localization != null)
            {
                var i1 = Mi(vm.IsDesirabilityVisible ? localization.GetString("Doe_Analysis_Mi_Hide", "隐藏意愿行") : localization.GetString("Doe_Analysis_Mi_Display", "显示意愿行"));
                i1.Click += (s, _) => vm.ToggleDesirabilityCommand.Execute();
                menu.Items.Add(i1);
                menu.Items.Add(Sep());

                var i2 = Mi(localization.GetString("Doe_Analysis_Mi_Max", "最大化意愿")); i2.Click += (s, _) => vm.MaximizeDesirabilityCommand.Execute();
                var i3 = Mi(localization.GetString("Doe_Analysis_Mi_Remember", "最大化并记住")); i3.Click += (s, _) => vm.MaximizeAndRememberCommand.Execute();
                menu.Items.Add(i2); menu.Items.Add(i3);
                menu.Items.Add(Sep());

                var i4 = Mi(localization.GetString("Doe_Analysis_Mi_Set", "设置意愿...")); i4.Click += (s, _) => vm.SetDesirabilityCommand.Execute();
                var i5 = Mi(localization.GetString("Doe_Analysis_Mi_Value", "保存意愿值")); i5.Click += (s, _) => vm.SaveDesirabilityCommand.Execute();
                var i6 = Mi(localization.GetString("Doe_Analysis_Mi_Save", "保存意愿公式")); i6.Click += (s, _) => vm.SaveDesirabilityFormulaCommand.Execute();
                menu.Items.Add(i4); menu.Items.Add(i5); menu.Items.Add(i6);
            }
            else
            {
                var i1 = Mi(vm.IsDesirabilityVisible ? "隐藏意愿行" : "显示意愿行");
                i1.Click += (s, _) => vm.ToggleDesirabilityCommand.Execute();
                menu.Items.Add(i1);
                menu.Items.Add(Sep());

                var i2 = Mi("最大化意愿"); i2.Click += (s, _) => vm.MaximizeDesirabilityCommand.Execute();
                var i3 = Mi("最大化并记住"); i3.Click += (s, _) => vm.MaximizeAndRememberCommand.Execute();
                menu.Items.Add(i2); menu.Items.Add(i3);
                menu.Items.Add(Sep());

                var i4 = Mi("设置意愿..."); i4.Click += (s, _) => vm.SetDesirabilityCommand.Execute();
                var i5 = Mi("保存意愿值"); i5.Click += (s, _) => vm.SaveDesirabilityCommand.Execute();
                var i6 = Mi("保存意愿公式"); i6.Click += (s, _) => vm.SaveDesirabilityFormulaCommand.Execute();
                menu.Items.Add(i4); menu.Items.Add(i5); menu.Items.Add(i6);
            }

            menu.IsOpen = true;
            e.Handled = true;
        }

        // ═══════════════════════════════════════════
        //  工具方法
        // ═══════════════════════════════════════════

        private double? GetRawDataX(OxyPlot.Wpf.PlotView plotView, MouseEventArgs e)
        {
            var model = plotView.Model;
            if (model == null) return null;
            var xAxis = model.Axes.OfType<LinearAxis>()
                .FirstOrDefault(a => a.Position == AxisPosition.Bottom);
            if (xAxis == null) return null;
            return xAxis.InverseTransform(e.GetPosition(plotView).X);
        }

        private double? ClampXToRange(OxyPlot.Wpf.PlotView plotView, MouseEventArgs e,
            DOEModelAnalysisViewModel vm, string factorName)
        {
            var rawX = GetRawDataX(plotView, e);
            if (!rawX.HasValue) return null;
            var (fMin, fMax) = vm.GetProfilerFactorRange(factorName);
            return Math.Round(Math.Max(fMin, Math.Min(fMax, rawX.Value)), 4);
        }

        private ItemsControl? FindParentItemsControl(DependencyObject child)
        {
            var parent = VisualTreeHelper.GetParent(child);
            while (parent != null)
            {
                if (parent is ItemsControl ic)
                    return ic;
                parent = VisualTreeHelper.GetParent(parent);
            }
            return null;
        }

        private int FindPlotViewIndex(ItemsControl itemsControl, OxyPlot.Wpf.PlotView plotView)
        {
            for (int i = 0; i < itemsControl.Items.Count; i++)
            {
                var container = itemsControl.ItemContainerGenerator.ContainerFromIndex(i) as ContentPresenter;
                if (container != null)
                {
                    var pv = FindChild<OxyPlot.Wpf.PlotView>(container);
                    if (pv == plotView) return i;
                }
            }
            return -1;
        }

        private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;
                var result = FindChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }
    }
}