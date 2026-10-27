using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Updates
{
    public sealed class AppUpdate
    {
        public const string Repository = "miacadaver/Noiser";
        public const string Package = "com.companyname.andriodapp1.rollback";
        public const long MaximumApkBytes = 512L * 1024 * 1024;
        public int SchemaVersion { get; set; }
        public long VersionCode { get; set; }
        public string VersionName { get; set; } = "";
        public string PackageName { get; set; } = "";
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";
        public string ApkUrl { get; set; } = "";
        [System.Text.Json.Serialization.JsonIgnore] public string ReleaseNotes { get; set; } = "";
        [System.Text.Json.Serialization.JsonIgnore] public string ReleaseUrl => "https://github.com/" + Repository + "/releases/tag/" + Tag;
        [System.Text.Json.Serialization.JsonIgnore] public string Tag => "noiser-" + VersionName;
        public static string AssetUrl(string tag, string name) => "https://github.com/" + Repository + "/releases/download/" + tag + "/" + name;

        public void Validate()
        {
            if (SchemaVersion != 1 || VersionCode <= 0 || VersionCode > int.MaxValue || PackageName != Package
                || VersionName == null || !Regex.IsMatch(VersionName, @"\A[a-zA-Z0-9][a-zA-Z0-9.+_-]{0,127}\z")
                || Size <= 0 || Size > MaximumApkBytes || Sha256 == null || !Regex.IsMatch(Sha256, @"\A[0-9a-fA-F]{64}\z")
                || ApkUrl != AssetUrl(Tag, "Noiser.apk"))
                throw new InvalidDataException("Unsupported update metadata.");
        }

        public void ValidatePackage(string packageName, long versionCode, long installedVersion,
            IEnumerable<string> signers, IEnumerable<string> installedSigners)
        {
            Validate();
            var expected = new HashSet<string>(installedSigners, StringComparer.OrdinalIgnoreCase);
            var actual = new HashSet<string>(signers, StringComparer.OrdinalIgnoreCase);
            if (packageName != PackageName || versionCode != VersionCode || versionCode <= installedVersion
                || expected.Count == 0 || actual.Count == 0 || !expected.SetEquals(actual))
                throw new InvalidDataException("The APK is not an update for this installation.");
        }
    }

    /// <summary>Public GitHub releases only; no token, account credentials, or background polling.</summary>
    public sealed class GithubUpdateClient
    {
        public const string LatestUrl = "https://api.github.com/repos/" + AppUpdate.Repository + "/releases/latest";
        private readonly HttpClient http;
        public GithubUpdateClient(HttpClient http) { this.http = http; }

        public async Task<AppUpdate?> CheckAsync(long installedVersion, CancellationToken cancellation)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
            request.Headers.UserAgent.ParseAdd("Noiser-Updater/1.0");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            using var release = JsonDocument.Parse(await ReadLimitedAsync(response, 512 * 1024, cancellation).ConfigureAwait(false));
            var root = release.RootElement;
            try
            {
                if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
                string tag = root.GetProperty("tag_name").GetString() ?? "";
                if (!Regex.IsMatch(tag, @"\Anoiser-[a-zA-Z0-9][a-zA-Z0-9.+_-]{0,127}\z"))
                    throw new InvalidDataException("Unexpected release tag.");
                var assets = root.GetProperty("assets").EnumerateArray().ToArray();
                var manifests = assets.Where(a => a.GetProperty("name").GetString() == "Noiser-update.json").ToArray();
                var apks = assets.Where(a => a.GetProperty("name").GetString() == "Noiser.apk").ToArray();
                if (manifests.Length != 1 || apks.Length != 1) throw new InvalidDataException("The release is incomplete.");
                string url = manifests[0].GetProperty("browser_download_url").GetString() ?? "";
                if (url != AppUpdate.AssetUrl(tag, "Noiser-update.json")) throw new InvalidDataException("Unexpected update URL.");
                using var metadata = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
                metadata.EnsureSuccessStatusCode();
                var update = JsonSerializer.Deserialize<AppUpdate>(await ReadLimitedAsync(metadata, 16 * 1024, cancellation).ConfigureAwait(false))
                    ?? throw new InvalidDataException("Missing update metadata.");
                update.Validate();
                if (update.Tag != tag || apks[0].GetProperty("browser_download_url").GetString() != update.ApkUrl
                    || apks[0].GetProperty("size").GetInt64() != update.Size)
                    throw new InvalidDataException("Release assets do not match update metadata.");
                update.ReleaseNotes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
                if (update.ReleaseNotes.Length > 16000) update.ReleaseNotes = update.ReleaseNotes.Substring(0, 16000);
                return update.VersionCode > installedVersion ? update : null;
            }
            catch (Exception error) when (error is KeyNotFoundException || error is InvalidOperationException || error is FormatException)
            { throw new InvalidDataException("Invalid release metadata.", error); }
        }

        private static async Task<string> ReadLimitedAsync(HttpResponseMessage response, int limit, CancellationToken cancellation)
        {
            if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Update metadata is too large.");
            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > limit) throw new InvalidDataException("Update metadata is too large.");
                output.Write(buffer, 0, count);
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }

        public async Task DownloadAsync(AppUpdate update, string destination, IProgress<int>? progress, CancellationToken cancellation)
        {
            update.Validate();
            string temporary = destination + ".part";
            try
            {
                using var response = await http.GetAsync(update.ApkUrl, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long length && length != update.Size)
                    throw new InvalidDataException("Unexpected download length.");
                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var hash = SHA256.Create())
                {
                    var buffer = new byte[65536];
                    long total = 0;
                    int count, lastPercent = -1;
                    while ((count = await input.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) != 0)
                    {
                        total += count;
                        if (total > update.Size) throw new InvalidDataException("Download exceeds expected size.");
                        await output.WriteAsync(buffer, 0, count, cancellation).ConfigureAwait(false);
                        hash.TransformBlock(buffer, 0, count, null, 0);
                        int percent = (int)(100 * total / update.Size);
                        if (percent != lastPercent) { progress?.Report(percent); lastPercent = percent; }
                    }
                    hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    string digest = BitConverter.ToString(hash.Hash!).Replace("-", "");
                    if (total != update.Size || !string.Equals(digest, update.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The downloaded APK failed verification.");
                    await output.FlushAsync(cancellation).ConfigureAwait(false);
                }
                cancellation.ThrowIfCancellationRequested();
                if (File.Exists(destination)) File.Delete(destination);
                File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
