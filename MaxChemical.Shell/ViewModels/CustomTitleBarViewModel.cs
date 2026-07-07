using MaxChemical.Infrastructure.Events;
using MaxChemical.Infrastructure.Services;
using MaxChemical.Logging;
using MaxChemical.Shell.Services;
using Prism.Commands;
using Prism.Events;
using Prism.Mvvm;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace MaxChemical.Shell.ViewModels
{
    public class CustomTitleBarViewModel : BindableBase, IDisposable
    {
        private readonly IEventAggregator _eventAggregator;
        private readonly ILocalizationService _localizationService;
        private readonly IVoiceAssistantService _voiceAssistant;
        private readonly PiperVoiceAssistantService _piperVoiceService;
        private readonly ILogService _logger;

        private string _applicationTitle = string.Empty;
        private string _loginText = string.Empty;
        private UserInfo? _currentUser;
        private string _maximizeTooltip = string.Empty;
        private string _minimizeTooltip = string.Empty;
        private string _closeTooltip = string.Empty;
        private bool _isWindowMaximized = false;
        private bool _isWindowMinimized = false;
        private bool _isVoiceAssistantActive = false;
        private string _voiceAssistantStatus = "未启动";
        private bool _isMuted = false;
        private bool _isAwake = false;

        // 新增：Popup 交互相关属性
        private bool _isVoiceInteractionVisible = false;
        private bool _isListening = false;
        private string _listeningStatus = "准备就绪";
        private string _recognizedText = string.Empty;
        private string _assistantReply = string.Empty;

        public CustomTitleBarViewModel(
            IEventAggregator eventAggregator,
            ILocalizationService localizationService,
            IVoiceAssistantService voiceAssistant)
        {
            _logger = LogManager.GetLogger<CustomTitleBarViewModel>();
            _eventAggregator = eventAggregator;
            _localizationService = localizationService;
            _voiceAssistant = voiceAssistant;

            _piperVoiceService = voiceAssistant as PiperVoiceAssistantService;

            InitializeCommands();
            InitializeProperties();
            SubscribeEvents();
            InitializeVoiceAssistant();
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

        public bool IsWindowMinimized
        {
            get => _isWindowMinimized;
            set => SetProperty(ref _isWindowMinimized, value);
        }

        public bool IsVoiceAssistantActive
        {
            get => _isVoiceAssistantActive;
            set => SetProperty(ref _isVoiceAssistantActive, value);
        }

        public string VoiceAssistantStatus
        {
            get => _voiceAssistantStatus;
            set => SetProperty(ref _voiceAssistantStatus, value);
        }

        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                if (SetProperty(ref _isMuted, value))
                {
                    _voiceAssistant.IsSpeechEnabled = !value;
                    _logger.LogInformation($"语音输出已{(value ? "静音" : "启用")}");

                    if (!value)
                    {
                        _voiceAssistant.SpeakAsync("语音已恢复");
                    }
                }
            }
        }

        public bool IsAwake
        {
            get => _isAwake;
            set => SetProperty(ref _isAwake, value);
        }

        public bool IsVoiceInteractionVisible
        {
            get => _isVoiceInteractionVisible;
            set => SetProperty(ref _isVoiceInteractionVisible, value);
        }

        public bool IsListening
        {
            get => _isListening;
            set
            {
                if (SetProperty(ref _isListening, value))
                {
                    ListeningStatus = value ? "正在监听..." : "准备就绪";
                }
            }
        }

        public string ListeningStatus
        {
            get => _listeningStatus;
            set => SetProperty(ref _listeningStatus, value);
        }

        public string RecognizedText
        {
            get => _recognizedText;
            set => SetProperty(ref _recognizedText, value);
        }

        public string AssistantReply
        {
            get => _assistantReply;
            set => SetProperty(ref _assistantReply, value);
        }

        #endregion

        #region Commands

        public DelegateCommand LoginCommand { get; private set; } = null!;
        public DelegateCommand MinimizeCommand { get; private set; } = null!;
        public DelegateCommand MaximizeCommand { get; private set; } = null!;
        public DelegateCommand CloseCommand { get; private set; } = null!;
        public DelegateCommand DragMoveCommand { get; private set; } = null!;
        public DelegateCommand MaximizeCommandNoPostion { get; private set; } = null!;
        public DelegateCommand ToggleVoiceAssistantCommand { get; private set; } = null!;
        public DelegateCommand ToggleMuteCommand { get; private set; } = null!;
        public DelegateCommand TestVoiceCommand { get; private set; } = null!;
        public DelegateCommand CloseVoiceInteractionCommand { get; private set; } = null!;

        #endregion

        private void InitializeCommands()
        {
            LoginCommand = new DelegateCommand(OnLogin);
            MinimizeCommand = new DelegateCommand(OnMinimize);
            MaximizeCommand = new DelegateCommand(OnMaximize);
            CloseCommand = new DelegateCommand(OnClose);
            DragMoveCommand = new DelegateCommand(OnDragMove);
            MaximizeCommandNoPostion = new DelegateCommand(OnMaximizeNoPosition);
            ToggleVoiceAssistantCommand = new DelegateCommand(OnToggleVoiceAssistant);
            ToggleMuteCommand = new DelegateCommand(OnToggleMute);
            TestVoiceCommand = new DelegateCommand(OnTestVoice);
            CloseVoiceInteractionCommand = new DelegateCommand(OnCloseVoiceInteraction);
        }

        private void InitializeProperties()
        {
            LoadLocalizedStrings();
            UpdateLoginText();

            var window = Application.Current.MainWindow;
            if (window != null)
            {
                _isWindowMaximized = window.WindowState == WindowState.Maximized;
                _isWindowMinimized = window.Width <= 1200;
            }
        }

        private void InitializeVoiceAssistant()
        {
            try
            {
                _voiceAssistant.ListeningStateChanged += OnListeningStateChanged;
                _voiceAssistant.CommandRecognized += OnVoiceCommandRecognized;

                if (_voiceAssistant is PiperVoiceAssistantService piperService)
                {
                    piperService.SpeechStarted += OnSpeechStarted;
                }

                _voiceAssistant.Start();
                VoiceAssistantStatus = "等待唤醒";
                _logger.LogInformation("语音助手已初始化并启动");
            }
            catch (Exception ex)
            {
                _logger.LogError($"初始化语音助手失败: {ex.Message}");
                VoiceAssistantStatus = "启动失败";
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

        private void OnMaximize()
        {
            var window = Application.Current.MainWindow;

            if (_isWindowMinimized)
            {
                //window.WindowState = WindowState.Maximized;

                // 最大化时铺满工作区
                window.Top = 0;
                window.Left = 0;
                window.Width = SystemParameters.WorkArea.Width;
                window.Height = SystemParameters.WorkArea.Height;

                //if (window.Content is FrameworkElement rootElement)
                //{
                //    rootElement.Margin = new Thickness(0, 6, 0, 0);
                //}

                _isWindowMinimized = false;
                _isWindowMaximized = true;
            }
            else
            {
                window.WindowState = WindowState.Normal;
                window.Width = 1200;
                window.Height = 800;

                var screenWidth = SystemParameters.PrimaryScreenWidth;
                var screenHeight = SystemParameters.PrimaryScreenHeight;
                window.Left = (screenWidth - window.Width) / 2;
                window.Top = (screenHeight - window.Height) / 2;

                //if (window.Content is FrameworkElement rootElement)
                //{
                //    rootElement.Margin = new Thickness(0);
                //}

                _isWindowMinimized = true;
                _isWindowMaximized = false;
            }

            UpdateMaximizeTooltip();
        }

        private void OnMaximizeNoPosition()
        {
            var window = Application.Current.MainWindow;

            if (_isWindowMinimized)
            {
                var workingArea = SystemParameters.WorkArea;
                window.Width = workingArea.Width;
                window.Height = workingArea.Height;

                window.WindowState = WindowState.Normal;
                _isWindowMinimized = false;
                _isWindowMaximized = true;
            }
            else
            {
                window.Width = 1200;
                window.Height = 800;

                _isWindowMinimized = true;
                _isWindowMaximized = false;
            }

            UpdateMaximizeTooltip();
        }

        private void OnClose()
        {
            try
            {
                if (!_isMuted && _isAwake)
                {
                    _voiceAssistant.Speak("再见,欢迎下次使用");
                }

                _voiceAssistant?.Stop();
            }
            catch (Exception ex)
            {
                _logger.LogError($"关闭语音助手时出错: {ex.Message}");
            }
            finally
            {
                Application.Current.Shutdown();
            }
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
            }
            catch (Exception ex)
            {
                _logger.LogCritical($"DragMove 异常: {ex.Message}");
            }
        }

        private void OnToggleVoiceAssistant()
        {
            try
            {
                if (_voiceAssistant.IsListening)
                {
                    _voiceAssistant.Stop();
                    VoiceAssistantStatus = "已停止";
                    IsVoiceInteractionVisible = false;
                    _logger.LogInformation("用户手动停止语音助手");
                }
                else
                {
                    _voiceAssistant.Start();
                    VoiceAssistantStatus = "等待唤醒";

                    // 显示启动提示
                    IsVoiceInteractionVisible = true;
                    RecognizedText = string.Empty;
                    AssistantReply = "语音助手已启动，请说'你好小桐'唤醒我";

                    // 5秒后自动关闭提示
                    System.Threading.Tasks.Task.Delay(5000).ContinueWith(_ =>
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (!IsAwake && IsVoiceInteractionVisible)
                            {
                                OnCloseVoiceInteraction();
                            }
                        });
                    });

                    _logger.LogInformation("用户手动启动语音助手");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"切换语音助手状态失败: {ex.Message}");
                VoiceAssistantStatus = "错误";
            }
        }

        private void OnToggleMute()
        {
            IsMuted = !IsMuted;
        }

        private void OnTestVoice()
        {
            try
            {
                if (!_voiceAssistant.IsListening)
                {
                    _logger.LogWarning("语音助手未启动，无法测试");
                    return;
                }

                var testMessages = new[]
                {
                    "你好，我是小桐",
                    "语音测试成功",
                    $"现在时间是 {DateTime.Now:HH点mm分}",
                    "MaxChemical 智能助手为您服务"
                };

                var random = new Random();
                var message = testMessages[random.Next(testMessages.Length)];

                _voiceAssistant.SpeakAsync(message);
                _logger.LogInformation($"测试语音: {message}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"测试语音失败: {ex.Message}");
            }
        }

        private void OnCloseVoiceInteraction()
        {
            IsVoiceInteractionVisible = false;
            RecognizedText = string.Empty;
            AssistantReply = string.Empty;
        }

        #endregion

        #region Voice Assistant Event Handlers

        private void OnListeningStateChanged(object sender, bool isListening)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsVoiceAssistantActive = isListening;
                IsListening = isListening && IsAwake;

                if (!isListening && VoiceAssistantStatus != "已停止")
                {
                    VoiceAssistantStatus = "已停止";
                    IsAwake = false;
                    IsVoiceInteractionVisible = false;
                }
                else if (isListening && VoiceAssistantStatus == "已停止")
                {
                    VoiceAssistantStatus = "等待唤醒";
                }

                _logger.LogInformation($"语音助手监听状态: {(isListening ? "开启" : "关闭")}");
            });
        }

        private void OnSpeechStarted(object sender, string text)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                _logger.LogDebug($"开始语音输出: {text}");
            });
        }

        private void OnVoiceCommandRecognized(object sender, string command)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                _logger.LogInformation($"收到语音命令: {command}");

                // 显示交互界面（除了自动休眠命令）
                if (command != "AUTO_SLEEP")
                {
                    IsVoiceInteractionVisible = true;
                    RecognizedText = command;
                }

                switch (command)
                {
                    case "WAKE_UP":
                        VoiceAssistantStatus = "小桐在线 🎤";
                        IsAwake = true;
                        IsListening = true;
                        AssistantReply = "你好！我是小桐，有什么可以帮您的？";
                        _logger.LogInformation("语音助手已唤醒");
                        break;

                    case "AUTO_SLEEP":
                        VoiceAssistantStatus = "自动休眠 💤";
                        IsAwake = false;
                        IsListening = false;
                        IsVoiceInteractionVisible = false;
                        _logger.LogInformation("语音助手自动休眠");
                        break;

                    case "SLEEP":
                        VoiceAssistantStatus = "休眠中 💤";
                        IsAwake = false;
                        IsListening = false;
                        AssistantReply = "好的，我先休息了。需要时说'你好小桐'唤醒我。";
                        // 3秒后关闭 Popup
                        System.Threading.Tasks.Task.Delay(3000).ContinueWith(_ =>
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                IsVoiceInteractionVisible = false;
                                RecognizedText = string.Empty;
                                AssistantReply = string.Empty;
                            });
                        });
                        _logger.LogInformation("语音助手已休眠");
                        break;

                    case "最大化窗口":
                        MaximizeCommand.Execute();
                        AssistantReply = "已为您最大化窗口";
                        break;

                    case "最小化窗口":
                        MinimizeCommand.Execute();
                        AssistantReply = "已为您最小化窗口";
                        break;

                    case "还原窗口":
                        if (_isWindowMaximized)
                        {
                            MaximizeCommand.Execute();
                            AssistantReply = "已为您还原窗口";
                        }
                        break;

                    case "关闭窗口":
                        AssistantReply = "正在为您关闭窗口，再见！";
                        System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ =>
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                CloseCommand.Execute();
                            });
                        });
                        break;

                    case "静音":
                        IsMuted = true;
                        AssistantReply = "好的，已为您静音";
                        break;

                    case "取消静音":
                        IsMuted = false;
                        AssistantReply = "好的，已为您取消静音";
                        break;

                    case "现在几点":
                        AssistantReply = $"现在是 {DateTime.Now:HH点mm分}";
                        break;

                    case "今天星期几":
                        var dayOfWeek = DateTime.Now.DayOfWeek switch
                        {
                            DayOfWeek.Monday => "星期一",
                            DayOfWeek.Tuesday => "星期二",
                            DayOfWeek.Wednesday => "星期三",
                            DayOfWeek.Thursday => "星期四",
                            DayOfWeek.Friday => "星期五",
                            DayOfWeek.Saturday => "星期六",
                            DayOfWeek.Sunday => "星期日",
                            _ => ""
                        };
                        AssistantReply = $"今天是{dayOfWeek}";
                        break;

                    case "今天日期":
                        AssistantReply = $"今天是 {DateTime.Now:yyyy年MM月dd日}";
                        break;

                    default:
                        if (IsAwake)
                        {
                            AssistantReply = $"收到您的指令：{command}";
                        }
                        break;
                }

                // 自动关闭 Popup（15秒后）
                if (IsVoiceInteractionVisible && command != "WAKE_UP")
                {
                    System.Threading.Tasks.Task.Delay(15000).ContinueWith(_ =>
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (IsVoiceInteractionVisible)
                            {
                                OnCloseVoiceInteraction();
                            }
                        });
                    });
                }
            });
        }

        #endregion

        #region Event Handlers

        private void OnUserLogin(UserInfo userInfo)
        {
            CurrentUser = userInfo;

            if (_voiceAssistant.IsListening && !_isMuted)
            {
                // _voiceAssistant.SpeakAsync($"欢迎回来，{userInfo.DisplayName}");
            }
        }

        private void OnUserLogout()
        {
            CurrentUser = null;

            if (_voiceAssistant.IsListening && !_isMuted && IsAwake)
            {
                _voiceAssistant.SpeakAsync("再见");
            }
        }

        private void OnLanguageChanged(string cultureName)
        {
            LoadLocalizedStrings();
            UpdateLoginText();

            _logger.LogInformation($"语言已切换为: {cultureName}");
        }

        #endregion

        public void Dispose()
        {
            try
            {
                _logger.LogInformation("正在清理 CustomTitleBarViewModel 资源");

                if (_voiceAssistant != null)
                {
                    _voiceAssistant.ListeningStateChanged -= OnListeningStateChanged;
                    _voiceAssistant.CommandRecognized -= OnVoiceCommandRecognized;

                    if (_voiceAssistant is PiperVoiceAssistantService piperService)
                    {
                        piperService.SpeechStarted -= OnSpeechStarted;
                    }
                }

                _voiceAssistant?.Stop();

                if (_voiceAssistant is IDisposable disposable)
                {
                    disposable.Dispose();
                }

                _logger.LogInformation("CustomTitleBarViewModel 资源清理完成");
            }
            catch (Exception ex)
            {
                _logger.LogError($"清理资源时出错: {ex.Message}");
            }
        }
    }
}