// Plugins/Commands/LoopCommandPlugin.cs
using System.Windows;
using System.Windows.Controls;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Views;

namespace MaxChemical.Modules.Designer.Plugins.Commands
{
    public class LoopCommandPlugin : LogicCommandPluginBase
    {
        public override string Id => "Loop";
        public override string Name => "循环";
        public override string Description => "重复执行指定次数或满足条件时执行";
        public override string IconPath => "pack://siteoforigin:,,,/Resources/CommadIcon/loop.png";
        public override string Bezel_lessIconPath => "pack://siteoforigin:,,,/Resources/CommadIcon/Bezel-lessIF.png";
        public override string ToolTip => "循环执行内部命令";
        public override int Order => 3;

        public override FrameworkElement CreateToolbarButton()
        {
            return CreateStandardToolbarButton();
        }

        public override UserControl CreateNodeControl(CommandNode node)
        {
            return new LoopNodeControlView { DataContext = node };
        }

        public override CommandNode CreateCommandNode()
        {
            var logicCommand = new LogicCommand(Name, Description, IconPath, LogicCommandType.Loop);

            var properties = new LoopCommandProperties();
            logicCommand.SetProperty("Bezel_lessIconPath", properties.Bezel_lessIconPath);
            logicCommand.SetProperty("LoopType", properties.LoopType);
            logicCommand.SetProperty("LoopCount", properties.LoopCount);
            logicCommand.SetProperty("Description", properties.Description);

            return new CommandNode(logicCommand);
        }

        public override ValidationResult ValidateNodeConfiguration(CommandNode node)
        {
            var baseResult = base.ValidateNodeConfiguration(node);
            if (!baseResult.IsValid) return baseResult;

            var loopType = node.LogicCommand?.GetProperty<LoopType>("LoopType", LoopType.FixedCount);

            if (loopType == LoopType.FixedCount)
            {
                var loopCount = node.LogicCommand?.GetProperty<int>("LoopCount", 0);
                if (loopCount <= 0)
                {
                    return ValidationResult.Error("循环次数必须大于0");
                }
            }
            else if (loopType == LoopType.Conditional)
            {
                var condition = node.LogicCommand?.GetProperty<string>("LoopCondition", "");
                if (string.IsNullOrWhiteSpace(condition))
                {
                    return ValidationResult.Error("循环条件不能为空");
                }
            }

            return ValidationResult.Success();
        }
    }
}