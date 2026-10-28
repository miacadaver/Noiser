# Next-song preloading validation — 2026-10-28

**3.11.2-stream.16 (159)**, merged in [PR #7](https://github.com/miacadaver/Noiser/pull/7), commit `f917b583b25a4defa97ae0b6efd36a39a074d45d`.

- [Main Android build and publication](https://github.com/miacadaver/Noiser/actions/runs/40725943890) passed compilation, Media3 contract/resource checks, signing checks, and release publication. The publication gate reported **809 host tests passed, 1 skipped**.
- **25 prefetch tests** cover completed-file handoff, cancellation before fallback, rapid queue edits, failed/partial/oversized transfers, timeouts, identity checks, file leases, total-cache bounds, observer failures, abandoned files, and completed-buffer detachment. Local host tests and 45 theme contrast checks passed.
- APK certificate SHA-256 is unchanged: `d30cc162e830346498ad2f111a20bc29eede5a224ca1ab4423369e23ccf840ba`.
- [Published release](https://github.com/miacadaver/Noiser/releases/tag/noiser-3.11.2-stream.16) includes `Noiser.apk`, update metadata, and the public signing fingerprint. An anonymous request verified the published metadata's version code **159**, expected package, and exact APK URL.
- APK metadata reports **180,274,178 bytes**, SHA-256 `7815132dbb0efc4bc7d58fa6284a15517c5daa5b1ebec6683ff244b463a33f78`.

The feature is **off by default** under Settings → General → Preload next song. It requires Autoplay, waits until the current download completes, and skips files over 32 MiB. There is no claim of gapless playback.

Actual Soulseek peers, cached-file playback on Android, screen-off transitions, and device-specific cancellation behavior remain unverified on a phone. Follow the [device checklist](../next-song-preloading.md#verification) before treating those cases as accepted. Host tests use controlled transfer delegates; Android CI checks compilation and contracts rather than a running media session.
