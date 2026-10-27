using Common.Updates;
using NUnit.Framework;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace UnitTestCommon
{
    public class AppUpdateTests
    {
        private string directory = null!;
        private static readonly byte[] Apk = Encoding.UTF8.GetBytes("test APK bytes");
        private static AppUpdate Update() => new AppUpdate
        {
            SchemaVersion = 1, VersionCode = 157, VersionName = "3.11.2-stream.14", PackageName = AppUpdate.Package,
            Size = Apk.Length, Sha256 = Convert.ToHexString(SHA256.HashData(Apk)),
            ApkUrl = AppUpdate.AssetUrl("noiser-3.11.2-stream.14", "Noiser.apk")
        };
        private static string Release(AppUpdate update, string? manifestUrl = null, bool prerelease = false) => JsonSerializer.Serialize(new
        {
            draft = false, prerelease, tag_name = update.Tag, body = "Release notes",
            assets = new[] {
                new { name = "Noiser-update.json", browser_download_url = manifestUrl ?? AppUpdate.AssetUrl(update.Tag, "Noiser-update.json"), size = 500L },
                new { name = "Noiser.apk", browser_download_url = update.ApkUrl, size = update.Size }
            }
        });
        private sealed class Handler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> response;
            public Handler(Func<HttpRequestMessage, HttpResponseMessage> response) { this.response = response; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            { token.ThrowIfCancellationRequested(); return Task.FromResult(response(request)); }
        }
        private static HttpResponseMessage Json(string text) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) };
        private static HttpClient ReleaseClient(AppUpdate update, string? release = null, string? metadata = null) => new HttpClient(new Handler(request =>
            request.RequestUri!.ToString() == GithubUpdateClient.LatestUrl ? Json(release ?? Release(update)) : Json(metadata ?? JsonSerializer.Serialize(update))));
        [SetUp] public void Setup() { directory = Path.Combine(Path.GetTempPath(), "noiser-update-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); }
        [TearDown] public void Cleanup() => Directory.Delete(directory, true);

        [Test] public async Task NewerCompleteReleaseProvidesNotesAndVersion()
        {
            using var http = ReleaseClient(Update());
            var found = await new GithubUpdateClient(http).CheckAsync(156, CancellationToken.None);
            Assert.That(found!.VersionCode, Is.EqualTo(157));
            Assert.That(found.ReleaseNotes, Is.EqualTo("Release notes"));
        }
        [TestCase(157)] [TestCase(158)] public async Task SameOrOlderReleaseDoesNotOfferDowngrade(long installed)
        {
            using var http = ReleaseClient(Update());
            Assert.That(await new GithubUpdateClient(http).CheckAsync(installed, CancellationToken.None), Is.Null);
        }
        [Test] public async Task MissingFirstReleaseIsHandled()
        {
            using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
            Assert.That(await new GithubUpdateClient(http).CheckAsync(156, CancellationToken.None), Is.Null);
        }
        [Test] public async Task PrereleaseIsNotOffered()
        {
            using var http = ReleaseClient(Update(), Release(Update(), prerelease: true));
            Assert.That(await new GithubUpdateClient(http).CheckAsync(156, CancellationToken.None), Is.Null);
        }
        [TestCase("https://evil.example/Noiser-update.json")]
        [TestCase("http://github.com/miacadaver/Noiser/releases/download/noiser-3.11.2-stream.14/Noiser-update.json")]
        [TestCase("https://github.com/other/Noiser/releases/download/noiser-3.11.2-stream.14/Noiser-update.json")]
        public void ForeignMetadataIsRejected(string url)
        {
            using var http = ReleaseClient(Update(), Release(Update(), url));
            Assert.ThrowsAsync<InvalidDataException>(async () => await new GithubUpdateClient(http).CheckAsync(156, CancellationToken.None));
        }
        [Test] public void IncompleteReleaseIsRejected()
        {
            using var http = ReleaseClient(Update(), "{\"draft\":false,\"prerelease\":false,\"tag_name\":\"noiser-3.11.2-stream.14\",\"assets\":[]}");
            Assert.ThrowsAsync<InvalidDataException>(async () => await new GithubUpdateClient(http).CheckAsync(156, CancellationToken.None));
        }
        [Test] public void MismatchedReleaseAndManifestAreRejected()
        {
            var update = Update(); update.Size++;
            using var http = ReleaseClient(Update(), metadata: JsonSerializer.Serialize(update));
            Assert.ThrowsAsync<InvalidDataException>(async () => await new GithubUpdateClient(http).CheckAsync(156, CancellationToken.None));
        }
        [Test] public void OversizedMetadataIsRejected()
        {
            using var http = ReleaseClient(Update(), metadata: new string(' ', 16385));
            Assert.ThrowsAsync<InvalidDataException>(async () => await new GithubUpdateClient(http).CheckAsync(156, CancellationToken.None));
        }
        [TestCase("SchemaVersion")] [TestCase("PackageName")] [TestCase("Sha256")] [TestCase("ApkUrl")]
        [TestCase("Size")] [TestCase("VersionCode")] [TestCase("VersionName")]
        public void InvalidManifestIsRejected(string property)
        {
            var update = Update();
            switch (property)
            {
                case "SchemaVersion": update.SchemaVersion = 2; break;
                case "PackageName": update.PackageName = "wrong.app"; break;
                case "Sha256": update.Sha256 = "invalid"; break;
                case "ApkUrl": update.ApkUrl += "?redirect=1"; break;
                case "Size": update.Size = AppUpdate.MaximumApkBytes + 1; break;
                case "VersionCode": update.VersionCode = 0; break;
                case "VersionName": update.VersionName = "../bad"; break;
            }
            Assert.Throws<InvalidDataException>(() => update.Validate());
        }
        [Test] public void ArchiveMustMatchPackageVersionAndInstalledSigners()
        {
            var update = Update();
            Assert.DoesNotThrow(() => update.ValidatePackage(AppUpdate.Package, 157, 156, new[] { "AB" }, new[] { "ab" }));
            Assert.Throws<InvalidDataException>(() => update.ValidatePackage("other.app", 157, 156, new[] { "ab" }, new[] { "ab" }));
            Assert.Throws<InvalidDataException>(() => update.ValidatePackage(AppUpdate.Package, 158, 156, new[] { "ab" }, new[] { "ab" }));
            Assert.Throws<InvalidDataException>(() => update.ValidatePackage(AppUpdate.Package, 157, 157, new[] { "ab" }, new[] { "ab" }));
            Assert.Throws<InvalidDataException>(() => update.ValidatePackage(AppUpdate.Package, 157, 156, new[] { "bad" }, new[] { "ab" }));
            Assert.Throws<InvalidDataException>(() => update.ValidatePackage(AppUpdate.Package, 157, 156, Array.Empty<string>(), Array.Empty<string>()));
            Assert.Throws<InvalidDataException>(() => update.ValidatePackage(AppUpdate.Package, 157, 156, new[] { "ab", "extra" }, new[] { "ab" }));
        }
        private static HttpClient DownloadClient(byte[] data, long? length = null) => new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
            response.Content.Headers.ContentLength = length ?? data.Length;
            return response;
        }));
        [Test] public async Task ValidDownloadCommitsOnlyVerifiedBytes()
        {
            using var http = DownloadClient(Apk);
            string path = Path.Combine(directory, "Noiser.apk");
            await new GithubUpdateClient(http).DownloadAsync(Update(), path, null, CancellationToken.None);
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(Apk));
            Assert.That(File.Exists(path + ".part"), Is.False);
        }
        [TestCase("hash")] [TestCase("short")] [TestCase("long")] [TestCase("header")]
        public void BadDownloadNeverReplacesCommittedApkAndCleansPartialFile(string failure)
        {
            var bytes = (byte[])Apk.Clone();
            if (failure == "hash") bytes[0] ^= 1;
            if (failure == "short") Array.Resize(ref bytes, bytes.Length - 1);
            if (failure == "long") Array.Resize(ref bytes, bytes.Length + 1);
            using var http = DownloadClient(bytes, failure == "header" ? Apk.Length + 1 : Apk.Length);
            string path = Path.Combine(directory, "Noiser.apk");
            File.WriteAllText(path, "previous");
            Assert.ThrowsAsync<InvalidDataException>(async () => await new GithubUpdateClient(http).DownloadAsync(Update(), path, null, CancellationToken.None));
            Assert.That(File.ReadAllText(path), Is.EqualTo("previous"));
            Assert.That(File.Exists(path + ".part"), Is.False);
        }
        [Test] public void CancelledDownloadDoesNotPublishApk()
        {
            using var http = DownloadClient(Apk);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            string path = Path.Combine(directory, "Noiser.apk");
            Assert.CatchAsync<OperationCanceledException>(async () => await new GithubUpdateClient(http).DownloadAsync(Update(), path, null, cancelled.Token));
            Assert.That(Directory.GetFiles(directory), Is.Empty);
        }
        [Test] public void RateLimitedChecksCanBeRetriedByUser()
        {
            using var http = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
            Assert.ThrowsAsync<HttpRequestException>(async () => await new GithubUpdateClient(http).CheckAsync(156, CancellationToken.None));
        }
    }
}
