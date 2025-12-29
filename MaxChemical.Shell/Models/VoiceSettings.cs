namespace MaxChemical.Shell.Models
{
    public class VoiceSettings
    {
        /// <summary>
        /// TTS提供商: "edge", "azure", "piper", "sherpa"
        /// </summary>
        public string TtsProvider { get; set; } = "sherpa";

        public EdgeTtsSettings EdgeTts { get; set; } = new();
        public AzureTtsSettings AzureTts { get; set; } = new();
        public PiperSettings Piper { get; set; } = new();
        public SherpaOnnxSettings SherpaOnnx { get; set; } = new();
    }


    /// <summary>
    /// Edge TTS 配置（无需 Python，纯 C# 实现）
    /// </summary>
    public class EdgeTtsSettings
    {
        public string Voice { get; set; } = "zh-CN-XiaoxiaoNeural"; // 默认女声
        public float SpeakingRate { get; set; } = 1.2f; // 语速 (0.5 - 2.0)
        public string Volume { get; set; } = "+0%"; // 音量 (-100% to +100%)
        public string Pitch { get; set; } = "+0Hz"; // 音调 (-100Hz to +100Hz)
    }

    /// <summary>
    /// Azure TTS 配置
    /// </summary>
    public class AzureTtsSettings
    {
        public string SubscriptionKey { get; set; } = "";
        public string Region { get; set; } = "eastasia";
        public string DefaultVoice { get; set; } = "zh-CN-XiaoxiaoNeural";
        public string SpeakingRate { get; set; } = "1.0";
        public string Pitch { get; set; } = "+0Hz";
        public string Volume { get; set; } = "+0%";
    }

    /// <summary>
    /// Piper TTS 配置
    /// </summary>
    public class PiperSettings
    {
        public string ExecutablePath { get; set; } = @"Assets\Piper\piper.exe";
        public string ModelsDirectory { get; set; } = @"Assets\Piper\Models";
        public string PreferredModel { get; set; } = "zh_CN-huayan-medium.onnx";
        public float SpeakingRate { get; set; } = 1.0f;
        public float NoiseFactor { get; set; } = 0.667f;
    }

    public class SherpaOnnxSettings
    {
        /// <summary>
        /// 模型路径
        /// </summary>
        public string ModelPath { get; set; } = @"vits-melo-tts-zh_en";

        /// <summary>
        /// 说话人ID (默认 0)
        /// </summary>
        public int SpeakerId { get; set; } = 0;

        /// <summary>
        /// 语速 (0.5 - 2.0)
        /// </summary>
        public float SpeakingRate { get; set; } = 1.0f;
    }

}