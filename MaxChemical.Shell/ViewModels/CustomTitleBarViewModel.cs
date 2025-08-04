using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using System.Windows;
using System.Windows.Input;

namespace MaxChemical.Shell.ViewModels;

public class CustomTitleBarViewModel : BindableBase
{
    private readonly IEventAggregator _eventAggregator;
    private readonly ILocalizationService _localizationService;
    private readonly ILogService _logger;
    private string _applicationTitle = string.Empty;
    private string _loginText = string.Empty;
    private UserInfo? _currentUser;
    private string _maximizeTooltip = string.Empty;
    private string _minimizeTooltip = string.Empty;
    private string _closeTooltip = string.Empty;
    private bool _isWindowMaximized = false;
    private bool _isWindowMinimized = false; // 保留原有属性

    public CustomTitleBarViewModel(
        IEventAggregator eventAggregator,
        ILocalizationService localizationService)
    {
        _logger = LogManager.GetLogger<CustomTitleBarViewModel>();
        _eventAggregator = eventAggregator;
        _localizationService = localizationService;

        InitializeCommands();
        InitializeProperties();
        SubscribeEvents();
    }

    #region Properties

    public string ApplicationTitle
    {
        get => _applicationTitle;
        set => SetProperty(ref _applicationTitle, value);
    }

    public string LoginText
    {
        get => _loginText;
        set => SetProperty(ref _loginText, value);
    }

    public UserInfo? CurrentUser
    {
        get => _currentUser;
        set
        {
            if (SetProperty(ref _currentUser, value))
            {
                UpdateLoginText();
            }
        }
    }

    public string MaximizeTooltip
    {
        get => _maximizeTooltip;
        set => SetProperty(ref _maximizeTooltip, value);
    }

    public string MinimizeTooltip
    {
        get => _minimizeTooltip;
        set => SetProperty(ref _minimizeTooltip, value);
    }

    public string CloseTooltip
    {
        get => _closeTooltip;
        set => SetProperty(ref _closeTooltip, value);
    }

    public bool IsWindowMaximized
    {
        get => _isWindowMaximized;
        set => SetProperty(ref _isWindowMaximized, value);
    }

    // 保留您原有的 IsWindowMinimized 属性
    public bool IsWindowMinimized
    {
        get => _isWindowMinimized;
        set => SetProperty(ref _isWindowMinimized, value);
    }

    #endregion

    #region Commands

    public DelegateCommand LoginCommand { get; private set; } = null!;
    public DelegateCommand MinimizeCommand { get; private set; } = null!;
    public DelegateCommand MaximizeCommand { get; private set; } = null!;
    public DelegateCommand CloseCommand { get; private set; } = null!;
    public DelegateCommand DragMoveCommand { get; private set; } = null!;

    // 保留您原有的 MaximizeCommandNoPostion 命令
    public DelegateCommand MaximizeCommandNoPostion { get; private set; } = null!;

    #endregion

    private void InitializeCommands()
    {
        LoginCommand = new DelegateCommand(OnLogin);
        MinimizeCommand = new DelegateCommand(OnMinimize);
        MaximizeCommand = new DelegateCommand(OnMaximize);
        CloseCommand = new DelegateCommand(OnClose);
        DragMoveCommand = new DelegateCommand(OnDragMove);

        // 初始化您原有的命令
        MaximizeCommandNoPostion = new DelegateCommand(OnMaximizeNoPosition);
    }

    private void InitializeProperties()
    {
        LoadLocalizedStrings();
        UpdateLoginText();

        // 初始化窗口状态
        var window = Application.Current.MainWindow;
        if (window != null)
        {
            _isWindowMaximized = window.WindowState == WindowState.Maximized;
            _isWindowMinimized = window.Width <= 1200; // 根据您的逻辑判断
        }
    }

    private void LoadLocalizedStrings()
    {
        ApplicationTitle = _localizationService.GetString("ApplicationTitle");
        MinimizeTooltip = _localizationService.GetString("Minimize_Tooltip");
        CloseTooltip = _localizationService.GetString("Close_Tooltip");
        UpdateMaximizeTooltip();
    }

    private void UpdateMaximizeTooltip()
    {
        // 根据 IsWindowMinimized 状态更新提示文本
        MaximizeTooltip = _isWindowMinimized
            ? _localizationService.GetString("Maximize_Tooltip")
            : _localizationService.GetString("Restore_Tooltip");
    }

    private void SubscribeEvents()
    {
        _eventAggregator.GetEvent<UserLoginEvent>().Subscribe(OnUserLogin);
        _eventAggregator.GetEvent<UserLogoutEvent>().Subscribe(OnUserLogout);
        _eventAggregator.GetEvent<LanguageChangedEvent>().Subscribe(OnLanguageChanged);
    }

    private void UpdateLoginText()
    {
        if (CurrentUser != null)
        {
            var template = _localizationService.GetString("LoggedInUser");
            LoginText = string.Format(template, CurrentUser.UserName ?? CurrentUser.UserName);
        }
        else
        {
            LoginText = _localizationService.GetString("NotLoggedIn");
        }
    }

    #region Command Implementations

    private void OnLogin()
    {
        if (CurrentUser == null)
        {
            // 模拟登录
            var adminName = _localizationService.GetString("Administrator");
            var user = new UserInfo("admin", adminName, "Administrator");
            _eventAggregator.GetEvent<UserLoginEvent>().Publish(user);
        }
        else
        {
            _eventAggregator.GetEvent<UserLogoutEvent>().Publish();
        }
    }

    private void OnMinimize()
    {
        Application.Current.MainWindow.WindowState = WindowState.Minimized;
    }

    // 保留您原有的 OnMaximize 逻辑（带边距的最大化）
    private void OnMaximize()
    {
        var window = Application.Current.MainWindow;

        if (_isWindowMinimized)
        {
            // 使用最大化但添加边距
            window.WindowState = WindowState.Maximized;

            // 设置窗口的 Margin 或者调整内容区域
            if (window.Content is FrameworkElement rootElement)
            {
                rootElement.Margin = new Thickness(0, 6, 0, 0); // 左,上,右,下
            }

            _isWindowMinimized = false;
            _isWindowMaximized = true;
        }
        else
        {
            window.WindowState = WindowState.Normal;
            // 设置为较小尺寸并居中
            window.Width = 1200;
            window.Height = 800;

            // 居中显示
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;
            window.Left = (screenWidth - window.Width) / 2;
            window.Top = (screenHeight - window.Height) / 2;

            // 清除边距
            if (window.Content is FrameworkElement rootElement)
            {
                rootElement.Margin = new Thickness(0);
            }

            _isWindowMinimized = true;
            _isWindowMaximized = false;
        }

        UpdateMaximizeTooltip();
    }

    // 保留您原有的 OnMaximizeNoPosition 逻辑（不居中的最大化）
    private void OnMaximizeNoPosition()
    {
        var window = Application.Current.MainWindow;

        if (_isWindowMinimized)
        {
            // 当前是缩小状态，执行放大到屏幕大小
            var workingArea = SystemParameters.WorkArea;
            window.Width = workingArea.Width;
            window.Height = workingArea.Height;

            window.WindowState = WindowState.Normal;
            _isWindowMinimized = false;
            _isWindowMaximized = true;
        }
        else
        {
            // 设置为较小尺寸（不居中）
            window.Width = 1200;
            window.Height = 800;

            _isWindowMinimized = true;
            _isWindowMaximized = false;
        }

        UpdateMaximizeTooltip();
    }

    private void OnClose()
    {
        Application.Current.Shutdown();
    }

    private void OnDragMove()
    {
        try
        {
            if (Application.Current.MainWindow != null && Mouse.LeftButton == MouseButtonState.Pressed)
            {
                Application.Current.MainWindow.DragMove();
            }
        }
        catch (InvalidOperationException)
        {
            // 当窗口不在拖拽状态时会抛出此异常，可以安全忽略
        }
        catch (Exception ex)
        {
            _logger.LogCritical($"DragMove 异常: {ex.Message}");
        }
    }

    #endregion

    #region Event Handlers

    private void OnUserLogin(UserInfo userInfo)
    {
        CurrentUser = userInfo;
    }

    private void OnUserLogout()
    {
        CurrentUser = null;
    }

    private void OnLanguageChanged(string cultureName)
    {
        LoadLocalizedStrings();
        UpdateLoginText();
    }

    #endregion
}