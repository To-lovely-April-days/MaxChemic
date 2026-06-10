// 新建文件: Services/PythonEnvironmentManager.cs
using Python.Runtime;
using MaxChemical.Logging;
using System;

namespace MaxChemical.Modules.Designer.Services
{
    /// <summary>
    /// Python环境全局管理器 - 单例模式
    /// 
    ///  关键修复: Initialize() 完成后调用 PythonEngine.BeginAllowThreads()
    ///   释放 GIL，允许其他线程（如 DOE 后台执行线程）正常获取 GIL。
    ///   
    ///   根因: PythonEngine.Initialize() 在 UI 线程调用后，GIL 绑定到 UI 线程。
    ///   如果不调用 BeginAllowThreads()，其他线程通过 Py.GIL() 获取 GIL 时
    ///   需要等待 UI 线程释放，导致跨线程死锁。
    /// </summary>
    public sealed class PythonEnvironmentManager : IDisposable
    {
        private static readonly Lazy<PythonEnvironmentManager> _instance =
            new Lazy<PythonEnvironmentManager>(() => new PythonEnvironmentManager());

        private readonly ILogService _logger;
        private bool _isInitialized;
        private readonly object _lockObject = new object();

        /// <summary>
        ///  保存 BeginAllowThreads 返回的线程状态指针，
        /// Dispose 时需要用它恢复主线程状态才能安全 Shutdown。
        /// </summary>
        private IntPtr _mainThreadState;

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

                    //Runtime.PythonDLL = @"D:\Python\Python313\python313.dll";

                    if (!PythonEngine.IsInitialized)
                    {
                        PythonEngine.Initialize();
                        _logger.LogInformation("PythonEngine初始化成功");
                    }

                    // 设置 Python 搜索路径
                    using (Py.GIL())
                    {
                        dynamic sys = Py.Import("sys");

                        sys.path.append(@".\");
                    }

                    // 关键修复：释放 GIL，允许其他线程获取 
                    // PythonEngine.Initialize() 后，当前线程（UI线程）持有 GIL。
                    // 必须调用 BeginAllowThreads() 让出 GIL，否则其他线程的 Py.GIL()
                    // 会因为等待 UI 线程释放 GIL 而永久阻塞。
                    // 返回的 IntPtr 是主线程状态指针，Shutdown 前需要用它恢复。
                    _mainThreadState = PythonEngine.BeginAllowThreads();
                    _logger.LogInformation("Python GIL 已释放，允许多线程访问");

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

                        //  恢复主线程状态，然后才能安全 Shutdown
                        if (_mainThreadState != IntPtr.Zero)
                        {
                            PythonEngine.EndAllowThreads(_mainThreadState);
                            _mainThreadState = IntPtr.Zero;
                        }

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