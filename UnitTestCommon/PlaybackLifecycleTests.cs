using Common.Playback;
using NUnit.Framework;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UnitTestCommon
{
    public class PlaybackLifecycleTests
    {
        [Test]
        public void ReplayedIntentIsConsumedOnlyOnce()
        {
            var mailbox = new PlaybackRequestMailbox<object>();
            var request = new object();
            string key = mailbox.Publish(request);
            Assert.That(mailbox.TryTake(key, out var result), Is.True);
            Assert.That(result, Is.SameAs(request));
            Assert.That(mailbox.TryTake(key, out _), Is.False);
        }

        [Test]
        public void RapidSelectionSupersedesOlderPendingRequests()
        {
            var mailbox = new PlaybackRequestMailbox<string>();
            string older = mailbox.Publish("old peer/file");
            string latest = mailbox.Publish("new peer/file");
            Assert.That(mailbox.TryTake(older, out _), Is.False);
            Assert.That(mailbox.TryTake(latest, out var request), Is.True);
            Assert.That(request, Is.EqualTo("new peer/file"));
        }

        [Test]
        public void ProcessRecreationCannotRestoreADeadTransfer()
        {
            var previousProcess = new PlaybackRequestMailbox<string>();
            string oldIntent = previousProcess.Publish("old peer/file");
            var newProcess = new PlaybackRequestMailbox<string>();
            Assert.That(newProcess.TryTake(oldIntent, out _), Is.False);
            Assert.That(newProcess.TryTake(null, out _), Is.False);
        }

        [Test]
        public void UnknownIntentDoesNotConsumeTheUsersSelection()
        {
            var mailbox = new PlaybackRequestMailbox<string>();
            string key = mailbox.Publish("chosen");
            Assert.That(mailbox.TryTake("unrecognized", out _), Is.False);
            Assert.That(mailbox.TryTake(key, out var result), Is.True);
            Assert.That(result, Is.EqualTo("chosen"));
        }

        [Test]
        public async Task ConcurrentDeliveryStartsAtMostOneRequest()
        {
            var mailbox = new PlaybackRequestMailbox<object>();
            string key = mailbox.Publish(new object());
            int accepted = 0;
            await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(() =>
            {
                if (mailbox.TryTake(key, out _)) Interlocked.Increment(ref accepted);
            })));
            Assert.That(accepted, Is.EqualTo(1));
        }

        [TestCase(false, false, 1, false, false, false, false, false, PlaybackPhase.Stopped)]
        [TestCase(true, true, 2, true, false, false, true, false, PlaybackPhase.Error)]
        [TestCase(true, true, 1, false, false, false, false, true, PlaybackPhase.Error)]
        [TestCase(true, false, 4, false, false, false, true, false, PlaybackPhase.Stopped)]
        [TestCase(true, false, 2, true, false, false, false, true, PlaybackPhase.Queued)]
        [TestCase(true, false, 2, true, false, false, false, false, PlaybackPhase.Connecting)]
        [TestCase(true, false, 2, true, false, false, true, false, PlaybackPhase.Buffering)]
        [TestCase(true, false, 3, true, true, false, true, false, PlaybackPhase.Playing)]
        [TestCase(true, false, 3, false, false, false, true, false, PlaybackPhase.Paused)]
        [TestCase(true, false, 3, true, false, true, true, false, PlaybackPhase.Paused)]
        [TestCase(true, false, 2, false, false, false, false, true, PlaybackPhase.Paused)]
        public void ActualPlayerStateControlsPresentation(bool track, bool error, int state, bool ready,
            bool playing, bool suppressed, bool bytes, bool queued, PlaybackPhase expected)
        {
            Assert.That(PlaybackPresentation.Resolve(track, error, state, ready, playing, suppressed, bytes, queued), Is.EqualTo(expected));
        }
    }
}
