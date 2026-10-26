using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Playback
{
    public sealed class SavedPlaybackQueue
    {
        public PlaybackQueue Queue { get; }
        public PlaybackRepeatMode RepeatMode { get; }
        public bool Autoplay { get; }
        internal SavedPlaybackQueue(PlaybackQueue queue, PlaybackRepeatMode repeatMode, bool autoplay)
        { Queue = queue; RepeatMode = repeatMode; Autoplay = autoplay; }
    }

    /// <summary>Versioned queue sources only: no player state, cached bytes or trusted tag metadata.</summary>
    public static class PlaybackQueuePersistence
    {
        public const int MaximumJsonLength = 2 * 1024 * 1024;
        public const int MaximumEntries = 10000;

        public static string? Serialize(PlaybackQueue queue, PlaybackRepeatMode repeatMode, bool autoplay)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            if (queue.Current == null) return null;
            var entries = queue.Entries;
            if (entries.Count > MaximumEntries) throw new InvalidDataException("Queue is too large to save.");
            var document = new PlaybackQueueDocument
            {
                Version = 1, SelectedId = queue.Current.Id, RepeatMode = repeatMode, Autoplay = autoplay,
                Entries = entries.Select(entry => new SavedQueueEntryData
                {
                    Id = entry.Id, Username = entry.Username, RemotePath = entry.RemotePath, Size = entry.Size,
                    DiscNumber = entry.DiscNumber, FilenameDecoded = entry.WasFilenameLatin1Decoded,
                    FolderDecoded = entry.WasFolderLatin1Decoded
                }).ToList()
            };
            string json = JsonSerializer.Serialize(document, PlaybackQueueJsonContext.Default.PlaybackQueueDocument);
            if (json.Length > MaximumJsonLength) throw new InvalidDataException("Queue is too large to save.");
            return json;
        }

        public static bool TryRestore(string? json, out SavedPlaybackQueue? saved)
        {
            saved = null;
            if (string.IsNullOrEmpty(json) || json.Length > MaximumJsonLength) return false;
            try
            {
                var document = JsonSerializer.Deserialize(json, PlaybackQueueJsonContext.Default.PlaybackQueueDocument);
                if (document == null || document.Version != 1 || document.Entries == null
                    || document.Entries.Count == 0 || document.Entries.Count > MaximumEntries
                    || !ValidText(document.SelectedId, 128)
                    || !Enum.IsDefined(typeof(PlaybackRepeatMode), document.RepeatMode)
                    || (!document.Autoplay && document.RepeatMode != PlaybackRepeatMode.Off)) return false;
                var entries = new List<PlaybackQueueEntry>();
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in document.Entries)
                {
                    if (entry == null || !ValidText(entry.Id, 128) || !ids.Add(entry.Id!)
                        || !ValidText(entry.Username, 1024) || !ValidText(entry.RemotePath, 32768)
                        || !PlaybackQueue.IsSupportedAudio(entry.RemotePath!) || entry.Size < 0
                        || (entry.DiscNumber.HasValue && (entry.DiscNumber < 1 || entry.DiscNumber > 999))) return false;
                    entries.Add(new PlaybackQueueEntry(entry.Username!, entry.RemotePath!, entry.Size,
                        discNumber: entry.DiscNumber, id: entry.Id,
                        wasFilenameLatin1Decoded: entry.FilenameDecoded, wasFolderLatin1Decoded: entry.FolderDecoded));
                }
                var queue = new PlaybackQueue();
                if (!queue.Replace(entries, document.SelectedId)) return false;
                saved = new SavedPlaybackQueue(queue, document.RepeatMode, document.Autoplay);
                return true;
            }
            catch (JsonException) { return false; }
            catch (ArgumentException) { return false; }
        }

        private static bool ValidText(string? value, int maximumLength) =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && value.IndexOf('\0') < 0;
    }

    internal sealed class PlaybackQueueDocument
    {
        public PlaybackQueueDocument() { }
        public int Version { get; set; }
        public string? SelectedId { get; set; }
        public PlaybackRepeatMode RepeatMode { get; set; }
        public bool Autoplay { get; set; }
        public List<SavedQueueEntryData>? Entries { get; set; }
    }

    internal sealed class SavedQueueEntryData
    {
        public SavedQueueEntryData() { }
        public string? Id { get; set; }
        public string? Username { get; set; }
        public string? RemotePath { get; set; }
        public long Size { get; set; }
        public int? DiscNumber { get; set; }
        public bool FilenameDecoded { get; set; }
        public bool FolderDecoded { get; set; }
    }

    [JsonSerializable(typeof(PlaybackQueueDocument))]
    internal partial class PlaybackQueueJsonContext : JsonSerializerContext { }
}
