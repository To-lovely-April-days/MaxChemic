using MaxChemical.Core;
using Prism.Mvvm;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace MaxChemical.Shell.ViewModels
{
    public class LogMessageViewerViewModel : BindableBase
    {
        private int _maxCount = 1000;
        private ObservableCollection<LogMessage> _logMessages = new ObservableCollection<LogMessage>();

        public LogMessageViewerViewModel()
        {
            _maxCount = LogMessageCache.Instance.MaxLogCount;
            LogMessageCache.Instance.LogMessageReceived += Instance_LogMessageReceived;
        }


        /// <summary>
        /// 最大显示数量
        /// </summary>
        public int MaxCount
        {
            get => _maxCount;
            set
            {
                if (value != _maxCount)
                {
                    SetProperty(ref _maxCount, value);
                    LogMessageCache.Instance.MaxLogCount = value;
                }
            }
        }

        /// <summary>
        /// 
        /// </summary>
        public ObservableCollection<LogMessage> LogMessages
        {
            get => _logMessages;
            set => SetProperty(ref _logMessages, value);
        }


        public void Dispose()
        {
            LogMessageCache.Instance.LogMessageReceived -= Instance_LogMessageReceived;
        }

        private void Instance_LogMessageReceived(object? sender, List<LogMessage> e)
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                for (int i = 0; i < e.Count; i++)
                {
                    if (_logMessages.Count >= _maxCount)
                    {
                        _logMessages.RemoveAt(0);
                    }
                    _logMessages.Add(e[i]);
                }
                RaisePropertyChanged(nameof(LogMessage));
            }, DispatcherPriority.ApplicationIdle);
        }
    }
}
