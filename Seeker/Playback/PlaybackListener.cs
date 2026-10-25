using AndroidX.Media3.Common;
using System;

namespace Seeker.Playback
{
    internal sealed class PlaybackListener : Java.Lang.Object, IPlayerListener
    {
        private readonly Action<Tracks> tracksChanged;
        private readonly Action stateChanged;
        public PlaybackListener(Action<Tracks> tracksChanged, Action stateChanged)
        { this.tracksChanged = tracksChanged; this.stateChanged = stateChanged; }
        public void OnMediaMetadataChanged(MediaMetadata metadata) { }

        // Explicit implementations generate Java callback bridges. The binding's CLR default
        // interface bodies do not cover these callbacks on Android (AbstractMethodError).
        public void OnAudioAttributesChanged(AndroidX.Media3.Common.AudioAttributes audioAttributes) { }
        public void OnAudioSessionIdChanged(int audioSessionId) { }
        public void OnAvailableCommandsChanged(PlayerCommands availableCommands) { }
        public void OnCues(AndroidX.Media3.Common.Text.CueGroup cueGroup) { }
        public void OnCuesDeprecated(System.Collections.Generic.IList<AndroidX.Media3.Common.Text.Cue> cues) { }
        public void OnDeviceInfoChanged(DeviceInfo deviceInfo) { }
        public void OnDeviceVolumeChanged(int volume, bool muted) { }
        public void OnEvents(IPlayer player, PlayerEvents events) => stateChanged?.Invoke();
        public void OnIsLoadingChanged(bool isLoading) { }
        public void OnIsPlayingChanged(bool isPlaying) { }
        public void OnLoadingChanged(bool isLoading) { }
        public void OnMaxSeekToPreviousPositionChanged(long maxSeekToPreviousPositionMs) { }
        public void OnMediaItemTransition(MediaItem mediaItem, int reason) { }
        public void OnMetadata(AndroidX.Media3.Common.Metadata metadata) { }
        public void OnPlayWhenReadyChanged(bool playWhenReady, int reason) { }
        public void OnPlaybackParametersChanged(PlaybackParameters playbackParameters) { }
        public void OnPlaybackStateChanged(int playbackState) { }
        public void OnPlaybackSuppressionReasonChanged(int playbackSuppressionReason) { }
        public void OnPlayerError(PlaybackException error) { }
        public void OnPlayerErrorChanged(PlaybackException error) { }
        public void OnPlayerStateChanged(bool playWhenReady, int playbackState) { }
        public void OnPlaylistMetadataChanged(MediaMetadata mediaMetadata) { }
        // Still present in the Java AAR, although absent from the current C# interface.
        [Java.Interop.Export("onPositionDiscontinuity")]
        public void OnPositionDiscontinuity(int reason) { }
        public void OnPositionDiscontinuity(PlayerPositionInfo oldPosition, PlayerPositionInfo newPosition, int reason) { }
        public void OnRenderedFirstFrame() { }
        public void OnRepeatModeChanged(int repeatMode) { }
        public void OnSeekBackIncrementChanged(long seekBackIncrementMs) { }
        public void OnSeekForwardIncrementChanged(long seekForwardIncrementMs) { }
        public void OnShuffleModeEnabledChanged(bool shuffleModeEnabled) { }
        public void OnSkipSilenceEnabledChanged(bool skipSilenceEnabled) { }
        public void OnSurfaceSizeChanged(int width, int height) { }
        public void OnTimelineChanged(Timeline timeline, int reason) { }
        public void OnTrackSelectionParametersChanged(TrackSelectionParameters parameters) { }
        public void OnTracksChanged(Tracks tracks) => tracksChanged?.Invoke(tracks);
        public void OnVideoSizeChanged(VideoSize videoSize) { }
        public void OnVolumeChanged(float volume) { }
    }
}
