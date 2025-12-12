using MaxChemical.Logging;
using MaxChemical.Shell.Models;
using Microsoft.CognitiveServices.Speech;
using System;
using System.Threading.Tasks;

namespace MaxChemical.Shell.Services
{
    /// <summary>
    /// Azure 云端 TTS 引擎
    /// </summary>
    public class AzureTtsEngine : ITtsEngine
    {
        private readonly ILogService _logger;
        private readonly AzureTtsSettings _settings;
        private SpeechSynthesizer? _synthesizer;
        private SpeechConfig? _speechConfig;
        private float _speakingRate;

        public string EngineName => "Azure TTS";
        public bool IsAvailable { get; private set; }

        public AzureTtsEngine(ILogService logger, AzureTtsSettings settings)
        {
            _logger = logger;
            _settings = settings;
            _speakingRate = ParseFloat(settings.SpeakingRate, 1.0f);

            InitializeAzureTts();
        }

        private void InitializeAzureTts()
        {
            try
            {
                // 检查 API Key 是否配置
                if (string.IsNullOrWhiteSpace(_settings.SubscriptionKey))
                {
                    _logger.LogWarning($" {EngineName} 未配置 API Key");
                    IsAvailable = false;
                    return;
                }

                // 创建语音配置
                _speechConfig = SpeechConfig.FromSubscription(
                    _settings.SubscriptionKey,
                    _settings.Region);

                _speechConfig.SpeechSynthesisVoiceName = _settings.DefaultVoice;

                // 创建语音合成器
                _synthesizer = new SpeechSynthesizer(_speechConfig);

                IsAvailable = true;
                _logger.LogInformation($" {EngineName} 已就绪");
                _logger.LogInformation($"   - 区域: {_settings.Region}");
                _logger.LogInformation($"   - 语音: {_settings.DefaultVoice}");
            }
            catch (Exception ex)
            {
                _logger.LogError($" {EngineName} 初始化失败: {ex.Message}");
                IsAvailable = false;
            }
        }

        public void Speak(string text)
        {
            if (!IsAvailable || _synthesizer == null)
            {
                _logger.LogWarning($"[{EngineName}] 不可用，跳过语音输出");
                return;
            }

            try
            {
                _logger.LogInformation($"🔊 [{EngineName}] 语音输出（同步）: {text}");

                var ssml = BuildSsml(text);
                var result = _synthesizer.SpeakSsmlAsync(ssml).Result;

                LogResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError($" [{EngineName}] 语音输出失败: {ex.Message}");
            }
        }

        public void SpeakAsync(string text)
        {
            if (!IsAvailable || _synthesizer == null)
            {
                _logger.LogWarning($"[{EngineName}] 不可用，跳过语音输出");
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    _logger.LogInformation($"🔊 [{EngineName}] 语音输出（异步）: {text}");

                    var ssml = BuildSsml(text);
                    var result = await _synthesizer.SpeakSsmlAsync(ssml);

                    LogResult(result);
                }
                catch (Exception ex)
                {
                    _logger.LogError($" [{EngineName}] 异步语音输出失败: {ex.Message}");
                }
            });
        }

        private string BuildSsml(string text)
        {
            // 计算语速百分比
            var ratePercent = $"{(_speakingRate * 100):F0}%";

            return $@"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='zh-CN'>
    <voice name='{_settings.DefaultVoice}'>
        <prosody rate='{ratePercent}' pitch='{_settings.Pitch}' volume='{_settings.Volume}'>
            {System.Security.SecurityElement.Escape(text)}
        </prosody>
    </voice>
</speak>";
        }

        private void LogResult(SpeechSynthesisResult result)
        {
            switch (result.Reason)
            {
                case ResultReason.SynthesizingAudioCompleted:
                    _logger.LogInformation($" [{EngineName}] 语音输出完成");
                    break;

                case ResultReason.Canceled:
                    var cancellation = SpeechSynthesisCancellationDetails.FromResult(result);
                    _logger.LogError($" [{EngineName}] 语音合成被取消: {cancellation.Reason}");
                    if (cancellation.Reason == CancellationReason.Error)
                    {
                        _logger.LogError($"   错误代码: {cancellation.ErrorCode}");
                        _logger.LogError($"   错误详情: {cancellation.ErrorDetails}");
                    }
                    break;

                default:
                    _logger.LogWarning($"⚠ [{EngineName}] 未知结果: {result.Reason}");
                    break;
            }
        }

        public void StopSpeaking()
        {
            try
            {
                // Azure TTS 不支持中途停止，这里重新创建 synthesizer
                _synthesizer?.Dispose();

                if (_speechConfig != null)
                {
                    _synthesizer = new SpeechSynthesizer(_speechConfig);
                    _logger.LogDebug($"⏹ [{EngineName}] 已重置");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"⚠ [{EngineName}] 停止语音时出错: {ex.Message}");
            }
        }

        public void SetSpeakingRate(float rate)
        {
            _speakingRate = Math.Clamp(rate, 0.5f, 2.0f);
            _logger.LogInformation($"⚡ [{EngineName}] 语速: {_speakingRate}");
        }

        private float ParseFloat(string value, float defaultValue)
        {
            if (float.TryParse(value, out var result))
            {
                return result;
            }
            return defaultValue;
        }

        public void Dispose()
        {
            _logger.LogDebug($"释放 {EngineName} 资源");
            _synthesizer?.Dispose();
            //_speechConfig?.Dispose();
        }
    }
}