using Edge_tts_sharp;
using Edge_tts_sharp.Model;
using MaxChemical.Logging;
using MaxChemical.Shell.Models;
using System;
using System.Linq;
using System.Threading;
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
        private eVoice? _selectedVoice;
        private readonly object _lockObject = new object();
        private CancellationTokenSource? _currentPlaybackCts;
        private Task? _currentPlaybackTask;

        public string EngineName => "Edge TTS";
        public bool IsAvailable { get; private set; }

        public EdgeTtsEngine(ILogService logger, EdgeTtsSettings settings)
        {
            _logger = logger;
            _settings = settings;
            _speakingRate = settings.SpeakingRate;

            Edge_tts.Debug = false;

            CheckAvailability();
        }

        private void CheckAvailability()
        {
            try
            {
                var voices = Edge_tts.GetVoice();

                if (voices == null || !voices.Any())
                {
                    _logger.LogError("Unable to retrieve voice list");
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

                _logger.LogInformation($"{EngineName} initialized successfully");
                _logger.LogInformation($"Voice: {_selectedVoice.Name}");
                _logger.LogInformation($"Friendly Name: {_selectedVoice.FriendlyName}");
                _logger.LogInformation($"Speaking Rate: {_settings.SpeakingRate}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"{EngineName} initialization failed: {ex.Message}");
                IsAvailable = false;
            }
        }

        public void Speak(string text)
        {
            if (!IsAvailable || _selectedVoice == null)
            {
                _logger.LogWarning($"[{EngineName}] is not available, skipping speech output");
                return;
            }

            lock (_lockObject)
            {
                try
                {
                    StopCurrentPlayback();

                    _logger.LogInformation($"[{EngineName}] Speaking: {text}");

                    _currentPlaybackCts = new CancellationTokenSource();
                    var cts = _currentPlaybackCts;

                    _currentPlaybackTask = Task.Run(() =>
                    {
                        try
                        {
                            Edge_tts.Await = false;

                            var option = CreatePlayOption(text);

                            Edge_tts.PlayText(option, _selectedVoice);

                            Thread.Sleep(100);

                            _logger.LogDebug("Playback started successfully");
                        }
                        catch (Exception ex)
                        {
                            if (!cts.Token.IsCancellationRequested)
                            {
                                _logger.LogError($"Playback failed: {ex.Message}");
                            }
                        }
                    }, cts.Token);

                    if (!_currentPlaybackTask.Wait(TimeSpan.FromSeconds(30)))
                    {
                        _logger.LogWarning("Playback timeout");
                        StopCurrentPlayback();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"[{EngineName}] Speech output failed: {ex.Message}");
                }
            }
        }

        public void SpeakAsync(string text)
        {
            if (!IsAvailable || _selectedVoice == null)
            {
                _logger.LogWarning($"[{EngineName}] is not available, skipping speech output");
                return;
            }

            Task.Run(() =>
            {
                lock (_lockObject)
                {
                    try
                    {
                        StopCurrentPlayback();

                        _logger.LogInformation($"[{EngineName}] Speaking asynchronously: {text}");

                        _currentPlaybackCts = new CancellationTokenSource();

                        Edge_tts.Await = false;
                        var option = CreatePlayOption(text);

                        Edge_tts.PlayText(option, _selectedVoice);

                        _logger.LogDebug("Asynchronous playback started");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Asynchronous playback failed: {ex.Message}");
                    }
                }
            });
        }

        private PlayOption CreatePlayOption(string text)
        {
            var rate = (int)((_speakingRate - 1.0f) * 100);
            rate = Math.Clamp(rate, -100, 100);

            var volume = ParseVolume(_settings.Volume);

            return new PlayOption
            {
                Text = text,
                Rate = rate,
                Volume = volume,
                SavePath = string.Empty
            };
        }

        private float ParseVolume(string volumeStr)
        {
            try
            {
                var valueStr = volumeStr.Replace("%", "").Replace("+", "").Trim();

                if (float.TryParse(valueStr, out var value))
                {
                    if (value == 0)
                        return 1.0f;

                    return Math.Clamp((value + 100) / 200f, 0f, 1f);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Volume parsing failed: {ex.Message}, using default value 1.0");
            }

            return 1.0f;
        }

        private void StopCurrentPlayback()
        {
            try
            {
                _currentPlaybackCts?.Cancel();

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
                _logger.LogWarning($"Error occurred while stopping playback: {ex.Message}");
            }
        }

        public void StopSpeaking()
        {
            lock (_lockObject)
            {
                _logger.LogDebug("Stopping speech playback");
                StopCurrentPlayback();
            }
        }

        public void SetSpeakingRate(float rate)
        {
            _speakingRate = Math.Clamp(rate, 0.5f, 2.0f);
            _logger.LogInformation($"[{EngineName}] Speaking rate set to: {_speakingRate}");
        }

        public void Dispose()
        {
            _logger.LogDebug($"Disposing {EngineName} resources");

            StopCurrentPlayback();

            Thread.Sleep(500);

            _logger.LogDebug($"{EngineName} resources disposed");
        }
    }
}