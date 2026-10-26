# Embedded-only session albums — October 26, 2026

The loading track previously published its folder/filename-derived album through Android's media
session before ID3 extraction completed. The 1.5-second audible-playback wait did not prevent this
earlier metadata publication.

`TrackMetadata.Album` remains the in-app display value. `EmbeddedAlbum` records only a valid parsed
album tag, and `StreamingMetadata.BuildSessionMetadata` uses that value for Android's album field.
The field is unset while tags load and when no album tag exists. Queue copies, queue metadata updates,
and duration-only updates preserve the distinction. The initial playback wait is unchanged.

## Verification

- Release host suite, using the CI filter: **707 passed, 1 skipped, 0 failed**. Eleven new cases cover
  folder/filename guesses, missing/empty tags, tags matching folder names, partial updates, independent
  tracks, and queue copying/updating/ordering.
- Standalone Debug Mock Android build succeeded with no errors. Existing compiler/resource and
  assembly-reference warnings remain. The generated Media3 Java callback and manifest checks passed.
- **11 Android 15 emulator checks passed**, using the existing temporary validation Activity and mock
  peer transport. See the [timestamped results](2026-10-26-album-results.txt).

The current and upcoming tracks initially have a known fallback album, `Wrong folder album`. The first
track's bytes and tags are delayed for 2.5 seconds, beyond the playback wait. Android's current album
is empty before tags, and the upcoming queue item's album is empty. Once parsed, the real tag
`Noiser Validation Album` reaches MediaController and is retained in the queue.

The second track has embedded title/artist tags but no album tag. After switching to it, its in-app
album remains the folder display, while the native MediaController album stays unset. A native duration
update does not publish the folder album, and the previous queue entry retains its real tagged album.
No observed current-session update exposed the folder guess. Transfers and temporary buffers were
cleaned up at the end. The final captured log contained no fatal exception or app ANR.

This validates the publication path with controlled fixtures; it does not claim a real scrobble was
recorded by Pano Scrobbler. Temporary harness sources and logs are in `/tmp/noiser-album-validation/`.
