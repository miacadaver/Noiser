# Metadata publication gate — October 26, 2026

The earlier album-only fix still published a provisional title/artist with an empty album while
tags loaded. It also left the folder-derived album visible in the full player. This change withholds
identifying Android media metadata until the selected audio track's tag read finishes, then publishes
the result together. The mini-player and full player show their generic title until that point.
The album display and Android session use only an actual embedded album tag.

The 1.5-second audible startup wait remains bounded; its timeout does not complete tag reading or
release provisional metadata. Native duration updates preserve the gate. Confirmed untagged tracks
may publish title/artist fallbacks, and a genuine missing album is allowed. Queue copies and updates
preserve each track's completion state. Switching tracks starts a new pending tag read.

Release host tests with `TestCategory!=RealData`: **716 passed, 1 skipped, 0 failed**. Nine new cases
cover repeated duration updates during delayed tag reading, publication of the complete tagged
result, missing/empty album tags, confirmed untagged playback, independent subsequent tracks,
queue copies and updates, and completing the current track without releasing the next one's guesses.
Generated Media3 Java callback contracts and the media-session manifest checks also passed.
The standalone Debug Android APK build succeeded with zero errors. Existing compiler, resource and
assembly-reference warnings remain.

These host tests verify the shared state and queue behavior; they do not execute Media3 on Android
or prove a real scrobbler's behavior. The earlier emulator results describe the preceding album-only
fix and must not be treated as runtime validation of this publication gate.
