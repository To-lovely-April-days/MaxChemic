// ILogicCommandPlugin.cs
using System.Windows;
using System.Windows.Controls;
using MaxChemical.Modules.Designer.Models;

namespace MaxChemical.Modules.Designer.Plugins
{
    /// <summary>
    /// 逻辑命令插件接口
    /// </summary>
    public interface ILogicCommandPlugin
    {
        /// <summary>
        /// 插件唯一标识
        /// </summary>
        string Id { get; }

        /// <summary>
        /// 插件名称
        /// </summary>
        string Name { get; }

        /// <summary>
        /// 插件描述
        /// </summary>
        string Description { get; }

        /// <summary>
        /// 插件图标路径
        /// </summary>
        string IconPath { get; }

        /// <summary>
        /// 工具提示文本
        /// </summary>
        string ToolTip { get; }

        /// <summary>
        /// 插件版本
        /// </summary>
        string Version { get; }

        /// <summary>
        /// 插件排序顺序
        /// </summary>
        int Order { get; }

        /// <summary>
        /// 是否启用
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// 插件类别
        /// </summary>
        string Category { get; }

        /// <summary>
        /// 创建工具栏按钮
        /// </summary>
        FrameworkElement CreateToolbarButton();

        /// <summary>
        /// 创建节点用户控件
        /// </summary>
        UserControl CreateNodeControl(CommandNode node);

        /// <summary>
        /// 创建命令节点
        /// </summary>
        CommandNode CreateCommandNode();

        /// <summary>
        /// 验证节点配置
        /// </summary>
        ValidationResult ValidateNodeConfiguration(CommandNode node);

        /// <summary>
        /// 插件初始化
        /// </summary>
        void Initialize();

        /// <summary>
        /// 插件卸载
        /// </summary>
        void Dispose();
    }

    /// <summary>
    /// 验证结果
    /// </summary>
    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }

        public static ValidationResult Success() => new ValidationResult { IsValid = true };
        public static ValidationResult Error(string message) => new ValidationResult { IsValid = false, ErrorMessage = message };
    }
}