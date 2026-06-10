// ILogicCommandPluginManager.cs
using System.Collections.Generic;

namespace MaxChemical.Modules.Designer.Plugins
{
    public interface ILogicCommandPluginManager
    {
        /// <summary>
        /// 获取所有已加载的插件
        /// </summary>
        IEnumerable<ILogicCommandPlugin> GetAllPlugins();

        /// <summary>
        /// 根据ID获取插件
        /// </summary>
        ILogicCommandPlugin GetPlugin(string id);

        /// <summary>
        /// 根据类别获取插件
        /// </summary>
        IEnumerable<ILogicCommandPlugin> GetPluginsByCategory(string category);

        /// <summary>
        /// 加载插件
        /// </summary>
        void LoadPlugins();

        /// <summary>
        /// 注册插件
        /// </summary>
        void RegisterPlugin(ILogicCommandPlugin plugin);

        /// <summary>
        /// 卸载插件
        /// </summary>
        bool UnregisterPlugin(string id);

        /// <summary>
        /// 卸载所有插件
        /// </summary>
        void UnloadAllPlugins();

        /// <summary>
        /// 重新加载插件
        /// </summary>
        void ReloadPlugins();
    }
}