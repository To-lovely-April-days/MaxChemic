namespace MaxChemical.Shell.Models
{
    /// <summary>
    /// 语音设置配置
    /// </summary>
    public class VoiceSettings
    {
        /// <summary>
        /// TTS提供商: "Piper" 或 "Azure"
        /// </summary>
        public string TtsProvider { get; set; } = "Piper";

        /// <summary>
        /// Piper TTS 配置
        /// </summary>
        public PiperSettings Piper { get; set; } = new PiperSettings();

        /// <summary>
        /// Azure TTS 配置
        /// </summary>
        public AzureTtsSettings AzureTts { get; set; } = new AzureTtsSettings();
    }

    /// <summary>
    /// Piper TTS 配置
    /// </summary>
    public class PiperSettings
    {
        /// <summary>
        /// Piper 可执行文件路径
        /// </summary>
        public string ExecutablePath { get; set; } = "Piper\\piper.exe";

        /// <summary>
        /// 模型文件目录
        /// </summary>
        public string ModelsDirectory { get; set; } = "Piper\\models";

        /// <summary>
        /// 首选模型文件名
        /// </summary>
        public string PreferredModel { get; set; } = "zh_CN-huayan-medium.onnx";

        /// <summary>
        /// 语速 (0.5 - 2.0)
        /// </summary>
        public float SpeakingRate { get; set; } = 1.0f;

        /// <summary>
        /// 噪音因子 (0.0 - 1.0)
        /// </summary>
        public float NoiseFactor { get; set; } = 0.667f;

        /// <summary>
        /// 长度缩放
        /// </summary>
        public float LengthScale { get; set; } = 1.0f;
    }

    /// <summary>
    /// Azure TTS 配置
    /// </summary>
    public class AzureTtsSettings
    {
        /// <summary>
        /// Azure 订阅密钥
        /// </summary>
        public string SubscriptionKey { get; set; } = string.Empty;

        /// <summary>
        /// Azure 区域
        /// </summary>
        public string Region { get; set; } = "eastasia";

        /// <summary>
        /// 默认语音名称
        /// </summary>
        public string DefaultVoice { get; set; } = "zh-CN-XiaoxiaoNeural";

        /// <summary>
        /// 语速 (0.5 - 2.0)
        /// </summary>
        public string SpeakingRate { get; set; } = "1.0";

        /// <summary>
        /// 音调 (-50% 到 +50%)
        /// </summary>
        public string Pitch { get; set; } = "0%";

        /// <summary>
        /// 音量 (0 - 100)
        /// </summary>
        public string Volume { get; set; } = "100";
    }
}