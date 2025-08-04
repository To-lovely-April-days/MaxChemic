using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using MaxChemical.Core;
using MaxChemical.Logging;

namespace DevicePlugins.Devices
{
    /// <summary>
    /// 设备基类
    /// </summary>
    public abstract class Device : IDevice
    {

        private readonly ILogService _logger;

        protected Device()
        {
            _logger = LogManager.GetLogger(GetType().Name);
        }

        #region Properties

        /// <summary>
        /// 设备名称
        /// </summary>
        public string Name { get; protected set; } = string.Empty;

        /// <summary>
        /// 制造商
        /// </summary>
        public string Manufacturer { get; protected set; } = string.Empty;

        /// <summary>
        /// 图标位置
        /// </summary>
        public string ImageLocation { get; protected set; } = string.Empty;

        /// <summary>
        /// 设备类别
        /// </summary>
        public string Category { get; protected set; } = string.Empty;

        /// <summary>
        /// 设备参数
        /// </summary>
        public DeviceParameters Parameters { get; } = new DeviceParameters();

        /// <summary>
        /// 设备命令
        /// </summary>
        public List<DeviceCommand> Commands { get; } = new List<DeviceCommand>();

        // 内部存储字段
        private List<RegionType> _allowedRegions = new List<RegionType>();

        /// <summary>
        /// 允许的区域类型列表(新接口)
        /// </summary>
        public virtual List<RegionType> AllowedRegions
        {
            get => _allowedRegions;
            protected set => _allowedRegions = value ?? new List<RegionType>();
        }

        /// <summary>
        /// 允许的区域类型(兼容旧接口)
        /// </summary>
        public virtual RegionType AllowedRegion
        {
            get => _allowedRegions.FirstOrDefault();
            protected set
            {
                if (value == RegionType.None)
                {
                    _allowedRegions.Clear();
                }
                else
                {
                    _allowedRegions.Clear();
                    _allowedRegions.Add(value);
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(AllowedRegions));
            }
        }

        #endregion

        #region Events

        /// <summary>
        /// 属性变更事件
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        #endregion

        #region Methods

        /// <summary>
        /// 触发属性变更通知
        /// </summary>
        /// <param name="name">属性名称</param>
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>
        /// 初始化设备
        /// </summary>
        public virtual void Initialize()
        {
            TraceLog("设备初始化开始");

            try
            {
                // 子类可以重写此方法进行具体的初始化操作
                OnInitialize();

                TraceLog("设备初始化完成");
            }
            catch (Exception ex)
            {
                ErrorLog("设备初始化失败", ex);
                throw;
            }
        }

        /// <summary>
        /// 关闭设备
        /// </summary>
        public virtual void Shutdown()
        {
            TraceLog("设备关闭开始");

            try
            {
                // 子类可以重写此方法进行具体的关闭操作
                OnShutdown();

                TraceLog("设备关闭完成");
            }
            catch (Exception ex)
            {
                ErrorLog("设备关闭失败", ex);
                throw;
            }
        }

        /// <summary>
        /// 具体的初始化实现(子类重写)
        /// </summary>
        protected virtual void OnInitialize()
        {
            // 子类实现具体初始化逻辑
        }

        /// <summary>
        /// 具体的关闭实现(子类重写)
        /// </summary>
        protected virtual void OnShutdown()
        {
            // 子类实现具体关闭逻辑
        }

        #endregion

        #region 日志
        /// <summary>
        /// 记录跟踪日志
        /// </summary>
        /// <param name="message">日志消息</param>
        protected void TraceLog(string message)
        {
            _logger.LogTrace("[{DeviceName}]: {Message}", Name, message);
        }

        /// <summary>
        /// 记录调试日志
        /// </summary>
        /// <param name="message">调试消息</param>
        protected void DebugLog(string message)
        {
            _logger.LogDebug("[{DeviceName}]: {Message}", Name, message);
        }

        /// <summary>
        /// 记录信息日志
        /// </summary>
        /// <param name="message">信息消息</param>
        protected void InfoLog(string message)
        {
            _logger.LogInformation("[{DeviceName}]: {Message}", Name, message);
        }

        /// <summary>
        /// 记录警告日志
        /// </summary>
        /// <param name="message">警告消息</param>
        protected void WarnLog(string message)
        {
            _logger.LogWarning("[{DeviceName}]: {Message}", Name, message);
        }

        /// <summary>
        /// 记录错误日志
        /// </summary>
        /// <param name="message">错误消息</param>
        /// <param name="exception">异常对象</param>
        protected void ErrorLog(string message, Exception? exception = null)
        {
            if (exception != null)
            {
                _logger.LogError(exception, "[{DeviceName}]: {Message}", Name, message);
            }
            else
            {
                _logger.LogError("[{DeviceName}]: {Message}", Name, message);
            }
        }
        #endregion
    }
}