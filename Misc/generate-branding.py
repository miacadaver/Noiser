"""Export Noiser's vector bird to Android launcher assets and repository artwork.

Install Misc/branding-requirements.txt, then run this script from any directory.
The upstream source artwork is retained in Seeker/Assets/seeker_logo_source*.svg.
"""

from io import BytesIO
from pathlib import Path

import cairosvg
from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "Seeker/Assets/noiser_bird.svg"
BACKGROUND = "#102C32"
MINT = "#8DE5C2"


def bird(size, monochrome=False):
    svg = SOURCE.read_text()
    if monochrome:
        svg = svg.replace(MINT, "#FFFFFF")
    png = cairosvg.svg2png(bytestring=svg.encode(), output_width=size, output_height=size)
    return Image.open(BytesIO(png)).convert("RGBA")


def icon(size, round_icon=False):
    # Oversample to keep the smallest launcher assets smooth.
    canvas = Image.new("RGBA", (1024, 1024))
    draw = ImageDraw.Draw(canvas)
    if round_icon:
        draw.ellipse((0, 0, 1023, 1023), fill=BACKGROUND)
    else:
        draw.rounded_rectangle((0, 0, 1023, 1023), radius=224, fill=BACKGROUND)
    # Legacy icons have no system inset; adaptive foregrounds retain the safe zone.
    mark = bird(1380)
    canvas.alpha_composite(mark, (-178, -178))
    return canvas.resize((size, size), Image.Resampling.LANCZOS)


def main():
    resources = ROOT / "Seeker/Resources"
    for density, legacy_size, adaptive_size in (
        ("mdpi", 48, 108), ("hdpi", 72, 162), ("xhdpi", 96, 216),
        ("xxhdpi", 144, 324), ("xxxhdpi", 192, 432),
    ):
        target = resources / f"mipmap-{density}"
        icon(legacy_size).save(target / "ic_launcher.png")
        icon(legacy_size, round_icon=True).save(target / "ic_launcher_round.png")
        bird(adaptive_size).save(target / "ic_launcher_foreground.png")
        bird(adaptive_size, monochrome=True).save(target / "ic_launcher_mono_foreground.png")

    images = ROOT / "docs/images"
    images.mkdir(parents=True, exist_ok=True)
    icon(512).save(images / "noiser-icon.png")
    store = ROOT / "fastlane/metadata/android/en-US/images"
    icon(512).save(store / "icon.png")
    icon(512, round_icon=True).save(store / "icon_circle_crop.png")

    # Notification icons use alpha only; Android supplies their display color.
    mono = bird(256, monochrome=True)
    bounds = mono.getbbox()
    mark = mono.crop(bounds)
    mark.thumbnail((70, 70), Image.Resampling.LANCZOS)
    notification = Image.new("RGBA", (96, 96))
    notification.alpha_composite(mark, ((96 - mark.width) // 2, (96 - mark.height) // 2))
    notification.save(resources / "drawable/ic_stat_soulseekicontransparent.png")

    # A vector banner keeps typography and artwork editable.
    body = SOURCE.read_text().split("</defs>", 1)[1].rsplit("</svg>", 1)[0]
    defs = SOURCE.read_text().split("<defs>", 1)[1].split("</defs>", 1)[0]
    banner = f'''<svg xmlns="http://www.w3.org/2000/svg" width="1280" height="480" viewBox="0 0 1280 480">
      <defs>{defs}</defs>
      <rect width="1280" height="480" rx="32" fill="{BACKGROUND}"/>
      <circle cx="1060" cy="220" r="280" fill="none" stroke="#20464A" stroke-width="2"/>
      <circle cx="1060" cy="220" r="220" fill="none" stroke="#20464A" stroke-width="2"/>
      <g transform="translate(780 -30) scale(1.1)">{body}</g>
      <g font-family="DejaVu Sans, sans-serif">
        <text x="72" y="100" fill="{MINT}" font-size="18" letter-spacing="4">SOULSEEK / ANDROID</text>
        <text x="68" y="230" fill="#F2F8F5" font-size="112" font-weight="bold">Noiser</text>
        <text x="74" y="298" fill="#B9CDCA" font-size="28">Find a track. Press play.</text>
        <text x="74" y="396" fill="{MINT}" font-size="17">MP3 streaming · Background playback · Built on Seeker</text>
      </g>
    </svg>'''
    (images / "noiser-banner.svg").write_text(banner)
    cairosvg.svg2png(bytestring=banner.encode(), write_to=str(images / "noiser-banner.png"))
    cairosvg.svg2png(bytestring=banner.encode(), output_width=1024, output_height=375,
                   write_to=str(store / "featureGraphic.png"))
    # Store feature graphics must be exactly 1024 by 500, without stretching artwork.
    feature = Image.new("RGB", (1024, 500), BACKGROUND)
    feature.paste(Image.open(store / "featureGraphic.png").convert("RGB"), (0, 62))
    feature.save(store / "featureGraphic.png")
    print("Exported all launcher densities, themed and notification icons, store artwork, and README banner.")


if __name__ == "__main__":
    main()
