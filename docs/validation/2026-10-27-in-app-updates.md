# Noiser icons and in-app updater validation — 2026-10-27

Version **3.11.2-stream.13**, version code **156**, package `com.companyname.andriodapp1.rollback`.

- Player icons merged in [PR #3](https://github.com/miacadaver/Noiser/pull/3), commit `9622f186b7510482522edf44863aca2d942608a4`. Its Android build and tests passed.
- Updater merged in [PR #4](https://github.com/miacadaver/Noiser/pull/4), commit `3fbbcb18a781ac12e302bf8f4970f507a9503404`.
- [Main build and publication](https://github.com/miacadaver/Noiser/actions/runs/40440916388) passed Android compilation, callback-contract/resource/theme checks, signing safeguards, update-manifest generation, APK certificate verification, and release publication. The publication job required **764 host tests passed, 1 skipped**.
- Local checks also passed: **764 host tests**, including **29 update cases**; **4** manifest-generation tests; **15** signing tests; **45** contrast checks.
- APK certificate SHA-256: `d30cc162e830346498ad2f111a20bc29eede5a224ca1ab4423369e23ccf840ba` (unchanged).
- [Published release](https://github.com/miacadaver/Noiser/releases/tag/noiser-3.11.2-stream.13) contains `Noiser.apk`, `Noiser-update.json`, and the public signing fingerprint. No temporary Actions artifact is required for installation.

## Live updater-client smoke check

An external temporary console harness referenced `Common.Updates.GithubUpdateClient` directly, with no GitHub authentication headers. It performed real network operations against the published release:

1. Installed-version input **155** returned **3.11.2-stream.13 / 156**, including the release notes and exact public download URL.
2. Downloaded **180,208,387 bytes** via `DownloadAsync`; its size and SHA-256 checks passed. APK SHA-256: `e5571469f71131d63912bac443db09ca718c342596cabb8810e837a19ec80d72`.
3. Installed-version input **156** returned no newer published update.
4. Before first publication, a real check gracefully handled GitHub's missing-release response.

This verifies the live release format, anonymous access, download stream, and integrity checks on the host. It does not exercise Android PackageManager, FileProvider grants, install-source settings, or the Android installer.

## Device checks pending

Review the icon layout and state labels on the phone. Install this APK manually once, then exercise a later version through Settings → Check for updates, including permission denial/approval, installer cancellation, rotation/interrupted downloads, and a successful same-signature update that retains settings and the saved queue. The updater does not prevent Android process termination and does not add background playback work.
