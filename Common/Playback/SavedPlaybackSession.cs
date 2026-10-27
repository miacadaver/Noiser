using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Common.Playback
{
    /// <summary>Durable track identities only: no player, transfer, artwork, position, or play intent.</summary>
    public sealed class SavedPlaybackSession
    {
        public int Version { get; set; }
        public string Account { get; set; } = "";
        public string CurrentId { get; set; } = "";
        public List<SavedPlaybackEntry> Entries { get; set; } = new List<SavedPlaybackEntry>();
        [System.Text.Json.Serialization.JsonIgnore]
        public SavedPlaybackEntry Current => Entries.First(entry => entry.Id == CurrentId);

        public static SavedPlaybackSession Capture(string account, PlaybackQueue queue) => new SavedPlaybackSession
        {
            Version = 1, Account = account, CurrentId = queue.Current?.Id ?? "",
            Entries = queue.Entries.Select(entry => new SavedPlaybackEntry
            {
                Id = entry.Id, Username = entry.Username, RemotePath = entry.RemotePath, Size = entry.Size,
                FilenameDecoded = entry.WasFilenameLatin1Decoded, FolderDecoded = entry.WasFolderLatin1Decoded,
                Title = entry.Metadata.Title, Artist = entry.Metadata.Artist, Album = entry.Metadata.EmbeddedAlbum
            }).ToList()
        };

        public PlaybackQueue ToQueue()
        {
            Validate();
            var queue = new PlaybackQueue();
            if (!queue.Replace(Entries.Select(entry => entry.ToQueueEntry()), CurrentId))
                throw new InvalidDataException("The saved current song is missing.");
            return queue;
        }

        public void Validate()
        {
            if (Version != 1 || !Valid(Account, 1024) || Entries == null || Entries.Count == 0
                || Entries.Count > 2000 || !Valid(CurrentId, 256))
                throw new InvalidDataException("Unsupported or incomplete saved queue.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            long maximumEncodedBytes = 512 + 6L * (Account.Length + CurrentId.Length);
            foreach (var entry in Entries)
            {
                if (entry == null || !Valid(entry.Id, 256) || !ids.Add(entry.Id)
                    || !Valid(entry.Username, 1024) || !Valid(entry.RemotePath, 16384)
                    || !PlaybackQueue.IsSupportedAudio(entry.RemotePath) || entry.Size < 0
                    || (entry.Title?.Length ?? 0) > 4096 || (entry.Artist?.Length ?? 0) > 4096
                    || (entry.Album?.Length ?? 0) > 4096)
                    throw new InvalidDataException("Invalid saved queue entry.");
                // Bound allocation before JSON encoding, including worst-case Unicode escaping.
                maximumEncodedBytes += 256 + 6L * (entry.Id.Length + entry.Username.Length + entry.RemotePath.Length
                    + (entry.Title?.Length ?? 0) + (entry.Artist?.Length ?? 0) + (entry.Album?.Length ?? 0));
                if (maximumEncodedBytes > PlaybackSessionStore.MaximumBytes)
                    throw new InvalidDataException("The saved queue is too large.");
            }
            if (!ids.Contains(CurrentId)) throw new InvalidDataException("The saved current song is missing.");
        }

        private static bool Valid(string value, int maximum) =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Contains('\0');

        public static string FolderOf(string remotePath)
        {
            string normalized = remotePath.Replace('/', '\\');
            int separator = normalized.LastIndexOf('\\');
            return separator < 0 ? "" : normalized.Substring(0, separator);
        }

        /// <summary>The offline folder preview is explicitly limited to known queue tracks.</summary>
        public IReadOnlyList<PlaybackQueueEntry> FolderTracks() => ToQueue().Entries
            .Where(entry => entry.Username == Current.Username && FolderOf(entry.RemotePath) == FolderOf(Current.RemotePath))
            .GroupBy(entry => entry.RemotePath, StringComparer.Ordinal).Select(group => group.First()).ToArray();
    }

    public sealed class SavedPlaybackEntry
    {
        public string Id { get; set; } = "";
        public string Username { get; set; } = "";
        public string RemotePath { get; set; } = "";
        public long Size { get; set; }
        public bool FilenameDecoded { get; set; }
        public bool FolderDecoded { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }

        public PlaybackQueueEntry ToQueueEntry() => new PlaybackQueueEntry(Username, RemotePath, Size,
            TrackMetadataFallback.FromPath(RemotePath).WithResolvedTags(new TrackMetadataValues
            { Title = Title, Artist = Artist, Album = Album }), id: Id,
            wasFilenameLatin1Decoded: FilenameDecoded, wasFolderLatin1Decoded: FolderDecoded);
    }

    /// <summary>Atomic private-file checkpoints, independent of Android shutdown callbacks.</summary>
    public sealed class PlaybackSessionStore
    {
        public const int MaximumBytes = 4 * 1024 * 1024;
        private readonly string path;
        private readonly object gate = new object();
        private string? lastContent;
        public PlaybackSessionStore(string path) { this.path = path; }

        public SavedPlaybackSession? Load(string account)
        {
            lock (gate)
            {
                try
                {
                    if (!File.Exists(path) || new FileInfo(path).Length > MaximumBytes) return null;
                    string json = File.ReadAllText(path);
                    var session = JsonSerializer.Deserialize<SavedPlaybackSession>(json);
                    if (session == null || !string.Equals(account, session.Account, StringComparison.Ordinal)) return null;
                    session.Validate();
                    lastContent = json;
                    return session;
                }
                catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException
                    || error is JsonException || error is ArgumentException || error is NotSupportedException)
                { return null; }
            }
        }

        public bool Save(SavedPlaybackSession session)
        {
            session.Validate();
            string json = JsonSerializer.Serialize(session);
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("The saved queue is too large.");
            lock (gate)
            {
                if (json == lastContent && File.Exists(path)) return false;
                string temporary = path + ".tmp";
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    file.Write(bytes, 0, bytes.Length);
                    file.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
                lastContent = json;
                return true;
            }
        }

        public void Clear()
        {
            lock (gate)
            {
                File.Delete(path);
                File.Delete(path + ".tmp");
                lastContent = null;
            }
        }
    }
}
