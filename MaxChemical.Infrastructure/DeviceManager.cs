using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.IO;
using System.Linq;
using System.Reflection;
using DevicePlugins.Devices;
using MaxChemical.Logging;
using Serilog.Core;


namespace MaxChemical.Infrastructure.Services
{
    public interface IDeviceManager
    {
        IEnumerable<IDevice> GetAllDevices();
        IEnumerable<IDevice> GetDevicesByCategory(string category);
        IEnumerable<string> GetCategories();
        void LoadDevices();
        void UnloadDevices();
    }

    public class DeviceManager : IDeviceManager
    {
        [ImportMany(typeof(IDevice))]
        private IEnumerable<IDevice> _devices = null;

        private CompositionContainer _container;
        private readonly List<IDevice> _loadedDevices = new List<IDevice>();
        private readonly ILogService _logger;
        public DeviceManager()
        {
            _logger = LogManager.GetLogger<DeviceManager>();
        }
        public void LoadDevices()
        {
            
            try
            {
                
                _loadedDevices.Clear();
                _logger.LogInformation("开始加载设备插件...");

                // 获取设备插件目录
                string pluginPath = GetPluginPath();
                if (!Directory.Exists(pluginPath))
                {
                    _logger.LogWarning($"插件目录不存在: {pluginPath}");
                    Directory.CreateDirectory(pluginPath);
                    return;
                }

                _logger.LogInformation($"扫描插件目录: {pluginPath}");

                // 创建MEF容器
                var catalog = new AggregateCatalog();

                // 扫描插件目录中的所有DLL
                foreach (var dll in Directory.GetFiles(pluginPath, "*.dll"))
                {
                    try
                    {
                        _logger.LogInformation($"正在加载插件: {Path.GetFileName(dll)}");
                        var assemblyCatalog = new AssemblyCatalog(dll);

                        // 这一行不是多余的。AssemblyCatalog 的构造是**惰性**的:
                        // 真正去加载程序集、反射类型是在后面 ComposeParts 的时候。
                        // 如果某个插件缺依赖(比如驱动引用了一个没跟着装的 DLL),
                        // 异常会在 ComposeParts 里抛出来 —— 那已经不在这个 try 里了,
                        // 结果是**一个坏插件把全部设备一起拖垮**,而日志上只看到一条
                        // "加载设备插件时发生错误",根本看不出是哪个插件的问题。
                        //
                        // 在这里先摸一下 Parts 逼它当场加载,坏的就地丢掉、记名字,
                        // 其余照常。宁可少一个设备,不能少一整批。
                        _ = assemblyCatalog.Parts.Count();

                        catalog.Catalogs.Add(assemblyCatalog);
                    }
                    catch (ReflectionTypeLoadException ex)
                    {
                        // 缺依赖时最常见的就是它。把内层异常也打出来 ——
                        // 外层消息只会说"无法加载一个或多个请求的类型",毫无信息量,
                        // 内层才会指出到底缺哪个程序集。
                        var detail = string.Join("; ",
                            ex.LoaderExceptions.Where(e => e != null).Select(e => e!.Message).Distinct().Take(5));
                        _logger.LogWarning(
                            $"加载插件失败 {Path.GetFileName(dll)}: 类型加载失败({detail})。" +
                            "通常是该驱动的依赖程序集没有随安装包一起部署。");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"加载插件失败 {Path.GetFileName(dll)}: {ex.Message}");
                    }
                }

                // 也加载当前程序集中的设备
                catalog.Catalogs.Add(new AssemblyCatalog(Assembly.GetExecutingAssembly()));

                _container = new CompositionContainer(catalog);
                _container.ComposeParts(this);

                // 初始化所有设备
                if (_devices != null)
                {
                    foreach (var device in _devices)
                    {
                        try
                        {
                            //device.Initialize();
                            _loadedDevices.Add(device);
                            _logger.LogInformation($"设备加载成功: {device.Name} ({device.Category})");
                        }   
                        catch (Exception ex)
                        {
                            _logger.LogCritical($"设备初始化失败 {device.Name}: {ex.Message}");
                        }
                    }
                }

                _logger.LogInformation($"设备插件加载完成，共加载 {_loadedDevices.Count} 个设备");
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "加载设备插件时发生错误");
            }
        }

        public void UnloadDevices()
        {
            try
            {
                _logger.LogInformation("开始卸载设备插件...");

                foreach (var device in _loadedDevices)
                {
                    try
                    {
                        device.Shutdown();
                        _logger.LogInformation($"设备卸载成功: {device.Name}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogCritical($"设备卸载失败 {device.Name}: {ex.Message}");
                    }
                }

                _loadedDevices.Clear();
                _container?.Dispose();
                _container = null;

                _logger.LogInformation("设备插件卸载完成");
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "卸载设备插件时发生错误");
            }
        }

        public IEnumerable<IDevice> GetAllDevices()
        {
            return _loadedDevices.AsReadOnly();
        }

        public IEnumerable<IDevice> GetDevicesByCategory(string category)
        {
            return _loadedDevices.Where(d => d.Category == category);
        }

        public IEnumerable<string> GetCategories()
        {
            return _loadedDevices
                .Select(d => d.Category)
                .Distinct()
                .OrderBy(c => c);
        }

        private string GetPluginPath()
        {
            // 从应用程序目录下的 Plugins 文件夹加载
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(baseDirectory, "Plugins");
        }
    }
}