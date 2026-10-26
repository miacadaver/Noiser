using Common.Playback;
using NUnit.Framework;

namespace UnitTestCommon
{
    public class TrackMetadataTests
    {
        [TestCase("Music/Radiohead/In Rainbows/03 - Nude.mp3")]
        [TestCase(@"Music\Radiohead\In Rainbows\03 - Nude.MP3")]
        public void MusicHierarchySuppliesMissingFields(string path)
        {
            var metadata = TrackMetadataFallback.FromPath(path);
            Assert.Multiple(() =>
            {
                Assert.That(metadata.Title, Is.EqualTo("Nude"));
                Assert.That(metadata.Artist, Is.EqualTo("Radiohead"));
                Assert.That(metadata.Album, Is.EqualTo("In Rainbows"));
                Assert.That(metadata.TrackNumber, Is.EqualTo(3));
                Assert.That(metadata.OriginalPath, Is.EqualTo(path));
                Assert.That(metadata.OriginalFilename, Does.StartWith("03 - Nude."));
                Assert.That(metadata.DurationMilliseconds, Is.Null);
                Assert.That(metadata.AlbumArtist, Is.Null);
            });
        }

        [Test]
        public void ArtistTitleFilenameDoesNotInventAlbum()
        {
            var metadata = TrackMetadataFallback.FromPath("Music/Radiohead - No Surprises.mp3");
            Assert.That(metadata.Artist, Is.EqualTo("Radiohead"));
            Assert.That(metadata.Title, Is.EqualTo("No Surprises"));
            Assert.That(metadata.Album, Is.Null);
        }

        [TestCase("Downloads/Someone/Loose Files/song.mp3")]
        [TestCase("Shared/song.mp3")]
        [TestCase("Music/Various Artists/Compilation/song.mp3")]
        public void AmbiguousPathsDoNotInventArtistOrAlbum(string path)
        {
            var metadata = TrackMetadataFallback.FromPath(path);
            Assert.That(metadata.Title, Is.EqualTo("song"));
            Assert.That(metadata.Artist, Is.Null);
            Assert.That(metadata.Album, Is.Null);
        }

        [TestCase("01. Jóga.mp3", "Jóga", 1)]
        [TestCase("02_你好 🌙.mp3", "你好 🌙", 2)]
        [TestCase("03 Nude.mp3", "Nude", 3)]
        [TestCase("1984.mp3", "1984", null)]
        [TestCase("01.mp3", "01", null)]
        public void TrackPrefixParsingPreservesUnusualTitles(string filename, string title, int? track)
        {
            var metadata = TrackMetadataFallback.FromPath(filename);
            Assert.That(metadata.Title, Is.EqualTo(title));
            Assert.That(metadata.TrackNumber, Is.EqualTo(track));
            Assert.That(metadata.OriginalFilename, Is.EqualTo(filename));
        }

        [Test]
        public void MultipleArtistSeparatorsAreAmbiguous()
        {
            var metadata = TrackMetadataFallback.FromPath("Artist - Title - Live.mp3");
            Assert.That(metadata.Title, Is.EqualTo("Artist - Title - Live"));
            Assert.That(metadata.Artist, Is.Null);
        }

        [TestCase("The Misfits - Walk Among Us - 06 - Hatebreeders.mp3", "The Misfits", "Walk Among Us", 6, "Hatebreeders")]
        [TestCase("The Misfits- Walk Among Us - 06 - Hatebreeders.mp3", "The Misfits", "Walk Among Us", 6, "Hatebreeders")]
        [TestCase("The Misfits - 06 - Hatebreeders.mp3", "The Misfits", null, 6, "Hatebreeders")]
        [TestCase("Artist - Album - 06 - Title - Live.mp3", "Artist", "Album", 6, "Title - Live")]
        public void FlatArtistAlbumTrackFilenameSuppliesInitialMetadata(
            string filename, string artist, string? album, int track, string title)
        {
            var metadata = TrackMetadataFallback.FromPath(filename);
            Assert.Multiple(() =>
            {
                Assert.That(metadata.Artist, Is.EqualTo(artist));
                Assert.That(metadata.Album, Is.EqualTo(album));
                Assert.That(metadata.TrackNumber, Is.EqualTo(track));
                Assert.That(metadata.Title, Is.EqualTo(title));
            });
        }

        [Test]
        public void FlatFilenameMetadataIsUsedBeforeEmbeddedTagsArrive()
        {
            var metadata = TrackMetadataFallback.FromPath("The Misfits - Walk Among Us - 06 - Hatebreeders.mp3");
            Assert.That(metadata.Title, Is.EqualTo("Hatebreeders"));
            Assert.That(metadata.Artist, Is.EqualTo("The Misfits"));
            Assert.That(metadata.Album, Is.EqualTo("Walk Among Us"));
        }

        [Test]
        public void ContradictingFilenameAndFolderDoesNotGuessAlbum()
        {
            var metadata = TrackMetadataFallback.FromPath("Music/Other artist/Album/Radiohead - Song.mp3");
            Assert.That(metadata.Artist, Is.EqualTo("Radiohead"));
            Assert.That(metadata.Album, Is.Null);
        }

        [Test]
        public void EmbeddedAlbumOverridesFolderAlbum()
        {
            var metadata = TrackMetadataFallback.FromPath("Music/Radiohead/In Rainbows/03 - Nude.mp3")
                .WithEmbedded(new TrackMetadataValues { Album = "Tagged album" });
            Assert.That(metadata.Album, Is.EqualTo("Tagged album"));
            Assert.That(metadata.Title, Is.EqualTo("Nude"));
        }

        [Test]
        public void EmbeddedFieldsOverrideFallbackAndKeepUnspecifiedFields()
        {
            var fallback = TrackMetadataFallback.FromPath("Music/Radiohead/In Rainbows/03 - Nude.mp3");
            byte[] artwork = { 1, 2, 3 };
            var metadata = fallback.WithEmbedded(new TrackMetadataValues
            {
                Title = " Real title\0 ", Artist = "Real artist", AlbumArtist = "Album artist",
                TrackNumber = 7, ReleaseYear = 2007, DurationMilliseconds = 240000, ArtworkData = artwork
            });
            Assert.Multiple(() =>
            {
                Assert.That(metadata.Title, Is.EqualTo("Real title"));
                Assert.That(metadata.Artist, Is.EqualTo("Real artist"));
                Assert.That(metadata.Album, Is.EqualTo("In Rainbows"));
                Assert.That(metadata.AlbumArtist, Is.EqualTo("Album artist"));
                Assert.That(metadata.TrackNumber, Is.EqualTo(7));
                Assert.That(metadata.ReleaseYear, Is.EqualTo(2007));
                Assert.That(metadata.DurationMilliseconds, Is.EqualTo(240000));
                Assert.That(metadata.ArtworkData, Is.SameAs(artwork));
                Assert.That(metadata.OriginalPath, Is.EqualTo(fallback.OriginalPath));
                Assert.That(fallback.Title, Is.EqualTo("Nude"));
            });
        }

        [Test]
        public void EmptyAndInvalidUpdatesDoNotEraseGoodMetadata()
        {
            var metadata = TrackMetadataFallback.FromPath("Artist - Song.mp3")
                .WithEmbedded(new TrackMetadataValues { Album = "Album", ReleaseYear = 1997, TrackNumber = 3 });
            metadata = metadata.WithEmbedded(new TrackMetadataValues
            {
                Title = "\0  ", Artist = " ", Album = "\0", TrackNumber = -1, ReleaseYear = 99999,
                DurationMilliseconds = -9223372036854775807, ArtworkData = new byte[0]
            });
            Assert.That(metadata.Title, Is.EqualTo("Song"));
            Assert.That(metadata.Artist, Is.EqualTo("Artist"));
            Assert.That(metadata.Album, Is.EqualTo("Album"));
            Assert.That(metadata.ReleaseYear, Is.EqualTo(1997));
            Assert.That(metadata.TrackNumber, Is.EqualTo(3));
            Assert.That(metadata.DurationMilliseconds, Is.Null);
        }

        [Test]
        public void OversizedArtworkIsNotRetained()
        {
            var metadata = TrackMetadataFallback.FromPath("Song.mp3").WithEmbedded(new TrackMetadataValues
                { Title = "Tagged title", ArtworkData = new byte[TrackMetadata.MaximumArtworkBytes + 1] });
            Assert.That(metadata.ArtworkData, Is.Null);
            Assert.That(metadata.Title, Is.EqualTo("Tagged title"));
        }

        [Test]
        public void NextTrackStartsWithIndependentFallback()
        {
            var first = TrackMetadataFallback.FromPath("First.mp3").WithEmbedded(new TrackMetadataValues
                { Artist = "First artist", Album = "First album", ArtworkData = new byte[] { 1 } });
            var next = TrackMetadataFallback.FromPath("Second.mp3");
            Assert.That(next.Title, Is.EqualTo("Second"));
            Assert.That(next.Artist, Is.Null);
            Assert.That(next.Album, Is.Null);
            Assert.That(next.ArtworkData, Is.Null);
            Assert.That(first.Artist, Is.EqualTo("First artist"));
        }

        [Test]
        public void VeryLongTextIsBoundedWithoutSplittingASurrogatePair()
        {
            var metadata = TrackMetadataFallback.FromPath("song.mp3")
                .WithEmbedded(new TrackMetadataValues { Title = new string('x', 4095) + "🌙tail" });
            Assert.That(metadata.Title.Length, Is.EqualTo(4095));
        }
    }
}
