# Startup compatibility — October 26, 2026

The reported Samsung Galaxy A07 / Android 16 startup crash persisted after the battery optimization
was rolled back in `3.11.2-stream.6`. The same downloaded APK started on Android 15 and 16 x86-64
emulators. There is still no confirmed exception or native crash signature from the phone.

`3.11.2-stream.7` (version code 150) changes the following:

- Explicitly disables LLVM JNI marshal-method registration. This selects the supported managed
  registration path. A [reported .NET 10 device-specific crash](https://github.com/dotnet/android/issues/10910)
  describes this workaround on some Samsung devices, but concerns different hardware/Android versions
  and does **not** establish the cause of this phone's startup failure. See Microsoft's
  [build-property reference](https://learn.microsoft.com/en-us/dotnet/android/building-apps/build-properties#androidenablemarshalmethods).
- Checks for absent Wi-Fi/mobile network information in the Debug connectivity receiver. Android's
  deprecated [`GetNetworkInfo` API](https://developer.android.com/reference/android/net/ConnectivityManager#getNetworkInfo(int))
  can return null. Debug-only connectivity toasts must not dereference that result unchecked.
- Keeps StrictMode detection/logging, but removes its fatal-network penalty and flashing debug
  overlay from Debug builds distributed as APKs.
- Routes synchronous managed application/home-screen/player-attachment initialization failures to a
  plain platform Activity with a readable stage, exception and Copy error details button. It uses no
  app theme, fragments, preferences, playback objects or logger. Error details remain in memory and
  reach the clipboard only when the user taps Copy; there is no automatic upload.
- Suppresses normal lifecycle persistence and network callbacks after initialization has failed, so
  partial defaults do not overwrite saved preferences or transfer state. The error screen resets no
  settings, queue entries or bookmarks. A new process retries initialization normally.

The startup boundary is deliberately limited: it cannot intercept a native process abort before .NET
initializes, an OS process kill, or every later asynchronous failure. It is not a general exception
swallower, nor confirmation that the Samsung crash is fixed.

## Validation

- Release host suite (`TestCategory!=RealData`): **790 passed, 1 skipped, 0 failed**.
- Standalone Debug APK built with embedded assemblies, SDK 36 / JDK 21: **zero errors**.
- Generated Media3 callback contracts and mediaPlayback service manifest checks passed. The new
  error Activity is not exported. Generated ARM64 and x86-64 application configuration confirms
  `marshal_methods_enabled=false`.

The repeatable device check requires a dedicated emulator and intentionally resets this app's
emulator data. It checks fresh/saved-queue starts, forces a real preference type mismatch, verifies
the readable error/Copy action and original data retention, then restores the test preferences:

```sh
python3 .github/scripts/check_android_startup.py \
  --apk PATH_TO_APK --adb PATH_TO_ADB --device emulator-5554 \
  --output /tmp/noiser-startup-check --reset-emulator-app-data
```
