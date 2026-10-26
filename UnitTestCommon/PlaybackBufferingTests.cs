using Common.Playback;
using NUnit.Framework;
using System;

namespace UnitTestCommon
{
    public class PlaybackBufferingTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void PeerQueueIsShownEvenAfterReceivingAPrefix(bool hasBytes)
        {
            var phase = PlaybackPresentation.Resolve(true, false, 2, true, false, false,
                hasBytes, queued: true, stalled: true);
            Assert.That(phase, Is.EqualTo(PlaybackPhase.Queued));
            Assert.That(PlaybackPresentation.CanRetry(phase), Is.False);
        }

        [Test]
        public void ExistingReconnectAttemptIsDistinctFromPeerQueueAndStall()
        {
            var phase = PlaybackPresentation.Resolve(true, false, 2, true, false, false,
                true, queued: true, reconnecting: true, stalled: true);
            Assert.That(phase, Is.EqualTo(PlaybackPhase.Reconnecting));
            Assert.That(PlaybackPresentation.CanRetry(phase), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ProlongedWaitCanBeRetriedBeforeOrAfterFirstAudio(bool hasBytes)
        {
            var phase = PlaybackPresentation.Resolve(true, false, 2, true, false, false,
                hasBytes, queued: false, stalled: true);
            Assert.That(phase, Is.EqualTo(PlaybackPhase.Stalled));
            Assert.That(PlaybackPresentation.CanRetry(phase), Is.True);
        }

        [TestCase(true, true, false, PlaybackPhase.Playing)]
        [TestCase(false, false, false, PlaybackPhase.Paused)]
        [TestCase(true, false, true, PlaybackPhase.Paused)]
        public void BufferedPlaybackAndUserOrFocusPausesDoNotOfferRetry(
            bool ready, bool playing, bool suppressed, PlaybackPhase expected)
        {
            var phase = PlaybackPresentation.Resolve(true, false, 3, ready, playing, suppressed,
                true, queued: true, reconnecting: true, stalled: true);
            Assert.That(phase, Is.EqualTo(expected));
            Assert.That(PlaybackPresentation.CanRetry(phase), Is.False);
        }

        [Test]
        public void TerminalFailureCanBeRetriedButEndedOrStoppedTracksCannot()
        {
            Assert.That(PlaybackPresentation.CanRetry(PlaybackPresentation.Resolve(
                true, true, 1, false, false, false, true, false)), Is.True);
            Assert.That(PlaybackPresentation.CanRetry(PlaybackPresentation.Resolve(
                true, false, 4, false, false, false, true, false)), Is.False);
            Assert.That(PlaybackPresentation.CanRetry(PlaybackPresentation.Resolve(
                false, false, 1, false, false, false, false, false)), Is.False);
        }

        [TestCase(true, 0, 100, 19.9, false)]
        [TestCase(true, 0, 100, 20, true)]
        [TestCase(true, 50, 100, 30, true)]
        [TestCase(true, 100, 100, 30, false)]
        [TestCase(false, 50, 100, 30, false)]
        [TestCase(true, 50, 100, 0, false)]
        public void StallRequiresAnUnfinishedTransferWithoutProgressForTwentySeconds(
            bool receiving, long received, long size, double seconds, bool expected)
        {
            Assert.That(PlaybackPresentation.IsStalled(receiving, received, size,
                TimeSpan.FromSeconds(seconds)), Is.EqualTo(expected));
        }

        [Test]
        public void NewBytesClearStallAndRestoreOrdinaryBuffering()
        {
            bool stalled = PlaybackPresentation.IsStalled(true, 50, 100, TimeSpan.Zero);
            var phase = PlaybackPresentation.Resolve(true, false, 2, true, false, false,
                true, queued: false, stalled: stalled);
            Assert.That(phase, Is.EqualTo(PlaybackPhase.Buffering));
            Assert.That(PlaybackPresentation.CanRetry(phase), Is.False);
        }
    }
}
