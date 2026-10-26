# Streaming transfer power — October 26, 2026

**Historical validation:** this optimization was reverted in `3.11.2-stream.6` as a recovery step
after a Samsung Galaxy A07 / Android 16 startup-crash report. The downloaded `d3485cb` APK launched
on Android 15 and Android 16 emulators, including with a saved queue, so its connection to the phone's
crash remains unconfirmed. The results below describe the earlier optimization, not the recovery build.

Previously, `IsReceiving` kept the transfer CPU and high-performance Wi-Fi locks held for the entire
request, including peer queues, reconnect delays and stalled transfers. Streaming now uses a bounded
`StreamingPowerLease`, separate from the player's existing audio wake protection.

Requested, initializing and in-progress state transitions grant 20 seconds of transfer protection.
Only increasing committed byte counts renew that window; duplicate state/progress reports cannot
keep a stall awake. Queued and completed states revoke it immediately, including the queued state
published before retry backoff. A new attempt or leaving the peer's queue grants protection again.
Waiting for the previous source's cleanup acquires no transfer locks.

A five-second timer checks expiry while a lease exists, scheduling sooner at its
deadline. After expiry it releases both locks and stops that timer. The first bytes that renew an
expired lease request a coalesced main-thread lock check; healthy progress does not post per-chunk
UI/session updates. CPU acquisition has a 21-second OS timeout as an additional bound and renews at
most every five seconds, even when metadata events publish frequently. Stop, failure, source replacement
and service shutdown retain their existing ownership and cleanup guards.

Pausing audio still allows an active transfer to finish with protection renewed by actual incoming
bytes. Completed local playback needs no transfer locks. Ordinary downloads/uploads and ExoPlayer's
local audio wake mode are unchanged. This follows Android's guidance to
[release streaming locks when no longer needed](https://developer.android.com/media/platform/mediaplayer/background)
and [bound manual wake locks](https://developer.android.com/develop/background-work/background-tasks/awake/wakelock).

## Verification

- Release host suite using `TestCategory!=RealData`: **803 passed, 1 skipped, 0 failed**. Thirteen new
  cases cover idle expiry, duplicate reports, fresh bytes, inactive states, retries, queue transitions,
  and delivery of progress callbacks only after committed bytes increase. Existing streaming recovery,
  cancellation, seek, metadata and persistence tests continue to pass.
- **13 Android 15 emulator checks passed**, using the real service, CPU lock, Wi-Fi lock and expiry
  handler with controlled transfer states and a private progressive buffer. See
  [results](2026-10-26-transfer-power-results.txt). Incoming bytes renewed protection beyond the initial
  lease; inactivity then released both locks, and fresh bytes requested successful reacquisition.
  Completion, transfer exit and service shutdown released them too. The host turned the screen off
  during the run. No fatal app exception or app ANR appeared in the captured log.
- Generated Media3 callback contracts and media-session manifest checks passed for the regular build.
  Its manifest excludes the temporary validation Activity.
- A clean standalone Debug rebuild with embedded assemblies passed: **411 existing warnings, 0 errors**.
  The regular managed assembly also excludes the probe; the signed APK contains the normal app.

The temporary probe is outside the repository in `/tmp/noiser-power-validation/`. It injects transfer
states to isolate native lock management; it does not establish live-peer reconnection or audible
screen-off playback reliability. The regular APK is built without the probe Activity.

Battery savings, physical-phone screen-off behavior and live-peer handoffs require device testing;
there is no measured battery-life claim.
