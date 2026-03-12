using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace DevicePlugins.Devices
{
    /// <summary>
    /// 设备命令类
    /// </summary>
    public class DeviceCommand : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _imageLocation = string.Empty;
        private string _helpText = string.Empty;
        private bool _isEnabled = true;

        /// <summary>
        /// 命令名称
        /// </summary>
        public string Name
        {
            get => _name;
            set
            {
                _name = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 图标位置
        /// </summary>
        public string ImageLocation
        {
            get => _imageLocation;
            set
            {
                _imageLocation = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 帮助文本
        /// </summary>
        public string HelpText
        {
            get => _helpText;
            set
            {
                _helpText = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 是否启用
        /// </summary>
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                _isEnabled = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// 输入参数
        /// </summary>
        public DeviceParameters InputParameters { get; set; } = new DeviceParameters();

        /// <summary>
        /// 输出参数
        /// </summary>
        public DeviceParameters OutputParameters { get; set; } = new DeviceParameters();

        /// <summary>
        /// 同步命令执行动作（保留向后兼容）
        /// </summary>
        public Func<DeviceParameters, DeviceParameters>? Action { get; set; }

        /// <summary>
        /// 异步命令执行动作（推荐使用，配合 ExecuteAsync）
        /// </summary>
        public Func<DeviceParameters, Task<DeviceParameters>>? AsyncAction { get; set; }

        /// <summary>
        /// 命令执行前的验证动作
        /// </summary>
        public Func<DeviceParameters, bool>? CanExecute { get; set; }

        /// <summary>
        /// 执行命令（同步，保留向后兼容）
        /// </summary>
        public DeviceCommandResult Execute(DeviceParameters? parameters = null)
        {
            var result = new DeviceCommandResult();

            try
            {
                if (!IsEnabled)
                {
                    result.Success = false;
                    result.ErrorMessage = "命令已禁用";
                    return result;
                }

                var inputParams = parameters ?? new DeviceParameters();

                if (!inputParams.ValidateAll())
                {
                    result.Success = false;
                    result.ErrorMessage = "输入参数验证失败";
                    result.ValidationErrors = inputParams.GetValidationErrors();
                    return result;
                }

                if (CanExecute != null && !CanExecute(inputParams))
                {
                    result.Success = false;
                    result.ErrorMessage = "命令执行条件不满足";
                    return result;
                }

                if (Action != null)
                {
                    result.OutputParameters = Action(inputParams);
                    result.Success = true;
                    result.ExecutionTime = DateTime.Now;
                }
                else if (AsyncAction != null)
                {
                    // 同步调用异步方法时，用 Task.Run 避免死锁
                    result.OutputParameters = Task.Run(() => AsyncAction(inputParams)).GetAwaiter().GetResult();
                    result.Success = true;
                    result.ExecutionTime = DateTime.Now;
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = "命令动作未定义";
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.Exception = ex;
            }

            return result;
        }

        /// <summary>
        /// 异步执行命令（推荐在 async 调用链中使用，避免死锁）
        /// </summary>
        public async Task<DeviceCommandResult> ExecuteAsync(DeviceParameters? parameters = null)
        {
            var result = new DeviceCommandResult();

            try
            {
                if (!IsEnabled)
                {
                    result.Success = false;
                    result.ErrorMessage = "命令已禁用";
                    return result;
                }

                var inputParams = parameters ?? new DeviceParameters();

                if (!inputParams.ValidateAll())
                {
                    result.Success = false;
                    result.ErrorMessage = "输入参数验证失败";
                    result.ValidationErrors = inputParams.GetValidationErrors();
                    return result;
                }

                if (CanExecute != null && !CanExecute(inputParams))
                {
                    result.Success = false;
                    result.ErrorMessage = "命令执行条件不满足";
                    return result;
                }

                if (AsyncAction != null)
                {
                    // 直接 await，不阻塞调用线程
                    result.OutputParameters = await AsyncAction(inputParams).ConfigureAwait(false);
                    result.Success = true;
                    result.ExecutionTime = DateTime.Now;
                }
                else if (Action != null)
                {
                    // 兼容只设置了同步 Action 的命令，放到线程池执行
                    result.OutputParameters = await Task.Run(() => Action(inputParams)).ConfigureAwait(false);
                    result.Success = true;
                    result.ExecutionTime = DateTime.Now;
                }
                else
                {
                    result.Success = false;
                    result.ErrorMessage = "命令动作未定义";
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
                result.Exception = ex;
            }

            return result;
        }

        /// <summary>
        /// 属性变更事件
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// 设备命令执行结果
    /// </summary>
    public class DeviceCommandResult
    {
        /// <summary>
        /// 是否执行成功
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// 错误消息
        /// </summary>
        public string ErrorMessage { get; set; } = string.Empty;

        /// <summary>
        /// 输出参数
        /// </summary>
        public DeviceParameters? OutputParameters { get; set; }

        /// <summary>
        /// 验证错误列表
        /// </summary>
        public List<string> ValidationErrors { get; set; } = new List<string>();

        /// <summary>
        /// 异常对象
        /// </summary>
        public Exception? Exception { get; set; }

        /// <summary>
        /// 执行时间
        /// </summary>
        public DateTime ExecutionTime { get; set; } = DateTime.Now;

        public bool IsSimulationMode { get; set; }
    }
}