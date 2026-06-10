// Plugins/Commands/ParallelCommandPlugin.cs
using System.Windows;
using System.Windows.Controls;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Views;

namespace MaxChemical.Modules.Designer.Plugins.Commands
{
    public class ParallelCommandPlugin : LogicCommandPluginBase
    {
        public override string Id => "Parallel";
        public override string Name => "并行";
        public override string Description => "同时执行多个任务";
        public override string IconPath => "pack://siteoforigin:,,,/Resources/CommadIcon/Parallel.png";
        public override string Bezel_lessIconPath => "pack://siteoforigin:,,,/Resources/CommadIcon/Bezel-lessIF.png";
        public override string ToolTip => "并行执行多个命令";
        public override int Order => 5;

        public override FrameworkElement CreateToolbarButton()
        {
            return CreateStandardToolbarButton();
        }

        public override UserControl CreateNodeControl(CommandNode node)
        {
            return new ParallelNodeControlView { DataContext = node };
        }

        public override CommandNode CreateCommandNode()
        {
            var logicCommand = new LogicCommand(Name, Description, IconPath, LogicCommandType.Parallel);

            var properties = new ParallelCommandProperties();
            logicCommand.SetProperty("Bezel_lessIconPath", properties.Bezel_lessIconPath);
            logicCommand.SetProperty("MaxParallelCount", properties.MaxParallelCount);
            logicCommand.SetProperty("WaitStrategy", properties.WaitStrategy);
            logicCommand.SetProperty("TimeoutSeconds", properties.TimeoutSeconds);
            logicCommand.SetProperty("Description", properties.Description);

            return new CommandNode(logicCommand);
        }
    }
}