using System.Windows;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class LoadingWindow : Window
    {
        public LoadingWindow()
        {
            InitializeComponent();
            DataContext = this;
        }

        private string _loadingMessage = "正在初始化模型";
        public string LoadingMessage
        {
            get => _loadingMessage;
            set
            {
                _loadingMessage = value;
                Dispatcher.Invoke(() => LoadingText.Text = value);
            }
        }

        private string _loadingSubMessage = "请稍候...";
        public string LoadingSubMessage
        {
            get => _loadingSubMessage;
            set
            {
                _loadingSubMessage = value;
                Dispatcher.Invoke(() => LoadingSubText.Text = value);
            }
        }

        public void UpdateMessage(string message, string subMessage = null)
        {
            LoadingMessage = message;
            if (subMessage != null)
            {
                LoadingSubMessage = subMessage;
            }
        }
    }
}