using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MaxChemical.Modules.DOE.Events;
using MaxChemical.Modules.DOE.ViewModels;
using Prism.Events;
using Prism.Ioc;

namespace MaxChemical.Modules.DOE.Views
{
    public partial class DOEMainView : Window
    {
        private DOEOverviewViewModel? _overviewVm;
        private DOEExecutionDashboardViewModel? _execVm;
        private DOEAnalysisViewModel? _analysisVm;
        private DOEHistoryViewModel? _historyVm;
        private DOEGPRModelViewModel? _gprVm;
        private DOEMainViewModel? _mainVm;

        public DOEMainView(DOEMainViewModel mainVm, IContainerProvider container)
        {
            InitializeComponent();
            DataContext = mainVm;
            _mainVm = mainVm;

            // ViewModel 改变 SelectedTabIndex 时同步 RadioButton
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

            Loaded += async (s, e) => await InitAsync(mainVm, container);
        }

        // ═══ 标题栏 ═══
        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) return; // 不支持双击最大化
            DragMove();
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

        // ═══ Tab 切换 ═══
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
                        case 0: if (_overviewVm != null) await _overviewVm.LoadAsync(); break;
                        case 2:
                            if (_analysisVm != null && !string.IsNullOrEmpty(_mainVm?.CurrentBatchId)
                                && _analysisVm.BatchId != _mainVm.CurrentBatchId)
                                await _analysisVm.LoadBatchAsync(_mainVm.CurrentBatchId);
                            break;
                        case 3: if (_historyVm != null) await _historyVm.LoadBatchesAsync(); break;
                        case 4: if (_gprVm != null) await _gprVm.LoadAsync(); break;
                    }
                }
                catch { }
            }
        }

        // ═══ 异步初始化 ═══
        private async Task InitAsync(DOEMainViewModel mainVm, IContainerProvider container)
        {
            try
            {
                LoadingOverlay.Visibility = Visibility.Visible;

                await Task.Run(() =>
                {
                    try
                    {
                        var env = MaxChemical.Modules.Designer.Services.PythonEnvironmentManager.Instance;
                        if (!env.IsInitialized) env.Initialize();
                    }
                    catch { }
                });

                _overviewVm = container.Resolve<DOEOverviewViewModel>();
                _execVm = container.Resolve<DOEExecutionDashboardViewModel>();
                _analysisVm = container.Resolve<DOEAnalysisViewModel>();
                _historyVm = container.Resolve<DOEHistoryViewModel>();
                _gprVm = container.Resolve<DOEGPRModelViewModel>();

                OverviewView.DataContext = _overviewVm;
                ExecutionView.DataContext = _execVm;
                AnalysisView.DataContext = _analysisVm;
                HistoryView.DataContext = _historyVm;
                GPRView.DataContext = _gprVm;

                // 导航事件
                mainVm.RequestLoadExecution += async (s, id) => { if (_execVm != null) await _execVm.LoadBatchAsync(id); };
                mainVm.RequestLoadAnalysis += async (s, id) => { if (_analysisVm != null) await _analysisVm.LoadBatchAsync(id); };
                mainVm.RequestRefreshHistory += async (s, e) => { if (_historyVm != null) await _historyVm.LoadBatchesAsync(); };

                if (_historyVm != null)
                {
                    _historyVm.RequestExecuteBatch += (s, id) => mainVm.NavigateToExecution(id);
                    _historyVm.RequestAnalyzeBatch += (s, id) =>
                    {
                        mainVm.CurrentBatchId = id;
                        mainVm.SelectedTabIndex = 2;
                        _ = _analysisVm?.LoadBatchAsync(id);
                    };
                }

                if (_overviewVm != null)
                {
                    _overviewVm.RequestResumeBatch += (s, id) => mainVm.NavigateToExecution(id);
                    _overviewVm.RequestGoToHistory += (s, e) => mainVm.SelectedTabIndex = 3;
                }

                // 执行完成 → 分析页
                var ea = container.Resolve<IEventAggregator>();
                ea.GetEvent<DOEBatchCompletedEvent>().Subscribe(p =>
                {
                    if (p.FinalStatus == Models.DOEBatchStatus.Completed && _analysisVm != null)
                        Dispatcher.InvokeAsync(async () =>
                        {
                            mainVm.CurrentBatchId = p.BatchId;
                            mainVm.SelectedTabIndex = 2;
                            await _analysisVm.LoadBatchAsync(p.BatchId);
                        });
                }, ThreadOption.UIThread);

                LoadingOverlay.Visibility = Visibility.Collapsed;

                // 触发首页显示
                mainVm.SelectedTabIndex = 0;
                await _overviewVm.LoadAsync();
            }
            catch (Exception ex)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                MessageBox.Show($"初始化失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
