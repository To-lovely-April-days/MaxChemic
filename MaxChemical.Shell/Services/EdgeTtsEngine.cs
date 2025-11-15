using Edge_tts_sharp;
using Edge_tts_sharp.Model;
using MaxChemical.Logging;
using MaxChemical.Shell.Models;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace MaxChemical.Shell.Services
{
    /// <summary>
    /// Edge TTS 引擎 (使用 Edge_tts_sharp)
    /// </summary>
    public class EdgeTtsEngine : ITtsEngine
    {
        private readonly ILogService _logger;
        private readonly EdgeTtsSettings _settings;
        private float _speakingRate;
        private eVoice? _selectedVoice; // 使用 object 避免类型问题

        public string EngineName => "Edge TTS";
        public bool IsAvailable { get; private set; }

        public EdgeTtsEngine(ILogService logger, EdgeTtsSettings settings)
        {
            _logger = logger;
            _settings = settings;
            _speakingRate = settings.SpeakingRate;

            // 关闭调试模式
            Edge_tts.Debug = false;

            CheckAvailability();
        }

        private void CheckAvailability()
        {
            try
            {
                // 获取语音列表
                var voices = Edge_tts.GetVoice();

                if (voices == null || !voices.Any())
                {
                    _logger.LogError("❌ 无法获取语音列表");
                    IsAvailable = false;
                    return;
                }

                // 查找晓晓语音（根据文档，Name 包含 "Xiaoxiao"）
                _selectedVoice = voices.FirstOrDefault(v => v.ShortName.Equals(_settings.Voice));

                // 如果没找到晓晓，使用第一个中文语音
                if (_selectedVoice == null)
                {
                    _selectedVoice = voices.FirstOrDefault(v => v.Locale.StartsWith("zh-CN"));
                }

                // 还没找到就用第一个
                if (_selectedVoice == null)
                {
                    _selectedVoice = voices.First();
                }

                IsAvailable = true;

                // 使用反射获取语音信息来打印日志
                var voiceType = _selectedVoice.GetType();
                var name = voiceType.GetProperty("Name")?.GetValue(_selectedVoice)?.ToString() ?? "Unknown";
                var friendlyName = voiceType.GetProperty("FriendlyName")?.GetValue(_selectedVoice)?.ToString() ?? "Unknown";

                _logger.LogInformation($"✅ {EngineName} 已就绪");
                _logger.LogInformation($"   - 语音: {name}");
                _logger.LogInformation($"   - 友好名称: {friendlyName}");
                _logger.LogInformation($"   - 语速: {_settings.SpeakingRate}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ {EngineName} 初始化失败: {ex.Message}");
                IsAvailable = false;
            }
        }

        public void Speak(string text)
        {
            if (!IsAvailable || _selectedVoice == null)
            {
                _logger.LogWarning($"[{EngineName}] 不可用，跳过语音输出");
                return;
            }

            try
            {
                _logger.LogInformation($"🔊 [{EngineName}] 语音输出（同步）: {text}");

                // 使用同步模式
                Edge_tts.Await = true;

                var option = CreatePlayOption(text);

                // 直接播放（使用 PlayText 方法）
                Edge_tts.PlayText(option, _selectedVoice);

                _logger.LogDebug("✅ 播放完成");
            }
            catch (Exception ex)
            {
                _logger.LogError($"❌ [{EngineName}] 语音输出失败: {ex.Message}");
            }
        }

        public void SpeakAsync(string text)
        {
            if (!IsAvailable || _selectedVoice == null)
            {
                _logger.LogWarning($"[{EngineName}] 不可用，跳过语音输出");
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    _logger.LogInformation($"🔊 [{EngineName}] 语音输出（异步）: {text}");

                    // 使用异步模式
                    Edge_tts.Await = false;

                    var option = CreatePlayOption(text);

                    // 直接播放
                    Edge_tts.PlayText(option, _selectedVoice);

                    _logger.LogDebug("✅ 异步播放已启动");
                }
                catch (Exception ex)
                {
                    _logger.LogError($"❌ [{EngineName}] 异步语音输出失败: {ex.Message}");
                }
            });
        }

        private PlayOption CreatePlayOption(string text)
        {
            // 计算语速：Edge_tts_sharp 的 Rate 范围是 -100 到 100
            // 我们的 _speakingRate 范围是 0.5 到 2.0
            // 转换公式：rate = (speakingRate - 1.0) * 100
            var rate = (int)((_speakingRate - 1.0f) * 100);
            rate = Math.Clamp(rate, -100, 100);

            // 解析音量：从 "+0%" 格式转换为 0-1 的浮点数
            var volume = ParseVolume(_settings.Volume);

            return new PlayOption
            {
                Text = text,
                Rate = rate,
                Volume = volume,
                SavePath = string.Empty // 不保存文件，直接播放
            };
        }

        private float ParseVolume(string volumeStr)
        {
            try
            {
                // 移除 "%" 和 "+" 符号
                var valueStr = volumeStr.Replace("%", "").Replace("+", "").Trim();

                if (float.TryParse(valueStr, out var value))
                {
                    // "+0%" 表示正常音量 1.0
                    // "+100%" 表示最大音量 1.0
                    // "-100%" 表示最小音量 0.0
                    // 简化处理：0% = 1.0（正常音量）
                    if (value == 0)
                        return 1.0f;

                    // 转换为 0-1 范围
                    return Math.Clamp((value + 100) / 200f, 0f, 1f);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"解析音量失败: {ex.Message}，使用默认值 1.0");
            }

            return 1.0f;
        }

        public void StopSpeaking()
        {
            try
            {
                _logger.LogDebug("⏹ 停止语音播放");
                // Edge_tts_sharp 没有直接的停止方法
                // 可以通过重新初始化来停止
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"停止播放时出错: {ex.Message}");
            }
        }

        public void SetSpeakingRate(float rate)
        {
            _speakingRate = Math.Clamp(rate, 0.5f, 2.0f);
            _logger.LogInformation($"⚡ [{EngineName}] 语速: {_speakingRate}");
        }

        public void Dispose()
        {
            _logger.LogDebug($"释放 {EngineName} 资源");
            // Edge_tts_sharp 会自动清理资源
        }
    }
}