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

This increment supports one MP3, Play/Pause/Stop, buffering, and one reconnect attempt.
Seeking and album queues are future increments. See [streaming implementation and device checks](docs/streaming.md)
and [background playback architecture and validation](docs/background-playback.md), plus the upstream
[development instructions](DEVELOPMENT.md).

Every push to `main` and pull request targeting `main` runs [unit tests](https://github.com/miacadaver/Noiser/actions/workflows/unittests.yml)
and [builds an Android APK](https://github.com/miacadaver/Noiser/actions/workflows/android.yml).
Open a successful APK build and download **noiser-debug-apk** from its **Artifacts** section.
This is a standalone development APK with debug signing; the workflow needs no release signing secrets.
The background playback build reports version **3.11.2-stream.5** in Android's app information.
The playback dialog shows embedded title, artist, album, and artwork when available, with conservative
filename fallbacks. See [metadata behavior, limits, and verification](docs/streaming-metadata.md).

Automated tests and Android builds pass locally. Live playback, lock-screen/Bluetooth controls, and
external scrobbler detection still require Android device testing.
