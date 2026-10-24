using Uri = Android.Net.Uri;
using AndroidX.Media3.DataSource;
using Common.Streaming;
using System;
using System.Threading;

namespace Seeker.Playback
{
    /// <summary>Media3 loader reads wait for the peer; Close interrupts the wait before player release.</summary>
    internal sealed class ProgressiveDataSource : BaseDataSource
    {
        private readonly ProgressiveBuffer buffer;
        private readonly CancellationToken lifetime;
        private CancellationTokenSource readCancellation;
        private Uri uri;
        private long position;
        private long remaining;

        public ProgressiveDataSource(ProgressiveBuffer buffer, CancellationToken lifetime) : base(false)
        { this.buffer = buffer; this.lifetime = lifetime; }
        public override Uri Uri => uri;

        public override long Open(DataSpec dataSpec)
        {
            TransferInitializing(dataSpec);
            position = dataSpec.Position;
            if (position < 0 || position > buffer.ExpectedLength) throw new Java.IO.IOException("Invalid MP3 read position.");
            remaining = dataSpec.Length < 0 ? buffer.ExpectedLength - position : Math.Min(dataSpec.Length, buffer.ExpectedLength - position);
            uri = dataSpec.Uri;
            readCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            TransferStarted(dataSpec);
            return remaining;
        }

        public override int Read(byte[] bytes, int offset, int length)
        {
            if (length == 0) return 0;
            if (remaining == 0) return -1;
            try
            {
                // Initial peer queue waits get more time than a stall during playback.
                var timeout = buffer.AvailableLength == 0 ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(45);
                int read = buffer.ReadAt(position, bytes, offset, (int)Math.Min(length, remaining), readCancellation.Token, timeout);
                if (read > 0)
                {
                    position += read;
                    remaining -= read;
                    BytesTransferred(read);
                }
                return read;
            }
            catch (Exception exception) when (exception is System.IO.IOException || exception is OperationCanceledException || exception is ObjectDisposedException)
            {
                throw new Java.IO.IOException(exception.Message);
            }
        }

        private void CancelRead() => readCancellation?.Cancel();

        public override void Close()
        {
            CancelRead();
            readCancellation?.Dispose();
            readCancellation = null;
            if (uri != null)
            {
                uri = null;
                TransferEnded();
            }
        }

        internal sealed class Factory : Java.Lang.Object, IDataSourceFactory
        {
            private readonly ProgressiveBuffer buffer;
            private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
            public Factory(ProgressiveBuffer buffer) { this.buffer = buffer; }
            public IDataSource CreateDataSource()
            {
                return new ProgressiveDataSource(buffer, lifetime.Token);
            }
            public void CancelReads()
            {
                lifetime.Cancel();
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) lifetime.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
