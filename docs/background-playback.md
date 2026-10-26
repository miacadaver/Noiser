# Background playback, MediaSession, and mini-player

The manifest reports version **3.11.2-stream.5** (Android version code 148). Minimum Android API is 23;
target API is 36. There is one ExoPlayer, one MediaSession, and at most one active peer stream.
The current code includes MP3 folder queues, repeat/autoplay controls, and bounded seeking.
Playback resumption after process death and direct scrobbling are not implemented.

## Ownership and data flow

`StreamingPlaybackController` owns the Activity's dialog and controller proxy. The service uses
`DownloadService.StreamAsync` with the authenticated Soulseek client, a private `ProgressiveBuffer`,
and a blocking `ProgressiveDataSource` read at the available byte frontier. Its Media3 MP3 extractor
supplies ID3 metadata and tracks safe seek points in the parsed audio prefix.

```text
Soulseek StreamAsync -> growing private cache -> ProgressiveDataSource -> one ExoPlayer
                                                                         |
                                               SessionPlayer (state/command adapter)
                                                                         |
                                              one MediaSession in MediaSessionService
                                                 |                       |
                              native notification / lock screen       MediaController proxy
                              Bluetooth / external controllers        full dialog + mini-player
```

`StreamingPlaybackService` owns the player independently of Activities. The existing transfer,
source setup, metadata work, retry, and cleanup logic lives in its per-track `StreamingPlayback`.
An Activity releases only its controller proxy and views when stopped. Returning/recreating the
Activity connects to the existing service; it does not select a song, create a decoder, or restart a
download. Tapping Play on the same peer/file reuses the active track. A new selection stops the old
reader and waits for its transfer cleanup before starting the new peer request.

User selections are single-use in-process requests, not replayable filenames in exported service
intents. Rapid selections supersede pending selections. The service is `NotSticky`: if Android kills
the process, a dead peer connection is never assumed resumable. Relaunching cleans abandoned cache
files; the user can select Play again. Explicit Stop and app Shutdown cancel reads, release the transfer
reservation, and delete temporary data after transfer shutdown. Advancing the queue performs the same
cleanup for the old track before starting the next remote request. Natural completion advances, repeats,
or holds the current track according to the queue controls. A held track retains its buffer for replay
until another selection or Stop. Pausing retains position and allows the transfer to continue receiving.

## Session and metadata

`SessionPlayer` is a `ForwardingSimpleBasePlayer` adapter around the same ExoPlayer, not another player
instance. Native Media3 listener handling forwards actual playback state and position. A restricted
command set exposes play/pause, stop, volume, metadata/timeline information, available Next/Previous,
queue selection, and seeking within the current track's validated range. General external playlist
mutation is not advertised. Stop also cancels the Soulseek transfer and clears the queue.
Terminal transfer errors become session player errors and disable Play until a new
selection; the mini-player and dialog show the failure.

The session's queue timeline publishes the merged `TrackMetadata`: title, artist, album, album
artist, track number, release year, and reliable duration when the native player supplies it. Extraction
reads the original selected audio format's ID3 data on the existing stream; it makes no extra network
request. Missing fields retain the established conservative filename/folder fallback. Revision and
track ownership checks reject late work from previous songs. Non-current entries use their stored
metadata without artwork; only the active entry uses the current session metadata and artwork.
The session and notification withhold all identifying metadata until the selected track's tag read
finishes, even if the audible startup wait expires. The result is published together, allowing genuine
missing tags to retain title/artist fallbacks. Album always uses the parsed tag in both the player
dialog and session: folder/filename album guesses are never displayed or exported. A confirmed
missing album remains unset. Duration updates and queue copies preserve tag-read completion state.
Each refresh takes one defensive queue snapshot and reuses it throughout timeline construction.

Artwork decoding remains on a worker with Step 2's limits. The in-app cover is at most approximately
512 pixels per side. Session artwork is an additional thumbnail at most 256 pixels per side and
128 KiB encoded. The session timeline excludes original audio format/APIC bytes so multi-megabyte
artwork cannot be sent accidentally through session IPC. Late metadata updates invalidate session
state and update controls without preparing a new source or restarting playback. Streamed MP3
duration can remain unknown until the extractor establishes it; no file-size/bitrate guess is published.

## Foreground service, notification, and audio focus

The manifest declares an exported Media3 session service with type `mediaPlayback`, its session
intent filter, and `FOREGROUND_SERVICE_MEDIA_PLAYBACK`, in addition to existing foreground-service,
network, wake-lock, and Wi-Fi permissions. File selection starts the service from visible UI. Media3's
`DefaultMediaNotificationProvider` owns the media channel, notification, and foreground promotion.
There is no second manually managed media notification or background timer.

Play/resume initially publishes a pending buffering/play intent. The service observes completion of
Media3's normal notification update and starts the underlying ExoPlayer after foreground promotion
and a bounded initial metadata wait of up to 1.5 seconds. The wait helps scrobblers receive parsed tags
at audible playback start; see [metadata behavior](streaming-metadata.md#metadata-and-fallback).
This preserves Android 15+ audio-focus ordering. Pause, track switches, errors, and Stop retire pending
play requests so a late notification completion cannot start an old track. Repeated Play while already
playing completes immediately. Notification preferences include play/pause, available Previous/Next,
and Stop; exact layout depends on the Android version. Dismissal is handled through Media3's stop action.

ExoPlayer handles media audio focus and audio-becoming-noisy events. It responds to focus loss,
interruptions, and headphone unplug events using native behavior. Its local wake mode protects audio
playback. The service holds CPU/Wi-Fi locks only while the existing peer transfer is receiving, including
when playback is paused. Those locks release after transfer completion, failure, or Stop. Streaming
events still kick Seeker's keepalive but no longer create ordinary download notifications/counters;
ordinary downloads and uploads retain their existing behavior.

Media3 manages paused foreground-service demotion and notification updates. Ongoing playback remains
when the task is swiped away; a paused task removal stops and cleans the track. Long-paused playback
is not guaranteed to survive process reclamation. Explicit application Shutdown releases the session,
player, transfer, and locks before closing the Soulseek client.

## In-app use and scrobblers

Tap Play beside an MP3 as before. **Close** on the full dialog minimizes it. The mini-player appears
above MainActivity's bottom navigation, reducing content height instead of covering it. Its artwork,
title, artist, and play/pause state come from the same service snapshot. Tap the mini-player to reopen
the dialog. **Stop** removes it. Other Activities use Android's media controls; the mini-player returns
when the main interface is visible. A 250 ms position refresh runs only while that Activity is visible.

Notification and Bluetooth commands use the same MediaSession. The Activity normally controls it
through a MediaController proxy; while the proxy is connecting it can call the same session player
in-process. It never constructs another playback instance. Old titles/artwork are cleared while a new
request is waiting and when playback is explicitly stopped. A track held at natural completion keeps
its metadata available for replay and queue navigation.

Compatible scrobblers can inspect Android's standard active media session and notification, with title,
artist, album, album artist, duration when known, and native playback transitions. There is no Last.fm
authentication or custom scrobbling service. Pano Scrobbler detection depends on its own settings,
permissions, and Android/OEM behavior; compatibility is not claimed as device-verified.

## Files and dependencies

Created:

- `Common/Playback/PlaybackRequestMailbox.cs`, `PlaybackPhase.cs`: single-use request handoff and
  presentation state, independent of Android UI.
- `Seeker/Playback/StreamingPlaybackService.cs`, `StreamingPlayback.cs`, `SessionPlayer.cs`: service,
  existing per-track engine, and session adapter.
- `Seeker/Playback/PlaybackListener.cs`, `Mp3ExtractorsFactory.cs`: extracted native callback/factory
  classes, preserving the complete Java bridge contract from Step 2.
- `Seeker/Main/MainActivity.Playback.cs`: attach/detach the Activity's controls.
- `Seeker/Resources/layout/stream_mini_player.xml`, `drawable/stream_pause.xml`: compact player assets.
- `UnitTestCommon/PlaybackLifecycleTests.cs`: request handoff and presentation-state cases.
- `Common/Playback/PlaybackQueue.cs`, `PlaybackControls.cs`, `ProgressiveSeekIndex.cs`: queue,
  repeat/autoplay, and parsed-audio seek boundaries, with corresponding host tests.
- `docs/background-playback.md`: architecture and acceptance checks.

Modified:

- `Seeker/Playback/StreamingPlaybackController.cs`: Activity UI and controller proxy only.
- `Seeker/Playback/StreamingMetadata.cs`: safe session metadata and bounded artwork encoding.
- `Seeker/Main/MainActivity.cs`, `CloseActivity.cs`, `Helpers/ForegroundLifecycleTracker.cs`: preserve
  playback across UI lifecycle changes and release it on explicit shutdown.
- `Seeker/Transfers/TransferEventRouter.cs`: avoid ordinary download notifications for media streams.
- `Seeker/Resources/layout/activity_main.xml`, `values/strings.xml`: navigation-safe mini-player and labels.
- `Seeker/Properties/AndroidManifest.xml`, `Seeker.csproj`: media service permission, version, dependency.
- `.github/scripts/check_media3_contracts.py`: session override exports and generated manifest checks.
- `README.md`, `docs/streaming.md`, `docs/streaming-metadata.md`: current behavior and validation.

**Xamarin.AndroidX.Media3.Session 1.11.1** matches ExoPlayer. Session requires AndroidX Core 1.19,
which includes the former core-ktx classes; the `Core.Core.Ktx` package's assets are excluded to prevent
D8 duplicate-class errors, while its version matches Lifecycle's minimum. Lifecycle LiveData, Process,
and ViewModelSavedState are aligned with Session's Service/Runtime 2.11 requirements. DataStore
Preferences 1.2.1 matches the resolved DataStore family instead of Firebase Common's older minimum.
Common uses MessagePack and its analyzer at 2.5.307, staying on the existing major version while
incorporating the [upstream security patches](https://github.com/MessagePack-CSharp/MessagePack-CSharp/releases/tag/v2.5.307).
The existing saved-data fixtures verify serialization compatibility.

## Verification and device acceptance

Maintenance validation on October 26, 2026:

- Release host suite using the CI filter: **696 passed, 1 skipped, 0 failed**. Coverage includes one-time
  request delivery, rapid track supersession, unknown/replayed intents, process recreation, concurrent
  delivery, and stopped/error/queued/connecting/buffering/playing/paused/focus-suppressed presentation.
- Streaming, queue, metadata, and saved-data compatibility tests pass in that suite, including reads
  before transfer completion, frontier waits, cancellation, retry/reservation behavior, metadata fallback,
  malformed ID3 headers, and existing MessagePack cache fixtures.
- Restoring the Android project reports no NuGet dependency-range conflicts or vulnerability warnings.
  NuGet audits of the Android and host-test projects, including transitive packages, report no known
  vulnerabilities with the current advisory feed.
- An isolated 1,000-entry queue traversal allocation check produces the same checksum with both paths:
  repeated `Entries` access allocates 16,056,024 bytes; one snapshot allocates 8,024 bytes. This measures
  list traversal only, not total Android media-session allocations or device performance.
- Standalone Android Debug build with SDK 36/JDK 21 and `EmbedAssembliesIntoApk=true` passed, including
  resources, Java compilation, D8, and debug APK signing. The build reports 411 compiler, analyzer,
  assembly-reference, and resource warnings and no errors; the NuGet warnings addressed here are gone.
- Generated Java/AAR checks: all 37 Player.Listener methods, six extractor factory methods, seven
  extractor methods, five session-player overrides, and two service overrides are exported. The generated
  manifest has the exported mediaPlayback service, intent filter, and required foreground permission.
- The native Media3 MP3 seek probe passes for CBR, VBR, ID3-prefixed VBR, and its unmasked control.

Subsequent [Android 15 emulator validation](validation/2026-10-26-emulator.md) passed 24 runtime assertions
with generated MP3s and a simulated peer, including metadata timing, media controls, background playback,
queue transitions, and cleanup. It also exposed and fixed standalone Debug Mock startup configuration.
Physical phone, Bluetooth, live-peer, and real-scrobbler validation remain outstanding. The following is
the broader device acceptance checklist; the emulator report identifies the subset actually exercised:

1. Stream from a throttled peer; verify audio starts with Received below 100%, then lock the phone and
   switch apps during buffering and playback. Observe the same transfer and continuous audio.
2. Use notification, lock-screen, and Bluetooth play/pause; verify position is retained. Test headphone
   unplug, focus loss to another media app, and an incoming call.
3. Close/reopen the dialog, change tabs, leave/return to the main interface, and recreate the Activity.
   Verify the mini-player, metadata, and artwork remain synchronized without a new player/transfer.
4. Play tagged and untagged MP3s. Switch tracks while artwork decoding is in progress. Verify old titles
   and covers never reach the new session; inspect session metadata using `adb shell dumpsys media_session`.
5. Stall or disconnect the peer during playback and while paused. Verify existing buffering/retry limits,
   terminal errors, Stop responsiveness, wake-lock release, and cache deletion.
6. Stop from the dialog and notification; dismiss the notification where supported. Verify no remaining
   session/transfer. Swipe away a playing task, then a paused task; force-stop/relaunch and verify safe cleanup.
7. Repeat Play on the current file; try ordinary download/stream duplicate cases and normal downloads,
   uploads, search, and browsing. Profile repeated switches for retained views, bitmaps, and JNI callbacks.
8. Enable the appropriate permissions in Pano Scrobbler and test title/artist detection and state changes.
   Unknown duration and conservative fallback metadata may limit recognition for some MP3s.

Library references: [Media3 background playback](https://developer.android.com/media/media3/session/background-playback),
[controller connections](https://developer.android.com/media/media3/session/connect-to-media-app),
[ForwardingSimpleBasePlayer](https://github.com/androidx/media/blob/1.11.1/libraries/common/src/main/java/androidx/media3/common/ForwardingSimpleBasePlayer.java).
