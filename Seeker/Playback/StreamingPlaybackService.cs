using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Graphics;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.Session;
using AndroidX.Concurrent.Futures;
using AndroidX.Core.Content;
using Google.Common.Util.Concurrent;
using Common.Playback;
using Seeker.Helpers;
using Soulseek;
using System;
using System.Linq;
using System.Threading.Tasks;
using NativePlayer = AndroidX.Media3.Common.BasePlayer.InterfaceConsts;

namespace Seeker.Playback
{
    [Service(Name = ServiceName, Exported = true, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
    [IntentFilter(new[] { "androidx.media3.session.MediaSessionService" })]
    public sealed class StreamingPlaybackService : MediaSessionService
    {
        public const string ServiceName = "com.companyname.andriodapp1.StreamingPlaybackService";
        public const string OpenPlayerExtra = "noiser.open_player";
        private const string PlayAction = "noiser.play_selected_stream";
        private const string AppendAction = "noiser.append_stream_queue";
        private const string RequestExtra = "noiser.playback_request";
        private const string AppendRequestExtra = "noiser.playback_append_request";
        // Keep startup bounded. Tagged tracks normally publish metadata well before this; untagged
        // tracks complete the wait as soon as their audio tracks are known.
        private static readonly TimeSpan InitialMetadataWait = TimeSpan.FromMilliseconds(1500);
        private static readonly PlaybackRequestMailbox<PlayRequest> requests = new PlaybackRequestMailbox<PlayRequest>();
        private static readonly PlaybackAppendMailbox<AppendRequest> appendRequests = new PlaybackAppendMailbox<AppendRequest>();
        private readonly Handler handler = new Handler(Looper.MainLooper);
        private IExoPlayer actualPlayer;
        private MediaSession session;
        private PlaybackListener listener;
        private Task previousCleanup = Task.CompletedTask;
        private PowerManager.WakeLock transferCpuLock;
        private Android.Net.Wifi.WifiManager.WifiLock transferWifiLock;
        private bool handlingEvents;
        private bool destroyed;
        private ResolvableFuture pendingPlay;
        private string playbackAccount;

        internal static StreamingPlaybackService Current { get; private set; }
        internal static event Action Changed;
        internal StreamingPlayback Playback { get; private set; }
        internal PlaybackQueue Queue { get; private set; } = new PlaybackQueue();
        private readonly PlaybackControls controls = new PlaybackControls();
        internal PlaybackRepeatMode RepeatMode => controls.RepeatMode;
        internal bool Autoplay => controls.Autoplay;
        internal bool CanSkipNext => Queue.CurrentIndex >= 0
            && (Queue.HasNext || (RepeatMode == PlaybackRepeatMode.All && Queue.Entries.Count > 1));
        internal bool CanSkipPrevious => Queue.CurrentIndex >= 0
            && (Queue.HasPrevious || (RepeatMode == PlaybackRepeatMode.All && Queue.Entries.Count > 1));
        internal SessionPlayer SessionPlayer { get; private set; }
        internal string LastHandledRequestId { get; private set; }

        internal void CycleRepeatMode()
        {
            controls.CycleRepeatMode();
            Publish();
        }

        internal void ToggleAutoplay()
        {
            controls.ToggleAutoplay();
            Publish();
        }

        // An exported media service accepts transport controls, not arbitrary peer-download intents.
        // Only an in-process, single-use request created by the user's Play action selects a file.
        internal static string RequestPlay(Context context, string username, FullFileInfo file)
        {
            var entry = new PlaybackQueueEntry(username, file.FullFileName, file.Size,
                wasFilenameLatin1Decoded: file.wasFilenameLatin1Decoded,
                wasFolderLatin1Decoded: file.wasFolderLatin1Decoded);
            return RequestPlayQueue(context, new[] { entry }, entry.Id);
        }

        internal static string RequestPlayQueue(Context context,
            System.Collections.Generic.IEnumerable<PlaybackQueueEntry> entries, string selectedEntryId)
        {
            if (entries == null) throw new ArgumentNullException(nameof(entries));
            var copied = entries.Select(entry => entry.Copy()).ToArray();
            var validation = new PlaybackQueue();
            if (!validation.Replace(copied, selectedEntryId))
            {
                SeekerApplication.Toaster.ShowToast(SeekerApplication.GetString(Resource.String.stream_queue_empty), Android.Widget.ToastLength.Short);
                return null;
            }
            PlaybackRecovery.BeginUserSelection();
            string key = requests.Publish(new PlayRequest(validation.Entries, selectedEntryId));
            try
            {
                // Called from the visible file UI. Media3 promotes the service when buffering/play
                // starts, and owns the foreground notification; no duplicate placeholder notification.
                context.StartService(new Intent(context, typeof(StreamingPlaybackService))
                    .SetAction(PlayAction).PutExtra(RequestExtra, key));
                return key;
            }
            catch (Exception exception)
            {
                requests.TryTake(key, out _);
                Logger.Debug("Cannot start streaming service: " + exception.Message);
                SeekerApplication.Toaster.ShowToast(exception.Message, Android.Widget.ToastLength.Long);
                return null;
            }
        }

        internal static void CancelPending(string key) => requests.TryTake(key, out _);

        internal static string RequestAppend(Context context, PlaybackQueueEntry entry, bool playNext)
        {
            if (entry == null || !PlaybackQueue.IsSupportedAudio(entry.RemotePath)) return null;
            string key = appendRequests.Publish(new AppendRequest(entry.Copy(), playNext));
            try
            {
                context.StartService(new Intent(context, typeof(StreamingPlaybackService))
                    .SetAction(AppendAction).PutExtra(AppendRequestExtra, key));
                return key;
            }
            catch (Exception exception)
            {
                appendRequests.TryTake(key, out _);
                Logger.Debug("Cannot add stream queue entry: " + exception.Message);
                SeekerApplication.Toaster.ShowToast(exception.Message, Android.Widget.ToastLength.Long);
                return null;
            }
        }

        public override void OnCreate()
        {
            base.OnCreate();
            actualPlayer = new ExoPlayerBuilder(ApplicationContext)
                .SetLoadControl(new DefaultLoadControl.Builder().SetBufferDurationsMs(15000, 60000, 2000, 4000).Build())
                .SetWakeMode(C.WakeModeLocal)
                .SetHandleAudioBecomingNoisy(true).Build();
            actualPlayer.SetAudioAttributes(new AndroidX.Media3.Common.AudioAttributes.Builder()
                .SetUsage(C.UsageMedia).SetContentType(C.AudioContentTypeMusic).Build(), true);
            SessionPlayer = new SessionPlayer(actualPlayer, () => Playback, () => Queue,
                StopPlayback, SetPlayWhenReady, SkipToNext, SkipToPrevious, SelectQueueEntry,
                () => CanSkipNext, () => CanSkipPrevious);
            var launch = new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop)
                .PutExtra(OpenPlayerExtra, true);
            var activity = PendingIntent.GetActivity(this, 1401, launch, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
            var previous = new CommandButton.Builder(CommandButton.IconPrevious).SetPlayerCommand(NativePlayer.CommandSeekToPreviousMediaItem)
                .SetDisplayName(GetString(Resource.String.stream_previous)).SetSlots(new[] { CommandButton.SlotBack }).Build();
            var next = new CommandButton.Builder(CommandButton.IconNext).SetPlayerCommand(NativePlayer.CommandSeekToNextMediaItem)
                .SetDisplayName(GetString(Resource.String.stream_next)).SetSlots(new[] { CommandButton.SlotForward }).Build();
            var stop = new CommandButton.Builder(CommandButton.IconStop).SetPlayerCommand(NativePlayer.CommandStop)
                .SetDisplayName(GetString(Resource.String.stream_stop)).SetSlots(new[] { CommandButton.SlotOverflow }).Build();
            session = new MediaSession.Builder(this, SessionPlayer).SetId("noiser-stream")
                .SetSessionActivity(activity).SetMediaButtonPreferences((System.Collections.Generic.IList<CommandButton>)new[] { previous, next, stop }).Build();
            var provider = new DefaultMediaNotificationProvider.Builder(this).SetNotificationId(1401)
                .SetChannelId("noiser-stream-playback").SetChannelName(Resource.String.stream_notification_channel).Build();
            provider.SetSmallIcon(Resource.Drawable.stream_play);
            SetMediaNotificationProvider(provider);
            AddSession(session);
            listener = new PlaybackListener(tracks => Playback?.ReadTracks(tracks), OnPlayerEvents);
            actualPlayer.AddListener(listener);
            Current = this;
        }

        public override MediaSession OnGetSession(MediaSession.ControllerInfo controllerInfo) => destroyed ? null : session;

        private IListenableFuture SetPlayWhenReady(bool playWhenReady)
        {
            if (!playWhenReady)
            {
                var pending = pendingPlay;
                pendingPlay = null;
                actualPlayer.Pause();
                pending?.Set(null);
                return SessionPlayer.CompletedFuture();
            }
            if (actualPlayer.PlaybackState == NativePlayer.StateEnded && Playback != null)
            {
                try
                {
                    // Play after a naturally ended/hold state restarts the current track.
                    actualPlayer.SeekTo(0);
                    Queue.Current?.MarkPlaying();
                }
                catch (Exception exception)
                {
                    Playback.Fail(exception.Message);
                    return SessionPlayer.CompletedFuture();
                }
            }
            if (actualPlayer.PlayWhenReady) return SessionPlayer.CompletedFuture();
            // SimpleBasePlayer publishes the pending play intent using its standard placeholder
            // state. Media3 can then promote the service before ExoPlayer requests audio focus.
            // The underlying playback state/position are still forwarded from the one ExoPlayer.
            pendingPlay ??= ResolvableFuture.Create();
            return pendingPlay;
        }

        public override IListenableFuture OnUpdateNotificationAsync(MediaSession mediaSession, bool startInForegroundRequired)
        {
            var update = base.OnUpdateNotificationAsync(mediaSession, startInForegroundRequired);
            var pending = pendingPlay;
            var track = Playback;
            string trackId = track?.Id;
            if (startInForegroundRequired && pending != null)
            {
                update.AddListener(new Java.Lang.Runnable(() =>
                {
                    if (destroyed || !ReferenceEquals(pendingPlay, pending) || Playback?.Id != trackId) return;
                    try
                    {
                        update.Get();
                        // The service is foreground and the notification exists at this point.
                        // Delay only the audible start until the first ID3 metadata is visible.
                        PlayAfterInitialMetadataAsync(track, pending);
                    }
                    catch (Exception exception)
                    {
                        pendingPlay = null;
                        pending.Set(null);
                        track?.Fail(exception.Message);
                    }
                }), ContextCompat.GetMainExecutor(this));
            }
            return update;
        }

        private async void PlayAfterInitialMetadataAsync(StreamingPlayback track, ResolvableFuture pending)
        {
            try
            {
                if (track != null)
                    await track.WaitForInitialMetadataAsync(InitialMetadataWait).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Logger.Debug("Initial metadata wait: " + exception.Message);
            }
            RunOnMain(() =>
            {
                if (destroyed || !ReferenceEquals(pendingPlay, pending)) return;
                if (Playback == null || !ReferenceEquals(Playback, track) || track == null
                    || track.IsStopped || track.Failure != null)
                {
                    pendingPlay = null;
                    pending.Set(null);
                    return;
                }
                try
                {
                    pendingPlay = null;
                    actualPlayer.Play();
                    pending.Set(null);
                }
                catch (Exception exception)
                {
                    pendingPlay = null;
                    pending.Set(null);
                    track.Fail(exception.Message);
                }
            });
        }

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            if (intent?.Action == PlayAction)
            {
                if (requests.TryTake(intent.GetStringExtra(RequestExtra), out var request))
                    PlaySelected(intent.GetStringExtra(RequestExtra), request);
                else if (Playback == null) StopSelf();
            }
            else if (intent?.Action == AppendAction)
            {
                if (appendRequests.TryTake(intent.GetStringExtra(AppendRequestExtra), out var append)) AppendQueueEntry(append);
            }
            else if (intent != null) base.OnStartCommand(intent, flags, startId);
            // A killed process has no live peer transfer or buffer reader to restore.
            return StartCommandResult.NotSticky;
        }

        private void PlaySelected(string id, PlayRequest request)
        {
            LastHandledRequestId = id;
            var replacement = new PlaybackQueue();
            if (!replacement.Replace(request.Entries, request.SelectedEntryId)) return;
            var selected = replacement.Current;
            if (selected == null) return;
            bool retainCurrent = Playback != null && Playback.Failure == null
                && Playback.Username == selected.Username && Playback.FileInfo.FullFileName == selected.RemotePath;
            Queue = replacement;
            playbackAccount = Common.PreferencesState.Username;
            selected.MarkPlaying();
            PlaybackRecovery.Remember(this, Queue, playbackAccount);
            if (retainCurrent)
            {
                SessionPlayer.Play();
                Publish();
                return;
            }
            StartQueueEntry(selected, playWhenReady: true);
            Publish();
        }

        private void StartQueueEntry(PlaybackQueueEntry entry, bool playWhenReady)
        {
            playbackAccount ??= Common.PreferencesState.Username;
            // Checkpoint the selected identity before any player or network work can fail.
            PlaybackRecovery.Remember(this, Queue, playbackAccount);
            var pending = pendingPlay;
            pendingPlay = null;
            var previous = Playback;
            Playback = null;
            Task cleanup = previous?.Stop() ?? Task.CompletedTask;
            // Include all prior cleanup. A local-file interlude has no transfer task of its own,
            // but must not let an earlier remote stream overlap the next remote request.
            previousCleanup = Task.WhenAll(previousCleanup, cleanup);
            pending?.Set(null);
            var file = new FullFileInfo
            {
                FullFileName = entry.RemotePath,
                Size = entry.Size,
                Depth = 1,
                wasFilenameLatin1Decoded = entry.WasFilenameLatin1Decoded,
                wasFolderLatin1Decoded = entry.WasFolderLatin1Decoded
            };
            // Playback IDs are transition-specific even when the queue entry itself stays stable.
            Playback = new StreamingPlayback(this, actualPlayer, Guid.NewGuid().ToString("N"),
                entry.Id, entry.Username, file, previousCleanup);
            Playback.Start(playWhenReady);
        }

        internal void UpdateQueueMetadata(StreamingPlayback playback, TrackMetadata metadata)
        {
            RunOnMain(() =>
            {
                if (!IsCurrent(playback) || Queue.Current?.Id != playback.QueueEntryId) return;
                Queue.Current.UpdateMetadata(metadata);
                PlaybackRecovery.Remember(this, Queue, playbackAccount);
                Publish();
            });
        }

        private void AppendQueueEntry(AppendRequest request)
        {
            if (!PlaybackQueue.IsSupportedAudio(request.Entry.RemotePath)) return;
            bool empty = Queue.Current == null;
            bool added = request.PlayNext ? Queue.AddNext(request.Entry) : Queue.AddLast(request.Entry);
            if (!added) return;
            if (!empty) PlaybackRecovery.Remember(this, Queue, playbackAccount);
            if (empty)
            {
                Queue.Current.MarkPlaying();
                StartQueueEntry(Queue.Current, true);
            }
            Publish();
        }

        internal void RemoveQueueEntry(string entryId)
        {
            bool wasCurrent;
            var selected = Queue.Remove(entryId, out wasCurrent);
            if (selected != null) PlaybackRecovery.Remember(this, Queue, playbackAccount);
            if (!wasCurrent) { Publish(); return; }
            if (selected == null) { StopPlayback(); return; }
            selected.MarkPlaying();
            StartQueueEntry(selected, SessionPlayer.PlayWhenReady);
            Publish();
        }

        internal void ClearQueue() => StopPlayback();

        internal bool IsCurrent(StreamingPlayback track) => !destroyed && ReferenceEquals(Playback, track);

        internal void RunOnMain(Action action)
        {
            // Let stale metadata callbacks run their ownership check and dispose their bitmap.
            if (Looper.MyLooper() == Looper.MainLooper) action();
            else handler.Post(action);
        }

        private void OnPlayerEvents()
        {
            if (destroyed || handlingEvents) return;
            handlingEvents = true;
            try
            {
                if (actualPlayer.PlayerError != null)
                {
                    Queue.Current?.MarkFailed(actualPlayer.PlayerError.Message);
                    Playback?.Fail(actualPlayer.PlayerError.Message, actualPlayer.PlayerError);
                }
                else if (actualPlayer.PlaybackState == NativePlayer.StateEnded) AdvanceOnCompletion();
                Playback?.UpdateDuration();
                Publish();
            }
            finally { handlingEvents = false; }
        }

        private void AdvanceOnCompletion()
        {
            if (Playback == null) return;
            if (RepeatMode == PlaybackRepeatMode.One)
            {
                ReplayCurrent();
                return;
            }
            // Autoplay is the master switch. Repeat All only wraps once autoplay continues.
            if (!Autoplay)
            {
                HoldAtEnd();
                return;
            }
            var next = Queue.MoveNext(completed: true);
            if (next == null && RepeatMode == PlaybackRepeatMode.All && Queue.Entries.Count == 1)
            {
                ReplayCurrent();
                return;
            }
            if (next == null && RepeatMode == PlaybackRepeatMode.All && Queue.Entries.Count > 1)
            {
                next = Queue.Entries[0];
                Queue.Select(next.Id);
            }
            if (next == null)
            {
                HoldAtEnd();
                return;
            }
            next.MarkPlaying();
            StartQueueEntry(next, true);
            Publish();
        }

        private void HoldAtEnd()
        {
            // Do not clear the queue/index. Queue, Next, Previous, and Play remain usable.
            Queue.Current?.MarkPlayed();
            if (actualPlayer?.PlayWhenReady == true) actualPlayer.Pause();
            Publish();
        }

        private void ReplayCurrent()
        {
            if (Playback == null || Playback.Failure != null || Queue.Current == null)
            {
                HoldAtEnd();
                return;
            }
            try
            {
                Queue.Current.MarkPlaying();
                actualPlayer.SeekTo(0);
                actualPlayer.Pause();
                SessionPlayer.Play();
                Publish();
            }
            catch (Exception exception)
            {
                Playback.Fail(exception.Message);
            }
        }

        internal bool SkipToNext()
        {
            var next = Queue.MoveNext();
            if (next == null && RepeatMode == PlaybackRepeatMode.All && Queue.Entries.Count > 1)
            {
                next = Queue.Entries[0];
                Queue.Select(next.Id);
            }
            if (next == null) return false;
            next.MarkPlaying();
            StartQueueEntry(next, true);
            Publish();
            return true;
        }

        internal bool SkipToPrevious()
        {
            PlaybackQueueEntry? previous;
            if (Queue.HasPrevious)
            {
                previous = Queue.MovePrevious();
            }
            else if (RepeatMode == PlaybackRepeatMode.All && Queue.Entries.Count > 1)
            {
                previous = Queue.Entries[Queue.Entries.Count - 1];
                Queue.Select(previous.Id);
            }
            else
            {
                return false;
            }
            if (previous == null) return false;
            previous.MarkPlaying();
            StartQueueEntry(previous, true);
            Publish();
            return true;
        }

        internal bool SelectQueueEntry(string entryId)
        {
            var oldCurrent = Queue.Current;
            bool sameEntry = oldCurrent?.Id == entryId;
            var selected = Queue.Select(entryId);
            if (selected == null) return false;
            bool retainCurrent = sameEntry && Playback != null && Playback.Failure == null
                && actualPlayer.PlaybackState != NativePlayer.StateEnded
                && Playback.Username == selected.Username
                && Playback.FileInfo.FullFileName == selected.RemotePath;
            if (!retainCurrent)
            {
                if (!sameEntry && oldCurrent?.State == PlaybackQueueEntryState.Playing) oldCurrent.MarkSkipped();
                selected.MarkPlaying();
                StartQueueEntry(selected, true);
            }
            Publish();
            return true;
        }

        internal void Publish()
        {
            if (destroyed) return;
            if (Playback?.Failure != null && pendingPlay != null)
            {
                var pending = pendingPlay;
                pendingPlay = null;
                pending.Set(null);
            }
            UpdateTransferLocks();
            SessionPlayer?.Refresh();
            NotifyViews();
        }

        private static void NotifyViews()
        {
            if (Changed == null) return;
            foreach (Action observer in Changed.GetInvocationList())
            {
                try { observer(); }
                catch (Exception exception) { Logger.Debug("Playback UI update: " + exception.Message); }
            }
        }

        internal PlaybackSnapshot Snapshot()
        {
            if (destroyed || Playback == null) return null;
            var track = Playback;
            var phase = PlaybackPresentation.Resolve(true, track.Failure != null, actualPlayer.PlaybackState,
                SessionPlayer.PlayWhenReady, actualPlayer.IsPlaying, actualPlayer.PlaybackSuppressionReason != 0,
                track.HasBytes, track.IsQueued);
            return new PlaybackSnapshot(track.Metadata, track.Artwork, phase, actualPlayer.CurrentPosition,
                track.Received, track.FileInfo.Size, track.Failure?.Message, track.Id, track.SeekRange,
                SessionPlayer.PlayWhenReady && actualPlayer.PlaybackSuppressionReason == 0,
                RepeatMode, Autoplay);
        }

        internal void StopPlayback()
        {
            var pending = pendingPlay;
            pendingPlay = null;
            var previous = Playback;
            Playback = null;
            if (previous != null) previousCleanup = Task.WhenAll(previousCleanup, previous.Stop());
            else { actualPlayer?.Pause(); actualPlayer?.Stop(); actualPlayer?.ClearMediaItems(); }
            Queue.Clear();
            PlaybackRecovery.Forget();
            pending?.Set(null);
            Publish();
            StopSelf();
        }

        public override void OnTaskRemoved(Intent rootIntent)
        {
            if (!IsPlaybackOngoing) { Shutdown(); StopSelf(); }
            base.OnTaskRemoved(rootIntent);
        }

        public override void OnDestroy()
        {
            Shutdown();
            base.OnDestroy();
        }

        internal static void ShutdownCurrent()
        {
            var service = Current;
            service?.Shutdown();
            service?.StopSelf();
        }

        private void Shutdown()
        {
            if (destroyed) return;
            destroyed = true;
            if (ReferenceEquals(Current, this)) Current = null;
            var previous = Playback;
            Playback = null;
            previous?.Stop();
            Queue.Clear();
            pendingPlay?.Set(null);
            pendingPlay = null;
            ReleaseTransferLocks();
            session?.Release();
            if (listener != null) actualPlayer?.RemoveListener(listener);
            listener?.Dispose();
            SessionPlayer?.Release();
            SessionPlayer?.Dispose();
            actualPlayer?.Dispose();
            session?.Dispose();
            NotifyViews();
        }

        private void UpdateTransferLocks()
        {
            if (Playback?.IsReceiving != true || Playback.Failure != null) { ReleaseTransferLocks(); return; }
            if (transferCpuLock == null)
            {
                transferCpuLock = ((PowerManager)GetSystemService(PowerService)).NewWakeLock(WakeLockFlags.Partial, "noiser:stream-transfer");
                transferCpuLock.SetReferenceCounted(false);
                var wifi = (Android.Net.Wifi.WifiManager)ApplicationContext.GetSystemService(WifiService);
                transferWifiLock = wifi?.CreateWifiLock(Android.Net.WifiMode.FullHighPerf, "noiser:stream-transfer");
                transferWifiLock?.SetReferenceCounted(false);
            }
            if (!transferCpuLock.IsHeld) transferCpuLock.Acquire();
            if (transferWifiLock?.IsHeld == false) transferWifiLock.Acquire();
        }

        private void ReleaseTransferLocks()
        {
            if (transferCpuLock?.IsHeld == true) transferCpuLock.Release();
            if (transferWifiLock?.IsHeld == true) transferWifiLock.Release();
        }

        private sealed class PlayRequest
        {
            public System.Collections.Generic.IReadOnlyList<PlaybackQueueEntry> Entries { get; }
            public string SelectedEntryId { get; }
            public PlayRequest(System.Collections.Generic.IReadOnlyList<PlaybackQueueEntry> entries, string selectedEntryId)
            { Entries = entries.Select(entry => entry.Copy()).ToArray(); SelectedEntryId = selectedEntryId; }
        }

        private sealed class AppendRequest
        {
            public PlaybackQueueEntry Entry { get; }
            public bool PlayNext { get; }
            public AppendRequest(PlaybackQueueEntry entry, bool playNext) { Entry = entry.Copy(); PlayNext = playNext; }
        }
    }

    internal sealed class PlaybackSnapshot
    {
        public TrackMetadata Metadata { get; }
        public Bitmap Artwork { get; }
        public PlaybackPhase Phase { get; }
        public long Position { get; }
        public long Received { get; }
        public long Size { get; }
        public string Error { get; }
        public string TrackId { get; }
        public Common.Playback.SeekRangeSnapshot SeekRange { get; }
        public bool ShowPause { get; }
        public PlaybackRepeatMode RepeatMode { get; }
        public bool Autoplay { get; }
        public PlaybackSnapshot(TrackMetadata metadata, Bitmap artwork, PlaybackPhase phase, long position,
            long received, long size, string error, string trackId, Common.Playback.SeekRangeSnapshot seekRange,
            bool showPause, PlaybackRepeatMode repeatMode, bool autoplay)
        { Metadata = metadata; Artwork = artwork; Phase = phase; Position = position; Received = received; Size = size; Error = error; TrackId = trackId; SeekRange = seekRange; ShowPause = showPause; RepeatMode = repeatMode; Autoplay = autoplay; }
    }
}
