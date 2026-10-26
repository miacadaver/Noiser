using AndroidX.Media3.Common;
using Google.Common.Util.Concurrent;
using AndroidX.Concurrent.Futures;
using Common.Playback;
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
        private readonly Func<PlaybackQueue> queue;
        private readonly Action stop;
        private readonly Func<bool, IListenableFuture> setPlayWhenReady;
        private readonly Func<bool> next;
        private readonly Func<bool> previous;
        private readonly Func<string, bool> select;
        private readonly Func<bool> canNext;
        private readonly Func<bool> canPrevious;

        public SessionPlayer(IPlayer player, Func<StreamingPlayback> current, Func<PlaybackQueue> queue,
            Action stop, Func<bool, IListenableFuture> setPlayWhenReady, Func<bool> next,
            Func<bool> previous, Func<string, bool> select, Func<bool> canNext, Func<bool> canPrevious) : base(player)
        { actualPlayer = player; this.current = current; this.queue = queue; this.stop = stop; this.setPlayWhenReady = setPlayWhenReady; this.next = next; this.previous = previous; this.select = select; this.canNext = canNext; this.canPrevious = canPrevious; }

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
            var playbackQueue = queue?.Invoke();
            commands.AddIf(Commands.CommandSeekToNextMediaItem, track != null && canNext?.Invoke() == true);
            commands.AddIf(Commands.CommandSeekToPreviousMediaItem, track != null && canPrevious?.Invoke() == true);
            commands.AddIf(Commands.CommandSeekToMediaItem, track != null && playbackQueue?.Entries.Count > 1);
            var seekRange = track?.SeekRange ?? default;
            commands.AddIf(Commands.CommandSeekInCurrentMediaItem,
                track != null && track.Failure == null && seekRange.CanSeek);
            builder.SetAvailableCommands(commands.Build());

            var playlist = new List<MediaItemData>();
            if (track != null && playbackQueue?.Entries.Count > 0)
            {
                for (int i = 0; i < playbackQueue.Entries.Count; i++)
                {
                    var entry = playbackQueue.Entries[i];
                    bool isCurrent = i == playbackQueue.CurrentIndex;
                    MediaMetadata metadata = isCurrent ? track.SessionMetadata : StreamingMetadata.BuildSessionMetadata(entry.Metadata, null);
                    var item = new MediaItem.Builder().SetMediaId(entry.Id)
                        .SetUri(Android.Net.Uri.Parse("noiser-queue:///" + Uri.EscapeDataString(entry.Id)))
                        .SetMediaMetadata(metadata).Build();
                    playlist.Add(new MediaItemData.Builder(entry.Id).SetMediaItem(item).SetMediaMetadata(metadata)
                        .SetIsSeekable(isCurrent && seekRange.CanSeek)
                        .SetDurationUs(isCurrent && seekRange.DurationUs > 0 ? seekRange.DurationUs : C.TimeUnset).Build());
                }
            }
            builder.SetPlaylist(playlist).SetCurrentMediaItemIndex(playlist.Count == 0 ? C.IndexUnset : playbackQueue.CurrentIndex);
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
            var playbackQueue = queue?.Invoke();
            if (seekCommand == Commands.CommandSeekInCurrentMediaItem
                && track != null && (mediaItemIndex == C.IndexUnset || mediaItemIndex == playbackQueue?.CurrentIndex))
                track.TrySeekTo(positionMs);
            else if (seekCommand == Commands.CommandSeekToNextMediaItem) next?.Invoke();
            else if (seekCommand == Commands.CommandSeekToPreviousMediaItem) previous?.Invoke();
            else if (seekCommand == Commands.CommandSeekToMediaItem && playbackQueue != null
                && mediaItemIndex >= 0 && mediaItemIndex < playbackQueue.Entries.Count)
            {
                // An external controller may select a queued item at zero. Media3's
                // seekToDefaultPosition() supplies TimeUnset rather than zero.
                if (positionMs == 0 || positionMs == C.TimeUnset)
                    select?.Invoke(playbackQueue.Entries[mediaItemIndex].Id);
            }
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
