# Buffering states and manual retry — October 26, 2026

This report describes the earlier presentation/manual-retry change. The subsequent
[automatic recovery change](2026-10-26-automatic-recovery.md) expands the automatic retry budget;
the original single-retry assertions below describe the earlier version.

The full player distinguishes connecting, a peer queue, ordinary buffering, the existing automatic
reconnect attempt, and waiting without new audio for 20 seconds. Queue status takes precedence over
an idle transfer clock, including after a received prefix. Playing buffered audio, pauses, focus
suppression, and completed transfers do not show a stalled warning. Only committed non-empty writes
reset the monotonic progress clock. The existing visible-Activity refresh drives presentation; there
is no new background polling timer.

**Retry from start** is shown for a terminal error or stalled wait. It validates the displayed track
ID and the current playback state, preserves the queue and playback options, and uses the existing
serialized source replacement/transfer cleanup to restart the current song. It does not restore the
old playback position or add more automatic reconnect attempts.

Release host tests using `TestCategory!=RealData`: **735 passed, 1 skipped, 0 failed**. Added coverage
checks phase precedence, stalled/healthy progress, completed transfers, retry availability, progress
clock resets, reconnect notification order, the existing single automatic retry limit, rejection without
retry, and cancellation followed by a fresh same-file request with a maximum of one active transfer.
The offset-based automatic retry still preserves the committed prefix; manual retry starts at zero.

Generated Media3 Java callback contracts and the media-session manifest checks passed. Host tests
exercise the shared presentation and transfer services, not Android button taps or service callbacks.
The standalone Debug Android APK build succeeded with zero errors; existing compiler, resource,
Java and assembly-reference warnings remain.
A physical phone/UI retry test and screen-off, Bluetooth, call and network-switch testing remain
outstanding. The earlier emulator reports describe earlier changes, not runtime validation of this UI.
