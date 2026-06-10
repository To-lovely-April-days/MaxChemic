using MaxChemical.Logging;
using MaxChemical.Shell.Models;
using MaxChemical.Shell.Utils;
using NAudio.Wave;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MaxChemical.Shell.Services
{
    public class SherpaOnnxTtsEngine : ITtsEngine
    {
        private readonly ILogService _logger;
        private readonly SherpaOnnxSettings _settings;
        private SherpaOnnxTTS? _tts;
        private float _speakingRate;
        private readonly object _lockObject = new object();
        private CancellationTokenSource? _currentPlaybackCts;
        private Task? _currentPlaybackTask;
        private WaveOutEvent? _waveOut;

        public string EngineName => "Sherpa-ONNX TTS";
        public bool IsAvailable { get; private set; }

        public SherpaOnnxTtsEngine(ILogService logger, SherpaOnnxSettings settings)
        {
            _logger = logger;
            _settings = settings;
            _speakingRate = settings.SpeakingRate;

            Initialize();
        }

        private void Initialize()
        {
            try
            {
                _logger.LogInformation($"正在初始化 {EngineName}");
                _logger.LogInformation($"模型路径: {_settings.ModelPath}");

                if (!Directory.Exists(_settings.ModelPath))
                {
                    _logger.LogError($"模型目录不存在: {_settings.ModelPath}");
                    IsAvailable = false;
                    return;
                }

                _tts = new SherpaOnnxTTS(_settings.ModelPath);
                IsAvailable = true;

                _logger.LogInformation($"{EngineName} 初始化成功");
                _logger.LogInformation($"语速: {_speakingRate}");
                _logger.LogInformation($"说话人ID: {_settings.SpeakerId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"{EngineName} 初始化失败: {ex.Message}");
                _logger.LogError($"详细信息: {ex}");
                IsAvailable = false;
            }
        }

        private int ConvertRateToSpeed(float rate)
        {
            rate = Math.Clamp(rate, 0.5f, 2.0f);
            int speed = (int)Math.Round(rate * 100);
            _logger.LogDebug($"语速转换: rate={rate:F2} -> speed={speed}");
            return speed;
        }

        private string PreprocessText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            text = NumberToChineseConverter.ConvertNumbersInText(text);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");
            text = text.Trim();

            _logger.LogDebug($"文本预处理: {text}");
            return text;
        }

        public void Speak(string text)
        {
            if (!IsAvailable || _tts == null)
            {
                _logger.LogWarning($"[{EngineName}] 不可用，跳过语音输出");
                return;
            }

            lock (_lockObject)
            {
                try
                {
                    StopCurrentPlayback();

                    text = PreprocessText(text);
                    _logger.LogInformation($"[{EngineName}] 正在朗读: {text}");

                    int speed = ConvertRateToSpeed(_speakingRate);

                    var audioBytes = _tts.GenerateAudioBytes(
                        text,
                        _settings.SpeakerId,
                        speed
                    );

                    if (audioBytes == null || audioBytes.Length == 0)
                    {
                        _logger.LogError("生成的音频数据为空");
                        return;
                    }

                    _logger.LogDebug($"音频生成成功，大小: {audioBytes.Length} 字节");
                    PlayAudioBytesSync(audioBytes);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[{EngineName}] 语音输出失败: {ex.Message}");
                }
            }
        }

        public void SpeakAsync(string text)
        {
            if (!IsAvailable || _tts == null)
            {
                _logger.LogWarning($"[{EngineName}] 不可用，跳过语音输出");
                return;
            }

            Task.Run(() =>
            {
                lock (_lockObject)
                {
                    try
                    {
                        StopCurrentPlayback();

                        text = PreprocessText(text);
                        _logger.LogInformation($"[{EngineName}] 正在异步朗读: {text}");

                        _currentPlaybackCts = new CancellationTokenSource();
                        var cts = _currentPlaybackCts;

                        _currentPlaybackTask = Task.Run(() =>
                        {
                            try
                            {
                                int speed = ConvertRateToSpeed(_speakingRate);

                                var audioBytes = _tts.GenerateAudioBytes(
                                    text,
                                    _settings.SpeakerId,
                                    speed
                                );

                                if (audioBytes == null || audioBytes.Length == 0)
                                {
                                    _logger.LogError("生成的音频数据为空");
                                    return;
                                }

                                _logger.LogDebug($"音频生成成功，大小: {audioBytes.Length} 字节");
                                PlayAudioBytesAsync(audioBytes, cts.Token);
                            }
                            catch (Exception ex)
                            {
                                if (!cts.Token.IsCancellationRequested)
                                {
                                    _logger.LogError($"异步播放失败: {ex.Message}");
                                }
                            }
                        }, cts.Token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"异步播放启动失败: {ex.Message}");
                    }
                }
            });
        }

        private void PlayAudioBytesSync(byte[] audioBytes)
        {
            try
            {
                using var ms = new MemoryStream(audioBytes);
                using var reader = new WaveFileReader(ms);
                using var waveOut = new WaveOutEvent();

                waveOut.Init(reader);
                waveOut.Play();

                _logger.LogDebug("开始播放音频");

                while (waveOut.PlaybackState == PlaybackState.Playing)
                {
                    Thread.Sleep(100);
                }

                _logger.LogDebug("音频播放完成");
            }
            catch (Exception ex)
            {
                _logger.LogError($"同步播放失败: {ex.Message}");
                throw;
            }
        }

        private void PlayAudioBytesAsync(byte[] audioBytes, CancellationToken cancellationToken)
        {
            MemoryStream? ms = null;
            WaveFileReader? reader = null;
            WaveOutEvent? localWaveOut = null;

            try
            {
                ms = new MemoryStream(audioBytes);
                reader = new WaveFileReader(ms);
                localWaveOut = new WaveOutEvent();

                // 在锁内设置 _waveOut
                lock (_lockObject)
                {
                    _waveOut = localWaveOut;
                }

                localWaveOut.Init(reader);
                localWaveOut.Play();

                _logger.LogDebug("开始异步播放音频");

                // 使用局部变量检查状态，避免并发问题
                while (true)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogDebug("播放被取消");
                        localWaveOut.Stop();
                        break;
                    }

                    // 检查播放状态
                    var playbackState = localWaveOut.PlaybackState;
                    if (playbackState != PlaybackState.Playing)
                    {
                        break;
                    }

                    Thread.Sleep(100);
                }

                _logger.LogDebug("异步播放完成");
            }
            catch (Exception ex)
            {
                _logger.LogError($"异步播放失败: {ex.Message}");
            }
            finally
            {
                // 清理资源
                lock (_lockObject)
                {
                    if (_waveOut == localWaveOut)
                    {
                        _waveOut = null;
                    }
                }

                localWaveOut?.Dispose();
                reader?.Dispose();
                ms?.Dispose();
            }
        }

        private void StopCurrentPlayback()
        {
            try
            {
                // 取消播放任务
                _currentPlaybackCts?.Cancel();

                // 停止并清理 WaveOut
                WaveOutEvent? waveOutToStop = null;
                lock (_lockObject)
                {
                    waveOutToStop = _waveOut;
                    _waveOut = null;
                }

                if (waveOutToStop != null)
                {
                    try
                    {
                        waveOutToStop.Stop();
                        waveOutToStop.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"停止WaveOut时出错: {ex.Message}");
                    }
                }

                // 等待播放任务完成
                if (_currentPlaybackTask != null && !_currentPlaybackTask.IsCompleted)
                {
                    try
                    {
                        _currentPlaybackTask.Wait(TimeSpan.FromSeconds(2));
                    }
                    catch (AggregateException)
                    {
                        // 忽略任务取消异常
                    }
                }

                // 释放取消令牌
                _currentPlaybackCts?.Dispose();
                _currentPlaybackCts = null;
                _currentPlaybackTask = null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"停止播放时发生错误: {ex.Message}");
            }
        }

        public void StopSpeaking()
        {
            lock (_lockObject)
            {
                _logger.LogDebug("停止语音播放");
                StopCurrentPlayback();
            }
        }

        public void SetSpeakingRate(float rate)
        {
            _speakingRate = Math.Clamp(rate, 0.5f, 2.0f);
            int speed = ConvertRateToSpeed(_speakingRate);
            _logger.LogInformation($"[{EngineName}] 语速已设置为: {_speakingRate:F2} (speed={speed})");
        }

        public void Dispose()
        {
            _logger.LogDebug($"正在释放 {EngineName} 资源");

            StopCurrentPlayback();

            _tts?.Dispose();
            _tts = null;

            Thread.Sleep(500);

            _logger.LogDebug($"{EngineName} 资源已释放");
        }
    }
}