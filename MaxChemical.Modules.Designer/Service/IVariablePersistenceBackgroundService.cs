using System;
using System.Threading.Tasks;
using MaxChemical.Data.Services;
using MaxChemical.Data.Models;
using MaxChemical.Logging;
using MaxChemical.Modules.Designer.Event;
using Prism.Events;

namespace MaxChemical.Modules.Designer.Services
{
    public interface IVariablePersistenceBackgroundService
    {
        void StartListening();
        void StopListening();
    }

    public class VariablePersistenceBackgroundService : IVariablePersistenceBackgroundService
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly ICustomVariablePersistenceService _persistenceService;
        private readonly ILogService _logger;
        private SubscriptionToken _variableUpdateToken;
        private bool _isListening = false;

        public VariablePersistenceBackgroundService(
            IEventAggregator eventAggregator,
            ICustomVariablePersistenceService persistenceService,
            ILogService logService)
        {
            _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));
            _persistenceService = persistenceService ?? throw new ArgumentNullException(nameof(persistenceService));
            _logger = logService?.ForContext<VariablePersistenceBackgroundService>() ?? throw new ArgumentNullException(nameof(logService));
        }

        public void StartListening()
        {
            if (_isListening) return;

            try
            {
                _variableUpdateToken = _eventAggregator.GetEvent<VariableUpdatedEvent>()
                    .Subscribe(OnVariableUpdated, ThreadOption.BackgroundThread, true);

                _isListening = true;
                _logger.LogInformation("变量持久化后台服务已启动");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "启动变量持久化后台服务失败");
            }
        }

        public void StopListening()
        {
            if (!_isListening) return;

            try
            {
                if (_variableUpdateToken != null)
                {
                    _eventAggregator.GetEvent<VariableUpdatedEvent>().Unsubscribe(_variableUpdateToken);
                    _variableUpdateToken = null;
                }

                _isListening = false;
                _logger.LogInformation("变量持久化后台服务已停止");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "停止变量持久化后台服务失败");
            }
        }

        private async void OnVariableUpdated(VariableUpdatedEventArgs args)
        {
            try
            {
                _logger.LogDebug("后台服务处理变量更新: {Name} = {Value} ({Type})",
                    args.VariableName, args.VariableValue, args.VariableType);

                // 检查变量是否已存在
                var existingVariable = await _persistenceService.GetCustomVariableAsync(args.VariableName);

                if (existingVariable != null)
                {
                    // 更新现有变量
                    existingVariable.Value = args.VariableValue;
                    existingVariable.Type = args.VariableType;
                    existingVariable.LastModifiedTime = args.UpdateTime;
                    existingVariable.IsFromExecutionContext = true;

                    bool success = await _persistenceService.UpdateCustomVariableAsync(existingVariable);
                    if (success)
                    {
                        _logger.LogDebug("变量已更新到数据库: {Name} = {Value}", args.VariableName, args.VariableValue);
                    }
                    else
                    {
                        _logger.LogWarning("更新变量到数据库失败: {Name}", args.VariableName);
                    }
                }
                else
                {
                    // 创建新变量
                    var newVariable = new CustomVariableData
                    {
                        Name = args.VariableName,
                        Value = args.VariableValue,
                        Type = args.VariableType,
                        IsFromExecutionContext = true,
                        Category = "Execution",
                        Description = "执行上下文创建的变量",
                        CreatedTime = args.UpdateTime,
                        LastModifiedTime = args.UpdateTime
                    };

                    int newId = await _persistenceService.SaveCustomVariableAsync(newVariable);
                    _logger.LogDebug("新变量已保存到数据库: {Name} = {Value}, ID: {Id}",
                        args.VariableName, args.VariableValue, newId);
                }

                // 发布变量已持久化事件（可选，用于通知UI更新）
                _eventAggregator.GetEvent<VariablePersistedEvent>()?.Publish(new VariablePersistedEventArgs
                {
                    VariableName = args.VariableName,
                    VariableValue = args.VariableValue,
                    VariableType = args.VariableType,
                    UpdateTime = args.UpdateTime,
                    IsNewVariable = existingVariable == null
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "后台处理变量更新失败: {Name}", args.VariableName);
            }
        }
    }
}