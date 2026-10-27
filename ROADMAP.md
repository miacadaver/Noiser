# Noiser roadmap

Noiser's goal is a dependable Android Soulseek client with integrated playback. This roadmap separates implemented behavior from proposed work. It is a priority order, not a release-date promise.

## Available now

- [x] MP3 playback while receiving a peer transfer, with temporary-cache cleanup.
- [x] Search/Browse playback entry points, a mini-player, and Android media controls.
- [x] Playback queues, repeat, autoplay, and seeking within parsed audio.
- [x] Offline recovery of the queue/current song and remembered folder, paused; explicit Play starts from zero.
- [x] Embedded tags/artwork and bounded metadata startup handling.
- [x] Noiser app name, refreshed Seeker-derived bird, and Android icon variants.
- [x] Noiser Mint light, dark, and AMOLED interface palettes matching the bird icon.
- [x] APK and host-test workflows for `main` and pull requests.
- [x] Folder bookmarks with saved peer/path, rename/remove, Browse and search-folder entry points.
- [x] Icon-based player controls with state labels and accessibility descriptions.
- [x] Manual in-app update checks, verified APK downloads, and Android installer handoff via GitHub Releases.

The current foundation is the phone-confirmed rollback. Its removed background changes remain removed; offline queue recovery is a new, separate implementation without automatic player/service startup. See [baseline history](docs/validation/2026-10-26-full-background-rollback.md).

## 1. Validate the working build

### Agreed implementation priorities

1. **Persistent development signing:** enabled on `main` with a configured repository secret and a pinned public certificate. CI checks the restored key and the published APK; a settings-preserving physical-device update remains to be checked. See [setup and transition instructions](docs/development-signing.md).
2. **Android lifecycle regression coverage:** screen-off playback, activity recreation, interruptions, Bluetooth controls, rapid track changes, and Stop during stalls; record physical-device results.
3. **Protected credential storage:** Keystore-backed encryption, migration of existing preferences, and explicit credential exclusions from backups/exports.
4. **Buffering and failure presentation:** distinguish peer queue, connection, buffering, retry, and terminal failure; add a dependable user Retry action before broader automatic recovery.
5. **Saved queue/current song:** implemented as offline, paused recovery with explicit Play from zero. Position recovery is optional future work and is outside the agreed implementation.
6. **Shareable playback diagnostics:** app/Android versions, player/transfer state, and failure cause, with credentials and personal data removed.

Reduce static activity references and split large playback-related classes incrementally alongside these changes.

- [ ] Install and launch the newly branded build on the Samsung Android 16 test phone; confirm the launcher, round/adaptive/themed icons, and retained settings on a same-signature update.
- [ ] Exercise real-peer MP3 playback, queue transitions, bounded seeking, and Stop during a stalled transfer.
- [ ] Validate screen-off playback, Bluetooth/headset controls, interruptions, task dismissal, and cleanup on physical devices.
- [ ] Check real external scrobblers, artwork, tag timing, and long-session battery use.
- [ ] Capture current Noiser search, player, queue, and notification screenshots to replace the inherited/validation gallery.
- [x] Configure persistent development signing with a pinned certificate and fail-closed APK verification.
- [x] Verify two successive APK builds use the same certificate. See [CI evidence](docs/validation/2026-10-26-development-signing.md).
- [ ] Verify a same-certificate update on the physical phone retains settings.

**Done when:** device model, Android version, APK/commit, and pass/fail results are recorded, with regressions resolved or clearly documented.

## 2. Restore recovery and persistence incrementally

- [ ] Improve buffering/retry presentation without changing startup ownership.
- [ ] Reintroduce bounded automatic playback recovery with explicit cancellation and no duplicate transfers.
- [x] Restore a versioned saved queue/current selection offline and paused, without creating a player or contacting peers.
- [ ] Validate offline queue recovery and explicit Play on the physical Samsung phone.
- [ ] Optional future position recovery, respecting received/seekable audio on a new transfer; not included in queue recovery.

**Done when:** each feature lands separately with meaningful host tests and physical-device acceptance checks. Replayed service intents, rapid selections, Stop, and fresh installs must remain predictable.

## 3. Improve the listening experience

- [ ] Refine queue editing, error messages, and accessibility.
- [ ] Review credential storage and backup behavior before broader distribution.
- [ ] Investigate prefetching and additional audio formats against peer/network, cache, and battery costs.
- [ ] Publish a short real-device demo and a clearly versioned installation guide.

**Done when:** proposed capabilities have measured behavior, clear limits, and a device-tested implementation before being advertised as supported.

## Reporting useful feedback

[Open an issue](https://github.com/miacadaver/Noiser/issues) with the app version, device/Android version, entry point (search or Browse), file type, expected behavior, and reproduction steps. Remove credentials, private messages, and other personal information from logs or screenshots before sharing.
