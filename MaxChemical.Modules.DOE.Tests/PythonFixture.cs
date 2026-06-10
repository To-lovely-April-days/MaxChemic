using System;
using MaxChemical.Modules.Designer.Services;
using Xunit;

namespace MaxChemical.Modules.DOE.Tests
{
    /// <summary>
    /// Python 环境 Fixture — 整个测试程序集共享一个 Python 环境实例。
    /// 
    /// 为什么需要这个:
    ///   PythonEngine.Initialize() 在进程中只能调用一次。
    ///   如果每个测试类都调用 Initialize/Shutdown，第二个类会崩溃。
    ///   用 xUnit 的 ICollectionFixture 确保全局只初始化一次。
    /// </summary>
    public class PythonFixture : IDisposable
    {
        public bool IsReady { get; }

        public PythonFixture()
        {
            try
            {
                var envManager = PythonEnvironmentManager.Instance;
                if (!envManager.IsInitialized)
                {
                    IsReady = envManager.Initialize();
                }
                else
                {
                    IsReady = true;
                }
            }
            catch (Exception ex)
            {
                IsReady = false;
                Console.WriteLine($"[PythonFixture] 初始化失败: {ex.Message}");
            }
        }

        public void Dispose()
        {
            // 不在这里 Shutdown — 让 PythonEnvironmentManager 在进程退出时自行清理
            // 如果在这里 Shutdown，后面的测试就无法再用 Python 了
        }
    }

    /// <summary>
    /// 定义测试集合: 所有标注 [Collection("Python")] 的测试类共享同一个 PythonFixture
    /// </summary>
    [CollectionDefinition("Python")]
    public class PythonCollection : ICollectionFixture<PythonFixture>
    {
    }
}