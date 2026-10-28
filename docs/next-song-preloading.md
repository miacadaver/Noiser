# Next-song preloading

Available in **3.11.2-stream.16 (159)**. Enable **Settings → General → Preload next song**. It is off by default and requires Autoplay to be on. Repeat One does not preload another song.

While a song is playing, Noiser waits for that song's transfer to finish, then tries to download the immediate next queued MP3 into private temporary storage. Files over **32 MiB** or with unknown sizes are skipped. Preloading uses additional data; it does not promise gapless playback or bypass a peer's upload queue.

If the complete file is ready at the transition, the existing local-file player path plays it. If it is unfinished, stale, missing, or the wrong size, preloading is cancelled and its transfer is allowed to finish cancelling before the normal streaming request starts. A prefetch error does not fail the current track. Permanent completed downloads retain precedence.

## Ownership and limits

- One player and MediaSession remain. Only one playback-related peer download runs at a time: current streaming completes before prefetch starts; foreground fallback waits for cancelled prefetch cleanup. Unrelated ordinary downloads keep their existing behavior.
- Only the immediate next queue entry is considered. Repeat All can wrap to the first entry; duplicate current sources, Repeat One, Autoplay off, pause/end states, Stop, account changes, and disabling the setting suppress or cancel preloading. A queue edit that changes the next entry cancels the obsolete attempt. An unchanged next entry retains its cache.
- A five-minute deadline bounds a speculative attempt. The existing transfer layer may make its one bounded retry; subsequent playback refreshes do not retry a failed prefetch. A new queue selection, resume, or preference change can create a new attempt.
- Cache identity includes entry ID, peer, exact remote path, size, and decoding flags. Only an exact-size, successfully finished transfer may detach its temporary buffer and become a ready file.
- Cache files live in `cache/mp3-prefetch/`. A consumed file belongs to the current track until its stop/cleanup; stale ready files and partial transfers are discarded. Abandoned files are removed at the next process startup. Nothing is replayed or downloaded by startup/queue restoration.
- Each file is at most 32 MiB. The cache checks a **64 MiB** total budget, covering the current leased file plus the next file and any files that could not be deleted. Insufficient space or cleanup failures cause optional work to be skipped. Ordinary streaming cache is separate.
- Network work uses the authenticated transfer path, existing cancellation and speed governor, and the current service's transfer locks. There is no persisted download row, second player, extra service, or metadata publication for the next song.

## Verification

Host tests exercise next-entry selection, repeat/autoplay, size limits, complete versus partial files, stable ownership, cancellation barriers, rapid queue changes, failure/timeout handling, total-cache bounds, missing/modified files, abandoned-cache cleanup, and completed-buffer detachment. Android compilation checks the setting and local-file handoff. Actual peer and Android lifecycle behavior still require a phone.

Device acceptance:

1. Leave preloading off and verify ordinary playback, seeking, and queue transitions.
2. Enable it with Autoplay on and queue two small MP3s from a responsive peer. Wait for both downloads; confirm the next song plays with the correct title/artwork.
3. Try an offline or queued peer, then manually skip before prefetch completes. Normal streaming must remain available after cancellation, and the current song must not be marked failed by prefetch errors.
4. Rapidly skip, insert/remove the next song, pause, turn preloading off, and press Stop. Verify stale downloads and cache files are cleaned up and no duplicate transfer remains.
5. Test Repeat One/All, existing local downloads, screen-off transitions, task removal, account changes, and reopening after process death. Preloading must not run from an offline saved queue preview.
