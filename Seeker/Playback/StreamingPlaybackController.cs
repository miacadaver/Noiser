using Android.OS;
using Android.Content;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.Core.Content;
using AndroidX.Fragment.App;
using AndroidX.Media3.Common;
using AndroidX.Media3.Session;
using Common.Playback;
using Google.Common.Util.Concurrent;
using MediaController = AndroidX.Media3.Session.MediaController;
using Seeker.Helpers;
using Soulseek;
using System;

namespace Seeker.Playback
{
    /// <summary>Activity UI only. Its MediaController is a proxy, never a second audio player.</summary>
    internal sealed class StreamingPlaybackController
    {
        private readonly MainActivity activity;
        private readonly Handler handler = new Handler(Looper.MainLooper);
        private readonly Java.Lang.Runnable tick;
        private readonly View mini;
        private readonly ImageView miniArtwork;
        private readonly TextView miniTitle;
        private readonly TextView miniArtist;
        private readonly ImageButton miniPause;
        private IListenableFuture controllerFuture;
        private MediaController controller;
        private AndroidX.AppCompat.App.AlertDialog dialog;
        private ImageView artwork;
        private TextView artist;
        private TextView album;
        private TextView status;
        private TextView progress;
        private Button pause;
        private bool attached;
        private string pendingRequest;

        internal StreamingPlaybackController(MainActivity activity)
        {
            this.activity = activity;
            tick = new Java.Lang.Runnable(Update);
            mini = activity.FindViewById(Resource.Id.streamMiniPlayer);
            miniArtwork = activity.FindViewById<ImageView>(Resource.Id.streamMiniArtwork);
            miniTitle = activity.FindViewById<TextView>(Resource.Id.streamMiniTitle);
            miniArtist = activity.FindViewById<TextView>(Resource.Id.streamMiniArtist);
            miniPause = activity.FindViewById<ImageButton>(Resource.Id.streamMiniPause);
            mini.Click += (_, __) => ShowCurrent();
            miniPause.Click += (_, __) => TogglePause();
        }

        public static void Show(FragmentActivity activity, string username, FullFileInfo file)
        {
            activity.RunOnUiThread(() =>
            {
                string request = StreamingPlaybackService.RequestPlay(activity, username, file);
                if (request == null) return;
                if (activity is MainActivity main)
                {
                    var ui = main.EnsurePlaybackUi();
                    ui.pendingRequest = request;
                    ui.Connect();
                    ui.ShowCurrent();
                }
                else activity.StartActivity(new Intent(activity, typeof(MainActivity))
                    .SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop)
                    .PutExtra(StreamingPlaybackService.OpenPlayerExtra, true));
            });
        }

        public static void CleanAbandonedCache(string cacheDirectory) => StreamingPlayback.CleanAbandonedCache(cacheDirectory);

        internal void Attach()
        {
            if (attached) return;
            attached = true;
            StreamingPlaybackService.Changed += Update;
            Update();
        }

        internal void Detach()
        {
            if (!attached) return;
            attached = false;
            StreamingPlaybackService.Changed -= Update;
            handler.RemoveCallbacks(tick);
            dialog?.Dismiss();
            dialog = null;
            miniArtwork.SetImageResource(Resource.Drawable.stream_artwork_placeholder);
            mini.Visibility = ViewStates.Gone;
            if (controllerFuture != null) MediaController.ReleaseFuture(controllerFuture);
            controllerFuture = null;
            controller = null;
        }

        private void Connect()
        {
            if (!attached || controllerFuture != null) return;
            var context = activity.ApplicationContext;
            var token = new SessionToken(context, new ComponentName(context, StreamingPlaybackService.ServiceName));
            var future = new MediaController.Builder(context, token).BuildAsync();
            controllerFuture = future;
            future.AddListener(new Java.Lang.Runnable(() =>
            {
                if (!attached || !ReferenceEquals(controllerFuture, future)) return;
                try { controller = future.Get().JavaCast<MediaController>(); }
                catch (Exception exception)
                {
                    Logger.Debug("Cannot connect playback controls: " + exception.Message);
                    MediaController.ReleaseFuture(future);
                    controllerFuture = null;
                }
                Update();
            }), ContextCompat.GetMainExecutor(context));
        }

        private IPlayer Controls => controller?.IsConnected == true
            ? controller : StreamingPlaybackService.Current?.SessionPlayer;

        private void TogglePause()
        {
            var snapshot = StreamingPlaybackService.Current?.Snapshot();
            if (snapshot == null || snapshot.Phase == PlaybackPhase.Error) return;
            if (snapshot.ShowPause) Controls?.Pause();
            else Controls?.Play();
        }

        private void Stop()
        {
            StreamingPlaybackService.CancelPending(pendingRequest);
            pendingRequest = null;
            Controls?.Stop();
            Update();
        }

        internal void ShowCurrent()
        {
            if (!attached || activity.IsFinishing || activity.IsDestroyed) return;
            if (dialog?.IsShowing == true) return;
            View view = activity.LayoutInflater.Inflate(Resource.Layout.stream_playback, null);
            artwork = view.FindViewById<ImageView>(Resource.Id.streamArtwork);
            artist = view.FindViewById<TextView>(Resource.Id.streamArtist);
            album = view.FindViewById<TextView>(Resource.Id.streamAlbum);
            status = view.FindViewById<TextView>(Resource.Id.streamStatus);
            progress = view.FindViewById<TextView>(Resource.Id.streamProgress);
            pause = view.FindViewById<Button>(Resource.Id.streamPause);
            pause.Click += (_, __) => TogglePause();
            var window = new Google.Android.Material.Dialog.MaterialAlertDialogBuilder(activity)
                .SetTitle(Resource.String.stream_player)
                .SetView(view)
                .SetPositiveButton(Resource.String.close, (_, __) => { })
                .SetNegativeButton(Resource.String.stream_stop, (_, __) => Stop()).Create();
            dialog = window;
            var windowArtwork = artwork;
            window.DismissEvent += (_, __) =>
            {
                // Closing/minimizing the UI never controls playback lifetime.
                windowArtwork.SetImageResource(Resource.Drawable.stream_artwork_placeholder);
                if (!ReferenceEquals(dialog, window)) return;
                dialog = null;
                pendingRequest = null;
                Update();
            };
            dialog.Show();
            if (StreamingPlaybackService.Current?.Playback != null || pendingRequest != null) Connect();
            Update();
        }

        private void Update()
        {
            handler.RemoveCallbacks(tick);
            if (!attached || activity.IsFinishing || activity.IsDestroyed) return;
            var service = StreamingPlaybackService.Current;
            if (service?.LastHandledRequestId == pendingRequest) pendingRequest = null;
            var snapshot = pendingRequest == null ? service?.Snapshot() : null;
            if (controller != null && !controller.IsConnected)
            {
                MediaController.ReleaseFuture(controllerFuture);
                controllerFuture = null;
                controller = null;
            }
            if (snapshot != null) Connect();
            bool full = dialog?.IsShowing == true;
            if (snapshot == null && pendingRequest == null && controllerFuture != null)
            {
                MediaController.ReleaseFuture(controllerFuture);
                controllerFuture = null;
                controller = null;
            }
            mini.Visibility = snapshot != null && !full ? ViewStates.Visible : ViewStates.Gone;
            if (snapshot == null)
            {
                miniArtwork.SetImageResource(Resource.Drawable.stream_artwork_placeholder);
                if (full)
                {
                    dialog.SetTitle(Resource.String.stream_player);
                    artwork.SetImageResource(Resource.Drawable.stream_artwork_placeholder);
                    artist.Text = album.Text = progress.Text = string.Empty;
                    artist.Visibility = album.Visibility = ViewStates.Gone;
                    status.SetText(pendingRequest != null ? Resource.String.stream_connecting : Resource.String.stream_stopped);
                    pause.Enabled = false;
                }
            }
            else
            {
                miniTitle.Text = snapshot.Metadata.Title;
                miniArtist.Text = snapshot.Metadata.Artist;
                miniArtist.Visibility = string.IsNullOrEmpty(snapshot.Metadata.Artist) ? ViewStates.Gone : ViewStates.Visible;
                SetArtwork(miniArtwork, snapshot);
                miniPause.SetImageResource(snapshot.ShowPause ? Resource.Drawable.stream_pause : Resource.Drawable.stream_play);
                miniPause.ContentDescription = activity.GetString(snapshot.ShowPause ? Resource.String.stream_pause : Resource.String.stream_resume);
                miniPause.Enabled = snapshot.Phase != PlaybackPhase.Error && Controls != null;
                if (full)
                {
                    dialog.SetTitle(snapshot.Metadata.Title);
                    artist.Text = snapshot.Metadata.Artist;
                    artist.Visibility = string.IsNullOrEmpty(snapshot.Metadata.Artist) ? ViewStates.Gone : ViewStates.Visible;
                    album.Text = snapshot.Metadata.Album;
                    album.Visibility = string.IsNullOrEmpty(snapshot.Metadata.Album) ? ViewStates.Gone : ViewStates.Visible;
                    SetArtwork(artwork, snapshot);
                    if (snapshot.Phase == PlaybackPhase.Error)
                        status.Text = activity.GetString(Resource.String.stream_failed, snapshot.Error);
                    else status.SetText(StatusResource(snapshot.Phase));
                    pause.Enabled = snapshot.Phase != PlaybackPhase.Error && Controls != null;
                    pause.SetText(snapshot.ShowPause ? Resource.String.stream_pause : Resource.String.stream_resume);
                    long seconds = Math.Max(0, snapshot.Position) / 1000;
                    long percent = snapshot.Size > 0 ? Math.Min(100, snapshot.Received * 100 / snapshot.Size) : 0;
                    progress.Text = activity.GetString(Resource.String.stream_progress, new Java.Lang.Object[]
                    { Java.Lang.Long.ValueOf(percent), Java.Lang.Long.ValueOf(seconds / 60), Java.Lang.Long.ValueOf(seconds % 60) });
                }
            }
            // Position refresh exists only while this Activity is visible, never as a background poll.
            if (snapshot != null || pendingRequest != null) handler.PostDelayed(tick, 250);
        }

        private static void SetArtwork(ImageView view, PlaybackSnapshot snapshot)
        {
            if (snapshot.Artwork != null) view.SetImageBitmap(snapshot.Artwork);
            else view.SetImageResource(Resource.Drawable.stream_artwork_placeholder);
        }

        private static int StatusResource(PlaybackPhase phase) => phase switch
        {
            PlaybackPhase.Playing => Resource.String.stream_playing,
            PlaybackPhase.Paused => Resource.String.stream_paused,
            PlaybackPhase.Queued => Resource.String.stream_queued,
            PlaybackPhase.Connecting => Resource.String.stream_connecting,
            PlaybackPhase.Buffering => Resource.String.stream_buffering,
            _ => Resource.String.stream_stopped
        };
    }
}
