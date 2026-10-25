namespace Common.Playback
{
    public enum PlaybackPhase { Stopped, Connecting, Queued, Buffering, Playing, Paused, Error }

    public static class PlaybackPresentation
    {
        // Native Media3 states are IDLE=1, BUFFERING=2, READY=3, ENDED=4.
        public static PlaybackPhase Resolve(bool hasTrack, bool hasError, int playerState,
            bool playWhenReady, bool isPlaying, bool suppressed, bool hasBytes, bool queued)
        {
            if (!hasTrack) return PlaybackPhase.Stopped;
            if (hasError) return PlaybackPhase.Error;
            if (playerState == 4) return PlaybackPhase.Stopped;
            if (!playWhenReady || suppressed) return PlaybackPhase.Paused;
            if (isPlaying) return PlaybackPhase.Playing;
            if (!hasBytes && queued) return PlaybackPhase.Queued;
            if (!hasBytes) return PlaybackPhase.Connecting;
            return PlaybackPhase.Buffering;
        }
    }
}
