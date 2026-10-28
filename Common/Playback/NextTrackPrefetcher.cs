using Common.Streaming;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Playback
{
    public sealed class PrefetchedTrackFile : IDisposable
    {
        public string FilePath { get; }
        public long Size { get; }
        internal PrefetchedTrackFile(string path, long size) { FilePath = path; Size = size; }
        public void Dispose() => NextTrackPrefetcher.Delete(FilePath);
    }

    /// <summary>One speculative full-file download. It has no player or persisted transfer state.</summary>
    public sealed class NextTrackPrefetcher : IDisposable
    {
        public const long MaximumFileSize = 32L * 1024 * 1024;
        public const long MaximumCacheBytes = 2 * MaximumFileSize;
        public const string CacheFolder = "mp3-prefetch";
        private readonly object gate = new object();
        private readonly string directory;
        private readonly Func<PlaybackQueueEntry, ProgressiveBuffer, CancellationToken, Task> receive;
        private readonly Action? changed;
        private readonly TimeSpan timeout;
        private Attempt? current;
        private Task tail = Task.CompletedTask;
        private bool disposed;
        private int receiving;
        public bool IsReceiving => Volatile.Read(ref receiving) != 0;
        public Task Cleanup { get { lock (gate) return tail; } }

        public NextTrackPrefetcher(string directory,
            Func<PlaybackQueueEntry, ProgressiveBuffer, CancellationToken, Task> receive,
            Action? changed = null, TimeSpan? timeout = null)
        { this.directory = directory; this.receive = receive; this.changed = changed; this.timeout = timeout ?? TimeSpan.FromMinutes(5); }

        public static PlaybackQueueEntry? Next(PlaybackQueue queue, PlaybackRepeatMode repeat, bool autoplay)
        {
            if (!autoplay || repeat == PlaybackRepeatMode.One || queue.Current == null) return null;
            var entries = queue.Entries;
            var next = queue.HasNext ? entries[queue.CurrentIndex + 1]
                : repeat == PlaybackRepeatMode.All && entries.Count > 1 ? entries[0] : null;
            return next != null && Eligible(next) && (next.Username != queue.Current.Username || next.RemotePath != queue.Current.RemotePath)
                ? next.Copy() : null;
        }
        private static bool Eligible(PlaybackQueueEntry entry) => entry.Size > 0 && entry.Size <= MaximumFileSize
            && PlaybackQueue.IsSupportedAudio(entry.RemotePath);
        private static bool Same(PlaybackQueueEntry a, PlaybackQueueEntry b) => a.Id == b.Id && a.Username == b.Username
            && a.RemotePath == b.RemotePath && a.Size == b.Size
            && a.WasFilenameLatin1Decoded == b.WasFilenameLatin1Decoded && a.WasFolderLatin1Decoded == b.WasFolderLatin1Decoded;

        public void Update(PlaybackQueueEntry? candidate, Task? predecessor = null)
        {
            lock (gate)
            {
                if (disposed) return;
                if (candidate != null && !Eligible(candidate)) candidate = null;
                if (candidate == null && current == null || candidate != null && current != null && Same(candidate, current.Entry)) return;
                CancelLocked();
                if (candidate == null) return;
                var attempt = new Attempt(candidate.Copy());
                current = attempt;
                Task prior = Task.WhenAll(tail, predecessor ?? Task.CompletedTask);
                tail = Task.Run(() => ReceiveAsync(attempt, prior));
            }
        }
        public Task Cancel()
        {
            lock (gate) { CancelLocked(); return tail; }
        }
        private void CancelLocked()
        {
            var previous = current;
            current = null;
            if (previous == null) return;
            try { previous.Cancellation.Cancel(); } catch (ObjectDisposedException) { } catch (AggregateException) { }
            if (previous.ReadyPath != null) { Delete(previous.ReadyPath); previous.ReadyPath = null; }
        }
        /// <summary>Only a fully completed matching file can be taken. Otherwise cancel and await Cleanup before streaming.</summary>
        public PrefetchedTrackFile? Take(PlaybackQueueEntry entry)
        {
            lock (gate)
            {
                var attempt = current;
                if (attempt != null && Same(entry, attempt.Entry) && attempt.ReadyPath != null)
                {
                    try
                    {
                        if (File.Exists(attempt.ReadyPath) && new FileInfo(attempt.ReadyPath).Length == entry.Size)
                        {
                            var lease = new PrefetchedTrackFile(attempt.ReadyPath, entry.Size);
                            attempt.Taken = true;
                            attempt.ReadyPath = null;
                            current = null;
                            return lease;
                        }
                    }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { }
                }
                CancelLocked();
                return null;
            }
        }
        private async Task ReceiveAsync(Attempt attempt, Task predecessor)
        {
            string key = Guid.NewGuid().ToString("N");
            string partial = Path.Combine(directory, key + ".part");
            string ready = Path.Combine(directory, key + ".mp3");
            ProgressiveBuffer? buffer = null;
            bool active = false;
            try
            {
                await predecessor.ConfigureAwait(false);
                attempt.Cancellation.Token.ThrowIfCancellationRequested();
                attempt.Cancellation.CancelAfter(timeout);
                Directory.CreateDirectory(directory);
                long used = 0;
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    used += new FileInfo(file).Length;
                    if (used > MaximumCacheBytes - attempt.Entry.Size)
                        throw new IOException("The prefetch cache is full.");
                }
                buffer = new ProgressiveBuffer(partial, attempt.Entry.Size);
                Interlocked.Increment(ref receiving); active = true; Notify();
                await receive(attempt.Entry, buffer, attempt.Cancellation.Token).ConfigureAwait(false);
                attempt.Cancellation.Token.ThrowIfCancellationRequested();
                buffer.Complete();
                buffer.DetachCompletedFile();
                File.Move(partial, ready);
                lock (gate)
                {
                    if (ReferenceEquals(current, attempt) && !attempt.Cancellation.IsCancellationRequested && !disposed)
                        attempt.ReadyPath = ready;
                }
            }
            // Speculation is optional: failures must never reach the current player or create a retry loop.
            catch (Exception) { }
            finally
            {
                try { buffer?.Dispose(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                Delete(partial);
                lock (gate)
                {
                    if (!attempt.Taken && (!ReferenceEquals(current, attempt) || attempt.ReadyPath == null)) Delete(ready);
                    attempt.Cancellation.Dispose();
                }
                if (active) Interlocked.Decrement(ref receiving);
                Notify();
            }
        }
        private void Notify()
        {
            if (changed == null) return;
            // Cancellation callbacks may finish a worker inline while Update holds gate.
            // Observers must never re-enter that replacement operation synchronously.
            ThreadPool.QueueUserWorkItem(_ => { try { changed(); } catch (Exception) { } });
        }
        internal static void Delete(string path)
        { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        public static void CleanAbandonedCache(string cacheDirectory)
        {
            string directory = Path.Combine(cacheDirectory, CacheFolder);
            try
            {
                if (!Directory.Exists(directory)) return;
                foreach (var file in Directory.EnumerateFiles(directory).Where(path => path.EndsWith(".part", StringComparison.Ordinal)
                    || path.EndsWith(".mp3", StringComparison.Ordinal))) Delete(file);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        public void Dispose()
        { lock (gate) { disposed = true; CancelLocked(); } }
        private sealed class Attempt
        {
            public PlaybackQueueEntry Entry { get; }
            public CancellationTokenSource Cancellation { get; } = new CancellationTokenSource();
            public string? ReadyPath;
            public bool Taken;
            public Attempt(PlaybackQueueEntry entry) { Entry = entry; }
        }
    }
}
