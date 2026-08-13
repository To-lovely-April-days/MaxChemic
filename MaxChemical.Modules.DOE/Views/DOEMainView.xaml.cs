using MaxChemical.Infrastructure.Services;
using MaxChemical.Modules.DOE.Events;
using MaxChemical.Modules.DOE.ViewModels;
using Prism.Events;
using Prism.Ioc;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MaxChemical.Modules.DOE.Views
{
    public partial class DOEMainView : Window
    {
        private DOEOverviewViewModel? _overviewVm;
        private DOEExecutionDashboardViewModel? _execVm;
        private DOEModelAnalysisViewModel? _modelAnalysisVm;
        private DOEHistoryViewModel? _historyVm;
        private DOEMainViewModel? _mainVm;

        // ── 迷你模式状态保存 ──
        private double _savedWidth, _savedHeight, _savedLeft, _savedTop;
        private WindowState _savedWindowState;

        // ── 小桐隐身代开 ──
        // 此窗口没有 AllowsTransparency,Opacity 压 0 不生效;隐身靠屏外坐标实现。
        // InitAsync 挂在 Loaded 上,窗口必须真正 Show 才会初始化,Hide 会卡住装配,
        // 所以隐身期用「已 Show 但在屏幕外」的状态完成全部加载。
        private bool _stealthPending;   // 已 Show 但仍在屏外,等首次切迷你岛时现身
        private bool _agentOpened;      // 本窗口由小桐代开(用户从未主动打开过)

        /// <summary>
        /// 小桐执行批次时代开窗口专用,必须在 Show() 之前调用:窗口在屏幕外不激活、
        /// 不进任务栏地完成初始化,首次切入迷你监控(灵动岛)时才现身——
        /// 执行全程大窗口不在屏幕上闪现。
        /// </summary>
        public void PrepareStealthOpen()
        {
            _stealthPending = true;
            _agentOpened = true;
            ShowActivated = false;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = -20000;
            Top = -20000;
        }

        /// <summary>批次没能启动、窗口仍在屏外隐身时的兜底:直接关闭,不留幽灵窗口。</summary>
        public void CloseIfStillStealth()
        {
            if (_stealthPending) Close();
        }

        private void ExitStealth()
        {
            if (!_stealthPending) return;
            _stealthPending = false;
            ShowInTaskbar = true;
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            // 隐身窗口被主动激活(如第二次执行请求走了置前分支):立即在屏幕中央现身,
            // 不能留一个「看不见的活动窗口」
            if (_stealthPending)
            {
                ExitStealth();
                var wa = SystemParameters.WorkArea;
                Left = wa.Left + (wa.Width - ActualWidth) / 2;
                Top = wa.Top + (wa.Height - ActualHeight) / 2;
                _agentOpened = false;
            }
        }

        // ── DWM 窗口圆角(Win11 22000+ 原生) ──
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>给窗口描 DWM 圆角;不支持的系统(Win10 及更早)静默 no-op,窗口保持直角。</summary>
        private void ApplyRoundedCorners()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int pref = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            }
            catch { /* 老系统无此 API,直角即可 */ }
        }
        private const double MINI_WIDTH = 320;
        private const double MINI_HEIGHT = 240;
        private static readonly Duration AnimDuration = new(TimeSpan.FromMilliseconds(300));
        private static readonly CubicEase AnimEase = new() { EasingMode = EasingMode.EaseInOut };
        // 迷你岛底色(与 DOEMiniPanel 根 Border 同色):迷你模式下让窗口底色跟岛一致,四角不露白
        private static readonly SolidColorBrush _islandBackground =
            new(Color.FromRgb(0xEE, 0xF1, 0xF6));
        private static readonly string[] LoadingMessages = new[]
{
   "正在启动 Python 运行时...",
    "正在加载统计分析引擎...",
    // GPR 禁用: "正在初始化 GPR 模型服务...",
    "正在加载项目数据..."
};

        private readonly ILocalizationService _localization;
        public DOEMainView(DOEMainViewModel mainVm, IContainerProvider container)
        {
            InitializeComponent();
            DataContext = mainVm;
            _mainVm = mainVm;
            _localization = container.Resolve<ILocalizationService>();

            mainVm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(DOEMainViewModel.SelectedTabIndex))
                {
                    int idx = mainVm.SelectedTabIndex;
                    foreach (var child in TabBar.Children)
                    {
                        if (child is RadioButton rb && rb.Tag is string t && int.TryParse(t, out int i))
                            rb.IsChecked = (i == idx);
                    }
                }
            };

            // 不透明窗(无分层透明)本身是直角方窗;Win11 用 DWM 给窗口原生描圆角,
            // 既无 Margin 露出的方框,又不必回到吃性能的分层透明。Win10 及更早为 no-op(保持直角)。
            SourceInitialized += (s, e) => ApplyRoundedCorners();

            Loaded += async (s, e) => await InitAsync(mainVm, container);

            // 兜底:无论从哪条路径关闭(标题栏按钮/Alt+F4/系统关闭),都退订 VM 的事件,
            // 防止残留的旧 VM 继续响应外部执行请求(会与新窗口抢着启动批次),
            // 并释放四个子 VM——执行看板 VM 订阅了单例执行引擎,不退订会漏整棵窗口树
            Closed += (s, e) => DisposeAll();
        }

        /// <summary>释放主 VM 与四个子 VM(退订各自事件)。可重复调用,子 VM 各自 Dispose 幂等。</summary>
        private void DisposeAll()
        {
            _mainVm?.Dispose();
            _execVm?.Dispose();
            (_overviewVm as IDisposable)?.Dispose();
            (_modelAnalysisVm as IDisposable)?.Dispose();
            (_historyVm as IDisposable)?.Dispose();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) return;
            DragMove();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
        {
            // ★ 修复: 关闭窗口时取消事件订阅，防止下次打开时重复弹窗
            if (_mainVm != null)
                _mainVm.Dispose();
            Close();
        }

        private async void TabRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string tagStr && int.TryParse(tagStr, out int index))
            {
                if (_mainVm != null)
                    _mainVm.SelectedTabIndex = index;

                try
                {
                    switch (index)
                    {
                        case 0:
                            if (_overviewVm != null) await _overviewVm.LoadAsync();
                            break;
                        case 1:
                            if (_execVm != null && !string.IsNullOrEmpty(_mainVm?.CurrentBatchId))
                                await _execVm.LoadBatchAsync(_mainVm.CurrentBatchId);
                            break;
                        case 2:
                            // ★ 如果是从历史页跳转过来（CurrentBatchId 已设置），
                            //    不重复加载，NavigateToOlsBatchAsync 已经处理了
                            //    只有手动点 Tab 且没有 CurrentBatchId 时才 LoadAsync
                            if (_modelAnalysisVm != null && string.IsNullOrEmpty(_mainVm?.CurrentBatchId))
                            {
                                await _modelAnalysisVm.LoadAsync();
                            }
                            break;
                        case 3:
                            if (_historyVm != null) await _historyVm.LoadBatchesAsync();
                            break;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"TabSwitch error: {ex.Message}");
                }
            }
        }

        private async Task InitAsync(DOEMainViewModel mainVm, IContainerProvider container)
        {
            try
            {
                LoadingOverlay.Visibility = Visibility.Visible;

                // ── Step 1: Python 运行时 ──
                //UpdateLoadingStep(0);
                //await Task.Run(() =>
                //{
                //    try
                //    {
                //        var env = MaxChemical.Modules.Designer.Services.PythonEnvironmentManager.Instance;
                //        if (!env.IsInitialized) env.Initialize();
                //    }
                //    catch { }
                //});

                // ── Step 2: 统计分析引擎 ──
                UpdateLoadingStep(1);
                _overviewVm = container.Resolve<DOEOverviewViewModel>();
                _execVm = container.Resolve<DOEExecutionDashboardViewModel>();

                // ── Step 3: GPR 模型服务 ──
                UpdateLoadingStep(2);
                _modelAnalysisVm = container.Resolve<DOEModelAnalysisViewModel>();
                _historyVm = container.Resolve<DOEHistoryViewModel>();

                // ── Step 4: 项目数据 ──
                UpdateLoadingStep(3);

                OverviewView.DataContext = _overviewVm;
                ExecutionView.DataContext = _execVm;
                //ModelAnalysisView.DataContext = _modelAnalysisVm;
                HistoryView.DataContext = _historyVm;
                MiniPanel.DataContext = _execVm;

                _execVm.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(DOEExecutionDashboardViewModel.IsMiniMode))
                    {
                        if (_execVm.IsMiniMode) SwitchToMiniMode();
                        else SwitchToNormalMode();
                    }
                };

                mainVm.RequestLoadExecution += async (s, id) =>
                {
                    if (_execVm != null) await _execVm.LoadBatchAsync(id);
                };
                // 外部(小桐)请求执行:先加载批次,再走看板自身的启动路径(会弹出迷你监控窗)
                mainVm.RequestExecuteExternal += async (s, id) =>
                {
                    if (_execVm == null) return;
                    await _execVm.LoadBatchAsync(id);
                    await _execVm.StartFromExternalAsync();
                };
                mainVm.RequestLoadAnalysis += async (s, id) =>
                {
                    if (_modelAnalysisVm != null) await _modelAnalysisVm.LoadBatchAsync(id);
                };
                mainVm.RequestRefreshHistory += async (s, e) =>
                {
                    if (_historyVm != null) await _historyVm.LoadBatchesAsync();
                };
                mainVm.RequestRefreshOverview += async (s, e) =>
                {
                    if (_overviewVm != null) await _overviewVm.LoadAsync();
                };

                if (_historyVm != null)
                {
                   

                    _historyVm.RequestExecuteBatch += (s, id) => mainVm.NavigateToExecution(id);
                    _historyVm.RequestAnalyzeBatch += async (s, batchId) =>
                    {
                        try
                        {
                            var vm = _modelAnalysisVm;
                            if (vm == null) return;
                            var loading = new OlsLoadingWindow();
                            loading.Owner = System.Windows.Application.Current.MainWindow;
                            loading.SetOlsTitle(_localization.GetString("Doe_Ols_Title", "OLS 分析"));
                            loading.Show();
                            // ★ 关键: 断开主窗口 ModelAnalysisView 的 DataContext
                            // 这样其内部所有 PlotView 的绑定全部解除，PlotModel 被释放
                            //var savedContext = ModelAnalysisView.DataContext;
                            //ModelAnalysisView.DataContext = null;

                            // 加载该批次的 OLS 分析（内部会 new 新的 PlotModel）
                            await vm.NavigateToOlsBatchAsync(batchId);

                            // 后台加载
                            loading.UpdateStatus(_localization.GetString("Doe_Ols_StatusMsg_LoadData", "正在加载实验数据..."));
                            await vm.NavigateToOlsBatchAsync(batchId);

                            loading.UpdateStatus(_localization.GetString("Doe_Ols_StatusMsg_CreateWin", "正在构建分析窗口..."));
                            await Task.Delay(50);

                            // 弹出非模态窗口
                            var win = new OlsAnalysisWindow
                            {
                                DataContext = vm
                            };

                            // ★ Closing：窗口还在，强制清理所有子控件
                            win.Closing += (_, __) =>
                            {
                                win.DataContext = null;   // 断开所有绑定
                                win.Content = null;       // 移除视觉树 → 触发 PlotView.Unloaded → 释放 PlotModel
                            };

                            // ★ Closed：窗口已销毁，安全恢复主窗口
                            win.Closed += (_, __) =>
                            {
                                //// ApplicationIdle = 最低优先级，等 WPF 完成所有布局/渲染清理
                                //Dispatcher.BeginInvoke(
                                //    System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                                //    () => { ModelAnalysisView.DataContext = savedContext; });
                            };
                            // 关闭 Loading → 打开分析窗口
                            loading.Close();
                            win.Show();
                        }
                        catch (Exception ex)
                        {
                            //// 出错时也要恢复
                            //if (ModelAnalysisView.DataContext == null)
                            //    ModelAnalysisView.DataContext = _modelAnalysisVm;

                            MessageBox.Show($"打开分析窗口失败: {ex.Message}", "错误",
                                MessageBoxButton.OK, MessageBoxImage.Error);
                        }
                    };
                }

                if (_overviewVm != null)
                {
                    _overviewVm.RequestResumeBatch += (s, id) => mainVm.NavigateToExecution(id);
                    _overviewVm.RequestGoToHistory += (s, e) => mainVm.SelectedTabIndex = 3;
                    _overviewVm.RequestContinueProject += (s, projectId) =>
                    {
                        _ = mainVm.ContinueProjectAsync(projectId);
                    };
                    _overviewVm.RequestViewProject += (s, projectId) =>
                    {
                        mainVm.SelectedTabIndex = 3;
                    };
                }
                _overviewVm.RequestViewAnalysis += (s, projectId) =>
                {
                    _ = mainVm.NavigateToAnalysisByProjectAsync(projectId);
                };
                // ── 完成 ──
                UpdateLoadingStep(4, "准备就绪");
                await Task.Delay(200);

                HideLoadingOverlay();
                // 外部执行请求可能已在初始化期间把页签切到执行看板,此时不要拉回概览页
                if (string.IsNullOrEmpty(mainVm.CurrentBatchId))
                    mainVm.SelectedTabIndex = 0;
                await _overviewVm.LoadAsync();

                // 外部执行请求若在接线完成前就到达(新开窗口初始化耗时),在这里补跑。
                // 初始化极慢时窗口可能已被隐身兜底关掉(CloseIfStillStealth),
                // 对已关闭的窗口补跑会让批次在没有任何监控的情况下开跑——直接放弃
                if (!IsLoaded) return;
                var pendingExec = mainVm.TakePendingExternalExecuteRequest();
                if (!string.IsNullOrEmpty(pendingExec) && _execVm != null)
                {
                    mainVm.SelectedTabIndex = 1;
                    await _execVm.LoadBatchAsync(pendingExec);
                    await _execVm.StartFromExternalAsync();
                }
            }
            catch (Exception ex)
            {
                HideLoadingOverlay();
                MessageBox.Show($"初始化失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// 隐藏加载遮罩并停掉 16 个格子的 Forever 呼吸动画。
        /// 只 Collapse 不停动画的话,动画时钟会让 WPF 渲染循环以 60fps 空转到窗口关闭,
        /// 其渲染优先级高于监控标签更新回调,整个应用(尤其画布监控刷新)会被拖卡。
        /// BeginAnimation(prop, null) 会替换掉该属性上的全部动画时钟(含 Storyboard 挂上的)。
        /// </summary>
        private void HideLoadingOverlay()
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            try
            {
                foreach (var cell in new[]
                {
                    Cell00, Cell01, Cell02, Cell03,
                    Cell10, Cell11, Cell12, Cell13,
                    Cell20, Cell21, Cell22, Cell23,
                    Cell30, Cell31, Cell32, Cell33
                })
                {
                    cell.BeginAnimation(OpacityProperty, null);
                    cell.Opacity = 0.10;
                }
            }
            catch { }
        }
      
        private void UpdateLoadingStep(int completedSteps, string? overrideText = null)
        {
            // 更新进度条宽度（总宽 220）
            var targetWidth = 220.0 * completedSteps / 4.0;
            LoadingProgressBar.Width = targetWidth;

            // 更新状态文字
            if (overrideText != null)
            {
                LoadingStatusText.Text =_localization.GetString("Doe_Main_LoadMsg_Ready", overrideText);
            }
            else if (completedSteps < LoadingMessages.Length)
            {
                LoadingStatusText.Text = _localization.GetString($"Doe_Main_LoadMsg_{completedSteps}", LoadingMessages[completedSteps]);
            }
            else
            {
                LoadingStatusText.Text = _localization.GetString("Doe_Main_LoadMsg_Ready", "准备就绪");
            }
        }
        private void ProjectMenuBtn_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.ContextMenu != null)
            {
                btn.ContextMenu.PlacementTarget = btn;
                btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                btn.ContextMenu.DataContext = DataContext;
                btn.ContextMenu.IsOpen = true;
            }
        }

        // ══════════════ 迷你模式切换 ══════════════

        private void SwitchToMiniMode()
        {
            bool wasStealth = _stealthPending;
            ExitStealth();

            _savedWindowState = WindowState;
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;

            _savedWidth = ActualWidth;
            _savedHeight = ActualHeight;
            _savedLeft = Left;
            _savedTop = Top;
            // 隐身期的屏外坐标不能作为「展开」的还原目标,换成工作区居中
            if (wasStealth)
            {
                var wa = SystemParameters.WorkArea;
                _savedLeft = wa.Left + (wa.Width - _savedWidth) / 2;
                _savedTop = wa.Top + (wa.Height - _savedHeight) / 2;
            }

            MainContent.Visibility = Visibility.Collapsed;
            MiniPanel.Visibility = Visibility.Visible;
            MiniPanel.Opacity = 0;

            // 迷你岛自身是 #EEF1F6 圆角卡片,不透明窗下圆角外的四角会露窗口底色;
            // 大窗底色是白,迷你模式下把窗口底色切成与岛同色,四角才不露白边
            Background = _islandBackground;

            SizeToContent = SizeToContent.WidthAndHeight;  // ★ 改这里：宽高都自适应
            MinWidth = 480;   // ★ 新增：最小宽度，防止太窄
            MaxWidth = 720;   // ★ 新增：最大宽度，防止太宽

            // ★ 灵动岛定位：屏幕顶部居中（等布局完成后再定位）
            var screen = SystemParameters.WorkArea;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                Left = (screen.Width - ActualWidth) / 2;
                Top = screen.Top + 12;
            });

            Topmost = true;
            ResizeMode = ResizeMode.NoResize;

            MiniPanel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(150))));
        }

        private void SwitchToNormalMode()
        {
            // 小桐代开的窗口:批次结束时看板把 IsMiniMode 置回 false,不该顺势把大窗口
            // 弹到用户面前(用户从未主动打开过它)——直接关闭,决策链路不受影响
            // (智能决策报告等对话框挂在主窗口上,由看板 VM 的后续流程继续弹出);
            // 执行中用户点岛上「展开」视为主动接管,此后按普通窗口对待
            if (_agentOpened)
            {
                if (_execVm?.IsRunning != true)
                {
                    Close();
                    return;
                }
                _agentOpened = false;
            }

            var fadeOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(120)));
            fadeOut.Completed += (s, e) =>
            {
                MiniPanel.Visibility = Visibility.Collapsed;
                MainContent.Visibility = Visibility.Visible;
                MainContent.Opacity = 0;

                // 恢复大窗白底(与 MainContent 同色,消除那圈方框)
                Background = System.Windows.Media.Brushes.White;

                SizeToContent = SizeToContent.Manual;
                MinWidth = 0;      // ★ 清除迷你模式的约束
                MaxWidth = double.PositiveInfinity;
                Topmost = false;
                ResizeMode = ResizeMode.CanResizeWithGrip;

                Width = _savedWidth;
                Height = _savedHeight;
                Left = _savedLeft;
                Top = _savedTop;

                if (_savedWindowState == WindowState.Maximized)
                    WindowState = WindowState.Maximized;

                MainContent.BeginAnimation(OpacityProperty,
                    new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(150))));
            };

            MiniPanel.BeginAnimation(OpacityProperty, fadeOut);
        }

        private static DoubleAnimation CreateAnim(double to)
        {
            return new DoubleAnimation(to, AnimDuration) { EasingFunction = AnimEase };
        }

      
    }
}