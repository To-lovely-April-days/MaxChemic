using Prism.Events;

namespace MaxChemical.Shell.Events
{
    /// <summary>
    /// AI 工具请求切换主界面 Tab。载荷:"flow"=流程设计器 / "process"=工艺设计器。
    /// 由工程专员的工具在 UI 线程发布,MainTabControlViewModel 订阅后切换选中页
    /// (左侧面板与设计器 UI 同步走既有切换逻辑)。
    /// </summary>
    public class TabSwitchRequestEvent : PubSubEvent<string>
    {
    }
}
