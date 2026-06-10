using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using Prism.Events;
using Prism.Mvvm;
using System.IO;
using System.Windows.Threading;

namespace MaxChemical.Shell.ViewModels;

public class MainStatusBarViewModel : BindableBase
{
    private readonly IEventAggregator _eventAggregator;
    private readonly ILocalizationService _localizationService;
    private readonly DispatcherTimer _timer;

    private string _statusMessage = string.Empty;
    private string _runningModeText = string.Empty;
    private DateTime _currentTime;

    public MainStatusBarViewModel(
        IEventAggregator eventAggregator,
        ILocalizationService localizationService)
    {
        _eventAggregator = eventAggregator;
        _localizationService = localizationService;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (s, e) => CurrentTime = DateTime.Now;

        InitializeProperties();
        SubscribeEvents();
        _timer.Start();
    }

    #region Properties

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string RunningModeText
    {
        get => _runningModeText;
        set => SetProperty(ref _runningModeText, value);
    }

    public DateTime CurrentTime
    {
        get => _currentTime;
        set => SetProperty(ref _currentTime, value);
    }

    #endregion

    private void InitializeProperties()
    {
        StatusMessage = _localizationService.GetString("Status_Ready");
        RunningModeText = "模拟模式";
        CurrentTime = DateTime.Now;
    }

    private void SubscribeEvents()
    {
        _eventAggregator.GetEvent<StatusMessageChangedEvent>().Subscribe(OnStatusMessageChanged);
        _eventAggregator.GetEvent<SimulationModeChangedEvent>().Subscribe(OnSimulationModeChanged);
        _eventAggregator.GetEvent<LanguageChangedEvent>().Subscribe(OnLanguageChanged);
    }

    #region Event Handlers

    private void OnStatusMessageChanged(string message)
    {
        StatusMessage = message;
    }

    private void OnSimulationModeChanged(bool isSimulationMode)
    {
        RunningModeText = isSimulationMode ? "模拟模式" : "实际模式";
    }

    private void OnLanguageChanged(string cultureName)
    {
        if (StatusMessage == "就绪" || StatusMessage == "Ready")
        {
            StatusMessage = _localizationService.GetString("Status_Ready");
        }
    }

    #endregion
}