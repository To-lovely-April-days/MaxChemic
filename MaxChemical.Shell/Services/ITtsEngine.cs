using System;

namespace MaxChemical.Shell.Services
{
    /// <summary>
    /// TTS引擎统一接口
    /// </summary>
    public interface ITtsEngine : IDisposable
    {
        /// <summary>
        /// TTS引擎名称
        /// </summary>
        string EngineName { get; }

        /// <summary>
        /// 引擎是否可用
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// 同步语音输出
        /// </summary>
        /// <param name="text">要朗读的文本</param>
        void Speak(string text);

        /// <summary>
        /// 异步语音输出
        /// </summary>
        /// <param name="text">要朗读的文本</param>
        void SpeakAsync(string text);

        /// <summary>
        /// 停止当前语音输出
        /// </summary>
        void StopSpeaking();

        /// <summary>
        /// 设置语速
        /// </summary>
        /// <param name="rate">语速值 (0.5 - 2.0)</param>
        void SetSpeakingRate(float rate);
    }
}