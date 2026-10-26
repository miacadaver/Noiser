# Noiser

Seeker Android with an incremental prototype for playing MP3 files directly from Soulseek peers.

The Seeker source was imported from [jackBonadies/SeekerAndroid](https://github.com/jackBonadies/SeekerAndroid)
at commit `ff1dcc6de55a412e8d6eadaddf36deca54517413` (October 11, 2026).
The original project README is retained as [UPSTREAM_README.md](UPSTREAM_README.md), along with its license and notices.

Tap **Play** beside an MP3 in a peer’s folder or in the track list opened from a search result.
The Browse file menu also retains its **Play** option. The prototype receives the file through the existing
Soulseek client and feeds a temporary buffer to Media3/ExoPlayer while the transfer continues.
Playback controls appear in a dialog. **Close** minimizes it to a mini-player above the main navigation.
The same playback session continues in the background and exposes Android notification, lock-screen,
and headset play/pause controls. **Stop** ends playback and removes the temporary file after transfer shutdown.

Playback supports MP3 folder queues, Next/Previous, Play next/Add to queue, repeat modes, and autoplay.
One track is received at a time, with buffering and up to three reconnect attempts. Seeking is limited to
audio already received and parsed; it cannot jump to an unreceived part of a remote file.
At the end of the queue, the current track stays available for replay until Stop or another selection.
The queue and selected song are saved across app restarts and return paused. Tap Play to start the
selected song at its saved position. A stream may need to receive audio up to that point before it
can resume. Stop clears the saved queue and position.
See [streaming implementation and device checks](docs/streaming.md)
and [background playback architecture and validation](docs/background-playback.md), plus the upstream
[development instructions](DEVELOPMENT.md).

Every push to `main` and pull request targeting `main` runs [unit tests](https://github.com/miacadaver/Noiser/actions/workflows/unittests.yml)
and [builds an Android APK](https://github.com/miacadaver/Noiser/actions/workflows/android.yml).
Open a successful APK build and download **noiser-debug-apk** from its **Artifacts** section.
This is a standalone development APK with debug signing; the workflow needs no release signing secrets.
The background playback build reports version **3.11.2-stream.5** in Android's app information.
The playback dialog shows embedded title, artist, album, and artwork when available, with conservative
filename fallbacks. See [metadata behavior, limits, and verification](docs/streaming-metadata.md).
Playback briefly waits for initial tags before starting, helping scrobblers receive embedded metadata
instead of a temporary filename title. This wait is bounded to 1.5 seconds; missing tags do not block playback.

Automated tests and Android builds pass locally. Live playback, lock-screen/Bluetooth controls, and
external scrobbler detection still require Android device testing.
An [Android 15 emulator validation run](docs/validation/2026-10-26-emulator.md) passed 24 runtime checks
using generated MP3s and a simulated peer; physical-phone and real-scrobbler checks remain outstanding.
