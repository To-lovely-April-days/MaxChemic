using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using DevicePlugins.Devices;
using MaxChemical.Core;
using MaxChemical.Logging;

namespace ConicalFlask_ModbusTCP
{
    [Export(typeof(IDevice))]
    public class ConicalFlask_ModbusTCP : Device, IControllableDevice, IFlowLifecycleAware
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
                InfoLog("锥形瓶设备没有需要暂停的活跃状态");
            }
            return true;
        }

        public virtual async Task<bool> ResumeAllPausedStatesAsync()
        {
            lock (_statesLock)
            {
                InfoLog("锥形瓶设备没有需要恢复的暂停状态");
            }
            return true;
        }

        public virtual async Task<bool> StopAllActiveStatesAsync()
        {
            lock (_statesLock)
            {
                InfoLog("锥形瓶设备没有需要停止的活跃状态");
            }
            return true;
        }

        #endregion

        #region IFlowLifecycleAware Implementation

        public virtual async Task<bool> OnFlowStartedAsync(FlowStartContext context)
        {
            try
            {
                InfoLog($"锥形瓶设备流程开始: {context.FlowName}");
                InfoLog("锥形瓶设备已准备好参与流程");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("锥形瓶设备流程开始处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowCompletedAsync(FlowCompletionContext context)
        {
            try
            {
                InfoLog($"锥形瓶设备流程完成: {context.FlowName}, 成功: {context.IsSuccessful}");
                InfoLog("锥形瓶设备流程完成处理结束");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("锥形瓶设备流程完成处理失败", ex);
                return false;
            }
        }

        public virtual async Task<bool> OnFlowFailedAsync(FlowFailureContext context)
        {
            try
            {
                ErrorLog($"锥形瓶设备流程失败: {context.FlowName}, 错误: {context.ErrorMessage}");
                InfoLog("锥形瓶设备流程失败处理完成");
                return true;
            }
            catch (Exception ex)
            {
                ErrorLog("锥形瓶设备流程失败处理异常", ex);
                return false;
            }
        }

        #endregion

        public ConicalFlask_ModbusTCP()
        {
            _logger = LogManager.GetLogger<ConicalFlask_ModbusTCP>();

            Name = "锥形瓶";
            Manufacturer = "ModbusTCP锥形瓶";
            Category = DeviceCategories.LiquidAccumulator;
            ComId = "CF0101"; // 默认设备ID

            ConnectionManager = DeviceConnectionManagerFactory.GetInstance();

            AllowedRegions = new List<RegionType>()
            {
                RegionType.Feed,
                RegionType.PreHeat,
                RegionType.Reaction,
                RegionType.Quench,
                RegionType.PostProcess
            };
            //  使用特殊标记表示使用自定义控件
            ImageLocation = "CUSTOM_CONTROL:ConicalFlaskControl";

            Parameters.Variables.Add(new StringParameter("DeviceId", "CF0101")
            {
                Options = new ObservableCollection<string>(new List<string>()
                {
                    "CF0101", "CF0201", "CF0301"
                }),
                HelpText = "锥形瓶设备ID"
            });

            Parameters.Variables.Add(new NumberParameter("Volume", 0, 10000, 250, "容量(mL)")
            {
                HelpText = "锥形瓶的容量"
            });

            Parameters.Variables.Add(new StringParameter("Material", "玻璃")
            {
                Options = new ObservableCollection<string>(new List<string>()
                {
                    "玻璃", "塑料", "不锈钢", "PTFE"
                }),
                HelpText = "锥形瓶材质"
            });
            Parameters.Variables.Add(new StringParameter("LiquidName", "HNO₃")
            {
                Options = new ObservableCollection<string>(new List<string>()
    {
        "HNO₃",        // 1 路 - 硝化剂
        "H₂SO₄",       // 2 路 - 脱水剂
        "Fe+HCl"       // 3 路 - 还原剂
    }),
                HelpText = "液体名称标注(硝化还原反应三路:硝酸/硫酸/还原剂)"
            });

            Parameters.Variables.Add(new StringParameter("LiquidColor", "黄色")
            {
                Options = new ObservableCollection<string>(new List<string>()
    {
        "黄色",        // HNO₃ - 浓硝酸放置后微黄
        "淡蓝色",      // H₂SO₄ - 工业惯用色,标识脱水剂
        "绿色"         // Fe+HCl - Fe²⁺ 真实颜色
    }),
                HelpText = "液体颜色(对应液体名称:硝酸黄、硫酸淡蓝、还原液绿)"
            });
            // 锥形瓶是被动设备，不需要初始化命令
            InfoLog("锥形瓶设备初始化完成 - 被动容器设备");
        }

        #region 基类重写方法

        public override void Initialize()
        {
            InfoLog("开始初始化锥形瓶设备");

            try
            {
                base.Initialize();
                InfoLog("锥形瓶设备初始化完成 - 被动容器设备，无需主动控制");
            }
            catch (Exception ex)
            {
                ErrorLog("锥形瓶设备初始化失败", ex);
                throw;
            }
        }

        public override void Shutdown()
        {
            InfoLog("锥形瓶设备关闭 - 被动设备，无需特殊关闭操作");
        }

        protected override async Task<bool> OnConnectAsync()
        {
            InfoLog("锥形瓶设备连接 - 被动设备");
            await Task.Delay(50);
            return true;
        }

        protected override async Task<bool> OnDisconnectAsync()
        {
            InfoLog("锥形瓶设备断开连接 - 被动设备");
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