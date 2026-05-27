// ============================================================================
//  DeviceScreenBase.cs
//  放置位置：MaxChemical.Modules.Designer/Views/Controls/DeviceScreenBase.cs
//  命名空间：MaxChemical.Modules.Designer.Views.Controls
//
//  作用：
//    所有设备嵌入式屏幕的基类。封装了与命令式驱动对接的全部公共逻辑：
//      - OnLoaded 启动 DispatcherTimer 轮询、OnUnloaded 停止（生命周期安全）；
//      - 轮询时调用一个“读数命令”，拿回原始 DeviceParameters；
//      - 把刷新回调切回 UI 线程；
//      - 提供 ExecuteCommandAsync 给子类发“写/开关”命令；
//      - 防重入：上一轮读没回来就不发下一轮。
//
//  子类只需做两件事：
//      1) 在构造里 InitializeComponent() 摆好 XAML 布局；
//      2) 重写 PollCommandName（要轮询哪个命令）和 OnDataRefreshed（拿到数据怎么填 UI）。
//
//  为什么直接用 command.ExecuteAsync 而不是 IDeviceUIInteraction.ExecuteUICommandAsync？
//    因为 Device 基类里 ExecuteUICommandAsync 的默认实现只认同步 command.Action，
//    而本批驱动命令全是 AsyncAction，会被判定为“未找到命令”。command.ExecuteAsync
//    两种都支持，且直接返回原始 OutputParameters（含 MFC_FSV 等），取数最直接。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using DevicePlugins.Devices;

namespace MaxChemical.Modules.Designer.Views.Controls
{
    /// <summary>
    /// 设备嵌入式屏幕基类。子类继承后只写布局 + 数据映射。
    /// </summary>
    public abstract class DeviceScreenBase : UserControl
    {
        private DispatcherTimer _pollTimer;
        private CancellationTokenSource _cts;
        private volatile bool _isPolling;   // 防重入：上一轮没回来不发下一轮
        private bool _started;

        /// <summary>本屏幕绑定的设备实例。子类布局时可直接读它的 Parameters。</summary>
        protected IDevice Device { get; private set; }

        /// <summary>轮询间隔，默认 500ms。子类可在构造里改。</summary>
        protected int PollIntervalMs { get; set; } = 500;

        /// <summary>
        /// 轮询时要执行的“读数命令”名称。
        /// 例如两液一气系统返回 "获取所有数据"。返回 null 表示不轮询（纯静态屏幕）。
        /// </summary>
        protected abstract string PollCommandName { get; }

        protected DeviceScreenBase()
        {
            Loaded += OnScreenLoaded;
            Unloaded += OnScreenUnloaded;
        }

        /// <summary>
        /// 由宿主在创建控件后调用，注入设备实例。
        /// 必须在控件 Loaded 之前或之后都安全，因此这里只存引用，真正轮询在 Loaded 启动。
        /// </summary>
        public void AttachDevice(IDevice device)
        {
            Device = device;
            // 注入后立即做一次初始化填值（用当前 Parameters 里的缺省/上次值），
            // 让屏幕打开瞬间不是空白，随后轮询再覆盖为实时值。
            try { OnDeviceAttached(); } catch (Exception ex) { Debug.WriteLine($"[Screen] OnDeviceAttached 失败: {ex.Message}"); }
        }

        /// <summary>
        /// 设备注入后调用一次。子类可在此做初始 UI 填充（可选重写）。
        /// </summary>
        protected virtual void OnDeviceAttached() { }

        private void OnScreenLoaded(object sender, System.Windows.RoutedEventArgs e)
        {
            StartPolling();
        }

        /// <summary>
        /// 启动轮询。幂等：已在轮询则忽略。
        /// 既被 Loaded 自动调用，也可被宿主在“切回屏幕 Tab”时手动调用
        /// （因为用 Visibility 隐藏不会触发 Unloaded/Loaded）。
        /// </summary>
        public void StartPolling()
        {
            System.Diagnostics.Debug.WriteLine($"[ScreenBase] StartPolling 调用. _started={_started}, PollCmd='{PollCommandName}', Device={(Device == null ? "null" : Device.Name)}");

            if (_started) { System.Diagnostics.Debug.WriteLine("[ScreenBase] 已在轮询，跳过"); return; }

            if (string.IsNullOrEmpty(PollCommandName) || Device == null)
            {
                System.Diagnostics.Debug.WriteLine("[ScreenBase] ✗ 无轮询命令或设备为空，不启动");
                return;
            }

            _started = true;
            _cts = new System.Threading.CancellationTokenSource();
            _pollTimer = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(PollIntervalMs) };
            _pollTimer.Tick += async (_, __) => await PollOnceAsync();
            _pollTimer.Start();
            _ = PollOnceAsync();
            System.Diagnostics.Debug.WriteLine($"[ScreenBase] ✓ 轮询已启动 @ {PollIntervalMs}ms");
        }

        private void OnScreenUnloaded(object sender, System.Windows.RoutedEventArgs e)
        {
            StopPolling();
        }

        /// <summary>停止轮询并释放资源。宿主关闭对话框时控件 Unloaded 会自动调到。</summary>
        public void StopPolling()
        {
            try
            {
                _pollTimer?.Stop();
                _pollTimer = null;
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = null;
                _started = false;
                Debug.WriteLine("[Screen] 轮询已停止");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Screen] 停止轮询出错: {ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task PollOnceAsync()
        {
            if (_isPolling) { System.Diagnostics.Debug.WriteLine("[ScreenBase] 上一轮未回，跳过"); return; }
            if (Device == null) { System.Diagnostics.Debug.WriteLine("[ScreenBase] Device 为空，跳过"); return; }
            var token = _cts?.Token ?? System.Threading.CancellationToken.None;
            if (token.IsCancellationRequested) return;

            _isPolling = true;
            try
            {
                System.Diagnostics.Debug.WriteLine($"[ScreenBase] → 执行命令 '{PollCommandName}'");
                var cmd = System.Linq.Enumerable.FirstOrDefault(Device.Commands, c => c.Name == PollCommandName);
                if (cmd == null)
                {
                    System.Diagnostics.Debug.WriteLine($"[ScreenBase] ✗ 设备里找不到命令 '{PollCommandName}'! 现有命令: {string.Join(", ", System.Linq.Enumerable.Select(Device.Commands, c => c.Name))}");
                    return;
                }

                var result = await cmd.ExecuteAsync(null).ConfigureAwait(true);
                System.Diagnostics.Debug.WriteLine($"[ScreenBase] ← 命令返回 Success={result.Success}, Err='{result.ErrorMessage}', 输出参数数={result.OutputParameters?.Variables?.Count ?? 0}");

                if (token.IsCancellationRequested) return;

                if (result.Success && result.OutputParameters != null)
                {
                    System.Diagnostics.Debug.WriteLine("[ScreenBase] ✓ 调 OnDataRefreshed 填值");
                    try { OnDataRefreshed(result.OutputParameters); }
                    catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine($"[ScreenBase] OnDataRefreshed 抛异常: {ex.Message}"); }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[ScreenBase] ✗ 命令失败或无输出，不刷新（设备可能未连接/模拟模式）");
                    OnPollError(new System.Exception(result.ErrorMessage ?? "no data"));
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ScreenBase] ✗ 轮询异常: {ex.Message}");
                OnPollError(ex);
            }
            finally { _isPolling = false; }
        }

        /// <summary>
        /// 执行一个命令并返回其原始 OutputParameters（读数用）。
        /// 直接走 DeviceCommand.ExecuteAsync，兼容 AsyncAction / Action。
        /// </summary>
        protected async Task<DeviceParameters> ExecuteForOutputAsync(
            string commandName, DeviceParameters input)
        {
            var cmd = Device?.Commands?.FirstOrDefault(c => c.Name == commandName);
            if (cmd == null)
            {
                Debug.WriteLine($"[Screen] 未找到命令: {commandName}");
                return null;
            }

            var result = await cmd.ExecuteAsync(input).ConfigureAwait(true);
            if (!result.Success)
            {
                Debug.WriteLine($"[Screen] 命令失败 {commandName}: {result.ErrorMessage}");
                return null;
            }
            return result.OutputParameters;
        }

        /// <summary>
        /// 子类发“写/开关”命令用。构造好输入参数（key 对应驱动 InputParameters 里的名字，
        /// 例如 TargetValue），执行后返回是否成功（读 OutputParameters 的 "Success"）。
        /// </summary>
        protected async System.Threading.Tasks.Task<bool> ExecuteCommandAsync(
    string commandName, System.Collections.Generic.Dictionary<string, object> inputs = null)
        {
            var cmd = System.Linq.Enumerable.FirstOrDefault(Device?.Commands, c => c.Name == commandName);
            if (cmd == null)
            {
                System.Diagnostics.Debug.WriteLine($"[ScreenBase] 写命令找不到: {commandName}");
                return false;
            }

            DeviceParameters input = null;
            if (inputs != null && inputs.Count > 0)
            {
                input = new DeviceParameters();
                foreach (var kv in inputs)
                {
                    // 直接 new 干净参数，名字=命令期望的 key（如 TargetValue）
                    // 用 double 范围足够覆盖 short/int/float；命令里 GetValue<short>/<float> 会再转换
                    double val = System.Convert.ToDouble(kv.Value);
                    input.Variables.Add(new NumberParameter(kv.Key, -1000000, 1000000, val, kv.Key, false));
                }
            }

            try
            {
                var result = await cmd.ExecuteAsync(input).ConfigureAwait(true);
                bool ok = result.Success
                          && (result.OutputParameters?.GetValue<bool>("Success") ?? result.Success);
                System.Diagnostics.Debug.WriteLine(
                    $"[ScreenBase] 写命令 '{commandName}' Success={result.Success}, " +
                    $"Out.Success={result.OutputParameters?.GetValue<bool>("Success")}, Err='{result.ErrorMessage}'");
                return ok;
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ScreenBase] 写命令 '{commandName}' 异常: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 拿到一轮轮询数据后回调（已在 UI 线程）。子类把 output 里的值填到屏幕控件上。
        /// </summary>
        /// <param name="output">读数命令返回的原始参数集（含 MFC_FSV、Pump1_FLOW 等）。</param>
        protected abstract void OnDataRefreshed(DeviceParameters output);

        /// <summary>轮询出错回调（可选重写，用于在屏幕上显示“通信异常”）。</summary>
        protected virtual void OnPollError(Exception ex) { }
    }
}