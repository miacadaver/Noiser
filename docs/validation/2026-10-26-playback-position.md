# Saved playback positions — October 26, 2026

The selected queue entry now restores its last checkpoint, paused until explicit Play. Queue schema
2 adds a position while still accepting schema 1 at zero. A separate small private preferences file
holds live checkpoints, matched against entry ID, peer, remote path and size. This avoids rewriting a
large queue every five seconds. Pause, accepted seek, failure and service shutdown also checkpoint;
an abrupt process kill can lose about five seconds of playback progress.

Remote resumes use the normal sequential source and may need to receive the prefix again. Completed
local downloads use the existing local-source path. Audible playback waits for the saved target.
While paused, the parser can advance through validated seek points in steps of at least 30 seconds,
without opening another peer transfer or seeking into unreceived audio. Small per-sample staging
seeks caused repeated decoder flushes in validation; batching the steps fixes that churn. The final
target can be sought as soon as it is inside the validated range, without waiting for another full
step. Manual seeking stays disabled during preparation, and the player shows the resume target.

Stop/Clear Queue remove both stores. Next/Previous, a different selection, completion and Retry from
start reset the bookmark. A target beyond a known duration falls back to zero. Unseekable files report
a resume error with Retry from start available. Pause/Stop/track replacement retain the existing
pending-play ownership checks, preventing late callbacks from starting an old selection.

## Verification

- Release host suite with `TestCategory!=RealData`: **790 passed, 1 skipped, 0 failed**. Nine new cases
  cover position round-tripping, legacy queues, invalid positions, safe staged seeks, final seek
  acknowledgement, short/end-of-track fallback and suppression of tiny repeated preparation seeks.
- **11 Android 15 emulator checks passed**. A temporary probe used a generated silent MP3 through
  the real completed-download source, Media3 extractor, player, service and preferences. See
  [results](2026-10-26-playback-position-results.txt).
- The regular standalone Debug APK was rebuilt without the probe and succeeded with zero errors.
  Existing compiler, resource and assembly-reference warnings remain. Generated Media3 Java callback
  and media-session manifest checks passed; the regular manifest excludes the temporary Activity.

The first playing position was **240,013 ms** for a 240,000 ms target, after a paused staging position
of **212,114 ms** was observed directly on ExoPlayer. The Activity then moved to Home and the host
turned the screen off; the five-second position checkpoint continued advancing. Pause captured
**245,055 ms**, service recreation restored it paused, and the next playing position was **245,080 ms**.
Stop cleared both queue and position. A different peer could not reuse the checkpoint.

For a cold-process check, the captured on-disk queue/position records were restored after the probe's
Stop cleanup, and the app was force-stopped/relaunched into its normal MainActivity. The saved queue
displayed the checkpoint's resume time, with native playback stopped, null media metadata and an
empty session timeline. No fatal exception or app ANR was observed in the validation log.

The probe does not claim live-peer prefix recovery, physical-phone behavior or real scrobbler
verification. Temporary probe sources are outside the repository in `/tmp/noiser-position-validation/`;
the regular APK is rebuilt without that Activity. Battery optimization remains a separate task.
