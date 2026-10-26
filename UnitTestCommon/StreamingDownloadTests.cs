using Common.Streaming;
using NUnit.Framework;
using Seeker;
using Seeker.Helpers;
using Seeker.Services;
using Soulseek;
using System;
using System.IO;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace UnitTestCommon
{
    [TestFixture, NonParallelizable]
    public class StreamingDownloadTests
    {
        public class Stub : DispatchProxy
        {
            public Func<MethodInfo, object?[], object?>? Handle;
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handle?.Invoke(targetMethod!, args!);
        }

        private static T Dummy<T>() where T : class => DispatchProxy.Create<T, Stub>();
        private static ProgressiveBuffer CreateBuffer() => new ProgressiveBuffer(
            Path.Combine(Path.GetTempPath(), "seeker-stream-test-" + Guid.NewGuid().ToString("N")), 4);
        private static FullFileInfo Track() => new FullFileInfo { FullFileName = "music\\track.mp3", Size = 4 };

        private static DownloadService CreateService(Func<Stream, long, CancellationToken, Task> receive,
            Action<int>? token = null)
        {
            var client = Dummy<ISoulseekClient>();
            int nextToken = 0;
            ((Stub)(object)client).Handle = (method, args) =>
            {
                if (method.Name == nameof(ISoulseekClient.GetNextToken))
                {
                    int next = Interlocked.Increment(ref nextToken);
                    token?.Invoke(next);
                    return next;
                }
                if (method.Name == nameof(ISoulseekClient.DownloadAsync)) return Download(args);
                return null;
            };
            async Task<Transfer> Download(object?[] args)
            {
                var factory = (Func<Task<Stream>>)args[2]!;
                await receive(await factory(), (long)args[4]!, (CancellationToken)args[7]!);
                return null!;
            }
            return new DownloadService(Dummy<IToaster>(), Dummy<IFileSystemService>(), Dummy<ISessionService>(),
                Dummy<IMainThreadRunner>(), () => client, Dummy<ILoggerBackend>(), Dummy<INetworkStatus>());
        }

        [Test]
        public async Task ReconnectResumesAfterCommittedPrefix()
        {
            int attempts = 0;
            var events = new List<string>();
            var service = CreateService(async (writer, offset, cancellation) =>
            {
                events.Add("attempt");
                if (attempts++ == 0)
                {
                    Assert.That(offset, Is.Zero);
                    await writer.WriteAsync(new byte[] { 1, 2 }, cancellation);
                    throw new ConnectionException("Remote connection closed");
                }
                Assert.That(offset, Is.EqualTo(2));
                Assert.That(writer.Position, Is.EqualTo(offset));
                await writer.WriteAsync(new byte[] { 3, 4 }, cancellation);
            });
            using var buffer = CreateBuffer();
            await service.StreamAsync("stream-test-peer", Track(), buffer,
                _ => events.Add("queued"), CancellationToken.None, reconnecting: () => events.Add("reconnecting"));
            var bytes = new byte[4];
            Assert.That(buffer.ReadAt(0, bytes, 0, 4, CancellationToken.None, TimeSpan.FromSeconds(1)), Is.EqualTo(4));
            Assert.That(bytes, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
            Assert.That(attempts, Is.EqualTo(2));
            Assert.That(events, Is.EqualTo(new[] { "attempt", "queued", "reconnecting", "attempt" }));
        }

        [Test]
        public void RecoveryStopsAfterThreeReconnectsAndReleasesReservation()
        {
            int attempts = 0;
            int reconnects = 0;
            var service = CreateService(async (writer, _, cancellation) =>
            {
                attempts++;
                if (attempts < 4) await writer.WriteAsync(new[] { (byte)attempts }, cancellation);
                throw new ConnectionException("Remote connection closed");
            });
            using var buffer = CreateBuffer();
            Assert.ThrowsAsync<ConnectionException>(async () => await service.StreamAsync(
                "stream-test-peer", Track(), buffer, _ => { }, CancellationToken.None,
                reconnecting: () => reconnects++));
            Assert.That(attempts, Is.EqualTo(4));
            Assert.That(reconnects, Is.EqualTo(3));
            Assert.That(service.IsStreamingFile("stream-test-peer", Track().FullFileName), Is.False);
            Assert.That(buffer.AvailableLength, Is.EqualTo(3), "progress must not reset the retry budget");
            var prefix = new byte[3];
            Assert.That(buffer.ReadAt(0, prefix, 0, 3, CancellationToken.None, TimeSpan.FromSeconds(1)), Is.EqualTo(3));
            Assert.That(prefix, Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.Throws<IOException>(() => buffer.ReadAt(3, new byte[1], 0, 1,
                CancellationToken.None, TimeSpan.FromMinutes(1)));
        }

        [Test]
        public async Task RepeatedDropsResumeEveryCommittedByteWithoutOverlappingTransfers()
        {
            int attempts = 0;
            int active = 0;
            int maximumActive = 0;
            var offsets = new List<long>();
            var tokens = new List<int>();
            var service = CreateService(async (writer, offset, cancellation) =>
            {
                maximumActive = Math.Max(maximumActive, Interlocked.Increment(ref active));
                try
                {
                    offsets.Add(offset);
                    Assert.That(writer.Position, Is.EqualTo(offset));
                    attempts++;
                    await writer.WriteAsync(new[] { (byte)attempts }, cancellation);
                    if (attempts < 4) throw new ConnectionException("Remote connection closed");
                }
                finally { Interlocked.Decrement(ref active); }
            }, token: tokens.Add);
            using var buffer = CreateBuffer();
            await service.StreamAsync("stream-test-peer", Track(), buffer, _ => { }, CancellationToken.None);
            var bytes = new byte[4];
            Assert.That(buffer.ReadAt(0, bytes, 0, 4, CancellationToken.None, TimeSpan.FromSeconds(1)), Is.EqualTo(4));
            Assert.That(bytes, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
            Assert.That(offsets, Is.EqualTo(new long[] { 0, 1, 2, 3 }));
            Assert.That(tokens, Is.EqualTo(new[] { 1, 2, 3, 4 }));
            Assert.That(maximumActive, Is.EqualTo(1));
        }

        [Test]
        public async Task StopDuringBackoffCancelsRecoveryAndReleasesReservation()
        {
            int attempts = 0;
            using var cancellation = new CancellationTokenSource();
            var service = CreateService((_, __, ___) =>
            {
                attempts++;
                return Task.FromException(new ConnectionException("Remote connection closed"));
            });
            using var buffer = CreateBuffer();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var request = service.StreamAsync("stream-test-peer", Track(), buffer, _ => { }, cancellation.Token,
                reconnecting: () => started.TrySetResult());
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.That(service.IsStreamingFile("stream-test-peer", Track().FullFileName), Is.True);
            using var duplicate = CreateBuffer();
            Assert.ThrowsAsync<InvalidOperationException>(async () => await service.StreamAsync(
                "stream-test-peer", Track(), duplicate, _ => { }, CancellationToken.None));
            cancellation.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await request.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(service.IsStreamingFile("stream-test-peer", Track().FullFileName), Is.False);
            Assert.Throws<IOException>(() => buffer.ReadAt(0, new byte[4], 0, 4,
                CancellationToken.None, TimeSpan.FromSeconds(1)));
        }

        [Test]
        public async Task NetworkHandoffCanWaitForServerReconnectWithoutLosingPrefix()
        {
            int attempts = 0;
            var offsets = new List<long>();
            var service = CreateService(async (writer, offset, cancellation) =>
            {
                offsets.Add(offset);
                attempts++;
                if (attempts == 1)
                {
                    await writer.WriteAsync(new byte[] { 1, 2 }, cancellation);
                    throw new IOException("Network changed", new SocketException((int)SocketError.NetworkUnreachable));
                }
                if (attempts == 2) throw new InvalidOperationException("Client must be logged in");
                await writer.WriteAsync(new byte[] { 3, 4 }, cancellation);
            });
            using var buffer = CreateBuffer();
            await service.StreamAsync("stream-test-peer", Track(), buffer, _ => { }, CancellationToken.None);
            Assert.That(offsets, Is.EqualTo(new long[] { 0, 2, 2 }));
            var bytes = new byte[4];
            Assert.That(buffer.ReadAt(0, bytes, 0, 4, CancellationToken.None, TimeSpan.FromSeconds(1)), Is.EqualTo(4));
            Assert.That(bytes, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
        }

        [Test]
        public async Task SameFileCannotStartSecondStreamOrNormalDownload()
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = CreateService(async (writer, _, cancellation) =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(cancellation);
                await writer.WriteAsync(new byte[] { 1, 2, 3, 4 }, cancellation);
            });
            using var first = CreateBuffer();
            using var duplicate = CreateBuffer();
            var transfer = service.StreamAsync("stream-test-peer", Track(), first, _ => { }, CancellationToken.None);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.ThrowsAsync<InvalidOperationException>(async () => await service.StreamAsync(
                    "stream-test-peer", Track(), duplicate, _ => { }, CancellationToken.None));
                Assert.That(service.AddTransfer("stream-test-peer", Track().FullFileName, 4, 0, 1, false, false, false, true), Is.Null);
            }
            finally { release.TrySetResult(); await transfer; }
        }

        [Test]
        public async Task CancellingStalledRequestAllowsFreshRetryWithoutOverlappingTransfers()
        {
            int attempts = 0;
            int active = 0;
            int maximumActive = 0;
            var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = CreateService(async (writer, offset, cancellation) =>
            {
                maximumActive = Math.Max(maximumActive, Interlocked.Increment(ref active));
                try
                {
                    Assert.That(offset, Is.Zero, "manual retry starts a fresh stream");
                    if (attempts++ == 0)
                    {
                        await writer.WriteAsync(new byte[] { 1, 2 }, cancellation);
                        stalled.SetResult();
                        await Task.Delay(Timeout.Infinite, cancellation);
                    }
                    else await writer.WriteAsync(new byte[] { 3, 4, 5, 6 }, cancellation);
                }
                finally { Interlocked.Decrement(ref active); }
            });
            using var first = CreateBuffer();
            using var retry = CreateBuffer();
            using var stop = new CancellationTokenSource();
            var oldRequest = service.StreamAsync("stream-test-peer", Track(), first, _ => { }, stop.Token,
                retainUntilReleased: true);
            try { await stalled.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
            finally { stop.Cancel(); }
            Assert.CatchAsync<OperationCanceledException>(async () => await oldRequest);
            Assert.That(service.IsStreamingFile("stream-test-peer", Track().FullFileName), Is.False);

            await service.StreamAsync("stream-test-peer", Track(), retry, _ => { }, CancellationToken.None);
            var bytes = new byte[4];
            Assert.That(retry.ReadAt(0, bytes, 0, 4, CancellationToken.None, TimeSpan.FromSeconds(1)), Is.EqualTo(4));
            Assert.That(bytes, Is.EqualTo(new byte[] { 3, 4, 5, 6 }));
            Assert.That(attempts, Is.EqualTo(2));
            Assert.That(maximumActive, Is.EqualTo(1));
        }

        [Test]
        public void ActiveNormalDownloadPreventsPeerStreamingRequest()
        {
            var savedManager = TransferItems.TransferItemManagerDL;
            TransferItems.TransferItemManagerDL = new TransferItemManager();
            DownloadInfo? request = null;
            int attempts = 0;
            try
            {
                var service = CreateService((_, __, ___) => { attempts++; return Task.CompletedTask; });
                request = service.AddTransfer("stream-test-peer", Track().FullFileName, 4, 0, 1, false, false, false, true);
                Assert.That(request, Is.Not.Null);
                using var buffer = CreateBuffer();
                Assert.ThrowsAsync<InvalidOperationException>(async () => await service.StreamAsync(
                    "stream-test-peer", Track(), buffer, _ => { }, CancellationToken.None));
                Assert.That(attempts, Is.Zero);
            }
            finally
            {
                if (request != null) TransferState.CancelAndRemoveToken(request.TransferItemReference);
                TransferItems.TransferItemManagerDL = savedManager;
            }
        }

        [Test]
        public async Task CompletedStreamKeepsItsCacheReservationUntilPlaybackReleasesIt()
        {
            var service = CreateService(async (writer, _, cancellation) =>
                await writer.WriteAsync(new byte[] { 1, 2, 3, 4 }, cancellation));
            using var buffer = CreateBuffer();
            await service.StreamAsync("stream-test-peer", Track(), buffer, _ => { }, CancellationToken.None, retainUntilReleased: true);
            try
            {
                Assert.That(service.IsStreamingFile("stream-test-peer", Track().FullFileName), Is.True);
                Assert.That(service.AddTransfer("stream-test-peer", Track().FullFileName, 4, 0, 1, false, false, false, true), Is.Null);
            }
            finally { service.ReleaseStream("stream-test-peer", Track().FullFileName, buffer); }
            Assert.That(service.IsStreamingFile("stream-test-peer", Track().FullFileName), Is.False);
        }

        [Test]
        public async Task RejectedPeerDoesNotRetryAndWakesReader()
        {
            int attempts = 0;
            int reconnects = 0;
            var service = CreateService((_, __, ___) =>
            {
                attempts++;
                return Task.FromException(new TransferRejectedException("File not shared"));
            });
            using var buffer = CreateBuffer();
            Assert.ThrowsAsync<TransferRejectedException>(async () => await service.StreamAsync(
                "stream-test-peer", Track(), buffer, _ => { }, CancellationToken.None,
                reconnecting: () => reconnects++));
            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(reconnects, Is.Zero);
            var read = Task.Run(() => buffer.ReadAt(0, new byte[4], 0, 4, CancellationToken.None, TimeSpan.FromSeconds(5)));
            Assert.ThrowsAsync<IOException>(async () => await read.WaitAsync(TimeSpan.FromSeconds(2)));
        }
    }
}
