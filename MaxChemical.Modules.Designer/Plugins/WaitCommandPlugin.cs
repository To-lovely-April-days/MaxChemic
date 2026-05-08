using System.Windows;
using System.Windows.Controls;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Views;

namespace MaxChemical.Modules.Designer.Plugins.Commands
{
    public class WaitCommandPlugin : LogicCommandPluginBase
    {
        public override string Id => "Wait";
        public override string Name => "等待";
        public override string Description => "暂停执行指定时间或等待条件满足";
        public override string IconPath => "pack://siteoforigin:,,,/Resources/CommadIcon/wait.png";
        public override string Bezel_lessIconPath => "pack://siteoforigin:,,,/Resources/CommadIcon/Bezel-lessDelay.png";
        public override string ToolTip => "等待指定时间或等待条件满足后继续执行";
        public override int Order => 1;

        public override FrameworkElement CreateToolbarButton()
        {
            return CreateStandardToolbarButton();
        }

        public override UserControl CreateNodeControl(CommandNode node)
        {
            return new WaitNodeControlView { DataContext = node };
        }

        public override CommandNode CreateCommandNode()
        {
            var logicCommand = new LogicCommand(Name, Description, IconPath, LogicCommandType.Wait);

            // 设置默认属性
            var properties = new WaitCommandProperties();

            // 固定时间等待属性
            logicCommand.SetProperty("WaitTime", properties.WaitTime);
            logicCommand.SetProperty("TimeUnit", properties.TimeUnit);

            // 条件等待属性
            logicCommand.SetProperty("ConditionLeftVariable", properties.ConditionLeftVariable);
            logicCommand.SetProperty("ConditionOperator", properties.ConditionOperator);
            logicCommand.SetProperty("ConditionRightValue", properties.ConditionRightValue);
            logicCommand.SetProperty("TimeoutSeconds", properties.TimeoutSeconds);
            logicCommand.SetProperty("CheckIntervalSeconds", properties.CheckIntervalSeconds);

            // 新增属性
            logicCommand.SetProperty("ExecutionMode", properties.ExecutionMode);
            logicCommand.SetProperty("IsWarning", properties.IsWarning);
            // 通用属性
            logicCommand.SetProperty("Description", properties.Description);
            logicCommand.SetProperty("Bezel_lessIconPath", Bezel_lessIconPath);
            //  新增：等待稳定相关
            logicCommand.SetProperty("WaitForStability", properties.WaitForStability);
            logicCommand.SetProperty("StabilityDeviceIds", properties.StabilityDeviceIds);
            logicCommand.SetProperty("StabilityTimeoutSeconds", properties.StabilityTimeoutSeconds);
            return new CommandNode(logicCommand);
        }

        public override ValidationResult ValidateNodeConfiguration(CommandNode node)
        {
            var baseResult = base.ValidateNodeConfiguration(node);
            if (!baseResult.IsValid) return baseResult;

            var condition = node.LogicCommand?.GetProperty<string>("Condition", "");

            // 判断是固定时间等待还是条件等待
            if (string.IsNullOrWhiteSpace(condition))
            {
                // 固定时间等待验证
                var waitTime = node.LogicCommand?.GetProperty<double>("WaitTime", 0);
                if (waitTime <= 0)
                {
                    return ValidationResult.Error("等待时间必须大于0");
                }
            }
            else
            {
                // 条件等待验证
                var timeoutSeconds = node.LogicCommand?.GetProperty<double>("TimeoutSeconds", 0);
                if (timeoutSeconds <= 0)
                {
                    return ValidationResult.Error("超时时间必须大于0");
                }

                var checkInterval = node.LogicCommand?.GetProperty<double>("CheckIntervalSeconds", 0);
                if (checkInterval <= 0)
                {
                    return ValidationResult.Error("检查间隔必须大于0");
                }
            }

            return ValidationResult.Success();
        }
    }
}