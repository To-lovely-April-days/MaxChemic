// 新建文件: Services/PythonEnvironmentManager.cs
using Python.Runtime;
using MaxChemical.Logging;
using System;

namespace MaxChemical.Modules.Designer.Services
{
    /// <summary>
    /// Python环境全局管理器 - 单例模式
    /// </summary>
    public sealed class PythonEnvironmentManager : IDisposable
    {
        private static readonly Lazy<PythonEnvironmentManager> _instance =
            new Lazy<PythonEnvironmentManager>(() => new PythonEnvironmentManager());

        private readonly ILogService _logger;
        private bool _isInitialized;
        private readonly object _lockObject = new object();

        public static PythonEnvironmentManager Instance => _instance.Value;

        public bool IsInitialized => _isInitialized;

        private PythonEnvironmentManager()
        {
            _logger = new LogService().ForContext<PythonEnvironmentManager>();
        }

        /// <summary>
        /// 初始化 Python 环境（全局只初始化一次）
        /// </summary>
        public bool Initialize()
        {
            lock (_lockObject)
            {
                if (_isInitialized)
                {
                    _logger.LogInformation("Python环境已初始化，跳过重复初始化");
                    return true;
                }

                try
                {
                    _logger.LogInformation("开始初始化全局Python环境");

                    Runtime.PythonDLL = @"D:\Python\Python313\python313.dll";

                    if (!PythonEngine.IsInitialized)
                    {
                        PythonEngine.Initialize();
                        _logger.LogInformation("PythonEngine初始化成功");
                    }

                    using (Py.GIL())
                    {
                        dynamic sys = Py.Import("sys");
                        sys.path.append(@".\");
                    }

                    _isInitialized = true;
                    _logger.LogInformation("全局Python环境初始化完成");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Python环境初始化失败");
                    _isInitialized = false;
                    return false;
                }
            }
        }

        /// <summary>
        /// 创建模型实例
        /// </summary>
        public dynamic CreateModel()
        {
            if (!_isInitialized)
            {
                throw new InvalidOperationException("Python环境未初始化");
            }

            try
            {
                using (Py.GIL())
                {
                    dynamic nitroModule = Py.Import("nitro_reduction_modely");
                    return nitroModule.create_model();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建Python模型失败");
                throw;
            }
        }

        /// <summary>
        /// 应用程序退出时才关闭 Python 环境
        /// </summary>
        public void Dispose()
        {
            lock (_lockObject)
            {
                if (_isInitialized && PythonEngine.IsInitialized)
                {
                    try
                    {
                        _logger.LogInformation("关闭全局Python环境");
                        PythonEngine.Shutdown();
                        _isInitialized = false;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "关闭Python环境失败");
                    }
                }
            }
        }
    }
}