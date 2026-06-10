using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace FlowEngine.Core
{
    /// <summary>
    /// 命令结果变量管理器
    /// </summary>
    public class CommandResultVariableManager : IDisposable
    {
        private readonly ConcurrentDictionary<string, CommandResultVariable> _variables = new ConcurrentDictionary<string, CommandResultVariable>();
        private readonly IDeviceService _deviceService;
        private readonly IFlowLogger _logger;
        private readonly FlowContext _context;
        private volatile bool _disposed = false;

        /// <summary>
        /// 变量值变更事件
        /// </summary>
        public event EventHandler<VariableValueChangedEventArgs> VariableChanged;

        /// <summary>
        /// 变量刷新错误事件
        /// </summary>
        public event EventHandler<(string VariableName, Exception Error)> VariableRefreshError;

        public CommandResultVariableManager(IDeviceService deviceService, IFlowLogger logger, FlowContext context)
        {
            _deviceService = deviceService ?? throw new ArgumentNullException(nameof(deviceService));
            _logger = logger;
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        /// <summary>
        /// 注册命令结果变量
        /// </summary>
        public bool RegisterVariable(CommandResultVariableDefinition definition)
        {
            if (definition == null || string.IsNullOrWhiteSpace(definition.VariableName))
            {
                _logger?.LogWarning("注册变量失败：定义为空或变量名为空");
                return false;
            }

            try
            {
                var variable = new CommandResultVariable(definition, _deviceService, _logger);

                variable.ValueChanged += OnVariableValueChanged;
                variable.RefreshError += (sender, error) =>
                    VariableRefreshError?.Invoke(this, (definition.VariableName, error));

                if (_variables.TryAdd(definition.VariableName, variable))
                {
                    _logger?.LogInfo($"命令结果变量注册成功: {definition.VariableName}");

                    if (definition.RefreshStrategy == RefreshStrategy.Automatic)
                    {
                        _ = Task.Run(variable.StartAsync);
                    }

                    return true;
                }
                else
                {
                    variable.Dispose();
                    _logger?.LogWarning($"命令结果变量注册失败，变量名已存在: {definition.VariableName}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"注册命令结果变量时发生错误: {definition.VariableName} - {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 取消注册命令结果变量
        /// </summary>
        public bool UnregisterVariable(string variableName)
        {
            if (string.IsNullOrWhiteSpace(variableName))
                return false;

            if (_variables.TryRemove(variableName, out var variable))
            {
                variable.ValueChanged -= OnVariableValueChanged;
                variable.Dispose();
                _logger?.LogInfo($"命令结果变量已取消注册: {variableName}");
                return true;
            }

            return false;
        }

        /// <summary>
        /// 获取变量值
        /// </summary>
        public object GetVariableValue(string variableName)
        {
            if (_variables.TryGetValue(variableName, out var variable))
            {
                return variable.CurrentValue;
            }
            return null;
        }

        /// <summary>
        /// 手动刷新指定变量
        /// </summary>
        public async Task<bool> RefreshVariableAsync(string variableName)
        {
            if (_variables.TryGetValue(variableName, out var variable))
            {
                return await variable.RefreshValueAsync();
            }
            return false;
        }

        /// <summary>
        /// 手动刷新所有变量
        /// </summary>
        public async Task RefreshAllVariablesAsync()
        {
            var tasks = _variables.Values.Select(v => v.RefreshValueAsync()).ToArray();
            await Task.WhenAll(tasks);
            _logger?.LogInfo("所有命令结果变量刷新完成");
        }

        /// <summary>
        /// 刷新事件驱动的变量
        /// </summary>
        public void RefreshEventDrivenVariables()
        {
            var eventDrivenVariables = _variables.Values
                .Where(v => v.RefreshStrategy == RefreshStrategy.EventDriven)
                .ToList();

            if (eventDrivenVariables.Any())
            {
                _logger?.LogDebug($"刷新 {eventDrivenVariables.Count} 个事件驱动变量");

                foreach (var variable in eventDrivenVariables)
                {
                    _ = Task.Run(variable.RefreshValueAsync);
                }
            }
        }

        /// <summary>
        /// 检查表达式中的按需变量并刷新
        /// </summary>
        public void RefreshOnDemandVariables(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
                return;

            var variableNames = ExtractVariableNames(expression);

            foreach (var varName in variableNames)
            {
                if (_variables.TryGetValue(varName, out var variable) &&
                    variable.RefreshStrategy == RefreshStrategy.OnDemand)
                {
                    _ = Task.Run(variable.RefreshValueAsync);
                }
            }
        }

        /// <summary>
        /// 从表达式中提取变量名
        /// </summary>
        private HashSet<string> ExtractVariableNames(string expression)
        {
            var variableNames = new HashSet<string>();

            var matches = Regex.Matches(expression, @"\{([^}]+)\}");
            foreach (Match match in matches)
            {
                var varName = match.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(varName))
                {
                    variableNames.Add(varName);
                }
            }

            return variableNames;
        }

        /// <summary>
        /// 获取所有变量信息
        /// </summary>
        public List<VariableInfo> GetAllVariableInfo()
        {
            return _variables.Values.Select(v => new VariableInfo
            {
                Name = v.VariableName,
                Value = v.CurrentValue,
                RefreshStrategy = v.RefreshStrategy,
                LastRefreshTime = v.LastRefreshTime,
                IsRefreshing = v.IsRefreshing,
                IsEnabled = v.IsEnabled
            }).ToList();
        }

        /// <summary>
        /// 启动所有自动刷新变量
        /// </summary>
        public async Task StartAllAsync()
        {
            var autoVariables = _variables.Values
                .Where(v => v.RefreshStrategy == RefreshStrategy.Automatic)
                .ToList();

            if (autoVariables.Any())
            {
                _logger?.LogInfo($"启动 {autoVariables.Count} 个自动刷新变量");
                var tasks = autoVariables.Select(v => v.StartAsync()).ToArray();
                await Task.WhenAll(tasks);
            }
        }

        /// <summary>
        /// 停止所有变量
        /// </summary>
        public void StopAll()
        {
            foreach (var variable in _variables.Values)
            {
                variable.Stop();
            }
            _logger?.LogInfo("所有命令结果变量已停止");
        }

        /// <summary>
        /// 变量值变更事件处理
        /// </summary>
        private void OnVariableValueChanged(object sender, VariableValueChangedEventArgs e)
        {
            _context.SetVariable(e.VariableName, e.NewValue);
            VariableChanged?.Invoke(this, e);
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            foreach (var variable in _variables.Values)
            {
                variable.ValueChanged -= OnVariableValueChanged;
                variable.Dispose();
            }

            _variables.Clear();
            VariableChanged = null;
            VariableRefreshError = null;

            _logger?.LogInfo("命令结果变量管理器已释放");
        }
    }
}