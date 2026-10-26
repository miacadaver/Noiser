using System;

namespace Common.Playback
{
    /// <summary>A playback snapshot, independent of Android views, players, and transfer state.</summary>
    public sealed class TrackMetadata
    {
        public const int MaximumArtworkBytes = 2 * 1024 * 1024;
        public string OriginalPath { get; }
        public string OriginalFilename { get; }
        public string Title { get; }
        public string? Artist { get; }
        public string? Album { get; }
        // Album may be a display fallback. Only a parsed tag is suitable for external scrobblers.
        public string? EmbeddedAlbum { get; }
        public string? AlbumArtist { get; }
        public int? TrackNumber { get; }
        public int? ReleaseYear { get; }
        public long? DurationMilliseconds { get; }
        // Encoded artwork only. Bitmap decoding belongs to the Android presentation layer.
        public byte[]? ArtworkData { get; }

        internal TrackMetadata(string path, string filename, string title, string? artist, string? album,
            string? albumArtist = null, int? trackNumber = null, int? year = null,
            long? duration = null, byte[]? artwork = null, string? embeddedAlbum = null)
        {
            OriginalPath = path;
            OriginalFilename = filename;
            Title = title;
            Artist = artist;
            Album = album;
            EmbeddedAlbum = embeddedAlbum;
            AlbumArtist = albumArtist;
            TrackNumber = trackNumber;
            ReleaseYear = year;
            DurationMilliseconds = duration;
            ArtworkData = artwork;
        }

        /// <summary>Valid embedded values replace fallback values; absent values preserve the snapshot.</summary>
        public TrackMetadata WithEmbedded(TrackMetadataValues values) => new TrackMetadata(
            OriginalPath, OriginalFilename, Clean(values.Title) ?? Title,
            Clean(values.Artist) ?? Artist, Clean(values.Album) ?? Album,
            Clean(values.AlbumArtist) ?? AlbumArtist,
            values.TrackNumber > 0 && values.TrackNumber <= 9999 ? values.TrackNumber : TrackNumber,
            values.ReleaseYear >= 1000 && values.ReleaseYear <= 9999 ? values.ReleaseYear : ReleaseYear,
            values.DurationMilliseconds > 0 ? values.DurationMilliseconds : DurationMilliseconds,
            values.ArtworkData?.Length > 0 && values.ArtworkData.Length <= MaximumArtworkBytes
                ? values.ArtworkData : ArtworkData,
            embeddedAlbum: Clean(values.Album) ?? EmbeddedAlbum);

        internal static string? Clean(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Replace("\0", string.Empty).Trim();
            if (value.Length == 0) return null;
            if (value.Length <= 4096) return value;
            int length = char.IsHighSurrogate(value[4095]) ? 4095 : 4096;
            return value.Substring(0, length);
        }
    }

    /// <summary>Fields supplied by an audio metadata decoder; no filename guesses belong here.</summary>
    public sealed class TrackMetadataValues
    {
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? AlbumArtist { get; set; }
        public int? TrackNumber { get; set; }
        public int? ReleaseYear { get; set; }
        public long? DurationMilliseconds { get; set; }
        public byte[]? ArtworkData { get; set; }
    }
}
