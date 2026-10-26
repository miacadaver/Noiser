# Noiser branding

The app label is **Noiser**, and the standard GitHub Actions download contains **Noiser.apk**. Release APKs use `Noiser-<version>-<ABI>.apk` (or `-universal.apk`). Android's internal package remains `com.companyname.andriodapp1.rollback` to preserve the working installation identity.

## Bird and palette

The bird retains Seeker's original silhouette and eye, with two feather cutouts and a compact inset for Android adaptive masks. This is an editable vector adaptation, not an AI-generated replacement. The upstream source and notices remain intact.

- **Background:** `#102C32`, dark teal, in both light and dark launcher configurations.
- **Bird:** `#8DE5C2`, mint.
- **Themed icon:** white alpha silhouette; Android supplies the user's theme color.
- **Notification icon:** white alpha silhouette; Android supplies the notification color.

Source: [`noiser_bird.svg`](../Seeker/Assets/noiser_bird.svg), derived from [`seeker_logo_source_mono.svg`](../Seeker/Assets/seeker_logo_source_mono.svg). The README icon and banner are in [`docs/images`](images).

## Regenerating assets

From the repository root, in a Python virtual environment:

```sh
python -m pip install -r Misc/branding-requirements.txt
python Misc/generate-branding.py
```

CairoSVG also requires the Cairo system library. The banner uses DejaVu Sans; install that font to reproduce its typography.

The script exports legacy square/round icons and transparent adaptive/monochrome foregrounds for mdpi through xxxhdpi. Foregrounds retain Android's 108 dp canvas with the bird inside its central safe area. It also exports the notification icon, 512 px store icons, a 1024 × 500 store feature graphic, and the README banner. Adaptive XML uses the matching background color resources, and the manifest declares both square and round icon references.

Screenshots currently shown in the README are explicitly labeled as inherited Seeker UI or a historical Noiser emulator validation capture. They are not edited to imply a new runtime capture. Replacing them with real-device Noiser screenshots is tracked in the roadmap.
