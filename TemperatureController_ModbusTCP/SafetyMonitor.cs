using System;
using System.Collections.Generic;
using System.Linq;

namespace MicroReactor_ModbusTCP
{
    /// <summary>
    /// 安全监测器 - 跟踪温度偏差、变化速率和趋势,触发分级报警
    /// </summary>
    /// <remarks>
    /// 设计思路:
    /// - 不修改任何控制输出,只产生"报警标志"和"建议动作"
    /// - 控制器自己决定是否响应(停止微调/限制循环器命令/保持现状)
    /// - 所有阈值由 SafetyMonitorConfig 配置
    /// </remarks>
    public class SafetyMonitor
    {
        private readonly SafetyMonitorConfig _config;
        private readonly SmartControlLogger _logger;
        private readonly string _sensorName;

        // 温度历史(用于速率和趋势计算)
        private readonly Queue<TempReading> _tempHistory = new Queue<TempReading>();
        private const int MaxHistoryPoints = 60;

        // 大偏差持续计时(用于收敛性判据)
        private DateTime? _largeDeviationStartTime = null;

        // 当前状态
        public SafetyState CurrentState { get; private set; } = SafetyState.Normal;
        public string LastAlarmMessage { get; private set; } = "";
        public DateTime? LastAlarmTime { get; private set; }

        public SafetyMonitor(string sensorName, SafetyMonitorConfig config, SmartControlLogger logger)
        {
            _sensorName = sensorName ?? throw new ArgumentNullException(nameof(sensorName));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger;
        }

        /// <summary>
        /// 提交一次温度读数,返回当前安全评估结果
        /// </summary>
        public SafetyAssessment Assess(double currentTemp, double targetTemp, double currentCirculatorTemp)
        {
            var now = DateTime.Now;

            _tempHistory.Enqueue(new TempReading
            {
                Time = now,
                MaterialTemp = currentTemp,
                CirculatorTemp = currentCirculatorTemp
            });
            while (_tempHistory.Count > MaxHistoryPoints)
                _tempHistory.Dequeue();

            var assessment = new SafetyAssessment
            {
                AssessTime = now,
                CurrentTemp = currentTemp,
                TargetTemp = targetTemp,
                Deviation = currentTemp - targetTemp
            };

            CheckLargeDeviation(assessment, now);
            CheckTemperatureRate(assessment, now);
            CheckTrendPrediction(assessment, now);

            assessment.OverallState = DetermineOverallState(assessment);
            CurrentState = assessment.OverallState;

            if (assessment.HasNewAlarm)
            {
                LastAlarmMessage = assessment.AlarmReason;
                LastAlarmTime = now;
                _logger?.LogWarning($"[安全报警] {_sensorName}", assessment.AlarmReason);
            }

            return assessment;
        }

        /// <summary>
        /// 检查 1: 收敛性判据
        /// 偏差绝对值"不再缩小"持续 ConvergenceCheckSec 秒 → 报警
        /// 升温/降温过程中偏差大是正常的,只要在朝目标缩小就不报警
        /// 只有"卡住"(温度不再朝目标接近)才视为异常
        /// </summary>
        private void CheckLargeDeviation(SafetyAssessment assessment, DateTime now)
        {
            double absDeviation = Math.Abs(assessment.Deviation);

            // 偏差小于阈值 → 完全不需要检查,清零状态
            if (absDeviation <= _config.AlarmDeviationDeg)
            {
                _largeDeviationStartTime = null;
                return;
            }

            // 数据点不够,无法判断收敛性
            if (_tempHistory.Count < 5)
            {
                return;
            }

            // 取最近 ConvergenceCheckSec 秒的数据评估收敛性
            double checkWindowSec = _config.AlarmDurationSec;
            var recentData = _tempHistory
                .Where(r => (now - r.Time).TotalSeconds <= checkWindowSec)
                .ToList();

            // 数据窗口还没集满 → 还在观察期,不报警
            double dataSpanSec = recentData.Count >= 2
                ? (recentData.Last().Time - recentData.First().Time).TotalSeconds
                : 0;

            if (dataSpanSec < checkWindowSec * 0.8)
            {
                if (_largeDeviationStartTime == null)
                    _largeDeviationStartTime = now;
                return;
            }

            // 评估收敛性: 偏差绝对值的线性回归斜率
            // 斜率 < 0 表示偏差在缩小(收敛中,正常)
            // 斜率 ≈ 0 或 > 0 表示偏差不再缩小(卡住,异常)
            double convergenceRate = CalculateAbsDeviationSlope(recentData, assessment.TargetTemp);
            assessment.ConvergenceRatePerSec = convergenceRate;

            // 容忍度: 偏差缩小速率 < 0.005℃/秒 (即 0.3℃/分钟) → 视为"几乎不缩小"
            const double MinConvergenceRate = -0.005;

            if (convergenceRate > MinConvergenceRate)
            {
                if (_largeDeviationStartTime == null)
                {
                    _largeDeviationStartTime = now;
                }

                double stuckDurationSec = (now - _largeDeviationStartTime.Value).TotalSeconds;
                assessment.LargeDeviationDurationSec = stuckDurationSec;

                if (stuckDurationSec >= checkWindowSec)
                {
                    assessment.LargeDeviationAlarm = true;
                    string trend = convergenceRate >= 0
                        ? "偏差不再缩小"
                        : $"缩小速率仅 {-convergenceRate * 60:F2}℃/分钟,过慢";
                    assessment.AlarmReason =
                        $"收敛性异常: 当前偏差 {assessment.Deviation:F2}℃, {trend}, " +
                        $"持续 {stuckDurationSec:F0}秒 (窗口 {checkWindowSec:F0}秒)";
                }
            }
            else
            {
                // 偏差在持续缩小 → 正常
                _largeDeviationStartTime = null;
            }
        }

        /// <summary>
        /// 计算"偏差绝对值"对时间的线性回归斜率
        /// 单位: ℃/秒
        /// 负值表示偏差在缩小(系统在收敛)
        /// 正值或接近 0 表示偏差不再缩小(系统卡住)
        /// </summary>
        private double CalculateAbsDeviationSlope(List<TempReading> data, double targetTemp)
        {
            if (data.Count < 2) return 0;

            var firstTime = data.First().Time;
            double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
            int n = data.Count;

            foreach (var r in data)
            {
                double x = (r.Time - firstTime).TotalSeconds;
                double y = Math.Abs(r.MaterialTemp - targetTemp);
                sumX += x;
                sumY += y;
                sumXY += x * y;
                sumXX += x * x;
            }

            double denom = n * sumXX - sumX * sumX;
            if (Math.Abs(denom) < 1e-6) return 0;

            return (n * sumXY - sumX * sumY) / denom;
        }

        /// <summary>
        /// 检查 2: 温度变化速率
        /// </summary>
        private void CheckTemperatureRate(SafetyAssessment assessment, DateTime now)
        {
            if (_tempHistory.Count < 5) return;

            var recentReadings = _tempHistory
                .Where(r => (now - r.Time).TotalSeconds <= 60)
                .ToList();

            if (recentReadings.Count < 3) return;

            var first = recentReadings.First();
            var last = recentReadings.Last();
            double timeSpanSec = (last.Time - first.Time).TotalSeconds;

            if (timeSpanSec < 1) return;

            double tempChange = last.MaterialTemp - first.MaterialTemp;
            double rateDegPerMin = (tempChange / timeSpanSec) * 60.0;
            assessment.CurrentRateDegPerMin = rateDegPerMin;

            double absRate = Math.Abs(rateDegPerMin);
            if (absRate > _config.MaxRateDegPerMin)
            {
                assessment.RateAlarm = true;
                assessment.AlarmReason = $"温度变化速率过快: {rateDegPerMin:F1}℃/min " +
                                         $"超过限值 ±{_config.MaxRateDegPerMin}℃/min";
            }
        }

        /// <summary>
        /// 检查 3: 趋势预测
        /// </summary>
        private void CheckTrendPrediction(SafetyAssessment assessment, DateTime now)
        {
            if (_tempHistory.Count < 5) return;

            var recentReadings = _tempHistory
                .Where(r => (now - r.Time).TotalSeconds <= 30)
                .ToList();

            if (recentReadings.Count < 3) return;

            var firstTime = recentReadings.First().Time;
            double sumT = 0, sumX = 0, sumXY = 0, sumXX = 0;
            int n = recentReadings.Count;

            foreach (var r in recentReadings)
            {
                double x = (r.Time - firstTime).TotalSeconds;
                double y = r.MaterialTemp;
                sumX += x;
                sumT += y;
                sumXY += x * y;
                sumXX += x * x;
            }

            double denom = n * sumXX - sumX * sumX;
            if (Math.Abs(denom) < 1e-6) return;

            double slope = (n * sumXY - sumX * sumT) / denom;
            double intercept = (sumT - slope * sumX) / n;

            double currentX = (now - firstTime).TotalSeconds;
            double futureX = currentX + _config.TrendLookaheadSec;
            double predictedTemp = slope * futureX + intercept;

            assessment.PredictedTempLookahead = predictedTemp;
            assessment.PredictedDeviation = predictedTemp - assessment.TargetTemp;

            if (Math.Abs(assessment.PredictedDeviation) > _config.TrendAlarmDeviationDeg)
            {
                assessment.TrendAlarm = true;
                if (string.IsNullOrEmpty(assessment.AlarmReason))
                {
                    assessment.AlarmReason = $"趋势预测异常: {_config.TrendLookaheadSec:F0}秒后预计温度 " +
                                             $"{predictedTemp:F1}℃,偏差 {assessment.PredictedDeviation:F1}℃";
                }
            }
        }

        /// <summary>
        /// 综合判定整体状态
        /// </summary>
        private SafetyState DetermineOverallState(SafetyAssessment assessment)
        {
            bool wasNormal = CurrentState == SafetyState.Normal;

            if (assessment.LargeDeviationAlarm || assessment.RateAlarm)
            {
                if (wasNormal || CurrentState == SafetyState.SoftAlarm)
                {
                    assessment.HasNewAlarm = true;
                }
                return SafetyState.HardAlarm;
            }

            if (assessment.TrendAlarm)
            {
                if (wasNormal)
                {
                    assessment.HasNewAlarm = true;
                }
                return SafetyState.SoftAlarm;
            }

            return SafetyState.Normal;
        }

        /// <summary>
        /// 重置监测器状态(目标温度变更时调用)
        /// </summary>
        public void Reset()
        {
            _tempHistory.Clear();
            _largeDeviationStartTime = null;
            CurrentState = SafetyState.Normal;
            LastAlarmMessage = "";
            LastAlarmTime = null;
            _logger?.LogInfo($"[安全监测器已重置] {_sensorName}");
        }

        private class TempReading
        {
            public DateTime Time { get; set; }
            public double MaterialTemp { get; set; }
            public double CirculatorTemp { get; set; }
        }
    }

    /// <summary>
    /// 安全监测器配置
    /// </summary>
    public class SafetyMonitorConfig
    {
        /// <summary>大偏差阈值(℃),默认 10</summary>
        public double AlarmDeviationDeg { get; set; } = 10.0;

        /// <summary>收敛性检查窗口(秒),默认 120 (2 分钟)</summary>
        public double AlarmDurationSec { get; set; } = 120.0;

        /// <summary>温度变化速率上限(℃/分钟),默认 10</summary>
        public double MaxRateDegPerMin { get; set; } = 10.0;

        /// <summary>趋势预测前瞻时间(秒),默认 120</summary>
        public double TrendLookaheadSec { get; set; } = 120.0;

        /// <summary>趋势预测偏差报警阈值(℃),默认 15</summary>
        public double TrendAlarmDeviationDeg { get; set; } = 15.0;
    }

    /// <summary>
    /// 安全状态等级
    /// </summary>
    public enum SafetyState
    {
        Normal,
        SoftAlarm,
        HardAlarm
    }

    /// <summary>
    /// 单次安全评估结果
    /// </summary>
    public class SafetyAssessment
    {
        public DateTime AssessTime { get; set; }
        public double CurrentTemp { get; set; }
        public double TargetTemp { get; set; }
        public double Deviation { get; set; }

        public bool LargeDeviationAlarm { get; set; }
        public double LargeDeviationDurationSec { get; set; }

        public bool RateAlarm { get; set; }
        public double CurrentRateDegPerMin { get; set; }

        public bool TrendAlarm { get; set; }
        public double PredictedTempLookahead { get; set; }
        public double PredictedDeviation { get; set; }

        public SafetyState OverallState { get; set; }
        public bool HasNewAlarm { get; set; }
        public string AlarmReason { get; set; } = "";

        /// <summary>
        /// 偏差绝对值的变化速率 (℃/秒)
        /// 负值=偏差在缩小(收敛中); 正值或接近 0=偏差不再缩小(卡住)
        /// </summary>
        public double ConvergenceRatePerSec { get; set; }

        public bool ShouldStopAdjusting => OverallState == SafetyState.HardAlarm;

        public override string ToString()
        {
            return OverallState switch
            {
                SafetyState.Normal => "[正常]",
                SafetyState.SoftAlarm => $"[软报警]({AlarmReason})",
                SafetyState.HardAlarm => $"[硬报警]({AlarmReason})",
                _ => "[未知]"
            };
        }
    }
}