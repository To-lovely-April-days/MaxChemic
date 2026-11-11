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

        /// <summary>是否提供实际语音播放开始/结束事件。</summary>
        bool SupportsSpeechPlaybackEvents { get; }

        /// <summary>实际播放状态变化,true=开始播放,false=结束或停止。</summary>
        event EventHandler<bool> SpeechPlaybackChanged;

        /// <summary>暂停当前播放;成功暂停返回 true,不支持或当前不可暂停返回 false。</summary>
        bool PauseSpeaking();

        /// <summary>恢复已暂停的播放;成功恢复返回 true,不支持或当前不可恢复返回 false。</summary>
        bool ResumeSpeaking();

        // ── 按键说话(PTT):小桐聊天窗麦克风按钮的一轮聆听 ──

        /// <summary>PTT 是否正在聆听。</summary>
        bool IsListeningOnce { get; }

        /// <summary>开始一轮按键说话聆听。</summary>
        void StartListenOnce();

        /// <summary>结束当前按键说话(再点一次麦克风)。</summary>
        void StopListenOnce();

        /// <summary>PTT 聆听状态变化(界面按钮/动画反馈)。</summary>
        event EventHandler<bool> ListenOnceStateChanged;

        /// <summary>PTT 实时中间识别片段(边说边出字反馈)。</summary>
        event EventHandler<string> ListenPartialChanged;

        /// <summary>PTT 一轮聆听结束,带回整句识别文本;空串=未识别到内容或超时/取消。</summary>
        event EventHandler<string> ListenOnceCompleted;
    }
}