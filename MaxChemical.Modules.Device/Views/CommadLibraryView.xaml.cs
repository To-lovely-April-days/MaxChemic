using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using DevicePlugins.Devices;
using MaxChemical.DataModule;
using MaxChemical.DataModule.Model;
using MaxChemical.Infrastructure.Events;

namespace MaxChemical.Modules.Device.Views
{
    /// <summary>
    /// CommadLibraryView.xaml 的交互逻辑
    /// </summary>
    public partial class CommadLibraryView : UserControl
    {
        private bool _isDragging = false;
        private Point _startPoint;
        private IDevice _draggedDevice = null;
        private DeviceCommand _draggedCommand = null;

        public CommadLibraryView()
        {
            InitializeComponent();
        }

        #region 设备拖拽相关代码（保持不变）
        private void DeviceButton_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            Debug.WriteLine("PreviewMouseDown 事件触发");
            DeviceButton_MouseDown(sender, e);
        }

        private void DeviceButton_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            DeviceButton_MouseMove(sender, e);
        }

        private void DeviceButton_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            DeviceButton_MouseUp(sender, e);
        }

        private void DeviceButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Debug.WriteLine("DeviceButton_MouseDown 事件触发");

            if (e.LeftButton == MouseButtonState.Pressed)
            {
                var button = sender as Button;
                if (button?.DataContext is IDevice device)
                {
                    _startPoint = e.GetPosition(button);
                    _draggedDevice = device;
                    Debug.WriteLine($"鼠标按下，准备拖拽设备: {device.Name}");

                    // 捕获鼠标，确保能接收到后续事件
                    button.CaptureMouse();

                    // 阻止事件冒泡，防止按钮的 Click 事件触发
                    e.Handled = true;
                }
            }
        }

        private void DeviceButton_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed &&
                _draggedDevice != null &&
                !_isDragging)
            {
                var button = sender as Button;
                if (button != null)
                {
                    Point currentPosition = e.GetPosition(button);

                    if (Math.Abs(currentPosition.X - _startPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                        Math.Abs(currentPosition.Y - _startPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
                    {
                        Debug.WriteLine("满足拖拽条件，开始拖拽设备");
                        StartDeviceDrag(button, _draggedDevice);
                    }
                }
            }
        }

        private void DeviceButton_MouseUp(object sender, MouseButtonEventArgs e)
        {
            Debug.WriteLine("DeviceButton_MouseUp 事件触发");

            var button = sender as Button;
            if (button != null && button.IsMouseCaptured)
            {
                button.ReleaseMouseCapture();
            }

            if (_isDragging)
            {
                _isDragging = false;
                Debug.WriteLine("结束拖拽");
            }

            _draggedDevice = null;
        }

        private void StartDeviceDrag(Button button, IDevice device)
        {
            _isDragging = true;
            Debug.WriteLine($"开始拖拽设备: {device.Name}");

            // 释放鼠标捕获
            if (button.IsMouseCaptured)
            {
                button.ReleaseMouseCapture();
            }

            try
            {
                var dragData = new DataObject();
                dragData.SetData("Device", device);
                dragData.SetData(DataFormats.Text, device.Name);

                var dragInfo = new DeviceDragInfo
                {
                    Device = device,
                    SourceControl = button,
                    DragStartTime = DateTime.Now
                };
                dragData.SetData("DeviceDragInfo", dragInfo);

                var result = DragDrop.DoDragDrop(button, dragData, DragDropEffects.Copy | DragDropEffects.Move);

                Debug.WriteLine($"拖拽操作完成，结果: {result}");

                // 通过 ViewModel 发布事件
                if (DataContext is ViewModels.DeviceLibraryViewModel viewModel)
                {
                    viewModel.PublishDragCompleteEvent(device, result);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"拖拽操作出现异常: {ex.Message}");
            }
            finally
            {
                _isDragging = false;
                _draggedDevice = null;
            }
        }
        #endregion

        #region 命令拖拽相关代码
        private void CommandButton_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            Debug.WriteLine("Command PreviewMouseDown 事件触发");
            CommandButton_MouseDown(sender, e);
        }

        private void CommandButton_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            CommandButton_MouseMove(sender, e);
        }

        private void CommandButton_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            CommandButton_MouseUp(sender, e);
        }

        private void CommandButton_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Debug.WriteLine("CommandButton_MouseDown 事件触发");

            if (e.LeftButton == MouseButtonState.Pressed)
            {
                var button = sender as Button;
                if (button?.DataContext is DeviceCommand command)
                {
                    _startPoint = e.GetPosition(button);
                    _draggedCommand = command;
                    Debug.WriteLine($"鼠标按下，准备拖拽命令: {command.Name}");

                    // 捕获鼠标，确保能接收到后续事件
                    button.CaptureMouse();

                    // 阻止事件冒泡，防止按钮的 Click 事件触发
                    e.Handled = true;
                }
            }
        }

        private void CommandButton_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed &&
                _draggedCommand != null &&
                !_isDragging)
            {
                var button = sender as Button;
                if (button != null)
                {
                    Point currentPosition = e.GetPosition(button);

                    if (Math.Abs(currentPosition.X - _startPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                        Math.Abs(currentPosition.Y - _startPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
                    {
                        Debug.WriteLine("满足拖拽条件，开始拖拽命令");
                        StartCommandDrag(button, _draggedCommand);
                    }
                }
            }
        }

        private void CommandButton_MouseUp(object sender, MouseButtonEventArgs e)
        {
            Debug.WriteLine("CommandButton_MouseUp 事件触发");

            var button = sender as Button;
            if (button != null && button.IsMouseCaptured)
            {
                button.ReleaseMouseCapture();
            }

            if (_isDragging)
            {
                _isDragging = false;
                Debug.WriteLine("结束命令拖拽");
            }

            _draggedCommand = null;
        }

        private void StartCommandDrag(Button button, DeviceCommand command)
        {
            _isDragging = true;
            Debug.WriteLine($"开始拖拽命令: {command.Name}");

            // 释放鼠标捕获
            if (button.IsMouseCaptured)
            {
                button.ReleaseMouseCapture();
            }

            try
            {
                var dragData = new DataObject();
                dragData.SetData("DeviceCommand", command);
                dragData.SetData(DataFormats.Text, command.Name);

                var dragInfo = new CommandDragInfo
                {
                    Command = command,
                    SourceControl = button,
                    DragStartTime = DateTime.Now
                };
                dragData.SetData("CommandDragInfo", dragInfo);

                var result = DragDrop.DoDragDrop(button, dragData, DragDropEffects.Copy | DragDropEffects.Move);

                Debug.WriteLine($"命令拖拽操作完成，结果: {result}");

                // 通过 ViewModel 发布事件
                if (DataContext is ViewModels.CommadLibraryViewModel viewModel)
                {
                    viewModel.PublishCommandDragCompleteEvent(command, result);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"命令拖拽操作出现异常: {ex.Message}");
            }
            finally
            {
                _isDragging = false;
                _draggedCommand = null;
            }
        }
        #endregion
    }

  
}