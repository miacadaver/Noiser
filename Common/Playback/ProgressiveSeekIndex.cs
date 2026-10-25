using System;
using System.Collections.Generic;

namespace Common.Playback
{
    /// <summary>A completed MP3 sample's extractor timestamp and absolute audio byte bounds.</summary>
    public readonly struct ProgressiveSeekPoint
    {
        public long TimeUs { get; }
        public long Position { get; }
        public long EndPosition { get; }
        public ProgressiveSeekPoint(long timeUs, long position, long endPosition)
        { TimeUs = timeUs; Position = position; EndPosition = endPosition; }
    }

    /// <summary>Thread-safe high-water and short frame tail for a sequentially received MP3 prefix.</summary>
    /// <remarks>
    /// Media3's MP3 extractor owns the timestamp-to-byte map (including Xing/VBRI handling and its
    /// incremental index). The tail proves that mapped seeks have parsed audio lookahead; it is
    /// not a second seek map and never infers offsets from bitrate.
    /// </remarks>
    public sealed class ProgressiveSeekIndex
    {
        private const long NotifyIntervalUs = 250_000;
        private const long FrontierSafetyUs = 500_000;
        private const int MaximumTailSamples = 64;
        private readonly object sync = new object();
        private readonly List<ProgressiveSeekPoint> tail = new List<ProgressiveSeekPoint>(MaximumTailSamples);
        private long latestCompleteTimeUs = -1;
        private long latestCompletePosition = -1;
        private long firstAudioPosition = -1;
        private long lastNotifiedTimeUs = -1;
        private int completedSamples;

        /// <summary>Records a completed sample; returns true when observers should refresh.</summary>
        public bool RecordCompleteSample(long timeUs, long startPosition, long endPosition)
        {
            if (timeUs < 0 || startPosition < 0 || endPosition <= startPosition) return false;
            lock (sync)
            {
                // Seeking backwards replays known frames. Retain the per-track high-water mark.
                if (timeUs <= latestCompleteTimeUs || endPosition <= latestCompletePosition) return false;
                if (firstAudioPosition < 0) firstAudioPosition = startPosition;
                latestCompleteTimeUs = timeUs;
                latestCompletePosition = endPosition;
                completedSamples++;
                tail.Add(new ProgressiveSeekPoint(timeUs, startPosition, endPosition));
                if (tail.Count > MaximumTailSamples) tail.RemoveAt(0);

                if (lastNotifiedTimeUs >= 0 && timeUs - lastNotifiedTimeUs < NotifyIntervalUs) return false;
                lastNotifiedTimeUs = timeUs;
                return true;
            }
        }

        public SeekRangeSnapshot Snapshot(long durationUs)
        {
            lock (sync)
            {
                long safeEndUs = latestCompleteTimeUs < FrontierSafetyUs
                    ? 0 : latestCompleteTimeUs - FrontierSafetyUs;
                if (durationUs > 0) safeEndUs = Math.Min(safeEndUs, durationUs);
                ProgressiveSeekPoint? boundary = null;
                for (int i = tail.Count - 1; i >= 0; i--)
                {
                    if (tail[i].TimeUs <= safeEndUs) { boundary = tail[i]; break; }
                }

                bool canSeek = completedSamples >= 2 && safeEndUs > 0 && boundary.HasValue
                    && boundary.Value.TimeUs > 0 && boundary.Value.EndPosition > firstAudioPosition;
                var safeBoundary = boundary.GetValueOrDefault();
                long seekableTimeUs = canSeek ? safeBoundary.TimeUs : 0;
                long safePosition = canSeek ? safeBoundary.EndPosition : 0;
                return new SeekRangeSnapshot(seekableTimeUs, durationUs > 0 ? durationUs : -1,
                    canSeek, safePosition, firstAudioPosition < 0 ? 0 : firstAudioPosition);
            }
        }
    }

    public readonly struct SeekRangeSnapshot
    {
        public long SeekablePositionUs { get; }
        public long DurationUs { get; }
        public bool CanSeek { get; }
        public long SafePosition { get; }
        public long MinimumPosition { get; }
        public bool HasKnownDuration => DurationUs > 0;
        public SeekRangeSnapshot(long seekablePositionUs, long durationUs, bool canSeek,
            long safePosition = 0, long minimumPosition = 0)
        {
            SeekablePositionUs = seekablePositionUs;
            DurationUs = durationUs;
            CanSeek = canSeek;
            SafePosition = safePosition;
            MinimumPosition = minimumPosition;
        }

        public bool Contains(long positionUs) => CanSeek && positionUs >= 0 && positionUs <= SeekablePositionUs;

        public bool ContainsMappedPoint(long timeUs, long bytePosition) =>
            Contains(timeUs) && bytePosition >= MinimumPosition && bytePosition <= SafePosition;

        /// <summary>Returns a range bounded by an actual safe point from Media3's native seek map.</summary>
        public SeekRangeSnapshot WithSafeNativePoint(long timeUs, long bytePosition) =>
            ContainsMappedPoint(timeUs, bytePosition)
                ? new SeekRangeSnapshot(timeUs, DurationUs, true, SafePosition, MinimumPosition)
                : WithoutSafeNativePoint();

        public SeekRangeSnapshot WithoutSafeNativePoint() =>
            new SeekRangeSnapshot(0, DurationUs, false, SafePosition, MinimumPosition);

        public long SliderPosition(int progress, int max)
        {
            if (max <= 0 || progress < 0) return 0;
            int bounded = Math.Min(progress, max);
            long extent = HasKnownDuration ? DurationUs : SeekablePositionUs;
            return (long)((decimal)extent * bounded / max);
        }

        public int SliderProgress(long positionUs, int max)
        {
            long extent = HasKnownDuration ? DurationUs : SeekablePositionUs;
            if (max <= 0 || extent <= 0 || positionUs <= 0) return 0;
            long bounded = Math.Min(positionUs, extent);
            return (int)Math.Min(max, (decimal)bounded * max / extent);
        }

        public int SeekableProgress(int max)
        {
            if (max <= 0) return 0;
            if (!HasKnownDuration) return CanSeek ? max : 0;
            return (int)Math.Min(max, (decimal)SeekablePositionUs * max / DurationUs);
        }
    }
}
