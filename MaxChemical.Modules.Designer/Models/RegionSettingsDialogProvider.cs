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
}