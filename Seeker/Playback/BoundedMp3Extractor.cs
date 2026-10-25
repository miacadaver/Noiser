using AndroidX.Media3.Extractor;
using AndroidX.Media3.Extractor.Mp3;
using AndroidX.Media3.Common;
using Common.Playback;
using System;
using ForwardingInput = AndroidX.Media3.Extractor.ForwardingExtractorInput;
using ForwardingOutput = AndroidX.Media3.Extractor.ForwardingExtractorOutput;
using ForwardingTrack = AndroidX.Media3.Extractor.ForwardingTrackOutput;

namespace Seeker.Playback
{
    /// <summary>
    /// Keeps Media3's ID3 decoder, but bounds its leading-tag peek buffer before it allocates.
    /// Runs on Media3's loader, using the same data source and cancellation as audio playback.
    /// </summary>
    internal sealed class BoundedMp3Extractor : Java.Lang.Object, IExtractor
    {
        private readonly Mp3Extractor extractor;
        private readonly ProgressiveSeekIndex seekIndex = new ProgressiveSeekIndex();
        private readonly Action<SeekRangeSnapshot> seekIndexChanged;
        private readonly bool disableSeeking;
        private readonly object durationSync = new object();
        private long durationUs = C.TimeUnset;
        private long pendingSampleTimeUs = C.TimeUnset;
        private int pendingSampleSize;
        private int pendingSampleOffset;
        private long sampleVersion;
        private long indexedSampleVersion;
        private volatile ISeekMap nativeSeekMap;
        private bool inspected;
        private int leadingTagBytes;

        public BoundedMp3Extractor(bool disableSeeking, Action<SeekRangeSnapshot> seekIndexChanged = null)
        {
            // With the transfer's advertised length hidden from the inner extractor, this native
            // index is used instead of its CBR fallback when Xing/VBRI/MLLT data is absent.
            extractor = new Mp3Extractor(Mp3Extractor.FlagEnableIndexSeeking);
            this.seekIndexChanged = seekIndexChanged;
            this.disableSeeking = disableSeeking;
            if (disableSeeking) extractor.DisableSeeking();
        }

        // This factory is used only for explicitly selected MP3s. Read performs validation so
        // Media3 cannot allocate an unchecked ID3 payload while sniffing.
        public bool Sniff(IExtractorInput input) => true;
        public void Init(IExtractorOutput output) => extractor.Init(new TrackingExtractorOutput(output, this));
        public void Seek(long position, long timeUs)
        {
            if (position == 0) inspected = false;
            extractor.Seek(position, timeUs);
        }
        public void Release() => extractor.Release();
        public IExtractor UnderlyingImplementation => extractor;
        public System.Collections.Generic.IList<ISniffFailure> SniffFailureDetails => System.Array.Empty<ISniffFailure>();

        public int Read(IExtractorInput input, PositionHolder seekPosition)
        {
            if (!inspected)
            {
                InspectLeadingTags(input);
                inspected = true;
            }
            try
            {
                // The file size is useful to the datasource and ID3 guard but isn't evidence of a
                // timestamp-to-byte mapping. Unknown length makes Media3 build its incremental
                // sample index for untagged CBR and VBR streams.
                return ReadNative(input, seekPosition);
            }
            catch (Java.Lang.RuntimeException) when (input.Position == 0 && leadingTagBytes > 0)
            {
                // Some malformed extended headers throw rather than returning empty metadata.
                // Discard only the validated, bounded tag prefix and let Media3 find audio.
                input.ResetPeekPosition();
                input.SkipFully(leadingTagBytes);
                leadingTagBytes = 0;
                return ReadNative(input, seekPosition);
            }
        }

        private int ReadNative(IExtractorInput input, PositionHolder seekPosition)
        {
            using var extractorInput = new UnknownLengthExtractorInput(input);
            int result = extractor.Read(extractorInput, seekPosition);
            if (sampleVersion != indexedSampleVersion)
            {
                indexedSampleVersion = sampleVersion;
                long endPosition = Math.Max(0, input.Position - pendingSampleOffset);
                long startPosition = Math.Max(0, endPosition - pendingSampleSize);
                if (seekIndex.RecordCompleteSample(pendingSampleTimeUs, startPosition, endPosition))
                    seekIndexChanged?.Invoke(GetSeekRange());
            }
            return result;
        }

        private void UpdateDuration(long nativeDurationUs)
        {
            // An unknown/missing duration stays unknown. Never substitute a byte-count estimate.
            if (nativeDurationUs <= 0 || nativeDurationUs == C.TimeUnset) return;
            lock (durationSync) durationUs = nativeDurationUs;
            seekIndexChanged?.Invoke(GetSeekRange());
        }

        public SeekRangeSnapshot GetSeekRange()
        {
            long duration;
            lock (durationSync) duration = durationUs;
            var range = seekIndex.Snapshot(duration);
            var map = nativeSeekMap;
            if (disableSeeking || !range.CanSeek || map?.IsSeekable != true)
                return range.WithoutSafeNativePoint();
            var safePoint = FindLastSafeNativePoint(map, range, range.SeekablePositionUs);
            return safePoint == null
                ? range.WithoutSafeNativePoint()
                : range.WithSafeNativePoint(safePoint.TimeUs, safePoint.Position);
        }

        private static SeekPoint FindLastSafeNativePoint(ISeekMap source, SeekRangeSnapshot range, long requestedTimeUs)
        {
            long low = 0, high = Math.Min(requestedTimeUs, range.SeekablePositionUs);
            SeekPoint safe = null;
            for (int i = 0; i < 32 && low <= high; i++)
            {
                long middle = low + ((high - low) / 2);
                var points = source.GetSeekPoints(middle);
                SeekPoint candidate = IsSafe(points.Second, range) ? points.Second
                    : IsSafe(points.First, range) ? points.First : null;
                if (candidate != null)
                {
                    if (safe == null || candidate.TimeUs > safe.TimeUs) safe = candidate;
                    low = middle + 1;
                }
                else high = middle - 1;
            }
            return safe;
        }

        private static bool IsSafe(SeekPoint point, SeekRangeSnapshot range) =>
            point != null && range.ContainsMappedPoint(point.TimeUs, point.Position);

        private void OnSampleMetadata(long timeUs, int size, int offset)
        { pendingSampleTimeUs = timeUs; pendingSampleSize = size; pendingSampleOffset = offset; sampleVersion++; }

        private sealed class UnknownLengthExtractorInput : ForwardingInput
        {
            public UnknownLengthExtractorInput(IExtractorInput input) : base(input) { }
            public override long Length => C.LengthUnset;
        }

        private sealed class TrackingExtractorOutput : ForwardingOutput
        {
            private readonly IExtractorOutput output;
            private readonly BoundedMp3Extractor owner;
            public TrackingExtractorOutput(IExtractorOutput output, BoundedMp3Extractor owner) : base(output)
            { this.output = output; this.owner = owner; }

            public override ITrackOutput Track(int id, int type) =>
                new TrackingTrackOutput(output.Track(id, type), owner);

            public override void SeekMap(ISeekMap map)
            {
                owner.nativeSeekMap = map;
                owner.UpdateDuration(map.DurationUs);
                output.SeekMap(owner.disableSeeking
                    ? map : new SafeSeekMap(owner.GetSeekRange, () => owner.nativeSeekMap, () => owner.CurrentDurationUs));
            }
        }

        private long CurrentDurationUs { get { lock (durationSync) return durationUs; } }

        private sealed class TrackingTrackOutput : ForwardingTrack
        {
            private readonly BoundedMp3Extractor owner;
            public TrackingTrackOutput(ITrackOutput output, BoundedMp3Extractor owner) : base(output) { this.owner = owner; }
            public override void SampleMetadata(long timeUs, int flags, int size, int offset, TrackOutputCryptoData cryptoData)
            {
                base.SampleMetadata(timeUs, flags, size, offset, cryptoData);
                owner.OnSampleMetadata(timeUs, size, offset);
            }
        }

        private sealed class SafeSeekMap : Java.Lang.Object, ISeekMap
        {
            private readonly Func<SeekRangeSnapshot> rangeProvider;
            private readonly Func<ISeekMap> nativeMap;
            private readonly Func<long> duration;
            public SafeSeekMap(Func<SeekRangeSnapshot> rangeProvider, Func<ISeekMap> nativeMap, Func<long> duration)
            { this.rangeProvider = rangeProvider; this.nativeMap = nativeMap; this.duration = duration; }
            // Let the native timeline cache its map's seekability from the start. The app-facing
            // SessionPlayer separately gates seek commands until completed bytes are indexed.
            public bool IsSeekable => nativeMap()?.IsSeekable == true;
            public long DurationUs => duration();
            // Media3 declares isEstimated() as a Java default method. The Xamarin binding's CLR
            // default implementation does not generate a Java bridge, so export it explicitly or
            // ExoPlayer crashes with AbstractMethodError while preparing a stream.
            public bool IsEstimated => nativeMap()?.IsEstimated == true;
            public SeekMapSeekPoints GetSeekPoints(long timeUs)
            {
                var range = rangeProvider();
                var source = nativeMap();
                if (!range.CanSeek || source == null || !source.IsSeekable)
                    return new SeekMapSeekPoints(SeekPoint.Start);

                long targetUs = Math.Max(0, Math.Min(timeUs, range.SeekablePositionUs));
                var points = source.GetSeekPoints(targetUs);
                bool firstSafe = BoundedMp3Extractor.IsSafe(points.First, range);
                bool secondSafe = BoundedMp3Extractor.IsSafe(points.Second, range);
                if (firstSafe && secondSafe)
                    return new SeekMapSeekPoints(points.First, points.Second);
                if (firstSafe)
                    return new SeekMapSeekPoints(points.First);
                if (secondSafe)
                    return new SeekMapSeekPoints(points.Second);

                // Xing/VBRI tables can point beyond the written prefix near their frontier.
                // Walk backward through the same native map until a completed-frame position is
                // found; this keeps extractor time anchoring consistent after seek/re-read.
                SeekPoint safe = BoundedMp3Extractor.FindLastSafeNativePoint(source, range, targetUs);
                return safe == null ? new SeekMapSeekPoints(SeekPoint.Start) : new SeekMapSeekPoints(safe);
            }
        }

        private void InspectLeadingTags(IExtractorInput input)
        {
            leadingTagBytes = 0;
            if (input.Position != 0) return;
            var header = new byte[Id3TagHeader.HeaderLength];
            bool discardMetadata = false;
            int searchedBytes = 0;
            input.ResetPeekPosition();
            while (input.Length < 0 || input.PeekPosition + header.Length <= input.Length)
            {
                if (!input.PeekFully(header, 0, header.Length, true)) break;
                int length = Id3TagHeader.GetTotalLength(header);
                if (length == 0)
                {
                    int audioHeader = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
                    if (MpegAudioUtil.GetFrameSize(audioHeader) > 0 || ++searchedBytes > 128 * 1024) break;
                    // Media3 also searches past leading junk. Check that prefix too, otherwise
                    // a large tag hidden just after it could bypass the allocation guard.
                    leadingTagBytes++;
                    input.ResetPeekPosition();
                    input.AdvancePeekPosition(leadingTagBytes);
                    continue;
                }
                long tagStart = input.PeekPosition - header.Length;
                bool invalid = length < 0 || (input.Length >= 0 && length > input.Length - tagStart);
                if (invalid || discardMetadata || length > Id3TagHeader.MaximumTagBytes - leadingTagBytes)
                {
                    // Skip in the extractor input's small scratch buffer, never by allocating
                    // the advertised tag length. An impossible size discards just its header.
                    input.ResetPeekPosition();
                    input.SkipFully(leadingTagBytes + (invalid ? header.Length : length));
                    leadingTagBytes = 0;
                    discardMetadata = true;
                }
                else
                {
                    input.AdvancePeekPosition(length - header.Length);
                    leadingTagBytes += length;
                }
            }
            input.ResetPeekPosition();
        }
    }
}
