"""Check the actual Android theme roles for missing resources and text contrast."""

from pathlib import Path
import re
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]
RESOURCES = ROOT / "Seeker/Resources"


def load(mode):
    colors = {}
    styles = {}
    for directory in ("values", "values-night") if mode == "night" else ("values",):
        for path in sorted((RESOURCES / directory).glob("*.xml")):
            for node in ET.parse(path).getroot():
                if node.tag == "color":
                    colors[node.get("name")] = node.text.strip()
                elif node.tag == "style":
                    name = node.get("name")
                    parent = node.get("parent", name.rsplit(".", 1)[0] if "." in name else "")
                    styles[name] = (parent, {item.get("name"): (item.text or "").strip() for item in node.findall("item")})
    return colors, styles


def theme_items(name, styles):
    if name not in styles:
        return {}
    parent, own = styles[name]
    return {**theme_items(parent.removeprefix("@style/"), styles), **own}


def color(value, colors, items, seen=()):
    if value in seen:
        raise AssertionError(f"Cyclic color reference: {seen + (value,)}")
    if value.startswith("@color/"):
        return color(colors[value[7:]], colors, items, seen + (value,))
    if value.startswith("?attr/"):
        return color(items[value[6:]], colors, items, seen + (value,))
    assert re.fullmatch(r"#[0-9a-fA-F]{6}", value), f"Expected an opaque color, got {value}"
    return tuple(int(value[index:index + 2], 16) / 255 for index in (1, 3, 5))


def luminance(rgb):
    linear = [channel / 12.92 if channel <= .04045 else ((channel + .055) / 1.055) ** 2.4 for channel in rgb]
    return sum(channel * weight for channel, weight in zip(linear, (.2126, .7152, .0722)))


def contrast(first, second):
    low, high = sorted((luminance(first), luminance(second)))
    return (high + .05) / (low + .05)


def main():
    # Parsing every resource catches malformed XML before the Android build.
    for path in RESOURCES.rglob("*.xml"):
        ET.parse(path)
    palette = {node.get("name") for node in ET.parse(RESOURCES / "values/noiser_palette.xml").getroot()}
    for path in RESOURCES.rglob("*.xml"):
        for reference in re.findall(r"@color/(noiser\w+)", path.read_text(encoding="utf-8-sig")):
            assert reference in palette or (RESOURCES / f"color/{reference}.xml").exists(), (path, reference)

    checks = 0
    for name, mode in (("DefaultLight", "day"), ("DefaultDark", "night"), ("Amoled", "night")):
        colors, styles = load(mode)
        items = theme_items(name, styles)
        for foreground, background in (
            ("normalTextColor", "colorSurface"),
            ("cellTextColorSubdued", "dialog_background"),
            ("cellTextColorMoreSubdued", "surface_variant1"),
            ("colorOnPrimary", "colorPrimary"),
            ("colorOnPrimaryContainer", "colorPrimaryContainer"),
            ("colorOnSecondary", "colorSecondary"),
            ("colorOnSecondaryContainer", "colorSecondaryContainer"),
            ("colorOnSurfaceVariant", "colorSurfaceVariant"),
            ("mainTextColor", "colorSurface"),
            ("mainTextColor", "dialog_background"),
            ("noiserNavigationActive", "whitePurpleForNavBar"),
            ("noiserNavigationInactive", "mainPurple"),
        ):
            ratio = contrast(color(items[foreground], colors, items), color(items[background], colors, items))
            assert ratio >= 4.5, f"{name}: {foreground} on {background} is {ratio:.2f}:1 (needs 4.5:1)"
            checks += 1
        for foreground, background in (
            ("noiserOnChrome", "noiserChrome"),
            ("noiserMint", "noiserNavIndicator"),
            ("noiserNavInactive", "noiserChrome"),
        ):
            ratio = contrast(color("@color/" + foreground, colors, items), color("@color/" + background, colors, items))
            assert ratio >= 4.5, f"{name}: {foreground} on {background} is {ratio:.2f}:1"
            checks += 1
        print(f"{name}: text, controls, containers, and navigation passed contrast checks.")
    print(f"All XML and Noiser color references valid; {checks} text contrast checks passed (at least 4.5:1).")


if __name__ == "__main__":
    main()
