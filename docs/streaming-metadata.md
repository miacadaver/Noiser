# MP3 metadata for foreground streaming

## Architecture inspected

The existing C# Android implementation uses `Xamarin.AndroidX.Media3.ExoPlayer` 1.11.1.
`DownloadService.StreamAsync` receives ordered Soulseek TCP bytes through the existing authenticated
client into `ProgressiveBuffer.Writer`. `ProgressiveDataSource` supplies independent positional reads
from that growing cache file. Reads wait at the frontier; they do not negotiate network byte ranges.
The transfer's existing resume offset is used only by its existing reconnect logic.

`Mp3Extractor` already decodes ID3v2.3/v2.4 through Media3's `Id3Decoder`, including ISO-8859-1,
UTF-16, UTF-16BE, UTF-8, extended headers, and supported unsynchronization. Its static metadata is
published through `Player.Listener.onMediaMetadataChanged`. The project had no MediaSession or
streaming track metadata model. No queue, background service, seeking control, recognition API,
or scrobbling service is added by this change.

The Soulseek transfer service, buffer, data source, startup buffering thresholds, retry behavior, and
temporary-file lifecycle are unchanged. Metadata inspection uses the same Media3 extractor input,
on the loader thread, so there is no second transfer, separate partial request, or full-file scan.
MP3 audio follows its leading tags; a large leading tag must arrive or be skipped before audio starts.

## Metadata and fallback

`Common.Playback.TrackMetadata` holds original path/filename, title, artist, album, album artist,
track number, release year, optional duration in milliseconds, and bounded encoded artwork.
It is independent of Android UI/player classes and can be consumed by a later MediaSession integration.
Snapshots merge valid embedded fields over fallback fields. Missing/invalid values do not erase good data.

Media3 maps TIT2, TPE1, TALB, TPE2, TRCK, TYER/TDRC/TDRL, and APIC to its `MediaMetadata` fields.
Release year takes precedence over recording year; the latter covers legacy TYER tags. Front cover
selection uses Media3's existing artwork preference. Duration is accepted only when the player reports
a positive value. The existing non-seekable streamed extractor often reports no duration, even for a
Xing-tagged MP3; we do not estimate it from file size/bitrate. Completed ordinary-download playback
retains the native extractor's duration support and uses the same metadata allocation guard.

`TrackMetadataFallback` handles Windows/Unix peer paths and preserves Unicode. It removes the MP3
extension and common numeric prefixes. An unambiguous `Artist - Title` supplies artist/title.
An artist/album directory pair requires a recognized music root (`Music`, `Audio`, `MP3`, `MP3s`,
`Albums`) or agreement with the filename artist. Generic compilation/download folders and contradictory
artist names are rejected. Arbitrary peer directory names are deliberately left unknown.

Examples:

| Peer path | Fallback |
| --- | --- |
| `Music/Radiohead/In Rainbows/03 - Nude.mp3` | Radiohead / In Rainbows / Nude / track 3 |
| `Music/Radiohead - No Surprises.mp3` | Radiohead / No Surprises; album unknown |
| `Downloads/Someone/Loose Files/song.mp3` | song; artist/album unknown |

## Bounds, errors, and asynchronous ownership

Media3's ID3 peeker allocates the declared tag payload before frame decoding. `BoundedMp3Extractor`
therefore validates the 10-byte header's version, flags, and four synchsafe size bytes first, and bounds
the cumulative leading-tag prefix to **4 MiB**. This is a header/allocation guard, not a second ID3 frame
parser. Normal bounded tags are still passed to Media3 unchanged, preserving native gapless metadata.
The guard also covers Media3's leading-junk search. Oversized tag prefixes are discarded incrementally
using the input's skip buffer; their metadata is omitted. Impossible lengths discard the offending
header and allow audio resynchronization. Malformed extended headers that throw a native runtime
exception discard only the validated prefix before retrying audio extraction. Truncated audio/transfer
failures can still produce the existing playback error, rather than crashing the app.

Only artwork at most **2 MiB encoded** is copied into the common model. Bitmap bounds are read before
decoding; invalid dimensions, dimensions above 32768, or images over 64 million source pixels are
rejected. Accepted images are sampled to at most approximately **512 × 512** pixels. Text is capped at
4096 UTF-16 code units without splitting a surrogate pair. Invalid/missing pictures show a vector
placeholder; missing metadata never blocks playback.

Metadata marshaling and artwork decoding run on a worker. UI updates require both the current
controller instance and the newest metadata revision to match. Switching tracks creates fresh fallback
data and placeholder artwork; stopping invalidates queued results. Old/replaced bitmaps are disposed.
The existing dialog layout is extended only with artist, album, and a small cover image; its title updates
to the real title as metadata arrives.

## Verification

Automated host tests cover fallback examples, Unicode, ambiguous/contradictory paths, embedded-field
priority, partial/invalid updates, original filename retention, independent track snapshots, text limits,
artwork byte limits, ID3v2.3/v2.4 header sizes, large declared sizes without payload allocation, partial
headers, unsupported versions, reserved flags, and non-synchsafe lengths. Existing streaming tests
cover reads before transfer completion, frontier waits, cancellation, failures, retries, and duplicate
transfer protection. Host .NET tests cannot execute Media3's Android ID3 decoder or render artwork.

```sh
dotnet test UnitTestCommon/UnitTestCommon.csproj --configuration Release --filter 'TestCategory!=RealData'
```

Android compilation and the GitHub APK workflow verify binding/resource integration. A physical Android
device has not been used for this change. The following remain device acceptance checks:

1. Install `3.11.2-stream.3`, stream a tagged MP3 from a throttled test peer, and confirm audible playback
   while Received is below 100%, with the correct title/artist/album/cover in the existing dialog.
2. Repeat with ID3v2.3 UTF-16, v2.4 UTF-8, legacy Latin-1, track/total values, album artist, and year tags.
   Confirm those values in the metadata snapshot when debugging.
3. Try untagged MP3s and the fallback paths above. Missing metadata must allow playback and show the
   filename-derived title and default artwork.
4. Try malformed/short ID3 and extended headers, oversized tags, invalid image bytes, and very large
   image dimensions. Confirm fallback or the existing playback error, no crash, and bounded memory.
5. Switch songs/stop while cover decoding is in progress. Confirm no old title or cover reaches the new
   dialog. Profile repeated switches for bitmap retention and main-thread decoding.
6. Confirm pause/stop, slow-peer buffering, reconnect/resume, cache deletion, normal downloads, and
   streaming duplicate protection still behave as documented. Observe one peer transfer per stream.

Local validation: the Release suite with the CI filter passed **649 tests**, with **1 skipped**, including
35 new metadata tests and all 11 existing streaming tests. The final Android `Compile` target passed.
These checks do not claim successful native decoding, rendered artwork, or live audio on a device.

## Files

- Added `Common/Playback/TrackMetadata.cs`, `TrackMetadataFallback.cs`, and `Id3TagHeader.cs`.
- Added `Seeker/Playback/BoundedMp3Extractor.cs` and `StreamingMetadata.cs`; updated
  `StreamingPlaybackController.cs` for metadata callbacks, worker decoding, and dialog updates.
- Updated `Seeker/Resources/layout/stream_playback.xml`; added
  `Seeker/Resources/drawable/stream_artwork_placeholder.xml`.
- Added `UnitTestCommon/TrackMetadataTests.cs` and `Id3TagHeaderTests.cs`.
- Updated the Android manifest version, README, and streaming documentation; added this document.

No package references or dependencies were added.

## Library references

- [Media3 1.11.1 MP3 extractor](https://github.com/androidx/media/blob/1.11.1/libraries/extractor/src/main/java/androidx/media3/extractor/mp3/Mp3Extractor.java)
- [Media3 ID3 decoder and encodings](https://github.com/androidx/media/blob/1.11.1/libraries/extractor/src/main/java/androidx/media3/extractor/metadata/id3/Id3Decoder.java)
- [Native ID3-to-MediaMetadata mapping](https://github.com/androidx/media/blob/1.11.1/libraries/extractor/src/main/java/androidx/media3/extractor/metadata/id3/TextInformationFrame.java)
