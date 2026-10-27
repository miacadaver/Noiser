using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using Common.Updates;
using Google.Android.Material.BottomSheet;
using Seeker.Helpers;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Seeker.Updates
{
    public sealed class UpdateBottomSheet : BottomSheetDialogFragment
    {
        private CancellationTokenSource lifetime;
        private HttpClient http;
        private TextView status, notes;
        private ProgressBar progress;
        private Button action;
        private AppUpdate update;
        private string downloaded;
        private bool busy;

        public static void Show(AppCompatActivity host)
        {
            if (!host.SupportFragmentManager.IsStateSaved && host.SupportFragmentManager.FindFragmentByTag("NoiserUpdates") == null)
                new UpdateBottomSheet().Show(host.SupportFragmentManager, "NoiserUpdates");
        }

        public override View OnCreateView(LayoutInflater inflater, ViewGroup container, Bundle savedInstanceState)
        {
            var view = inflater.Inflate(Resource.Layout.update_bottom_sheet, container, false);
            lifetime = new CancellationTokenSource();
            http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            update = null;
            downloaded = null;
            busy = false;
            status = view.FindViewById<TextView>(Resource.Id.updateStatus);
            notes = view.FindViewById<TextView>(Resource.Id.updateNotes);
            progress = view.FindViewById<ProgressBar>(Resource.Id.updateProgress);
            action = view.FindViewById<Button>(Resource.Id.updateAction);
            view.FindViewById<TextView>(Resource.Id.updateInstalled).Text = GetString(Resource.String.update_installed, CommonHelpers.GetVersionString());
            view.FindViewById<Button>(Resource.Id.updateClose).Click += (_, __) => Dismiss();
            action.Click += async (_, __) =>
            {
                if (busy) return;
                if (downloaded != null) await InstallAsync();
                else if (update != null) await DownloadAsync();
                else await CheckAsync();
            };
            _ = CheckAsync();
            return view;
        }

        public override void OnDestroyView()
        {
            lifetime?.Cancel();
            http?.Dispose();
            base.OnDestroyView();
        }

        private long InstalledVersion(Context context)
        {
            var info = context.PackageManager.GetPackageInfo(context.PackageName, 0);
            return OperatingSystem.IsAndroidVersionAtLeast(28) ? info.LongVersionCode : info.VersionCode;
        }

        private bool IsCurrent(CancellationTokenSource owner) => ReferenceEquals(owner, lifetime) && !owner.IsCancellationRequested;
        private void Busy(bool value)
        {
            busy = value;
            action.Enabled = !value;
            progress.Visibility = value ? ViewStates.Visible : ViewStates.Gone;
        }

        private async Task CheckAsync()
        {
            var owner = lifetime;
            var context = Context.ApplicationContext;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(owner.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            Busy(true);
            progress.Indeterminate = true;
            status.SetText(Resource.String.update_checking);
            try
            {
                var found = await new GithubUpdateClient(http).CheckAsync(InstalledVersion(context), timeout.Token);
                if (!IsCurrent(owner)) return;
                update = found;
                if (found == null)
                {
                    status.SetText(Resource.String.update_current);
                    notes.Visibility = ViewStates.Gone;
                    action.SetText(Resource.String.update_check);
                }
                else
                {
                    status.Text = GetString(Resource.String.update_available, found.VersionName, Java.Lang.Long.ValueOf((found.Size + 1048575) / 1048576));
                    notes.Text = found.ReleaseNotes;
                    notes.Visibility = string.IsNullOrWhiteSpace(found.ReleaseNotes) ? ViewStates.Gone : ViewStates.Visible;
                    action.SetText(Resource.String.update_download);
                }
            }
            catch (Exception error) { ShowFailure(owner, error); }
            finally { if (IsCurrent(owner)) Busy(false); }
        }

        private async Task DownloadAsync()
        {
            var owner = lifetime;
            var candidate = update;
            var context = Context.ApplicationContext;
            string directory = Path.Combine(context.CacheDir.AbsolutePath, "noiser-updates");
            string path = Path.Combine(directory, "Noiser-" + candidate.VersionCode + "-" + Guid.NewGuid().ToString("N") + ".apk");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(owner.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(30));
            Busy(true);
            progress.Indeterminate = false;
            progress.Progress = 0;
            status.SetText(Resource.String.update_downloading);
            try
            {
                Directory.CreateDirectory(directory);
                foreach (string old in Directory.GetFiles(directory))
                    if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddDays(-2))
                        try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                var reporter = new Progress<int>(percent =>
                {
                    if (!IsCurrent(owner)) return;
                    progress.Progress = percent;
                    status.Text = GetString(Resource.String.update_download_progress, Java.Lang.Integer.ValueOf(percent));
                });
                await new GithubUpdateClient(http).DownloadAsync(candidate, path, reporter, timeout.Token);
                if (!IsCurrent(owner)) return;
                status.SetText(Resource.String.update_verifying);
                await Task.Run(() => VerifyPackage(context, candidate, path), timeout.Token);
                if (!IsCurrent(owner)) return;
                downloaded = path;
                action.SetText(Resource.String.update_install);
                status.SetText(Resource.String.update_ready);
            }
            catch (Exception error)
            {
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                ShowFailure(owner, error);
            }
            finally { if (IsCurrent(owner)) Busy(false); }
        }

        private static string[] Signers(PackageInfo package)
        {
            var signatures = OperatingSystem.IsAndroidVersionAtLeast(28)
                ? package.SigningInfo?.GetApkContentsSigners() : package.Signatures;
            if (signatures == null) return Array.Empty<string>();
            using var hash = SHA256.Create();
            return signatures.Select(s => BitConverter.ToString(hash.ComputeHash(s.ToByteArray())).Replace("-", "")).ToArray();
        }

        private void VerifyPackage(Context context, AppUpdate candidate, string path)
        {
            var flags = OperatingSystem.IsAndroidVersionAtLeast(28) ? PackageInfoFlags.SigningCertificates : PackageInfoFlags.Signatures;
            var installed = context.PackageManager.GetPackageInfo(context.PackageName, flags);
            var archive = context.PackageManager.GetPackageArchiveInfo(path, flags);
            if (archive == null) throw new InvalidDataException("Cannot read update APK.");
            long version = OperatingSystem.IsAndroidVersionAtLeast(28) ? archive.LongVersionCode : archive.VersionCode;
            candidate.ValidatePackage(archive.PackageName, version, InstalledVersion(context), Signers(archive), Signers(installed));
            using var input = File.OpenRead(path);
            using var hash = SHA256.Create();
            string digest = BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "");
            if (input.Length != candidate.Size || !string.Equals(digest, candidate.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The update file has changed.");
        }

        private async Task InstallAsync()
        {
            var owner = lifetime;
            var context = Context.ApplicationContext;
            Busy(true);
            try
            {
                await Task.Run(() => VerifyPackage(context, update, downloaded), owner.Token);
                if (!IsCurrent(owner)) return;
                if (OperatingSystem.IsAndroidVersionAtLeast(26) && !context.PackageManager.CanRequestPackageInstalls())
                {
                    status.SetText(Resource.String.update_permission);
                    StartActivity(new Intent(Android.Provider.Settings.ActionManageUnknownAppSources,
                        Android.Net.Uri.Parse("package:" + context.PackageName)));
                    return;
                }
                var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, context.PackageName + ".provider", new Java.IO.File(downloaded));
                var intent = new Intent(Intent.ActionView);
                intent.SetDataAndType(uri, "application/vnd.android.package-archive");
                intent.AddFlags(ActivityFlags.GrantReadUriPermission);
                intent.ClipData = ClipData.NewRawUri("Noiser update", uri);
                StartActivity(intent);
            }
            catch (Exception error) { ShowFailure(owner, error); }
            finally { if (IsCurrent(owner)) Busy(false); }
        }

        private void ShowFailure(CancellationTokenSource owner, Exception error)
        {
            if (!IsCurrent(owner)) return;
            Logger.Debug("App update failed: " + error.GetType().Name);
            status.SetText(error is InvalidDataException || error is System.Text.Json.JsonException
                ? Resource.String.update_invalid : error is HttpRequestException || error is OperationCanceledException
                ? Resource.String.update_network_error : Resource.String.update_failed);
            action.SetText(downloaded != null ? Resource.String.update_install
                : update != null ? Resource.String.update_download : Resource.String.update_check);
        }
    }
}
