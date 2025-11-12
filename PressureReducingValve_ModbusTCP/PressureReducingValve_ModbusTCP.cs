using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;

namespace PressureReducingValve_ModbusTCP
{
    [Export(typeof(IDevice))]
    public class PressureReducingValve_ModbusTCP : Device, IControllableDevice, IFlowLifecycleAware
    {
        private readonly ILogService _logger;
        private readonly object _stateLock = new object();

        // *** 状态跟踪相关字段 ***
        private readonly List<DeviceActiveState> _activeStates = new List<DeviceActiveState>();
        private readonly List<DeviceActiveState> _pausedStates = new List<DeviceActiveState>();
        private readonly object _statesLock = new object();

        #region IControllableDevice Implementation

        public List<DeviceActiveState> ActiveStates
        {
            get
            {
                lock (_statesLock)
                {
                    return new List<DeviceActiveState>(_activeStates);
                }
            }
        }

        public event EventHandler<DeviceStateChangedEventArgs> StateChanged;

        public virtual async Task<bool> PauseAllActiveStatesAsync()
        {
            lock (_statesLock)
            {
                InfoLog("减压阀设备没有需要暂停的活跃状态");
            }
            return true;
        }

        public virtual async Task<bool> ResumeAllPausedStatesAsync()
        {
            lock (_statesLock)
            {
                InfoLog("减压阀设备没有需要恢复的暂停状态");
            }
            return true;
        }

        public virtual async Task<bool> StopAllActiveStatesAsync()
        {
            lock (_statesLock)
            {
                InfoLog("减压阀设备没有需要停止的活跃状态");
            }
            return true;
        }

        #endregion

        #region IFlowLifecycleAware Implementation

        public virtual async Task<bool> OnFlowStartedAsync(FlowStartContext context)
        {
            try
            {
                InfoLog($"减压阀设备流程开始: {context.FlowName}");
                InfoLog("减压阀设备已准备好参与流程");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("减压阀设备流程开始处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowCompletedAsync(FlowCompletionContext context)
        {
            try
            {
                InfoLog($"减压阀设备流程完成: {context.FlowName}, 成功: {context.IsSuccessful}");
                InfoLog("减压阀设备流程完成处理结束");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("减压阀设备流程完成处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowFailedAsync(FlowFailureContext context)
        {
            try
            {
                ErrorLog($"减压阀设备流程失败: {context.FlowName}, 错误: {context.ErrorMessage}");
                InfoLog("减压阀设备流程失败处理完成");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("减压阀设备流程失败处理异常", ex);
                return false;
            }
        }

        #endregion

        public PressureReducingValve_ModbusTCP()
        {
            _logger = LogManager.GetLogger<PressureReducingValve_ModbusTCP>();

            Name = "减压阀";
            Manufacturer = "ModbusTCP减压阀";
            Category = DeviceCategories.Valves;
            ComId = "PRV0101"; // 默认设备ID

            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();

            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };

            //ImageLocation = "pack://siteoforigin:,,,/Resources/DeviceIcon/PressureReducingValve.png";
            ImageLocation = "CUSTOM_CONTROL:ValveControl";
            Parameters.Variables.Add(new StringParameter("DeviceId", "PRV0101")
            {
                Options = new ObservableCollection<string>(new List<string>()
                {
                    "PRV0101", "PRV0201", "PRV0301"
                }),
                HelpText = "减压阀设备ID"
            });

            Parameters.Variables.Add(new NumberParameter("InletPressure", 0, 50, 20, "进口压力(MPa)")
            {
                HelpText = "减压阀进口压力"
            });

            Parameters.Variables.Add(new NumberParameter("OutletPressure", 0, 50, 5, "出口压力(MPa)")
            {
                HelpText = "减压阀出口压力设定值"
            });

            Parameters.Variables.Add(new NumberParameter("FlowRate", 0, 1000, 50, "流量范围(L/min)")
            {
                HelpText = "减压阀的流量范围"
            });

            Parameters.Variables.Add(new StringParameter("ValveType", "弹簧式")
            {
                Options = new ObservableCollection<string>(new List<string>()
                {
                    "弹簧式", "膜片式", "活塞式", "比例式"
                }),
                HelpText = "减压阀类型"
            });

            // 减压阀是被动压力调节设备，不需要主动控制命令
            InfoLog("减压阀设备初始化完成 - 被动压力调节设备");
        }

        #region 基类重写方法

        public override void Initialize()
        {
            InfoLog("开始初始化减压阀设备");

            try
            {
                base.Initialize();
                InfoLog("减压阀设备初始化完成 - 被动压力调节设备，自动调节出口压力");
            }
            catch (Exception ex)
            {
                ErrorLog("减压阀设备初始化失败", ex);
                throw;
            }
        }

        public override void Shutdown()
        {
            InfoLog("减压阀设备关闭 - 被动设备，无需特殊关闭操作");
        }

        protected override async Task<bool> OnConnectAsync()
        {
            InfoLog("减压阀设备连接 - 被动设备");
            await Task.Delay(50);
            return true;
        }

        protected override async Task<bool> OnDisconnectAsync()
        {
            InfoLog("减压阀设备断开连接 - 被动设备");
            await Task.Delay(50);
            return true;
        }

        protected override async Task<bool> OnCheckConnectionAsync()
        {
            await Task.Delay(10);
            return true; // 被动设备始终连接正常
        }

        #endregion
    }
}