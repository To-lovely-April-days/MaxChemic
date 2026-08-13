using System.Windows;
using System.Windows.Controls;
using MaxChemical.Shell.ViewModels;

namespace MaxChemical.Shell.Controls
{
    /// <summary>
    /// 聊天消息模板选择器:按消息形态挑对应的小模板。
    ///
    /// 之前五种形态(用户气泡/助手卡片/工具行/图表卡/确认卡)挤在同一个 DataTemplate 里靠
    /// Visibility 切换,每一行消息都要实例化全部五套控件树,隐藏分支的绑定照样求值——
    /// MarkdownBlock 对每条工具行/用户消息都白解析一遍 Markdown,ChartBlock 对每条文本都
    /// 白解析一遍 JSON。切换会话时整屏容器重建,成本 ×5,肉眼可见地卡。
    /// 拆成按形态选择后,每行只建自己那一套,切换与滚动的布局成本都降到原来的几分之一。
    /// </summary>
    public sealed class ChatTemplateSelector : DataTemplateSelector
    {
        public DataTemplate UserTemplate { get; set; }
        public DataTemplate AssistantTemplate { get; set; }
        public DataTemplate ToolTemplate { get; set; }
        public DataTemplate ChartTemplate { get; set; }
        public DataTemplate ConfirmTemplate { get; set; }
        public DataTemplate ChoiceTemplate { get; set; }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            if (item is ChatItem c)
            {
                if (c.IsUser) return UserTemplate ?? AssistantTemplate;
                if (c.IsToolLine) return ToolTemplate ?? AssistantTemplate;
                if (c.IsChart) return ChartTemplate ?? AssistantTemplate;
                if (c.IsConfirm) return ConfirmTemplate ?? AssistantTemplate;
                if (c.IsChoice) return ChoiceTemplate ?? AssistantTemplate;
                return AssistantTemplate; // IsAssistant 及一切兜底:宁可按助手卡片渲染,不许消息消失
            }
            return base.SelectTemplate(item, container);
        }
    }
}
