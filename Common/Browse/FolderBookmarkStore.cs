using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Common.Browse
{
    public sealed class FolderBookmark
    {
        public string Id { get; set; } = "";
        public string Username { get; set; } = "";
        public string FolderPath { get; set; } = "";
        public string Label { get; set; } = "";
        public static string DefaultLabel(string username, string path)
        {
            string value = path.Replace('/', '\\').TrimEnd('\\');
            int separator = value.LastIndexOf('\\');
            string label = separator < 0 ? value : value.Substring(separator + 1);
            return string.IsNullOrWhiteSpace(label) ? username : label.Substring(0, Math.Min(label.Length, 200));
        }
        internal static string Key(string path) => path.Replace('/', '\\').TrimEnd('\\');
        internal FolderBookmark Copy() => new FolderBookmark { Id = Id, Username = Username, FolderPath = FolderPath, Label = Label };
    }

    /// <summary>Local folder addresses only; loading bookmarks never contacts a peer.</summary>
    public sealed class FolderBookmarkStore
    {
        public const int MaximumBytes = 512 * 1024;
        private readonly object gate = new object();
        private readonly string path;
        private List<FolderBookmark>? entries;
        public sealed class Document
        {
            public int Version { get; set; } = 1;
            public List<FolderBookmark> Entries { get; set; } = new List<FolderBookmark>();
        }
        public FolderBookmarkStore(string path) { this.path = path; }
        private void Load() { entries ??= Read(path) ?? Read(path + ".bak") ?? new List<FolderBookmark>(); }
        public IReadOnlyList<FolderBookmark> All()
        {
            lock (gate) { Load(); return entries!.Select(entry => entry.Copy()).ToArray(); }
        }
        public bool Contains(string username, string folderPath)
        {
            lock (gate) { Load(); return entries!.Any(e => Same(e, username, folderPath)); }
        }
        public FolderBookmark? Find(string username, string folderPath)
        {
            lock (gate) { Load(); return entries!.FirstOrDefault(entry => Same(entry, username, folderPath))?.Copy(); }
        }
        private static bool Same(FolderBookmark entry, string username, string folder) =>
            entry.Username == username && FolderBookmark.Key(entry.FolderPath) == FolderBookmark.Key(folder);
        public bool Add(string username, string folderPath)
        {
            lock (gate)
            {
                Load();
                if (username == null || folderPath == null) throw new InvalidDataException("Missing bookmark identity.");
                var addition = new FolderBookmark { Id = Guid.NewGuid().ToString("N"), Username = username, FolderPath = folderPath,
                    Label = FolderBookmark.DefaultLabel(username, folderPath) };
                Validate(new List<FolderBookmark> { addition });
                if (entries!.Any(entry => Same(entry, username, folderPath))) return false;
                var replacement = entries.Select(entry => entry.Copy()).ToList();
                replacement.Insert(0, addition);
                Save(replacement);
                return true;
            }
        }
        public bool Remove(string id)
        {
            lock (gate)
            {
                Load();
                var replacement = entries!.Where(entry => entry.Id != id).Select(entry => entry.Copy()).ToList();
                if (replacement.Count == entries.Count) return false;
                Save(replacement); return true;
            }
        }
        public bool Rename(string id, string label)
        {
            lock (gate)
            {
                Load();
                if (!entries!.Any(entry => entry.Id == id)) return false;
                var replacement = entries.Select(entry => entry.Copy()).ToList();
                replacement.First(entry => entry.Id == id).Label = label?.Trim() ?? "";
                Save(replacement); return true;
            }
        }
        private static bool Valid(string value, int max, bool empty = false) => value != null && value.Length <= max
            && (empty || !string.IsNullOrWhiteSpace(value)) && !value.Contains('\0');
        private static void Validate(List<FolderBookmark> list)
        {
            if (list == null || list.Count > 500) throw new InvalidDataException("Invalid bookmark list.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var identities = new HashSet<(string, string)>();
            long budget = 128;
            foreach (var entry in list)
            {
                if (entry == null || !Valid(entry.Id, 64) || !ids.Add(entry.Id) || !Valid(entry.Username, 1024)
                    || !Valid(entry.FolderPath, 16384, true) || !Valid(entry.Label, 200)
                    || !identities.Add((entry.Username, FolderBookmark.Key(entry.FolderPath))))
                    throw new InvalidDataException("Invalid bookmark.");
                budget += 256 + 6L * (entry.Id.Length + entry.Username.Length + entry.FolderPath.Length + entry.Label.Length);
                if (budget > MaximumBytes) throw new InvalidDataException("Bookmark storage limit reached.");
            }
        }
        private static List<FolderBookmark>? Read(string file)
        {
            try
            {
                if (!File.Exists(file) || new FileInfo(file).Length > MaximumBytes) return null;
                var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(file));
                if (document == null || document.Version != 1) return null;
                Validate(document.Entries);
                return document.Entries;
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException
                || error is JsonException || error is ArgumentException || error is NotSupportedException) { return null; }
        }
        private void Save(List<FolderBookmark> replacement)
        {
            Validate(replacement);
            string json = JsonSerializer.Serialize(new Document { Entries = replacement });
            string temporary = path + ".tmp";
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            try
            {
                using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(file, System.Text.Encoding.UTF8, 4096, true))
                {
                    writer.Write(json); writer.Flush(); file.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
                else File.Move(temporary, path);
                entries = replacement;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
