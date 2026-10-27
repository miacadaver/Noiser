# In-app updates

Starting with **3.11.2-stream.13 (156)**, open **Settings → Check for updates**. Noiser checks its public GitHub Releases, shows the newer version, download size, and release notes, then offers **Download update**. Once verification succeeds, tap **Install update** to open Android's installer.

On Android 8 or later, the first install may require allowing installations from Noiser. Enable this in the Android screen that opens, return to Noiser, and tap **Install update** again. Android controls installation confirmation. Cancelling the installer leaves the existing app intact. Noiser never uninstalls the current app or asks you to clear its data.

Install the first updater-enabled APK manually. A matching package and signing key allow later APKs to replace it while retaining app data. Copies signed with a different key (including disposable PR validation builds) are rejected by the updater. The existing development signing key/package remain unchanged; this is the Noiser development build channel, separate from upstream Seeker/Play/Izzy builds.

## Lifecycle and verification

- Checks are manual. Opening the update sheet performs a check; app startup and playback do not poll for releases.
- Only a complete, published non-prerelease from `miacadaver/Noiser` is accepted. Version **codes** determine whether an update is newer; labels are not compared alphabetically.
- The release must include `Noiser.apk` and `Noiser-update.json`, whose tag, exact repository asset URLs, version, APK size, and schema agree. No authentication token or Soulseek credentials are sent.
- Metadata and APK downloads are bounded (512 KiB release response, 16 KiB manifest, 512 MiB APK). Checks have a 45-second deadline; downloads have a 30-minute deadline.
- The APK is streamed into a private cache file with byte-count and SHA-256 verification. Partial downloads are removed and cannot be installed. Interrupted downloads restart rather than resume.
- Android package parsing checks the package name, version code, and exact installed signing-certificate set. Package identity, certificate, length, and digest are checked again before opening the installer. Signing-key rotation is intentionally unsupported for now.
- APK access is granted through FileProvider for `cache/noiser-updates/` only. The updater requests no general storage permission. Existing unrelated provider mappings are retained.
- Dismissing/recreating the update view cancels its work; there is no download service, startup task, or media-session dependency. Verified cache files remain available while the Android installer reads them. Old update files are cleaned up after two days when another download is requested.
- Network failures, GitHub rate limits, unavailable storage, and verification errors leave the installed app untouched and allow a manual retry.

## Publishing

`.github/workflows/android.yml` builds and verifies the APK using the persistent key. `.github/scripts/update-manifest.py` creates `Noiser-update.json` from the source Android manifest and actual APK bytes. Only a `main` push can publish an update. A separate job downloads that verified artifact, requires the host tests to pass, and creates a draft release with all three assets (APK, update manifest, public signing fingerprint) before publishing it as latest.

Tags use `noiser-<versionName>` to keep this channel separate from the upstream `v*` release workflow. Already-published versions and their assets are not overwritten. Increase Android `versionCode` and `versionName` for each new app version; keep version codes increasing. Update `docs/latest-release-notes.md` for the new release. A failed draft publication can be rerun without exposing an incomplete release.

A documentation-only commit at the same app version does not replace the existing release. Published assets are the source for in-app downloads; temporary Actions artifact ZIPs are not used by the updater.

## Validation

Host tests cover newer/older versions, incomplete and foreign releases, metadata bounds, malformed manifests, package/signature/version mismatches, verified downloads, length/hash failures, cancellation, and HTTP errors. Python tests cover manifest generation and unsafe identities. Android compilation and persistent certificate verification run in CI.

Physical-device acceptance remains required: open Settings, check the current version, publish a later version, download it, exercise permission denial/approval and installer cancellation, then install and confirm settings and saved queue remain intact. Also check offline failures, closing/rotating during download, and retry after an interrupted download.
