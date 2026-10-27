using Common.Browse;
using NUnit.Framework;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UnitTestCommon
{
    public class FolderBookmarkTests
    {
        private string directory = null!, path = null!;
        private FolderBookmarkStore store = null!;
        [SetUp] public void Setup()
        {
            directory = Path.Combine(Path.GetTempPath(), "noiser-bookmarks-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory); path = Path.Combine(directory, "bookmarks.json"); store = new FolderBookmarkStore(path);
        }
        [TearDown] public void Cleanup() => Directory.Delete(directory, true);
        [Test] public void NewStoreRestoresUnicodePeerPathLabelAndStableIdentity()
        {
            store.Add("peer 雪", @"Music\Björk\雪");
            var first = store.All().Single();
            var restored = new FolderBookmarkStore(path).All().Single();
            Assert.That(restored.Id, Is.EqualTo(first.Id));
            Assert.That(restored.Username, Is.EqualTo("peer 雪"));
            Assert.That(restored.FolderPath, Is.EqualTo(@"Music\Björk\雪"));
            Assert.That(restored.Label, Is.EqualTo("雪"));
        }
        [Test] public void DuplicateDoesNotReplaceCustomLabelOrOriginalNetworkPath()
        {
            store.Add("peer", @"Music\Album\");
            store.Rename(store.All().Single().Id, "Favourite album");
            Assert.That(store.Add("peer", "Music/Album"), Is.False);
            Assert.That(store.All().Single().Label, Is.EqualTo("Favourite album"));
            Assert.That(store.All().Single().FolderPath, Is.EqualTo(@"Music\Album\"));
        }
        [Test] public void SamePathOnDifferentPeersAndCaseDistinctPathsStaySeparate()
        {
            store.Add("peer1", "Music"); store.Add("peer2", "Music"); store.Add("peer1", "music");
            Assert.That(store.All().Count, Is.EqualTo(3));
            Assert.That(store.Contains("peer1", "Music"), Is.True);
            Assert.That(store.Contains("peer3", "Music"), Is.False);
        }
        [Test] public void RootFolderHasUsefulLabelAndCanBeRemovedDurably()
        {
            store.Add("peer", ""); var entry = store.All().Single();
            Assert.That(entry.Label, Is.EqualTo("peer"));
            Assert.That(entry.FolderPath, Is.Empty);
            store.Remove(entry.Id);
            Assert.That(new FolderBookmarkStore(path).All(), Is.Empty);
        }
        [Test] public void RenameAndRemoveUseStableIdsAndPreserveOtherBookmarks()
        {
            store.Add("peer", "A"); store.Add("peer", "B");
            var entry = store.Find("peer", "A")!;
            store.Rename(entry.Id, "  Custom name  "); store.Remove(store.Find("peer", "B")!.Id);
            var restored = new FolderBookmarkStore(path).All().Single();
            Assert.That(restored.Id, Is.EqualTo(entry.Id)); Assert.That(restored.Label, Is.EqualTo("Custom name"));
            Assert.That(restored.FolderPath, Is.EqualTo("A"));
        }
        [Test] public void QueriesCannotMutateStoredEntries()
        {
            store.Add("peer", "A"); store.All().Single().FolderPath = "changed"; store.Find("peer", "A")!.Label = "changed";
            Assert.That(store.All().Single().FolderPath, Is.EqualTo("A")); Assert.That(store.All().Single().Label, Is.EqualTo("A"));
        }
        [TestCase("")] [TestCase("   ")] [TestCase("bad\0name")]
        public void InvalidRenamePreservesExistingFileAndMemory(string name)
        {
            store.Add("peer", "A"); string before = File.ReadAllText(path);
            Assert.Throws<InvalidDataException>(() => store.Rename(store.All().Single().Id, name));
            Assert.That(File.ReadAllText(path), Is.EqualTo(before)); Assert.That(store.All().Single().Label, Is.EqualTo("A"));
        }
        [Test] public void OversizedNameOrPathCannotReplaceValidData()
        {
            store.Add("peer", "A"); var id = store.All().Single().Id;
            Assert.Throws<InvalidDataException>(() => store.Rename(id, new string('a', 201)));
            Assert.Throws<InvalidDataException>(() => store.Add("peer", new string('a', 16385)));
            Assert.That(new FolderBookmarkStore(path).All().Single().Id, Is.EqualTo(id));
        }
        [TestCase("{broken")] [TestCase("null")] [TestCase("{\"Version\":99}")] [TestCase("{\"Version\":1,\"Entries\":null}")]
        public void CorruptOrFutureFileDoesNotCrashAndAllowsNewBookmark(string json)
        {
            File.WriteAllText(path, json); Assert.That(store.All(), Is.Empty);
            store.Add("peer", "A"); Assert.That(new FolderBookmarkStore(path).All().Single().FolderPath, Is.EqualTo("A"));
        }
        [Test] public void LastCommittedBackupRecoversFromDamagedMainFile()
        {
            store.Add("peer", "A"); store.Add("peer", "B");
            File.WriteAllText(path, "broken");
            var restored = new FolderBookmarkStore(path); Assert.That(restored.All().Single().FolderPath, Is.EqualTo("A"));
            restored.Add("peer", "C"); Assert.That(new FolderBookmarkStore(path).All().Count, Is.EqualTo(2));
        }
        [Test] public void InterruptedTemporaryFileDoesNotOverrideCommittedList()
        {
            store.Add("peer", "A"); File.WriteAllText(path + ".tmp", "unfinished");
            Assert.That(new FolderBookmarkStore(path).All().Single().FolderPath, Is.EqualTo("A"));
            store.Add("peer", "B"); Assert.That(File.Exists(path + ".tmp"), Is.False);
        }
        [Test] public void OversizedDiskFileIsIgnored()
        {
            using (var file = File.Create(path)) file.SetLength(FolderBookmarkStore.MaximumBytes + 1);
            Assert.That(store.All(), Is.Empty);
        }
        [Test] public void StorageBoundsRejectOversizedReplacementBeforeWriting()
        {
            for (int i = 0; i < 5; i++) store.Add("peer", i + new string('雪', 16000));
            string before = File.ReadAllText(path);
            Assert.Throws<InvalidDataException>(() => store.Add("peer", "sixth" + new string('雪', 16000)));
            Assert.That(File.ReadAllText(path), Is.EqualTo(before)); Assert.That(store.All().Count, Is.EqualTo(5));
        }
        [Test] public void InvalidDuplicateIdentitiesInJsonAreIgnored()
        {
            var document = new FolderBookmarkStore.Document();
            document.Entries.Add(new FolderBookmark { Id = "one", Username = "peer", FolderPath = "Music", Label = "One" });
            document.Entries.Add(new FolderBookmark { Id = "two", Username = "peer", FolderPath = "Music/", Label = "Two" });
            File.WriteAllText(path, JsonSerializer.Serialize(document)); Assert.That(store.All(), Is.Empty);
        }
        [Test] public void MissingIdsAreHarmless()
        {
            store.Add("peer", "A");
            Assert.That(store.Rename("missing", "name"), Is.False); Assert.That(store.Remove("missing"), Is.False);
            Assert.That(store.All().Single().FolderPath, Is.EqualTo("A"));
        }
    }
}
