using System;
using System.Collections.Generic;

namespace MaxChemical.Shell.Services
{
    public enum SpeechPlaybackState
    {
        Idle,
        Playing,
        Paused,
        Stopped
    }

    public interface ISpeechPlaybackService
    {
        SpeechPlaybackState State { get; }

        event EventHandler<SpeechPlaybackState> StateChanged;

        void Play(IEnumerable<string> chunks);

        void Stop();

        void Pause();

        void Resume();
    }
}
