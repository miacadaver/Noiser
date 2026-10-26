using Common.Playback;
using NUnit.Framework;
using System.Linq;

namespace UnitTestCommon
{
    public class AlbumSessionMetadataTests
    {
        private const string Path = "Music/Artist/Folder album/01 - Song.mp3";

        [TestCase(Path)]
        [TestCase("Artist - Filename album - 01 - Song.mp3")]
        public void GuessedAlbumRemainsAvailableForDisplayButIsNotAnEmbeddedAlbum(string path)
        {
            var metadata = TrackMetadataFallback.FromPath(path);

            Assert.That(metadata.Album, Is.Not.Null.And.Not.Empty);
            Assert.That(metadata.EmbeddedAlbum, Is.Null);
        }

        [Test]
        public void OtherTagsAndDurationUpdatesDoNotConfirmTheFolderAlbum()
        {
            var metadata = TrackMetadataFallback.FromPath(Path)
                .WithEmbedded(new TrackMetadataValues { DurationMilliseconds = 120000 })
                .WithEmbedded(new TrackMetadataValues { Title = "Tagged song", Artist = "Tagged artist" });

            Assert.That(metadata.Album, Is.EqualTo("Folder album"));
            Assert.That(metadata.EmbeddedAlbum, Is.Null);
            Assert.That(metadata.Title, Is.EqualTo("Tagged song"));
        }

        [TestCase("Tagged album")]
        [TestCase("Folder album")]
        public void ParsedAlbumIsConfirmedEvenWhenItMatchesTheFolder(string album)
        {
            var metadata = TrackMetadataFallback.FromPath(Path)
                .WithEmbedded(new TrackMetadataValues { Album = " " + album + "\0 " })
                .WithEmbedded(new TrackMetadataValues { DurationMilliseconds = 120000 });

            Assert.That(metadata.Album, Is.EqualTo(album));
            Assert.That(metadata.EmbeddedAlbum, Is.EqualTo(album));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" \0 ")]
        public void MissingOrEmptyAlbumTagsNeverPublishTheGuess(string? album)
        {
            var metadata = TrackMetadataFallback.FromPath(Path)
                .WithEmbedded(new TrackMetadataValues { Title = "Tagged song", Album = album });

            Assert.That(metadata.Album, Is.EqualTo("Folder album"));
            Assert.That(metadata.EmbeddedAlbum, Is.Null);
        }

        [Test]
        public void PartialUpdatesPreserveConfirmedAlbumWithoutLeakingItToTheNextTrack()
        {
            var first = TrackMetadataFallback.FromPath(Path)
                .WithEmbedded(new TrackMetadataValues { Album = "Tagged album" })
                .WithEmbedded(new TrackMetadataValues { Album = "\0", DurationMilliseconds = 120000 });
            var next = TrackMetadataFallback.FromPath("Music/Artist/Another folder/02 - Next.mp3");

            Assert.That(first.EmbeddedAlbum, Is.EqualTo("Tagged album"));
            Assert.That(next.Album, Is.EqualTo("Another folder"));
            Assert.That(next.EmbeddedAlbum, Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void QueueCopiesAndUpdatesPreserveAlbumProvenance(bool tagged)
        {
            var metadata = TrackMetadataFallback.FromPath(Path);
            if (tagged) metadata = metadata.WithEmbedded(new TrackMetadataValues { Album = "Tagged album" });
            string? expected = tagged ? "Tagged album" : null;
            var entry = new PlaybackQueueEntry("peer", Path, 100, metadata, id: "current");
            var queue = new PlaybackQueue();
            queue.Replace(new[] { entry });
            queue.AddNext(new PlaybackQueueEntry("peer", Path, 100, metadata, id: "next"));
            queue.AddLast(new PlaybackQueueEntry("peer", Path, 100, metadata, id: "last"));
            queue.Current!.UpdateMetadata(metadata.WithEmbedded(new TrackMetadataValues { DurationMilliseconds = 120000 }));

            Assert.That(queue.Entries.Select(item => item.Metadata.EmbeddedAlbum),
                Is.EqualTo(new[] { expected, expected, expected }));
            Assert.That(PlaybackQueue.Order(queue.Entries).Select(item => item.Metadata.EmbeddedAlbum),
                Is.EqualTo(new[] { expected, expected, expected }));
            Assert.That(queue.Current.Metadata.Album, Is.EqualTo(tagged ? "Tagged album" : "Folder album"));
        }
    }
}
