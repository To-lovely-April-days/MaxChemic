using System;

namespace MaxChemical.Shell.Services
{
    public interface IVoiceAssistantService
    {
        /// <summary>
        /// 是否正在监听
        /// </summary>
        bool IsListening { get; }

        /// <summary>
        /// 是否启用语音输出
        /// </summary>
        bool IsSpeechEnabled { get; set; }

        /// <summary>
        /// 监听状态变化事件
        /// </summary>
        event EventHandler<bool> ListeningStateChanged;

        /// <summary>
        /// 语音命令识别事件
        /// </summary>
        event EventHandler<string> CommandRecognized;

        /// <summary>
        /// 语音输出事件
        /// </summary>
        event EventHandler<string> SpeechStarted;

        /// <summary>
        /// 启动语音助手
        /// </summary>
        void Start();

        /// <summary>
        /// 停止语音助手
        /// </summary>
        void Stop();

        /// <summary>
        /// 执行语音命令
        /// </summary>
        void ExecuteCommand(string command);

        /// <summary>
        /// 语音输出文本
        /// </summary>
        void Speak(string text);

        /// <summary>
        /// 异步语音输出文本
        /// </summary>
        void SpeakAsync(string text);

        /// <summary>
        /// 停止当前语音输出
        /// </summary>
        void StopSpeaking();
    }
}