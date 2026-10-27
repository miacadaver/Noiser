using Common.Playback;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UnitTestCommon
{
    public class SavedPlaybackSessionTests
    {
        private string directory = null!;
        private string path = null!;
        private PlaybackSessionStore store = null!;

        [SetUp]
        public void Setup()
        {
            directory = Path.Combine(Path.GetTempPath(), "noiser-recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "queue.json");
            store = new PlaybackSessionStore(path);
        }

        [TearDown]
        public void Cleanup() => Directory.Delete(directory, true);

        private static PlaybackQueue Queue()
        {
            var queue = new PlaybackQueue();
            var first = new PlaybackQueueEntry("peer", @"Music\Album\01 雪.mp3", 12000, id: "first",
                wasFilenameLatin1Decoded: true, wasFolderLatin1Decoded: true);
            var second = new PlaybackQueueEntry("peer", @"Music\Album\02 Song.mp3", 25000, id: "second");
            queue.Replace(new[] { first, second }, second.Id);
            return queue;
        }

        [Test]
        public void NewProcessRestoresOrderCurrentSourceAndEncodingFlags()
        {
            store.Save(SavedPlaybackSession.Capture("account", Queue()));
            var restored = new PlaybackSessionStore(path).Load("account")!.ToQueue();
            Assert.That(restored.Entries.Select(entry => entry.Id), Is.EqualTo(new[] { "first", "second" }));
            Assert.That(restored.Current!.RemotePath, Is.EqualTo(@"Music\Album\02 Song.mp3"));
            Assert.That(restored.Current.Username, Is.EqualTo("peer"));
            Assert.That(restored.Current.Size, Is.EqualTo(25000));
            Assert.That(restored.Entries[0].WasFilenameLatin1Decoded, Is.True);
            Assert.That(restored.Entries[0].WasFolderLatin1Decoded, Is.True);
        }

        [Test]
        public void RestoredEntriesArePendingWithoutPlayingFailedOrTransferState()
        {
            var queue = Queue();
            queue.Current!.MarkFailed("old failure");
            store.Save(SavedPlaybackSession.Capture("account", queue));
            var restored = store.Load("account")!.ToQueue();
            Assert.That(restored.Current!.State, Is.EqualTo(PlaybackQueueEntryState.Pending));
            Assert.That(restored.Current.Error, Is.Null);
            Assert.That(restored.Entries.All(entry => entry.State == PlaybackQueueEntryState.Pending), Is.True);
        }

        [Test]
        public void SchemaHasNoPositionPlayIntentArtworkOrTemporaryBuffer()
        {
            var queue = Queue();
            queue.Current!.UpdateMetadata(queue.Current.Metadata.WithResolvedTags(new TrackMetadataValues
            {
                Title = "Embedded song", Artist = "Artist", Album = "Album", DurationMilliseconds = 90000,
                ArtworkData = new byte[] { 1, 2, 3, 4 }
            }));
            store.Save(SavedPlaybackSession.Capture("account", queue));
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.That(json.RootElement.EnumerateObject().Select(property => property.Name),
                Is.EquivalentTo(new[] { "Version", "Account", "CurrentId", "Entries" }));
            var entry = json.RootElement.GetProperty("Entries")[1];
            Assert.That(entry.EnumerateObject().Select(property => property.Name),
                Is.EquivalentTo(new[] { "Id", "Username", "RemotePath", "Size", "FilenameDecoded", "FolderDecoded", "Title", "Artist", "Album" }));
            var restored = store.Load("account")!.ToQueue().Current!;
            Assert.That(restored.Metadata.Title, Is.EqualTo("Embedded song"));
            Assert.That(restored.Metadata.Artist, Is.EqualTo("Artist"));
            Assert.That(restored.Metadata.ArtworkData, Is.Null);
            Assert.That(restored.Metadata.DurationMilliseconds, Is.Null);
        }

        [Test]
        public void RepeatedCheckpointDoesNotRewriteDisk()
        {
            var snapshot = SavedPlaybackSession.Capture("account", Queue());
            Assert.That(store.Save(snapshot), Is.True);
            Assert.That(store.Save(SavedPlaybackSession.Capture("account", Queue())), Is.False);
            var reopened = new PlaybackSessionStore(path);
            reopened.Load("account");
            Assert.That(reopened.Save(snapshot), Is.False);
        }

        [Test]
        public void SelectionAndQueueEditsSurviveAnotherProcess()
        {
            var queue = Queue();
            store.Save(SavedPlaybackSession.Capture("account", queue));
            queue.AddNext(new PlaybackQueueEntry("other peer", @"Other\Next.mp3", 99, id: "next"));
            queue.Select("next");
            queue.Remove("first", out _);
            store.Save(SavedPlaybackSession.Capture("account", queue));
            var restored = new PlaybackSessionStore(path).Load("account")!.ToQueue();
            Assert.That(restored.Current!.Id, Is.EqualTo("next"));
            Assert.That(restored.Entries.Select(entry => entry.Id), Is.EqualTo(new[] { "second", "next" }));
        }

        [Test]
        public void DuplicateSongsKeepDistinctStableQueueIdentities()
        {
            var queue = Queue();
            queue.AddLast(new PlaybackQueueEntry("peer", queue.Current!.RemotePath, 25000, id: "reprise"));
            queue.Select("reprise");
            store.Save(SavedPlaybackSession.Capture("account", queue));
            Assert.That(store.Load("account")!.ToQueue().Current!.Id, Is.EqualTo("reprise"));
        }

        [Test]
        public void FolderPreviewDoesNotMixPeersOrOtherFolders()
        {
            var queue = Queue();
            queue.AddLast(new PlaybackQueueEntry("other", @"Music\Album\03 Other.mp3", 30));
            queue.AddLast(new PlaybackQueueEntry("peer", @"Different\04 Song.mp3", 40));
            var saved = SavedPlaybackSession.Capture("account", queue);
            Assert.That(saved.FolderTracks().Select(entry => entry.Id), Is.EqualTo(new[] { "first", "second" }));
            Assert.That(SavedPlaybackSession.FolderOf("Music/Album/02 Song.mp3"), Is.EqualTo(@"Music\Album"));
            Assert.That(SavedPlaybackSession.FolderOf("Song.mp3"), Is.Empty);
        }

        [Test]
        public void AnotherAccountCannotRestoreTheQueue()
        {
            store.Save(SavedPlaybackSession.Capture("account", Queue()));
            Assert.That(store.Load("different account"), Is.Null);
            Assert.That(store.Load("Account"), Is.Null);
            Assert.That(store.Load("account"), Is.Not.Null);
        }

        [TestCase("{")]
        [TestCase("null")]
        [TestCase("{\"Version\":99}")]
        [TestCase("{\"Version\":99,\"Account\":\"account\",\"Entries\":[]}")]
        [TestCase("{\"Version\":1,\"Account\":\"account\",\"Entries\":null}")]
        public void CorruptMissingOrFutureDataDoesNotPreventStartup(string content)
        {
            Assert.That(store.Load("account"), Is.Null);
            File.WriteAllText(path, content);
            Assert.That(store.Load("account"), Is.Null);
        }

        [Test]
        public void OversizedFileIsIgnoredBeforeJsonParsing()
        {
            using (var file = File.Create(path)) file.SetLength(PlaybackSessionStore.MaximumBytes + 1);
            Assert.That(store.Load("account"), Is.Null);
        }

        [Test]
        public void ExcessiveMetadataIsRejectedBeforeAllocatingOrWritingJson()
        {
            var queue = new PlaybackQueue();
            string title = new string('雪', 4096);
            for (int i = 0; i < 200; i++)
                queue.AddLast(new PlaybackQueueEntry("peer", @"Music\" + i + ".mp3", 1,
                    TrackMetadataFallback.FromPath("Song.mp3").WithResolvedTags(new TrackMetadataValues { Title = title })));
            Assert.Throws<InvalidDataException>(() => store.Save(SavedPlaybackSession.Capture("account", queue)));
            Assert.That(File.Exists(path), Is.False);
        }

        [Test]
        public void InterruptedTemporaryWritePreservesCommittedQueue()
        {
            store.Save(SavedPlaybackSession.Capture("account", Queue()));
            File.WriteAllText(path + ".tmp", "{interrupted");
            Assert.That(new PlaybackSessionStore(path).Load("account")!.CurrentId, Is.EqualTo("second"));
        }

        [Test]
        public void InvalidReplacementDoesNotOverwriteCommittedFile()
        {
            store.Save(SavedPlaybackSession.Capture("account", Queue()));
            var invalid = SavedPlaybackSession.Capture("account", Queue());
            invalid.CurrentId = "not in queue";
            Assert.Throws<InvalidDataException>(() => store.Save(invalid));
            Assert.That(new PlaybackSessionStore(path).Load("account")!.CurrentId, Is.EqualTo("second"));
        }

        [Test]
        public void DuplicateIdsOrUnsupportedTracksAreRejected()
        {
            var saved = SavedPlaybackSession.Capture("account", Queue());
            saved.Entries[1].Id = saved.Entries[0].Id;
            Assert.Throws<InvalidDataException>(() => saved.ToQueue());
            saved = SavedPlaybackSession.Capture("account", Queue());
            saved.Entries[0].RemotePath = @"Music\not an mp3.flac";
            Assert.Throws<InvalidDataException>(() => saved.ToQueue());
        }

        [Test]
        public void StopOrClearRemovesCommittedAndInterruptedSnapshots()
        {
            var snapshot = SavedPlaybackSession.Capture("account", Queue());
            store.Save(snapshot);
            File.WriteAllText(path + ".tmp", "interrupted");
            store.Clear();
            Assert.That(File.Exists(path), Is.False);
            Assert.That(File.Exists(path + ".tmp"), Is.False);
            Assert.That(new PlaybackSessionStore(path).Load("account"), Is.Null);
            Assert.That(store.Save(snapshot), Is.True);
        }
    }
}
