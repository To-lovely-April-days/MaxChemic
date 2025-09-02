using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DevicePlugins.Devices;
using System.Windows;
using MaxChemical.Core;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.Designer.Views;
using System.Windows.Media;
using System.Windows.Input;

namespace MaxChemical.Modules.Designer.Models
{
    // 在包含RegionSettingsControl的程序集中
    public class RegionSettingsDialogProvider : IRegionSettingsDialogProvider
    {
        public bool? ShowDialog(RegionLayout currentLayout, Func<(double Width, double Height)> getCanvasSize, out RegionLayout result)
        {
            result = null;
            RegionLayout tempResult = null; // 使用临时变量

            // 获取画布尺寸来设置窗口大小
            var (canvasWidth, canvasHeight) = getCanvasSize();

            var window = new Window
            {
                WindowStyle = WindowStyle.None, // 移除窗口标题栏
                ResizeMode = ResizeMode.NoResize, // 禁止调整大小，因为没有标题栏
                Width = canvasWidth,
                Height = canvasHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                AllowsTransparency = false,
                Background = Brushes.Transparent
            };

            var control = new RegionSettingsControl(currentLayout);
            control.SetCanvasSizeProvider(getCanvasSize);

            bool? dialogResult = null;

            control.ViewModel.DialogResult += (success) =>
            {
                if (success)
                {
                    tempResult = control.GetResult(); // 赋值给临时变量
                    dialogResult = true;
                }
                else
                {
                    dialogResult = false;
                }
                window.DialogResult = dialogResult;
            };

            window.Closing += (sender, e) =>
            {
                if (dialogResult == null)
                {
                    dialogResult = false;
                }
            };

            window.Content = control;

            if (Application.Current.MainWindow != null && Application.Current.MainWindow.IsLoaded)
            {
                window.Owner = Application.Current.MainWindow;
            }

            try
            {
                window.ShowDialog();
                result = tempResult; // 在方法结束前赋值给out参数
                return dialogResult;
            }
            catch (Exception ex)
            {
                // 可以在这里添加日志记录
                // LogManager.Instance?.Error("RegionDialog", $"显示区域设置对话框时出错: {ex.Message}");
                result = null;
                return false;
            }
        }

        private Window _currentWindow;

        public void ShowWindow(RegionLayout currentLayout,Func<(double Width, double Height)> getDialogSize,Func<(double Width, double Height)> getCanvasSize,Action<RegionLayout> onCompleted = null)
        {
            // 如果已经有窗口打开，先关闭它
            if (_currentWindow != null && _currentWindow.IsLoaded)
            {
                _currentWindow.Close();
            }

            // 获取对话框窗口尺寸
            var (dialogWidth, dialogHeight) = getDialogSize();

            // 为阴影预留空间
            var shadowMargin = 50;
            var windowWidth = dialogWidth + shadowMargin * 2;
            var windowHeight = dialogHeight + shadowMargin * 2;

            _currentWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Width = windowWidth,
                Height = windowHeight,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = true,
                Topmost = false,
                AllowsTransparency = true,
                Background = Brushes.Transparent
            };

            var control = new RegionSettingsControl(currentLayout);
            // 🔥 传入画布尺寸获取委托
            control.SetCanvasSizeProvider(getCanvasSize);

            // 设置控件边距来为阴影留出空间
            control.Margin = new Thickness(shadowMargin);

            control.ViewModel.DialogResult += (success) =>
            {
                if (success)
                {
                    var result = control.GetResult();
                    onCompleted?.Invoke(result);
                }
                _currentWindow.Close();
            };

            _currentWindow.Closing += (sender, e) =>
            {
                _currentWindow = null;
            };

            control.DragArea.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Left)
                {
                    try
                    {
                        _currentWindow.DragMove();
                    }
                    catch
                    {
                        // 忽略异常
                    }
                }
            };

            _currentWindow.Content = control;

            try
            {
                _currentWindow.Show();
            }
            catch (Exception ex)
            {
                _currentWindow = null;
            }
        }
    }
    public class ProjectNameDialogProvider : IProjectNameDialogProvider
    {
        private Window _currentWindow;

        public void ShowDialog(string defaultName = null, Action<string> onCompleted = null)
        {
            // 如果已经有窗口打开，先关闭它
            if (_currentWindow != null && _currentWindow.IsLoaded)
            {
                _currentWindow.Close();
            }

            // 设置对话框窗口尺寸
            var dialogWidth = 450;
            var dialogHeight = 300;

            // 为阴影预留空间
            var shadowMargin = 20;
            var windowWidth = dialogWidth + shadowMargin * 2;
            var windowHeight = dialogHeight + shadowMargin * 2;

            _currentWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                Width = windowWidth,
                Height = windowHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Topmost = true,
                AllowsTransparency = true,
                Background = Brushes.Transparent
            };

            // 设置默认名称
            var projectName = defaultName ?? $"新建项目_{DateTime.Now:yyyyMMdd_HHmmss}";

            var control = new ProjectNameInputDialogControl(projectName);

            // 设置控件边距来为阴影留出空间
            control.Margin = new Thickness(shadowMargin);

            // 处理完成事件
            control.ViewModel.OnConfirmed += (name) =>
            {
                onCompleted?.Invoke(name);
                _currentWindow.Close();
            };

            control.ViewModel.OnCancelled += () =>
            {
                onCompleted?.Invoke(null);
                _currentWindow.Close();
            };

            _currentWindow.Closing += (sender, e) =>
            {
                _currentWindow = null;
            };

            // 支持拖拽移动窗口 - 使用修改后的方法名
            var dragArea = control.GetDragArea();
            dragArea.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ChangedButton == MouseButton.Left)
                {
                    try
                    {
                        _currentWindow.DragMove();
                    }
                    catch
                    {
                        // 忽略异常
                    }
                }
            };

            // 支持键盘事件
            _currentWindow.KeyDown += (s, e) =>
            {
                switch (e.Key)
                {
                    case Key.Enter:
                        if (control.ViewModel.ConfirmCommand.CanExecute())
                        {
                            control.ViewModel.ConfirmCommand.Execute();
                        }
                        e.Handled = true;
                        break;
                    case Key.Escape:
                        control.ViewModel.CancelCommand.Execute();
                        e.Handled = true;
                        break;
                }
            };

            _currentWindow.Content = control;

            // 设置窗口所有者
            if (Application.Current.MainWindow != null && Application.Current.MainWindow.IsLoaded)
            {
                _currentWindow.Owner = Application.Current.MainWindow;
            }

            try
            {
                _currentWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                _currentWindow = null;
                // 可以添加日志记录
                System.Diagnostics.Debug.WriteLine($"显示项目名称输入对话框时出错: {ex.Message}");
            }
        }
    }

}