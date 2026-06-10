using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using edge_tts_net;
using edge_tts_net;
using MaxChemical.Logging;
using MaxChemical.Shell.Models;
using NAudio.Wave;

namespace MaxChemical.Shell.Services
{
    /// <summary>
    /// Edge TTS 语音合成引擎实现
    /// 基于 edge-tts-net 库，使用微软 Edge 浏览器的在线文本转语音服务
    /// 无需 API Key，完全免费使用
    /// </summary>
    public class EdgeTtsEngine : ITtsEngine
    {
        private readonly ILogService _logger;
        private readonly EdgeTtsSettings _settings;
        private float _speakingRate;
        private TTSVoice? _selectedVoice;
        private readonly object _lockObject = new object();
        private CancellationTokenSource? _currentPlaybackCts;
        private Task? _currentPlaybackTask;
        private IWavePlayer? _wavePlayer;
        private WaveStream? _waveStream;

        public string EngineName => "Edge TTS";
        public bool IsAvailable { get; private set; }

        public EdgeTtsEngine(ILogService logger, EdgeTtsSettings settings)
        {
            _logger = logger;
            _settings = settings;
            _speakingRate = settings.SpeakingRate;

            CheckAvailability();
        }

        /// <summary>
        /// 异步检查 Edge TTS 服务的可用性
        /// 通过获取语音列表来验证服务是否可访问
        /// </summary>
        private async void CheckAvailability()
        {
            try
            {
                var edgeTts = new EdgeTTSNet();
                var voices = await edgeTts.GetVoices();

                if (voices == null || !voices.Any())
                {
                    _logger.LogError("无法从 Edge TTS 服务获取语音列表");
                    IsAvailable = false;
                    return;
                }

                _selectedVoice = voices.FirstOrDefault(v =>
                    v.ShortName.Equals(_settings.Voice, StringComparison.OrdinalIgnoreCase));

                if (_selectedVoice == null)
                {
                    _selectedVoice = voices.FirstOrDefault(v =>
                        v.Locale.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase));
                }

                if (_selectedVoice == null)
                {
                    _selectedVoice = voices.First();
                }

                IsAvailable = true;

                _logger.LogInformation($"{EngineName} 初始化成功");
                _logger.LogInformation($"当前语音模型: {_selectedVoice.ShortName}");
                _logger.LogInformation($"友好名称: {_selectedVoice.FriendlyName}");
                _logger.LogInformation($"语音区域: {_selectedVoice.Locale}");
                _logger.LogInformation($"语速倍率: {_settings.SpeakingRate}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"{EngineName} 初始化失败: {ex.Message}");
                IsAvailable = false;
            }
        }

        /// <summary>
        /// 同步方式进行语音合成和播放
        /// 该方法会阻塞当前线程直到语音播放完成
        /// </summary>
        /// <param name="text">需要转换为语音的文本内容</param>
        public void Speak(string text)
        {
            if (!IsAvailable || _selectedVoice == null)
            {
                _logger.LogWarning($"{EngineName} 当前不可用，跳过语音输出");
                return;
            }

            lock (_lockObject)
            {
                try
                {
                    StopCurrentPlayback();

                    _logger.LogInformation($"{EngineName} 正在朗读: {text}");

                    _currentPlaybackCts = new CancellationTokenSource();
                    var cts = _currentPlaybackCts;

                    _currentPlaybackTask = Task.Run(async () =>
                    {
                        string? tempFile = null;
                        try
                        {
                            tempFile = Path.Combine(Path.GetTempPath(), $"edge_tts_{Guid.NewGuid():N}.mp3");

                            var option = CreateTTSOption();
                            var edgeTts = new EdgeTTSNet(option);

                            await edgeTts.Save(text, tempFile, cts.Token);

                            if (cts.Token.IsCancellationRequested) return;

                            PlayAudioFile(tempFile, cts.Token);
                        }
                        catch (Exception ex)
                        {
                            if (!cts.Token.IsCancellationRequested)
                            {
                                _logger.LogError($"语音合成或播放失败: {ex.Message}");
                            }
                        }
                        finally
                        {
                            if (!string.IsNullOrEmpty(tempFile) && File.Exists(tempFile))
                            {
                                try
                                {
                                    File.Delete(tempFile);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning($"删除临时文件失败: {ex.Message}");
                                }
                            }
                        }
                    }, cts.Token);

                    _currentPlaybackTask.Wait();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"{EngineName} 语音输出失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 异步方式进行语音合成和播放
        /// 该方法不会阻塞当前线程，语音在后台播放
        /// </summary>
        /// <param name="text">需要转换为语音的文本内容</param>
        public void SpeakAsync(string text)
        {
            if (!IsAvailable || _selectedVoice == null)
            {
                _logger.LogWarning($"{EngineName} 当前不可用，跳过语音输出");
                return;
            }

            Task.Run(async () =>
            {
                lock (_lockObject)
                {
                    try
                    {
                        StopCurrentPlayback();

                        _logger.LogInformation($"{EngineName} 异步朗读: {text}");

                        _currentPlaybackCts = new CancellationTokenSource();
                        var cts = _currentPlaybackCts;

                        Task.Run(async () =>
                        {
                            string? tempFile = null;
                            try
                            {
                                tempFile = Path.Combine(Path.GetTempPath(), $"edge_tts_{Guid.NewGuid():N}.mp3");

                                var option = CreateTTSOption();
                                var edgeTts = new EdgeTTSNet(option);

                                await edgeTts.Save(text, tempFile, cts.Token);

                                if (!cts.Token.IsCancellationRequested)
                                {
                                    PlayAudioFile(tempFile, cts.Token);
                                }
                            }
                            catch (Exception ex)
                            {
                                if (!cts.Token.IsCancellationRequested)
                                {
                                    _logger.LogError($"异步语音合成或播放失败: {ex.Message}");
                                }
                            }
                            finally
                            {
                                if (!string.IsNullOrEmpty(tempFile) && File.Exists(tempFile))
                                {
                                    try
                                    {
                                        File.Delete(tempFile);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning($"删除临时文件失败: {ex.Message}");
                                    }
                                }
                            }
                        }, cts.Token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"异步语音播放失败: {ex.Message}");
                    }
                }
            });
        }

        /// <summary>
        /// 创建 Edge TTS 的选项配置
        /// 包括语音模型、音调、语速和音量设置
        /// </summary>
        /// <returns>配置好的 TTSOption 对象</returns>
        private TTSOption CreateTTSOption()
        {
            var rate = (int)((_speakingRate - 1.0f) * 100);
            rate = Math.Clamp(rate, -100, 100);
            var rateStr = rate >= 0 ? $"+{rate}%" : $"{rate}%";

            return new TTSOption(
                voice: _selectedVoice!.ShortName,
                pitch: _settings.Pitch,
                rate: rateStr,
                volume: _settings.Volume
            );
        }

        /// <summary>
        /// 使用 NAudio 播放音频文件
        /// 支持通过 CancellationToken 中断播放
        /// </summary>
        /// <param name="audioFile">音频文件的完整路径</param>
        /// <param name="cancellationToken">用于取消播放的令牌</param>
        private void PlayAudioFile(string audioFile, CancellationToken cancellationToken)
        {
            try
            {
                using var audioFileReader = new AudioFileReader(audioFile);
                using var waveOut = new WaveOutEvent();

                _wavePlayer = waveOut;
                _waveStream = audioFileReader;

                var playbackStopped = new ManualResetEvent(false);

                waveOut.PlaybackStopped += (sender, args) =>
                {
                    playbackStopped.Set();
                };

                waveOut.Init(audioFileReader);
                waveOut.Play();

                while (waveOut.PlaybackState == PlaybackState.Playing)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        waveOut.Stop();
                        break;
                    }
                    Thread.Sleep(100);
                }

                playbackStopped.WaitOne(1000);
            }
            catch (Exception ex)
            {
                _logger.LogError($"音频播放错误: {ex.Message}");
            }
            finally
            {
                _wavePlayer = null;
                _waveStream = null;
            }
        }

        /// <summary>
        /// 停止当前正在进行的语音播放
        /// 包括取消任务、停止音频播放器和清理资源
        /// </summary>
        private void StopCurrentPlayback()
        {
            try
            {
                _currentPlaybackCts?.Cancel();

                _wavePlayer?.Stop();
                _wavePlayer?.Dispose();
                _waveStream?.Dispose();

                if (_currentPlaybackTask != null && !_currentPlaybackTask.IsCompleted)
                {
                    _currentPlaybackTask.Wait(TimeSpan.FromSeconds(2));
                }

                _currentPlaybackCts?.Dispose();
                _currentPlaybackCts = null;
                _currentPlaybackTask = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"停止播放时发生错误: {ex.Message}");
            }
        }

        /// <summary>
        /// 立即停止所有语音播放
        /// 公共接口，用于外部调用
        /// </summary>
        public void StopSpeaking()
        {
            lock (_lockObject)
            {
                _logger.LogDebug("停止语音播放");
                StopCurrentPlayback();
            }
        }

        /// <summary>
        /// 设置语音播放速度
        /// </summary>
        /// <param name="rate">语速倍率，范围 0.5 到 2.0，1.0 为正常速度</param>
        public void SetSpeakingRate(float rate)
        {
            _speakingRate = Math.Clamp(rate, 0.5f, 2.0f);
            _logger.LogInformation($"{EngineName} 语速已设置为: {_speakingRate}");
        }

        /// <summary>
        /// 释放所有资源
        /// 包括停止播放、取消任务和清理临时文件
        /// </summary>
        public void Dispose()
        {
            _logger.LogDebug($"正在释放 {EngineName} 资源");

            StopCurrentPlayback();

            Thread.Sleep(500);

            _logger.LogDebug($"{EngineName} 资源已释放");
        }
    }
}