using MaxChemical.Core;
using MaxChemical.Shell.Utils;
using Prism.Mvvm;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace MaxChemical.Shell.ViewModels
{
    public class LogMessageViewerViewModel : BindableBase, IDisposable
    {
        // 内部字段
        private bool _disposed;
        private bool _isEnbable = true;
        private int _maxCount = 1000;
        private ObservableCollection<LogMessage> _logMessages = new ObservableCollection<LogMessage>();
        // 
        private readonly SemaphoreSlim _slim = new SemaphoreSlim(1, 1);
        // 批量删除计时器
        private readonly DispatcherTimer _timer;

        public LogMessageViewerViewModel()
        {
            // 注册日志记录接收事件
            LogMessageCache.Instance.LogMessageReceived += Instance_LogMessageReceived;

            // 初始化批量删除计时器
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(60);
            _timer.Tick += new EventHandler(MaxCountChanged_Tick);
        }


        /// <summary>
        /// 最大显示数量
        /// </summary>
        public int MaxCount
        {
            get => _maxCount;
            set
            {
                if (value != _maxCount && value > 0)
                {
                    SetProperty(ref _maxCount, value);
                    MaxCountChangedHandler();
                }
            }
        }

        /// <summary>
        /// 是否启用下拉列表
        /// </summary>
        public bool IsEnable
        {
            get => _isEnbable;
            set => SetProperty(ref _isEnbable, value);
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
            if (_disposed)
            {
                return;
            }

            // 取消日志记录接收事件的注册
            LogMessageCache.Instance.LogMessageReceived -= Instance_LogMessageReceived;
            // 停止批量删除
            _timer.Stop();

            _disposed = true;
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Instance_LogMessageReceived(object? sender, List<LogMessage> e)
        {
            if (e == null || e.Count == 0)
            {
                return;
            }

            var logs = new List<LogMessage>(e);

            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                foreach (var item in logs)
                {
                    try
                    {
                        if (_logMessages.Count >= _maxCount)
                        {
                            _logMessages.RemoveAt(0);
                        }
                        _logMessages.Add(item);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"日志记录添加失败：{ex.Message}");
                    }
                }
            }, DispatcherPriority.Background);
        }

        /// <summary>
        /// 处理缓存数量的更改
        /// </summary>
        private void MaxCountChangedHandler()
        {
            // 是否需要删除
            if (_logMessages.Count > _maxCount && !_timer.IsEnabled)
            {
                IsEnable = false;
                _timer.Start();
            }
        }

        private void MaxCountChanged_Tick(object? sender, EventArgs e)
        {
            try
            {
                if (_disposed)
                {
                    return;
                }

                _slim.Wait();

                // 删除完成，启用下拉列表，停止批量删除计时器
                if (_logMessages.Count <= _maxCount)
                {
                    IsEnable = true;
                    _timer.Stop();
                    return;
                }

                // 获取批量删除的数量
                int delCount = _logMessages.Count - _maxCount;
                // 每次最多删除10条，避免UI长时间阻塞
                delCount = Math.Min(delCount, 10);

                for (int i = 0; i < delCount; i++)
                {
                    LogMessages.RemoveAt(0);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"响应缓存数量更改，移除旧记录失败：{ex.Message}");
            }
            finally
            {
                // 释放信号，允许计时器下一次轮询处理
                _slim.Release();
            }
        }
    }
}
