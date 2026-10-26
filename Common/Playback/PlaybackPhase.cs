namespace Common.Playback
{
    public enum PlaybackPhase { Stopped, Connecting, Queued, Buffering, Playing, Paused, Error, Reconnecting, Stalled, Saved }

    public static class PlaybackPresentation
    {
        // Native Media3 states are IDLE=1, BUFFERING=2, READY=3, ENDED=4.
        public static PlaybackPhase Resolve(bool hasTrack, bool hasError, int playerState,
            bool playWhenReady, bool isPlaying, bool suppressed, bool hasBytes, bool queued,
            bool reconnecting = false, bool stalled = false)
        {
            if (!hasTrack) return PlaybackPhase.Stopped;
            if (hasError) return PlaybackPhase.Error;
            if (playerState == 4) return PlaybackPhase.Stopped;
            if (!playWhenReady || suppressed) return PlaybackPhase.Paused;
            if (isPlaying) return PlaybackPhase.Playing;
            if (reconnecting) return PlaybackPhase.Reconnecting;
            if (queued) return PlaybackPhase.Queued;
            if (stalled) return PlaybackPhase.Stalled;
            if (!hasBytes) return PlaybackPhase.Connecting;
            return PlaybackPhase.Buffering;
        }

        public static bool IsStalled(bool receiving, long received, long size, System.TimeSpan idleTime) =>
            receiving && received < size && idleTime >= System.TimeSpan.FromSeconds(20);

        public static bool CanRetry(PlaybackPhase phase) => phase == PlaybackPhase.Error || phase == PlaybackPhase.Stalled;
    }
}
