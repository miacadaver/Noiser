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
using Seeker.Services;
using Soulseek;
using System;
using System.Collections.Generic;
using System.Linq;

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
        private AndroidX.AppCompat.App.AlertDialog queueDialog;
        private AndroidX.AppCompat.App.AlertDialog savedQueueDialog;
        private ListView queueList;
        private QueueAdapter queueAdapter;
        private string queueSignature;
        private ImageView artwork;
        private TextView artist;
        private TextView album;
        private TextView status;
        private TextView progress;
        private TextView seekStatus;
        private SeekBar seekBar;
        private ImageButton pause;
        private ImageButton previous;
        private ImageButton next;
        private ImageButton openQueue;
        private ImageButton repeat;
        private ImageButton autoplay;
        private bool attached;
        private string pendingRequest;
        private bool trackingSeekBar;
        private bool staleSeekGesture;
        private string trackingTrackId;
        private SeekRangeSnapshot trackingSeekRange;

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

        public static void ShowQueue(FragmentActivity activity,
            IEnumerable<PlaybackQueueEntry> entries, string selectedEntryId)
        {
            activity.RunOnUiThread(() =>
            {
                string request = StreamingPlaybackService.RequestPlayQueue(activity, entries, selectedEntryId);
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

        public static void AddToQueue(FragmentActivity activity, string username, FullFileInfo file, bool playNext)
        {
            activity.RunOnUiThread(() =>
            {
                var entry = ToQueueEntry(username, file);
                if (StreamingPlaybackService.Current?.Playback == null && PlaybackRecovery.Append(activity, entry, playNext)) return;
                StreamingPlaybackService.RequestAppend(activity, entry, playNext);
            });
        }

        private static PlaybackQueueEntry ToQueueEntry(string username, FullFileInfo file) =>
            new PlaybackQueueEntry(username, file.FullFileName, file.Size,
                wasFilenameLatin1Decoded: file.wasFilenameLatin1Decoded,
                wasFolderLatin1Decoded: file.wasFolderLatin1Decoded);

        public static void CleanAbandonedCache(string cacheDirectory) => StreamingPlayback.CleanAbandonedCache(cacheDirectory);

        internal void Attach()
        {
            if (attached) return;
            attached = true;
            StreamingPlaybackService.Changed += Update;
            PlaybackRecovery.Changed += RecoveryChanged;
            Update();
        }

        internal void Detach()
        {
            if (!attached) return;
            attached = false;
            StreamingPlaybackService.Changed -= Update;
            PlaybackRecovery.Changed -= RecoveryChanged;
            handler.RemoveCallbacks(tick);
            dialog?.Dismiss();
            dialog = null;
            queueDialog?.Dismiss();
            queueDialog = null;
            savedQueueDialog?.Dismiss();
            savedQueueDialog = null;
            queueList = null;
            queueAdapter = null;
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
            if (snapshot == null && PlaybackRecovery.Saved != null) { ResumeSavedQueue(); return; }
            if (snapshot == null || snapshot.Phase == PlaybackPhase.Error) return;
            if (snapshot.ShowPause) Controls?.Pause();
            else Controls?.Play();
        }

        private void Stop()
        {
            StreamingPlaybackService.CancelPending(pendingRequest);
            pendingRequest = null;
            Controls?.Stop();
            if (StreamingPlaybackService.Current?.Playback == null) PlaybackRecovery.Forget();
            Update();
        }

        internal void ShowCurrent()
        {
            if (!attached || activity.IsFinishing || activity.IsDestroyed) return;
            if (StreamingPlaybackService.Current?.Playback == null && pendingRequest == null && PlaybackRecovery.Saved != null)
            { ShowSavedQueue(); return; }
            if (dialog?.IsShowing == true) return;
            View view = activity.LayoutInflater.Inflate(Resource.Layout.stream_playback, null);
            artwork = view.FindViewById<ImageView>(Resource.Id.streamArtwork);
            artist = view.FindViewById<TextView>(Resource.Id.streamArtist);
            album = view.FindViewById<TextView>(Resource.Id.streamAlbum);
            status = view.FindViewById<TextView>(Resource.Id.streamStatus);
            progress = view.FindViewById<TextView>(Resource.Id.streamProgress);
            seekStatus = view.FindViewById<TextView>(Resource.Id.streamSeekStatus);
            seekBar = view.FindViewById<SeekBar>(Resource.Id.streamSeekBar);
            seekBar.ProgressChanged += (_, args) =>
            {
                if (args.FromUser) HandleUserSeekProgress(args.Progress);
            };
            seekBar.StartTrackingTouch += (_, __) =>
            {
                staleSeekGesture = false;
                var current = StreamingPlaybackService.Current?.Snapshot();
                trackingSeekBar = current?.SeekRange.CanSeek == true;
                trackingTrackId = current?.TrackId;
                trackingSeekRange = current?.SeekRange ?? default;
            };
            seekBar.StopTrackingTouch += (_, __) =>
            {
                var current = StreamingPlaybackService.Current?.Snapshot();
                if (!staleSeekGesture && trackingSeekBar && current?.TrackId == trackingTrackId)
                {
                    long targetUs = trackingSeekRange.SliderPosition(seekBar.Progress, seekBar.Max);
                    if (trackingSeekRange.Contains(targetUs)) Controls?.SeekTo(targetUs / 1000);
                }
                trackingSeekBar = false;
                staleSeekGesture = false;
                trackingTrackId = null;
                Update();
            };
            pause = view.FindViewById<ImageButton>(Resource.Id.streamPause);
            pause.Click += (_, __) => TogglePause();
            previous = view.FindViewById<ImageButton>(Resource.Id.streamPrevious);
            previous.Click += (_, __) => Controls?.SeekToPreviousMediaItem();
            next = view.FindViewById<ImageButton>(Resource.Id.streamNext);
            next.Click += (_, __) => Controls?.SeekToNextMediaItem();
            openQueue = view.FindViewById<ImageButton>(Resource.Id.streamQueue);
            openQueue.Click += (_, __) => ShowQueueDialog();
            repeat = view.FindViewById<ImageButton>(Resource.Id.streamRepeat);
            repeat.Click += (_, __) => StreamingPlaybackService.Current?.CycleRepeatMode();
            autoplay = view.FindViewById<ImageButton>(Resource.Id.streamAutoplay);
            autoplay.Click += (_, __) => StreamingPlaybackService.Current?.ToggleAutoplay();
            var window = new Google.Android.Material.Dialog.MaterialAlertDialogBuilder(activity)
                .SetTitle(Resource.String.stream_player)
                .SetView(view).Create();
            var stop = view.FindViewById<ImageButton>(Resource.Id.streamStop);
            var close = view.FindViewById<ImageButton>(Resource.Id.streamClose);
            stop.Click += (_, __) => { Stop(); window.Dismiss(); };
            close.Click += (_, __) => window.Dismiss();
            foreach (var button in new[] { previous, pause, next, openQueue, repeat, autoplay, stop, close })
                AndroidX.AppCompat.Widget.TooltipCompat.SetTooltipText(button, button.ContentDescription);
            dialog = window;
            var windowArtwork = artwork;
            window.DismissEvent += (_, __) =>
            {
                // Closing/minimizing the UI never controls playback lifetime.
                windowArtwork.SetImageResource(Resource.Drawable.stream_artwork_placeholder);
                if (!ReferenceEquals(dialog, window)) return;
                dialog = null;
                pendingRequest = null;
                trackingSeekBar = false;
                staleSeekGesture = false;
                trackingTrackId = null;
                Update();
            };
            dialog.Show();
            if (StreamingPlaybackService.Current?.Playback != null || pendingRequest != null) Connect();
            Update();
        }

        private void SetPlayerIcon(ImageButton button, int drawable, int label, bool selected = false)
        {
            string description = activity.GetString(label);
            if (button.ContentDescription != description)
            {
                button.SetImageResource(drawable);
                button.ContentDescription = description;
                AndroidX.AppCompat.Widget.TooltipCompat.SetTooltipText(button, description);
            }
            button.Selected = selected;
        }

        private void Update()
        {
            handler.RemoveCallbacks(tick);
            if (!attached || activity.IsFinishing || activity.IsDestroyed) return;
            var service = StreamingPlaybackService.Current;
            if (service?.LastHandledRequestId == pendingRequest) pendingRequest = null;
            var snapshot = pendingRequest == null ? service?.Snapshot() : null;
            if (snapshot != null || PlaybackRecovery.Saved == null)
            { savedQueueDialog?.Dismiss(); savedQueueDialog = null; }
            if (controller != null && !controller.IsConnected)
            {
                MediaController.ReleaseFuture(controllerFuture);
                controllerFuture = null;
                controller = null;
            }
            if (snapshot != null) Connect();
            bool full = dialog?.IsShowing == true;
            UpdateQueueDialog(service);
            if (snapshot == null && pendingRequest == null && controllerFuture != null)
            {
                MediaController.ReleaseFuture(controllerFuture);
                controllerFuture = null;
                controller = null;
            }
            mini.Visibility = snapshot != null && !full ? ViewStates.Visible : ViewStates.Gone;
            if (snapshot == null && pendingRequest == null && PlaybackRecovery.Saved is SavedPlaybackSession saved)
            {
                // An offline preview is not a player/session. No Connect(), timer, service,
                // peer request, wake lock, or metadata publication is needed to show it.
                var current = saved.Current.ToQueueEntry();
                mini.Visibility = ViewStates.Visible;
                miniArtwork.SetImageResource(Resource.Drawable.stream_artwork_placeholder);
                miniTitle.Text = current.Metadata.Title;
                miniArtist.SetText(Resource.String.stream_saved_paused);
                miniArtist.Visibility = ViewStates.Visible;
                miniPause.SetImageResource(Resource.Drawable.stream_play);
                miniPause.ContentDescription = activity.GetString(Resource.String.stream_play);
                miniPause.Enabled = true;
            }
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
                    previous.Enabled = next.Enabled = openQueue.Enabled = false;
                    repeat.Enabled = autoplay.Enabled = false;
                    seekBar.Enabled = false;
                    seekBar.Progress = 0;
                    seekBar.SecondaryProgress = 0;
                    seekStatus.Text = string.Empty;
                    trackingSeekBar = false;
                    staleSeekGesture = false;
                    trackingTrackId = null;
                }
            }
            else
            {
                string title = snapshot.Metadata.IsTagReadComplete
                    ? snapshot.Metadata.Title : activity.GetString(Resource.String.stream_player);
                string trackArtist = snapshot.Metadata.IsTagReadComplete ? snapshot.Metadata.Artist : null;
                miniTitle.Text = title;
                miniArtist.Text = trackArtist;
                miniArtist.Visibility = string.IsNullOrEmpty(trackArtist) ? ViewStates.Gone : ViewStates.Visible;
                SetArtwork(miniArtwork, snapshot);
                miniPause.SetImageResource(snapshot.ShowPause ? Resource.Drawable.stream_pause : Resource.Drawable.stream_play);
                miniPause.ContentDescription = activity.GetString(snapshot.ShowPause ? Resource.String.stream_pause : Resource.String.stream_resume);
                miniPause.Enabled = snapshot.Phase != PlaybackPhase.Error && Controls != null;
                if (full)
                {
                    dialog.SetTitle(title);
                    artist.Text = trackArtist;
                    artist.Visibility = string.IsNullOrEmpty(trackArtist) ? ViewStates.Gone : ViewStates.Visible;
                    album.Text = snapshot.Metadata.EmbeddedAlbum;
                    album.Visibility = string.IsNullOrEmpty(snapshot.Metadata.EmbeddedAlbum) ? ViewStates.Gone : ViewStates.Visible;
                    SetArtwork(artwork, snapshot);
                    if (snapshot.Phase == PlaybackPhase.Error)
                        status.Text = activity.GetString(Resource.String.stream_failed, snapshot.Error);
                    else status.SetText(StatusResource(snapshot.Phase));
                    pause.Enabled = snapshot.Phase != PlaybackPhase.Error && Controls != null;
                    previous.Enabled = service?.CanSkipPrevious == true && Controls != null;
                    next.Enabled = service?.CanSkipNext == true && Controls != null;
                    openQueue.Enabled = service?.Queue.CurrentIndex >= 0;
                    repeat.Enabled = autoplay.Enabled = Controls != null;
                    SetPlayerIcon(repeat, snapshot.RepeatMode switch
                    {
                        PlaybackRepeatMode.All => Resource.Drawable.stream_repeat,
                        PlaybackRepeatMode.One => Resource.Drawable.stream_repeat_one,
                        _ => Resource.Drawable.stream_repeat_off
                    }, snapshot.RepeatMode switch
                    {
                        PlaybackRepeatMode.All => Resource.String.stream_repeat_all,
                        PlaybackRepeatMode.One => Resource.String.stream_repeat_one,
                        _ => Resource.String.stream_repeat_off
                    }, snapshot.RepeatMode != PlaybackRepeatMode.Off);
                    SetPlayerIcon(autoplay, snapshot.Autoplay ? Resource.Drawable.stream_autoplay : Resource.Drawable.stream_autoplay_off,
                        snapshot.Autoplay ? Resource.String.stream_autoplay_on : Resource.String.stream_autoplay_off, snapshot.Autoplay);
                    SetPlayerIcon(pause, snapshot.ShowPause ? Resource.Drawable.stream_pause : Resource.Drawable.stream_play,
                        snapshot.ShowPause ? Resource.String.stream_pause : Resource.String.stream_resume);
                    long seconds = Math.Max(0, snapshot.Position) / 1000;
                    long percent = snapshot.Size > 0 ? Math.Min(100, snapshot.Received * 100 / snapshot.Size) : 0;
                    progress.Text = activity.GetString(Resource.String.stream_progress, new Java.Lang.Object[]
                    { Java.Lang.Long.ValueOf(percent) });
                    if (trackingSeekBar && trackingTrackId != snapshot.TrackId)
                    {
                        trackingSeekBar = false;
                        staleSeekGesture = true;
                    }
                    seekBar.Enabled = snapshot.Phase != PlaybackPhase.Error
                        && snapshot.SeekRange.CanSeek && Controls != null;
                    seekBar.SecondaryProgress = snapshot.SeekRange.SeekableProgress(seekBar.Max);
                    if (!trackingSeekBar)
                    {
                        seekBar.Progress = snapshot.SeekRange.SliderProgress(Math.Max(0, snapshot.Position) * 1000, seekBar.Max);
                        UpdateSeekStatus(Math.Max(0, snapshot.Position) * 1000, snapshot.SeekRange);
                    }
                }
            }
            if (full)
                foreach (var button in new[] { previous, pause, next, openQueue, repeat, autoplay })
                    button.Alpha = button.Enabled ? 1f : 0.38f;
            // Position refresh exists only while this Activity is visible, never as a background poll.
            if (snapshot != null || pendingRequest != null) handler.PostDelayed(tick, 250);
        }

        private void ShowQueueDialog()
        {
            var service = StreamingPlaybackService.Current;
            if (!attached || service == null || service.Queue.CurrentIndex < 0) return;
            queueRows = service.Queue.Upcoming(service.RepeatMode).ToArray();
            queueSignature = null;
            queueAdapter = new QueueAdapter(activity, queueRows, service.Queue.Current?.Id);
            queueList = new ListView(activity) { Adapter = queueAdapter };
            queueList.ItemClick += (_, args) =>
            {
                // Use the stable ID from the row snapshot, never an index into a changed queue.
                if (args.Position < 0 || args.Position >= queueRows.Length) return;
                StreamingPlaybackService.Current?.SelectQueueEntry(queueRows[args.Position].Id);
                queueDialog?.Dismiss();
            };
            queueList.ItemLongClick += (_, args) =>
            {
                if (args.Position < 0 || args.Position >= queueRows.Length) return;
                var id = queueRows[args.Position].Id;
                var options = new[] { activity.GetString(Resource.String.stream_play), activity.GetString(Resource.String.stream_remove) };
                new Google.Android.Material.Dialog.MaterialAlertDialogBuilder(activity)
                    .SetItems(options, (_, choice) =>
                    {
                        var current = StreamingPlaybackService.Current;
                        if (choice.Which == 0) current?.SelectQueueEntry(id);
                        else current?.RemoveQueueEntry(id);
                    }).Show();
            };
            queueDialog = new Google.Android.Material.Dialog.MaterialAlertDialogBuilder(activity)
                .SetTitle(Resource.String.stream_queue).SetView(queueList)
                .SetNeutralButton(Resource.String.stream_queue_clear, (_, __) =>
                {
                    StreamingPlaybackService.Current?.ClearQueue();
                    queueDialog?.Dismiss();
                })
                .SetNegativeButton(Resource.String.close, (_, __) => { }).Show();
            queueDialog.DismissEvent += (_, __) => { queueDialog = null; queueList = null; queueAdapter = null; queueRows = Array.Empty<PlaybackQueueEntry>(); queueSignature = null; };
            UpdateQueueDialog(service);
        }

        internal void RefreshRecoveredQueue() => Update();

        private void RecoveryChanged() => activity.RunOnUiThread(Update);

        private void ResumeSavedQueue()
        {
            var saved = PlaybackRecovery.Saved;
            if (saved == null || !attached) return;
            if (!Common.PreferencesState.CurrentlyLoggedIn)
            {
                SeekerApplication.Toaster.ShowToast(activity.GetString(Resource.String.must_be_logged_to_browse), ToastLength.Short);
                return;
            }
            string selectedId = saved.CurrentId;
            SessionService.Instance.RunWithReconnect(() =>
            {
                var latest = PlaybackRecovery.Saved;
                if (!attached || activity.IsFinishing || activity.IsDestroyed || latest == null || latest.CurrentId != selectedId) return;
                savedQueueDialog?.Dismiss();
                savedQueueDialog = null;
                ShowQueue(activity, latest.ToQueue().Entries, latest.CurrentId);
            });
        }

        private void ShowSavedQueue()
        {
            if (savedQueueDialog?.IsShowing == true) return;
            var saved = PlaybackRecovery.Saved;
            if (saved == null) return;
            var rows = saved.ToQueue().Entries.ToArray();
            string[] labels = rows.Select(entry => entry.Metadata.Title
                + (string.IsNullOrEmpty(entry.Metadata.Artist) ? "" : " · " + entry.Metadata.Artist)).ToArray();
            savedQueueDialog = new Google.Android.Material.Dialog.MaterialAlertDialogBuilder(activity)
                .SetTitle(Resource.String.stream_saved_queue)
                .SetSingleChoiceItems(labels, Array.FindIndex(rows, entry => entry.Id == saved.CurrentId), (_, args) =>
                {
                    if (args.Which >= 0 && args.Which < rows.Length) PlaybackRecovery.Select(activity, rows[args.Which].Id);
                })
                .SetPositiveButton(Resource.String.stream_play, (_, __) => ResumeSavedQueue())
                .SetNeutralButton(Resource.String.stream_queue_clear, (_, __) => PlaybackRecovery.Forget())
                .SetNegativeButton(Resource.String.close, (_, __) => { }).Create();
            var window = savedQueueDialog;
            window.DismissEvent += (_, __) => { if (ReferenceEquals(savedQueueDialog, window)) savedQueueDialog = null; };
            window.Show();
        }

        private PlaybackQueueEntry[] queueRows = Array.Empty<PlaybackQueueEntry>();

        private void UpdateQueueDialog(StreamingPlaybackService service)
        {
            if (queueDialog?.IsShowing != true || queueList == null || activity.IsFinishing || activity.IsDestroyed) return;
            var rows = service == null ? Array.Empty<PlaybackQueueEntry>()
                : service.Queue.Upcoming(service.RepeatMode).ToArray();
            string currentId = service?.Queue.Current?.Id;
            string signature = string.Join("\u001f", rows.Select(entry => string.Join("\u001e",
                new[] { entry.Id, entry.Metadata.Title, entry.Metadata.Artist, entry.State.ToString(), entry.Error ?? string.Empty })))
                + "\u001d" + currentId;
            if (signature == queueSignature) return;
            queueSignature = signature;
            queueRows = rows;
            queueAdapter = new QueueAdapter(activity, queueRows, currentId);
            queueList.Adapter = queueAdapter;
        }

        private sealed class QueueAdapter : ArrayAdapter<PlaybackQueueEntry>
        {
            private readonly string currentId;
            public QueueAdapter(Context context, PlaybackQueueEntry[] entries, string currentId)
                : base(context, Android.Resource.Layout.SimpleListItem1, entries) { this.currentId = currentId; }
            public override View GetView(int position, View convertView, ViewGroup parent)
            {
                var view = (TextView)base.GetView(position, convertView, parent);
                var entry = GetItem(position);
                string prefix = entry.Id == currentId ? "▶  " : "";
                string artist = string.IsNullOrWhiteSpace(entry.Metadata.Artist) ? "" : " · " + entry.Metadata.Artist;
                string error = entry.State == PlaybackQueueEntryState.Failed && !string.IsNullOrWhiteSpace(entry.Error)
                    ? "\n" + Context.GetString(Resource.String.stream_failed, entry.Error) : "";
                view.Text = prefix + entry.Metadata.Title + artist + error;
                view.SetPadding(24, 16, 24, 16);
                return view;
            }
        }

        private void HandleUserSeekProgress(int progressValue)
        {
            var current = StreamingPlaybackService.Current?.Snapshot();
            if (current == null || current.Phase == PlaybackPhase.Error || !current.SeekRange.CanSeek) return;
            if (staleSeekGesture)
            {
                seekBar.Progress = current.SeekRange.SliderProgress(Math.Max(0, current.Position) * 1000, seekBar.Max);
                UpdateSeekStatus(Math.Max(0, current.Position) * 1000, current.SeekRange);
                return;
            }
            if (trackingSeekBar && current.TrackId != trackingTrackId)
            {
                trackingSeekBar = false;
                staleSeekGesture = true;
                seekBar.Progress = current.SeekRange.SliderProgress(Math.Max(0, current.Position) * 1000, seekBar.Max);
                UpdateSeekStatus(Math.Max(0, current.Position) * 1000, current.SeekRange);
                return;
            }
            trackingSeekRange = current.SeekRange;
            trackingTrackId = current.TrackId;
            long requested = trackingSeekRange.SliderPosition(progressValue, seekBar.Max);
            long bounded = Math.Min(requested, trackingSeekRange.SeekablePositionUs);
            if (bounded != requested)
            {
                seekBar.Progress = trackingSeekRange.SliderProgress(bounded, seekBar.Max);
            }
            UpdateSeekStatus(bounded, trackingSeekRange);

            // Keyboard and accessibility changes may not produce touch tracking callbacks.
            // Commit them immediately, through the same MediaSession validator.
            if (!trackingSeekBar && Controls != null && trackingSeekRange.Contains(bounded))
                Controls.SeekTo(bounded / 1000);
        }

        private static void SetArtwork(ImageView view, PlaybackSnapshot snapshot)
        {
            if (snapshot.Artwork != null) view.SetImageBitmap(snapshot.Artwork);
            else view.SetImageResource(Resource.Drawable.stream_artwork_placeholder);
        }

        private void UpdateSeekStatus(long positionUs, SeekRangeSnapshot range)
        {
            if (seekStatus == null) return;
            long positionSeconds = Math.Max(0, positionUs) / 1_000_000;
            string position = FormatTime(positionSeconds);
            seekStatus.Text = range.HasKnownDuration
                ? activity.GetString(Resource.String.stream_seek_position,
                    position, FormatTime(range.DurationUs / 1_000_000))
                : activity.GetString(Resource.String.stream_seek_unknown,
                    position, FormatTime(range.SeekablePositionUs / 1_000_000));
        }

        private static string FormatTime(long seconds) => string.Format(System.Globalization.CultureInfo.InvariantCulture,
            "{0}:{1:00}", seconds / 60, seconds % 60);

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
