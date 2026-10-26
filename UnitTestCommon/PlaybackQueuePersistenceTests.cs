using Common.Playback;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace UnitTestCommon
{
    public class PlaybackQueuePersistenceTests
    {
        private static PlaybackQueue Queue()
        {
            var queue = new PlaybackQueue();
            queue.Replace(new[]
            {
                new PlaybackQueueEntry("peer 🌙", @"Music\Björk\Album\10 - Jóga.mp3", 64000,
                    discNumber: 2, id: "first", wasFilenameLatin1Decoded: true, wasFolderLatin1Decoded: true),
                new PlaybackQueueEntry("other peer", "Music/Artist/Album/02 - Next.MP3", 128000, id: "second")
            }, "second");
            return queue;
        }

        [TestCase(PlaybackRepeatMode.Off, true)]
        [TestCase(PlaybackRepeatMode.All, true)]
        [TestCase(PlaybackRepeatMode.One, true)]
        [TestCase(PlaybackRepeatMode.Off, false)]
        public void RestoresSourceOrderSelectedTrackAndPlaybackOptions(PlaybackRepeatMode repeat, bool autoplay)
        {
            var original = Queue();
            string? json = PlaybackQueuePersistence.Serialize(original, repeat, autoplay);
            Assert.That(PlaybackQueuePersistence.TryRestore(json, out var saved), Is.True);
            var restored = saved!.Queue;
            Assert.Multiple(() =>
            {
                Assert.That(restored.Entries.Select(entry => entry.Id), Is.EqualTo(new[] { "first", "second" }));
                Assert.That(restored.Current!.Id, Is.EqualTo("second"));
                Assert.That(restored.Entries[0].Username, Is.EqualTo("peer 🌙"));
                Assert.That(restored.Entries[0].RemotePath, Is.EqualTo(original.Entries[0].RemotePath));
                Assert.That(restored.Entries[0].Size, Is.EqualTo(64000));
                Assert.That(restored.Entries[0].DiscNumber, Is.EqualTo(2));
                Assert.That(restored.Entries[0].WasFilenameLatin1Decoded, Is.True);
                Assert.That(restored.Entries[0].WasFolderLatin1Decoded, Is.True);
                Assert.That(saved.RepeatMode, Is.EqualTo(repeat));
                Assert.That(saved.Autoplay, Is.EqualTo(autoplay));
            });
        }

        [Test]
        public void RestoreDoesNotTrustOldTagsErrorsOrPlayingState()
        {
            var queue = Queue();
            queue.Current!.UpdateMetadata(queue.Current.Metadata.WithResolvedTags(new TrackMetadataValues
            {
                Title = "Tagged title", Album = "Tagged album", DurationMilliseconds = 120000,
                ArtworkData = new byte[] { 1, 2, 3 }
            }));
            queue.Current.MarkPlaying();
            queue.Entries[0].MarkFailed("Temporary error");
            string? json = PlaybackQueuePersistence.Serialize(queue, PlaybackRepeatMode.Off, true);
            Assert.That(json, Does.Not.Contain("Tagged title").And.Not.Contain("Tagged album")
                .And.Not.Contain("ArtworkData").And.Not.Contain("DurationMilliseconds").And.Not.Contain("Temporary error"));
            Assert.That(PlaybackQueuePersistence.TryRestore(json, out var saved), Is.True);
            foreach (var entry in saved!.Queue.Entries)
            {
                Assert.That(entry.State, Is.EqualTo(PlaybackQueueEntryState.Pending));
                Assert.That(entry.Error, Is.Null);
                Assert.That(entry.Metadata.IsTagReadComplete, Is.False);
                Assert.That(entry.Metadata.EmbeddedAlbum, Is.Null);
                Assert.That(entry.Metadata.ArtworkData, Is.Null);
            }
        }

        [Test]
        public void EditsAndCurrentRemovalPersistWithoutResorting()
        {
            var queue = Queue();
            queue.AddNext(new PlaybackQueueEntry("peer", "03.mp3", 100, id: "next"));
            queue.AddLast(new PlaybackQueueEntry("peer", "01.mp3", 100, id: "last"));
            queue.Remove("second", out _);
            Assert.That(PlaybackQueuePersistence.TryRestore(
                PlaybackQueuePersistence.Serialize(queue, PlaybackRepeatMode.All, true), out var saved), Is.True);
            Assert.That(saved!.Queue.Entries.Select(entry => entry.Id), Is.EqualTo(new[] { "first", "next", "last" }));
            Assert.That(saved.Queue.Current!.Id, Is.EqualTo("next"));
            queue.Clear();
            Assert.That(PlaybackQueuePersistence.Serialize(queue, PlaybackRepeatMode.All, true), Is.Null);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("{")]
        [TestCase("null")]
        [TestCase("{}")]
        [TestCase("[]")]
        [TestCase("{\"Version\":2}")]
        public void InvalidOrUnsupportedSnapshotsAreIgnored(string? json)
        {
            Assert.That(PlaybackQueuePersistence.TryRestore(json, out var saved), Is.False);
            Assert.That(saved, Is.Null);
        }

        [TestCase("version")]
        [TestCase("selection")]
        [TestCase("repeat")]
        [TestCase("autoplay")]
        [TestCase("empty")]
        [TestCase("null entry")]
        [TestCase("duplicate")]
        [TestCase("peer")]
        [TestCase("path")]
        [TestCase("nul path")]
        [TestCase("size")]
        [TestCase("disc")]
        public void RejectsInvalidDataAsAWholeInsteadOfRestoringADifferentQueue(string change)
        {
            var root = JsonNode.Parse(PlaybackQueuePersistence.Serialize(Queue(), PlaybackRepeatMode.All, true)!)!;
            var entries = root["Entries"]!.AsArray();
            switch (change)
            {
                case "version": root["Version"] = 99; break;
                case "selection": root["SelectedId"] = "missing"; break;
                case "repeat": root["RepeatMode"] = 99; break;
                case "autoplay": root["Autoplay"] = false; break;
                case "empty": entries.Clear(); break;
                case "null entry": entries[0] = null; break;
                case "duplicate": entries[0]!["Id"] = "second"; break;
                case "peer": entries[0]!["Username"] = " "; break;
                case "path": entries[0]!["RemotePath"] = "song.exe"; break;
                case "nul path": entries[0]!["RemotePath"] = "song\0.mp3"; break;
                case "size": entries[0]!["Size"] = -1; break;
                case "disc": entries[0]!["DiscNumber"] = 1000; break;
            }
            Assert.That(PlaybackQueuePersistence.TryRestore(root.ToJsonString(), out _), Is.False);
        }

        [Test]
        public void BoundsStoredDataSizeAndQueueCount()
        {
            Assert.That(PlaybackQueuePersistence.TryRestore(
                new string(' ', PlaybackQueuePersistence.MaximumJsonLength + 1), out _), Is.False);
            var queue = new PlaybackQueue();
            queue.Replace(Enumerable.Range(0, PlaybackQueuePersistence.MaximumEntries + 1)
                .Select(index => new PlaybackQueueEntry("peer", index + ".mp3", 100)));
            Assert.Throws<InvalidDataException>(() => PlaybackQueuePersistence.Serialize(queue, PlaybackRepeatMode.Off, true));
        }

        [Test]
        public void SourceRevisionChangesForEditsButNotForPlaybackProgressOrTags()
        {
            var queue = Queue();
            long initial = queue.Revision;
            queue.Select(queue.Current!.Id);
            queue.Select("missing");
            queue.Remove("missing", out _);
            queue.Current.MarkPlaying();
            queue.Current.UpdateMetadata(queue.Current.Metadata.WithResolvedTags(new TrackMetadataValues
                { Title = "Tagged title", DurationMilliseconds = 120000 }));
            Assert.That(queue.Revision, Is.EqualTo(initial));
            queue.MovePrevious();
            Assert.That(queue.Revision, Is.GreaterThan(initial));
            initial = queue.Revision;
            queue.AddNext(new PlaybackQueueEntry("peer", "03.mp3", 100));
            Assert.That(queue.Revision, Is.GreaterThan(initial));
            initial = queue.Revision;
            queue.Clear();
            Assert.That(queue.Revision, Is.GreaterThan(initial));
            initial = queue.Revision;
            queue.Clear();
            Assert.That(queue.Revision, Is.EqualTo(initial));
        }

        [Test]
        public void RestoredControlsRespectAutoplayAndRepeatInvariant()
        {
            var controls = new PlaybackControls();
            controls.Restore(PlaybackRepeatMode.One, true);
            Assert.That(controls.RepeatMode, Is.EqualTo(PlaybackRepeatMode.One));
            controls.Restore(PlaybackRepeatMode.All, false);
            Assert.That(controls.Autoplay, Is.False);
            Assert.That(controls.RepeatMode, Is.EqualTo(PlaybackRepeatMode.Off));
        }
    }
}
