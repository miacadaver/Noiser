# MP3 streaming, seeking, and playback queues

The prototype includes [embedded MP3 metadata, filename fallbacks, and bounded artwork display](streaming-metadata.md)
and [service-owned background playback, Android media controls, and a mini-player](background-playback.md).

## Source and architecture

Upstream: `jackBonadies/SeekerAndroid`, revision `ff1dcc6de55a412e8d6eadaddf36deca54517413`.
The import preserves upstream source, resources, dependencies, license, and notices. Noiser's Git history
and original remote are retained.

`BrowseAdapter` shows a Play button beside MP3 files. `BrowseFragment.ShowFileContextMenu` also keeps
its Play action. `DownloadDialog` shows Play beside unlocked MP3s in the track list opened from search
results, including after fetching the folder contents. Extension checks ignore case. Browse batch
selection hides the Play buttons, and tapping a search track's text still selects it for download.
Recycled rows refresh both the button visibility and the bound file so Play targets the displayed track.
`StreamingPlaybackService` owns one Media3 ExoPlayer and MediaSession; `StreamingPlaybackController`
only displays and controls that session. Playback uses a completed
ordinary download when its URI is readable; otherwise `DownloadService.StreamAsync` requests a temporary
transfer through the existing authenticated Soulseek client.

The modified Soulseek.NET library delivers ordered TCP bytes to a supplied stream. `ProgressiveBuffer`
publishes chunks after flushing them to an app-private cache file, supports independent positional readers,
and reports EOF only after completion. Its writer's Position always reports the committed prefix length,
including after a reader moves elsewhere. `ProgressiveDataSource` waits at the current frontier and wakes
on data, cancellation, failure, or timeout. The bounded MP3 extractor preserves the ID3 allocation guard,
uses Media3's Xing/Info/VBRI seeking data where available, and requests Media3's incremental index when
advertised file length alone would imply a constant-bitrate guess. It exposes only seek points within
completed extracted samples and committed bytes.

The received-byte indicator uses the committed streaming prefix. It reports the advertised size for a
completed local file only after its URI has been confirmed readable and installed as the playback source;
failed remote initialization with no buffer reports zero received bytes.

Ordinary downloads retain their existing storage and completion path. Streaming tokens are recognized by
`TransferEventRouter`, so they kick the existing keepalive without ordinary download counters/notifications,
saving permanent copies, scanning MediaStore, or being requeued on app restart. Per-file reservations also
cover a fully received track while it is playing. Late transfer events retain token provenance.

## Current behavior and limits

- One active MP3/peer transfer at a time; remote files have a 256 MiB maximum advertised size.
  Folder queues select tracks sequentially. Seeking is limited to extracted audio in the received
  prefix; remote ranges and prefetching are not supported.
- ExoPlayer starts with approximately two seconds of decoded audio buffered; it may read further ahead.
  Starting does not depend on the complete file arriving. Leading ID3 tags can increase startup time.
- A waiting peer gets up to three minutes before an empty-buffer read times out. Once bytes have arrived,
  an empty-buffer read waits up to 45 seconds. Stop remains available during these waits.
- One retry is allowed for connection closure, connection setup failure, timeout, or a peer-reported failure.
  Retry resumes at the committed byte offset and may return to the peer's upload queue.
- A rejected, unavailable, changed-size, malformed, or stalled file produces a visible error.
- Existing active normal downloads are not downloaded again. Play reports that the ordinary download must
  finish. Attempting to download a currently streaming file asks the user to stop playback first.
- Closing the dialog shows the mini-player. Backgrounding or recreating the Activity leaves the same
  service-owned player and transfer running. Advancing the queue releases the old stream before the next
  remote request. At the end of the queue, or with autoplay disabled, playback holds the current track
  for replay. Stop and explicit application shutdown release it. A killed process is not automatically resumed.
- Buffer deletion waits for transfer shutdown. Abandoned `.part` files are cleaned on application startup.
  A temporary file may contain the complete track while playback is still running.

## Playback queue and controls

Play beside a Browse or search-result track builds a queue from the available MP3 rows and starts at
the selected song. Browse also offers folder playback. Existing row order is retained; folder-menu
playback orders filenames case-insensitively. Unsupported files and locked Browse entries are excluded.
Embedded tags arrive for the active track and update its queue metadata without reordering the queue.

The player exposes Next, Previous, and Queue. Browse file actions and search-track long presses offer
Play next and Add to queue. Queue rows can be selected or removed; clearing the queue stops playback.
The dialog shows the current track and the tracks after it; Repeat All also shows the wrapped entries.
Queue identities and the current song are checkpointed privately and restored offline, paused, after process death. Stop/Clear discards that checkpoint. No position or play intent is saved; see [queue recovery](queue-recovery.md).

Autoplay is enabled by default. Repeat cycles Off, All, One; selecting All or One enables autoplay,
and disabling autoplay returns repeat to Off. Repeat One replays the current buffer; Repeat All wraps
the queue. There is no prefetch, so advancing to another remote song may require queue/connection time.
An error remains visible until the user selects another track or stops; failures are not auto-skipped.

The same queue is exposed through MediaSession for supported external Next/Previous and track-selection
commands. Timeline construction takes one defensive queue snapshot, avoiding repeated full-list copies.
The initial metadata wait is independent of queue snapshots and is preserved; see
[metadata startup behavior](streaming-metadata.md#metadata-and-fallback).

## Seeking

The app-private file remains a sequential Soulseek download. Playback reads it positionally, so rewinding
does not restart or reposition the peer transfer. Media3 supplies the time-to-byte mapping, including Xing,
Info, and VBRI metadata when present. If that metadata cannot establish a reliable VBR mapping, the extractor
uses its incremental sample index instead of estimating byte positions from the advertised file size.

Only completed extractor samples advance the bounded per-track timestamp and byte frontier. A 64-frame tail
retains parsed sample start/end positions; it does not map time to bytes. The seekable timestamp ends at the
last parsed frame at least 500 ms behind the extractor frontier, leaving completed audio for decoder preroll.
Media3 remains the only timestamp-to-byte map. Its native points must land between the first parsed audio frame
and that safe frame boundary; if no native point qualifies, seeking stays disabled. This works with a large ID3
prefix and short received files without treating metadata bytes as audio or subtracting a fixed byte count. The
full player and MediaSession use the same validated seek range. The seek bar reports the
current position, total duration when known, and seekable audio. When the duration is unknown, it labels it as
unknown. Stopping or switching tracks resets the visible range. A cancelled loader wait observes Media3's
thread interruption without cancelling the transfer or changing the Soulseek writer position.

## Build and automated checks

Requirements: the .NET SDK/workload pinned in `global.json`, Android SDK platform 36/build tools 36, and JDK 21.

```sh
dotnet workload restore Seeker/Seeker.csproj
dotnet build Seeker/Seeker.csproj -p:AndroidKeyStore=false -p:EmbedAssembliesIntoApk=true
dotnet test UnitTestCommon/UnitTestCommon.csproj --filter 'FullyQualifiedName~ProgressiveBufferTests|FullyQualifiedName~StreamingDownloadTests'
```

Set `AndroidSdkDirectory` and `JavaSdkDirectory` MSBuild properties if the tools are not discovered.
The streaming tests exercise reading before transfer completion, waiting at the frontier, cancellation,
failure versus EOF, size validation, cleanup, resume offsets, rejection, and duplicate reservations.
They use generated bytes and a stub Soulseek client; they do not prove decoding or live peer compatibility.

Use the CI configuration to run the full host suite:

```sh
dotnet test UnitTestCommon/UnitTestCommon.csproj --configuration Release --filter 'TestCategory!=RealData'
```

Run the Media3 parser/seek-map regression probe against the restored Xamarin AARs with
`.github/scripts/media3-mp3-seek-probe/run.sh`. It builds synthetic CBR/VBR MP3 frame streams, including a
200,000-byte ID3 prefix, and exercises partial-prefix seek/reparse plus final duration. On the pinned Media3
1.11.1 artifacts it reports `IndexSeeker` for masked CBR, VBR, and ID3-prefixed VBR; the unmasked VBR control
uses `ConstantBitrateSeeker` and estimates 28.7 seconds instead of the fixture's 5.224467 seconds. This is a
native parser and seek-map probe only: it does not instantiate the Android wrapper, run an audio decoder, or
verify audible playback.

The host suite covers queue mutations, repeat/autoplay policy, request delivery, buffering, metadata,
seeking bounds, transfer cleanup, and saved-data compatibility. Encrypted Debug-only fixtures need
upstream's `SeekerEncryptKey`; the Release CI configuration excludes those fixtures.
Current verification results are recorded in [background playback validation](background-playback.md#verification-and-device-acceptance).

## Device acceptance checks

Run these against a real Android device and an MP3 that the test peer is authorized to share:

1. Install the current Noiser development APK. Sign in, open a search result, and tap Play beside an unlocked MP3.
   Repeat from the Browse tab, using both the row button and the file menu. Scroll through mixed MP3,
   non-MP3, and locked search results; confirm Play stays with the correct file. Select tracks for regular
   download and use Browse batch selection to confirm those interactions still work.
2. Throttle the peer so transfer completion takes substantially longer than the player's starting buffer.
   Confirm audible playback while the dialog shows Received below 100%.
3. Pause/resume. Slow the transfer below the playback bitrate and confirm buffering followed by recovery.
4. Rewind and seek forward within received audio while the transfer is incomplete. Confirm the reported safe
   range grows with received samples, seeks past it are rejected, and no second peer connection starts.
   Repeat while paused, buffering, and near the received boundary.
5. Test CBR and VBR MP3s with and without Xing/Info/VBRI headers. Check that seeks use extractor timestamps
   and unreliable maps restrict seeking safely.
6. Disconnect the peer after a prefix arrives. Confirm one offset-based retry, or a visible terminal error.
7. Stop during the peer queue, a buffer wait, and playback. Confirm prompt UI response, no continuing transfer,
   and deletion of `cache/mp3-streams/*.part` after shutdown.
8. Repeat Play, switch tracks, rotate, lock the screen, and background the app. Confirm ongoing playback,
   correct controls/metadata, and no duplicate transfer or crash. Force-stop and relaunch to check
   abandoned cache cleanup. Follow the complete background playback checklist in the linked document.
9. Try Play during an ordinary download, Download during streaming, and Play on an existing completed download.
   Confirm no second peer transfer for the same file while its current owner is active.
10. Verify search, regular downloads/resume, transfers, uploads, and folder browsing alongside streaming.

Live audio playback and these Android lifecycle checks require device validation. The mock download mode
generates arbitrary bytes and therefore is not a valid MP3 decoding fixture.

## GitHub Actions

Pushes to `main`, pull requests targeting `main`, and manual runs trigger APK builds and unit tests.
The Android workflow uses JDK 21, the pinned .NET SDK/workload, and the required Android SDK components.
It builds the same standalone Debug configuration validated locally and uploads a `Noiser-apk`
artifact, retained for 14 days. Download it from the successful run's Artifacts section and unzip it.

The unit-test workflow follows upstream's Release configuration and `TestCategory!=RealData` filter.
Encrypted debug-only fixtures are excluded by that configuration; all streaming tests still run.
Upstream Crowdin/F-Droid automation is restricted to the original upstream repository. Tagged release
builds retain their separate release-signing setup and are not required to obtain a development APK.
