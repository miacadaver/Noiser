namespace Common.Playback
{
    /// <summary>Repeat policy for the service-owned folder queue. Exactly one mode is active.</summary>
    public enum PlaybackRepeatMode { Off, All, One }

    /// <summary>
    /// Pure repeat/autoplay state. Autoplay is the master switch: selecting All or One enables it,
    /// and disabling autoplay returns to Repeat Off so the controls cannot contradict each other.
    /// </summary>
    public sealed class PlaybackControls
    {
        public PlaybackRepeatMode RepeatMode { get; private set; } = PlaybackRepeatMode.Off;
        public bool Autoplay { get; private set; } = true;

        public void CycleRepeatMode()
        {
            RepeatMode = RepeatMode switch
            {
                PlaybackRepeatMode.Off => PlaybackRepeatMode.All,
                PlaybackRepeatMode.All => PlaybackRepeatMode.One,
                _ => PlaybackRepeatMode.Off
            };
            if (RepeatMode != PlaybackRepeatMode.Off) Autoplay = true;
        }

        public void ToggleAutoplay()
        {
            Autoplay = !Autoplay;
            if (!Autoplay) RepeatMode = PlaybackRepeatMode.Off;
        }
    }
}
