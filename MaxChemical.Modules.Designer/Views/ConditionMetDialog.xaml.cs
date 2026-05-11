using MaxChemical.Infrastructure.Services;
using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Prism.Ioc;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class ConditionMetDialog : Window
    {
        private readonly ILocalizationService _localization; // 本地化服务
        private readonly CancellationTokenSource _cancellationTokenSource;
        private bool _isContinue = false;
        private bool _isStopped = false;

        public bool IsContinue => _isContinue;
        public bool IsStopped => _isStopped;
        public CancellationToken CancellationToken => _cancellationTokenSource.Token;

        // 半圆弧线绝对长度 = π * r = π * 65 ≈ 204.2
        // WPF StrokeDashArray 的单位是 StrokeThickness 的倍数
        // StrokeThickness = 10，所以 dash 单位值 = 绝对像素 / 10
        private const double ArcLengthPx = 204.2;
        private const double StrokeWidth = 10.0;
        private static readonly double ArcLengthUnits = ArcLengthPx / StrokeWidth; // ≈ 20.42

        public ConditionMetDialog(string conditionExpression, bool isAsyncMode = false)
            : this(conditionExpression, isAsyncMode, false, null, null, null) { }

        public ConditionMetDialog(string conditionExpression, bool isAsyncMode, bool isWarning)
            : this(conditionExpression, isAsyncMode, isWarning, null, null, null) { }

        public ConditionMetDialog(
            string conditionExpression, bool isAsyncMode, bool isWarning,
            double? currentValue, double? thresholdValue, string parameterName)
        {
            InitializeComponent();
            // 
            _localization = ContainerLocator.Container.Resolve<ILocalizationService>();
            ContinueButton.Content = _localization.GetString("Condition_ContinueBtn_Exec", "继续执行");
            StopButton.Content = _localization.GetString("Condition_StopBtn","停止流程");

            _cancellationTokenSource = new CancellationTokenSource();

            ConditionText.Text = conditionExpression;
            ExecutionModeText.Text = isAsyncMode ? _localization.GetString("Condition_SyncMode_Async", "异步执行模式") : _localization.GetString("Condition_SyncMode_Sync", "同步执行模式");
            HelpText.Text = "• 继续：流程将继续执行下一步骤\n• 停止：终止整个流程的执行";

            if (isWarning)
                ApplyWarningGauge(currentValue, thresholdValue, parameterName);
            else
                ApplySuccessGauge();
        }

        public void SetCurrentValue(string value)
        {
            CurrentValuePanel.Visibility = Visibility.Visible;
            CurrentValueText.Text = value;
        }

        #region 仪表盘

        private void ApplyWarningGauge(double? currentValue, double? thresholdValue, string parameterName)
        {
            var warningColor = new SolidColorBrush(Color.FromRgb(230, 81, 0));
            var gaugeStroke = new SolidColorBrush(Color.FromRgb(255, 152, 0));

            GaugeValueArc.Stroke = gaugeStroke;
            TitleText.Foreground = warningColor;
            ConditionText.Foreground = warningColor;
            ConditionCard.Background = new SolidColorBrush(Color.FromRgb(255, 248, 240));
            ConditionCard.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 224, 178));
            ContinueButton.Background = warningColor;
            ContinueButton.Content = _localization.GetString("Condition_ContinueBtn_Confirm", "确认继续");

            var (unit, gaugeMin, gaugeMax) = GuessParameterRange(parameterName, currentValue, thresholdValue);
            double cv = currentValue ?? 0;
            double tv = thresholdValue ?? gaugeMax * 0.6;

            string displayName = ExtractDisplayName(parameterName);
            TitleText.Text = string.Format(_localization.GetString("Condition_Title_Than", "{0} 超出阈值"),displayName);
            double exceeded = Math.Abs(cv - tv);
            ExecutionModeText.Text = string.Format(_localization.GetString("Condition_SyncMode_Than", "阈值 {0:G5}{1} · 超出 {2:G4}{3}"), tv, unit, exceeded, unit);

            GaugeCenterValue.Text = FormatValue(cv);
            GaugeCenterValue.Foreground = warningColor;
            GaugeCenterUnit.Text = unit;

            GaugeMinLabel.Text = gaugeMin.ToString("G4");
            GaugeMaxLabel.Text = gaugeMax.ToString("G4");

            //  关键修正：StrokeDashArray 的值是以 StrokeThickness 为单位的
            // 比如弧线总长 204.2px，StrokeThickness=10，则总长 = 20.42 个单位
            // 要画 valueRatio 比例的弧线：dash = totalUnits * ratio, gap = 很大值
            double valueRatio = Math.Min(1.0, Math.Max(0, (cv - gaugeMin) / (gaugeMax - gaugeMin)));
            double dashLength = ArcLengthUnits * valueRatio;
            double gapLength = ArcLengthUnits + 10; // 确保剩余部分都是空白
            GaugeValueArc.StrokeDashArray = new DoubleCollection { dashLength, gapLength };
            GaugeValueArc.StrokeDashOffset = 0;

            double thresholdRatio = Math.Min(1.0, Math.Max(0, (tv - gaugeMin) / (gaugeMax - gaugeMin)));
            SetThresholdTick(thresholdRatio, tv);

            CenterText(GaugeCenterValue, CenterValueTranslate);
            CenterText(GaugeCenterUnit, CenterUnitTranslate);
        }

        private void ApplySuccessGauge()
        {
            var successColor = new SolidColorBrush(Color.FromRgb(46, 125, 50));
            GaugeValueArc.Stroke = new SolidColorBrush(Color.FromRgb(76, 175, 80));

            TitleText.Text = _localization.GetString("Condition_Title_Satisfy", "条件已满足");
            TitleText.Foreground = successColor;
            ConditionText.Foreground = successColor;
            ConditionCard.Background = new SolidColorBrush(Color.FromRgb(241, 248, 233));
            ConditionCard.BorderBrush = new SolidColorBrush(Color.FromRgb(200, 230, 201));
            ContinueButton.Background = successColor;

            GaugeCenterValue.Text = "PASS";
            GaugeCenterValue.FontSize = 22;
            GaugeCenterValue.Foreground = successColor;
            GaugeCenterUnit.Text = _localization.GetString("Condition_Title_Satisfy", "条件已满足");

            GaugeMinLabel.Visibility = Visibility.Collapsed;
            GaugeMaxLabel.Visibility = Visibility.Collapsed;

            CenterText(GaugeCenterValue, CenterValueTranslate);
            CenterText(GaugeCenterUnit, CenterUnitTranslate);
        }

        private void SetThresholdTick(double ratio, double thresholdValue)
        {
            double angleDeg = 180.0 - ratio * 180.0;
            double angleRad = angleDeg * Math.PI / 180.0;
            double cx = 80, cy = 80, r = 65;

            double x = cx + r * Math.Cos(angleRad);
            double y = cy - r * Math.Sin(angleRad);

            double dx = Math.Cos(angleRad);
            double dy = -Math.Sin(angleRad);
            ThresholdTick.X1 = x - dx * 7;
            ThresholdTick.Y1 = y - dy * 7;
            ThresholdTick.X2 = x + dx * 7;
            ThresholdTick.Y2 = y + dy * 7;
            ThresholdTick.Visibility = Visibility.Visible;

            ThresholdLabel.Text = thresholdValue.ToString("G4");
            ThresholdLabel.Visibility = Visibility.Visible;
            double labelX = x + dx * 11 - 10;
            double labelY = y + dy * 11 - 12;
            System.Windows.Controls.Canvas.SetLeft(ThresholdLabel, Math.Max(0, Math.Min(145, labelX)));
            System.Windows.Controls.Canvas.SetTop(ThresholdLabel, Math.Max(0, Math.Min(78, labelY)));
        }

        private void CenterText(System.Windows.Controls.TextBlock tb, TranslateTransform tt)
        {
            tb.Loaded += (s, e) => { tt.X = -tb.ActualWidth / 2; };
            var ft = new FormattedText(
                tb.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
                tb.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            tt.X = -ft.Width / 2;
        }

        #endregion

        #region 参数推测

        private (string unit, double min, double max) GuessParameterRange(
            string parameterName, double? currentValue, double? thresholdValue)
        {
            string name = (parameterName ?? "").ToLowerInvariant();
            double cv = currentValue ?? 0;
            double tv = thresholdValue ?? 0;
            double maxRef = Math.Max(Math.Abs(cv), Math.Abs(tv));

            if (name.Contains("temp") || name.Contains("温度") || name.Contains("tt"))
                return ("℃", 0, RoundUpRange(maxRef, 50, 300));
            if (name.Contains("press") || name.Contains("压力") || name.Contains("pt") || name.Contains("mpa"))
                return ("MPa", 0, RoundUpRange(maxRef, 1, 10));
            if (name.Contains("flow") || name.Contains("流量"))
                return ("mL/min", 0, RoundUpRange(maxRef, 100, 1000));
            if (name.Contains("speed") || name.Contains("转速") || name.Contains("rpm"))
                return ("RPM", 0, RoundUpRange(maxRef, 100, 5000));
            if (name.Contains("ph"))
                return ("", 0, 14);
            if (maxRef > 0)
                return ("", 0, RoundUpRange(maxRef, 10, 1000));
            return ("", 0, 100);
        }

        private double RoundUpRange(double value, double minRange, double defaultMax)
        {
            if (value <= 0) return defaultMax;
            double candidate = value * 1.5;
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(candidate)));
            double normalized = candidate / magnitude;
            if (normalized <= 1) return magnitude;
            if (normalized <= 2) return 2 * magnitude;
            if (normalized <= 5) return 5 * magnitude;
            return 10 * magnitude;
        }

        private string ExtractDisplayName(string parameterName)
        {
            if (string.IsNullOrEmpty(parameterName)) return "参数";
            var name = parameterName.Trim().TrimStart('{', '$').TrimEnd('}');
            if (name.Contains("."))
            {
                var parts = name.Split('.');
                name = parts[parts.Length - 1];
            }
            var lower = name.ToLowerInvariant();
            if (lower.Contains("temperature") || lower.Contains("temp")) return "温度";
            if (lower.Contains("pressure") || lower.Contains("press")) return "压力";
            if (lower.Contains("flowrate") || lower.Contains("flow")) return "流量";
            if (lower.Contains("speed")) return "转速";
            if (lower.Contains("ph")) return "pH";
            return name;
        }

        private string FormatValue(double value)
        {
            if (Math.Abs(value) >= 1000) return value.ToString("F0");
            if (Math.Abs(value) >= 100) return value.ToString("F1");
            if (Math.Abs(value) >= 1) return value.ToString("F2");
            return value.ToString("F3");
        }

        #endregion

        #region 按钮

        private void ContinueButton_Click(object sender, RoutedEventArgs e)
        {
            _isContinue = true; DialogResult = true; Close();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _isStopped = true; _cancellationTokenSource.Cancel(); DialogResult = false; Close();
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) { try { DragMove(); } catch { } }
        }

        protected override void OnClosed(EventArgs e)
        {
            _cancellationTokenSource?.Dispose(); base.OnClosed(e);
        }

        #endregion
    }
}
