using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Common.Playback
{
    public static class TrackMetadataFallback
    {
        private static readonly Regex TrackPrefix = new Regex(
            @"^\s*([0-9]{1,3})(?:\s*[-._)]\s*|\s+)(\S.*)$", RegexOptions.CultureInvariant);
        private static readonly Regex TitleSeparator = new Regex(
            @"\s+-\s*|\s*-\s+", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> MusicRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "music", "audio", "mp3", "mp3s", "albums" };
        private static readonly HashSet<string> GenericFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "music", "audio", "mp3", "mp3s", "albums", "downloads", "download", "shared", "shares",
                "unknown", "unknown artist", "unknown album", "various", "various artists", "compilations",
                "singles", "misc", "tracks", "cd1", "cd2", "disc 1", "disc 2", "disk 1", "disk 2" };

        /// <summary>
        /// Parses flat names such as "Artist - Album - 06 - Title". A numeric segment
        /// immediately before the title is required, so "Artist - Title - Live" remains
        /// deliberately ambiguous.
        /// </summary>
        private static bool TryParseFlatFilename(string title,
            out string? artist, out string? album, out int? track, out string? parsedTitle)
        {
            artist = null;
            album = null;
            track = null;
            parsedTitle = null;
            string[] segments = TitleSeparator.Split(title);
            if (segments.Length < 3) return false;

            int trackIndex = -1;
            int trackNumber = 0;
            for (int i = 1; i < segments.Length - 1; i++)
            {
                if (int.TryParse(segments[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int number)
                    && number > 0 && number <= 999)
                {
                    trackIndex = i;
                    trackNumber = number;
                    break;
                }
            }
            if (trackIndex < 1) return false;

            string artistValue = segments[0].Trim();
            string titleValue = string.Join(" - ", segments, trackIndex + 1,
                segments.Length - trackIndex - 1).Trim();
            if (artistValue.Length == 0 || titleValue.Length == 0) return false;
            string? albumValue = trackIndex > 1
                ? string.Join(" - ", segments, 1, trackIndex - 1).Trim() : null;
            if (GenericFolders.Contains(artistValue)
                || (albumValue != null && GenericFolders.Contains(albumValue))) return false;

            artist = TrackMetadata.Clean(artistValue);
            album = TrackMetadata.Clean(albumValue);
            track = trackNumber;
            parsedTitle = TrackMetadata.Clean(titleValue);
            return parsedTitle != null;
        }

        public static TrackMetadata FromPath(string path)
        {
            path ??= string.Empty;
            string[] parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            string filename = parts.Length == 0 ? path : parts[parts.Length - 1];
            string title = filename.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                ? filename.Substring(0, filename.Length - 4) : filename;
            title = TrackMetadata.Clean(title) ?? filename;
            int? track = null;
            var prefix = TrackPrefix.Match(title);
            if (prefix.Success && int.TryParse(prefix.Groups[1].Value, out int number) && number > 0)
            {
                track = number;
                title = prefix.Groups[2].Value.Trim();
            }

            string? artist = null;
            string? album = null;
            if (TryParseFlatFilename(title, out string? parsedArtist, out string? parsedAlbum,
                out int? parsedTrack, out string? parsedTitle))
            {
                artist = parsedArtist;
                album = parsedAlbum;
                track = parsedTrack;
                title = parsedTitle!;
            }
            else
            {
                int separator = title.IndexOf(" - ", StringComparison.Ordinal);
                if (separator > 0 && separator == title.LastIndexOf(" - ", StringComparison.Ordinal))
                {
                    artist = TrackMetadata.Clean(title.Substring(0, separator));
                    title = TrackMetadata.Clean(title.Substring(separator + 3)) ?? title;
                }
            }

            if (parts.Length >= 3)
            {
                string candidateArtist = parts[parts.Length - 3].Trim();
                string candidateAlbum = parts[parts.Length - 2].Trim();
                bool knownRoot = parts.Length >= 4 && MusicRoots.Contains(parts[parts.Length - 4]);
                bool corroborated = string.Equals(artist, candidateArtist, StringComparison.OrdinalIgnoreCase);
                // Arbitrary peer share roots are ambiguous. Require a music root or filename agreement.
                if ((knownRoot || corroborated) && (artist == null || corroborated) && !GenericFolders.Contains(candidateArtist)
                    && !GenericFolders.Contains(candidateAlbum) && candidateArtist.Length > 0 && candidateAlbum.Length > 0)
                {
                    artist ??= TrackMetadata.Clean(candidateArtist);
                    album ??= TrackMetadata.Clean(candidateAlbum);
                }
            }
            return new TrackMetadata(path, filename, title, artist, album, trackNumber: track);
        }
    }
}
