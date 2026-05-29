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
        // 内部字段
        private int _maxCount = 1000;
        private int _oldMaxCount = 1000;
        private ObservableCollection<LogMessage> _logMessages = new ObservableCollection<LogMessage>();

        private readonly SemaphoreSlim _slim = new SemaphoreSlim(1, 1);


        public LogMessageViewerViewModel()
        {
            // 注册日志记录接收事件
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
                    _oldMaxCount = _maxCount;
                    SetProperty(ref _maxCount, value);
                    _ = MaxCountChangedHandler();
                }
            }
        }

        /// <summary>
        /// 日志记录集合
        /// </summary>
        public ObservableCollection<LogMessage> LogMessages
        {
            get => _logMessages;
            set => SetProperty(ref _logMessages, value);
        }

        /// <summary>
        /// 释放相关资源
        /// </summary>
        public void Dispose()
        {
            // 取消日志记录接收事件的注册
            LogMessageCache.Instance.LogMessageReceived -= Instance_LogMessageReceived;
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Instance_LogMessageReceived(object? sender, List<LogMessage> e)
        {
            Application.Current.Dispatcher.BeginInvoke(async () =>
            {
                for (int i = 0; i < e.Count; i++)
                {
                    try
                    {
                        await _slim.WaitAsync(3000);

                        // 超出设定，删除旧记录
                        if (_logMessages.Count >= _maxCount)
                        {
                            _logMessages.RemoveAt(0);
                        }
                        _logMessages.Add(e[i]);
                    }
                    catch { }
                    finally
                    {
                        _slim.Release();
                    }
                }
            }, DispatcherPriority.ApplicationIdle);
        }


        private async Task MaxCountChangedHandler()
        {
            try
            {
                await _slim.WaitAsync(3000);

                // 如果最多显示条数发生更改，由大->小，且当前显示的大于设定的，则批量删除
                if (_oldMaxCount > _maxCount && _logMessages.Count > _maxCount)
                {
                    int del = _logMessages.Count - _maxCount;
                    for (int j = 0; j < del; j++)
                    {
                        _logMessages.RemoveAt(j);
                    }
                }
            }
            catch { }
            finally
            {
                _slim.Release();
            }
        }
    }
}
