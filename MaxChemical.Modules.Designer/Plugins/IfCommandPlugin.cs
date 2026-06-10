using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MaxChemical.Modules.Designer.Plugins
{
    using System.Windows;
    using System.Windows.Controls;
    using global::MaxChemical.Modules.Designer.Models;
    using global::MaxChemical.Modules.Designer.Views;

    namespace MaxChemical.Modules.Designer.Plugins
    {
        public class IfCommandPlugin : LogicCommandPluginBase
        {
            public override string Id => "If";
            public override string Name => "逻辑判断";
            public override string Description => "根据条件执行不同分支";
            public override string IconPath => "pack://siteoforigin:,,,/Resources/CommadIcon/if.png";
            public override string Bezel_lessIconPath => "pack://siteoforigin:,,,/Resources/CommadIcon/Bezel-lessIF.png";
            public override string ToolTip => "根据条件判断执行TRUE或FALSE分支";
            public override int Order => 2;

            public override FrameworkElement CreateToolbarButton()
            {
                return CreateStandardToolbarButton();
            }

            public override UserControl CreateNodeControl(CommandNode node)
            {
                return new IfNodeControlView { DataContext = node };
            }

            public override CommandNode CreateCommandNode()
            {
                var logicCommand = new LogicCommand(Name, Description, IconPath, LogicCommandType.If);

                // 设置默认属性
                var properties = new IfCommandProperties();
                logicCommand.SetProperty("Bezel_lessIconPath", properties.Bezel_lessIconPath);
                logicCommand.SetProperty("LeftOperand", properties.LeftOperand);
                logicCommand.SetProperty("Operator", properties.Operator);
                logicCommand.SetProperty("RightOperand", properties.RightOperand);
                logicCommand.SetProperty("Description", properties.Description);

                return new CommandNode(logicCommand);
            }

            public override ValidationResult ValidateNodeConfiguration(CommandNode node)
            {
                var baseResult = base.ValidateNodeConfiguration(node);
                if (!baseResult.IsValid) return baseResult;

                var leftOperand = node.LogicCommand?.GetProperty<string>("LeftOperand", "");
                var rightOperand = node.LogicCommand?.GetProperty<string>("RightOperand", "");

                if (string.IsNullOrWhiteSpace(leftOperand))
                {
                    return ValidationResult.Error("左操作数不能为空");
                }

                if (string.IsNullOrWhiteSpace(rightOperand))
                {
                    return ValidationResult.Error("右操作数不能为空");
                }

                return ValidationResult.Success();
            }
        }
    }
}
