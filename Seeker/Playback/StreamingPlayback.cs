using Android.Content;
using Android.Graphics;
using Android.Runtime;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.ExoPlayer.Source;
using Common.Playback;
using Common.Streaming;
using Seeker.Helpers;
using Seeker.Services;
using Soulseek;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using File = System.IO.File;
using Directory = System.IO.Directory;
using Path = System.IO.Path;

namespace Seeker.Playback
{
    /// <summary>The existing single-file streaming path, now owned by the playback service.</summary>
    internal sealed class StreamingPlayback
    {
        private const long MaximumFileSize = 256L * 1024 * 1024;
        private readonly StreamingPlaybackService owner;
        private readonly IExoPlayer player;
        private readonly Task predecessor;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private ProgressiveBuffer buffer;
        private ProgressiveDataSource.Factory dataSourceFactory;
        private Task transferTask = Task.CompletedTask;
        private bool resourcesClosed;
        private bool stopped;
        private int metadataRevision;
        private AndroidX.Media3.Common.Metadata lastId3;
        private volatile TransferStates transferState;
        private volatile bool receiving;

        public string Id { get; }
        public string Username { get; }
        public FullFileInfo FileInfo { get; }
        public TrackMetadata Metadata { get; private set; }
        public MediaMetadata SessionMetadata { get; private set; }
        public Bitmap Artwork { get; private set; }
        public byte[] SessionArtwork { get; private set; }
        public PlaybackException Failure { get; private set; }
        public Task Cleanup { get; private set; } = Task.CompletedTask;
        public bool IsStopped => stopped;
        public long Received => buffer?.AvailableLength ?? FileInfo.Size;
        public bool HasBytes => buffer == null || buffer.AvailableLength > 0;
        public bool IsQueued => transferState.HasFlag(TransferStates.Queued);
        public bool IsReceiving => receiving;

        public StreamingPlayback(StreamingPlaybackService owner, IExoPlayer player, string id,
            string username, FullFileInfo file, Task predecessor)
        {
            this.owner = owner;
            this.player = player;
            this.predecessor = predecessor;
            Id = id;
            Username = username;
            FileInfo = file;
            Metadata = TrackMetadataFallback.FromPath(file.FullFileName);
            SessionMetadata = StreamingMetadata.BuildSessionMetadata(Metadata, null);
        }

        public void Start()
        {
            try
            {
                var existing = TransferItems.TransferItemManagerDL?.GetTransferItemWithIndexFromAll(FileInfo.FullFileName, Username, out _);
                string localUri = existing?.State.HasFlag(TransferStates.Succeeded) == true ? existing.FinalUri : null;
                if (!string.IsNullOrEmpty(localUri) && CanRead(localUri))
                {
                    var source = new ProgressiveMediaSource.Factory(
                        new AndroidX.Media3.DataSource.DefaultDataSource.Factory(owner.ApplicationContext),
                        new Mp3ExtractorsFactory(disableSeeking: false))
                        .CreateMediaSource(new MediaItem.Builder().SetUri(localUri).SetMediaId(Id).Build());
                    player.SetMediaSource(source);
                }
                else
                {
                    if (FileInfo.Size <= 0 || FileInfo.Size > MaximumFileSize)
                        throw new InvalidOperationException(owner.GetString(Resource.String.stream_file_limit));
                    string directory = Path.Combine(owner.CacheDir.AbsolutePath, "mp3-streams");
                    Directory.CreateDirectory(directory);
                    buffer = new ProgressiveBuffer(Path.Combine(directory, Guid.NewGuid().ToString("N") + ".part"), FileInfo.Size);
                    dataSourceFactory = new ProgressiveDataSource.Factory(buffer);
                    var source = new ProgressiveMediaSource.Factory(dataSourceFactory, new Mp3ExtractorsFactory())
                        .CreateMediaSource(new MediaItem.Builder().SetUri("seeker-stream:///track.mp3")
                            .SetMediaId(Id).SetMimeType("audio/mpeg").Build());
                    player.SetMediaSource(source);
                    receiving = true;
                    transferTask = Task.Run(ReceiveAsync);
                }
                player.Prepare();
                // The session may still cache the previous (or empty) track's commands.
                // Expose the new track before Play checks whether transport is available.
                owner.SessionPlayer.Refresh();
                owner.SessionPlayer.Play();
            }
            catch (Exception exception) { Fail(exception.Message); }
        }

        private bool CanRead(string uri)
        {
            try
            {
                using var stream = owner.ContentResolver.OpenInputStream(Android.Net.Uri.Parse(uri));
                return stream != null;
            }
            catch (Exception) { return false; }
        }

        private async Task ReceiveAsync()
        {
            try
            {
                await predecessor.ConfigureAwait(false);
                await DownloadService.Instance.StreamAsync(Username, FileInfo, buffer,
                    state =>
                    {
                        transferState = state;
                        owner.RunOnMain(() => { if (owner.IsCurrent(this)) owner.Publish(); });
                    }, cancellation.Token, retainUntilReleased: true).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception exception)
            {
                string message = DownloadFailureClassifier.GetCause(exception)?.Message ?? exception.Message;
                owner.RunOnMain(() => { if (owner.IsCurrent(this)) Fail(message); });
            }
            finally
            {
                receiving = false;
                owner.RunOnMain(() => { if (owner.IsCurrent(this)) owner.Publish(); });
            }
        }

        public void ReadTracks(Tracks tracks)
        {
            // Decoder metadata and binding collections are optional; none may fail playback.
            try { ReadTracksCore(tracks); }
            catch (Exception exception) { Logger.Debug("Stream metadata ignored: " + exception.Message); }
        }

        private void ReadTracksCore(Tracks tracks)
        {
            if (stopped || resourcesClosed || !owner.IsCurrent(this)) return;
            if (player.CurrentMediaItem?.MediaId != Id || !JNIEnv.IsSameObject(tracks.Handle, player.CurrentTracks.Handle)) return;
            AndroidX.Media3.Common.Metadata id3 = null;
            foreach (Java.Lang.Object entry in tracks.Groups)
            {
                var group = entry.JavaCast<Tracks.Group>();
                if (!group.IsSelected || group.Type != C.TrackTypeAudio) continue;
                for (int i = 0; i < group.Length; i++)
                    if (group.IsTrackSelected(i)) { id3 = group.GetTrackFormat(i).Metadata; break; }
                if (id3 != null) break;
            }
            if (id3 == null || (lastId3 != null && JNIEnv.IsSameObject(lastId3.Handle, id3.Handle))) return;
            lastId3 = id3;
            int revision = Interlocked.Increment(ref metadataRevision);
            Task.Run(() =>
            {
                Bitmap bitmap = null;
                try
                {
                    if (Volatile.Read(ref metadataRevision) != revision) return;
                    // Read original extractor tags, independent of the bounded session presentation.
                    using var decoded = new MediaMetadata.Builder().PopulateFromMetadata(id3).Build();
                    var values = StreamingMetadata.Read(decoded);
                    if (Volatile.Read(ref metadataRevision) != revision) return;
                    bitmap = StreamingMetadata.DecodeArtwork(values.ArtworkData);
                    byte[] thumbnail = StreamingMetadata.EncodeSessionArtwork(bitmap);
                    owner.RunOnMain(() =>
                    {
                        if (stopped || resourcesClosed || !owner.IsCurrent(this) || revision != metadataRevision)
                        { bitmap?.Dispose(); return; }
                        Metadata = Metadata.WithEmbedded(values);
                        var previousBitmap = Artwork;
                        if (bitmap != null) Artwork = bitmap;
                        if (thumbnail != null) SessionArtwork = thumbnail;
                        SessionMetadata = StreamingMetadata.BuildSessionMetadata(Metadata, SessionArtwork);
                        owner.Publish();
                        if (bitmap != null) previousBitmap?.Dispose();
                    });
                }
                catch (Exception exception)
                {
                    bitmap?.Dispose();
                    Logger.Debug("Stream metadata ignored: " + exception.Message);
                }
            });
        }

        public void UpdateDuration()
        {
            long duration = player.Duration;
            if (duration <= 0 || duration == Metadata.DurationMilliseconds) return;
            Metadata = Metadata.WithEmbedded(new TrackMetadataValues { DurationMilliseconds = duration });
            SessionMetadata = StreamingMetadata.BuildSessionMetadata(Metadata, SessionArtwork);
        }

        public void Fail(string message, PlaybackException nativeError = null)
        {
            if (stopped || Failure != null) return;
            Failure = nativeError ?? new PlaybackException(message, null, PlaybackException.ErrorCodeIoUnspecified);
            BeginCleanup();
            owner.Publish();
        }

        public Task Stop()
        {
            if (stopped) return Cleanup;
            stopped = true;
            BeginCleanup();
            player.ClearMediaItems();
            Artwork?.Dispose();
            Artwork = null;
            return Cleanup;
        }

        private void BeginCleanup()
        {
            if (resourcesClosed) return;
            resourcesClosed = true;
            Interlocked.Increment(ref metadataRevision);
            cancellation.Cancel();
            dataSourceFactory?.CancelReads();
            buffer?.Fail(new OperationCanceledException());
            // Wake the loader before Stop waits for it, preserving the original cancellation order.
            // Stop alone retains ExoPlayer's play intent, which could auto-play the next source.
            player.Pause();
            player.Stop();
            // Stop/ClearMediaItems release sources asynchronously. Their Java factory must
            // remain callable until the source is released; cancellation above is immediate.
            // The Android GC bridge reclaims the factory with its native media source.
            var ownedBuffer = buffer;
            Cleanup = transferTask.ContinueWith(_ =>
            {
                DownloadService.Instance.ReleaseStream(Username, FileInfo.FullFileName, ownedBuffer);
                try { ownedBuffer?.Dispose(); }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                { Logger.Debug("Stream cache cleanup: " + exception.Message); }
                cancellation.Dispose();
            }, TaskScheduler.Default);
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
    }
}
