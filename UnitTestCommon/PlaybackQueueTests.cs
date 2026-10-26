using Common.Playback;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;

namespace UnitTestCommon
{
    [TestFixture]
    public class PlaybackQueueTests
    {
        [Test]
        public void NaturalFilenameOrderingTreatsDigitRunsNumericallyAndIgnoresCase()
        {
            var ordered = PlaybackQueue.Order(new[]
            {
                Entry("Peer", "Music\\Album\\10 - Finale.mp3"),
                Entry("Peer", "Music\\Album\\2 - Second.mp3"),
                Entry("Peer", "Music\\Album\\1 - First.mp3"),
                Entry("Peer", "Music\\Album\\Cover.JPG"),
                Entry("Peer", "Music\\Album\\Bonus.mp3"),
                Entry("Peer", "Music\\Album\\disc 2 - 1.mp3")
            });

            Assert.That(ordered.Select(track => track.DisplayFilename), Is.EqualTo(new[]
            { "1 - First.mp3", "2 - Second.mp3", "10 - Finale.mp3", "Bonus.mp3", "disc 2 - 1.mp3" }));
        }

        [Test]
        public void AvailableTrackAndDiscNumbersSortBeforeFilenameFallback()
        {
            var tracks = new[]
            {
                Entry("Peer", "Album\\01 - First.mp3", track: 1, disc: 1),
                Entry("Peer", "Album\\01 - Second Disc.mp3", track: 1, disc: 2),
                Entry("Peer", "Album\\Bonus.mp3", track: null, disc: null),
                Entry("Peer", "Album\\02 - Second.mp3", track: 2, disc: 1)
            };

            Assert.That(PlaybackQueue.Order(tracks).Select(track => track.RemotePath), Is.EqualTo(new[]
            { "Album\\01 - First.mp3", "Album\\02 - Second.mp3", "Album\\01 - Second Disc.mp3", "Album\\Bonus.mp3" }));
        }

        [Test]
        public void ReplaceStartsAtSelectedTrackAndDoesNotMutateCallerEntries()
        {
            var source = new List<PlaybackQueueEntry>
            { Entry("Peer", "Album\\1.mp3", id: "one"), Entry("Peer", "Album\\2.mp3", id: "two") };
            var queue = new PlaybackQueue();

            Assert.That(queue.Replace(source, "two"), Is.True);
            source.Clear();

            Assert.That(queue.Entries.Select(entry => entry.Id), Is.EqualTo(new[] { "one", "two" }));
            Assert.That(queue.Current?.Id, Is.EqualTo("two"));
            Assert.That(queue.CurrentIndex, Is.EqualTo(1));
            Assert.That(queue.HasPrevious, Is.True);
            Assert.That(queue.HasNext, Is.False);
        }

        [Test]
        public void InvalidReplacementLeavesExistingQueueUnchanged()
        {
            var queue = new PlaybackQueue();
            queue.Replace(new[] { Entry("Peer", "Album\\1.mp3", id: "one") });

            Assert.That(queue.Replace(new[] { Entry("Peer", "Other\\2.mp3", id: "two") }, "missing"), Is.False);
            Assert.That(queue.Current?.Id, Is.EqualTo("one"));
            Assert.That(() => queue.Replace(new[]
            { Entry("Peer", "Other\\2.mp3", id: "same"), Entry("Peer", "Other\\3.mp3", id: "same") }),
                Throws.ArgumentException);
            Assert.That(queue.Current?.Id, Is.EqualTo("one"));
        }

        [Test]
        public void NextPreviousAndCurrentRemovalKeepIndexConsistent()
        {
            var queue = new PlaybackQueue();
            queue.Replace(new[]
            { Entry("Peer", "A\\1.mp3", id: "one"), Entry("Peer", "A\\2.mp3", id: "two"), Entry("Peer", "A\\3.mp3", id: "three") }, "two");

            Assert.That(queue.MoveNext()?.Id, Is.EqualTo("three"));
            Assert.That(queue.MoveNext(), Is.Null);
            bool removedCurrent;
            Assert.That(queue.Remove("three", out removedCurrent)?.Id, Is.EqualTo("two"));
            Assert.That(removedCurrent, Is.True);
            Assert.That(queue.CurrentIndex, Is.EqualTo(1));
            Assert.That(queue.MovePrevious()?.Id, Is.EqualTo("one"));
            Assert.That(queue.Remove("one", out removedCurrent)?.Id, Is.EqualTo("two"));
            Assert.That(removedCurrent, Is.True);
            Assert.That(queue.CurrentIndex, Is.Zero);
        }

        [Test]
        public void AddNextAndAddLastUseDistinctStableEntriesAndRejectDuplicates()
        {
            var queue = new PlaybackQueue();
            var current = Entry("Peer", "A\\1.mp3", id: "current");
            var next = Entry("Peer", "A\\2.mp3", id: "next");
            var last = Entry("Peer", "A\\3.mp3", id: "last");
            queue.Replace(new[] { current });

            Assert.That(queue.AddNext(next), Is.True);
            Assert.That(queue.AddNext(next), Is.False);
            Assert.That(queue.AddLast(last), Is.True);
            Assert.That(queue.Entries.Select(entry => entry.Id), Is.EqualTo(new[] { "current", "next", "last" }));
            Assert.That(queue.Current?.Id, Is.EqualTo("current"));
        }

        [Test]
        public void AddingToEmptyQueueSelectsFirstAndUnsupportedTracksDoNotChangeIt()
        {
            var queue = new PlaybackQueue();
            var unsupported = Entry("Peer", "A\\cover.jpg", id: "cover");
            Assert.That(queue.AddLast(unsupported), Is.False);
            Assert.That(queue.Current, Is.Null);

            var first = Entry("Peer", "A\\1.mp3", id: "first");
            Assert.That(queue.AddLast(first), Is.True);
            Assert.That(queue.Current?.Id, Is.EqualTo("first"));
            Assert.That(queue.CurrentIndex, Is.Zero);
            Assert.That(queue.AddNext(unsupported), Is.False);
            Assert.That(queue.Entries.Select(entry => entry.Id), Is.EqualTo(new[] { "first" }));
        }

        [Test]
        public void SameRemoteFileCanAppearAsTwoIntentionalQueueEntries()
        {
            var queue = new PlaybackQueue();
            queue.Replace(new[]
            { Entry("Peer", "A\\1.mp3", id: "first-play"), Entry("Peer", "A\\1.mp3", id: "second-play") });

            Assert.That(queue.Entries.Select(entry => entry.RemotePath), Is.EqualTo(new[] { "A\\1.mp3", "A\\1.mp3" }));
            Assert.That(queue.Select("second-play")?.Id, Is.EqualTo("second-play"));
            Assert.That(queue.CurrentIndex, Is.EqualTo(1));
        }

        [Test]
        public void ClearAndStaleSelectionCannotKeepOldCurrentEntry()
        {
            var queue = new PlaybackQueue();
            queue.Replace(new[] { Entry("Peer", "A\\1.mp3", id: "old") });
            Assert.That(queue.Select("other"), Is.Null);
            Assert.That(queue.Current?.Id, Is.EqualTo("old"));
            queue.Clear();
            Assert.That(queue.Current, Is.Null);
            Assert.That(queue.CurrentIndex, Is.EqualTo(-1));
            Assert.That(queue.HasNext || queue.HasPrevious, Is.False);
        }

        private static PlaybackQueueEntry Entry(string peer, string path, int? track = 0, int? disc = null, string? id = null)
        {
            var metadata = TrackMetadataFallback.FromPath(path);
            if (track.HasValue && track.Value > 0)
                metadata = metadata.WithEmbedded(new TrackMetadataValues { TrackNumber = track.Value });
            return new PlaybackQueueEntry(peer, path, 100, metadata, disc, id);
        }
    }
}
