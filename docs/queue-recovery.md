# Restore a saved queue, paused

Version **3.11.2-stream.11** remembers the queue and the selected song. On reopening, Noiser restores an offline folder preview and shows the remembered song paused in the mini-player. Tap the mini-player to choose a queued song or clear the queue. Choosing a row changes the saved selection without starting playback; **Play** explicitly reconnects and starts the selected song from the beginning.

No playback position is stored. Restoration does not create a player, MediaSession, foreground service, peer transfer, notification, wake lock, or retry loop. Existing normal app login behavior is unchanged. This feature makes unexpected closure less disruptive; it does not prevent Android from terminating the process.

## Folder and offline behavior

The saved peer and folder come from the selected song's remote path. Browse initially shows only the known queue tracks in that same folder and from that same peer, with a **Saved queue tracks only** notice. This is not presented as the peer's complete current listing. Tap the notice to fetch the full listing using the existing Browse flow. The saved view remains available if the peer is offline.

A launcher reopen returns to the remembered folder when Browse has no newer results and the user has not already selected another tab or song. Notifications, deep links, activity recreation, and newer user selections take precedence. The queue may span multiple folders or peers; its full ordering and current entry are kept independently of the folder preview.

## Checkpoints and lifecycle

- The selected identity is checkpointed before starting its player/transfer. Queue replacements, appends, removals, track transitions, and metadata-label updates also checkpoint it.
- Progress updates, seek parsing, and periodic UI refreshes do not write checkpoints.
- Data is written to an app-private `files/playback-queue-v1.json.tmp`, flushed, then atomically moved/replaced as `playback-queue-v1.json`. A partial temporary file cannot replace the last committed queue on recovery.
- The versioned format retains stable queue IDs, peer/path/size, decoding flags, and small title/artist/album labels. It excludes artwork, audio buffers, native objects, transfer/error states, duration, position, and play intent.
- Saved data is bounded to 2,000 entries and a conservative 4 MiB encoded-size budget. Missing, corrupt, oversized, unsupported-version, or account-mismatched data is ignored without blocking startup.
- Stop, Clear queue, and account changes discard saved recovery data. Closing views, ordinary shutdown, inactive task removal, and service destruction retain the last committed queue. The media service remains `NotSticky` and does not replay dead transfer requests.
- A generation check prevents an older disk read from overwriting a newer selection or clear operation. Recovery runs after the Activity is visible and contains no playback-service startup work.

After Play, existing local-download reuse, metadata gating, progressive buffering, and transfer-failure handling apply. If the peer is unavailable, playback can fail normally, while the queue remains saved. Repeat/autoplay use the normal player defaults after process recreation; they are not part of this small recovery format.

## Verification

Host tests exercise queue order/current identity, Unicode and decoding flags, distinct IDs for duplicate songs, metadata-only serialization, repeat-checkpoint deduplication, atomic replacement, interrupted writes, corrupt/future data, resource limits, owner isolation, folder filtering, and clearing. These tests cover the common model/store, not Android lifecycle rendering.

Physical-device checks still needed:

1. Play the second song of a folder, append another track, and background the app. Force-stop it without clearing app data, then reopen it manually.
2. Verify the selected song and queue order, saved folder notice, paused mini-player, and absence of automatic audio or a new playback notification.
3. Select a different saved song: it must remain paused. Press Play and verify it starts from zero using the normal player path.
4. Repeat offline, with a peer unavailable, while rotating, and while making a newer selection during restoration. No automatic download should be started by restoration.
5. Clear the saved queue or press Stop, reopen, and verify it does not reappear. Check that activity recreation during live playback keeps the live player intact.
6. Refresh the saved folder and confirm the notice disappears only when a live browse response replaces it.

The older rollback record remains historical. This is a new offline-only implementation; it does not reintroduce the removed automatic reconnect, saved-position, battery, or startup experiments.
