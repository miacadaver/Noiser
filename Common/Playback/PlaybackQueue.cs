using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text.RegularExpressions;

namespace Common.Playback
{
    /// <summary>One remote source item in the in-memory playback queue.</summary>
    public sealed class PlaybackQueueEntry
    {
        public string Id { get; }
        public string Username { get; }
        public string RemotePath { get; }
        public string DisplayFilename { get; }
        public long Size { get; }
        public TrackMetadata Metadata { get; private set; }
        public int? DiscNumber { get; }
        public bool WasFilenameLatin1Decoded { get; }
        public bool WasFolderLatin1Decoded { get; }
        public PlaybackQueueEntryState State { get; private set; }
        public string? Error { get; private set; }

        public PlaybackQueueEntry(string username, string remotePath, long size,
            TrackMetadata? metadata = null, int? discNumber = null, string? id = null,
            bool wasFilenameLatin1Decoded = false, bool wasFolderLatin1Decoded = false)
        {
            if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("A source peer is required.", nameof(username));
            if (string.IsNullOrWhiteSpace(remotePath)) throw new ArgumentException("A remote path is required.", nameof(remotePath));
            if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id;
            Username = username;
            RemotePath = remotePath;
            DisplayFilename = remotePath.Replace('/', '\\').Split('\\').Last();
            Size = size;
            Metadata = metadata ?? TrackMetadataFallback.FromPath(remotePath);
            DiscNumber = discNumber > 0 && discNumber <= 999 ? discNumber : null;
            WasFilenameLatin1Decoded = wasFilenameLatin1Decoded;
            WasFolderLatin1Decoded = wasFolderLatin1Decoded;
            State = PlaybackQueueEntryState.Pending;
        }

        public void MarkPlaying() { State = PlaybackQueueEntryState.Playing; Error = null; }
        public void MarkPlayed() { State = PlaybackQueueEntryState.Played; Error = null; }
        public void MarkSkipped() { State = PlaybackQueueEntryState.Skipped; Error = null; }
        public void MarkFailed(string? error) { State = PlaybackQueueEntryState.Failed; Error = error; }
        public void MarkPending() { State = PlaybackQueueEntryState.Pending; Error = null; }
        public void UpdateMetadata(TrackMetadata metadata)
        {
            if (metadata == null) throw new ArgumentNullException(nameof(metadata));
            Metadata = new TrackMetadata(metadata.OriginalPath, Metadata.OriginalFilename, metadata.Title,
                metadata.Artist, metadata.Album, metadata.AlbumArtist, metadata.TrackNumber,
                metadata.ReleaseYear, metadata.DurationMilliseconds);
        }

        /// <summary>Queue entries deliberately retain metadata without artwork bytes.</summary>
        public PlaybackQueueEntry Copy() => new PlaybackQueueEntry(Username, RemotePath, Size,
            new TrackMetadata(Metadata.OriginalPath, Metadata.OriginalFilename, Metadata.Title,
                Metadata.Artist, Metadata.Album, Metadata.AlbumArtist, Metadata.TrackNumber,
                Metadata.ReleaseYear, Metadata.DurationMilliseconds), DiscNumber, Id,
            WasFilenameLatin1Decoded, WasFolderLatin1Decoded);
    }

    public enum PlaybackQueueEntryState { Pending, Playing, Played, Skipped, Failed }

    /// <summary>Service-owned, in-memory queue state. Methods are synchronous and expected on the service looper.</summary>
    public sealed class PlaybackQueue
    {
        private readonly List<PlaybackQueueEntry> entries = new List<PlaybackQueueEntry>();
        public IReadOnlyList<PlaybackQueueEntry> Entries => entries.ToArray();
        public PlaybackQueueEntry? Current => currentIndex >= 0 && currentIndex < entries.Count ? entries[currentIndex] : null;
        public int CurrentIndex => currentIndex;
        public bool HasNext => currentIndex >= 0 && currentIndex + 1 < entries.Count;
        public bool HasPrevious => currentIndex > 0;
        private int currentIndex = -1;

        /// <summary>Replace atomically after validation. A failed folder build must not discard the playing queue.</summary>
        public bool Replace(IEnumerable<PlaybackQueueEntry> source, string? selectedEntryId = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var replacement = source.Select(entry => entry?.Copy() ?? throw new ArgumentException("Queue entries cannot be null.", nameof(source)))
                .Where(entry => IsSupportedAudio(entry.RemotePath)).ToList();
            if (replacement.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != replacement.Count)
                throw new ArgumentException("Queue entry identifiers must be unique.", nameof(source));
            if (replacement.Count == 0) return false;
            int selected = selectedEntryId == null ? 0 : replacement.FindIndex(entry => entry.Id == selectedEntryId);
            if (selected < 0) return false;
            entries.Clear();
            entries.AddRange(replacement);
            currentIndex = selected;
            return true;
        }

        public PlaybackQueueEntry? Select(string id)
        {
            int found = entries.FindIndex(entry => entry.Id == id);
            if (found < 0) return null;
            currentIndex = found;
            return Current;
        }

        public PlaybackQueueEntry? MoveNext(bool completed = false)
        {
            if (!HasNext) return null;
            if (completed) Current?.MarkPlayed();
            else Current?.MarkSkipped();
            currentIndex++;
            return Current;
        }

        public PlaybackQueueEntry? MovePrevious()
        {
            if (!HasPrevious) return null;
            Current?.MarkPending();
            currentIndex--;
            return Current;
        }

        public bool AddLast(PlaybackQueueEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (!IsSupportedAudio(entry.RemotePath) || entries.Any(existing => existing.Id == entry.Id)) return false;
            entries.Add(entry.Copy());
            if (currentIndex < 0) currentIndex = 0;
            return true;
        }

        public bool AddNext(PlaybackQueueEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            if (!IsSupportedAudio(entry.RemotePath) || entries.Any(existing => existing.Id == entry.Id)) return false;
            int insertion = currentIndex < 0 ? 0 : currentIndex + 1;
            entries.Insert(insertion, entry.Copy());
            if (currentIndex < 0) currentIndex = 0;
            return true;
        }

        /// <summary>Removing current selects its successor, or the final predecessor, without changing IDs.</summary>
        public PlaybackQueueEntry? Remove(string id, out bool removedCurrent)
        {
            int index = entries.FindIndex(entry => entry.Id == id);
            removedCurrent = index >= 0 && index == currentIndex;
            if (index < 0) return Current;
            entries.RemoveAt(index);
            if (entries.Count == 0) currentIndex = -1;
            else if (index < currentIndex) currentIndex--;
            else if (index == currentIndex) currentIndex = Math.Min(index, entries.Count - 1);
            return Current;
        }

        public void Clear() { entries.Clear(); currentIndex = -1; }

        public static IReadOnlyList<PlaybackQueueEntry> Order(IEnumerable<PlaybackQueueEntry> tracks)
        {
            if (tracks == null) throw new ArgumentNullException(nameof(tracks));
            return tracks.Where(entry => entry != null && IsSupportedAudio(entry.RemotePath))
                .Select((entry, index) => new { Entry = entry, Index = index })
                .OrderBy(item => item.Entry.DiscNumber.HasValue ? 0 : 1)
                .ThenBy(item => item.Entry.DiscNumber ?? int.MaxValue)
                .ThenBy(item => item.Entry.Metadata.TrackNumber.HasValue ? 0 : 1)
                .ThenBy(item => item.Entry.Metadata.TrackNumber ?? int.MaxValue)
                .ThenBy(item => item.Entry.DisplayFilename, NaturalFilenameComparer.Instance)
                .ThenBy(item => item.Index)
                .Select(item => item.Entry.Copy()).ToArray();
        }

        public static bool IsSupportedAudio(string remotePath) =>
            string.Equals(Path.GetExtension(remotePath), ".mp3", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Case-insensitive filename order that compares digit runs by value (track 2 before track 10).</summary>
    public sealed class NaturalFilenameComparer : IComparer<string>
    {
        private static readonly Regex Digits = new Regex(@"\d+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        public static NaturalFilenameComparer Instance { get; } = new NaturalFilenameComparer();
        private NaturalFilenameComparer() { }

        public int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left == null) return -1;
            if (right == null) return 1;
            var a = Digits.Matches(left);
            var b = Digits.Matches(right);
            int ai = 0, bi = 0, offsetA = 0, offsetB = 0;
            while (ai < a.Count && bi < b.Count)
            {
                var matchA = a[ai];
                var matchB = b[bi];
                int prefix = StringComparer.OrdinalIgnoreCase.Compare(
                    left.Substring(offsetA, matchA.Index - offsetA), right.Substring(offsetB, matchB.Index - offsetB));
                if (prefix != 0) return prefix;
                if (long.TryParse(matchA.Value, NumberStyles.None, CultureInfo.InvariantCulture, out long numberA)
                    && long.TryParse(matchB.Value, NumberStyles.None, CultureInfo.InvariantCulture, out long numberB))
                {
                    int numeric = numberA.CompareTo(numberB);
                    if (numeric != 0) return numeric;
                }
                else
                {
                    int numericText = StringComparer.OrdinalIgnoreCase.Compare(matchA.Value, matchB.Value);
                    if (numericText != 0) return numericText;
                }
                offsetA = matchA.Index + matchA.Length;
                offsetB = matchB.Index + matchB.Length;
                ai++; bi++;
            }
            int tail = StringComparer.OrdinalIgnoreCase.Compare(left.Substring(offsetA), right.Substring(offsetB));
            return tail != 0 ? tail : StringComparer.Ordinal.Compare(left, right);
        }
    }
}
