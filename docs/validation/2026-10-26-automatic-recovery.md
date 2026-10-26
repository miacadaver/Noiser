# Bounded automatic stream recovery — October 26, 2026

Temporary peer/network failures now allow three reconnects per live stream, with delays of 2, 4
and 8 seconds (four total requests). Requests remain sequential, use fresh tokens, and resume at
the committed byte offset in the same buffer. The player and playback position are retained.
The retry count does not reset when bytes arrive, so an unstable peer cannot cause endless retries.
Stop and track replacement cancel both active requests and pending backoff delays.

Connection closure, timeout, failed peer negotiation, reported transfer failure, and temporary
socket/network errors are eligible. Rejected/unshared files, offline peers, changed sizes, storage
errors, unknown failures and cancellation fail normally. An initial missing server login still fails
immediately; a login interruption during existing recovery may use the remaining attempts while the
app reconnects its server session. This does not initiate authentication or switch network settings.

Buffer readers can wait until 90 seconds after the first transient failure in an outage, preserving
any longer normal initial queue wait. More failures without bytes cannot renew the window. New
committed bytes clear it so a later outage can have its own window, within the overall three-retry
budget. Terminal transfer errors wake readers immediately. This prevents the ordinary 45-second
read timeout from cutting recovery short while retaining bounded waits.

Release host suite using `TestCategory!=RealData`: **753 passed, 1 skipped, 0 failed**. New tests cover
socket-error classification, permanent failures, the 2/4/8-second backoff and retry limit, repeated
drops with exact offsets and byte contents, distinct request tokens, a maximum of one active transfer,
network handoff followed by temporary missing login, reservation retention during backoff, Stop,
reader recovery windows, and prompt reader failure/cancellation. The original duplicate-stream,
ordinary-download exclusion, metadata and playback tests still pass.

Generated Media3 callback contracts and media-session manifest checks passed. These host tests use
a simulated peer and the real shared buffer/download service; they do not exercise Android audio or
an actual Wi-Fi/mobile handoff. Live-peer, screen-off and physical-network-switch tests remain needed.
The standalone Debug Android APK built successfully with zero errors; existing compiler, resource
and assembly-reference warnings remain.
No process restoration or persisted queue/position is introduced by this change.
