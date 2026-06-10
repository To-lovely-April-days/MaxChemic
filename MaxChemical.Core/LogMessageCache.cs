using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Timers;
using System.Windows.Threading;

namespace MaxChemical.Core
{
    /// <summary>
    /// 日志消息缓存，默认保存最近1000条日志
    /// </summary>
    public sealed class LogMessageCache
    {
        // 日志记录缓存的内部实例
        private static LogMessageCache _cache;

        // 记录最大缓存数，内部字段
        private int _maxLogCount;

        // 日志缓存
        private readonly List<LogMessage> _logMessages = new List<LogMessage>();

        // 日志缓存实例锁定对象
        private static readonly object _locker = new object();
        // 日志记录添加锁定对象
        private readonly object _msgLock = new object();

        // 日志记录接收事件的委托对象
        private EventHandler<List<LogMessage>> _receiveHandler;

        private readonly System.Timers.Timer _timer;

        /// <summary>
        /// 私有构造函数，方式外部调创建实例
        /// </summary>
        private LogMessageCache()
        {
            _maxLogCount = 1000;

            _timer = new System.Timers.Timer();
            _timer.Interval = 200;
            _timer.Elapsed += new  ElapsedEventHandler(Timer_Elapsed);
            _timer.Start();
        }


        /// <summary>
        /// 定时器执行方法，批量更显UI显示的日志记录
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void Timer_Elapsed(object? sender, EventArgs e)
        {
            // 事件未注册
            if (_receiveHandler is null)
            {
                return;
            }

            List<LogMessage> lists = null;
            lock (_msgLock)
            {
                lists = _logMessages.ToList();
                _logMessages.Clear();
            }
            // 更新界面显示
            if (lists != null)
            {
                _receiveHandler.Invoke(this, lists);
            }
        }


        /// <summary>
        /// 获取日志记录缓存实例
        /// </summary>
        public static LogMessageCache Instance
        {
            get
            {
                // 第一次判断
                if (_cache == null)
                {
                    lock (_locker)
                    {
                        // 再次判断
                        if (_cache == null)
                        {
                            // 实例化日志记录缓存实例
                            _cache = new LogMessageCache();
                        }
                    }
                }
                // 返回当前实例
                return _cache;
            }
        }

        /// <summary>
        /// 日志记录最大缓存数
        /// </summary>
        public int MaxLogCount
        {
            get => _maxLogCount;
            set
            {
                if (value > 0 && value != _maxLogCount)
                {
                    _maxLogCount = value;
                }
            }
        }


        /// <summary>
        /// 日志消息接收事件 缓存容器 -> UI
        /// </summary>
        public event EventHandler<List<LogMessage>> LogMessageReceived { add { _receiveHandler += value; } remove { _receiveHandler -= value; } }


        #region 添加日志记录的相关公共方法

        public void AddTrace(string templateMessage, params object[] args) => AddLogMessage(LogMessageLevel.Trace, templateMessage, args);

        public void AddDebug(string templateMessage, params object[] args) => AddLogMessage(LogMessageLevel.Debug, templateMessage, args);

        public void AddInfo(string templateMessage, params object[] args) => AddLogMessage(LogMessageLevel.Info, templateMessage, args);

        public void AddWarn(string templateMessage, params object[] args) => AddLogMessage(LogMessageLevel.Warn, templateMessage, args);

        public void AddError(string templateMessage, params object[] args) => AddLogMessage(LogMessageLevel.Err, templateMessage, args);

        public void AddCritical(string templateMessage, params object[] args) => AddLogMessage(LogMessageLevel.Crit, templateMessage, args);

        #endregion


        /// <summary>
        /// 应用程序退出时调用
        /// </summary>
        public void Clear()
        {
            lock (_msgLock)
            {
                _logMessages.Clear();
            }

            _timer.Stop();
            _timer.Dispose();
        }

        /// <summary>
        /// 添加日志消息对象到队列(线程安全+自动移除旧日志)
        /// </summary>
        /// <param name="msg"></param>
        private void AddLogMessage(LogMessageLevel level, string templateMessage, params object[] args)
        {
            // Task 包装，避免阻塞调用线程
            Task.Run(() =>
            {
                var msg = new LogMessage
                {
                    Timestamp = DateTime.Now.ToString("HH:mm:ss"),
                    Level = string.Format("{0}", level),
                    Content = ParseMessageTemplate(templateMessage, args)
                };

                lock (_msgLock)
                {
                    // 达到最大数量，移除最早记录
                    if (_logMessages.Count >= _maxLogCount)
                    {
                        _logMessages.RemoveAt(0);
                    }
                    _logMessages.Add(msg);
                }
            });
        }

        /// <summary>
        /// 解析模板化的字符串
        /// </summary>
        /// <param name="templateMessage"></param>
        /// <param name="args"></param>
        /// <returns></returns>
        private string ParseMessageTemplate(string templateMessage, params object[] args)
        {
            if (string.IsNullOrWhiteSpace(templateMessage))
            {
                return "";
            }

            if (args == null || args.Length == 0)
            {
                return templateMessage;
            }

            try
            {
                int index = 0;

                // 使用正则表达式替换 {任意文本} 字符串
                var msg = Regex.Replace(templateMessage, "\\{[^{}]*\\}", match =>
                {
                    if (index < args.Length)
                    {
                        return string.Format("{0}", args[index++]);
                    }
                    return match.Value;
                });

                return msg;
            }
            catch (Exception)
            {
                return templateMessage;
            }
        }
    }

    /// <summary>
    /// 日志记录实体对象
    /// </summary>
    public sealed class LogMessage
    {
        /// <summary>
        /// 时间
        /// </summary>
        public string Timestamp { get; set; }

        /// <summary>
        /// 日志的严重等级
        /// </summary>
        public string Level { get; set; }

        /// <summary>
        /// 日志的来源
        /// </summary>
        public string Source { get; set; }

        /// <summary>
        /// 日志的内容
        /// </summary>
        public string Content { get; set; }
    }


    /// <summary>
    /// 日志记录的严重等级
    /// </summary>
    public enum LogMessageLevel
    {
        Trace,
        Debug,
        Info,
        Warn,
        Err,
        Crit
    }
}
