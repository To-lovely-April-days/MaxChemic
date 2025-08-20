using System;
using System.Threading;
using System.Windows;

namespace MaxChemical.Modules.Designer.Views
{
    public partial class ConditionMetDialog : Window
    {
        private readonly CancellationTokenSource _cancellationTokenSource;
        private bool _isContinue = false;
        private bool _isStopped = false;

        public bool IsContinue => _isContinue;
        public bool IsStopped => _isStopped;
        public CancellationToken CancellationToken => _cancellationTokenSource.Token;

        public ConditionMetDialog(string conditionExpression, bool isAsyncMode = false)
        {
            InitializeComponent();

            _cancellationTokenSource = new CancellationTokenSource();

            // 设置条件表达式
            ConditionText.Text = conditionExpression;

            // 设置执行模式显示
            ExecutionModeText.Text = isAsyncMode ? "异步执行模式" : "同步执行模式";

            // 根据执行模式调整说明文本
            if (isAsyncMode)
            {
                HelpText.Text = "• 继续：条件已满足，流程将继续执行下一步骤\n• 停止：终止整个流程的执行";
            }
            else
            {
                HelpText.Text = "• 继续：条件已满足，流程将继续执行下一步骤\n• 停止：终止整个流程的执行";
            }
        }

        private void ContinueButton_Click(object sender, RoutedEventArgs e)
        {
            _isContinue = true;
            DialogResult = true;
            Close();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _isStopped = true;
            _cancellationTokenSource.Cancel();
            DialogResult = false;
            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _cancellationTokenSource?.Dispose();
            base.OnClosed(e);
        }
    }
}