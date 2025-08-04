// Plugins/Commands/SetVariableCommandPlugin.cs
using System.Windows;
using System.Windows.Controls;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Views;

namespace MaxChemical.Modules.Designer.Plugins.Commands
{
    public class SetVariableCommandPlugin : LogicCommandPluginBase
    {
        public override string Id => "SetVariable";
        public override string Name => "设置变量";
        public override string Description => "设置变量的值";
        public override string IconPath => "pack://siteoforigin:,,,/Resources/CommadIcon/setvariables.png";
        public override string Bezel_lessIconPath => "pack://siteoforigin:,,,/Resources/CommadIcon/Bezel-lessIF.png";
        public override string ToolTip => "设置或修改变量值";
        public override int Order => 6;

        public override FrameworkElement CreateToolbarButton()
        {
            return CreateStandardToolbarButton();
        }

        public override UserControl CreateNodeControl(CommandNode node)
        {
            return new SetVariableNodeControlView { DataContext = node };
        }

        public override CommandNode CreateCommandNode()
        {
            var logicCommand = new LogicCommand(Name, Description, IconPath, LogicCommandType.SetVariable);

            var properties = new SetVariableCommandProperties();
            logicCommand.SetProperty("VariableName", properties.VariableName);
            logicCommand.SetProperty("VariableValue", properties.VariableValue);
            logicCommand.SetProperty("VariableType", properties.VariableType);
            logicCommand.SetProperty("Description", properties.Description);

            return new CommandNode(logicCommand);
        }

        public override ValidationResult ValidateNodeConfiguration(CommandNode node)
        {
            var baseResult = base.ValidateNodeConfiguration(node);
            if (!baseResult.IsValid) return baseResult;

            var variableName = node.LogicCommand?.GetProperty<string>("VariableName", "");
            if (string.IsNullOrWhiteSpace(variableName))
            {
                return ValidationResult.Error("变量名不能为空");
            }

            return ValidationResult.Success();
        }
    }
}