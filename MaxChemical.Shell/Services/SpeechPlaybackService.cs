using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MaxChemical.Shell.Services
{
    public sealed class SpeechPlaybackService : ISpeechPlaybackService
    {
        private readonly IVoiceAssistantService _voice;
        private readonly object _gate = new();
        private readonly object _operationGate = new();
        private SpeechPlaybackState _state = SpeechPlaybackState.Idle;
        private Session? _session;
        private long _nextSessionId;

        public SpeechPlaybackService(IVoiceAssistantService voice)
        {
            _voice = voice ?? throw new ArgumentNullException(nameof(voice));
            _voice.SpeechPlaybackChanged += OnSpeechPlaybackChanged;
        }

        public SpeechPlaybackState State
        {
            get
            {
                lock (_gate) return _state;
            }
        }

        public event EventHandler<SpeechPlaybackState>? StateChanged;

        public void Play(IEnumerable<string> chunks)
        {
            ArgumentNullException.ThrowIfNull(chunks);

            var texts = new List<string>();
            foreach (string chunk in chunks)
            {
                if (!string.IsNullOrWhiteSpace(chunk)) texts.Add(chunk);
            }

            Session? newSession = null;
            SpeechPlaybackState targetState;
            bool stateChanged;

            lock (_operationGate)
            {
                ChunkAttempt? oldAttempt;
                lock (_gate)
                {
                    oldAttempt = InvalidateSessionLocked();
                }

                CancelAttempt(oldAttempt);
                _voice.StopSpeaking();

                lock (_gate)
                {
                    if (texts.Count == 0)
                    {
                        targetState = SpeechPlaybackState.Idle;
                    }
                    else
                    {
                        newSession = new Session(++_nextSessionId, new Queue<string>(texts));
                        _session = newSession;
                        targetState = SpeechPlaybackState.Playing;
                    }

                    stateChanged = SetStateLocked(targetState);
                }
            }

            if (newSession != null) _ = RunSessionAsync(newSession);
            if (stateChanged) NotifyStateChanged(targetState);
        }

        public void Stop()
        {
            bool stateChanged;
            lock (_operationGate)
            {
                ChunkAttempt? attempt;
                lock (_gate)
                {
                    attempt = InvalidateSessionLocked();
                    stateChanged = SetStateLocked(SpeechPlaybackState.Stopped);
                }

                CancelAttempt(attempt);
                _voice.StopSpeaking();
            }

            if (stateChanged) NotifyStateChanged(SpeechPlaybackState.Stopped);
        }

        public void Pause()
        {
            bool pausedNatively;
            lock (_operationGate)
            {
                Session session;
                lock (_gate)
                {
                    if (_state != SpeechPlaybackState.Playing || _session == null) return;
                    session = _session;
                    SetStateLocked(SpeechPlaybackState.Paused);
                }

                pausedNatively = _voice.PauseSpeaking();
                if (pausedNatively)
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_session, session) && _state == SpeechPlaybackState.Paused)
                            session.PausedNatively = true;
                    }
                }
                else
                {
                    ChunkAttempt? attempt;
                    lock (_gate)
                    {
                        attempt = session.Attempt;
                        session.Attempt = null;
                        session.PausedNatively = false;
                    }

                    CancelAttempt(attempt);
                    _voice.StopSpeaking();
                }
            }

            if (!pausedNatively || State == SpeechPlaybackState.Paused)
                NotifyStateChanged(SpeechPlaybackState.Paused);
        }

        public void Resume()
        {
            Session session;
            bool restart = false;
            lock (_operationGate)
            {
                lock (_gate)
                {
                    if (_state != SpeechPlaybackState.Paused || _session == null ||
                        _session.CurrentText == null)
                        return;
                    session = _session;
                }

                if (session.PausedNatively && _voice.ResumeSpeaking())
                {
                    lock (_gate)
                    {
                        session.PausedNatively = false;
                        SetStateLocked(SpeechPlaybackState.Playing);
                    }
                }
                else
                {
                    ChunkAttempt? attempt = null;
                    if (session.PausedNatively)
                    {
                        lock (_gate)
                        {
                            attempt = session.Attempt;
                            session.Attempt = null;
                            session.PausedNatively = false;
                        }

                        CancelAttempt(attempt);
                        _voice.StopSpeaking();
                    }

                    lock (_gate)
                    {
                        SetStateLocked(SpeechPlaybackState.Playing);
                    }
                    restart = true;
                }
            }

            if (restart) _ = RunSessionAsync(session);
            NotifyStateChanged(SpeechPlaybackState.Playing);
        }

        private async Task RunSessionAsync(Session session)
        {
            while (true)
            {
                ChunkAttempt attempt;
                lock (_operationGate)
                {
                    lock (_gate)
                    {
                        if (!IsCurrentSessionLocked(session) ||
                            _state != SpeechPlaybackState.Playing ||
                            session.CurrentText == null || session.Attempt != null)
                            return;

                        attempt = new ChunkAttempt(session.CurrentText);
                        session.Attempt = attempt;
                    }

                    try
                    {
                        if (_voice.IsSpeechEnabled) _voice.SpeakAsync(attempt.Text);
                        else attempt.Completion.TrySetResult(true);
                    }
                    catch
                    {
                        attempt.Completion.TrySetResult(true);
                    }

                    if (!_voice.SupportsSpeechPlaybackEvents && !attempt.Completion.Task.IsCompleted)
                        _ = CompleteAfterEstimateAsync(session, attempt);
                }

                try
                {
                    await attempt.Completion.Task;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                finally
                {
                    attempt.Cancellation.Dispose();
                }

                bool completed;
                lock (_operationGate)
                {
                    lock (_gate)
                    {
                        if (!IsCurrentSessionLocked(session) ||
                            _state != SpeechPlaybackState.Playing ||
                            !ReferenceEquals(session.Attempt, attempt))
                            return;

                        session.Attempt = null;
                        session.CurrentText = session.Pending.Count > 0
                            ? session.Pending.Dequeue()
                            : null;
                        completed = session.CurrentText == null;
                        if (completed) SetStateLocked(SpeechPlaybackState.Idle);
                    }
                }

                if (completed)
                {
                    NotifyStateChanged(SpeechPlaybackState.Idle);
                    return;
                }
            }
        }

        private async Task CompleteAfterEstimateAsync(Session session, ChunkAttempt attempt)
        {
            try
            {
                await Task.Delay(GetEstimatedDuration(attempt.Text), attempt.Cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                attempt.Completion.TrySetCanceled();
                return;
            }

            lock (_gate)
            {
                if (IsCurrentSessionLocked(session) &&
                    _state == SpeechPlaybackState.Playing &&
                    ReferenceEquals(session.Attempt, attempt))
                    attempt.Completion.TrySetResult(true);
            }
        }

        private void OnSpeechPlaybackChanged(object? sender, bool playing)
        {
            lock (_gate)
            {
                Session? session = _session;
                ChunkAttempt? attempt = session?.Attempt;
                if (session == null || attempt == null || _state != SpeechPlaybackState.Playing)
                    return;

                if (playing)
                {
                    attempt.PlaybackStarted = true;
                }
                else if (attempt.PlaybackStarted)
                {
                    attempt.Completion.TrySetResult(true);
                }
            }
        }

        private ChunkAttempt? InvalidateSessionLocked()
        {
            Session? session = _session;
            if (session == null) return null;

            ChunkAttempt? attempt = session.Attempt;
            session.Attempt = null;
            session.CurrentText = null;
            session.Pending.Clear();
            session.PausedNatively = false;
            _session = null;
            return attempt;
        }

        private bool IsCurrentSessionLocked(Session session)
        {
            return ReferenceEquals(_session, session) && _session.Id == session.Id;
        }

        private bool SetStateLocked(SpeechPlaybackState state)
        {
            if (_state == state) return false;
            _state = state;
            return true;
        }

        private void NotifyStateChanged(SpeechPlaybackState state)
        {
            try { StateChanged?.Invoke(this, state); } catch { }
        }

        private static void CancelAttempt(ChunkAttempt? attempt)
        {
            if (attempt == null) return;
            try { attempt.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
            attempt.Completion.TrySetCanceled();
        }

        private static TimeSpan GetEstimatedDuration(string text)
        {
            long milliseconds = Math.Max(1500L, 1000L + 250L * text.Length);
            return TimeSpan.FromMilliseconds(Math.Min(milliseconds, int.MaxValue));
        }

        private sealed class Session
        {
            public Session(long id, Queue<string> pending)
            {
                Id = id;
                Pending = pending;
                CurrentText = pending.Dequeue();
            }

            public long Id { get; }
            public Queue<string> Pending { get; }
            public string? CurrentText { get; set; }
            public ChunkAttempt? Attempt { get; set; }
            public bool PausedNatively { get; set; }
        }

        private sealed class ChunkAttempt
        {
            public ChunkAttempt(string text)
            {
                Text = text;
            }

            public string Text { get; }
            public CancellationTokenSource Cancellation { get; } = new();
            public TaskCompletionSource<bool> Completion { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool PlaybackStarted { get; set; }
        }
    }
}
