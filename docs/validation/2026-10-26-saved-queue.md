# Saved playback queues — October 26, 2026

Queue source entries, stable IDs, order, current selection and repeat/autoplay settings now persist
in dedicated private Android preferences. They restore as **Saved queue**, without a player source,
audio transfer, playback timer or identifying media metadata. Play starts the selected song from
the beginning through the normal playback path and reads its tags again. Stop/Clear Queue remove
the saved snapshot; service teardown, app shutdown and paused task dismissal preserve it.

The versioned codec uses generated System.Text.Json metadata and validates the complete snapshot
before replacing a queue. It preserves source encoding flags and disc numbers. Cached audio, tags,
artwork, errors, playing state and position are not persisted. Corrupt or unsupported data is discarded.
Queue revisions/settings checkpoints avoid persistence work on ordinary progress or metadata updates.
Snapshots are bounded to 10,000 entries and 2,097,152 JSON characters; an oversized replacement drops
the older saved snapshot rather than restoring an unrelated queue later.

## Checks completed

- Release host suite using `TestCategory!=RealData`: **781 passed, 1 skipped, 0 failed**. Twenty-eight
  new cases cover round-tripping source/selection/settings, Unicode and encoding flags, edits/removal,
  empty queues, stale metadata/error state exclusion, invalid versions/records, size/count bounds,
  source revisions and repeat/autoplay coherence.
- Standalone Debug Android APK build succeeded with zero errors. Existing compiler, resource,
  assembly-reference and Java warnings remain. Generated Media3 Java callback/manifest checks passed.
- **12 installed-APK checks passed** on an Android 15/API 35 x86_64 emulator. The fixture was seeded
  in the app's private queue preferences; tests then used the normal MainActivity and player UI.
  See [results](2026-10-26-saved-queue-results.txt).

Cold launch restored the selected file and an empty, nonplaying native media session with null
metadata and an empty session timeline. Next changed the saved selection without starting audio;
repeat/autoplay changes survived force-stop and cold relaunch. Removing the selected row preserved
the remaining paused queue. Clear Queue and explicit Stop removed the snapshot, and a subsequent
cold launch did not resurrect it. Malformed saved JSON was discarded without a crash. No fatal
exception or app ANR was observed in the captured log.

Explicit Play was checked while logged out: it entered the normal source path and reported the
expected login failure while preserving the queue. This does not claim live-peer audio resumed.
Physical phone, authenticated live-peer, paused-task swipe-away and real-scrobbler tests remain
outstanding. Playback-position restoration is a separate pending improvement.

Temporary emulator logs and UI helpers are in `/tmp/noiser-saved-queue-validation/`; no validation
Activity or fixture is included in the app. The emulator was stopped after testing.
