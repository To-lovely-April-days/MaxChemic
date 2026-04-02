// ============================================================
// LogicCommandPluginBase.cs — 工具栏按钮缩小 (配合 Style A 48px 顶栏)
//
// 替换 CreateStandardToolbarButton 方法（约第 56~101 行）
// ============================================================

// LogicCommandPluginBase.cs - 修复版本
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using MaxChemical.Modules.Designer.Models;
using Serilog;

namespace MaxChemical.Modules.Designer.Plugins
{
    public abstract class LogicCommandPluginBase : ILogicCommandPlugin
    {
        public abstract string Id { get; }
        public abstract string Name { get; }
        public abstract string Description { get; }
        public abstract string IconPath { get; }
        public abstract string Bezel_lessIconPath { get; }
        public abstract string ToolTip { get; }
        public virtual string Version => "1.0.0";
        public virtual int Order => 0;
        public virtual bool IsEnabled => true;
        public virtual string Category => "Logic";

        public virtual void Initialize()
        {
            Log.Information($"初始化逻辑命令插件: {Name}");
        }

        public virtual void Dispose()
        {
            Log.Information($"卸载逻辑命令插件: {Name}");
        }

        public virtual ValidationResult ValidateNodeConfiguration(CommandNode node)
        {
            if (node == null)
                return ValidationResult.Error("节点不能为空");

            return ValidationResult.Success();
        }

        public abstract UserControl CreateNodeControl(CommandNode node);
        public abstract CommandNode CreateCommandNode();

        public virtual FrameworkElement CreateToolbarButton()
        {
            return CreateStandardToolbarButton();
        }

        /// <summary>
        ///  修改：按钮从 44×44 缩小到 32×32，图标从 44×44 缩到 24×24
        ///   使用内联 ControlTemplate 替代 ToolButtonStyle，自带 hover/pressed 效果
        /// </summary>
        protected virtual Button CreateStandardToolbarButton()
        {
            var button = new Button
            {
                Width = 38,
                Height = 38,
                Margin = new Thickness(0, 0, 3, 0),
                ToolTip = ToolTip,
                Tag = Id,
                Cursor = System.Windows.Input.Cursors.Hand,
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                RenderTransform = new ScaleTransform()
            };

            // 自定义按钮模板：圆角 6px + hover/pressed 背景色
            var template = new ControlTemplate(typeof(Button));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.Name = "Bd";
            borderFactory.SetValue(Border.BackgroundProperty, new SolidColorBrush(Colors.Transparent));
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            borderFactory.SetValue(Border.PaddingProperty, new Thickness(4));

            var contentFactory = new FrameworkElementFactory(typeof(ContentPresenter));
            contentFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            contentFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(contentFactory);

            template.VisualTree = borderFactory;

            // Hover → 浅灰背景 #E5E7EB
            var hoverTrigger = new Trigger
            {
                Property = UIElement.IsMouseOverProperty,
                Value = true
            };
            hoverTrigger.Setters.Add(new Setter(
                Border.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(0xE5, 0xE7, 0xEB)),
                "Bd"));
            template.Triggers.Add(hoverTrigger);

            // Pressed → 稍深灰 #D1D5DB
            var pressedTrigger = new Trigger
            {
                Property = Button.IsPressedProperty,
                Value = true
            };
            pressedTrigger.Setters.Add(new Setter(
                Border.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(0xD1, 0xD5, 0xDB)),
                "Bd"));
            template.Triggers.Add(pressedTrigger);

            button.Template = template;

            // 图标：24×24，高质量缩放
            try
            {
                var image = new Image
                {
                    Source = new BitmapImage(new Uri(IconPath, UriKind.RelativeOrAbsolute)),
                    Width = 30,
                    Height = 30,
                    Stretch = Stretch.Uniform
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                button.Content = image;
            }
            catch (Exception ex)
            {
                Log.Warning($"加载插件图标失败: {IconPath}, {ex.Message}");
                var defaultIcon = new TextBlock
                {
                    Text = "⚙",
                    FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.Gray
                };
                button.Content = defaultIcon;
            }

            // hover 缩放动画（1.0 ↔ 1.08，比原来的 1.1 更克制）
            AddMouseEventHandlers(button);

            return button;
        }

        private void AddMouseEventHandlers(Button button)
        {
            var scaleTransform = button.RenderTransform as ScaleTransform;

            button.MouseEnter += (s, e) =>
            {
                var storyboard = new Storyboard();
                var scaleXAnimation = new DoubleAnimation
                {
                    To = 1.08,
                    Duration = TimeSpan.FromMilliseconds(150),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                var scaleYAnimation = new DoubleAnimation
                {
                    To = 1.08,
                    Duration = TimeSpan.FromMilliseconds(150),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };

                Storyboard.SetTarget(scaleXAnimation, scaleTransform);
                Storyboard.SetTargetProperty(scaleXAnimation, new PropertyPath("ScaleX"));
                Storyboard.SetTarget(scaleYAnimation, scaleTransform);
                Storyboard.SetTargetProperty(scaleYAnimation, new PropertyPath("ScaleY"));

                storyboard.Children.Add(scaleXAnimation);
                storyboard.Children.Add(scaleYAnimation);
                storyboard.Begin();
            };

            button.MouseLeave += (s, e) =>
            {
                var storyboard = new Storyboard();
                var scaleXAnimation = new DoubleAnimation
                {
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(150),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                var scaleYAnimation = new DoubleAnimation
                {
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(150),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };

                Storyboard.SetTarget(scaleXAnimation, scaleTransform);
                Storyboard.SetTargetProperty(scaleXAnimation, new PropertyPath("ScaleX"));
                Storyboard.SetTarget(scaleYAnimation, scaleTransform);
                Storyboard.SetTargetProperty(scaleYAnimation, new PropertyPath("ScaleY"));

                storyboard.Children.Add(scaleXAnimation);
                storyboard.Children.Add(scaleYAnimation);
                storyboard.Begin();
            };
        }
    }
}