using Common.Playback;
using NUnit.Framework;
using System.Linq;

namespace UnitTestCommon
{
    public class MetadataPublicationTests
    {
        private const string Path = "Music/Guessed artist/Wrong folder album/01 - Guessed song.mp3";

        [Test]
        public void DurationUpdatesDuringSlowTagReadDoNotReleaseMetadata()
        {
            var metadata = TrackMetadataFallback.FromPath(Path);
            for (int i = 1; i <= 10; i++)
            {
                metadata = metadata.WithEmbedded(new TrackMetadataValues { DurationMilliseconds = i * 1000 });
                Assert.That(metadata.IsTagReadComplete, Is.False);
            }

            metadata = metadata.WithResolvedTags(new TrackMetadataValues
            {
                Title = "Real song", Artist = "Real artist", Album = "Real album"
            });

            Assert.Multiple(() =>
            {
                Assert.That(metadata.IsTagReadComplete, Is.True);
                Assert.That(metadata.Title, Is.EqualTo("Real song"));
                Assert.That(metadata.Artist, Is.EqualTo("Real artist"));
                Assert.That(metadata.EmbeddedAlbum, Is.EqualTo("Real album"));
                Assert.That(metadata.DurationMilliseconds, Is.EqualTo(10000));
            });
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" \0 ")]
        public void CompletedReadWithoutAlbumStillAllowsPublication(string? album)
        {
            var pending = TrackMetadataFallback.FromPath(Path);
            var resolved = pending.WithResolvedTags(new TrackMetadataValues
            {
                Title = "Real song", Artist = "Real artist", Album = album
            });

            Assert.That(pending.IsTagReadComplete, Is.False);
            Assert.That(resolved.IsTagReadComplete, Is.True);
            Assert.That(resolved.Title, Is.EqualTo("Real song"));
            Assert.That(resolved.Artist, Is.EqualTo("Real artist"));
            Assert.That(resolved.EmbeddedAlbum, Is.Null);
        }

        [Test]
        public void ConfirmedUntaggedTrackCanPublishItsFinalFilenameFallback()
        {
            var metadata = TrackMetadataFallback.FromPath(Path)
                .WithResolvedTags(new TrackMetadataValues());

            Assert.That(metadata.IsTagReadComplete, Is.True);
            Assert.That(metadata.Title, Is.EqualTo("Guessed song"));
            Assert.That(metadata.Artist, Is.EqualTo("Guessed artist"));
            Assert.That(metadata.EmbeddedAlbum, Is.Null);
        }

        [Test]
        public void ResolvedTrackStaysResolvedButNextTrackStartsPending()
        {
            var first = TrackMetadataFallback.FromPath(Path)
                .WithResolvedTags(new TrackMetadataValues { Album = "Real album" })
                .WithEmbedded(new TrackMetadataValues { DurationMilliseconds = 120000 });
            var next = TrackMetadataFallback.FromPath("Music/Next artist/Next folder/02 - Next.mp3");

            Assert.That(first.IsTagReadComplete, Is.True);
            Assert.That(first.EmbeddedAlbum, Is.EqualTo("Real album"));
            Assert.That(next.IsTagReadComplete, Is.False);
            Assert.That(next.EmbeddedAlbum, Is.Null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void QueueCopiesAndDurationUpdatesRetainTagReadState(bool resolved)
        {
            var metadata = TrackMetadataFallback.FromPath(Path);
            if (resolved) metadata = metadata.WithResolvedTags(new TrackMetadataValues());
            var queue = new PlaybackQueue();
            queue.Replace(new[] { new PlaybackQueueEntry("peer", Path, 100, metadata, id: "current") });
            queue.AddNext(new PlaybackQueueEntry("peer", Path, 100, metadata, id: "next"));
            queue.AddLast(new PlaybackQueueEntry("peer", Path, 100, metadata, id: "last"));
            queue.Current!.UpdateMetadata(metadata.WithEmbedded(new TrackMetadataValues { DurationMilliseconds = 120000 }));

            Assert.That(queue.Entries.Select(entry => entry.Metadata.IsTagReadComplete),
                Is.EqualTo(new[] { resolved, resolved, resolved }));
            Assert.That(PlaybackQueue.Order(queue.Entries).Select(entry => entry.Metadata.IsTagReadComplete),
                Is.EqualTo(new[] { resolved, resolved, resolved }));
        }

        [Test]
        public void CompletingCurrentTrackDoesNotReleaseTheNextTracksGuesses()
        {
            var queue = new PlaybackQueue();
            queue.Replace(new[]
            {
                new PlaybackQueueEntry("peer", Path, 100, id: "current"),
                new PlaybackQueueEntry("peer", "Music/Next artist/Next album/02 - Next.mp3", 100, id: "next")
            });
            queue.Current!.UpdateMetadata(queue.Current.Metadata.WithResolvedTags(new TrackMetadataValues
            {
                Title = "Real song", Artist = "Real artist", Album = "Real album"
            }));

            var entries = queue.Entries;
            Assert.That(entries[0].Metadata.IsTagReadComplete, Is.True);
            Assert.That(entries[0].Metadata.EmbeddedAlbum, Is.EqualTo("Real album"));
            Assert.That(entries[1].Metadata.IsTagReadComplete, Is.False);
            Assert.That(entries[1].Metadata.EmbeddedAlbum, Is.Null);
        }
    }
}
