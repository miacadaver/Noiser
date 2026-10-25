using Common.Playback;
using NUnit.Framework;

namespace UnitTestCommon
{
    [TestFixture]
    public class ProgressiveSeekIndexTests
    {
        [Test]
        public void RangeRequiresCompletedSamplesAndKeepsUnknownDurationExplicit()
        {
            var index = new ProgressiveSeekIndex();
            Assert.That(index.Snapshot(-1).CanSeek, Is.False);
            AddSample(index, 0, 0, 1_000);
            AddSample(index, 250_000, 1_000, 2_000);
            AddSample(index, 500_000, 2_000, 3_000);
            Assert.That(index.Snapshot(-1).CanSeek, Is.False);
            AddSample(index, 750_000, 3_000, 4_000);

            var range = index.Snapshot(-1);
            Assert.That(range.CanSeek, Is.True);
            Assert.That(range.HasKnownDuration, Is.False);
            Assert.That(range.SeekablePositionUs, Is.EqualTo(250_000));
            Assert.That(range.SafePosition, Is.EqualTo(2_000));
            Assert.That(range.Contains(250_000), Is.True);
            Assert.That(range.Contains(250_001), Is.False);
            Assert.That(range.Contains(-1), Is.False);
        }

        [Test]
        public void FrontierUsesExtractorTimestampsAndOffsetsWithoutAssumingCbr()
        {
            var index = new ProgressiveSeekIndex();
            AddSample(index, 0, 0, 1_000);
            AddSample(index, 250_000, 1_000, 40_000);
            AddSample(index, 500_000, 40_000, 60_000); // low bitrate span
            AddSample(index, 750_000, 60_000, 190_000); // high bitrate span
            AddSample(index, 1_000_000, 190_000, 210_000);
            AddSample(index, 1_250_000, 210_000, 390_000);
            AddSample(index, 1_500_000, 390_000, 410_000);

            var range = index.Snapshot(-1);
            Assert.That(range.SeekablePositionUs, Is.EqualTo(1_000_000));
            Assert.That(range.SafePosition, Is.EqualTo(210_000));
            Assert.That(range.ContainsMappedPoint(750_000, 190_000), Is.True);
            Assert.That(range.ContainsMappedPoint(750_000, range.SafePosition + 1), Is.False);
        }

        [Test]
        public void ReplayedAndOutOfOrderSamplesCannotMoveSeekFrontierBackwards()
        {
            var index = new ProgressiveSeekIndex();
            AddSample(index, 0, 0, 1_000);
            AddSample(index, 500_000, 10_000, 11_000);
            AddSample(index, 1_000_000, 20_000, 21_000);
            var previous = index.Snapshot(-1);

            Assert.That(index.RecordCompleteSample(750_000, 15_000, 16_000), Is.False);
            Assert.That(index.Snapshot(-1).SeekablePositionUs, Is.EqualTo(previous.SeekablePositionUs));
            Assert.That(index.RecordCompleteSample(1_500_000, 30_000, 31_000), Is.True);
            Assert.That(index.Snapshot(-1).SeekablePositionUs, Is.EqualTo(1_000_000));
        }

        [Test]
        public void KnownDurationClampsRangeAndSliderRequestsAtBothBoundaries()
        {
            var index = new ProgressiveSeekIndex();
            AddSample(index, 0, 0, 1_000);
            AddSample(index, 500_000, 10_000, 11_000);
            AddSample(index, 1_000_000, 20_000, 21_000);
            AddSample(index, 1_500_000, 30_000, 31_000);
            var range = index.Snapshot(1_200_000);

            Assert.That(range.SeekablePositionUs, Is.EqualTo(1_000_000));
            Assert.That(range.SliderPosition(-1, 1000), Is.EqualTo(0));
            Assert.That(range.SliderPosition(1200, 1000), Is.EqualTo(1_200_000));
            Assert.That(range.SliderProgress(long.MaxValue, 1000), Is.EqualTo(1000));
            Assert.That(range.SeekableProgress(1000), Is.EqualTo(833));
        }

        [Test]
        public void LongTracksRetainOnlyBoundedFrameTailAndNotifyAtBoundedRate()
        {
            var index = new ProgressiveSeekIndex();
            int notifications = 0;
            for (int i = 0; i < 100_000; i++)
            {
                long start = (long)i * 4_000;
                if (index.RecordCompleteSample((long)i * 10_000, start, start + 1_000)) notifications++;
            }

            Assert.That(index.Snapshot(-1).CanSeek, Is.True);
            Assert.That(index.Snapshot(-1).SeekablePositionUs, Is.GreaterThan(990_000_000));
            Assert.That(notifications, Is.LessThan(5_000));
        }

        [Test]
        public void ShortLowBitrateAudioCanSeekBelow128KiB()
        {
            var index = new ProgressiveSeekIndex();
            for (int i = 0; i < 64; i++)
            {
                long start = i * 1_000L;
                AddSample(index, i * 25_000L, start, start + 1_000);
            }

            var range = index.Snapshot(-1);
            Assert.That(range.CanSeek, Is.True);
            Assert.That(range.SafePosition, Is.LessThan(128 * 1024));
            Assert.That(range.ContainsMappedPoint(range.SeekablePositionUs, range.SafePosition), Is.True);
        }

        [Test]
        public void LargeLeadingId3OffsetIsNotMistakenForSeekableAudio()
        {
            const long firstAudioByte = 200_000;
            var index = new ProgressiveSeekIndex();
            for (int i = 0; i < 80; i++)
            {
                long start = firstAudioByte + i * 1_000L;
                AddSample(index, i * 25_000L, start, start + 1_000);
            }

            var range = index.Snapshot(-1);
            Assert.That(range.CanSeek, Is.True);
            Assert.That(range.MinimumPosition, Is.EqualTo(firstAudioByte));
            Assert.That(range.SafePosition, Is.GreaterThan(firstAudioByte));
            Assert.That(range.ContainsMappedPoint(range.SeekablePositionUs, 0), Is.False);
            Assert.That(range.ContainsMappedPoint(range.SeekablePositionUs, firstAudioByte - 1), Is.False);
            Assert.That(range.ContainsMappedPoint(range.SeekablePositionUs, firstAudioByte), Is.True);
        }

        [Test]
        public void RangeRemainsUnseekableWithoutAnySafeNativePoint()
        {
            var index = new ProgressiveSeekIndex();
            for (int i = 0; i < 40; i++)
            {
                long start = 200_000 + i * 1_000L;
                AddSample(index, i * 25_000L, start, start + 1_000);
            }

            var range = index.Snapshot(-1);
            Assert.That(range.CanSeek, Is.True);
            var rejected = range.WithSafeNativePoint(range.SeekablePositionUs, 0);
            Assert.That(rejected.CanSeek, Is.False);
            Assert.That(rejected.Contains(0), Is.False);

            var accepted = range.WithSafeNativePoint(range.SeekablePositionUs, range.MinimumPosition);
            Assert.That(accepted.CanSeek, Is.True);
            Assert.That(accepted.SeekablePositionUs, Is.EqualTo(range.SeekablePositionUs));
        }

        [Test]
        public void NewTrackStartsWithAnIndependentEmptyReceivedRange()
        {
            var firstTrack = new ProgressiveSeekIndex();
            AddSample(firstTrack, 0, 0, 1_000);
            AddSample(firstTrack, 500_000, 1_000, 2_000);
            AddSample(firstTrack, 1_000_000, 2_000, 3_000);
            Assert.That(firstTrack.Snapshot(-1).CanSeek, Is.True);

            var nextTrack = new ProgressiveSeekIndex();
            Assert.That(nextTrack.Snapshot(-1).CanSeek, Is.False);
            Assert.That(nextTrack.Snapshot(-1).SeekablePositionUs, Is.EqualTo(0));
            Assert.That(nextTrack.Snapshot(-1).SafePosition, Is.EqualTo(0));
        }

        private static void AddSample(ProgressiveSeekIndex index, long timeUs, long start, long end) =>
            index.RecordCompleteSample(timeUs, start, end);
    }
}
