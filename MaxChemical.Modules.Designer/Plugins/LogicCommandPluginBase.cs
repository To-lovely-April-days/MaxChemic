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
    /// <summary>
    /// 逻辑命令插件基类
    /// </summary>
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

        // 修改访问修饰符为 protected，让子类可以访问
        protected virtual Button CreateStandardToolbarButton()
        {
            var button = new Button
            {
                Width = 44,
                Height = 44,
                Margin = new Thickness(0, 0, 16, 0),
                ToolTip = ToolTip,
                Tag = Id,
                Style = Application.Current.FindResource("ToolButtonStyle") as Style,
                RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                RenderTransform = new ScaleTransform()
            };

            // 添加图标
            try
            {
                var image = new Image
                {
                    Source = new BitmapImage(new Uri(IconPath, UriKind.RelativeOrAbsolute)),
                    Width = 44,
                    Height = 44,
                    Stretch = Stretch.Fill
                };
                button.Content = image;
            }
            catch (Exception ex)
            {
                Log.Warning($"加载插件图标失败: {IconPath}, {ex.Message}");
                // 使用默认图标
                var defaultIcon = new TextBlock
                {
                    Text = "⚙",
                    FontSize = 24,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.Gray
                };
                button.Content = defaultIcon;
            }

            // 添加鼠标事件处理
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
                    To = 1.1,
                    Duration = TimeSpan.FromMilliseconds(200),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                var scaleYAnimation = new DoubleAnimation
                {
                    To = 1.1,
                    Duration = TimeSpan.FromMilliseconds(200),
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
                    Duration = TimeSpan.FromMilliseconds(200),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                var scaleYAnimation = new DoubleAnimation
                {
                    To = 1.0,
                    Duration = TimeSpan.FromMilliseconds(200),
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