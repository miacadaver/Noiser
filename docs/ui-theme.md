# Noiser Mint interface

Version **3.11.2-stream.10** replaces the default Seeker purple palette with the dark teal and mint identity of the Noiser bird icon. This is a native Android resource change; playback ownership, network transfers, credentials, and stored queues are not changed.

## Appearance

The default **Noiser Mint** day theme uses pale green-white surfaces, dark teal chrome, and a darker teal accent for legible controls. The night theme uses the icon's `#102C32` background and `#8DE5C2` mint accents. **AMOLED - Noiser Mint** keeps true-black main surfaces with teal raised surfaces and mint controls.

The shared palette covers Login, Search, Browse, Transfers, messages/chatrooms, user information, Settings, dialogs, bottom sheets, selection states, navigation, and Noiser's playback dialog, mini-player, queue, seek controls, and artwork placeholder. Successful/active MP3 and transfer indicators use the palette; warning and error states retain distinct amber/red meanings.

The existing theme IDs remain unchanged for saved-settings compatibility. The old Purple option is now called **Noiser Mint**; existing default-purple installations adopt it automatically. Existing Blue, Red, and Grey choices remain available. If one of those is selected, choose **Noiser Mint** under **Settings → Day theme** and **Settings → Night theme** to use the new palette. System/light/dark selection continues to work as before.

## Source and checks

- [`noiser_palette.xml`](../Seeker/Resources/values/noiser_palette.xml): explicit light, dark, AMOLED, chrome, and accent colors.
- [`themes.xml`](../Seeker/Resources/values/themes.xml): application and Material color roles, including tonal containers so components do not fall back to lavender surfaces.
- [`check_noiser_theme.py`](../.github/scripts/check_noiser_theme.py): XML/reference validation and 39 contrast checks across the three Noiser themes, each requiring at least 4.5:1 for the tested text roles.

Run the resource checks without an Android SDK:

```sh
python3 .github/scripts/check_noiser_theme.py
```

CI runs these checks before building the Android APK. Contrast checks cover the configured roles; they do not replace checking rendered views, disabled states, accessibility, or manufacturer-specific system UI on a device. Android controls the media-notification/lock-screen layout and may derive its colors from system settings or artwork, independently of the app's theme.

## Device acceptance

After installing the signed APK, check Noiser Mint in light, dark, and AMOLED modes. Review toolbar/system-bar icons, navigation selection, search chips, folder lists, download selection, transfer progress, chat bubbles and links, Settings switches, bottom sheets, the full player, and the mini-player. Confirm the content remains readable and playback continues through theme changes as it did before.

Screenshots currently in the README predate this theme and are labeled historical. They have not been recolored or presented as captures of the refreshed interface.
