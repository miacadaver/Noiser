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
        private const string RequestExtra = "noiser.playback_request";
        private static readonly PlaybackRequestMailbox<PlayRequest> requests = new PlaybackRequestMailbox<PlayRequest>();
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

        internal static StreamingPlaybackService Current { get; private set; }
        internal static event Action Changed;
        internal StreamingPlayback Playback { get; private set; }
        internal SessionPlayer SessionPlayer { get; private set; }
        internal string LastHandledRequestId { get; private set; }

        // An exported media service accepts transport controls, not arbitrary peer-download intents.
        // Only an in-process, single-use request created by the user's Play action selects a file.
        internal static string RequestPlay(Context context, string username, FullFileInfo file)
        {
            string key = requests.Publish(new PlayRequest(username, file));
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

        public override void OnCreate()
        {
            base.OnCreate();
            actualPlayer = new ExoPlayerBuilder(ApplicationContext)
                .SetLoadControl(new DefaultLoadControl.Builder().SetBufferDurationsMs(15000, 60000, 2000, 4000).Build())
                .SetWakeMode(C.WakeModeLocal)
                .SetHandleAudioBecomingNoisy(true).Build();
            actualPlayer.SetAudioAttributes(new AndroidX.Media3.Common.AudioAttributes.Builder()
                .SetUsage(C.UsageMedia).SetContentType(C.AudioContentTypeMusic).Build(), true);
            SessionPlayer = new SessionPlayer(actualPlayer, () => Playback, StopPlayback, SetPlayWhenReady);
            var launch = new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop)
                .PutExtra(OpenPlayerExtra, true);
            var activity = PendingIntent.GetActivity(this, 1401, launch, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
            var stop = new CommandButton.Builder(CommandButton.IconStop).SetPlayerCommand(NativePlayer.CommandStop)
                .SetDisplayName(GetString(Resource.String.stream_stop)).SetSlots(new[] { CommandButton.SlotForward }).Build();
            session = new MediaSession.Builder(this, SessionPlayer).SetId("noiser-stream")
                .SetSessionActivity(activity).SetMediaButtonPreferences((System.Collections.Generic.IList<CommandButton>)new[] { stop }).Build();
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
            string trackId = Playback?.Id;
            if (startInForegroundRequired && pending != null)
            {
                update.AddListener(new Java.Lang.Runnable(() =>
                {
                    if (destroyed || !ReferenceEquals(pendingPlay, pending) || Playback?.Id != trackId) return;
                    try
                    {
                        update.Get();
                        pendingPlay = null;
                        actualPlayer.Play();
                        pending.Set(null);
                    }
                    catch (Exception exception) { Playback?.Fail(exception.Message); }
                }), ContextCompat.GetMainExecutor(this));
            }
            return update;
        }

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId)
        {
            if (intent?.Action == PlayAction)
            {
                if (requests.TryTake(intent.GetStringExtra(RequestExtra), out var request))
                    PlaySelected(intent.GetStringExtra(RequestExtra), request);
                else if (Playback == null) StopSelf();
            }
            else if (intent != null) base.OnStartCommand(intent, flags, startId);
            // A killed process has no live peer transfer or buffer reader to restore.
            return StartCommandResult.NotSticky;
        }

        private void PlaySelected(string id, PlayRequest request)
        {
            LastHandledRequestId = id;
            if (Playback != null && Playback.Failure == null && Playback.Username == request.Username
                && Playback.FileInfo.FullFileName == request.File.FullFileName)
            {
                SessionPlayer.Play();
                Publish();
                return;
            }
            var pending = pendingPlay;
            pendingPlay = null;
            var previous = Playback;
            Playback = null;
            if (previous != null) previousCleanup = previous.Stop();
            pending?.Set(null);
            Playback = new StreamingPlayback(this, actualPlayer, id, request.Username, request.File, previousCleanup);
            Playback.Start();
            Publish();
        }

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
                if (actualPlayer.PlayerError != null) Playback?.Fail(actualPlayer.PlayerError.Message, actualPlayer.PlayerError);
                else if (actualPlayer.PlaybackState == NativePlayer.StateEnded) StopPlayback();
                Playback?.UpdateDuration();
                Publish();
            }
            finally { handlingEvents = false; }
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
                track.Received, track.FileInfo.Size, track.Failure?.Message,
                SessionPlayer.PlayWhenReady && actualPlayer.PlaybackSuppressionReason == 0);
        }

        internal void StopPlayback()
        {
            var pending = pendingPlay;
            pendingPlay = null;
            var previous = Playback;
            Playback = null;
            if (previous != null) previousCleanup = previous.Stop();
            else { actualPlayer?.Pause(); actualPlayer?.Stop(); actualPlayer?.ClearMediaItems(); }
            pending?.Set(null);
            Publish();
            StopSelf();
        }

        public override void OnTaskRemoved(Intent rootIntent)
        {
            if (!IsPlaybackOngoing) StopPlayback();
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
            public string Username { get; }
            public FullFileInfo File { get; }
            public PlayRequest(string username, FullFileInfo file) { Username = username; File = file; }
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
        public bool ShowPause { get; }
        public PlaybackSnapshot(TrackMetadata metadata, Bitmap artwork, PlaybackPhase phase, long position,
            long received, long size, string error, bool showPause)
        { Metadata = metadata; Artwork = artwork; Phase = phase; Position = position; Received = received; Size = size; Error = error; ShowPause = showPause; }
    }
}
