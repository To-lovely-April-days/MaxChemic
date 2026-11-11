using System.Windows;
using MaxChemical.Shell.ViewModels;

namespace MaxChemical.Shell.Views
{
    /// <summary>知识库管理窗口:导入文档、查看列表、删除。</summary>
    public partial class KnowledgeBaseWindow : Window
    {
        public KnowledgeBaseWindow(KnowledgeBaseViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;

            // 关闭改为隐藏:窗口实例只创建一次,WPF 的 Window 一旦真正 Close 就不能再 Show,
            // 第二次点「知识库」按钮会因窗口已销毁而打不开(与小桐聊天窗同一处理方式)。
            Closing += (s, e) =>
            {
                e.Cancel = true;
                Hide();
            };
        }
    }
}
