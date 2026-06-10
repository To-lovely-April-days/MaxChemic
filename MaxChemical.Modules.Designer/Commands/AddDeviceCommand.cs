using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Service;
using MaxChemical.Modules.Designer.Services;
using MaxChemical.Modules.Designer.ViewModels;

namespace MaxChemical.Modules.Designer.Commands
{
    // 添加设备命令
    public class AddDeviceCommand : ICommand
    {
        private readonly ProcessDesignerViewModel _viewModel;
        private readonly CanvasDeviceViewModel _deviceViewModel;
       

        public string Description { get; }

        public AddDeviceCommand(ProcessDesignerViewModel viewModel, CanvasDeviceViewModel deviceViewModel)
        {
            _viewModel = viewModel;
            _deviceViewModel = deviceViewModel;
          
            Description = $"添加设备: {deviceViewModel.Device?.Name}";
        }

        public void Execute()
        {
            if (!_viewModel.CanvasDevices.Contains(_deviceViewModel))
            {
                _viewModel.CanvasDevices.Add(_deviceViewModel);
                Debug.WriteLine($"重新添加设备: {_deviceViewModel.Device?.Name}");
            }
        }

        public void Undo()
        {
            if (_viewModel.CanvasDevices.Contains(_deviceViewModel))
            {
                // 删除相关连接
                //var relatedConnections = _connectionService?.Connections?.Values
                //    .Where(c => c.StartDeviceId == _deviceViewModel.DeviceId || c.EndDeviceId == _deviceViewModel.DeviceId)
                //    .ToList();

                //foreach (var connection in relatedConnections ?? new System.Collections.Generic.List<ConnectionInfo>())
                //{
                //    _connectionService?.RemoveConnection(connection.Id);
                //}

                _viewModel.CanvasDevices.Remove(_deviceViewModel);
                Debug.WriteLine($"撤销添加设备: {_deviceViewModel.Device?.Name}");
            }
        }
    }

    // 删除设备命令
    public class DeleteDeviceCommand : ICommand
    {
        private readonly ProcessDesignerViewModel _viewModel;
        private readonly CanvasDeviceViewModel _deviceViewModel;
     
        private readonly System.Collections.Generic.List<ConnectionInfo> _deletedConnections;

        public string Description { get; }

        public DeleteDeviceCommand(ProcessDesignerViewModel viewModel, CanvasDeviceViewModel deviceViewModel)
        {
            _viewModel = viewModel;
            _deviceViewModel = deviceViewModel;
           
            _deletedConnections = new System.Collections.Generic.List<ConnectionInfo>();
            Description = $"删除设备: {deviceViewModel.Device?.Name}";

            //// 保存要删除的连接
            //var relatedConnections = _connectionService?.Connections?.Values
            //    .Where(c => c.StartDeviceId == deviceViewModel.DeviceId || c.EndDeviceId == deviceViewModel.DeviceId)
            //    .ToList();

            //if (relatedConnections != null)
            //{
            //    _deletedConnections.AddRange(relatedConnections);
            //}
        }

        public void Execute()
        {
            //// 删除相关连接
            //foreach (var connection in _deletedConnections)
            //{
            //    _connectionService?.RemoveConnection(connection.Id);
            //}

            // 删除设备
            if (_viewModel.CanvasDevices.Contains(_deviceViewModel))
            {
                _viewModel.CanvasDevices.Remove(_deviceViewModel);
                Debug.WriteLine($"删除设备: {_deviceViewModel.Device?.Name}");
            }
        }

        public void Undo()
        {
            // 恢复设备
            if (!_viewModel.CanvasDevices.Contains(_deviceViewModel))
            {
                _viewModel.CanvasDevices.Add(_deviceViewModel);
                Debug.WriteLine($"恢复设备: {_deviceViewModel.Device?.Name}");
            }

            //// 恢复连接
            //foreach (var connection in _deletedConnections)
            //{
            //    if (!_connectionService.Connections.ContainsKey(connection.Id))
            //    {
            //        _connectionService.Connections[connection.Id] = connection;
            //        // 重新创建视觉元素
            //        _connectionService.UpdateConnectionPath(connection);
            //    }
            //}
        }
    }

    // 移动设备命令
    public class MoveDeviceCommand : ICommand
    {
        private readonly CanvasDeviceViewModel _deviceViewModel;
        private readonly Point _oldPosition;
        private readonly Point _newPosition;
        private readonly Action<string, Point> _updateVisualPosition; // 新增

        public string Description { get; }

        public MoveDeviceCommand(CanvasDeviceViewModel deviceViewModel, Point oldPosition, Point newPosition, Action<string, Point> updateVisualPosition)
        {
            _deviceViewModel = deviceViewModel;
            _oldPosition = oldPosition;
            _newPosition = newPosition;
            _updateVisualPosition = updateVisualPosition; // 新增
            Description = $"移动设备: {deviceViewModel.Device?.Name}";
        }

        public void Execute()
        {
            _deviceViewModel.Position = _newPosition;

            // 🔥 新增：更新视觉位置
            _updateVisualPosition?.Invoke(_deviceViewModel.DeviceId, _newPosition);

            Debug.WriteLine($"移动设备到: ({_newPosition.X:F1}, {_newPosition.Y:F1})");
        }

        public void Undo()
        {
            _deviceViewModel.Position = _oldPosition;

            // 🔥 新增：更新视觉位置
            _updateVisualPosition?.Invoke(_deviceViewModel.DeviceId, _oldPosition);

            Debug.WriteLine($"恢复设备位置到: ({_oldPosition.X:F1}, {_oldPosition.Y:F1})");
        }
    }

    // 缩放设备命令
    public class ScaleDeviceCommand : ICommand
    {
        private readonly string _deviceId;
        private readonly double _oldScaleX;
        private readonly double _oldScaleY;
        private readonly double _newScaleX;
        private readonly double _newScaleY;
        private readonly Action<string, double, double> _applyScale;

        public string Description { get; }

        public ScaleDeviceCommand(string deviceId, double oldScaleX, double oldScaleY, double newScaleX, double newScaleY, Action<string, double, double> applyScale, string deviceName)
        {
            _deviceId = deviceId;
            _oldScaleX = oldScaleX;
            _oldScaleY = oldScaleY;
            _newScaleX = newScaleX;
            _newScaleY = newScaleY;
            _applyScale = applyScale;
            Description = $"缩放设备: {deviceName}";
        }

        public void Execute()
        {
            _applyScale?.Invoke(_deviceId, _newScaleX, _newScaleY);
            Debug.WriteLine($"缩放设备到: {_newScaleX:F2}x{_newScaleY:F2}");
        }

        public void Undo()
        {
            _applyScale?.Invoke(_deviceId, _oldScaleX, _oldScaleY);
            Debug.WriteLine($"恢复设备缩放到: {_oldScaleX:F2}x{_oldScaleY:F2}");
        }
    }
}
