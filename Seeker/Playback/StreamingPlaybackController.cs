using Android.OS;
using Bitmap = Android.Graphics.Bitmap;
using Android.Views;
using Android.Widget;
using AndroidX.Fragment.App;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.ExoPlayer.Source;
using AndroidX.Media3.Extractor;
using Common.Streaming;
using Common.Playback;
using Seeker.Services;
using Seeker.Helpers;
using Soulseek;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Directory = System.IO.Directory;
using File = System.IO.File;
using OperationCanceledException = System.OperationCanceledException;

namespace Seeker.Playback
{
    /// <summary>First increment: one MP3, foreground playback, no user seeking or retained downloads.</summary>
    internal sealed class StreamingPlaybackController
    {
        private static StreamingPlaybackController current;
        private static Task previousTransfer = Task.CompletedTask;
        private const long MaximumFileSize = 256L * 1024 * 1024;
        private readonly FragmentActivity activity;
        private readonly string username;
        private readonly FullFileInfo file;
        private readonly Task predecessor;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly Handler handler = new Handler(Looper.MainLooper);
        private readonly Java.Lang.Runnable tick;
        private AndroidX.AppCompat.App.AlertDialog dialog;
        private TextView status;
        private TextView progress;
        private Button pause;
        private TextView artist;
        private TextView album;
        private ImageView artwork;
        private Bitmap artworkBitmap;
        private MetadataListener metadataListener;
        private int metadataRevision;
        public TrackMetadata Metadata { get; private set; }
        private IExoPlayer player;
        private ProgressiveBuffer buffer;
        private ProgressiveDataSource.Factory dataSourceFactory;
        private Task transferTask = Task.CompletedTask;
        private Task cleanupTask = Task.CompletedTask;
        private volatile TransferStates transferState;
        private bool stopped;

        private StreamingPlaybackController(FragmentActivity activity, string username, FullFileInfo file)
        {
            this.activity = activity;
            this.username = username;
            this.file = file;
            Metadata = TrackMetadataFallback.FromPath(file.FullFileName);
            predecessor = previousTransfer;
            tick = new Java.Lang.Runnable(Update);
        }

        public static void Show(FragmentActivity activity, string username, FullFileInfo file)
        {
            activity.RunOnUiThread(() =>
            {
                if (current != null && !current.stopped && current.username == username && current.file.FullFileName == file.FullFileName)
                    return;
                StopCurrent();
                current = new StreamingPlaybackController(activity, username, file);
                current.Start();
            });
        }

        public static void StopCurrent()
        {
            var previous = current;
            current = null;
            previous?.Stop();
            if (previous != null) previousTransfer = previous.cleanupTask;
            previous?.dialog?.Dismiss();
        }

        public static void StopForActivity(Android.App.Activity owner)
        {
            if (ReferenceEquals(current?.activity, owner)) StopCurrent();
        }

        public static void CleanAbandonedCache(string cacheDirectory)
        {
            string directory = Path.Combine(cacheDirectory, "mp3-streams");
            if (!Directory.Exists(directory)) return;
            foreach (string path in Directory.EnumerateFiles(directory, "*.part"))
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private void Start()
        {
            View view = activity.LayoutInflater.Inflate(Resource.Layout.stream_playback, null);
            status = view.FindViewById<TextView>(Resource.Id.streamStatus);
            progress = view.FindViewById<TextView>(Resource.Id.streamProgress);
            pause = view.FindViewById<Button>(Resource.Id.streamPause);
            artist = view.FindViewById<TextView>(Resource.Id.streamArtist);
            album = view.FindViewById<TextView>(Resource.Id.streamAlbum);
            artwork = view.FindViewById<ImageView>(Resource.Id.streamArtwork);
            dialog = new Google.Android.Material.Dialog.MaterialAlertDialogBuilder(activity)
                .SetTitle(Metadata.Title)
                .SetView(view)
                .SetNegativeButton(Resource.String.stream_stop, (_, __) => Stop())
                .Create();
            dialog.DismissEvent += (_, __) => Stop();
            dialog.Show();
            ShowMetadata();
            pause.Click += (_, __) =>
            {
                if (player == null || stopped) return;
                player.PlayWhenReady = !player.PlayWhenReady;
                Update();
            };
            try
            {
                player = new ExoPlayerBuilder(activity.ApplicationContext)
                    .SetLoadControl(new DefaultLoadControl.Builder().SetBufferDurationsMs(15000, 60000, 2000, 4000).Build())
                    .SetHandleAudioBecomingNoisy(true)
                    .Build();
                player.SetAudioAttributes(new AndroidX.Media3.Common.AudioAttributes.Builder()
                    .SetUsage(C.UsageMedia).SetContentType(C.AudioContentTypeMusic).Build(), true);
                metadataListener = new MetadataListener(ReadMetadata);
                player.AddListener(metadataListener);

                // Reuse a completed ordinary download when its URI is still readable.
                var existing = TransferItems.TransferItemManagerDL?.GetTransferItemWithIndexFromAll(file.FullFileName, username, out _);
                string localUri = existing?.State.HasFlag(TransferStates.Succeeded) == true ? existing.FinalUri : null;
                if (!string.IsNullOrEmpty(localUri) && CanRead(localUri))
                {
                    // Use the same bounded ID3 extractor for cached ordinary downloads.
                    var source = new ProgressiveMediaSource.Factory(
                        new AndroidX.Media3.DataSource.DefaultDataSource.Factory(activity.ApplicationContext),
                        new Mp3ExtractorsFactory(disableSeeking: false)).CreateMediaSource(MediaItem.FromUri(localUri));
                    player.SetMediaSource(source);
                }
                else
                {
                    if (file.Size <= 0 || file.Size > MaximumFileSize)
                        throw new InvalidOperationException(activity.GetString(Resource.String.stream_file_limit));
                    string directory = Path.Combine(activity.CacheDir.AbsolutePath, "mp3-streams");
                    Directory.CreateDirectory(directory);
                    buffer = new ProgressiveBuffer(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".part"), file.Size);
                    dataSourceFactory = new ProgressiveDataSource.Factory(buffer);
                    var extractors = new Mp3ExtractorsFactory();
                    var mediaSource = new ProgressiveMediaSource.Factory(dataSourceFactory, extractors)
                        .CreateMediaSource(new MediaItem.Builder().SetUri("seeker-stream:///track.mp3").SetMimeType("audio/mpeg").Build());
                    player.SetMediaSource(mediaSource);
                    transferTask = Task.Run(ReceiveAsync);
                }
                player.Prepare();
                player.PlayWhenReady = true;
                Update();
            }
            catch (Exception exception) { ShowFailure(exception.Message); }
        }

        private bool CanRead(string uri)
        {
            try
            {
                using var stream = activity.ContentResolver.OpenInputStream(Android.Net.Uri.Parse(uri));
                return stream != null;
            }
            catch (Exception) { return false; }
        }

        private async Task ReceiveAsync()
        {
            try
            {
                // Finish cancellation of the preceding transfer before acquiring its peer/path again.
                await predecessor.ConfigureAwait(false);
                await DownloadService.Instance.StreamAsync(username, file, buffer,
                    state => transferState = state, cancellation.Token, retainUntilReleased: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception exception)
            {
                string message = DownloadFailureClassifier.GetCause(exception)?.Message ?? exception.Message;
                activity.RunOnUiThread(() => { if (!stopped) ShowFailure(message); });
            }
        }

        private void Update()
        {
            handler.RemoveCallbacks(tick);
            if (stopped || player == null) return;
            // DisableSeeking intentionally leaves streamed duration unknown in many MP3s.
            // Keep it absent unless the player actually reports a positive duration.
            long duration = player.Duration;
            if (duration > 0 && duration != Metadata.DurationMilliseconds)
                Metadata = Metadata.WithEmbedded(new TrackMetadataValues { DurationMilliseconds = duration });
            if (player.PlayerError != null) { ShowFailure(player.PlayerError.Message); return; }
            if (player.PlaybackState == 4) // Media3 Player.STATE_ENDED
            {
                status.SetText(Resource.String.stream_finished);
                Stop();
                return;
            }
            int statusResource = !player.PlayWhenReady ? Resource.String.stream_paused
                : player.IsPlaying ? Resource.String.stream_playing
                : buffer?.AvailableLength == 0 && transferState.HasFlag(TransferStates.Queued) ? Resource.String.stream_queued
                : buffer?.AvailableLength == 0 && transferState == TransferStates.None ? Resource.String.stream_connecting
                : Resource.String.stream_buffering;
            status.SetText(statusResource);
            pause.Enabled = true;
            pause.SetText(player.PlayWhenReady ? Resource.String.stream_pause : Resource.String.stream_resume);
            long seconds = Math.Max(0, player.CurrentPosition) / 1000;
            long percent = buffer == null ? 100 : buffer.AvailableLength * 100 / file.Size;
            progress.Text = activity.GetString(Resource.String.stream_progress, new Java.Lang.Object[]
                { Java.Lang.Long.ValueOf(percent), Java.Lang.Long.ValueOf(seconds / 60), Java.Lang.Long.ValueOf(seconds % 60) });
            handler.PostDelayed(tick, 250);
        }

        private void ShowFailure(string message)
        {
            status.Text = activity.GetString(Resource.String.stream_failed, message);
            Stop();
        }

        private void Stop()
        {
            if (stopped) return;
            stopped = true;
            Interlocked.Increment(ref metadataRevision);
            handler.RemoveCallbacks(tick);
            cancellation.Cancel();
            dataSourceFactory?.CancelReads();
            buffer?.Fail(new OperationCanceledException());
            // Wake the loader before Release waits for it, so Stop never waits for a peer.
            try
            {
                if (metadataListener != null) player?.RemoveListener(metadataListener);
                player?.Release();
            }
            catch (Exception exception) { Logger.Debug("Stream player release: " + exception.Message); }
            player?.Dispose();
            player = null;
            metadataListener?.Dispose();
            metadataListener = null;
            artwork.SetImageResource(Resource.Drawable.stream_artwork_placeholder);
            artworkBitmap?.Dispose();
            artworkBitmap = null;
            dataSourceFactory?.Dispose();
            pause.Enabled = false;
            var ownedBuffer = buffer;
            cleanupTask = transferTask.ContinueWith(_ =>
            {
                DownloadService.Instance.ReleaseStream(username, file.FullFileName, ownedBuffer);
                try { ownedBuffer?.Dispose(); }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                { Logger.Debug("Stream cache cleanup: " + exception.Message); }
                cancellation.Dispose();
            }, TaskScheduler.Default);
            if (ReferenceEquals(current, this))
            {
                current = null;
                previousTransfer = cleanupTask;
            }
        }

        private void ShowMetadata()
        {
            dialog.SetTitle(Metadata.Title);
            artist.Text = Metadata.Artist;
            artist.Visibility = string.IsNullOrEmpty(Metadata.Artist) ? ViewStates.Gone : ViewStates.Visible;
            album.Text = Metadata.Album;
            album.Visibility = string.IsNullOrEmpty(Metadata.Album) ? ViewStates.Gone : ViewStates.Visible;
        }

        private void ReadMetadata(MediaMetadata mediaMetadata)
        {
            if (stopped || !ReferenceEquals(current, this)) return;
            int revision = Interlocked.Increment(ref metadataRevision);
            Task.Run(() =>
            {
                Bitmap bitmap = null;
                try
                {
                    if (Volatile.Read(ref metadataRevision) != revision) return;
                    var values = StreamingMetadata.Read(mediaMetadata);
                    if (Volatile.Read(ref metadataRevision) != revision) return;
                    bitmap = StreamingMetadata.DecodeArtwork(values.ArtworkData);
                    activity.RunOnUiThread(() =>
                    {
                        // Both the owner and revision must still match after asynchronous decoding.
                        if (stopped || !ReferenceEquals(current, this) || revision != metadataRevision)
                        {
                            bitmap?.Dispose();
                            return;
                        }
                        Metadata = Metadata.WithEmbedded(values);
                        ShowMetadata();
                        if (bitmap != null)
                        {
                            artwork.SetImageBitmap(bitmap);
                            artworkBitmap?.Dispose();
                            artworkBitmap = bitmap;
                        }
                    });
                }
                catch (Exception exception)
                {
                    bitmap?.Dispose();
                    Logger.Debug("Stream metadata ignored: " + exception.Message);
                }
            });
        }

        private sealed class MetadataListener : Java.Lang.Object, IPlayerListener
        {
            private readonly Action<MediaMetadata> changed;
            public MetadataListener(Action<MediaMetadata> changed) { this.changed = changed; }
            public void OnMediaMetadataChanged(MediaMetadata metadata) => changed(metadata);
        }

        private sealed class Mp3ExtractorsFactory : Java.Lang.Object, IExtractorsFactory
        {
            private readonly bool disableSeeking;
            public Mp3ExtractorsFactory(bool disableSeeking = true) { this.disableSeeking = disableSeeking; }

            public IExtractor[] CreateExtractors()
            {
                return new IExtractor[] { new BoundedMp3Extractor(disableSeeking) };
            }

            public IExtractor[] CreateExtractors(Android.Net.Uri uri,
                System.Collections.Generic.IDictionary<string, System.Collections.Generic.IList<string>> responseHeaders) => CreateExtractors();
        }
    }
}
