using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MaxChemical.Modules.Designer.Models;
using MaxChemical.Modules.Designer.Service;
using MaxChemical.Modules.Designer.Services;
using MaxChemical.Modules.Designer.ViewModels;

namespace MaxChemical.Modules.Designer.Commands
{
    public class CreateConnectionCommand : ICommand
    {
        private readonly ProcessDesignerViewModel _viewModel;
        private readonly IConnectionService _connectionService;
        private readonly ConnectionInfo _connection;

        public string Description { get; }

        public CreateConnectionCommand(ProcessDesignerViewModel viewModel, IConnectionService connectionService, ConnectionInfo connection)
        {
            _viewModel = viewModel;
            _connectionService = connectionService;
            _connection = connection;
            Description = $"创建连接: {connection.StartDeviceId} → {connection.EndDeviceId}";
        }

        public void Execute()
        {
            if (!_connectionService.Connections.ContainsKey(_connection.Id))
            {
                _connectionService.Connections[_connection.Id] = _connection;
                _connectionService.UpdateConnectionPath(_connection);
            }

            if (!_viewModel.Connections.Contains(_connection))
            {
                _viewModel.Connections.Add(_connection);
            }

            Debug.WriteLine($"重新创建连接: {_connection.Id}");
        }

        public void Undo()
        {
            _connectionService?.RemoveConnection(_connection.Id);

            if (_viewModel.Connections.Contains(_connection))
            {
                _viewModel.Connections.Remove(_connection);
            }

            Debug.WriteLine($"撤销连接: {_connection.Id}");
        }
    }

    // 删除连接命令
    public class DeleteConnectionCommand : ICommand
    {
        private readonly ProcessDesignerViewModel _viewModel;
        private readonly IConnectionService _connectionService;
        private readonly ConnectionInfo _connection;

        public string Description { get; }

        public DeleteConnectionCommand(ProcessDesignerViewModel viewModel, IConnectionService connectionService, ConnectionInfo connection)
        {
            _viewModel = viewModel;
            _connectionService = connectionService;
            _connection = connection;
            Description = $"删除连接: {connection.StartDeviceId} → {connection.EndDeviceId}";
        }

        public void Execute()
        {
            _connectionService?.RemoveConnection(_connection.Id);

            if (_viewModel.Connections.Contains(_connection))
            {
                _viewModel.Connections.Remove(_connection);
            }

            Debug.WriteLine($"删除连接: {_connection.Id}");
        }

        public void Undo()
        {
            if (!_connectionService.Connections.ContainsKey(_connection.Id))
            {
                _connectionService.Connections[_connection.Id] = _connection;
                _connectionService.UpdateConnectionPath(_connection);
            }

            if (!_viewModel.Connections.Contains(_connection))
            {
                _viewModel.Connections.Add(_connection);
            }

            Debug.WriteLine($"恢复连接: {_connection.Id}");
        }
    }
}
