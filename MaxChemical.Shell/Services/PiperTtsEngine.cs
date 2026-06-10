using MaxChemical.Logging;
using MaxChemical.Shell.Models;
using NAudio.Wave;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MaxChemical.Shell.Services
{
    /// <summary>
    /// Piper 本地 TTS 引擎
    /// </summary>
    public class PiperTtsEngine : ITtsEngine
    {
        private readonly ILogService _logger;
        private readonly PiperSettings _settings;
        private IWavePlayer? _wavePlayer;
        private AudioFileReader? _audioFileReader;
        private float _speakingRate;
        private string _piperPath;
        private string _modelPath;

        public string EngineName => "Piper TTS";
        public bool IsAvailable { get; private set; }

        public PiperTtsEngine(ILogService logger, PiperSettings settings)
        {
            _logger = logger;
            _settings = settings;
            _speakingRate = settings.SpeakingRate;

            InitializePaths();
            CheckAvailability();
        }

        private void InitializePaths()
        {
            var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
            _piperPath = Path.Combine(appDirectory, _settings.ExecutablePath);
            _modelPath = Path.Combine(appDirectory, _settings.ModelsDirectory, _settings.PreferredModel);

            _logger.LogDebug($"Piper 路径: {_piperPath}");
            _logger.LogDebug($"模型路径: {_modelPath}");
        }

        private void CheckAvailability()
        {
            var piperExists = File.Exists(_piperPath);
            var modelExists = File.Exists(_modelPath);

            IsAvailable = piperExists && modelExists;

            if (IsAvailable)
            {
                _logger.LogInformation($" {EngineName} 已就绪");
                _logger.LogInformation($"   - 模型: {Path.GetFileName(_modelPath)}");
            }
            else
            {
                _logger.LogWarning($" {EngineName} 不可用");
                if (!piperExists)
                    _logger.LogWarning($"   - Piper 未找到: {_piperPath}");
                if (!modelExists)
                    _logger.LogWarning($"   - 模型未找到: {_modelPath}");
            }
        }

        public void Speak(string text)
        {
            if (!IsAvailable)
            {
                _logger.LogWarning($"[{EngineName}] 不可用，跳过语音输出");
                return;
            }

            try
            {
                _logger.LogInformation($"🔊 [{EngineName}] 语音输出（同步）: {text}");
                ExecutePiper(text, false);
            }
            catch (Exception ex)
            {
                _logger.LogError($" [{EngineName}] 语音输出失败: {ex.Message}");
            }
        }

        public void SpeakAsync(string text)
        {
            if (!IsAvailable)
            {
                _logger.LogWarning($"[{EngineName}] 不可用，跳过语音输出");
                return;
            }

            Task.Run(() =>
            {
                try
                {
                    _logger.LogInformation($"🔊 [{EngineName}] 语音输出（异步）: {text}");
                    ExecutePiper(text, true);
                }
                catch (Exception ex)
                {
                    _logger.LogError($" [{EngineName}] 异步语音输出失败: {ex.Message}");
                }
            });
        }

        private void ExecutePiper(string text, bool async)
        {
            var tempWavFile = Path.Combine(Path.GetTempPath(), $"piper_{Guid.NewGuid()}.wav");

            try
            {
                // 计算长度缩放（语速的倒数关系）
                var lengthScale = 2.0f - _speakingRate;

                var arguments = $"--model \"{_modelPath}\" " +
                               $"--length_scale {lengthScale.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                               $"--noise_scale {_settings.NoiseFactor.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                               $"--output_file \"{tempWavFile}\"";

                var startInfo = new ProcessStartInfo
                {
                    FileName = _piperPath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(_piperPath),
                    StandardInputEncoding = System.Text.Encoding.UTF8,
                    StandardOutputEncoding = System.Text.Encoding.UTF8,
                    StandardErrorEncoding = System.Text.Encoding.UTF8
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process != null)
                    {
                        // 发送文本到 Piper
                        process.StandardInput.WriteLine(text);
                        process.StandardInput.Close();

                        // 读取错误输出（Piper 的日志）
                        var error = process.StandardError.ReadToEnd();
                        process.WaitForExit();

                        // 检查是否有真正的错误
                        if (!string.IsNullOrEmpty(error))
                        {
                            if (error.Contains("[error]") || error.Contains("Error:"))
                            {
                                _logger.LogError($" Piper 错误: {error}");
                                return;
                            }
                        }

                        if (process.ExitCode != 0)
                        {
                            _logger.LogError($" Piper 退出代码: {process.ExitCode}");
                            return;
                        }

                        // 检查生成的音频文件
                        if (File.Exists(tempWavFile))
                        {
                            var fileInfo = new FileInfo(tempWavFile);
                            if (fileInfo.Length > 0)
                            {
                                PlayWavFile(tempWavFile, async);
                            }
                            else
                            {
                                _logger.LogError($" 音频文件为空");
                                File.Delete(tempWavFile);
                            }
                        }
                        else
                        {
                            _logger.LogError($" 未生成音频文件");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($" 执行 Piper 失败: {ex.Message}");
                DeleteTempFile(tempWavFile);
            }
        }

        private void PlayWavFile(string wavFile, bool async)
        {
            try
            {
                _logger.LogDebug($"▶ 开始播放: {Path.GetFileName(wavFile)}");

                // 停止之前的播放
                StopCurrentPlayback();

                // 创建新的播放器和音频读取器
                _audioFileReader = new AudioFileReader(wavFile);
                _wavePlayer = new WaveOutEvent();
                _wavePlayer.Init(_audioFileReader);
                _wavePlayer.Volume = 1.0f;

                if (!async)
                {
                    // 同步播放
                    _wavePlayer.Play();
                    while (_wavePlayer.PlaybackState == PlaybackState.Playing)
                    {
                        Thread.Sleep(100);
                    }

                    CleanupPlaybackResources();
                    DeleteTempFile(wavFile);
                }
                else
                {
                    // 异步播放
                    var tempFile = wavFile;
                    _wavePlayer.PlaybackStopped += (sender, args) =>
                    {
                        CleanupPlaybackResources();
                        DeleteTempFile(tempFile);
                    };

                    _wavePlayer.Play();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($" 播放音频失败: {ex.Message}");
                CleanupPlaybackResources();
                DeleteTempFile(wavFile);
            }
        }

        public void StopSpeaking()
        {
            StopCurrentPlayback();
        }

        public void SetSpeakingRate(float rate)
        {
            _speakingRate = Math.Clamp(rate, 0.5f, 2.0f);
            _logger.LogInformation($"⚡ [{EngineName}] 语速: {_speakingRate}");
        }

        private void StopCurrentPlayback()
        {
            if (_wavePlayer?.PlaybackState == PlaybackState.Playing)
            {
                _logger.LogDebug("⏹ 停止当前播放");
                _wavePlayer.Stop();
            }
            CleanupPlaybackResources();
        }

        private void CleanupPlaybackResources()
        {
            try
            {
                if (_wavePlayer != null)
                {
                    if (_wavePlayer.PlaybackState == PlaybackState.Playing)
                    {
                        _wavePlayer.Stop();
                    }
                    _wavePlayer.Dispose();
                    _wavePlayer = null;
                }

                if (_audioFileReader != null)
                {
                    _audioFileReader.Dispose();
                    _audioFileReader = null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"清理播放资源时出错: {ex.Message}");
            }
        }

        private void DeleteTempFile(string filePath)
        {
            try
            {
                Thread.Sleep(200); // 稍微延迟确保文件句柄释放
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    _logger.LogDebug($"🗑 已删除临时文件");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"⚠ 删除临时文件失败: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _logger.LogDebug($"释放 {EngineName} 资源");
            StopSpeaking();
            CleanupPlaybackResources();
        }
    }
}