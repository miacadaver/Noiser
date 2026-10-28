using Common.Playback;
using Common.Streaming;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UnitTestCommon
{
    [NonParallelizable]
    public class NextTrackPrefetchTests
    {
        private string directory = null!;
        private NextTrackPrefetcher? cache;
        private static PlaybackQueueEntry Entry(string name = "next", long size = 4, string peer = "peer", string? id = null) =>
            new PlaybackQueueEntry(peer, "Music\\" + name + ".mp3", size, id: id ?? name);
        private static readonly byte[] Bytes = { 1, 2, 3, 4 };
        private static TaskCompletionSource<bool> Signal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private static Task Write(PlaybackQueueEntry entry, ProgressiveBuffer buffer, CancellationToken token) => buffer.Writer.WriteAsync(Bytes, 0, Bytes.Length, token);
        [SetUp] public void Setup()
        {
            directory = Path.Combine(Path.GetTempPath(), "noiser-prefetch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }
        [TearDown] public async Task Cleanup()
        {
            cache?.Dispose();
            if (cache != null) await cache.Cleanup.WaitAsync(TimeSpan.FromSeconds(5));
            Directory.Delete(directory, true);
        }
        [Test] public async Task OnlyACompleteMatchingFileCanBeTakenAndItsLeaseOwnsCleanup()
        {
            cache = new NextTrackPrefetcher(directory, Write);
            var entry = Entry(); cache.Update(entry); await cache.Cleanup;
            Assert.That(cache.IsReceiving, Is.False);
            using var file = cache.Take(entry);
            Assert.That(file, Is.Not.Null);
            Assert.That(File.ReadAllBytes(file!.FilePath), Is.EqualTo(Bytes));
            Assert.That(Directory.GetFiles(directory).Single(), Does.EndWith(".mp3"));
            cache.Dispose(); Assert.That(File.Exists(file.FilePath), Is.True);
            file.Dispose(); Assert.That(Directory.GetFiles(directory), Is.Empty);
        }
        [Test] public async Task FailedPrefetchDoesNotRetryEveryRefreshOrThrowIntoPlayer()
        {
            int calls = 0;
            cache = new NextTrackPrefetcher(directory, (_, __, ___) => { calls++; throw new IOException("offline"); });
            cache.Update(Entry()); await cache.Cleanup;
            for (int i = 0; i < 50; i++) cache.Update(Entry());
            await cache.Cleanup;
            Assert.That(calls, Is.EqualTo(1)); Assert.That(cache.Take(Entry()), Is.Null);
            Assert.That(Directory.GetFiles(directory), Is.Empty);
        }
        [TestCase(0)] [TestCase(33554433)]
        public async Task UnknownOrOversizedSongsAreNotDownloaded(long size)
        {
            int calls = 0;
            cache = new NextTrackPrefetcher(directory, (_, __, ___) => { calls++; return Task.CompletedTask; });
            cache.Update(Entry(size: size)); await cache.Cleanup;
            Assert.That(calls, Is.Zero); Assert.That(Directory.GetFiles(directory), Is.Empty);
        }
        [Test] public async Task UnsupportedFormatIsSkipped()
        {
            int calls = 0;
            cache = new NextTrackPrefetcher(directory, (_, __, ___) => { calls++; return Task.CompletedTask; });
            cache.Update(new PlaybackQueueEntry("peer", "Music\\song.flac", 4)); await cache.Cleanup;
            Assert.That(calls, Is.Zero);
        }
        [TestCase(3)] [TestCase(5)]
        public async Task ShortAndOversizedTransfersCannotBecomeReady(int length)
        {
            cache = new NextTrackPrefetcher(directory, (_, buffer, token) => buffer.Writer.WriteAsync(new byte[length], 0, length, token));
            cache.Update(Entry()); await cache.Cleanup;
            Assert.That(cache.Take(Entry()), Is.Null); Assert.That(Directory.GetFiles(directory), Is.Empty);
        }
        [Test] public async Task CandidateReplacementWaitsUntilCancelledTransferActuallyExits()
        {
            var started = Signal(); var release = Signal();
            int calls = 0, active = 0, maximum = 0;
            cache = new NextTrackPrefetcher(directory, async (entry, buffer, token) =>
            {
                calls++; maximum = Math.Max(maximum, ++active);
                try
                {
                    if (entry.Id == "old") { started.TrySetResult(true); await release.Task; }
                    token.ThrowIfCancellationRequested();
                    await Write(entry, buffer, token);
                }
                finally { active--; }
            });
            cache.Update(Entry("old")); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cache.Update(Entry("new"));
            Assert.That(calls, Is.EqualTo(1));
            release.SetResult(true); await cache.Cleanup.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(maximum, Is.EqualTo(1)); Assert.That(calls, Is.EqualTo(2));
            using var ready = cache.Take(Entry("new")); Assert.That(ready, Is.Not.Null);
            Assert.That(Directory.GetFiles(directory).Length, Is.EqualTo(1));
        }
        [Test] public async Task SelectingAnUnfinishedTrackCancelsItBeforeNormalStreamingCanStart()
        {
            var started = Signal(); var release = Signal(); bool cancelled = false;
            cache = new NextTrackPrefetcher(directory, async (_, buffer, token) =>
            {
                await buffer.Writer.WriteAsync(Bytes, 0, 2, token);
                started.SetResult(true); await release.Task;
                cancelled = token.IsCancellationRequested; token.ThrowIfCancellationRequested();
            });
            cache.Update(Entry()); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(cache.Take(Entry()), Is.Null);
            Task barrier = cache.Cancel(); Assert.That(barrier.IsCompleted, Is.False);
            release.SetResult(true); await barrier.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(cancelled, Is.True); Assert.That(cache.IsReceiving, Is.False);
            Assert.That(Directory.GetFiles(directory), Is.Empty);
        }
        [Test] public async Task PauseStopOrDisablingDeletesReadyFile()
        {
            cache = new NextTrackPrefetcher(directory, Write);
            cache.Update(Entry()); await cache.Cleanup;
            cache.Update(null); await cache.Cancel();
            Assert.That(Directory.GetFiles(directory), Is.Empty);
        }
        [Test] public async Task CacheIdentityIncludesPeerPathSizeEntryIdAndDecodingFlags()
        {
            cache = new NextTrackPrefetcher(directory, Write);
            cache.Update(Entry()); await cache.Cleanup;
            Assert.That(cache.Take(Entry(peer: "other")), Is.Null);
            cache.Update(Entry()); await cache.Cleanup;
            Assert.That(cache.Take(Entry("different", id: "next")), Is.Null);
            cache.Update(Entry()); await cache.Cleanup;
            Assert.That(cache.Take(Entry(size: 5)), Is.Null);
            cache.Update(Entry()); await cache.Cleanup;
            Assert.That(cache.Take(Entry(id: "another-id")), Is.Null);
            cache.Update(Entry()); await cache.Cleanup;
            Assert.That(cache.Take(new PlaybackQueueEntry("peer", "Music\\next.mp3", 4, id: "next", wasFolderLatin1Decoded: true)), Is.Null);
            Assert.That(Directory.GetFiles(directory), Is.Empty);
        }
        [Test] public async Task MissingOrModifiedCompletedFileFallsBackInsteadOfBeingPlayed()
        {
            cache = new NextTrackPrefetcher(directory, Write);
            cache.Update(Entry()); await cache.Cleanup;
            File.WriteAllBytes(Directory.GetFiles(directory).Single(), new byte[] { 1 });
            Assert.That(cache.Take(Entry()), Is.Null);
            cache.Update(Entry()); await cache.Cleanup;
            File.Delete(Directory.GetFiles(directory).Single());
            Assert.That(cache.Take(Entry()), Is.Null);
        }
        [Test] public async Task TimeoutIsBoundedAndNotRetriedAutomatically()
        {
            int calls = 0;
            cache = new NextTrackPrefetcher(directory, async (_, __, token) => { calls++; await Task.Delay(Timeout.Infinite, token); }, timeout: TimeSpan.FromMilliseconds(50));
            cache.Update(Entry()); await cache.Cleanup.WaitAsync(TimeSpan.FromSeconds(5));
            cache.Update(Entry()); await cache.Cleanup;
            Assert.That(calls, Is.EqualTo(1)); Assert.That(cache.IsReceiving, Is.False);
            Assert.That(Directory.GetFiles(directory), Is.Empty);
        }
        [Test] public async Task NotificationFailuresCannotBreakCacheOrItsCleanup()
        {
            cache = new NextTrackPrefetcher(directory, Write, () => throw new InvalidOperationException("view gone"));
            cache.Update(Entry()); await cache.Cleanup;
            using var ready = cache.Take(Entry()); Assert.That(ready, Is.Not.Null);
        }
        [Test] public async Task PredecessorBarrierPreventsStartingDuringEarlierStreamCleanup()
        {
            var prior = Signal(); int calls = 0;
            cache = new NextTrackPrefetcher(directory, (entry, buffer, token) => { calls++; return Write(entry, buffer, token); });
            cache.Update(Entry(), prior.Task); Assert.That(calls, Is.Zero);
            prior.SetResult(true); await cache.Cleanup;
            Assert.That(calls, Is.EqualTo(1));
        }
        [Test] public async Task MetadataRefreshKeepsCompletedCandidateAndDoesNotRedownload()
        {
            int calls = 0;
            cache = new NextTrackPrefetcher(directory, (entry, buffer, token) => { calls++; return Write(entry, buffer, token); });
            cache.Update(Entry()); await cache.Cleanup;
            for (int i = 0; i < 20; i++) cache.Update(Entry().Copy());
            Assert.That(calls, Is.EqualTo(1)); using var ready = cache.Take(Entry()); Assert.That(ready, Is.Not.Null);
        }
        [Test] public async Task TakingReadyFileAndPrefetchingSuccessorKeepsOwnershipSeparate()
        {
            cache = new NextTrackPrefetcher(directory, Write);
            cache.Update(Entry("second")); await cache.Cleanup;
            using var current = cache.Take(Entry("second"));
            cache.Update(Entry("third")); await cache.Cleanup;
            Assert.That(File.Exists(current!.FilePath), Is.True);
            cache.Update(null); await cache.Cancel();
            Assert.That(Directory.GetFiles(directory).Single(), Is.EqualTo(current.FilePath));
        }
        [Test] public async Task DisposePreventsLatePublicationAndFurtherRequests()
        {
            var started = Signal(); var release = Signal(); int calls = 0;
            cache = new NextTrackPrefetcher(directory, async (_, buffer, __) =>
            { calls++; started.SetResult(true); await release.Task; buffer.Writer.Write(Bytes, 0, Bytes.Length); });
            cache.Update(Entry()); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cache.Dispose(); release.SetResult(true); await cache.Cleanup;
            cache.Update(Entry("new")); Assert.That(calls, Is.EqualTo(1)); Assert.That(Directory.GetFiles(directory), Is.Empty);
        }
        [Test] public async Task RapidQueueChangesOnlyStartLastCandidateAfterPriorCleanup()
        {
            var prior = Signal(); int calls = 0;
            cache = new NextTrackPrefetcher(directory, (entry, buffer, token) => { calls++; return Write(entry, buffer, token); });
            for (int i = 0; i < 30; i++) cache.Update(Entry("song" + i), prior.Task);
            prior.SetResult(true); await cache.Cleanup.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(calls, Is.EqualTo(1)); using var file = cache.Take(Entry("song29")); Assert.That(file, Is.Not.Null);
        }
        [Test] public async Task TotalCacheBudgetIncludesFilesThatCouldNotBeCleanedUp()
        {
            string existing = Path.Combine(directory, "existing.mp3");
            using (var file = File.Create(existing)) file.SetLength(NextTrackPrefetcher.MaximumCacheBytes - 3);
            int calls = 0;
            cache = new NextTrackPrefetcher(directory, (entry, buffer, token) => { calls++; return Write(entry, buffer, token); });
            cache.Update(Entry()); await cache.Cleanup;
            Assert.That(calls, Is.Zero); Assert.That(cache.Take(Entry()), Is.Null);
            Assert.That(Directory.GetFiles(directory), Is.EqualTo(new[] { existing }));
        }
        [Test] public void NextPolicyMatchesAutoplayRepeatWrapAndQueueEdits()
        {
            var queue = new PlaybackQueue(); queue.Replace(new[] { Entry("one"), Entry("two"), Entry("three") });
            Assert.That(NextTrackPrefetcher.Next(queue, PlaybackRepeatMode.Off, true)!.Id, Is.EqualTo("two"));
            Assert.That(NextTrackPrefetcher.Next(queue, PlaybackRepeatMode.Off, false), Is.Null);
            Assert.That(NextTrackPrefetcher.Next(queue, PlaybackRepeatMode.One, true), Is.Null);
            queue.AddNext(Entry("inserted"));
            Assert.That(NextTrackPrefetcher.Next(queue, PlaybackRepeatMode.Off, true)!.Id, Is.EqualTo("inserted"));
            queue.Select("three"); Assert.That(NextTrackPrefetcher.Next(queue, PlaybackRepeatMode.Off, true), Is.Null);
            Assert.That(NextTrackPrefetcher.Next(queue, PlaybackRepeatMode.All, true)!.Id, Is.EqualTo("one"));
        }
        [Test] public void SameSourceAndIneligibleImmediateNextDoNotPrefetchOrSkipAhead()
        {
            var queue = new PlaybackQueue(); queue.Replace(new[] { Entry("one"), Entry("one", id: "duplicate"), Entry("later") });
            Assert.That(NextTrackPrefetcher.Next(queue, PlaybackRepeatMode.Off, true), Is.Null);
            queue.Replace(new[] { Entry("one"), Entry("large", NextTrackPrefetcher.MaximumFileSize + 1), Entry("later") });
            Assert.That(NextTrackPrefetcher.Next(queue, PlaybackRepeatMode.Off, true), Is.Null);
        }
        [Test] public void DetachRequiresSuccessfulCompletionAndTransfersDeletionOwnership()
        {
            string path = Path.Combine(directory, "detach.part");
            using var buffer = new ProgressiveBuffer(path, 4);
            buffer.Writer.Write(Bytes, 0, 2);
            Assert.Throws<InvalidOperationException>(() => buffer.DetachCompletedFile());
            buffer.Writer.Write(Bytes, 2, 2);
            Assert.Throws<InvalidOperationException>(() => buffer.DetachCompletedFile());
            buffer.Complete(); Assert.That(buffer.DetachCompletedFile(), Is.EqualTo(path));
            buffer.Dispose(); Assert.That(File.ReadAllBytes(path), Is.EqualTo(Bytes));
            File.Delete(path);
        }
        [Test] public void FailedCompletedBufferCannotDetach()
        {
            string path = Path.Combine(directory, "failed.part");
            using var buffer = new ProgressiveBuffer(path, 4);
            buffer.Writer.Write(Bytes, 0, 4); buffer.Complete(); buffer.Fail(new IOException("late failure"));
            Assert.Throws<InvalidOperationException>(() => buffer.DetachCompletedFile());
        }
        [Test] public void AbandonedPrefetchFilesAreRemovedAtProcessStartup()
        {
            string folder = Path.Combine(directory, NextTrackPrefetcher.CacheFolder); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "abandoned.part"), "partial");
            File.WriteAllText(Path.Combine(folder, "abandoned.mp3"), "ready");
            File.WriteAllText(Path.Combine(folder, "unrelated.txt"), "keep");
            NextTrackPrefetcher.CleanAbandonedCache(directory);
            Assert.That(Directory.GetFiles(folder).Select(Path.GetFileName), Is.EqualTo(new[] { "unrelated.txt" }));
        }
    }
}
