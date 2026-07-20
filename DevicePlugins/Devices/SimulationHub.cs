using System;
using System.Collections.Concurrent;
using System.Linq;

namespace DevicePlugins.Devices
{
    /// <summary>
    /// 全局「模拟模式」状态中枢（进程内单例）。
    ///
    /// 作用：在演示 / 模拟运行时，让本来相互独立的多个设备驱动之间保持
    /// 物理上自洽的联动关系，而不是各自返回随机噪声。当前建模两条物理链路：
    ///
    ///   1) 温度回路：高低温设备（循环器）加热 / 制冷载热流体，载热流体温度决定
    ///      碳化硅微反应器的芯片温度。=> 高低温写入「目标温度」，碳化硅读取
    ///      随时间一阶趋近目标的「回路温度」。
    ///
    ///   2) 进料 / 称重：进料泵（两液一气进料系统 / 平流泵）从放在电子秤上的
    ///      料瓶抽取液体，因此电子秤读数随进料流量持续下降。=> 进料泵写入各自的
    ///      「进料流量」，电子秤据此累积扣重。
    ///
    /// 说明：演示场景为「单套装置」，故采用进程内静态单例。所有成员线程安全。
    /// 温度采用「锚点 + 恒定斜率」的纯时间函数建模，任意数量的读取方在同一时刻
    /// 都得到一致结果（读取不产生副作用）。
    /// </summary>
    public static class SimulationHub
    {
        // ───────────────────────── 温度回路（高低温 → 碳化硅微反应器）─────────────────────────

        private static readonly object _tempLock = new object();

        /// <summary>环境温度（℃），作为上电初值。</summary>
        public const double AmbientTemperatureC = 25.0;

        /// <summary>载热流体升 / 降温的最大速率（℃/秒），演示用，可调。85℃两分钟</summary>
        public const double TemperatureRampRateCPerSec = 2; 

        private static double _targetTempC = AmbientTemperatureC;   // 高低温 SV（目标）
        private static double _anchorTempC = AmbientTemperatureC;   // 上一次改变目标时的当前温度
        private static DateTime _anchorTime = DateTime.Now;         // 上一次改变目标的时刻

        /// <summary>
        /// 由高低温设备发布目标温度（SV，单位 ℃）。会以「当前回路温度」为新锚点，
        /// 使温度曲线连续、无跳变。
        /// </summary>
        public static void PublishTargetTemperature(double celsius)
        {
            lock (_tempLock)
            {
                _anchorTempC = ComputeLoopTemperatureNoLock();
                _targetTempC = celsius;
                _anchorTime = DateTime.Now;
            }
        }

        /// <summary>当前的目标温度（高低温 SV）。</summary>
        public static double TargetTemperatureC
        {
            get { lock (_tempLock) { return _targetTempC; } }
        }

        /// <summary>
        /// 当前载热流体（回路）温度：从锚点温度按恒定斜率一阶趋近目标温度。
        /// 纯时间函数，读取无副作用。
        /// </summary>
        public static double LoopTemperatureC
        {
            get { lock (_tempLock) { return ComputeLoopTemperatureNoLock(); } }
        }

        private static double ComputeLoopTemperatureNoLock()
        {
            double elapsedSec = Math.Max(0.0, (DateTime.Now - _anchorTime).TotalSeconds);
            double diff = _targetTempC - _anchorTempC;
            double maxStep = TemperatureRampRateCPerSec * elapsedSec;
            double step = Math.Sign(diff) * Math.Min(Math.Abs(diff), maxStep);
            return _anchorTempC + step;
        }

        /// <summary>
        /// 反应器某一测点的模拟温度：回路温度叠加固定测点偏差与小幅测量噪声。
        /// 用于碳化硅芯片 T1~T6 等，使各测点既跟随高低温、又彼此略有差异。
        /// </summary>
        public static double GetReactorTemperatureC(double sensorOffsetC = 0.0, double noiseAmplitudeC = 0.3)
        {
            return LoopTemperatureC + sensorOffsetC + Noise(noiseAmplitudeC);
        }

        // ───────────────────────── 进料 / 称重（进料泵 → 电子秤）─────────────────────────

        // key = 进料源标识（设备实例 Id / 泵通道），value = 该源当前进料流量（mL/min）
        private static readonly ConcurrentDictionary<string, double> _feedFlows =
            new ConcurrentDictionary<string, double>();

        /// <summary>
        /// 进料泵发布自身当前进料（抽液）流量，单位 mL/min。
        /// 停泵 / 流量为 0 时应发布 0（或调用 <see cref="ClearFeedFlow"/>）。
        /// </summary>
        public static void PublishFeedFlow(string sourceId, double milliLitrePerMinute)
        {
            if (string.IsNullOrEmpty(sourceId)) sourceId = "default";
            _feedFlows[sourceId] = Math.Max(0.0, milliLitrePerMinute);
        }

        /// <summary>清除某进料源的流量（等价于发布 0）。</summary>
        public static void ClearFeedFlow(string sourceId)
        {
            if (string.IsNullOrEmpty(sourceId)) sourceId = "default";
            _feedFlows[sourceId] = 0.0;
        }

        /// <summary>当前所有进料源的总进料流量（mL/min）。</summary>
        public static double TotalFeedFlowMlPerMin => _feedFlows.Values.Sum();

        // ───────────────────────── 共享随机源 ─────────────────────────

        /// <summary>
        /// 返回 [-amplitude, +amplitude] 区间的对称噪声。使用线程安全的
        /// <see cref="Random.Shared"/>（.NET 6+），避免各命令内 <c>new Random()</c>
        /// 在同一毫秒被相同种子初始化、导致多路读数同步跳变。仅用于叠加轻微测量噪声。
        /// </summary>
        public static double Noise(double amplitude)
        {
            return (Random.Shared.NextDouble() - 0.5) * 2.0 * amplitude;
        }
    }
}
