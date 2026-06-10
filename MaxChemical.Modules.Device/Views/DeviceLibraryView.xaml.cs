using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DevicePlugins.Devices;
using MaxChemical.Infrastructure.Events;

namespace MaxChemical.Modules.Device.Views
{
    public partial class DeviceLibraryView : UserControl
    {
        private bool _isDraggingCandidate = false;
        private Point _startPoint;
        private IDevice _draggedDevice = null;

        public DeviceLibraryView()
        {
            InitializeComponent();
            Debug.WriteLine("DeviceLibraryView 构造函数被调用");
        }

        // Preview 事件处理（这些事件更容易触发）
        private void DeviceButton_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                var button = sender as Button;
                if (button?.DataContext is IDevice device)
                {
                    _startPoint = e.GetPosition(button);
                    _draggedDevice = device;
                    _isDraggingCandidate = true;

                    button.CaptureMouse();
                    e.Handled = true;
                }
            }
        }

        private void DeviceButton_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && _isDraggingCandidate && _draggedDevice != null)
            {
                var button = sender as Button;
                if (button != null)
                {
                    Point currentPosition = e.GetPosition(button);

                    if (Math.Abs(currentPosition.X - _startPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                        Math.Abs(currentPosition.Y - _startPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
                    {
                        StartDrag(button, _draggedDevice);
                    }
                }
            }
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
                !_isDraggingCandidate)
            {
                var button = sender as Button;
                if (button != null)
                {
                    Point currentPosition = e.GetPosition(button);

                    if (Math.Abs(currentPosition.X - _startPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                        Math.Abs(currentPosition.Y - _startPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
                    {
                        Debug.WriteLine("满足拖拽条件，开始拖拽");
                        StartDrag(button, _draggedDevice);
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

            if (_isDraggingCandidate)
            {
                _isDraggingCandidate = false;
                Debug.WriteLine("结束拖拽");
            }

            _draggedDevice = null;
        }

        private void StartDrag(Button button, IDevice device)
        {
            _isDraggingCandidate = false;

            if (button.IsMouseCaptured)
            {
                button.ReleaseMouseCapture();
            }

            try
            {
                var dragData = new DataObject();
                dragData.SetData("Device", device);
                dragData.SetData(DataFormats.Text, device.Name);

                // 使用拖拽服务开始拖拽
                if (DataContext is ViewModels.DeviceLibraryViewModel viewModel)
                {
                    viewModel.StartDeviceDrag(device, button, _startPoint);
                }

                var result = DragDrop.DoDragDrop(button, dragData, DragDropEffects.Copy | DragDropEffects.Move);

                Debug.WriteLine($"拖拽操作完成，结果: {result}");

                // 结束拖拽
                if (DataContext is ViewModels.DeviceLibraryViewModel vm)
                {
                    vm.EndDeviceDrag(device, result);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"拖拽操作出现异常: {ex.Message}");
            }
            finally
            {
                _draggedDevice = null;
            }
        }

      
    }
}