using AndroidX.Media3.Common;
using Google.Common.Util.Concurrent;
using AndroidX.Concurrent.Futures;
using System;
using System.Collections.Generic;
using Commands = AndroidX.Media3.Common.BasePlayer.InterfaceConsts;

namespace Seeker.Playback
{
    /// <summary>A session view of the same ExoPlayer. It owns no decoder or transfer.</summary>
    internal sealed class SessionPlayer : ForwardingSimpleBasePlayer
    {
        private readonly IPlayer actualPlayer;
        private readonly Func<StreamingPlayback> current;
        private readonly Action stop;
        private readonly Func<bool, IListenableFuture> setPlayWhenReady;

        public SessionPlayer(IPlayer player, Func<StreamingPlayback> current, Action stop,
            Func<bool, IListenableFuture> setPlayWhenReady) : base(player)
        { actualPlayer = player; this.current = current; this.stop = stop; this.setPlayWhenReady = setPlayWhenReady; }

        public void Refresh() => InvalidateState();

        protected override State GetPlaceholderState(State suggestedPlaceholderState)
        {
            // Starting/resuming waits for the media foreground notification. Do not advertise
            // audible playback while that asynchronous operation is still pending.
            if (suggestedPlaceholderState.PlayWhenReady && suggestedPlaceholderState.PlayerError == null
                && actualPlayer?.PlayWhenReady == false && actualPlayer.MediaItemCount > 0 && current?.Invoke() != null)
                return suggestedPlaceholderState.BuildUpon().SetPlaybackState(Commands.StateBuffering).Build();
            return base.GetPlaceholderState(suggestedPlaceholderState);
        }

        protected override State GetState()
        {
            var builder = base.GetState().BuildUpon();
            var track = current?.Invoke();
            var commands = new PlayerCommands.Builder();
            // Advertise only implemented transport operations and the information controllers need.
            foreach (int command in new[] { Commands.CommandGetCurrentMediaItem, Commands.CommandGetTimeline,
                Commands.CommandGetMetadata, Commands.CommandGetAudioAttributes, Commands.CommandGetVolume,
                Commands.CommandSetVolume, Commands.CommandRelease })
                commands.AddIf(command, actualPlayer.AvailableCommands.Contains(command));
            commands.AddIf(Commands.CommandPlayPause, track != null && track.Failure == null);
            commands.AddIf(Commands.CommandStop, track != null);
            var seekRange = track?.SeekRange ?? default;
            commands.AddIf(Commands.CommandSeekInCurrentMediaItem,
                track != null && track.Failure == null && seekRange.CanSeek);
            builder.SetAvailableCommands(commands.Build());

            var playlist = new List<MediaItemData>();
            var item = actualPlayer.CurrentMediaItem;
            if (track != null && item != null)
            {
                // Strip original Format/APIC data and publish only the merged metadata and
                // bounded thumbnail. Seekability follows the extractor's completed-sample index.
                playlist.Add(new MediaItemData.Builder(item)
                    .SetMediaItem(item.BuildUpon().SetMediaMetadata(track.SessionMetadata).Build())
                    .SetMediaMetadata(track.SessionMetadata)
                    .SetIsSeekable(seekRange.CanSeek)
                    .SetDurationUs(seekRange.DurationUs > 0 ? seekRange.DurationUs : C.TimeUnset)
                    .Build());
            }
            builder.SetPlaylist(playlist).SetCurrentMediaItemIndex(playlist.Count == 0 ? C.IndexUnset : 0);
            if (track?.Failure != null)
                builder.SetPlaybackState(Commands.StateIdle).SetIsLoading(false)
                    .SetPlayerError(track.Failure).SetPlayWhenReady(false, Commands.PlayWhenReadyChangeReasonUserRequest);
            return builder.Build();
        }

        protected override IListenableFuture HandleStop()
        {
            stop();
            return CompletedFuture();
        }

        protected override IListenableFuture HandleSeek(int mediaItemIndex, long positionMs, int seekCommand)
        {
            var track = current?.Invoke();
            // Do not expose seek-back/forward increments or playlist operations. Every accepted
            // seek comes through the same per-track timestamp range as the in-app seek bar.
            if (seekCommand == Commands.CommandSeekInCurrentMediaItem
                && track != null && (mediaItemIndex == C.IndexUnset || mediaItemIndex == 0))
                track.TrySeekTo(positionMs);
            return CompletedFuture();
        }

        protected override IListenableFuture HandleSetPlayWhenReady(bool playWhenReady) => setPlayWhenReady(playWhenReady);

        internal static IListenableFuture CompletedFuture()
        {
            var future = ResolvableFuture.Create();
            future.Set(null);
            return future;
        }
    }
}
