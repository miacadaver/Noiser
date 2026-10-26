using System;

namespace Common.Playback
{
    /// <summary>Advances a paused parser through received, validated seek points before audible resume.</summary>
    public sealed class PendingPlaybackResume
    {
        public const long MaximumPositionMilliseconds = 7L * 24 * 60 * 60 * 1000;
        public long TargetMilliseconds { get; private set; }
        public bool IsPending { get; private set; }
        private long lastStep;

        public PendingPlaybackResume(long positionMilliseconds)
        {
            if (!IsValidPosition(positionMilliseconds)) throw new ArgumentOutOfRangeException(nameof(positionMilliseconds));
            TargetMilliseconds = positionMilliseconds;
            IsPending = positionMilliseconds > 0;
        }

        public static bool IsValidPosition(long position) => position >= 0 && position <= MaximumPositionMilliseconds;

        public long? NextSeek(SeekRangeSnapshot range, long durationMilliseconds)
        {
            if (!IsPending) return null;
            // A completed or changed track must not wait forever for a position past its end.
            if (durationMilliseconds > 0 && TargetMilliseconds >= durationMilliseconds) TargetMilliseconds = 0;
            if (TargetMilliseconds == 0) return 0;
            if (!range.CanSeek) return null;
            if (range.Contains(TargetMilliseconds * 1000)) return TargetMilliseconds;
            long safe = range.SeekablePositionUs / 1000;
            // Let normal loading grow the index. Tiny seeks on every sample cancel the loader
            // and repeatedly flush the decoder instead of filling its paused buffer.
            return safe >= lastStep + 30000 ? safe : (long?)null;
        }

        public void SeekSucceeded(long positionMilliseconds)
        {
            if (!IsPending) return;
            if (positionMilliseconds == TargetMilliseconds) IsPending = false;
            else lastStep = positionMilliseconds;
        }
    }
}
