using Common.Streaming;
using NUnit.Framework;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UnitTestCommon
{
    [TestFixture]
    public class ProgressiveBufferTests
    {
        private static ProgressiveBuffer Create(long size = 4) => new ProgressiveBuffer(
            Path.Combine(Path.GetTempPath(), "seeker-stream-test-" + Guid.NewGuid().ToString("N")), size);

        [Test]
        public async Task OnlyCommittedBytesResetTheProgressClock()
        {
            using var buffer = Create();
            await Task.Delay(100);
            var idle = buffer.TimeSinceLastProgress;
            buffer.Writer.Write(Array.Empty<byte>(), 0, 0);
            Assert.That(buffer.TimeSinceLastProgress, Is.GreaterThanOrEqualTo(idle));
            buffer.Writer.Write(new byte[] { 1, 2 }, 0, 2);
            Assert.That(buffer.TimeSinceLastProgress, Is.LessThan(idle));
            Assert.That(buffer.AvailableLength, Is.EqualTo(2));
        }

        [Test]
        public async Task ReaderWaitsThroughRecoveryInsteadOfItsShorterOrdinaryTimeout()
        {
            using var buffer = Create();
            buffer.BeginRecovery(TimeSpan.FromSeconds(2));
            var read = Task.Run(() => buffer.ReadAt(0, new byte[4], 0, 4,
                CancellationToken.None, TimeSpan.FromMilliseconds(20)));
            Assert.That(await Task.WhenAny(read, Task.Delay(100)), Is.Not.SameAs(read));
            buffer.Writer.Write(new byte[] { 1, 2 }, 0, 2);
            Assert.That(await read.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(2));
        }

        [Test]
        public async Task RepeatedFailuresCannotRenewAnExpiredRecoveryWindowWithoutBytes()
        {
            using var buffer = Create();
            buffer.BeginRecovery(TimeSpan.FromMilliseconds(1));
            await Task.Delay(50);
            buffer.BeginRecovery(TimeSpan.FromMinutes(1));
            Assert.Throws<IOException>(() => buffer.ReadAt(0, new byte[4], 0, 4,
                CancellationToken.None, TimeSpan.Zero));
        }

        [Test]
        public async Task NewBytesAllowANewRecoveryWindowForALaterDrop()
        {
            using var buffer = Create();
            buffer.BeginRecovery(TimeSpan.FromMilliseconds(1));
            await Task.Delay(50);
            buffer.Writer.Write(new byte[] { 1, 2 }, 0, 2);
            buffer.BeginRecovery(TimeSpan.FromSeconds(2));
            var read = Task.Run(() => buffer.ReadAt(2, new byte[2], 0, 2,
                CancellationToken.None, TimeSpan.Zero));
            Assert.That(await Task.WhenAny(read, Task.Delay(100)), Is.Not.SameAs(read));
            buffer.Writer.Write(new byte[] { 3, 4 }, 0, 2);
            Assert.That(await read.WaitAsync(TimeSpan.FromSeconds(2)), Is.EqualTo(2));
        }

        [Test]
        public async Task StopAndTerminalFailureStillWakeReadersDuringRecovery()
        {
            using var buffer = Create();
            buffer.BeginRecovery(TimeSpan.FromMinutes(1));
            using var cancellation = new CancellationTokenSource();
            var read = Task.Run(() => buffer.ReadAt(0, new byte[4], 0, 4,
                cancellation.Token, TimeSpan.FromSeconds(1)));
            cancellation.Cancel();
            Assert.CatchAsync<OperationCanceledException>(async () => await read.WaitAsync(TimeSpan.FromSeconds(1)));
            buffer.Fail(new IOException("Recovery failed"));
            Assert.Throws<IOException>(() => buffer.ReadAt(0, new byte[4], 0, 4,
                CancellationToken.None, TimeSpan.FromMinutes(1)));
        }

        [Test]
        public async Task ReaderWaitsAtFrontierAndPlaysBeforeTransferCompletes()
        {
            using var buffer = Create();
            var received = new byte[4];
            var read = Task.Run(() => buffer.ReadAt(0, received, 0, 4, CancellationToken.None, TimeSpan.FromSeconds(5)));
            Assert.That(await Task.WhenAny(read, Task.Delay(100)), Is.Not.SameAs(read));
            await buffer.Writer.WriteAsync(new byte[] { 1, 2 });
            Assert.That(await read.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(2));
            Assert.That(received[..2], Is.EqualTo(new byte[] { 1, 2 }));
            Assert.That(buffer.AvailableLength, Is.LessThan(buffer.ExpectedLength));
            Assert.That(buffer.Writer.Position, Is.EqualTo(2), "reader position must not change resume position");
            var next = Task.Run(() => buffer.ReadAt(2, received, 2, 2, CancellationToken.None, TimeSpan.FromSeconds(5)));
            Assert.That(await Task.WhenAny(next, Task.Delay(100)), Is.Not.SameAs(next));
            await buffer.Writer.WriteAsync(new byte[] { 3, 4 });
            buffer.Complete();
            Assert.That(await next.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(2));
            Assert.That(received, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
            Assert.That(buffer.ReadAt(4, received, 0, 4, CancellationToken.None, TimeSpan.FromSeconds(1)), Is.EqualTo(-1));
        }

        [Test]
        public async Task RewindKeepsReceivedPrefixAndWriterResumePositionForLaterBytes()
        {
            using var buffer = Create(6);
            await buffer.Writer.WriteAsync(new byte[] { 1, 2, 3, 4 });
            var received = new byte[2];

            Assert.That(buffer.ReadAt(2, received, 0, 2, CancellationToken.None, TimeSpan.FromSeconds(1)), Is.EqualTo(2));
            Assert.That(received, Is.EqualTo(new byte[] { 3, 4 }));
            Assert.That(buffer.Writer.Position, Is.EqualTo(4));
            Assert.That(buffer.AvailableLength, Is.EqualTo(4));

            Assert.That(buffer.ReadAt(0, received, 0, 2, CancellationToken.None, TimeSpan.FromSeconds(1)), Is.EqualTo(2));
            Assert.That(received, Is.EqualTo(new byte[] { 1, 2 }));
            Assert.That(buffer.Writer.Position, Is.EqualTo(4));
            Assert.That(buffer.AvailableLength, Is.EqualTo(4));

            await buffer.Writer.WriteAsync(new byte[] { 5, 6 });
            buffer.Complete();
            Assert.That(buffer.Writer.Position, Is.EqualTo(6));
            Assert.That(buffer.AvailableLength, Is.EqualTo(6));
            var complete = new byte[6];
            Assert.That(buffer.ReadAt(0, complete, 0, complete.Length,
                CancellationToken.None, TimeSpan.FromSeconds(1)), Is.EqualTo(6));
            Assert.That(complete, Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6 }));
        }

        [Test]
        public async Task StopWakesBlockedReaderPromptly()
        {
            using var buffer = Create();
            using var cancellation = new CancellationTokenSource();
            var read = Task.Run(() => buffer.ReadAt(0, new byte[4], 0, 4, cancellation.Token, TimeSpan.FromMinutes(3)));
            cancellation.Cancel();
            Assert.ThrowsAsync<OperationCanceledException>(async () => await read.WaitAsync(TimeSpan.FromSeconds(2)));
        }

        [Test]
        public void FailureDrainsReceivedAudioThenReportsErrorInsteadOfEof()
        {
            using var buffer = Create();
            buffer.Writer.Write(new byte[] { 1, 2 });
            buffer.Fail(new IOException("peer disconnected"));
            Assert.That(buffer.ReadAt(0, new byte[4], 0, 4, CancellationToken.None, TimeSpan.FromSeconds(1)), Is.EqualTo(2));
            Assert.Throws<IOException>(() => buffer.ReadAt(2, new byte[4], 0, 4, CancellationToken.None, TimeSpan.FromSeconds(1)));
        }

        [Test]
        public void TruncatedOrOversizedTransfersCannotSucceed()
        {
            using var buffer = Create();
            buffer.Writer.Write(new byte[] { 1, 2 });
            Assert.Throws<IOException>(buffer.Complete);
            Assert.Throws<IOException>(() => buffer.Writer.Write(new byte[] { 3, 4, 5 }));
            Assert.That(buffer.AvailableLength, Is.EqualTo(2));
        }

        [Test]
        public void SlowPeerProducesAnErrorAfterConfiguredWait()
        {
            using var buffer = Create();
            Assert.Throws<IOException>(() => buffer.ReadAt(0, new byte[1], 0, 1, CancellationToken.None, TimeSpan.FromMilliseconds(30)));
        }

        [Test]
        public async Task InterruptingOneReaderDoesNotFailOrMoveTheTransfer()
        {
            using var buffer = Create(4);
            using var lifetime = new CancellationTokenSource();
            int interrupted = 0;
            var blockedRead = Task.Run(() => buffer.ReadAt(0, new byte[2], 0, 2, lifetime.Token,
                TimeSpan.FromSeconds(5), () => Volatile.Read(ref interrupted) != 0));
            await Task.Delay(25);
            Interlocked.Exchange(ref interrupted, 1);
            Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await blockedRead.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.That(buffer.AvailableLength, Is.EqualTo(0));
            Assert.That(buffer.Writer.Position, Is.EqualTo(0));

            await buffer.Writer.WriteAsync(new byte[] { 7, 8 });
            var received = new byte[2];
            Assert.That(buffer.ReadAt(0, received, 0, received.Length, lifetime.Token, TimeSpan.FromSeconds(1)), Is.EqualTo(2));
            Assert.That(received, Is.EqualTo(new byte[] { 7, 8 }));
            Assert.That(buffer.Writer.Position, Is.EqualTo(2));
        }

        [Test]
        public async Task ThreadInterruptWakesAFrontierWaitWithoutClosingTheWriter()
        {
            using var buffer = Create(4);
            using var entered = new ManualResetEventSlim();
            Thread readerThread = null;
            var blockedRead = Task.Run(() =>
            {
                readerThread = Thread.CurrentThread;
                entered.Set();
                return buffer.ReadAt(0, new byte[1], 0, 1, CancellationToken.None,
                    TimeSpan.FromSeconds(5), () => false);
            });

            Assert.That(entered.Wait(TimeSpan.FromSeconds(2)), Is.True);
            readerThread.Interrupt();
            Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await blockedRead.WaitAsync(TimeSpan.FromSeconds(2)));

            await buffer.Writer.WriteAsync(new byte[] { 42 });
            Assert.That(buffer.AvailableLength, Is.EqualTo(1));
            Assert.That(buffer.Writer.Position, Is.EqualTo(1));
        }

        [Test]
        public void DisposeRemovesTemporaryFileAndWakesReaders()
        {
            var buffer = Create();
            string path = buffer.FilePath;
            Assert.That(File.Exists(path), Is.True);
            var read = Task.Run(() => buffer.ReadAt(0, new byte[1], 0, 1, CancellationToken.None, TimeSpan.FromMinutes(3)));
            buffer.Dispose();
            Assert.That(File.Exists(path), Is.False);
            Assert.ThrowsAsync<ObjectDisposedException>(async () => await read.WaitAsync(TimeSpan.FromSeconds(2)));
            buffer.Dispose();
        }
    }
}
