using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Streaming
{
    /// <summary>
    /// One sequential writer and independent positional readers. Only flushed bytes are published.
    /// An empty, unfinished buffer is never EOF. The owner cancels readers before disposing it.
    /// </summary>
    public sealed class ProgressiveBuffer : IDisposable
    {
        private readonly object sync = new object();
        private readonly FileStream file;
        private readonly Stream writer;
        private long available;
        private long lastProgressTimestamp = Stopwatch.GetTimestamp();
        private bool completed;
        private bool disposed;
        private Exception? failure;

        public string FilePath { get; }
        public long ExpectedLength { get; }
        public long AvailableLength { get { lock (sync) return available; } }
        public Stream Writer => writer;
        public TimeSpan TimeSinceLastProgress
        {
            get
            {
                lock (sync) return TimeSpan.FromSeconds(
                    (Stopwatch.GetTimestamp() - lastProgressTimestamp) / (double)Stopwatch.Frequency);
            }
        }

        public ProgressiveBuffer(string filePath, long expectedLength)
        {
            if (expectedLength <= 0) throw new ArgumentOutOfRangeException(nameof(expectedLength));
            FilePath = filePath;
            ExpectedLength = expectedLength;
            file = new FileStream(filePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
            writer = new BufferWriter(this);
        }

        public int ReadAt(long position, byte[] bytes, int offset, int count,
            CancellationToken cancellationToken, TimeSpan timeout, Func<bool>? interruptRequested = null)
        {
            if (position < 0) throw new ArgumentOutOfRangeException(nameof(position));
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || count < 0 || offset > bytes.Length - count) throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0) return 0;
            using var registration = cancellationToken.Register(WakeReaders);
            var elapsed = Stopwatch.StartNew();
            lock (sync)
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (interruptRequested?.Invoke() == true)
                        throw new OperationCanceledException("The media read was cancelled by a seek.");
                    if (disposed) throw new ObjectDisposedException(nameof(ProgressiveBuffer));
                    if (position < available)
                    {
                        file.Position = position;
                        return file.Read(bytes, offset, (int)Math.Min(count, available - position));
                    }
                    if (failure != null) throw new IOException("The peer transfer could not continue.", failure);
                    if (completed) return -1;
                    var remaining = timeout - elapsed.Elapsed;
                    if (remaining <= TimeSpan.Zero) throw new IOException("Timed out waiting for audio from the peer.");
                    // Media3 interrupts a cancelled loader thread during a seek. Poll that thread
                    // signal promptly while retaining the same transfer buffer and cancellation
                    // token, so the next loader can read the same committed prefix.
                    try
                    {
                        Monitor.Wait(sync, interruptRequested == null
                            ? remaining : (remaining < TimeSpan.FromMilliseconds(100) ? remaining : TimeSpan.FromMilliseconds(100)));
                    }
                    catch (ThreadInterruptedException) when (interruptRequested != null)
                    {
                        throw new OperationCanceledException("The media read was interrupted by a seek.");
                    }
                }
            }
        }

        public void Complete()
        {
            lock (sync)
            {
                if (disposed) return;
                if (available != ExpectedLength) throw new IOException("The peer sent an incomplete MP3.");
                completed = true;
                Monitor.PulseAll(sync);
            }
        }

        public void Fail(Exception exception)
        {
            lock (sync)
            {
                if (disposed) return;
                failure = exception;
                Monitor.PulseAll(sync);
            }
        }

        private void Write(byte[] bytes, int offset, int count)
        {
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException(nameof(ProgressiveBuffer));
                if (completed || failure != null) throw new IOException("The streaming buffer is closed.");
                if (count > ExpectedLength - available) throw new IOException("The peer sent more data than advertised.");
                file.Position = available;
                file.Write(bytes, offset, count);
                file.Flush();
                available += count;
                if (count > 0) lastProgressTimestamp = Stopwatch.GetTimestamp();
                Monitor.PulseAll(sync);
            }
        }

        private void WakeReaders() { lock (sync) Monitor.PulseAll(sync); }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                Monitor.PulseAll(sync);
                file.Dispose();
            }
            File.Delete(FilePath);
        }

        // Soulseek reads Position even when the destination cannot seek. Reads never change it.
        private sealed class BufferWriter : Stream
        {
            private readonly ProgressiveBuffer owner;
            public BufferWriter(ProgressiveBuffer owner) { this.owner = owner; }
            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => owner.AvailableLength;
            public override long Position { get => owner.AvailableLength; set => throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) => owner.Write(buffer, offset, count);
            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Write(buffer, offset, count);
                return Task.CompletedTask;
            }
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // The library uses pooled Memory<byte> chunks on netstandard2.1.
                if (System.Runtime.InteropServices.MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> segment))
                    Write(segment.Array!, segment.Offset, segment.Count);
                else
                {
                    var bytes = buffer.ToArray();
                    Write(bytes, 0, bytes.Length);
                }
                return default;
            }
            public override void Flush() { }
            public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
