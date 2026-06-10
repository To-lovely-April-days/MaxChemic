using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MaxChemical.Shell.ViewModels;

namespace MaxChemical.Shell.Views
{
    public partial class CustomTitleBar : UserControl
    {
        public CustomTitleBar()
        {
            InitializeComponent();
            SetupDragHandlers();
        }

        private void SetupDragHandlers()
        {
            // 为拖动区域设置事件
            DragArea.MouseLeftButtonDown += OnMouseLeftButtonDown;

            // 为整个标题栏设置事件（排除按钮）
            this.MouseLeftButtonDown += OnTitleBarMouseLeftButtonDown;
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            HandleMouseDown(e);
        }

        private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 检查是否点击在按钮上
            if (IsClickOnButton(e.OriginalSource))
            {
                return; // 如果点击按钮，不处理拖动
            }

            HandleMouseDown(e);
        }

        private void HandleMouseDown(MouseButtonEventArgs e)
        {
            var window = Window.GetWindow(this);
            if (window == null) return;
            if (e.ClickCount == 1)
            {
                // 单击开始拖动
                StartDrag(window, e);
            }
        }

        private void StartDrag(Window window, MouseButtonEventArgs e)
        {
            try
            {
                // 计算还原后的鼠标位置
                var mousePos = e.GetPosition(this);
                var screenPos = this.PointToScreen(mousePos);

                // 还原窗口
                window.WindowState = WindowState.Normal;
                // 通过 ViewModel 执行最大化/还原逻辑
                var viewModel = this.DataContext as CustomTitleBarViewModel;
                viewModel.IsWindowMinimized = false;
                viewModel.MaximizeCommandNoPostion.Execute();
                
                // 开始拖动
                window.DragMove();
            }
            catch (InvalidOperationException)
            {
                // 当鼠标没有按下时调用 DragMove 会抛出异常
                System.Diagnostics.Debug.WriteLine("DragMove 调用时鼠标未按下");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"拖动异常: {ex.Message}");
            }
        }
        private bool IsClickOnButton(object source)
        {
            var element = source as DependencyObject;

            while (element != null)
            {
                if (element is Button)
                    return true;

                element = System.Windows.Media.VisualTreeHelper.GetParent(element);
            }

            return false;
        }
    }
}