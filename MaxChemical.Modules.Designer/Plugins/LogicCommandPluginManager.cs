// LogicCommandPluginManager.cs
using System;
using System.Collections.Generic;
using System.Linq;
using MaxChemical.Modules.Designer.Plugins.Commands;
using MaxChemical.Modules.Designer.Plugins.MaxChemical.Modules.Designer.Plugins;
using Serilog;

namespace MaxChemical.Modules.Designer.Plugins
{
    public class LogicCommandPluginManager : ILogicCommandPluginManager
    {
        private readonly Dictionary<string, ILogicCommandPlugin> _plugins = new();
        private bool _isLoaded = false;
        private readonly object _lock = new object();

        public IEnumerable<ILogicCommandPlugin> GetAllPlugins()
        {
            lock (_lock)
            {
                if (!_isLoaded)
                    LoadPlugins();

                return _plugins.Values.Where(p => p.IsEnabled).OrderBy(p => p.Order).ToList();
            }
        }

        public ILogicCommandPlugin GetPlugin(string id)
        {
            lock (_lock)
            {
                _plugins.TryGetValue(id, out var plugin);
                return plugin;
            }
        }

        public IEnumerable<ILogicCommandPlugin> GetPluginsByCategory(string category)
        {
            return GetAllPlugins().Where(p => p.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
        }

        public void LoadPlugins()
        {
            lock (_lock)
            {
                if (_isLoaded) return;

                try
                {
                    // 注册内置插件
                    RegisterBuiltInPlugins();

                    // TODO: 可以在这里添加从外部程序集加载插件的逻辑
                    // LoadExternalPlugins();

                    _isLoaded = true;
                    Log.Information($"已加载 {_plugins.Count} 个逻辑命令插件");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "加载逻辑命令插件失败");
                    throw;
                }
            }
        }

        public void RegisterPlugin(ILogicCommandPlugin plugin)
        {
            if (plugin == null)
                throw new ArgumentNullException(nameof(plugin));

            lock (_lock)
            {
                try
                {
                    if (_plugins.ContainsKey(plugin.Id))
                    {
                        Log.Warning($"插件 {plugin.Id} 已存在，将被替换");
                    }

                    plugin.Initialize();
                    _plugins[plugin.Id] = plugin;
                    Log.Information($"注册逻辑命令插件: {plugin.Name} ({plugin.Id}) v{plugin.Version}");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"注册插件失败: {plugin.Name}");
                    throw;
                }
            }
        }

        public bool UnregisterPlugin(string id)
        {
            lock (_lock)
            {
                if (_plugins.TryGetValue(id, out var plugin))
                {
                    try
                    {
                        plugin.Dispose();
                        _plugins.Remove(id);
                        Log.Information($"卸载插件: {plugin.Name} ({id})");
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"卸载插件失败: {plugin.Name}");
                        return false;
                    }
                }
                return false;
            }
        }

        public void UnloadAllPlugins()
        {
            lock (_lock)
            {
                var pluginList = _plugins.Values.ToList();
                foreach (var plugin in pluginList)
                {
                    try
                    {
                        plugin.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, $"卸载插件失败: {plugin.Name}");
                    }
                }

                _plugins.Clear();
                _isLoaded = false;
                Log.Information("所有逻辑命令插件已卸载");
            }
        }

        public void ReloadPlugins()
        {
            lock (_lock)
            {
                UnloadAllPlugins();
                LoadPlugins();
                Log.Information("插件重新加载完成");
            }
        }

        private void RegisterBuiltInPlugins()
        {
            // 注册内置的逻辑命令插件
            RegisterPlugin(new WaitCommandPlugin());
            RegisterPlugin(new IfCommandPlugin());
            RegisterPlugin(new LoopCommandPlugin());
            RegisterPlugin(new ParallelCommandPlugin());
            RegisterPlugin(new SetVariableCommandPlugin());
            RegisterPlugin(new EndCommandPlugin());
        }
    }
}