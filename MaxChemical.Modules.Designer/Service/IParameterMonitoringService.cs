using System;
using System.Collections.Generic;
using MaxChemical.Modules.Designer.Models;

namespace MaxChemical.Modules.Designer.Services
{
    /// <summary>
    /// 参数监控服务接口
    /// </summary>
    public interface IParameterMonitoringService : IDisposable
    {
        /// <summary>
        /// 参数数据更新事件
        /// </summary>
        event EventHandler<ParameterDataUpdatedEventArgs> ParameterDataUpdated;

        /// <summary>
        /// 添加监控参数
        /// </summary>
        /// <param name="unit">★ 新增:参数单位(如 "bar"/"℃"/"mL/min"),用于图表自动识别 Y 轴归属。可选,留空时图表会显示"未分类"</param>
        void AddMonitoredParameter(string deviceId, string deviceName, string commandName,
            string parameterName, string displayName = null, string unit = "");

        /// <summary>
        /// 移除监控参数
        /// </summary>
        void RemoveMonitoredParameter(string deviceId, string commandName, string parameterName);

        /// <summary>
        /// 获取所有监控参数
        /// </summary>
        List<MonitoredParameter> GetMonitoredParameters();

        /// <summary>
        /// 获取指定参数的数据
        /// </summary>
        List<ParameterDataPoint> GetParameterData(string deviceId, string commandName, string parameterName);

        /// <summary>
        /// 清空所有数据
        /// </summary>
        void ClearAllData();

        /// <summary>
        /// 清空指定参数的数据
        /// </summary>
        void ClearParameterData(string deviceId, string commandName, string parameterName);

        /// <summary>
        /// 设置参数启用状态
        /// </summary>
        void SetParameterEnabled(string deviceId, string commandName, string parameterName, bool enabled);

        /// <summary>
        /// 更新参数颜色
        /// </summary>
        void UpdateParameterColor(string deviceId, string commandName, string parameterName, string color);

        /// <summary>
        /// 获取可用参数列表
        /// </summary>
        List<AvailableParameter> GetAvailableParameters();

        /// <summary>
        /// 获取指定时间范围内的参数数据
        /// </summary>
        List<ParameterDataPoint> GetParameterDataInRange(string deviceId, string commandName, string parameterName);

        bool IsFlowCurrentlyRunning();
    }
}