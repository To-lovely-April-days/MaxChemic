// Plugins/EndCommandPlugin.cs
using System.Windows;
using System.Windows.Controls;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Views;

namespace MaxChemical.Modules.Designer.Plugins.Commands
{
    /// <summary>
    /// 结束指令插件：执行到此节点时立即结束整个流程。
    /// 注意：插件 Id 必须与 LogicCommandType.End.ToString() 一致（="End"），
    /// 否则恢复/创建节点时按类型名查插件会失败。
    /// </summary>
    public class EndCommandPlugin : LogicCommandPluginBase
    {
        public override string Id => "End";
        public override string Name => "结束";
        public override string Description => "结束整个流程的执行";
        public override string IconPath => "pack://siteoforigin:,,,/Resources/CommadIcon/setvariables.png";
        public override string Bezel_lessIconPath => "pack://siteoforigin:,,,/Resources/CommadIcon/Bezel-lessIF.png";
        public override string ToolTip => "执行到此处立即结束整个流程";
        public override int Order => 7;

        // 电源/结束图标（矢量），与画布节点图标一致；避免依赖 PNG 资源
        private const string EndIconGeometry =
            "M718.9248 239.328c24.1984 17.2096 45.7152 36.704 64.5376 58.4896s34.9568 45.312 48.4032 70.592c13.4464 25.28 23.6608 52.032 30.6496 80.2688 6.9952 28.2368 10.4896 56.8704 10.4896 85.9136 0 50.016-9.5424 96.9408-28.64 140.7744-19.0848 43.8336-44.9088 82.016-77.4464 114.5536s-70.7264 58.3552-114.5536 77.4464C608.5376 886.4576 561.6192 896 511.5968 896c-49.472 0-96.1344-9.5424-139.9616-28.64-43.8336-19.0848-82.1504-44.9088-114.9568-77.4464s-58.6176-70.7264-77.4464-114.5536-28.2368-90.7584-28.2368-140.7744c0-28.4992 3.36-56.4736 10.0864-83.8976 6.72-27.4304 16.2624-53.5104 28.64-78.2464 12.3712-24.7424 27.6928-47.872 45.984-69.376 18.2848-21.5168 38.72-40.8768 61.3056-58.0928 11.84-8.6016 24.608-11.8272 38.3232-9.6768 13.7152 2.1504 24.8704 8.8768 33.472 20.1664 8.608 11.296 11.84 23.936 9.6896 37.92-2.1568 13.984-8.8768 25.28-20.1728 33.8816C324.4352 352 298.4896 382.3872 280.4736 418.4192c-18.016 36.032-27.0336 74.7584-27.0336 116.1664 0 35.5008 6.7264 68.9728 20.1728 100.4352 13.4464 31.4624 31.8656 58.8928 55.2576 82.2848 23.3984 23.392 50.8224 41.952 82.2848 55.6608 31.4624 13.7216 64.9344 20.576 100.4288 20.576 35.5008 0 68.9792-6.8544 100.4352-20.576 31.4624-13.7152 58.8928-32.2688 82.2848-55.6608 23.3984-23.392 41.952-50.8224 55.6608-82.2848 13.7216-31.4624 20.576-64.9344 20.576-100.4352 0-41.952-9.6832-81.6128-29.0432-118.9888s-46.5216-68.1664-81.472-92.3712c-11.84-8.064-18.9632-19.0912-21.3824-33.0688-2.4192-13.9904 0.4032-26.8928 8.4672-38.7264 8.0704-11.296 19.0912-18.1504 33.0752-20.5696C694.1824 228.4352 707.0912 231.264 718.9248 239.328zM511.5968 537.0048c-13.984 0-25.9456-4.9728-35.8912-14.9184-9.952-9.952-14.9248-21.92-14.9248-35.904L460.7808 179.6288c0-13.984 4.9728-26.08 14.9248-36.3008S497.6128 128 511.5968 128c14.528 0 26.7648 5.1072 36.704 15.328 9.9584 10.2208 14.9312 22.3168 14.9312 36.3008l0 306.5536c0 13.984-4.9728 25.952-14.9312 35.904C538.3552 532.032 526.1184 537.0048 511.5968 537.0048z";

        public override FrameworkElement CreateToolbarButton()
        {
            // 复用基类按钮的样式(圆角/hover/缩放动画)，仅把内容替换为矢量图标，无需 PNG 资源
            var button = CreateStandardToolbarButton();
            button.Content = new System.Windows.Shapes.Path
            {
                Data = System.Windows.Media.Geometry.Parse(EndIconGeometry),
                Fill = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xA4, 0x16, 0x26)),
                Width = 26,
                Height = 26,
                Stretch = System.Windows.Media.Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = System.Windows.VerticalAlignment.Center
            };
            return button;
        }

        public override UserControl CreateNodeControl(CommandNode node)
        {
            return new EndNodeControlView { DataContext = node };
        }

        public override CommandNode CreateCommandNode()
        {
            var logicCommand = new LogicCommand(Name, Description, IconPath, LogicCommandType.End);
            logicCommand.SetProperty("Description", "结束整个流程");
            return new CommandNode(logicCommand);
        }
    }
}
